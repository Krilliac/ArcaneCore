using System.Buffers.Binary;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Updates;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Socket-level settlement coverage for deferred cast-item trade enchants.</summary>
public sealed class TradeItemEnchantSettlementTests
{
    private const uint Spell = 992701;
    private const uint Enchant = 992702;
    private const uint CastEntry = 992703;
    private const uint TargetEntry = 992704;
    private const uint ReagentEntry = 992705;

    [Fact]
    public async Task UseItemTemplateSpellWithoutSpellbookKnowledge_CommitsPartialChargeAndReagent()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte castBag, byte castSlot, ObjectGuid castGuid, ObjectGuid targetGuid) = await ConfigureAsync(rig, charges: 2);
        await OpenAsync(rig);
        await SetTargetAsync(rig, targetGuid);

        await rig.Sender.SendAsync(WorldOpcode.CmsgUseItem,
            UseItemPayload(castBag, castSlot, 0, rawTradeSlot: 6), rig.Token);
        await ReadPendingAsync(rig.Sender, rig.Token);

        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            var target = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(targetGuid)!;
            Assert.True(caster.Inventory.GetItemByGuid(castGuid)!.IsSoulBound);
            Assert.Equal(1u, caster.Inventory.GetItemCount(ReagentEntry));
            Assert.Equal(0u, target.EnchantmentId(0));
            Assert.False(rig.Server.Services.GetRequiredService<SpellFeature>().Spellbook.HasSpell(caster, Spell));
            return true;
        }).WaitAsync(rig.Token);

        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeAccept, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        byte[] go = await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgSpellGo, rig.Token);
        Assert.Equal(SpellPackets.BuildSpellGo(castGuid, new ObjectGuid(rig.SenderGuid), Spell,
            SpellCastFlags.Unknown9, [], [], new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = targetGuid }), go);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeComplete, rig.Token);

        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            var target = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(targetGuid)!;
            Item? cast = caster.Inventory.GetItemByGuid(castGuid);
            Assert.NotNull(cast);
            Assert.True(cast.IsSoulBound);
            Assert.Equal(1, cast.GetInt32(UpdateFields.ItemFieldSpellCharges));
            Assert.Equal(0u, caster.Inventory.GetItemCount(ReagentEntry));
            Assert.Equal(Enchant, target.EnchantmentId(0));
            return true;
        }).WaitAsync(rig.Token);
    }

    [Fact]
    public async Task FinalAcceptanceRechecksCastGuid_WhenSourceIsDeleted()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte castBag, byte castSlot, ObjectGuid castGuid, ObjectGuid targetGuid) = await ConfigureAsync(rig, charges: 2);
        await OpenAsync(rig);
        await SetTargetAsync(rig, targetGuid);
        await rig.Sender.SendAsync(WorldOpcode.CmsgUseItem, UseItemPayload(castBag, castSlot, 0, 6), rig.Token);
        await ReadPendingAsync(rig.Sender, rig.Token);

        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Equal(1u, caster.Inventory.DestroyItemCount(CastEntry, 1));
            return true;
        }).WaitAsync(rig.Token);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeAccept, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.BackToTrade, rig.Token);

        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            var target = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(targetGuid)!;
            Assert.Null(caster.Inventory.GetItemByGuid(castGuid));
            Assert.Equal(1u, caster.Inventory.GetItemCount(ReagentEntry));
            Assert.Equal(0u, target.EnchantmentId(0));
            return true;
        }).WaitAsync(rig.Token);
    }

    [Fact]
    public async Task FinalAcceptanceOfLastNegativeCharge_DeletesExpendableCastItem()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (_, _, ObjectGuid castGuid, ObjectGuid targetGuid) = await ConfigureAsync(rig, charges: -1);
        await OpenAsync(rig);
        await SetTargetAsync(rig, targetGuid);
        var location = await rig.Server.World.InvokeAsync(() =>
        {
            Item item = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!.Inventory.GetItemByGuid(castGuid)!;
            return (item.BagSlot, item.Slot);
        }).WaitAsync(rig.Token);
        await rig.Sender.SendAsync(WorldOpcode.CmsgUseItem, UseItemPayload(location.Item1, location.Item2, 0, 6), rig.Token);
        await ReadPendingAsync(rig.Sender, rig.Token);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeAccept, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        byte[] go = await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgSpellGo, rig.Token);
        Assert.Equal(SpellPackets.BuildSpellGo(castGuid, new ObjectGuid(rig.SenderGuid), Spell,
            SpellCastFlags.Unknown9, [], [], new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = targetGuid }), go);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeComplete, rig.Token);

        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Null(caster.Inventory.GetItemByGuid(castGuid));
            Assert.Equal(0u, caster.Inventory.GetItemCount(ReagentEntry));
            Assert.Equal(Enchant, rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(targetGuid)!.EnchantmentId(0));
            return true;
        }).WaitAsync(rig.Token);
    }

    [Theory]
    [InlineData(false, 1u, 1u)]
    [InlineData(true, 1u, 2u)]
    [InlineData(true, 2u, 1u)]
    public async Task SameEntryReagentOverlap_SkipsSecondItemUsePayment(bool stacked, uint reagentCount, uint expectedRemaining)
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (_, _, ObjectGuid castGuid, ObjectGuid targetGuid) = await ConfigureAsync(rig, charges: -1, sameEntryReagent: true, stacked: stacked, reagentCount: reagentCount);
        await OpenAsync(rig);
        await SetTargetAsync(rig, targetGuid);
        var location = await rig.Server.World.InvokeAsync(() =>
        {
            Item item = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!.Inventory.GetItemByGuid(castGuid)!;
            return (item.BagSlot, item.Slot);
        }).WaitAsync(rig.Token);
        await rig.Sender.SendAsync(WorldOpcode.CmsgUseItem, UseItemPayload(location.Item1, location.Item2, 0, 6), rig.Token);
        await ReadPendingAsync(rig.Sender, rig.Token);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeAccept, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        byte[] go = await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgSpellGo, rig.Token);
        Assert.Equal(SpellPackets.BuildSpellGo(new ObjectGuid(rig.SenderGuid), new ObjectGuid(rig.SenderGuid), Spell,
            SpellCastFlags.Unknown9, [], [], new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = targetGuid }), go);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeComplete, rig.Token);

        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            // vmangos clears the cast-item payment pointer on matching reagent entry,
            // even when a different stack supplied it. The extra last-charge reagent
            // applies when the SPELL reagent requirement exceeds one (Spell.cpp:7269),
            // not merely when the cast item's stack contains more than one.
            Item? remainingCast = caster.Inventory.GetItemByGuid(castGuid);
            Assert.NotNull(remainingCast);
            Assert.Equal(-1, remainingCast.GetInt32(UpdateFields.ItemFieldSpellCharges));
            Assert.Equal(expectedRemaining, caster.Inventory.GetItemCount(CastEntry));
            Assert.Equal(Enchant, rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(targetGuid)!.EnchantmentId(0));
            return true;
        }).WaitAsync(rig.Token);
    }

    [Fact]
    public async Task MultiOnUseTemplate_IsRefusedWithoutPendingSettlement()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte castBag, byte castSlot, ObjectGuid castGuid, ObjectGuid targetGuid) = await ConfigureAsync(rig, charges: 2, secondSpell: Spell + 1);
        await OpenAsync(rig);
        await SetTargetAsync(rig, targetGuid);
        await rig.Sender.SendAsync(WorldOpcode.CmsgUseItem, UseItemPayload(castBag, castSlot, 0, 6), rig.Token);
        await rig.Sender.SendAsync(WorldOpcode.CmsgSetTradeGold, ScenarioWire.UInt32(1), rig.Token);
        await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);

        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.True(caster.Inventory.GetItemByGuid(castGuid)!.IsSoulBound);
            Assert.Equal(1u, caster.Inventory.GetItemCount(ReagentEntry));
            Assert.Equal(0u, rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(targetGuid)!.EnchantmentId(0));
            Assert.Null(rig.Server.Services.GetRequiredService<EconomyFeature>().TradeOf(caster)!.SideOf(caster).PendingEnchantment);
            return true;
        }).WaitAsync(rig.Token);
    }

    [Fact]
    public async Task PendingCastFollowsCurrentGuid_WhenItemMovesBeforeAcceptance()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte castBag, byte castSlot, ObjectGuid castGuid, ObjectGuid targetGuid) = await ConfigureAsync(rig, charges: 1);
        await OpenAsync(rig);
        await SetTargetAsync(rig, targetGuid);
        await rig.Sender.SendAsync(WorldOpcode.CmsgUseItem, UseItemPayload(castBag, castSlot, 0, 6), rig.Token);
        await ReadPendingAsync(rig.Sender, rig.Token);

        await rig.Sender.SendAsync(WorldOpcode.CmsgSwapItem, [0, (byte)(castSlot + 1), castBag, castSlot], rig.Token);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeAccept, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgSpellGo, rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeComplete, rig.Token);

        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.NotNull(caster.Inventory.GetItemByGuid(castGuid));
            Assert.Equal(0u, caster.Inventory.GetItemCount(ReagentEntry));
            Assert.Equal(Enchant, rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(targetGuid)!.EnchantmentId(0));
            return true;
        }).WaitAsync(rig.Token);
    }

    [Fact]
    public async Task ChangedItemSpellMetadata_RefusesFinalAcceptanceAndClearsPending()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte bag, byte slot, ObjectGuid castGuid, ObjectGuid targetGuid) = await ConfigureAsync(rig, charges: 2);
        await OpenAsync(rig);
        await SetTargetAsync(rig, targetGuid);
        await rig.Sender.SendAsync(WorldOpcode.CmsgUseItem, UseItemPayload(bag, slot, 0, 6), rig.Token);
        await ReadPendingAsync(rig.Sender, rig.Token);
        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Item cast = caster.Inventory.GetItemByGuid(castGuid)!;
            var templateSpells = Assert.IsAssignableFrom<IList<ItemSpell>>(cast.Template.Spells);
            templateSpells[0] = templateSpells[0] with { Cooldown = 9_999 };
            return true;
        }).WaitAsync(rig.Token);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeAccept, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.BackToTrade, rig.Token);
        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Null(rig.Server.Services.GetRequiredService<EconomyFeature>().TradeOf(caster)!.SideOf(caster).PendingEnchantment);
            Assert.Equal(2, caster.Inventory.GetItemByGuid(castGuid)!.GetInt32(UpdateFields.ItemFieldSpellCharges));
            Assert.Equal(1u, caster.Inventory.GetItemCount(ReagentEntry));
            Assert.Equal(0u, rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!
                .Inventory.GetItemByGuid(targetGuid)!.EnchantmentId(0));
            return true;
        }).WaitAsync(rig.Token);
    }

    [Fact]
    public async Task NonSixTradeTarget_RefusesDeferralWithOrdinaryBindingPreserved()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte bag, byte slot, ObjectGuid castGuid, ObjectGuid targetGuid) = await ConfigureAsync(rig, charges: 2);
        await OpenAsync(rig);
        await SetTargetAsync(rig, targetGuid);
        await rig.Sender.SendAsync(WorldOpcode.CmsgUseItem, UseItemPayload(bag, slot, 0, 5), rig.Token);
        await rig.Sender.SendAsync(WorldOpcode.CmsgSetTradeGold, ScenarioWire.UInt32(1), rig.Token);
        await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Null(rig.Server.Services.GetRequiredService<EconomyFeature>().TradeOf(caster)!.SideOf(caster).PendingEnchantment);
            Assert.True(caster.Inventory.GetItemByGuid(castGuid)!.IsSoulBound);
            Assert.Equal(2, caster.Inventory.GetItemByGuid(castGuid)!.GetInt32(UpdateFields.ItemFieldSpellCharges));
            Assert.Equal(0u, rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!
                .Inventory.GetItemByGuid(targetGuid)!.EnchantmentId(0));
            return true;
        }).WaitAsync(rig.Token);
    }

    [Fact]
    public async Task PersistenceConflict_PreservesChargesReagentsAndTargetWithoutCooldown()
    {
        var entered = new TaskCompletionSource<EconomyCommitRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Rig rig = await Rig.StartAsync(loginReceiver: true, configureServices: services =>
            services.AddScoped<IEconomyStore>(provider => new RefusingEconomyStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), entered)));
        (byte bag, byte slot, ObjectGuid castGuid, ObjectGuid targetGuid) = await ConfigureAsync(rig, charges: 2);
        await OpenAsync(rig);
        await SetTargetAsync(rig, targetGuid);
        await rig.Sender.SendAsync(WorldOpcode.CmsgUseItem, UseItemPayload(bag, slot, 0, 6), rig.Token);
        await ReadPendingAsync(rig.Sender, rig.Token);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeAccept, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await entered.Task.WaitAsync(rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeCanceled, rig.Token);
        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Item cast = caster.Inventory.GetItemByGuid(castGuid)!;
            Assert.True(cast.IsSoulBound); // ordinary binding precedes the deferred operation
            Assert.Equal(2, cast.GetInt32(UpdateFields.ItemFieldSpellCharges));
            Assert.Equal(1u, caster.Inventory.GetItemCount(ReagentEntry));
            Assert.Equal(0u, rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!
                .Inventory.GetItemByGuid(targetGuid)!.EnchantmentId(0));
            Assert.DoesNotContain(rig.Server.Services.GetRequiredService<SpellFeature>().System.GetActiveCooldowns(caster),
                cooldown => cooldown.SpellId == Spell);
            return true;
        }).WaitAsync(rig.Token);
    }

    private static async Task<(byte Bag, byte Slot, ObjectGuid CastGuid, ObjectGuid TargetGuid)> ConfigureAsync(Rig rig, int charges, uint? secondSpell = null, bool sameEntryReagent = false, bool stacked = false, uint reagentCount = 1)
        => await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            var recipient = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            SpellFeature feature = rig.Server.Services.GetRequiredService<SpellFeature>();
            uint reagentEntry = sameEntryReagent ? CastEntry : ReagentEntry;
            feature.System.Store = new SpellStore([.. feature.System.Store.All,
                new SpellInfo { Id = Spell, Effects = [new SpellEffectInfo { Effect = SpellEffectName.EnchantItem, MiscValue = (int)Enchant }], Reagents = [new SpellReagent((int)reagentEntry, reagentCount)] },
                new SpellInfo { Id = Spell + 1, Effects = [new SpellEffectInfo { Effect = SpellEffectName.EnchantItem, MiscValue = (int)Enchant }] }], [], []);
            feature.System.ItemEnchantments = new ItemEnchantmentCatalog([new ItemEnchantmentDefinition(Enchant, [])]);
            var castSpells = new List<ItemSpell> { new(Spell, 0, charges, 0, 1200, 77, 3400) };
            if (secondSpell is { } extra) castSpells.Add(new ItemSpell(extra, 0, 0, 0, 0, 0, 0));
            var templates = new ItemTemplateStore([
                new ItemTemplate { Entry = CastEntry, Class = 0, Stackable = stacked ? 20u : 1u, Bonding = (uint)ItemBonding.WhenUse, Spells = castSpells },
                new ItemTemplate { Entry = TargetEntry, Class = 0, Stackable = 1 },
                new ItemTemplate { Entry = ReagentEntry, Class = 0, Stackable = 20 }], []);
            caster.Inventory.Templates = templates;
            recipient.Inventory.Templates = templates;
            Assert.Equal(InventoryResult.Ok, caster.Inventory.AddItem(CastEntry, sameEntryReagent ? (stacked ? (reagentCount > 1 ? 4u : 3u) : 2u) : 1u, out Item? cast));
            if (!sameEntryReagent)
                Assert.Equal(InventoryResult.Ok, caster.Inventory.AddItem(ReagentEntry, 1, out _));
            Assert.Equal(InventoryResult.Ok, recipient.Inventory.AddItem(TargetEntry, 1, out Item? target));
            return (cast!.BagSlot, cast.Slot, cast.Guid, target!.Guid);
        }).WaitAsync(rig.Token);

    private static byte[] UseItemPayload(byte bag, byte slot, byte spellIndex, byte rawTradeSlot)
    {
        var writer = new PacketWriter();
        writer.WriteByte(bag);
        writer.WriteByte(slot);
        writer.WriteByte(spellIndex);
        new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = new ObjectGuid(rawTradeSlot) }.Write(writer);
        return writer.ToArray();
    }

    private static async Task OpenAsync(Rig rig)
    {
        await rig.Sender.SendAsync(WorldOpcode.CmsgInitiateTrade, ScenarioWire.Guid(rig.ReceiverGuid), rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.BeginTrade, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgBeginTrade, [], rig.Token);
        await ReadStatusAsync(rig.Sender, TradeStatus.OpenWindow, rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.OpenWindow, rig.Token);
    }

    private static async Task SetTargetAsync(Rig rig, ObjectGuid targetGuid)
    {
        var target = await rig.Server.World.InvokeAsync(() => rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(targetGuid)!).WaitAsync(rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgSetTradeItem, [6, target.BagSlot, target.Slot], rig.Token);
        await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
    }

    private static async Task ReadPendingAsync(ScenarioConnection connection, CancellationToken token)
    {
        while (true)
        {
            byte[] payload = await connection.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(13)) == Spell) return;
        }
    }

    private static async Task ReadStatusAsync(ScenarioConnection connection, TradeStatus status, CancellationToken token)
    {
        while (true)
        {
            byte[] payload = await connection.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload) == (uint)status) return;
        }
    }
    private sealed class RefusingEconomyStore(IEconomyStore inner,
        TaskCompletionSource<EconomyCommitRequest> entered) : IEconomyStore
    {
        public Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult(request);
            return Task.FromResult(EconomyCommitResult.Conflict);
        }

        public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default) => inner.IsCommittedAsync(operationId, cancellationToken);
        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default) => inner.GetMailsAsync(receiverId, cancellationToken);
        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default) => inner.GetExpiredMailsAsync(now, max, cancellationToken);
        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetMailsInvolvingAsync(characterId, cancellationToken);
        public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default) => inner.GetItemTextAsync(itemTextId, cancellationToken);
        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default) => inner.GetAuctionsAsync(cancellationToken);
        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default) => inner.GetEscrowItemsAsync(itemGuids, cancellationToken);
        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default) => inner.GetAuctionSnapshotAsync(filter, cancellationToken);
        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default) => inner.GetIdSeedAsync(cancellationToken);
    }

}
