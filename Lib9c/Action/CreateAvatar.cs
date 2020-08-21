using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Bencodex.Types;
using Libplanet;
using Libplanet.Action;
using Libplanet.Assets;
using Nekoyume.Model.Item;
using Nekoyume.Model.State;
using Nekoyume.TableData;
using Serilog;

namespace Nekoyume.Action
{
    [Serializable]
    [ActionType("create_avatar")]
    public class CreateAvatar : GameAction
    {
        // 계정당 기본 소지 골드
        public static readonly BigInteger InitialGoldBalance = 1500;

        public Address avatarAddress;
        public int index;
        public int hair;
        public int lens;
        public int ear;
        public int tail;
        public string name;

        protected override IImmutableDictionary<string, IValue> PlainValueInternal => new Dictionary<string, IValue>()
        {
            ["avatarAddress"] = avatarAddress.Serialize(),
            ["index"] = (Integer) index,
            ["hair"] = (Integer) hair,
            ["lens"] = (Integer) lens,
            ["ear"] = (Integer) ear,
            ["tail"] = (Integer) tail,
            ["name"] = (Text) name,
        }.ToImmutableDictionary();

        protected override void LoadPlainValueInternal(IImmutableDictionary<string, IValue> plainValue)
        {
            avatarAddress = plainValue["avatarAddress"].ToAddress();
            index = (int) ((Integer) plainValue["index"]).Value;
            hair = (int) ((Integer) plainValue["hair"]).Value;
            lens = (int) ((Integer) plainValue["lens"]).Value;
            ear = (int) ((Integer) plainValue["ear"]).Value;
            tail = (int) ((Integer) plainValue["tail"]).Value;
            name = (Text) plainValue["name"];
        }

        public override IAccountStateDelta Execute(IActionContext context)
        {
            IActionContext ctx = context;
            var states = ctx.PreviousStates;
            if (ctx.Rehearsal)
            {
                states = states.SetState(ctx.Signer, MarkChanged);
                for (var i = 0; i < AvatarState.CombinationSlotCapacity; i++)
                {
                    var slotAddress = avatarAddress.Derive(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            CombinationSlotState.DeriveFormat,
                            i
                        )
                    );
                    states = states.SetState(slotAddress, MarkChanged);
                }

                return states
                    .SetState(avatarAddress, MarkChanged)
                    .MarkBalanceChanged(GoldCurrencyMock, GoldCurrencyState.Address, context.Signer);
            }

            if (!Regex.IsMatch(name, GameConfig.AvatarNickNamePattern))
            {
                return LogError(
                    context,
                    "Aborted as the input name {@Name} does not follow the allowed name pattern.",
                    name
                );
            }

            var sw = new Stopwatch();
            sw.Start();
            var started = DateTimeOffset.UtcNow;
            Log.Debug("CreateAvatar exec started.");
            AgentState existingAgentState = states.GetAgentState(ctx.Signer);
            var agentState = existingAgentState ?? new AgentState(ctx.Signer);
            var avatarState = states.GetAvatarState(avatarAddress);
            if (!(avatarState is null))
            {
                return LogError(context, "Aborted as there is already an avatar at {Address}.", avatarAddress);
            }

            if (agentState.avatarAddresses.ContainsKey(index))
            {
                return LogError(context, "Aborted as the signer already has an avatar at index #{Index}.", index);
            }
            sw.Stop();
            Log.Debug("CreateAvatar Get AgentAvatarStates: {Elapsed}", sw.Elapsed);
            sw.Restart();

            if (existingAgentState is null)
            {
                // 첫 아바타 생성이면 계정당 기본 소지금 부여.
                states = states.TransferAsset(
                    GoldCurrencyState.Address,
                    ctx.Signer,
                    states.GetGoldCurrency() * InitialGoldBalance
                );
            }

            Log.Debug("Execute CreateAvatar; player: {AvatarAddress}", avatarAddress);

            agentState.avatarAddresses.Add(index, avatarAddress);

            // Avoid NullReferenceException in test
            avatarState = CreateAvatarState(name, avatarAddress, ctx);

            if (hair < 0) hair = 0;
            if (lens < 0) lens = 0;
            if (ear < 0) ear = 0;
            if (tail < 0) tail = 0;

            avatarState.Customize(hair, lens, ear, tail);

            foreach (var address in avatarState.combinationSlotAddresses)
            {
                var slotState =
                    new CombinationSlotState(address, GameConfig.RequireClearedStageLevel.CombinationEquipmentAction);
                states = states.SetState(address, slotState.Serialize());
            }

            avatarState.UpdateQuestRewards(ctx);

            sw.Stop();
            Log.Debug("CreateAvatar CreateAvatarState: {Elapsed}", sw.Elapsed);
            var ended = DateTimeOffset.UtcNow;
            Log.Debug("CreateAvatar Total Executed Time: {Elapsed}", ended - started);
            return states
                .SetState(ctx.Signer, agentState.Serialize())
                .SetState(avatarAddress, avatarState.Serialize());
        }

        private static AvatarState CreateAvatarState(string name, Address avatarAddress, IActionContext ctx)
        {
            var tableSheets = TableSheets.FromActionContext(ctx);
            var gameConfigState = ctx.PreviousStates.GetGameConfigState();
            var avatarState = new AvatarState(
                avatarAddress,
                ctx.Signer,
                ctx.BlockIndex,
                tableSheets,
                gameConfigState,
                name
            );

            if (GameConfig.IsEditor)
            {
                AddItemsForTest(avatarState, ctx.Random, tableSheets);
            }

            return avatarState;
        }

        private static void AddItemsForTest(AvatarState avatarState, IRandom random, TableSheets tableSheets)
        {
            foreach (var row in tableSheets.CostumeItemSheet)
            {
                avatarState.inventory.AddItem(ItemFactory.CreateCostume(row));
            }

            foreach (var row in tableSheets.MaterialItemSheet)
            {
                avatarState.inventory.AddItem(ItemFactory.CreateMaterial(row), 10);
            }

            foreach (var pair in tableSheets.EquipmentItemSheet.Where(pair =>
                pair.Value.Id > GameConfig.DefaultAvatarWeaponId))
            {
                var itemId = random.GenerateRandomGuid();
                avatarState.inventory.AddItem(ItemFactory.CreateItemUsable(pair.Value, itemId, default));
            }
        }
    }
}
