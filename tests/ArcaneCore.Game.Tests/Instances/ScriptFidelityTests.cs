using System.Reflection;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackrockSpire;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.Deadmines;
using ArcaneCore.Game.Instances.Scripts.Gnomeregan;
using ArcaneCore.Game.Instances.Scripts.RazorfenDowns;
using ArcaneCore.Game.Instances.Scripts.RazorfenKraul;
using ArcaneCore.Game.Instances.Scripts.ScarletMonastery;
using ArcaneCore.Game.Instances.Scripts.Scholomance;
using ArcaneCore.Game.Instances.Scripts.Stratholme;
using ArcaneCore.Game.Instances.Scripts.ZulFarrak;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// A creature spell caster whose casts all succeed and put the spell's aura on the target (the caster when none), so scripts that
/// test auras (HasAura) and take them off (RemoveAuras) can be followed. Interrupts and removals are recorded.
/// </summary>
internal sealed class AuraTrackingCaster : ICreatureSpellCaster
{
    private readonly HashSet<(Unit Unit, uint Spell)> _auras = [];

    public List<(Creature Caster, uint Spell, Unit? Target)> Casts { get; } = [];

    public List<(Unit Unit, uint Spell)> Removed { get; } = [];

    public List<Creature> Interrupted { get; } = [];

    public event Action<Unit, Unit, SpellInfo>? SpellHit { add { } remove { } }

    public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
    {
        Casts.Add((caster, spellId, target));
        _auras.Add((target ?? caster, spellId));
        return CreatureCastResult.Ok;
    }

    public bool IsCasting(Creature caster) => false;

    public bool HasAura(Unit unit, uint spellId) => _auras.Contains((unit, spellId));

    public void RemoveAuras(Unit unit, uint spellId)
    {
        Removed.Add((unit, spellId));
        _auras.Remove((unit, spellId));
    }

    public void Interrupt(Creature caster) => Interrupted.Add(caster);

    public void OnCreatureRemoved(Creature creature)
    {
    }
}

/// <summary>A creature spell caster that records, with each cast, the caster's victim at that moment.</summary>
internal sealed class VictimRecordingCaster : ICreatureSpellCaster
{
    public List<(uint Spell, Unit? Target, Unit? Victim)> Casts { get; } = [];

    public event Action<Unit, Unit, SpellInfo>? SpellHit { add { } remove { } }

    public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
    {
        Casts.Add((spellId, target, caster.Combat.Victim));
        return CreatureCastResult.Ok;
    }

    public bool IsCasting(Creature caster) => false;

    public bool HasAura(Unit unit, uint spellId) => false;

    public void Interrupt(Creature caster)
    {
    }

    public void OnCreatureRemoved(Creature creature)
    {
    }
}

/// <summary>
/// The confirmed findings of the 2026-10-08 script fidelity review (docs/integration/script-fidelity-20261008.md), each against the
/// mangos-classic ScriptDev2 behaviour it restores.
/// </summary>
public sealed class ScriptFidelityTests
{
    private static void Ticks(DungeonScriptHarness run, uint ms, uint step = 100)
    {
        for (uint done = 0; done < ms; done += step)
        {
            run.Tick(step);
        }
    }

    private static void Ticks(DungeonScriptTestKit run, uint ms, uint step = 100)
    {
        for (uint done = 0; done < ms; done += step)
        {
            run.Tick(step);
        }
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(target, field.FieldType.IsEnum ? Enum.ToObject(field.FieldType, value) : value);
    }

    // ---- Wailing Caverns: npc_disciple_of_naralex (wailing_cavernsScripts.cpp) ----

    [Fact]
    public void Disciple_SleepsARandomAttackerBelowTheTopOfTheThreatList_NeverTheTank()
    {
        // UpdateEscortAI: SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 1) for SPELL_SLEEP (wailing_cavernsScripts.cpp:449-456).
        var caster = new DungeonTestCaster();
        using DungeonScriptHarness run = new(map => new WailingCavernsInstance(map), [3678], [3678], null,
            new CreatureAiServices { Spells = caster });
        Player second = run.AddPlayer(2);
        Creature disciple = run.Creature(3678);
        Assert.IsType<DiscipleOfNaralexAi>(disciple.AI);
        Assert.True(run.Creatures.AttackStart(disciple, run.Player));
        run.Map.Combat.DealDamage(run.Player, disciple, 10, direct: true);
        run.Map.Combat.DealDamage(second, disciple, 1, direct: true);

        Ticks(run, 6_000);

        Assert.Contains(caster.Casts, c => c.Spell == 1090 && ReferenceEquals(c.Target, second));
        Assert.DoesNotContain(caster.Casts, c => c.Spell == 1090 && ReferenceEquals(c.Target, run.Player));
    }

