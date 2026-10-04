using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Skills;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// A spell kit with one player who has a real inventory over a small item set (classic-db 1.12.1 entries: Linen Cloth 2589,
/// Linen Bandage 1251, Copper Bar 2840, Blacksmith Hammer 5956). Spell ids are synthetic.
/// </summary>
internal sealed class CraftingTestKit : IDisposable
{
    public const uint LinenCloth = 2589;
    public const uint LinenBandage = 1251;
    public const uint CopperBar = 2840;
    public const uint BlacksmithHammer = 5956;
    public const uint Filler = 90001;

    private static readonly ItemTemplate[] BaseTemplates =
    [
        new ItemTemplate { Entry = LinenCloth, Class = 7, Name = "Linen Cloth", DisplayId = 1, Stackable = 20, Quality = 1 },
        new ItemTemplate { Entry = LinenBandage, Class = 1, Name = "Linen Bandage", DisplayId = 2, Stackable = 20, Quality = 1 },
        new ItemTemplate { Entry = CopperBar, Class = 7, Name = "Copper Bar", DisplayId = 3, Stackable = 20, Quality = 1 },
        new ItemTemplate { Entry = BlacksmithHammer, Class = 7, Name = "Blacksmith Hammer", DisplayId = 4, Stackable = 1, Quality = 1 },
        new ItemTemplate { Entry = Filler, Class = 7, Name = "Unstackable Filler", DisplayId = 5, Stackable = 1, Quality = 1 },
    ];

    public static readonly ItemTemplateStore Templates = new(BaseTemplates, []);

    public CraftingTestKit(params SpellInfo[] spells)
        : this(spells, [])
    {
    }

    /// <summary>As above, with extra item templates (item-use tests).</summary>
    public CraftingTestKit(SpellInfo[] spells, ItemTemplate[] extraItems, ArcaneCore.Kernel.Accounts.AccountSecurity security = ArcaneCore.Kernel.Accounts.AccountSecurity.Player)
    {
        Kit = new SpellTestKit(spells);
        (Player, Session) = Kit.AddPlayer(1, security: security);
        Player.Inventory.Templates = extraItems.Length == 0 ? Templates : new ItemTemplateStore([.. BaseTemplates, .. extraItems], []);
        Player.Inventory.GuidAllocator = new ItemGuidAllocator();
        Player.Inventory.Load([]);
    }

    /// <summary>The scripted skill roll source (valid after <see cref="AttachSkills"/>).</summary>
    public ScriptedSkillRandom SkillRandom { get; } = new();

    public PlayerSkills? Skills { get; private set; }

    /// <summary>Give the player skills over the fixture catalog (Smelt Copper 2657: Blacksmithing, trivial 25 / 62 / 100).</summary>
    public PlayerSkills AttachSkills()
    {
        Skills = new PlayerSkills(Player, SkillTestKit.Catalog(), new SkillOptions(), new FakeSkillSpellHost { Cascade = Player }, SkillRandom);
        Player.AttachSkills(Skills);
        return Skills;
    }

    public SpellTestKit Kit { get; }

    public SpellSystem System => Kit.System;

    public Player Player { get; }

    public FakeSession Session { get; }

    public PlayerInventory Inventory => Player.Inventory;

    public Item Give(uint entry, uint count = 1)
    {
        InventoryResult result = Inventory.AddItem(entry, count, out Item? item);
        if (result != InventoryResult.Ok)
        {
            throw new InvalidOperationException($"could not give {count} x {entry}: {result}");
        }

        return item!;
    }

    public SpellCastResult Cast(uint spell)
    {
        Session.Clear();
        Kit.Spellbook.Teach(Player, spell);
        return Kit.System.HandleCastRequest(Player, spell, SpellCastTargets.ForSelf());
    }

    public static SpellInfo Craft(uint id, params SpellEffectInfo[] effects) =>
        SpellTestKit.Spell(id, effects.Length == 0 ? [SpellTestKit.Effect(SpellEffectName.Dummy, 0)] : effects) with
        {
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    public void Dispose() => Kit.Dispose();
}
