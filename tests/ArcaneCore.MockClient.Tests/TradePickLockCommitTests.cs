using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// Pick Lock aimed at the partner's lockbox in the non-traded slot is stored with the trade and cast for real when the trade commits
/// (vmangos Spell.cpp:6455-6477, TradeHandler.cpp:347-416). The commit cast has no cast item and is triggered, so it never rolls the orange failure
/// (Spell.cpp:6050-6052), and it opens a box that is in the partner's bags, not the caster's.
/// </summary>
public sealed class TradePickLockCommitTests
{
    private const uint PickLock = 992801;
    private const uint Lockbox = 992802;
    private const uint BoxLock = 992803;
    private const uint LockTypePicking = 1;

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => Math.Clamp(value, minValue, maxValue - 1);
    }

    [Fact]
    public async Task CommittedPickLock_OnThePartnersOrangeLockbox_UnlocksIt_DespiteAFailingRoll_AndOpensNoLootWindowForTheCaster()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true, configureServices: services =>
            services.AddSingleton(new SkillCatalog(
                [new SkillLineRecord(SkillIds.Lockpicking, SkillCategories.Secondary, "Lockpicking", 0)],
                [new SkillRaceClassInfoRecord(SkillIds.Lockpicking, 0, 0, 0, 0, 21)],
                [new SkillTierRecord(21, Enumerable.Repeat(0u, 16).ToArray(), Enumerable.Repeat(75u, 16).ToArray())],
                [])));
        ObjectGuid boxGuid = await rig.Server.World.InvokeAsync(() =>
        {
            Player caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Player recipient = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            SpellFeature spells = rig.Server.Services.GetRequiredService<SpellFeature>();
            spells.System.Store = new SpellStore([.. spells.System.Store.All, new SpellInfo
            {
                Id = PickLock,
                Targets = 0x10,
                Effects =
                [
                    new SpellEffectInfo
                    {
                        Effect = SpellEffectName.OpenLockItem, TargetA = (SpellImplicitTarget)26, MiscValue = (int)LockTypePicking,
                        BasePoints = -1, BaseDice = 1, DieSides = 1,
                    },
                ],
            }], [], []);
            // Skill 1 against a lock needing 1 is orange; a roll of 0 against irand(-24, 38) fails it whenever it is rolled.
            spells.System.Random = new FixedRandom(0);
            spells.Spellbook.LearnSpell(caster, PickLock);
            Assert.NotNull(caster.Skills);
            caster.Skills!.Set(SkillIds.Lockpicking, 1, 300, 1);

            uint[] types = new uint[LockEntry.Cases];
            uint[] indexes = new uint[LockEntry.Cases];
            uint[] skills = new uint[LockEntry.Cases];
            (types[0], indexes[0], skills[0]) = (2, LockTypePicking, 1);
            rig.Server.Services.GetRequiredService<GameObjectLootFeature>()
                .ReplaceContent(new GameObjectContent([], [], [new LockEntry(BoxLock, types, indexes, skills)], [], []));

            var templates = new ItemTemplateStore([new ItemTemplate { Entry = Lockbox, Class = 15, Name = "Synthetic Lockbox", Flags = 0x4, LockId = BoxLock }], []);
            caster.Inventory.Templates = templates;
            recipient.Inventory.Templates = templates;
            Assert.Equal(InventoryResult.Ok, recipient.Inventory.AddItem(Lockbox, 1, out Item? box));
            Assert.Equal(0, (int)(box!.DynamicFlags & ItemDynFlags.Unlocked));
            return box.Guid;
        }).WaitAsync(rig.Token);

        await rig.Sender.SendAsync(WorldOpcode.CmsgInitiateTrade, ScenarioWire.Guid(rig.ReceiverGuid), rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.BeginTrade, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgBeginTrade, [], rig.Token);
        await ReadStatusAsync(rig.Sender, TradeStatus.OpenWindow, rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.OpenWindow, rig.Token);

        (byte bag, byte slot) = await rig.Server.World.InvokeAsync(() =>
        {
            Item box = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(boxGuid)!;
            return (box.BagSlot, box.Slot);
        }).WaitAsync(rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgSetTradeItem, [6, bag, slot], rig.Token);
        await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);

        var cast = new PacketWriter();
        cast.WriteUInt32(PickLock);
        new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = new ObjectGuid(6) }.Write(cast);
        await rig.Sender.SendAsync(WorldOpcode.CmsgCastSpell, cast.ToArray(), rig.Token);
        while (true)
        {
            byte[] extended = await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(extended.AsSpan(13)) == PickLock)
            {
                break;
            }
        }

        Assert.False(await BoxUnlockedAsync(rig, boxGuid), "nothing is picked before the trade commits");

        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeAccept, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeComplete, rig.Token);

        while (!await BoxUnlockedAsync(rig, boxGuid))
        {
            await Task.Delay(10, rig.Token);
        }

        await rig.Server.World.InvokeAsync(() =>
        {
            Player caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Player recipient = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            Assert.Null(caster.Inventory.GetItemByGuid(boxGuid));
            Assert.NotNull(recipient.Inventory.GetItemByGuid(boxGuid));
            Assert.Null(rig.Server.Services.GetRequiredService<GameObjectLootFeature>().FindSystem(caster.Map!)?.Loot?.OpenLootOf(caster));
            return true;
        }).WaitAsync(rig.Token);

        // Nothing the caster was sent after the commit is a loot window (the box is not theirs to open).
        await rig.Sender.SendAsync(WorldOpcode.CmsgPing, ScenarioWire.Ping(7, 0), rig.Token);
        while (true)
        {
            WorldFrame frame = await rig.Sender.ReadAsync(rig.Token);
            Assert.NotEqual((ushort)WorldOpcode.SmsgLootResponse, frame.Opcode);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgPong)
            {
                break;
            }
        }
    }

    private static async Task<bool> BoxUnlockedAsync(Rig rig, ObjectGuid boxGuid)
        => await rig.Server.World.InvokeAsync(() =>
            (rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(boxGuid)!.DynamicFlags & ItemDynFlags.Unlocked) != 0)
            .WaitAsync(rig.Token);

    private static async Task ReadStatusAsync(ScenarioConnection connection, TradeStatus status, CancellationToken token)
    {
        while (true)
        {
            byte[] payload = await connection.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload) == (uint)status)
            {
                return;
            }
        }
    }
}