    [Fact]
    public void Disciple_ChamberStep8_EndsTheAwakeningChannelBeforeSpeaking()
    {
        // Step 8: InterruptNonMeleeSpells(false, SPELL_AWAKENING) and RemoveAurasDueToSpell(SPELL_AWAKENING) (wailing_cavernsScripts.cpp:365-372).
        var caster = new AuraTrackingCaster();
        using DungeonScriptHarness run = new(map => new WailingCavernsInstance(map), [3678], [3678], null,
            new CreatureAiServices { Spells = caster });
        Creature disciple = run.Creature(3678);
        var ai = Assert.IsType<DiscipleOfNaralexAi>(disciple.AI);
        SetField(ai, "_point", 70u);
        SetField(ai, "_phase", 8);

        typeof(DiscipleOfNaralexAi).GetMethod("UpdateChamber", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ai, null);

        Assert.Contains(disciple, caster.Interrupted);
        Assert.Contains((disciple, 6271u), caster.Removed);
        Assert.Equal(9, ai.EventPhase);
    }

    // ---- Scarlet Monastery: boss_high_inquisitor_whitemaneAI (boss_mograine_and_whitemane.cpp) ----

    private static DungeonScriptHarness MograineAndWhitemane(ICreatureSpellCaster caster)
        => new(map => new ScarletMonasteryInstance(map), [3976, 3977], [3976, 3977], null, new CreatureAiServices { Spells = caster },
            (ScarletMonasteryInstance.WhitemaneDoor, GameObjectType.Door));

    [Fact]
    public void Whitemane_DominateMind_IsOffBeforeTheResurrection_ThenGoesOnARandomPlayer()
    {
        // AddCombatAction(WHITEMANE_ACTION_DOMINATE_MIND, true) - disabled - is armed only by HandleResurrectionCombat, and its target is
        // SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 0, SPELL_DOMINATEMIND, SELECT_FLAG_PLAYER) (boss_mograine_and_whitemane.cpp:333-345,
        // 433-443, 497-504).
        var caster = new DungeonTestCaster();
        using DungeonScriptHarness run = MograineAndWhitemane(caster);
        Player second = run.AddPlayer(2);
        Creature whitemane = run.Creature(3977);
        var ai = Assert.IsType<WhitemaneAi>(whitemane.AI);
        run.Player.Relocate(whitemane.X + 2, whitemane.Y, whitemane.Z, 0, 0);
        second.Relocate(whitemane.X - 2, whitemane.Y, whitemane.Z, 0, 0);
        // Not direct: the players do not start swinging at her (her death prevention aside, they would end the test early).
        run.Map.Combat.DealDamage(run.Player, whitemane, 10, direct: false);
        run.Map.Combat.DealDamage(second, whitemane, 1, direct: false);
        whitemane.Combat.Threat.AddThreat(run.Player, 100);
        whitemane.Combat.Threat.AddThreat(second, 10);

        Ticks(run, 60_000);
        Assert.DoesNotContain(caster.Casts, c => c.Spell == 14515);
        Assert.True(whitemane.Combat.IsInCombat, $"victim {whitemane.Combat.Victim?.Guid} evading {whitemane.IsEvading} alive {whitemane.IsAlive}");

        whitemane.Health = whitemane.MaxHealth / 2;
        Ticks(run, 100);
        Assert.True(ai.DeepSleepTriggered);
        ai.OnMovementInform(MovementGeneratorType.Point, 2); // she reached Mograine: Scarlet Resurrection in 3 s, combat 5.7 s later
        Ticks(run, 9_000);
        Assert.Contains(caster.Casts, c => c.Spell == 9232);

        Ticks(run, 600_000, step: 500);
        Assert.Contains(caster.Casts, c => c.Spell == 14515 && ReferenceEquals(c.Target, second));
        Assert.All(caster.Casts.Where(c => c.Spell == 14515), c => Assert.IsType<Player>(c.Target));
    }

    [Fact]
    public void Whitemane_Heal_GoesToTheFriendlyMissingTheMostHealth_HerselfIncluded()
    {
        // WHITEMANE_ACTION_HEAL: DoSelectLowestHpFriendly(50.0f) (boss_mograine_and_whitemane.cpp:454-462), not only Mograine.
        var caster = new DungeonTestCaster();
        using DungeonScriptHarness run = MograineAndWhitemane(caster);
        Creature whitemane = run.Creature(3977);
        run.Player.Relocate(whitemane.X + 2, whitemane.Y, whitemane.Z, 0, 0);
        run.Map.Combat.DealDamage(run.Player, whitemane, 10, direct: true);

        Ticks(run, 10_500);

        Assert.Contains(caster.Casts, c => c.Spell == 12039 && ReferenceEquals(c.Target, whitemane));
    }

    // ---- Blackrock Depths: npc_grimstoneAI (blackrock_depths.cpp) ----

