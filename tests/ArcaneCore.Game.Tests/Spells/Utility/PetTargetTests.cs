using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.Game.Tests.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Utility;

/// <summary>
/// The implicit targets the warlock spells use: 5 TARGET_UNIT_CASTER_PET (Health Funnel), 27 TARGET_UNIT_CASTER_MASTER (Sacrifice)
/// and 32 TARGET_LOCATION_UNIT_MINION_POSITION (every Summon Pet spell). vmangos Spell.cpp:2212-2222, :2770-2772, :2975-3022,
/// :5545-5575 (the pet presence check).
/// </summary>
public sealed class PetTargetTests
{
    private const uint PetTargetSpell = 961_001;
    private const uint MasterTargetSpell = 961_002;
    private const uint MinionPositionSpell = 961_003;
    private const uint MinionPositionRadiusSpell = 961_004;

    private static PetTestKit Kit() => new(
    [
        Spell(PetTargetSpell, Effect(SpellEffectName.Dummy, 1, (SpellImplicitTarget)5)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        Spell(MasterTargetSpell, Effect(SpellEffectName.Dummy, 1, (SpellImplicitTarget)27)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        Spell(MinionPositionSpell, Effect(SpellEffectName.Dummy, 1, (SpellImplicitTarget)32)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        Spell(MinionPositionRadiusSpell, Effect(SpellEffectName.Dummy, 1, (SpellImplicitTarget)32) with { Radius = 4f })
            with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
    ]);

    private static TargetRecorder Install(PetTestKit kit)
    {
        PetTargets.Install(kit.Spells.System);
        var recorder = new TargetRecorder();
        kit.Spells.System.RegisterObserver(recorder);
        return recorder;
    }

    private sealed class TargetRecorder : ISpellCastObserver
    {
        public List<(uint Spell, Unit Target)> Outcomes { get; } = [];

        public List<(float X, float Y, float Z)> Destinations { get; } = [];

        public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
        {
            if (cast.Spell.Id is not (PetTargetSpell or MasterTargetSpell or MinionPositionSpell or MinionPositionRadiusSpell))
            {
                return; // the summon spell that creates the test pet is not under test
            }

            Outcomes.Add((cast.Spell.Id, outcome.Target));
            if (cast.Targets.HasDest)
            {
                Destinations.Add(cast.Targets.Dest);
            }
        }
    }

    private static Creature SummonPet(PetTestKit kit, Player caster)
    {
        kit.Cast(caster, PetSpell);
        return Assert.Single(kit.Creatures.Creatures);
    }

    [Fact]
    public void Target5_ReachesThePetOfTheCaster()
    {
        using PetTestKit kit = Kit();
        TargetRecorder recorder = Install(kit);
        (Player caster, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, caster);
        kit.Spells.Spellbook.Teach(caster, PetTargetSpell);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.HandleCastRequest(caster, PetTargetSpell, SpellCastTargets.ForSelf()));

        (uint _, Unit target) = Assert.Single(recorder.Outcomes);
        Assert.Same(pet, target);
        Assert.NotSame(caster, target);
    }

    [Fact]
    public void Target5_WithoutAPet_FailsNoPetForADirectCast_AndDontReportWhenTriggered()
    {
        using PetTestKit kit = Kit();
        TargetRecorder recorder = Install(kit);
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        kit.Spells.Spellbook.Teach(caster, PetTargetSpell);

        Assert.Equal(SpellCastResult.NoPet, kit.Spells.System.HandleCastRequest(caster, PetTargetSpell, SpellCastTargets.ForSelf()));
        Assert.Equal(SpellCastResult.DontReport, kit.Spells.System.CastSpell(caster, PetTargetSpell, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Empty(recorder.Outcomes);
        Assert.True(kit.Spells.System.IsSpellReady(caster, kit.Spells.Store.Get(PetTargetSpell)!));
        Assert.NotEmpty(session.Sent); // the NO_PET result reached the client
    }

    [Fact]
    public void Target5_WithADeadPet_FailsTargetsDead()
    {
        using PetTestKit kit = Kit();
        Install(kit);
        (Player caster, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, caster);
        kit.Spells.Spellbook.Teach(caster, PetTargetSpell);
        pet.Health = 0;

        Assert.Equal(SpellCastResult.TargetsDead, kit.Spells.System.HandleCastRequest(caster, PetTargetSpell, SpellCastTargets.ForSelf()));
        Assert.Equal(SpellCastResult.DontReport, kit.Spells.System.CastSpell(caster, PetTargetSpell, SpellCastTargets.ForSelf(), triggered: true));
    }

    [Fact]
    public void Target5_WithoutAPetButWithACharm_ReachesTheCharm()
    {
        using PetTestKit kit = Kit();
        TargetRecorder recorder = Install(kit);
        (Player caster, _) = kit.AddPlayer(1);
        kit.Cast(caster, WildSpell);
        Creature charm = Assert.Single(kit.Creatures.Creatures);
        Assert.True(caster.PetGuid.IsEmpty);
        caster.SetUInt64(UpdateFields.UnitFieldCharm, charm.Guid.Value); // vmangos Unit::GetCharm (UNIT_FIELD_CHARM)

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(caster, PetTargetSpell, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Same(charm, Assert.Single(recorder.Outcomes).Target);
    }

    [Fact]
    public void Target27_ReachesTheOwnerOfThePetCaster()
    {
        using PetTestKit kit = Kit();
        TargetRecorder recorder = Install(kit);
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, owner);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(pet, MasterTargetSpell, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Same(owner, Assert.Single(recorder.Outcomes).Target);
    }

    [Fact]
    public void Target27_WithoutAnOwner_HitsNothing()
    {
        using PetTestKit kit = Kit();
        TargetRecorder recorder = Install(kit);
        (Player caster, _) = kit.AddPlayer(1);

        kit.Spells.System.CastSpell(caster, MasterTargetSpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Empty(recorder.Outcomes);
    }

    [Fact]
    public void Target32_IsTheFrontLeftPointAtTheEffectRadius_AndTheCasterWhenTheEffectHasNoRadius()
    {
        using PetTestKit kit = Kit();
        TargetRecorder recorder = Install(kit);
        (Player caster, _) = kit.AddPlayer(1, 10, 20);
        caster.Orientation = 1.0f;

        kit.Spells.System.CastSpell(caster, MinionPositionRadiusSpell, SpellCastTargets.ForSelf(), triggered: true);
        kit.Spells.System.CastSpell(caster, MinionPositionSpell, SpellCastTargets.ForSelf(), triggered: true);

        float angle = 1.0f + (MathF.PI * 0.25f);
        Assert.Equal(2, recorder.Destinations.Count);
        Assert.Equal(10 + (4 * MathF.Cos(angle)), recorder.Destinations[0].X, 3);
        Assert.Equal(20 + (4 * MathF.Sin(angle)), recorder.Destinations[0].Y, 3);
        Assert.Equal((10f, 20f), (recorder.Destinations[1].X, recorder.Destinations[1].Y)); // radius index 0 = 0 yards
        Assert.All(recorder.Outcomes, o => Assert.Same(caster, o.Target)); // a location target: the caster carries the effect
    }

    [Fact]
    public void InstallingTwice_FailsClosed()
    {
        using PetTestKit kit = Kit();
        Install(kit);

        Assert.Throws<InvalidOperationException>(() => PetTargets.Install(kit.Spells.System));
    }

    [Fact]
    public void WithoutTheInstall_TheTargetsAreUnsupported()
    {
        using PetTestKit kit = Kit();
        var recorder = new TargetRecorder();
        kit.Spells.System.RegisterObserver(recorder);
        (Player caster, _) = kit.AddPlayer(1);
        SummonPet(kit, caster);

        kit.Spells.System.CastSpell(caster, PetTargetSpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Empty(recorder.Outcomes); // the base behaviour this slice removes: no selector, nothing is hit
    }
}
