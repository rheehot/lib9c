using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using Bencodex;
using Bencodex.Types;
using Libplanet;
using Libplanet.Action;
using Serilog;
using Nekoyume.Model.State;
using Libplanet.Assets;
#if UNITY_EDITOR || UNITY_STANDALONE
using UniRx;
#else
using System.Reactive.Subjects;
using System.Reactive.Linq;
#endif

namespace Nekoyume.Action
{
    [Serializable]
    public abstract class ActionBase : IAction
    {
        public static readonly IValue MarkChanged = default(Null);

        // FIXME GoldCurrencyState 에 정의된 것과 다른데 괜찮을지 점검해봐야 합니다.
        protected static readonly Currency GoldCurrencyMock = new Currency();

        public abstract IValue PlainValue { get; }
        public abstract void LoadPlainValue(IValue plainValue);
        public abstract IAccountStateDelta Execute(IActionContext context);

        private struct AccountStateDelta : IAccountStateDelta
        {
            private IImmutableDictionary<Address, IValue> _states;
            private IImmutableDictionary<(Address, Currency), (BigInteger, BigInteger)> _balances;

            public IImmutableSet<Address> UpdatedAddresses => _states.Keys.ToImmutableHashSet();

            public IImmutableSet<Address> StateUpdatedAddresses => _states.Keys.ToImmutableHashSet();

            public IImmutableDictionary<Address, IImmutableSet<Currency>> UpdatedFungibleAssets =>
                _balances.GroupBy(kv => kv.Key.Item1).ToImmutableDictionary(
                    g => g.Key,
                    g => (IImmutableSet<Currency>)g.Select(kv => kv.Key.Item2).ToImmutableHashSet()
                );

            public AccountStateDelta(
                IImmutableDictionary<Address, IValue> states,
                IImmutableDictionary<(Address, Currency), (BigInteger, BigInteger)> balances
            )
            {
                _states = states;
                _balances = balances;
            }

            public AccountStateDelta(Dictionary states, List balances)
            {
                _states = states.ToImmutableDictionary(
                    kv => new Address(kv.Key.EncodeAsByteArray()),
                    kv => kv.Value
                );

                (BigInteger, BigInteger) Decode(List value) =>
                    (
                        value.ElementAt(0).ToBigInteger(),
                        value.ElementAt(1).ToBigInteger()
                    );

                _balances = balances.Cast<Dictionary>().ToImmutableDictionary(
                    record => (record["address"].ToAddress(), CurrencyExtensions.Deserialize((Dictionary)record["currency"])),
                    record => Decode((List)record["amount"])
                );
            }

            public AccountStateDelta(IValue serialized)
                : this(
                    (Dictionary)((Dictionary)serialized)["states"],
                    (List)((Dictionary)serialized)["balances"]
                )
            {
            }

            public AccountStateDelta(byte[] bytes)
                : this((Dictionary)new Codec().Decode(bytes))
            {
            }

            public IValue GetState(Address address) =>
                _states.GetValueOrDefault(address, null);

            public IAccountStateDelta SetState(Address address, IValue state) =>
                new AccountStateDelta(_states.SetItem(address, state), _balances);

            public FungibleAssetValue GetBalance(Address address, Currency currency)
            {
                if (!_balances.TryGetValue((address, currency), out (BigInteger, BigInteger) amount))
                {
                    throw new BalanceDoesNotExistsException(address, currency);
                }

                return new FungibleAssetValue(currency, amount.Item1, amount.Item2);
            }

            public IAccountStateDelta MintAsset(Address recipient, FungibleAssetValue value)
            {
                // FIXME: 트랜잭션 서명자를 알아내 currency.AllowsToMint() 확인해서 CurrencyPermissionException
                // 던지는 처리를 해야하는데 여기서 트랜잭션 서명자를 무슨 수로 가져올지 잘 모르겠음.

                if (value <= new FungibleAssetValue(value.Currency))
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                var nextAmount = GetBalance(recipient, value.Currency) + value;

                return new AccountStateDelta(
                    _states,
                    _balances.SetItem(
                        (recipient, value.Currency),
                        (nextAmount.MajorUnit, nextAmount.MinorUnit)
                    )
                );
            }

