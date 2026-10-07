using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>
/// The power half of a cast for triggered casts and creatures.
/// vmangos Spell::CheckPower (Spell.cpp:7029-7066): a triggered cast passes at once, and a non-pet creature passes a spell
/// of a power it cannot have (any non-mana power, or mana without create mana).
/// vmangos Spell::TakePower (Spell.cpp:5051-5080): a cast triggered by an aura takes nothing; any other paid mana cast,
/// triggered or not, starts the five second rule.
/// </summary>
public sealed class TriggeredPowerTests
{
    private const uint ManaBolt = 2101;
    private const uint RageStrike = 2102;
    private const uint ManaTickAura = 2103;

    private static SpellTestKit NewKit() => new(
        SpellTestKit.Spell(ManaBolt, SpellTestKit.Effect(SpellEffectName.Heal, 5, SpellImplicitTarget.Unit)) with
        {
            PowerType = (int)PowerType.Mana,
            ManaCost = 100,
            School = SpellSchool.Frost,
        },
        SpellTestKit.Spell(RageStrike, SpellTestKit.Effect(SpellEffectName.Heal, 5, SpellImplicitTarget.Unit)) with
        {
            PowerType = (int)PowerType.Rage,
            ManaCost = 100,
        },
        // A periodic trigger aura on the holder that casts the paid ManaBolt every second.
        SpellTestKit.Spell(ManaTickAura, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster,
            AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: ManaBolt)) with
        {
            Duration = new SpellDuration(3000, 0, 3000),
            SpellVisual = 1,
        });

    private static Player CasterWithMana(SpellTestKit kit, uint mana)
    {
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        player.SetUInt32(UpdateFields.UnitFieldPower1, mana);
        return player;
    }

    private static Creature AddCreature(Player near, uint mana)
    {
        var template = new CreatureTemplate { Entry = 921000, Name = "Power test", MinLevel = 10, MaxLevel = 10,
            MinLevelHealth = 500, MaxLevelHealth = 500, MinLevelMana = mana, MaxLevelMana = mana, DisplayIds = [1], Faction = 35 };
        var creature = new Creature(921000, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        creature.Relocate(near.X + 2, near.Y, near.Z, 0, 0);
        creature.MapId = near.MapId;
        near.Map!.AddObject(creature);
        return creature;
    }

    [Fact]
    public void TriggeredCast_PassesThePowerCheck_WithoutTheMana()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit, mana: 10);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, ManaBolt, SpellCastTargets.ForSelf(), triggered: true));
    }

    [Fact]
    public void NonTriggeredCast_StillNeedsTheMana()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit, mana: 10);

        Assert.Equal(SpellCastResult.NoPower, kit.System.CastSpell(player, ManaBolt, SpellCastTargets.ForSelf(), triggered: false));
    }

    [Fact]
    public void Creature_WithoutCreateMana_CastsAManaSpell()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit, mana: 0);
        Creature creature = AddCreature(player, mana: 0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(creature, ManaBolt, SpellCastTargets.ForSelf(), triggered: false));
    }

    [Fact]
    public void Creature_CastsASpellOfAPowerItCannotHave()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit, mana: 0);
        Creature creature = AddCreature(player, mana: 300);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(creature, RageStrike, SpellCastTargets.ForSelf(), triggered: false));
    }

    [Fact]
    public void Creature_WithCreateMana_StillNeedsTheMana()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit, mana: 0);
        Creature creature = AddCreature(player, mana: 300);
        SpellSystem.SetPower(creature, PowerType.Mana, 10);

        Assert.Equal(SpellCastResult.NoPower, kit.System.CastSpell(creature, ManaBolt, SpellCastTargets.ForSelf(), triggered: false));
    }

    [Fact]
    public void AuraTriggeredCast_TakesNoMana_AndDoesNotStartTheFiveSecondRule()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit, mana: 500);
        int bolts = 0;
        kit.System.SpellHit += (_, _, s) =>
        {
            if (s.Id == ManaBolt)
            {
                bolts++;
            }
        };

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, ManaTickAura, SpellCastTargets.ForSelf(), triggered: true));
        kit.Advance(3000);

        Assert.True(bolts >= 2, $"the trigger aura must have fired (fired {bolts})");
        Assert.Equal(500u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.Equal(0u, player.Combat.LastManaUseTimer);
    }

    [Fact]
    public void NonAuraTriggeredCast_PaysTheMana_AndStartsTheFiveSecondRule()
    {
        using SpellTestKit kit = NewKit();
        Player player = CasterWithMana(kit, mana: 500);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, ManaBolt, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(400u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.Equal(CombatConstants.ManaRegenInterruptMs, player.Combat.LastManaUseTimer);
        Assert.Equal(ManaBolt, player.Combat.LastManaUseSpellId);
    }
}
