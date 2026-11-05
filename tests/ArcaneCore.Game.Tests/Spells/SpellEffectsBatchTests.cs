using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Pick Pocket failing breaks stealth (vmangos Spell.cpp:1225-1244), a LEARN_SPELL aimed at the caster's pet teaches the pet
/// (vmangos SpellEffects.cpp:2440-2444, the Warlock Grimoires) and the spell_scripts table starts from SCRIPT_EFFECT only
/// (vmangos SpellEffects.cpp:4565-4571).
/// </summary>
public sealed class SpellEffectsBatchTests
{
    private const uint Stealth = 930_001;
    private const uint PickPocket = 930_002;
    private const uint GrimoireOfSacrifice = 20270; // the id is vmangos' Firebolt (LEARN_SPELL 36 -> 7799); the name is kept for the call sites
    private const uint FirebluntPetSpell = 7799; // Firebolt (Imp) rank taught by the learn effect
    private const uint BeastEntry = 5251;

    private static SpellInfo StealthSpell() => Spell(Stealth, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModStealth)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo PickPocketSpell() => Spell(PickPocket, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
    {
        AttributesEx = SpellAttributesEx.FailureBreaksStealth,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    [Theory]
    [InlineData(SpellMissInfo.Miss)]
    [InlineData(SpellMissInfo.Resist)]
    public void MissedPickPocket_BreaksStealth_AndLeavesTheRogueInCombat(SpellMissInfo miss)
    {
        using SpellTestKit kit = new(StealthSpell(), PickPocketSpell());
        kit.System.CombatRules = new FixedRules { Miss = miss };
        kit.System.Damage = new MapCombatDamageSink(); // the default sink only moves health; the combat start under test is the real one's
        (Player rogue, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddHostilePlayer(2, 2);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(rogue, Stealth, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(rogue, Stealth));
        Assert.False(rogue.Combat.IsInCombat);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(rogue, PickPocket, SpellCastTargets.ForUnit(victim.Guid), triggered: true));

        Assert.False(kit.System.HasAura(rogue, Stealth));
        Assert.True(rogue.Combat.IsInCombat);
    }

    [Fact]
    public void SucceededPickPocket_KeepsStealth()
    {
        using SpellTestKit kit = new(StealthSpell(), PickPocketSpell());
        kit.System.CombatRules = new FixedRules { Miss = SpellMissInfo.None };
        (Player rogue, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddHostilePlayer(2, 2);
        kit.System.CastSpell(rogue, Stealth, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.CastSpell(rogue, PickPocket, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        Assert.True(kit.System.HasAura(rogue, Stealth));
    }

    private static PetTestKit GrimoireKit(uint grimoireLevel = 0)
    {
        PetTestKit kit = new(
            extraSpells:
            [
                Spell(GrimoireOfSacrifice, Effect(SpellEffectName.LearnSpell, 0, SpellImplicitTarget.UnitCasterPet, trigger: FirebluntPetSpell))
                    with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellLevel = grimoireLevel },
                Spell(FirebluntPetSpell, Effect(SpellEffectName.Dummy, 1)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            ],
            extraTemplates:
            [
                CreatureTestKit.Template(BeastEntry, b =>
                {
                    b.MinLevel = 20;
                    b.MaxLevel = 20;
                    b.MinLevelHealth = 100;
                    b.MaxLevelHealth = 100;
                    b.Faction = 14;
                }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
            ]);
        // The world's PetTargetFeature installs the TARGET_UNIT_CASTER_PET selector; the kit does not (see CharmPossessTests).
        PetTargets.Install(kit.Spells.System);
        return kit;
    }

    private static (Player Owner, FakeSession Session, CharmInfo Charm) TamePet(PetTestKit kit)
    {
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        owner.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        Creature pet = Assert.IsType<Creature>(kit.Service.TameCreature(owner, beast, 1515));
        CharmInfo charm = pet.Summon!.Charm!;
        Assert.False(charm.HasSpell(FirebluntPetSpell));
        session.Clear();
        return (owner, session, charm);
    }

    // The pet is a tamed beast: SummonService.TameCreature only takes tameable beasts and the learn path has no creature-type rule of its own.
    [Fact]
    public void GrimoireLearnSpellAimedAtThePet_TeachesThePet_AndSendsPetSpells()
    {
        using PetTestKit kit = GrimoireKit();
        (Player owner, FakeSession session, CharmInfo charm) = TamePet(kit);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, GrimoireOfSacrifice));

        Assert.True(charm.HasSpell(FirebluntPetSpell));
        Assert.False(kit.Spells.Spellbook.HasSpell(owner, FirebluntPetSpell)); // the pet learned it, not the caster
        Assert.NotEmpty(Packets(session, WorldOpcode.SmsgPetSpells));
    }

    [Fact]
    public void GrimoireWithAPetBelowTheSpellLevel_IsRefusedByTheLearnPetSpellChecks()
    {
        using PetTestKit kit = GrimoireKit(grimoireLevel: 60);
        (Player owner, _, CharmInfo charm) = TamePet(kit);

        Assert.Equal(SpellCastResult.Lowlevel, kit.Cast(owner, GrimoireOfSacrifice));

        Assert.False(charm.HasSpell(FirebluntPetSpell));
    }

    [Fact]
    public void GrimoireWithoutAPet_IsRefusedWithNoPet()
    {
        using PetTestKit kit = GrimoireKit();
        (Player owner, _) = kit.AddPlayer(1);

        // A triggered cast with no pet is silent (DontReport, CasterPetCastCheck); the player's own cast is refused with NoPet.
        Assert.Equal(SpellCastResult.NoPet, kit.Spells.System.CastSpell(owner, GrimoireOfSacrifice, SpellCastTargets.ForSelf(), triggered: false));
    }

    [Fact]
    public void LearnSpellAimedAtThePlayer_StillTeachesThePlayer()
    {
        using SpellTestKit kit = new(
            Spell(930_010, Effect(SpellEffectName.LearnSpell, 0, SpellImplicitTarget.UnitCaster, trigger: 930_011)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(930_011, Effect(SpellEffectName.Dummy, 1)));
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, 930_010, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(kit.Spellbook.HasSpell(player, 930_011));
    }

    [Theory]
    [InlineData(SpellEffectName.ScriptEffect, true)]
    [InlineData(SpellEffectName.Dummy, true)]
    [InlineData(SpellEffectName.TriggerSpell, false)]
    [InlineData(SpellEffectName.SchoolDamage, false)]
    public void ScriptEffectAndDummy_StartTheSpellScripts_OtherEffectsDoNot(SpellEffectName effect, bool starts)
    {
        SpellInfo spell = Spell(930_020, Effect(effect, 0));

        Assert.Equal(starts, SpellSystem.CanEffectStartDbScript(spell, 0));
    }

    [Fact]
    public void ADummyEffectNextToAScriptEffect_StartsNothingItself()
    {
        SpellInfo spell = Spell(930_021, Effect(SpellEffectName.Dummy, 0), Effect(SpellEffectName.ScriptEffect, 0));

        Assert.False(SpellSystem.CanEffectStartDbScript(spell, 0));
        Assert.True(SpellSystem.CanEffectStartDbScript(spell, 1));
    }

    [Fact]
    public void EveryScriptEffectEffect_StartsTheSpellScripts()
    {
        // vmangos runs the trailing DB-script block once per SCRIPT_EFFECT effect; the old cmangos priority rule only let the first one start it.
        SpellInfo spell = Spell(930_023, Effect(SpellEffectName.ScriptEffect, 0), Effect(SpellEffectName.ScriptEffect, 0), Effect(SpellEffectName.ScriptEffect, 0));

        Assert.True(SpellSystem.CanEffectStartDbScript(spell, 0));
        Assert.True(SpellSystem.CanEffectStartDbScript(spell, 1));
        Assert.True(SpellSystem.CanEffectStartDbScript(spell, 2));
    }

    [Fact]
    public void OnlyTheFirstDummyEffect_StartsTheSpellScripts()
    {
        SpellInfo spell = Spell(930_022, Effect(SpellEffectName.SchoolDamage, 0), Effect(SpellEffectName.Dummy, 0), Effect(SpellEffectName.Dummy, 0));

        Assert.False(SpellSystem.CanEffectStartDbScript(spell, 0));
        Assert.True(SpellSystem.CanEffectStartDbScript(spell, 1));
        Assert.False(SpellSystem.CanEffectStartDbScript(spell, 2));
    }
}