    [Fact]
    public void Grimstone_TheldrensBandDead_CreditsTheChallenge_AndTheirCorpsesStayToBeLooted()
    {
        // SummonedCreatureJustDied, PHASE_GLADIATORS: DoChallengeQuestCredit, KilledMonsterCredit(16166) (blackrock_depths.cpp:298-330);
        // the ring summons are TEMPSPAWN_DEAD_DESPAWN (:338).
        using DungeonScriptHarness run = new(map => new BlackrockDepthsInstance(map), [10096, 16059, 9027], []);
        var script = Assert.IsType<BlackrockDepthsInstance>(run.Data);
        List<(Player Player, uint Entry)> credits = [];
        script.CreatureCredit = (player, entry, _) => credits.Add((player, entry));
        Assert.True(script.EnterRingOfLaw(run.Player, run.Player.X, run.Player.Y, run.Player.Z));
        Creature grimstone = run.Creature(10096);
        var ai = Assert.IsType<GrimstoneAI>(grimstone.AI);

        // A ring boss of the ordinary phase gives no credit.
        MethodInfo summonAtNorth = typeof(GrimstoneAI).GetMethod("SummonAtNorth", BindingFlags.Instance | BindingFlags.NonPublic)!;
        summonAtNorth.Invoke(ai, [9027u]);
        Creature boss = run.Creature(9027);
        run.Map.Combat.Kill(run.Player, boss);
        Assert.Empty(credits);

        SetField(ai, "_ringPhase", 2); // RingPhase.Gladiators
        summonAtNorth.Invoke(ai, [16059u]);
        Creature theldren = run.Creature(16059);
        run.Map.Combat.Kill(run.Player, theldren);

        Assert.Equal((run.Player, 16166u), Assert.Single(credits));
        Ticks(run, 2_000);
        Assert.Same(theldren, run.Creatures.FindCreature(theldren.Guid));
        Assert.Same(boss, run.Creatures.FindCreature(boss.Guid));
        Assert.False(theldren.IsAlive);
    }

    // ---- Scholomance: instance_scholomance (instance_scholomance.cpp) ----

    [Fact]
    public void Scholomance_RattlegoresDeath_ResetsTheEntranceRoomOnce()
    {
        // SetData(TYPE_RATTLEGORE, DONE) -> DoRespawnEntranceRoom (instance_scholomance.cpp:107-158, 171-178).
        using var run = new DungeonScriptTestKit(map => new ScholomanceInstance(map),
            [11622, 10485, 10495, 10481, 11551], [11622], []);
        var script = Assert.IsType<ScholomanceInstance>(run.Script);
        // OnCreatureCreate stores the mobs inside aEntranceRoom's volume only.
        Creature roomMob = run.Creatures.SpawnTemporary(run.Creatures.Content.FindTemplate(10485)!, 190f, 80f, 105f, 0);
        Creature outside = run.Creatures.SpawnTemporary(run.Creatures.Content.FindTemplate(10495)!, 100f, 0f, 50f, 0);

        run.Kill(11622);

        Assert.False(run.Creatures.FindCreature(roomMob.Guid) is { IsAlive: true });
        Assert.True(outside.IsAlive);
        Creature[] fresh = [.. run.Creatures.Creatures.Where(c => c.Spawn is null && c.IsAlive && !ReferenceEquals(c, outside))];
        Assert.Equal(17, fresh.Length);
        Assert.Equal(8, fresh.Count(c => c.Template.Entry == 10485u));
        Assert.Equal(1, fresh.Count(c => c.Template.Entry == 11551u));

        script.OnPlayerEnter(run.Player); // m_bIsRoomReset: only once
        Assert.Equal(17, run.Creatures.Creatures.Count(c => c.Spawn is null && c.IsAlive && !ReferenceEquals(c, outside)));
    }

    [Fact]
    public void Scholomance_DawnGambit_TurnsTheViewingRoomHostileAfterTwelveSeconds()
    {
        // ProcessEventId_dawn_gambit and HandleDawnGambitEvent (instance_scholomance.cpp:429-469, 498-509).
        var caster = new DungeonTestCaster();
        using var run = new DungeonScriptTestKit(map => new ScholomanceInstance(map),
            [10475, 10432, 10433], [10475, 10432, 10433], [], aiServices: new CreatureAiServices { Spells = caster });
        Creature student = run.Creature(10475), vectus = run.Creature(10432), marduk = run.Creature(10433);

        Assert.True(run.Script.OnSpellEvent(run.Player, 5140u));
        Ticks(run, 11_000);
        Assert.NotEqual(233u, student.FactionTemplate);
        Ticks(run, 1_100);

        Assert.Equal(233u, student.FactionTemplate);
        Assert.Equal(233u, vectus.FactionTemplate);
        Assert.Equal(233u, marduk.FactionTemplate);
        Assert.Contains(caster.Casts, c => c.Spell == 18115u && ReferenceEquals(c.Target, student));
    }

    [Fact]
    public void Scholomance_Kirtonos_IsATimedOutOfCombatSummon_WhoseCorpseStays()
    {
        // dbscripts_on_go_use 2890009: command 10, 900000 ms - TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN (ScriptMgr.cpp:2062), not a corpse despawn.
        using var run = new DungeonScriptTestKit(map => new ScholomanceInstance(map), [ScholomanceInstance.NpcKirtonos], [],
            [(ScholomanceInstance.GoBrazierOfTheHerald, GameObjectType.Button)]);
        run.Script.OnGameObjectUse(run.Player, run.Object(ScholomanceInstance.GoBrazierOfTheHerald));
        Ticks(run, 5_100);
        Creature kirtonos = run.Creature(ScholomanceInstance.NpcKirtonos);

        run.Map.Combat.Kill(run.Player, kirtonos);
        Ticks(run, 2_000);

        Assert.Same(kirtonos, run.Creatures.FindCreature(kirtonos.Guid));
        Assert.False(kirtonos.IsAlive);
    }

