using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Creature equipment (world schema 56): a template's <c>EquipmentTemplateId</c> resolves through <c>creature_equip_template</c> and the
/// world item store into the three virtual item slots, at creation and at every respawn. The packing is cmangos
/// <c>Creature::SetVirtualItem</c>: the item's display, then the info bytes class, subclass, material and inventory type, and the sheath in
/// the next word. Item rows are synthetic.
/// </summary>
public sealed class CreatureEquipmentTests
{
    private const uint SwordItem = 1899;
    private const uint ShieldItem = 143;
    private const uint BowItem = 2551;
    private const uint EquipmentId = 68;

    private static readonly ItemTemplate Sword = new() { Entry = SwordItem, Class = 2, SubClass = 7, DisplayId = 7420, Material = 1, InventoryType = 13, Sheath = 3 };
    private static readonly ItemTemplate Shield = new() { Entry = ShieldItem, Class = 4, SubClass = 6, DisplayId = 18730, Material = 1, InventoryType = 14, Sheath = 4 };
    private static readonly ItemTemplate Bow = new() { Entry = BowItem, Class = 2, SubClass = 2, DisplayId = 8105, Material = 2, InventoryType = 15, Sheath = 2 };

    private static ItemTemplate? Items(uint entry) => entry switch
    {
        SwordItem => Sword,
        ShieldItem => Shield,
        BowItem => Bow,
        _ => null,
    };

    private static CreatureContent ArmedContent(CreatureTemplate template, params CreatureEquipment[] sets)
        => new([template], [Spawn(1, template.Entry, 0, 0)], [], [], [], equipment: sets);

    private static Creature MakeCreature(CreatureTemplate template, CreatureContent content, Func<uint, ItemTemplate?>? items = null)
        => new(1, template, content.GetSpawns(0)[0], content, new Random(1), itemResolver: items);