            public IAccountStateDelta TransferAsset(
                Address sender,
                Address recipient,
                FungibleAssetValue value,
                bool allowNegativeBalance = false)
            {
                if (value.Sign < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                FungibleAssetValue senderBalance = GetBalance(sender, value.Currency);
                if (senderBalance < value)
                {
                    throw new InsufficientBalanceException(
                        sender,
                        senderBalance,
                        $"There is no sufficient balance for {sender}: {senderBalance} < {value}"
                    );
                }

                Currency currency = value.Currency;
                FungibleAssetValue senderRemains = senderBalance - value;
                FungibleAssetValue recipientRemains = GetBalance(recipient, currency) + value;
                var balances = _balances
                    .SetItem((sender, currency), (senderRemains.MajorUnit, senderRemains.MinorUnit))
                    .SetItem((recipient, currency), (recipientRemains.MajorUnit, recipientRemains.MinorUnit));
                return new AccountStateDelta(_states, balances);
            }

            public IAccountStateDelta BurnAsset(Address owner, FungibleAssetValue value)
            {
                // FIXME: 트랜잭션 서명자를 알아내 currency.AllowsToMint() 확인해서 CurrencyPermissionException
                // 던지는 처리를 해야하는데 여기서 트랜잭션 서명자를 무슨 수로 가져올지 잘 모르겠음.

                if (value <= new FungibleAssetValue(value.Currency))
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                FungibleAssetValue balance = GetBalance(owner, value.Currency);
                if (balance < value)
                {
                    throw new InsufficientBalanceException(
                        owner,
                        value,
                        $"There is no sufficient balance for {owner}: {balance} < {value}"
                    );
                }

                FungibleAssetValue nextValue = balance - value;
                return new AccountStateDelta(
                    _states,
                    _balances.SetItem(
                        (owner, value.Currency),
                        (nextValue.MajorUnit, nextValue.MinorUnit)
                    )
                );
            }
        }

        [Serializable]
        public struct ActionEvaluation<T> : ISerializable
            where T : ActionBase
        {
            public T Action { get; set; }

            public Address Signer { get; set; }

            public long BlockIndex { get; set; }

            public IAccountStateDelta OutputStates { get; set; }

            public Exception Exception { get; set; }

            public IAccountStateDelta PreviousStates { get; set; }

            public ActionEvaluation(SerializationInfo info, StreamingContext ctx)
            {
                Action = FromBytes((byte[]) info.GetValue("action", typeof(byte[])));
                Signer = new Address((byte[]) info.GetValue("signer", typeof(byte[])));
                BlockIndex = info.GetInt64("blockIndex");
                OutputStates = new AccountStateDelta((byte[]) info.GetValue("outputStates", typeof(byte[])));
                Exception = (Exception) info.GetValue("exc", typeof(Exception));
                PreviousStates = new AccountStateDelta((byte[]) info.GetValue("previousStates", typeof(byte[])));
            }

            public void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                info.AddValue("action", ToBytes(Action));
                info.AddValue("signer", Signer.ToByteArray());
                info.AddValue("blockIndex", BlockIndex);
                info.AddValue("outputStates", ToBytes(OutputStates, OutputStates.UpdatedAddresses));
                info.AddValue("exc", Exception);
                info.AddValue("previousStates", ToBytes(PreviousStates, OutputStates.UpdatedAddresses));
            }

            private static byte[] ToBytes(T action)
            {
                var formatter = new BinaryFormatter();
                using (var stream = new MemoryStream())
                {
                    formatter.Serialize(stream, action);
                    return stream.ToArray();
                }
            }

