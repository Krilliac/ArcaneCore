using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Npc.NpcServiceKit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>Bankers, spirit healers and gossip option routing (vmangos NPCHandler.cpp, Player::OnGossipSelect).</summary>
public sealed class NpcBankSpiritServiceTests
{
    // ---- banker -------------------------------------------------------------------------------

    [Fact]
    public void BankerActivate_ShowsTheBankWhileTheBankerStaysInRange()
    {
        using var kit = new NpcServiceKit(NpcFlags.Banker);
        Assert.False(kit.Player.Inventory.BankUsable);
        kit.Services.BankerActivate(kit.Player, kit.Npc.Guid);
        Assert.Equal(BitConverter.GetBytes(kit.Npc.Guid.Value), kit.Single(WorldOpcode.SmsgShowBank));
        Assert.True(kit.Player.Inventory.BankUsable);
        kit.Npc = kit.Npc with { X = kit.Player.X + 30 };
        Assert.False(kit.Player.Inventory.BankUsable);
    }

    [Fact]
    public void BankerActivate_OutOfRangeDeadOrNotABanker_ShowsNothing()
    {
        using (var far = new NpcServiceKit(NpcFlags.Banker, npcDistance: 8))
        {
            far.Services.BankerActivate(far.Player, far.Npc.Guid);
            Assert.Empty(far.Drain());
            Assert.False(far.Player.Inventory.BankUsable);
        }

        using (var dead = new NpcServiceKit(NpcFlags.Banker))
        {
            dead.Player.Health = 0;
            dead.Services.BankerActivate(dead.Player, dead.Npc.Guid);
            Assert.Empty(dead.Drain());
        }

        using var vendor = new NpcServiceKit(NpcFlags.Vendor);
        vendor.Services.BankerActivate(vendor.Player, vendor.Npc.Guid);
        Assert.Empty(vendor.Drain());
    }

    private static BankBagSlotPriceTable Prices() => new([(1u, 1000u), (2u, 7500u), (3u, 15000u), (4u, 30000u), (5u, 60000u), (6u, 120000u)]);

    [Fact]
    public void BuyBankSlot_ChargesTheNextSlotPriceOnceTheCountIsStored()
    {
        var stored = new List<byte>();
        using var kit = new NpcServiceKit(NpcFlags.Banker, bankPrices: Prices(), persistBankSlots: (_, count) =>
        {
            stored.Add(count);
            return true;
        });
        kit.Player.Money = 9000;
        kit.Services.BuyBankSlot(kit.Player, kit.Npc.Guid);
        Assert.Equal(1, kit.Player.Inventory.BankBagSlotCount);
        Assert.Equal(8000u, kit.Player.Money);
        kit.Services.BuyBankSlot(kit.Player, kit.Npc.Guid);
        Assert.Equal(2, kit.Player.Inventory.BankBagSlotCount);
        Assert.Equal(500u, kit.Player.Money);

        kit.Services.BuyBankSlot(kit.Player, kit.Npc.Guid); // 15000 needed
        Assert.Equal(2, kit.Player.Inventory.BankBagSlotCount);
        Assert.Equal(500u, kit.Player.Money);
        Assert.Equal([1, 2], stored);

        kit.Player.Inventory.BankBagSlotCount = QuestNpcServices.MaxBankBagSlots;
        kit.Player.Money = 1_000_000;
        kit.Services.BuyBankSlot(kit.Player, kit.Npc.Guid);
        Assert.Equal(1_000_000u, kit.Player.Money);
        Assert.Equal([1, 2], stored);
    }

    [Fact]
    public void BuyBankSlot_WithoutPersistencePricesOrRange_BuysNothing()
    {
        using (var unpersisted = new NpcServiceKit(NpcFlags.Banker, bankPrices: Prices()))
        {
            unpersisted.Player.Money = 9000;
            unpersisted.Services.BuyBankSlot(unpersisted.Player, unpersisted.Npc.Guid);
            Assert.Equal(0, unpersisted.Player.Inventory.BankBagSlotCount);
            Assert.Equal(9000u, unpersisted.Player.Money);
        }

        using (var unpriced = new NpcServiceKit(NpcFlags.Banker, persistBankSlots: (_, _) => true))
        {
            unpriced.Player.Money = 9000;
            unpriced.Services.BuyBankSlot(unpriced.Player, unpriced.Npc.Guid);
            Assert.Equal(0, unpriced.Player.Inventory.BankBagSlotCount);
            Assert.Equal(9000u, unpriced.Player.Money);
        }

        using var far = new NpcServiceKit(NpcFlags.Banker, npcDistance: 8, bankPrices: Prices(), persistBankSlots: (_, _) => true);
        far.Player.Money = 9000;
        far.Services.BuyBankSlot(far.Player, far.Npc.Guid);
        Assert.Equal(0, far.Player.Inventory.BankBagSlotCount);
        Assert.Equal(9000u, far.Player.Money);
    }