    // ---- Stratholme: instance_stratholme (stratholme.cpp) and its bosses ----

    private static DungeonScriptHarness Stratholme(uint[] templates, uint[] spawns, ICreatureSpellCaster? caster = null)
        => new(map => new StratholmeInstance(map), templates, spawns, null, new CreatureAiServices { Spells = caster },
            (StratholmeInstance.GoGauntletPort, GameObjectType.Door), (StratholmeInstance.GoBaronDoor, GameObjectType.Door),
            (StratholmeInstance.GoSlaughterhouse, GameObjectType.Door));

    [Fact]
    public void Stratholme_BarthilasRun_WarnsRunsAndIsTeleportedEightSecondsLater_AndRespawnsThere()
    {
        // SetData(TYPE_BARTHILAS_RUN, IN_PROGRESS), Update's m_barthilasRunTimer, OnCreatureRespawn (stratholme.cpp:427-441, 761-768, 1089-1107).
        using DungeonScriptHarness run = Stratholme([StratholmeInstance.NpcBarthilas], [StratholmeInstance.NpcBarthilas]);
        Creature barthilas = run.Creature(StratholmeInstance.NpcBarthilas);

        run.Data.SetData(StratholmeInstance.TypeBarthilasRun, EncounterState.InProgress);
        Assert.Equal(MovementGeneratorType.Point, barthilas.Motion.CurrentType);
        Ticks(run, 7_800);
        Assert.Equal(EncounterState.InProgress, run.Data.GetData(StratholmeInstance.TypeBarthilasRun));
        Ticks(run, 400);

        Assert.Equal(EncounterState.Done, run.Data.GetData(StratholmeInstance.TypeBarthilasRun));
        Assert.Equal(4068.284f, barthilas.X, 0.01f);
        Assert.Equal(-3535.678f, barthilas.Y, 0.01f);

        run.Kill(StratholmeInstance.NpcBarthilas);
        run.Creatures.ForceRespawn(barthilas);
        Creature back = run.Creature(StratholmeInstance.NpcBarthilas);
        Assert.True(back.IsAlive);
        Assert.Equal(4068.284f, back.X, 0.01f);
    }

    [Fact]
    public void Stratholme_BlackGuardsWipe_OpensTheGauntlet_AndANewTryClosesIt()
    {
        // TYPE_BLACK_GUARDS: FAIL opens GO_PORT_GAUNTLET, IN_PROGRESS after FAIL closes it (stratholme.cpp:442-462).
        using DungeonScriptHarness run = Stratholme([], []);
        GameObject gauntlet = run.Object(StratholmeInstance.GoGauntletPort);
        Assert.Equal(GameObjectState.Ready, gauntlet.State);
        run.Data.SetData(StratholmeInstance.TypeBlackGuards, EncounterState.InProgress);
        Assert.Equal(GameObjectState.Ready, gauntlet.State);

        run.Data.SetData(StratholmeInstance.TypeBlackGuards, EncounterState.Fail);
        Assert.Equal(GameObjectState.Active, gauntlet.State);

        run.Data.SetData(StratholmeInstance.TypeBlackGuards, EncounterState.InProgress);
        Assert.Equal(GameObjectState.Ready, gauntlet.State);
    }

    [Fact]
    public void Stratholme_BaronRepulledAfterAWipe_ClosesTheGauntletAgain()
    {
        // TYPE_BARON IN_PROGRESS: DoUseOpenableObject(GO_PORT_GAUNTLET, false) after a FAIL (stratholme.cpp:352-357).
        using DungeonScriptHarness run = Stratholme([], []);
        GameObject gauntlet = run.Object(StratholmeInstance.GoGauntletPort);
        run.Data.SetData(StratholmeInstance.TypeBaron, EncounterState.InProgress);
        run.Data.SetData(StratholmeInstance.TypeBaron, EncounterState.Fail);
        Assert.Equal(GameObjectState.Active, gauntlet.State);

        run.Data.SetData(StratholmeInstance.TypeBaron, EncounterState.InProgress);

        Assert.Equal(GameObjectState.Ready, gauntlet.State);
    }

    [Fact]
    public void Stratholme_BlackGuardTimer_SendsTheGuardsOutOfTheSlaughterhouse()
    {
        // Update's m_blackGuardsTimer: each living, idle Black Guard MovePoints near stratholmeLocation[5] (stratholme.cpp:1055-1077).
        using DungeonScriptHarness run = Stratholme([StratholmeInstance.NpcBlackGuard], [StratholmeInstance.NpcBlackGuard]);
        Creature guard = run.Creature(StratholmeInstance.NpcBlackGuard);
        Assert.NotEqual(MovementGeneratorType.Point, guard.Motion.CurrentType);
        SetField(run.Data, "_guardsTimer", 100u);

        Ticks(run, 200);

        Assert.Equal(MovementGeneratorType.Point, guard.Motion.CurrentType);
    }

