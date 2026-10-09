using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Uldaman;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Spells;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>Archaedas after a wipe, and the dwarves his Awaken spells reach.</summary>
public sealed class UldamanEncounterTests
{
    private static void Ticks(DungeonScriptHarness run, uint ms)
    {
        for (uint done = 0; done < ms; done += 50)
        {
            run.Tick(50);
        }
    }

    [Fact]
    public void Archaedas_AfterAWipe_IsFrozenAndUnselectableAgain_AndTheNextAltarUseReplaysTheAwakening()
    {
        using DungeonScriptHarness run = new(map => new UldamanInstance(map), [UldamanInstance.Archaedas], [UldamanInstance.Archaedas]);
        var data = Assert.IsType<UldamanInstance>(run.Data);
        Creature archaedas = run.Creature(UldamanInstance.Archaedas);
        Assert.NotEqual(UnitFlags.None, archaedas.UnitFlags & UnitFlags.NotSelectable);
        data.StartEvent(UldamanInstance.AltarArchaedasEvent, run.Player);
        Ticks(run, 6_100);
        Assert.Equal(EncounterState.InProgress, data.GetData(UldamanInstance.TypeArchaedas));
        Assert.Equal(UnitFlags.None, archaedas.UnitFlags & UnitFlags.NotSelectable);

        run.Creatures.EnterEvadeMode(archaedas);
        Assert.NotEqual(UnitFlags.None, archaedas.UnitFlags & UnitFlags.NotSelectable); // Reset at the evade
        Ticks(run, 1_000);
        Assert.Equal(EncounterState.Fail, data.GetData(UldamanInstance.TypeArchaedas)); // JustReachedHome

        data.StartEvent(UldamanInstance.AltarArchaedasEvent, run.Player);
        Assert.Equal(EncounterState.Special, data.GetData(UldamanInstance.TypeArchaedas));
        Ticks(run, 6_100);
        Assert.Equal(EncounterState.InProgress, data.GetData(UldamanInstance.TypeArchaedas));
        Assert.Same(run.Player, archaedas.Combat.Victim);
    }

    [Fact]
    public void Archaedas_WakesTheGuardiansAndTwoVaultWardersWithin100Yards()
    {
        // z2815 spell_template 10252 (APPLY_AURA dummy) / 10258 (DUMMY): targets 22 / 7 (script AoE at the caster), radius index 12 (100 yd);
        // 10258 has MaxAffectedTargets 2; spell_script_target (10252,1,7076,0), (10258,1,10120,0).
        using var spells = new SpellTestKit(
            Spell(ArchaedasAi.AwakenGuardians, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.LocationCasterSrc, AuraType.Dummy,
                targetB: SpellImplicitTarget.EnumUnitsScriptAoeAtSrcLoc) with { Radius = 100 })
                with { StartRecoveryCategory = 0, StartRecoveryTime = 0, Duration = new SpellDuration(10_000, 0, 10_000) },
            Spell(ArchaedasAi.AwakenWarders, Effect(SpellEffectName.Dummy, 1, SpellImplicitTarget.LocationCasterSrc,
                targetB: SpellImplicitTarget.EnumUnitsScriptAoeAtSrcLoc) with { Radius = 100 })
                with { StartRecoveryCategory = 0, StartRecoveryTime = 0, MaxAffectedTargets = 2 });
        spells.System.Store = new SpellStore(spells.Store.All, [], [],
            [new SpellStore.ScriptTarget(ArchaedasAi.AwakenGuardians, 1, UldamanInstance.Guardian, 0),
             new SpellStore.ScriptTarget(ArchaedasAi.AwakenWarders, 1, UldamanInstance.VaultWarder, 0)]);
        SpellScriptDispatcher.Install(spells.System, SpellScriptRegistry.Discover(typeof(ArchaedasAwakenSpell).Assembly));
        uint[] spawns =
        [
            UldamanInstance.Archaedas, UldamanInstance.Guardian, UldamanInstance.Guardian,
            UldamanInstance.VaultWarder, UldamanInstance.VaultWarder, UldamanInstance.VaultWarder, UldamanInstance.VaultWarder,
        ];
        using DungeonScriptHarness run = new(map => new UldamanInstance(map),
            [UldamanInstance.Archaedas, UldamanInstance.Guardian, UldamanInstance.VaultWarder], spawns, null,
            new CreatureAiServices { Spells = new SpellSystemCreatureCaster(spells.System) });
        Creature archaedas = run.Creature(UldamanInstance.Archaedas);
        Creature[] guardians = [.. run.Creatures.Creatures.Where(c => c.Template.Entry == UldamanInstance.Guardian)];
        Creature[] warders = [.. run.Creatures.Creatures.Where(c => c.Template.Entry == UldamanInstance.VaultWarder)];
        Creature far = warders[^1];
        run.Creatures.NearTeleport(far, archaedas.X + 150f, archaedas.Y, archaedas.Z, 0);
        archaedas.UnitFlags &= ~UnitFlags.NotSelectable;
        Assert.True(run.Creatures.AttackStart(archaedas, run.Player));
        Assert.All(warders, w => Assert.Null(w.Combat.Victim));

        archaedas.Health = archaedas.MaxHealth * 3 / 10;
        Ticks(run, 100); // 30 % is below both 66.6 % and 33.2 %: guardians, then warders

        Assert.All(guardians, g => Assert.Same(run.Player, g.Combat.Victim));
        Assert.Equal(2, warders.Count(w => w.Combat.Victim is not null));
        Assert.Null(far.Combat.Victim);
    }
}