    private static void AssertSlot(Creature creature, int slot, ItemTemplate item)
    {
        int info = UpdateFields.UnitVirtualItemInfo + (slot * 2);
        Assert.Equal(item.DisplayId, creature.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot));
        Assert.Equal((byte)item.Class, creature.GetByte(info, 0));
        Assert.Equal((byte)item.SubClass, creature.GetByte(info, 1));
        Assert.Equal((byte)item.Material, creature.GetByte(info, 2));
        Assert.Equal((byte)item.InventoryType, creature.GetByte(info, 3));
        Assert.Equal(item.Sheath, creature.GetUInt32(info + 1));
    }

    private static void AssertEmpty(Creature creature, int slot)
    {
        Assert.Equal(0u, creature.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot));
        Assert.Equal(0u, creature.GetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2)));
        Assert.Equal(0u, creature.GetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2) + 1));
    }

    [Fact]
    public void ASwordShieldAndRangedItem_FillTheThreeSlotsWithDisplayInfoAndSheath()
    {
        CreatureTemplate template = Template(GuardEntry) with { EquipmentTemplateId = EquipmentId };
        CreatureContent content = ArmedContent(template, new CreatureEquipment(EquipmentId, SwordItem, ShieldItem, BowItem));

        Creature guard = MakeCreature(template, content, Items);

        AssertSlot(guard, 0, Sword);
        AssertSlot(guard, 1, Shield);
        AssertSlot(guard, 2, Bow);
        Assert.True(CreatureDamageStats.HasWeapon(guard)); // vmangos Creature::HasWeapon: the main hand is class weapon
    }

    [Fact]
    public void ARespawn_AppliesTheEquipmentAgain_AndDropsWhatAScriptChanged()
    {
        CreatureTemplate template = Template(GuardEntry) with { EquipmentTemplateId = EquipmentId };
        CreatureContent content = ArmedContent(template, new CreatureEquipment(EquipmentId, SwordItem, ShieldItem, BowItem));
        Creature guard = MakeCreature(template, content, Items);

        guard.ClearVirtualItem(0);
        guard.SetVirtualItem(1, 999, 2, 1, 1, 13, 1);
        guard.ScriptEquipmentDefault = guard.SnapshotVirtualItems();

        guard.InitializeFields();

        AssertSlot(guard, 0, Sword);
        AssertSlot(guard, 1, Shield);
        AssertSlot(guard, 2, Bow);
        Assert.Null(guard.ScriptEquipmentDefault);
    }

    [Fact]
    public void NoEquipmentId_AnUnknownId_OrAnUnknownItem_LeaveTheSlotsAtZero()
    {
        CreatureTemplate none = Template(GuardEntry);
        CreatureTemplate unknownId = Template(GuardEntry) with { EquipmentTemplateId = 7777 };
        CreatureTemplate unknownItem = Template(GuardEntry) with { EquipmentTemplateId = EquipmentId };
        CreatureEquipment set = new(EquipmentId, 424242, 0, 0);

        foreach (CreatureTemplate template in new[] { none, unknownId, unknownItem })
        {
            Creature creature = MakeCreature(template, ArmedContent(template, set), Items);
            for (int slot = 0; slot < 3; slot++)
            {
                AssertEmpty(creature, slot);
            }

            Assert.False(CreatureDamageStats.HasWeapon(creature));
        }

        // without any item store nothing resolves either
        CreatureTemplate armed = Template(GuardEntry) with { EquipmentTemplateId = EquipmentId };
        Creature noStore = MakeCreature(armed, ArmedContent(armed, new CreatureEquipment(EquipmentId, SwordItem, 0, 0)), items: null);
        AssertEmpty(noStore, 0);
    }

    [Fact]
    public void ChangingTheEntry_ClearsTheEquipmentTheOldEntryHeld()
    {
        CreatureTemplate armed = Template(GuardEntry) with { EquipmentTemplateId = EquipmentId };
        CreatureTemplate bare = Template(WolfEntry);
        CreatureContent content = new([armed, bare], [Spawn(1, GuardEntry, 0, 0)], [], [], [], equipment: [new CreatureEquipment(EquipmentId, SwordItem, ShieldItem, BowItem)]);
        Creature creature = MakeCreature(armed, content, Items);
        AssertSlot(creature, 0, Sword);

        creature.ChangeTemplate(bare);
        creature.InitializeFields();

        for (int slot = 0; slot < 3; slot++)
        {
            AssertEmpty(creature, slot);
        }
    }

    [Fact]
    public void AnEventsEquipmentId_ReplacesTheTemplatesOwn_AndOutlivesARespawnUntilItStops()
    {
        CreatureTemplate template = Template(GuardEntry) with { EquipmentTemplateId = EquipmentId };
        CreatureContent content = ArmedContent(template,
            new CreatureEquipment(EquipmentId, SwordItem, 0, 0), new CreatureEquipment(70, BowItem, ShieldItem, 0));
        Creature guard = MakeCreature(template, content, Items);

        guard.SetEventEquipment(70);
        AssertSlot(guard, 0, Bow);
        AssertSlot(guard, 1, Shield);
        AssertEmpty(guard, 2);

        guard.InitializeFields(); // a respawn
        AssertSlot(guard, 0, Bow);

        guard.SetEventEquipment(0);
        AssertSlot(guard, 0, Sword);
        AssertEmpty(guard, 1);
    }

    [Fact]
    public void SetVirtualItem_WritesTheInfoBytesAndTheSheathWord_AndRefusesASlotPastTheRanged()
    {
        Creature creature = MakeCreature(Template(), Content([Template()], [Spawn(1, WolfEntry, 0, 0)]));

        creature.SetVirtualItem(2, Bow);

        AssertSlot(creature, 2, Bow);
        Assert.Equal(0x0F020202u, creature.GetUInt32(UpdateFields.UnitVirtualItemInfo + 4)); // inventory type 15, material 2, subclass 2, class 2
        Assert.Throws<ArgumentOutOfRangeException>(() => creature.SetVirtualItem(3, Bow));
        Assert.Throws<ArgumentOutOfRangeException>(() => creature.ClearVirtualItem(-1));
        creature.SetVirtualItem(2, null);
        AssertEmpty(creature, 2);
    }

    [Fact]
    public void ASpawnedCreature_HoldsItsItems_ComesBackWithThemAfterTheRespawn_AndAnUnknownItemWarnsOnce()
    {
        CreatureTemplate template = Template(GuardEntry) with { EquipmentTemplateId = EquipmentId };
        CreatureTemplate broken = Template(WolfEntry) with { EquipmentTemplateId = 90 };
        CreatureContent content = new(
            [template, broken], [Spawn(1, GuardEntry, 0, 0), Spawn(2, WolfEntry, 4, 0), Spawn(3, WolfEntry, 8, 0)], [], [], [],
            equipment: [new CreatureEquipment(EquipmentId, SwordItem, ShieldItem, BowItem), new CreatureEquipment(90, 31337, 0, 0)]);
        var logger = new WarningLogger();
        var world = TestWorld.CreateRuntime();
        var map = world.GetMap(0);
        var system = new CreatureMapSystem(map, content, random: new Random(1), logger: logger, aiServices: new CreatureAiServices { ItemTemplateOf = Items });
        map.AddUpdater(system);
        using (world)
        {
            AddPlayer(world, 1, 0, 10);
            Creature guard = system.Creatures.Single(c => c.Entry == GuardEntry);
            AssertSlot(guard, 0, Sword);
            AssertSlot(guard, 1, Shield);
            AssertSlot(guard, 2, Bow);
            Assert.All(system.Creatures.Where(c => c.Entry == WolfEntry), wolf => AssertEmpty(wolf, 0));
            Assert.Equal(1, logger.Warnings.Count(w => w.Contains("31337", StringComparison.Ordinal))); // two wolves, one warning

            system.KillCreature(guard);
            for (int i = 0; i < 4000 && !(guard.IsAlive && guard.IsInWorld); i++)
            {
                Run(world, 100);
            }

            Assert.True(guard.IsAlive && guard.IsInWorld, "the guard did not respawn");
            AssertSlot(guard, 0, Sword);
            AssertSlot(guard, 1, Shield);
            AssertSlot(guard, 2, Bow);
        }
    }

    private sealed class WarningLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