    [Fact]
    public void Stratholme_Ramstein_RunsOutToTheSquare_AndHisCorpseStays()
    {
        // SetData(TYPE_RAMSTEIN, SPECIAL) with no abomination left: Ramstein (TEMPSPAWN_DEAD_DESPAWN) MovePoints to stratholmeLocation[5]
        // (4033.044, -3431.031), not the summon-room point [3] (stratholme.cpp:290-294; stratholme.h:127-138).
        using DungeonScriptHarness run = Stratholme([StratholmeInstance.NpcRamstein], []);
        run.Data.SetData(StratholmeInstance.TypeRamstein, EncounterState.Special);
        Creature ramstein = run.Creature(StratholmeInstance.NpcRamstein);

        Ticks(run, 15_000);
        Assert.True(ramstein.Y < -3400f, $"Ramstein stopped at y {ramstein.Y}");

        run.Map.Combat.Kill(run.Player, ramstein);
        Ticks(run, 2_000);
        Assert.Same(ramstein, run.Creatures.FindCreature(ramstein.Guid));
        Assert.Equal(EncounterState.Done, run.Data.GetData(StratholmeInstance.TypeRamstein));
    }

    private static Creature Me(CreatureAI ai) => ai.Me;

    private static (DungeonScriptHarness Run, Creature Boss, Player Near, Player Far) BossWithTwoAttackers<TAi>(
        Func<Creature, TAi> ai, ICreatureSpellCaster caster, out TAi script) where TAi : CreatureAI
    {
        DungeonScriptHarness run = Stratholme([10000], [10000], caster);
        Player far = run.AddPlayer(2);
        Creature boss = run.Creature(10000);
        run.Player.Relocate(boss.X + 1, boss.Y, boss.Z, 0, 0);
        far.Relocate(boss.X + 30, boss.Y, boss.Z, 0, 0);
        Assert.True(run.Creatures.AttackStart(boss, run.Player));
        boss.Combat.Threat.AddThreat(run.Player, 100);
        boss.Combat.Threat.AddThreat(far, 10);
        script = ai(boss);
        return (run, boss, run.Player, far);
    }

    [Fact]
    public void Willey_ShootsOnlyAttackersOutOfMeleeRange()
    {
        // SelectAttackingTarget(RANDOM, 0, SPELL_SHOOT, SELECT_FLAG_NOT_IN_MELEE_RANGE) (boss_cannon_master_willey.cpp:86).
        var caster = new DungeonTestCaster();
        (DungeonScriptHarness run, _, Player near, Player far) = BossWithTwoAttackers(c => new CannonMasterWilleyAI(c), caster, out var ai);
        using (run)
        {
            ai.OnRespawn();
            for (int i = 0; i < 20; i++)
            {
                ai.OnUpdate(4_000);
            }

            Assert.Contains(caster.Casts, c => c.Spell == 16496 && ReferenceEquals(c.Target, far));
            Assert.DoesNotContain(caster.Casts, c => c.Spell == 16496 && ReferenceEquals(c.Target, near));
        }
    }

