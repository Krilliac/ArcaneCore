using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackrockSpire;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.Scholomance;
using ArcaneCore.Game.Instances.Scripts.Uldaman;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The engine hooks behind the open findings of the 2026-10-08 script fidelity review (docs/integration/script-fidelity-20261008.md on
/// claude/script-fidelity): a script's own evade (WailingCaverns#3), the instance creature-despawn callback (BlackrockSpire#6), the holiday
/// query (Scholomance#3) and map variables with a creature-group despawn hook and variable-gated spawns (Uldaman#1).
/// </summary>
public sealed class EngineHookScriptTests
{
    private const uint NaralexPathKey = CreatureContent.WaypointPathBit | DiscipleOfNaralexAi.PathId;

    private static (uint Entry, uint PathId, CreatureWaypoint Point) PathPoint(uint point, float x)
        => (CreatureContent.WaypointPathEntry, NaralexPathKey, new CreatureWaypoint(point, x, -383.07f, 61.78f, 100, 0));

    /// <summary>
    /// WailingCaverns#3: npc_disciple_of_naralexAI::EnterEvadeMode (mangos-classic wailing_cavernsScripts.cpp:174-191). At the circle stop the
    /// disciple leaves combat where it stands and keeps channelling Serpentine Cleansing (6270): no interrupt, no aura reset, no run back to
    /// the combat start, not in evade mode, and the ritual goes on.
    /// </summary>
    [Fact]
    public void Disciple_EvadingAtTheCircle_KeepsChannellingAndStaysPut()
    {
        var spells = new CountingCreatureSpells();
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map), [3678], [3678], [],
            entryWaypoints: [PathPoint(30, -13.4f), PathPoint(31, -3.4f)],
            aiServices: new CreatureAiServices { Hostility = new AlwaysHostile(), Spells = spells });
        foreach (uint type in new uint[] { 0, 1, 2, 3 })
        {
            run.Script.SetData(type, EncounterState.Done);
        }

        Creature disciple = run.Creature(3678);
        var escort = Assert.IsType<DiscipleOfNaralexAi>(disciple.AI);
        Assert.True(escort.Start(waypointPath: DiscipleOfNaralexAi.PathId));
        for (int i = 0; i < 100 && !spells.Casts.Contains(6270); i++)
        {
            run.Tick(100);
        }

        Assert.True(spells.Casts.Contains(6270), $"casts [{string.Join(",", spells.Casts)}] phase {escort.EventPhase} at ({disciple.X},{disciple.Y}) motion {disciple.Motion.CurrentType} escorting {escort.HasEscortState(EscortAI.EscortState.Escorting)} combat {disciple.Combat.IsInCombat} paused {escort.HasEscortState(EscortAI.EscortState.Paused)} idx {escort.CurrentWaypointIndex}/{escort.WaypointCount}");
        Assert.Equal(1, escort.EventPhase);
        Assert.True(disciple.AI!.AttackStart(run.Player));
        run.Tick(50);
        Assert.True(disciple.Combat.IsInCombat);
        (float x, float y) = (disciple.X, disciple.Y);
        int interruptsBefore = spells.Interrupts;

        run.Creatures.EnterEvadeMode(disciple);

        Assert.Equal(interruptsBefore, spells.Interrupts);   // the cleansing channel is not broken
        Assert.Equal(0, spells.AuraResets);                   // nor are its auras reset
        Assert.False(disciple.IsEvading);
        Assert.False(disciple.Combat.IsInCombat);
        Assert.Null(disciple.Combat.Victim);
        Assert.NotEqual(MovementGeneratorType.Chase, disciple.Motion.CurrentType);
        Assert.NotEqual(MovementGeneratorType.Point, disciple.Motion.CurrentType); // not running back to the combat start
        run.Tick(50);
        Assert.Equal(x, disciple.X);
        Assert.Equal(y, disciple.Y);
        Assert.True(escort.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(1, escort.EventPhase);
    }

    /// <summary>Away from the circle the disciple's evade is the escort's: the engine evade interrupts the cast as before.</summary>
    [Fact]
    public void Disciple_EvadingBeforeTheCircle_TakesTheEngineEvade()
    {
        var spells = new CountingCreatureSpells();
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map), [3678], [3678], [],
            aiServices: new CreatureAiServices { Hostility = new AlwaysHostile(), Spells = spells });
        Creature disciple = run.Creature(3678);
        Assert.True(disciple.AI!.AttackStart(run.Player));
        run.Tick(50);
        int interruptsBefore = spells.Interrupts;

        run.Creatures.EnterEvadeMode(disciple);

        Assert.Equal(interruptsBefore + 1, spells.Interrupts);
        Assert.False(disciple.Combat.IsInCombat);
    }

    /// <summary>A script evade that hands back to the engine from inside the hook gets the engine's evade, not a recursion.</summary>
    [Fact]
    public void ScriptEvade_CallingEnterEvadeModeFromTheHook_RunsTheEngineEvadeOnce()
    {
        var spells = new CountingCreatureSpells();
        using var run = new DungeonScriptTestKit(map => new DespawnRecorder(map), [10000], [10000], [],
            aiServices: new CreatureAiServices { Hostility = new AlwaysHostile(), Spells = spells });
        run.Creatures.RegisterEntryAi(10000, c => new NestedEvadeAi(c), rebuildExisting: true);
        Creature disciple = run.Creature(10000);
        var ai = Assert.IsType<NestedEvadeAi>(disciple.AI);
        Assert.True(ai.AttackStart(run.Player));
        run.Tick(50);
        int evades = 0;
        run.Creatures.Evaded += _ => evades++;

        run.Creatures.EnterEvadeMode(disciple);

        Assert.Equal(1, ai.HookCalls);
        Assert.Equal(1, evades);
        Assert.True(disciple.IsEvading);
    }

    /// <summary>
    /// BlackrockSpire#6: instance_blackrock_spire::OnCreatureDespawn (instance_blackrock_spire.cpp:488-492). The Beast casts Finkle is
    /// Einhorn (16710) when its corpse is removed - not at death.
    /// </summary>
    [Fact]
    public void TheBeast_CorpseRemoval_CastsFinkleIsEinhorn()
    {
        var spells = new CountingCreatureSpells();
        using var run = new DungeonScriptTestKit(map => new BlackrockSpireInstance(map), [BlackrockSpireInstance.NpcBeast],
            [BlackrockSpireInstance.NpcBeast], [], aiServices: new CreatureAiServices { Spells = spells });
        Creature beast = run.Creature(BlackrockSpireInstance.NpcBeast);

        run.Kill(BlackrockSpireInstance.NpcBeast);
        run.Tick(50);
        Assert.False(beast.IsAlive);
        Assert.DoesNotContain(BlackrockSpireInstance.SpellFinkleIsEinhorn, spells.Casts);

        run.Creatures.ForcedDespawn(beast, 0);

        Assert.Equal([BlackrockSpireInstance.SpellFinkleIsEinhorn], spells.Casts.Where(s => s == BlackrockSpireInstance.SpellFinkleIsEinhorn));
        Assert.Same(beast, spells.CastersOf(BlackrockSpireInstance.SpellFinkleIsEinhorn).Single());
    }

    /// <summary>The despawn hook hears of every corpse removal of the map, other creatures included (they do nothing at Blackrock Spire).</summary>
    [Fact]
    public void InstanceData_OnCreatureDespawn_RunsAtCorpseRemovalOnly()
    {
        DespawnRecorder? recorder = null;
        using var run = new DungeonScriptTestKit(map => recorder = new DespawnRecorder(map), [10000], [10000], []);
        Creature creature = run.Creature(10000);

        run.Kill(10000);
        Assert.Empty(recorder!.Despawned);
        run.Creatures.ForcedDespawn(creature, 0);

        Assert.Same(creature, Assert.Single(recorder.Despawned));
    }

    /// <summary>Scholomance#3: instance_scholomance::DoSpawnGandlingIfCan casts SPELL_XMAS_GANDLING (26199) while Winter Veil (holiday 141) runs.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Gandling_SummonedDuringWinterVeil_CastsTheChristmasModel(bool winterVeil)
    {
        var spells = new CountingCreatureSpells();
        using var run = new DungeonScriptTestKit(map => new ScholomanceInstance(map), [ScholomanceInstance.NpcGandling], [], [],
            aiServices: new CreatureAiServices { Spells = spells },
            holidayActive: holiday => winterVeil && holiday == ScholomanceInstance.HolidayFeastOfWinterVeil);

        for (uint type = ScholomanceInstance.TypeMalicia; type <= ScholomanceInstance.TypeIlluciaBarov; type++)
        {
            run.Script.SetData(type, EncounterState.Done);
        }

        Creature gandling = run.Creature(ScholomanceInstance.NpcGandling);
        Assert.True(gandling.IsAlive);
        Assert.Equal(winterVeil, spells.Casts.Contains(ScholomanceInstance.SpellXmasGandling));
    }

    /// <summary>
    /// Uldaman#1: instance_uldaman Initialize sets WORLD_STATE_CUSTOM_SPAWN_ANNORA (700001) to 0 and OnCreatureGroupDespawn sets it to 1 when the
    /// last Cleft Scorpid of the classic-db group dies; Annora's spawn (7000324, spawn group WorldState condition 700001 = 1) appears then.
    /// </summary>
    [Fact]
    public void Uldaman_AnnoraAppearsOnlyAfterTheWholeScorpidGroupDied()
    {
        const uint scorpid = 7078, annora = 11073;
        CreatureSpawn[] spawns =
        [
            .. UldamanInstance.ScorpidSpawnGuids.Select((guid, i) => Spawn(guid, scorpid, -10f + i, -383.07f, 61.78f, mapId: InstanceFixture.Dungeon)),
            Spawn(UldamanInstance.AnnoraSpawnGuid, annora, -12f, -380f, 61.78f, mapId: InstanceFixture.Dungeon),
            Spawn(7000210, scorpid, -12f, -386f, 61.78f, mapId: InstanceFixture.Dungeon), // not of the group
        ];
        using var run = new DungeonScriptTestKit(map => new UldamanInstance(map), [scorpid, annora], [], [], extraSpawns: spawns);
        var uldaman = Assert.IsType<UldamanInstance>(run.Script);
        Assert.Equal(0, uldaman.GetVariable(UldamanInstance.VariableSpawnAnnora));
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Entry == annora);
        Creature[] group = [.. run.Creatures.Creatures.Where(c => c.Spawn is { } s && UldamanInstance.ScorpidSpawnGuids.Contains(s.Guid))];
        Assert.Equal(10, group.Length);

        foreach (Creature member in group.Take(9))
        {
            run.Map.Combat.Kill(run.Player, member);
        }

        run.Map.Combat.Kill(run.Player, run.Creatures.Creatures.Single(c => c.Spawn?.Guid == 7000210));
        run.Tick(50);
        Assert.Equal(0, uldaman.GetVariable(UldamanInstance.VariableSpawnAnnora));
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Entry == annora);

        run.Map.Combat.Kill(run.Player, group[9]);
        run.Tick(50);

        Assert.Equal(1, uldaman.GetVariable(UldamanInstance.VariableSpawnAnnora));
        Creature annoraCreature = Assert.Single(run.Creatures.Creatures, c => c.Entry == annora);
        Assert.True(annoraCreature.IsAlive);
        Assert.Equal(UldamanInstance.AnnoraSpawnGuid, annoraCreature.Spawn!.Guid);
    }

    /// <summary>A variable-gated spawn goes again when the variable no longer matches (cmangos SPAWN_GROUP_DESPAWN_ON_COND_FAIL style refresh).</summary>
    [Fact]
    public void MapVariable_GatedSpawn_FollowsTheVariable()
    {
        const uint annora = 11073;
        using var run = new DungeonScriptTestKit(map => new UldamanInstance(map), [annora], [], [],
            extraSpawns: [Spawn(UldamanInstance.AnnoraSpawnGuid, annora, -12f, -380f, 61.78f, mapId: InstanceFixture.Dungeon)]);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Entry == annora);

        run.Script.SetVariable(UldamanInstance.VariableSpawnAnnora, 1);
        Assert.Single(run.Creatures.Creatures, c => c.Entry == annora);

        run.Script.SetVariable(UldamanInstance.VariableSpawnAnnora, 0);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Entry == annora);
    }

    private sealed class DespawnRecorder(ArcaneCore.Game.Maps.Map map) : ScriptedInstance(map, 1)
    {
        public List<Creature> Despawned { get; } = [];
        public override void OnCreatureDespawn(Creature creature) => Despawned.Add(creature);
    }

    private sealed class NestedEvadeAi(Creature creature) : AggressorAI(creature)
    {
        public int HookCalls { get; private set; }

        public override bool OnEnterEvadeMode()
        {
            HookCalls++;
            EnterEvadeMode(); // the source's Base::EnterEvadeMode()
            return true;
        }
    }
}

internal sealed class CountingCreatureSpells : ICreatureSpellCaster, ICreatureAuraReset
{
    private readonly List<(Creature Caster, uint Spell)> _casts = [];
    public List<uint> Casts => [.. _casts.Select(c => c.Spell)];
    public int Interrupts { get; private set; }
    public int AuraResets { get; private set; }
    public IEnumerable<Creature> CastersOf(uint spellId) => _casts.Where(c => c.Spell == spellId).Select(c => c.Caster);

    public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
    {
        _casts.Add((caster, spellId));
        return CreatureCastResult.Ok;
    }

    public bool IsCasting(Creature caster) => false;
    public bool HasAura(Unit unit, uint spellId) => false;
    public void Interrupt(Creature caster) => Interrupts++;
    public void OnCreatureRemoved(Creature creature) { }
    public void ResetAuras(Creature creature, bool keepPositive) => AuraResets++;
    public event Action<Unit, Unit, SpellInfo>? SpellHit { add { } remove { } }
}