    // ---- spirit healer ------------------------------------------------------------------------

    private static readonly SpellInfo Sickness = SpellTestKit.Spell(SpiritHealerResurrection.ResurrectionSicknessSpell,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, -75, aura: AuraType.Dummy)) with
    {
        Duration = new SpellDuration(600_000, 0, 600_000),
    };

    private sealed class SpiritRig : IDisposable
    {
        public SpiritRig(byte level = 15, bool withSpells = true, NpcContent? content = null)
        {
            Spells = new SpellSystem(new SpellStore([Sickness], [], []), () => 0u) { MapUpdateIntervalMs = 0 };
            Kit = new NpcServiceKit(NpcFlags.SpiritHealer | NpcFlags.Gossip, content, new QuestNpcDependencies(
                Resurrection: new DelegateResurrection(player => Inner!.ResurrectAtSpiritHealer(player))));
            Inner = new SpiritHealerResurrection(() => withSpells ? Spells : null, Kit.Items, Saved.Add);
            Player p = Kit.Player;
            p.Level = level;
            p.MaxHealth = 1000;
            p.Health = 40;
            Map = Kit.World.GetMap(0);
            Map.Combat.Random = new ScriptedRandom();
            Killer = CombatTestKit.AddPlayer(Kit.World, 2, -2, 0, KillerSession, Race.Orc);
            Killer.SetFloat(UpdateFields.UnitFieldMindamage, 50);
            Killer.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
            Kit.World.RunTick(1);
        }

        public NpcServiceKit Kit { get; }

        public SpiritHealerResurrection? Inner { get; }

        public SpellSystem Spells { get; }

        public Map Map { get; }

        public Player Killer { get; }

        public FakeSession KillerSession { get; } = new(2);

        public List<Player> Saved { get; } = [];

        public void KillAndRelease()
        {
            Map.Combat.Attack(Killer, Kit.Player);
            Kit.World.RunTick(50);
            Map.Combat.AttackStop(Killer);
            Assert.False(Kit.Player.IsAlive);
            Assert.True(Map.Combat.RepopPlayer(Kit.Player));
            Assert.NotNull(Kit.Player.Combat.Corpse);
            Kit.Npc = Kit.Npc with { X = Kit.Player.X + 1, Y = Kit.Player.Y, Z = Kit.Player.Z };
            Kit.Drain();
        }

        public void Dispose() => Kit.Dispose();
    }

    private sealed class DelegateResurrection(Func<Player, bool> resurrect) : IResurrection
    {
        public bool ResurrectAtSpiritHealer(Player player) => resurrect(player);
    }

    [Fact]
    public void SpiritHealerActivate_ResurrectsAGhostWithSicknessAndDurabilityLoss()
    {
        using var rig = new SpiritRig();
        NpcServiceKit kit = rig.Kit;
        Item helm = kit.Give(Helm);
        kit.Player.Inventory.AutoEquipItem(InventorySlots.Bag0, helm.Slot);
        rig.KillAndRelease();

        kit.Services.SpiritHealerActivate(kit.Player, kit.Npc.Guid);
        Assert.True(kit.Player.IsAlive);
        Assert.Equal(500u, kit.Player.Health);
        Assert.False(kit.Player.Flags.HasFlag(PlayerFlags.Ghost));
        Assert.Null(kit.Player.Combat.Corpse);
        Assert.Equal(30u, helm.Durability);
        Assert.Equal([kit.Player], rig.Saved);
        SpellAuraHolder sickness = Assert.Single(rig.Spells.GetAuras(kit.Player), h => h.Spell.Id == SpiritHealerResurrection.ResurrectionSicknessSpell);
        Assert.Equal(5 * 60 * 1000, sickness.MaxDuration);
        Assert.Contains(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgUpdateAuraDuration);
    }

    [Fact]
    public void SpiritHealerActivate_BelowSicknessLevel_OnlyResurrects()
    {
        using var rig = new SpiritRig(level: 10);
        rig.KillAndRelease();
        rig.Kit.Services.SpiritHealerActivate(rig.Kit.Player, rig.Kit.Npc.Guid);
        Assert.True(rig.Kit.Player.IsAlive);
        Assert.Empty(rig.Spells.GetAuras(rig.Kit.Player));
    }

    [Fact]
    public void SpiritHealerActivate_RefusesTheLivingTheUnreleasedAndTheFar()
    {
        using var rig = new SpiritRig();
        NpcServiceKit kit = rig.Kit;
        kit.Services.SpiritHealerActivate(kit.Player, kit.Npc.Guid); // alive
        Assert.Empty(rig.Saved);

        rig.Map.Combat.Attack(rig.Killer, kit.Player);
        kit.World.RunTick(50);
        rig.Map.Combat.AttackStop(rig.Killer);
        kit.Services.SpiritHealerActivate(kit.Player, kit.Npc.Guid); // dead, not yet a ghost
        Assert.False(kit.Player.IsAlive);
        Assert.Empty(rig.Saved);

        Assert.True(rig.Map.Combat.RepopPlayer(kit.Player));
        kit.Npc = kit.Npc with { X = kit.Player.X + 30, Y = kit.Player.Y, Z = kit.Player.Z };
        kit.Services.SpiritHealerActivate(kit.Player, kit.Npc.Guid);
        Assert.False(kit.Player.IsAlive);
        Assert.Empty(rig.Saved);

        kit.Npc = kit.Npc with { X = kit.Player.X + 1, NpcFlags = NpcFlags.Vendor };
        kit.Services.SpiritHealerActivate(kit.Player, kit.Npc.Guid);
        Assert.False(kit.Player.IsAlive);
    }

    [Fact]
    public void Ghosts_CanOnlyUseSpiritServices()
    {
        using var rig = new SpiritRig();
        NpcServiceKit kit = rig.Kit;
        rig.KillAndRelease();
        kit.Npc = kit.Npc with { NpcFlags = NpcFlags.Vendor | NpcFlags.Gossip };
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        Assert.Empty(kit.Drain());
    }

    [Fact]
    public void GossipSpiritHealerOption_AsksTheGhostToConfirm()
    {
        var content = NpcContent.Empty with
        {
            GossipMenuOptions =
            [
                new GossipMenuOption { MenuId = 0, Id = 0, OptionId = (byte)GossipOption.SpiritHealer, NpcOptionNpcFlag = (uint)NpcFlags.SpiritHealer, OptionText = "Return me to life." },
            ],
        };
        using var rig = new SpiritRig(content: content);
        NpcServiceKit kit = rig.Kit;
        rig.KillAndRelease();
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        Assert.True(kit.Sent(WorldOpcode.SmsgGossipMessage));
        kit.Drain();
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 0, null);
        var sent = kit.Drain();
        Assert.Contains(sent, p => p.Opcode == WorldOpcode.SmsgGossipComplete);
        Assert.Equal(BitConverter.GetBytes(kit.Npc.Guid.Value), Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgSpiritHealerConfirm).Payload);
        Assert.False(kit.Player.IsAlive); // the client confirms with CMSG_SPIRIT_HEALER_ACTIVATE
    }

    // ---- gossip routing -----------------------------------------------------------------------

    [Fact]
    public void GossipOptions_RouteToTheirServices()
    {
        static GossipMenuOption Option(uint id, GossipOption option, NpcFlags flag)
            => new() { MenuId = 0, Id = id, OptionId = (byte)option, NpcOptionNpcFlag = (uint)flag, OptionText = option.ToString() };

        NpcContent content = NpcContent.Empty with
        {
            VendorItems = [new VendorItem { Entry = NpcServiceKit.Entry, Item = Bread }],
            GossipMenuOptions =
            [
                Option(0, GossipOption.Vendor, NpcFlags.Vendor),
                Option(1, GossipOption.Innkeeper, NpcFlags.Innkeeper),
                Option(2, GossipOption.Banker, NpcFlags.Banker),
                Option(3, GossipOption.SpiritHealer, NpcFlags.SpiritHealer), // hidden from the living
            ],
        };
        using var kit = new NpcServiceKit(NpcFlags.Gossip | NpcFlags.Vendor | NpcFlags.Innkeeper | NpcFlags.Banker, content);
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        var menu = new PacketReader(kit.Single(WorldOpcode.SmsgGossipMessage));
        menu.ReadUInt64();
        menu.ReadUInt32();
        Assert.Equal(3u, menu.ReadUInt32());

        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 0, null);
        Assert.True(kit.Sent(WorldOpcode.SmsgListInventory));
        kit.Drain();
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 1, null);
        Assert.True(kit.Sent(WorldOpcode.SmsgBinderConfirm));
        kit.Drain();
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 2, null);
        Assert.True(kit.Sent(WorldOpcode.SmsgShowBank));
        Assert.True(kit.Player.Inventory.BankUsable);
        kit.Drain();
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 3, null);
        Assert.Empty(kit.Drain());
    }
}