    [Fact]
    public void Maleki_DrainsManaOnlyFromManaUsers()
    {
        // SelectAttackingTarget(RANDOM, 0, SPELL_DRAIN_MANA, SELECT_FLAG_POWER_MANA) (boss_maleki_the_pallid.cpp:103).
        var caster = new DungeonTestCaster();
        (DungeonScriptHarness run, _, Player near, Player far) = BossWithTwoAttackers(c => new MalekiThePallidAI(c), caster, out var ai);
        using (run)
        {
            near.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Rage);
            far.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
            ai.OnRespawn();
            for (int i = 0; i < 20; i++)
            {
                SetField(ai, "_tomb", 10_000_000u); // keep Ice Tomb (which takes its target off the threat list) out of the way
                ai.OnUpdate(30_000);
            }

            Assert.Contains(caster.Casts, c => c.Spell == 17243 && ReferenceEquals(c.Target, far));
            Assert.DoesNotContain(caster.Casts, c => c.Spell == 17243 && ReferenceEquals(c.Target, near));
        }
    }

    [Fact]
    public void Anastari_Possess_HidesHer_AndBreaksWhenThePlayerFallsToHalfHealth()
    {
        // UpdateAI's m_uiPossessEndTimer check and the AnastariPossess aura script (boss_baroness_anastari.cpp:75-121, 159-206).
        var caster = new AuraTrackingCaster();
        (DungeonScriptHarness run, Creature boss, _, Player far) = BossWithTwoAttackers(c => new BaronessAnastariAI(c), caster, out var ai);
        using (run)
        {
            ai.OnRespawn();
            ai.OnUpdate(15_000);
            Assert.Contains(caster.Casts, c => c.Spell == 17244u && ReferenceEquals(c.Target, far));
            ai.OnUpdate(1_000);
            Assert.True(caster.Casts.Any(c => c.Spell == 17246u && ReferenceEquals(c.Target, far)),
                $"casts={string.Join(',', caster.Casts.Select(c => c.Spell))} aura={caster.HasAura(far, 17244u)} inMap={run.Map.Players.Contains(far)} alive={far.IsAlive}");
            Assert.True(caster.HasAura(boss, 17250u));

            int casts = caster.Casts.Count;
            ai.OnUpdate(1_000);
            Assert.Equal(casts, caster.Casts.Count); // she does nothing else while possessing

            far.Health = far.MaxHealth / 2;
            ai.OnUpdate(1_000);

            Assert.False(caster.HasAura(boss, 17250u));
            Assert.Contains((far, 17244u), caster.Removed);
            Assert.Contains((far, 17246u), caster.Removed);
        }
    }

    // ---- Minor findings ----

    [Fact]
    public void Zumrah_ShadowBolt_GoesOnARandomAttacker()
    {
        // SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 0) for SPELL_SHADOW_BOLT (boss_zumrah.cpp:165).
        var caster = new VictimRecordingCaster();
        using DungeonScriptHarness run = new(map => new ZulFarrakInstance(map), [ZumrahAi.Entry], [ZumrahAi.Entry], null,
            new CreatureAiServices { Spells = caster });
        Player second = run.AddPlayer(2);
        Creature zumrah = run.Creature(ZumrahAi.Entry);
        run.Player.Relocate(zumrah.X + 2, zumrah.Y, zumrah.Z, 0, 0);
        second.Relocate(zumrah.X - 2, zumrah.Y, zumrah.Z, 0, 0);
        run.Map.Combat.DealDamage(run.Player, zumrah, 1, direct: false);
        zumrah.Combat.Threat.AddThreat(run.Player, 100);
        zumrah.Combat.Threat.AddThreat(second, 10);

        Ticks(run, 60_000);

        Assert.Contains(caster.Casts, c => c.Spell == 12739 && c.Target is not null && !ReferenceEquals(c.Target, c.Victim));
    }

    [Fact]
    public void Zumrah_HealingWave_GoesToTheFriendlyMissingTheMostHealth_NotTheLowestPercentage()
    {
        // DoSelectLowestHpFriendly(40.0f) (boss_zumrah.cpp:192) with MostHPMissingInRangeCheck: the most health missing in points.
        var caster = new DungeonTestCaster();
        using DungeonScriptHarness run = new(map => new ZulFarrakInstance(map), [ZumrahAi.Entry, 10000], [ZumrahAi.Entry, 10000], null,
            new CreatureAiServices { Spells = caster });
        Creature zumrah = run.Creature(ZumrahAi.Entry), guard = run.Creature(10000);
        guard.FactionTemplate = 14; // the faction Zum'rah takes when he turns hostile (ZumrahAi.MoveInLineOfSight)
        guard.MaxHealth = 1000;
        guard.Health = 990; // 10 missing, 99%
        run.Player.Relocate(zumrah.X + 2, zumrah.Y, zumrah.Z, 0, 0);
        run.Map.Combat.DealDamage(run.Player, zumrah, 5, direct: false); // 5 missing, 91%
        zumrah.Combat.Threat.AddThreat(run.Player, 100);
        run.Map.Combat.DealDamage(run.Player, guard, 0, direct: false);
        guard.Combat.Threat.AddThreat(run.Player, 1);

        Ticks(run, 16_000);

        Assert.Contains(caster.Casts, c => c.Spell == 12491 && ReferenceEquals(c.Target, guard));
        Assert.DoesNotContain(caster.Casts, c => c.Spell == 12491 && ReferenceEquals(c.Target, zumrah));
    }

    [Fact]
    public void Wyrmthalak_SummonedAdds_AttackARandomThreatTarget()
    {
        // JustSummoned: SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 0), else the victim (boss_overlord_wyrmthalak.cpp:69-79).
        var caster = new DungeonTestCaster();
        (DungeonScriptHarness run, Creature boss, Player near, Player far) = BossWithTwoAttackers(c => new OverlordWyrmthalakAI(c), caster, out var ai);
        using (run)
        {
            CreatureTemplate template = run.Creatures.Content.FindTemplate(10000)!;
            List<Creature> adds = [];
            for (int i = 0; i < 12; i++)
            {
                Creature add = run.Creatures.SpawnTemporary(template, boss.X, boss.Y + 3, boss.Z, 0);
                ai.OnJustSummoned(add);
                adds.Add(add);
            }

            Assert.Contains(adds, a => ReferenceEquals(a.Combat.Victim, far));
            Assert.Contains(adds, a => ReferenceEquals(a.Combat.Victim, near));
        }
    }

    [Fact]
    public void Emi_GrubbisSpeaksHisOwnSpawnLine()
    {
        // JustSummoned: DoScriptText(SAY_GRUBBIS_SPAWN, pSummoned) - Grubbis is the speaker (gnomeregan.cpp:194).
        using var run = new DungeonScriptTestKit(map => new GnomereganInstance(map), [EmiShortfuseAi.Entry, EmiShortfuseAi.Grubbis],
            [EmiShortfuseAi.Entry, EmiShortfuseAi.Grubbis], [],
            ai: new CreatureAiContent([], [new CreatureAiText(-1090023, "Grubbis!", 0, 0, 0)]));
        Creature emi = run.Creature(EmiShortfuseAi.Entry), grubbis = run.Creature(EmiShortfuseAi.Grubbis);
        var ai = Assert.IsType<EmiShortfuseAi>(emi.AI);
        int before = run.Sent(WorldOpcode.SmsgMessagechat).Count;

        ai.OnJustSummoned(grubbis);

        byte[] chat = Assert.Single(run.Sent(WorldOpcode.SmsgMessagechat).Skip(before));
        Assert.Equal(grubbis.Guid.Value, BitConverter.ToUInt64(chat, 5));
    }

    private static (uint, uint, CreatureWaypoint)[] EscortPath(uint entry)
        => [(entry, EscortAI.EscortPathId, new CreatureWaypoint(1, -10f, -383.07f, 61.78f, 0, 0)),
            (entry, EscortAI.EscortPathId, new CreatureWaypoint(2, -5f, -383.07f, 61.78f, 0, 0))];

    [Fact]
    public void Willix_And_Belnistrasz_TakeTheirEscortFactionsWhenTheQuestIsAccepted()
    {
        // SetFactionTemporary(FACTION_ESCORT_N_NEUTRAL_PASSIVE 113 / _ACTIVE 250, TEMPFACTION_RESTORE_RESPAWN) (razorfen_kraul.cpp:148,
        // razorfen_downs.cpp:301; ScriptDevAIMgr.h:46, 50).
        using (DungeonScriptHarness run = new(map => new RazorfenKraulInstance(map), [WillixAi.Entry], [WillixAi.Entry], null, null,
                   EscortPath(WillixAi.Entry), null))
        {
            Creature willix = run.Creature(WillixAi.Entry);
            Assert.True(DungeonScriptHooks.OnQuestAccepted(run.Player, willix.Guid, WillixAi.Quest));
            Assert.Equal(113u, willix.FactionTemplate);
        }

        using (DungeonScriptHarness run = new(map => new RazorfenDownsInstance(map), [BelnistraszAi.Entry], [BelnistraszAi.Entry], null, null,
                   EscortPath(BelnistraszAi.Entry), null))
        {
            Creature belnistrasz = run.Creature(BelnistraszAi.Entry);
            Assert.True(DungeonScriptHooks.OnQuestAccepted(run.Player, belnistrasz.Guid, BelnistraszAi.Quest));
            Assert.Equal(250u, belnistrasz.FactionTemplate);
        }
    }

    [Fact]
    public void Emi_StartingTheEvent_TakesTheEscortFactionAndClearsHerFlags()
    {
        // Phase 1: SetFactionTemporary(FACTION_ESCORT_N_NEUTRAL_PASSIVE), UNIT_NPC_FLAGS and UNIT_DYNAMIC_FLAGS 0 (gnomeregan.cpp:352-357).
        using var run = new DungeonScriptTestKit(map => new GnomereganInstance(map), [EmiShortfuseAi.Entry], [EmiShortfuseAi.Entry], []);
        Creature emi = run.Creature(EmiShortfuseAi.Entry);
        var ai = Assert.IsType<EmiShortfuseAi>(emi.AI);
        emi.SetUInt32(UpdateFields.UnitDynamicFlags, 0x20);
        SetField(ai, "_phase", 1);
        SetField(ai, "_phaseTimer", 1u);

        run.Tick(100);

        Assert.Equal(113u, emi.FactionTemplate);
        Assert.Equal(0u, emi.GetUInt32(UpdateFields.UnitDynamicFlags));
    }

    [Fact]
    public void MrSmite_ResumesOnHisTopThreatTarget_NotTheOneHeStompedFrom()
    {
        // PhaseEquipEnd: SelectAttackingTarget(ATTACKING_TARGET_TOPAGGRO, 0); evade only when there is none (boss_mr_smite.cpp:148-167).
        var spells = new RecordingCreatureSpells();
        using DungeonScriptHarness run = new(map => new DeadminesInstance(map), [646], [646], null,
            new CreatureAiServices { Spells = spells }, (144111, GameObjectType.Generic));
        Player second = run.AddPlayer(2);
        Creature smite = run.Creature(646);
        var ai = Assert.IsType<MrSmiteAi>(smite.AI);
        run.Player.Relocate(smite.X + 1, smite.Y, smite.Z, 0, 0);
        second.Relocate(smite.X - 1, smite.Y, smite.Z, 0, 0);
        Assert.True(run.Creatures.AttackStart(smite, run.Player));
        smite.Combat.Threat.AddThreat(run.Player, 100);
        smite.Combat.Threat.AddThreat(second, 1);
        smite.Health = smite.MaxHealth * 60 / 100;
        run.Tick();
        Assert.Equal(MrSmiteAi.SmitePhase.Equipping, ai.Phase);
        smite.Combat.Threat.AddThreat(second, 10_000); // the other attacker took the lead meanwhile

        for (int i = 0; i < 200 && ai.Phase != MrSmiteAi.SmitePhase.Second; i++)
        {
            run.Tick(100);
        }

        Assert.Equal(MrSmiteAi.SmitePhase.Second, ai.Phase);
        Assert.Same(second, smite.Combat.Victim);
    }

    [Fact]
    public void Arugal_TeleportInterruptsHisCast_AndHisOpeningTimersAreRandom()
    {
        // shadowfang_keep.cpp:527-533 (InterruptNonMeleeSpells before the port) and :392-393 (22-26 s teleport, 20-30 s curse).
        var caster = new AuraTrackingCaster();
        using var run = new DungeonScriptTestKit(map => new ShadowfangKeepInstance(map), [4275], [], [],
            aiServices: new CreatureAiServices { Spells = caster },
            extraSpawns: [Spawn(10, 4275, -14f, -383.07f, 151f, mapId: InstanceFixture.Dungeon)]);
        Creature arugal = run.Creature(4275);
        var ai = Assert.IsType<ArugalAi>(arugal.AI);
        HashSet<uint> curses = [];
        for (int i = 0; i < 10; i++)
        {
            ai.OnRespawn();
            uint curse = (uint)typeof(ArugalAi).GetField("_curse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ai)!;
            uint teleport = (uint)typeof(ArugalAi).GetField("_teleport", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ai)!;
            Assert.InRange(curse, 20_000u, 30_000u);
            Assert.InRange(teleport, 22_000u, 26_000u);
            curses.Add(curse);
        }

        Assert.True(curses.Count > 1);
        Assert.True(run.Creatures.AttackStart(arugal, run.Player));
        for (int i = 0; i < 10 && !caster.Interrupted.Contains(arugal); i++)
        {
            ai.OnUpdate(60_000); // a roll for the spot he already holds only reschedules
        }

        Assert.Contains(arugal, caster.Interrupted);
    }

    [Fact]
    public void Mograine_DivineShield_WaitsFortySeconds_AndIsReadyAgainAfterTheRevival()
    {
        // AddCombatAction(MOGRAINE_ACTION_DIVINE_SHIELD, 40000u); HandleRevivedTimer: SetActionReadyStatus(DIVINE_SHIELD, true)
        // (boss_mograine_and_whitemane.cpp:88, 262-272, 301-307).
        var caster = new DungeonTestCaster();
        using DungeonScriptHarness run = MograineAndWhitemane(caster);
        Creature mograine = run.Creature(3976);
        var ai = Assert.IsType<MograineAi>(mograine.AI);
        run.Player.Relocate(mograine.X + 1, mograine.Y, mograine.Z, 0, 0);
        run.Map.Combat.DealDamage(run.Player, mograine, mograine.MaxHealth * 6 / 10, direct: false);
        mograine.Combat.Threat.AddThreat(run.Player, 100);

        Ticks(run, 30_000);
        Assert.DoesNotContain(caster.Casts, c => c.Spell == 642);
        Ticks(run, 11_000);
        Assert.Single(caster.Casts, c => c.Spell == 642);

        run.Map.Combat.DealDamage(run.Player, mograine, mograine.Health, direct: false); // fake death
        Assert.True(ai.IsFeigningDeath);
        ai.OnResurrected();
        Ticks(run, 5_200);
        Assert.False(ai.IsFeigningDeath);
        mograine.Health = mograine.MaxHealth / 3;
        mograine.Combat.Threat.AddThreat(run.Player, 100);
        Ticks(run, 500);

        Assert.Equal(2, caster.Casts.Count(c => c.Spell == 642));
    }

    [Fact]
    public void ChoRush_SitsWhenCreatedAfterTheKingDied()
    {
        // instance_dire_maul::OnCreatureCreate (instance_dire_maul.cpp:107-110).
        using var run = new DungeonScriptTestKit(map => new DireMaulInstance(map), [DireMaulInstance.NpcChorush], [], []);
        run.Script.SetData(DireMaulInstance.TypeKingGordok, EncounterState.Done);

        Creature chorush = run.Creatures.SpawnTemporary(run.Creatures.Content.FindTemplate(DireMaulInstance.NpcChorush)!,
            run.Player.X + 2, run.Player.Y, run.Player.Z, 0);

        Assert.Equal(StandState.Sit, chorush.StandState);
    }

    [Fact]
    public void Noxxion_DyingWhileHisSpawnsAuraIsUp_LeavesHisCorpseSelectable()
    {
        // SummonNoxxionsSpawns::OnApply's remove branch clears UNIT_FLAG_UNINTERACTIBLE (boss_noxxion.cpp:100-108).
        var caster = new AuraTrackingCaster();
        using DungeonScriptHarness run = Stratholme([10000], [10000], caster);
        Creature noxxion = run.Creature(10000);
        var ai = new NoxxionAI(noxxion);
        caster.Cast(noxxion, 21708, noxxion, triggered: true);
        ai.OnUpdate(100);
        Assert.NotEqual((UnitFlags)0, noxxion.UnitFlags & UnitFlags.NotSelectable);

        ai.OnDeath(run.Player);

        Assert.Equal((UnitFlags)0, noxxion.UnitFlags & UnitFlags.NotSelectable);
    }
}