            private static byte[] ToBytes(IAccountStateDelta delta, IImmutableSet<Address> updatedAddresses)
            {
                var state = new Dictionary(
                    updatedAddresses.Select(addr => new KeyValuePair<IKey, IValue>(
                        (Binary) addr.ToByteArray(),
                        delta.GetState(addr) ?? new Bencodex.Types.Null()
                    ))
                );
                var balance = new Bencodex.Types.List(
                    delta.UpdatedFungibleAssets.SelectMany(ua =>
                        ua.Value.Select(c =>
                            {
                                FungibleAssetValue b = delta.GetBalance(ua.Key, c);
                                return new Bencodex.Types.Dictionary(new[]
                                {
                                    new KeyValuePair<IKey, IValue>((Text) "address", (Binary) ua.Key.ToByteArray()),
                                    new KeyValuePair<IKey, IValue>((Text) "currency", c.Serialize()),
                                    new KeyValuePair<IKey, IValue>((Text) "amount", new Bencodex.Types.List(new IValue[]{ (Integer)b.MajorUnit, (Integer)b.MinorUnit})),
                                });
                            }
                        )
                    ).Cast<IValue>()
                );

                var bdict = new Dictionary(new []
                {
                    new KeyValuePair<IKey, IValue>((Text) "states", state),
                    new KeyValuePair<IKey, IValue>((Text) "balances", balance),
                });

                return new Codec().Encode(bdict);
            }

            private static T FromBytes(byte[] bytes)
            {
                var formatter = new BinaryFormatter();
                using (var stream = new MemoryStream(bytes))
                {
                    return (T)formatter.Deserialize(stream);
                }
            }
        }

        public static readonly Subject<ActionEvaluation<ActionBase>> RenderSubject =
            new Subject<ActionEvaluation<ActionBase>>();

        public static readonly Subject<ActionEvaluation<ActionBase>> UnrenderSubject =
            new Subject<ActionEvaluation<ActionBase>>();

        public void Render(IActionContext context, IAccountStateDelta nextStates)
        {
            RenderSubject.OnNext(new ActionEvaluation<ActionBase>()
            {
                Action = this,
                Signer = context.Signer,
                BlockIndex = context.BlockIndex,
                OutputStates = nextStates,
                PreviousStates = context.PreviousStates,
            });
        }

        public void Unrender(IActionContext context, IAccountStateDelta nextStates)
        {
            UnrenderSubject.OnNext(new ActionEvaluation<ActionBase>()
            {
                Action = this,
                Signer = context.Signer,
                BlockIndex = context.BlockIndex,
                OutputStates = nextStates,
                PreviousStates = context.PreviousStates,
            });
        }

        protected IAccountStateDelta LogError(IActionContext context, string message, params object[] values)
        {
            string actionType = GetType().Name;
            object[] prependedValues = new object[values.Length + 2];
            prependedValues[0] = context.BlockIndex;
            prependedValues[1] = context.Signer;
            values.CopyTo(prependedValues, 2);
            string msg = $"#{{BlockIndex}} {actionType} (by {{Signer}}): {message}";
            Log.Error(msg, prependedValues);
            return context.PreviousStates;
        }

        public void RenderError(IActionContext context, Exception exception)
        {
            RenderSubject.OnNext(
                new ActionEvaluation<ActionBase>()
                {
                    Action = this,
                    Signer = context.Signer,
                    BlockIndex = context.BlockIndex,
                    OutputStates = context.PreviousStates,
                    Exception = exception,
                    PreviousStates = context.PreviousStates,
                }
            );
        }

        public void UnrenderError(IActionContext context, Exception exception)
        {
            UnrenderSubject.OnNext(
                new ActionEvaluation<ActionBase>()
                {
                    Action = this,
                    Signer = context.Signer,
                    BlockIndex = context.BlockIndex,
                    OutputStates = context.PreviousStates,
                    Exception = exception,
                    PreviousStates = context.PreviousStates,
                }
            );
        }

        protected bool TryGetAdminState(IActionContext ctx, out AdminState state)
        {
            state = default;
            
            IValue rawState = ctx.PreviousStates.GetState(AdminState.Address);
            if (rawState is Bencodex.Types.Dictionary asDict)
            {
                state = new AdminState(asDict);
                return true;
            }

            return false;
        }

        protected void CheckPermission(IActionContext ctx)
        {
            if (TryGetAdminState(ctx, out AdminState policy))
            {
                if (ctx.BlockIndex > policy.ValidUntil)
                {
                    throw new PolicyExpiredException(policy, ctx.BlockIndex);
                }

                if (policy.AdminAddress != ctx.Signer)
                {
                    throw new PermissionDeniedException(policy, ctx.Signer);
                }
            }
        }
    }
}
