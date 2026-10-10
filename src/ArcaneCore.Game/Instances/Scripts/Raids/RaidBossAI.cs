using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.BlackwingLair;
using ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using ArcaneCore.Game.Instances.Scripts.ZulGurub;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Instances.Scripts.Raids;

/// <summary>
/// The combat-action subset of mangos-classic AI/ScriptDevAI/base/CombatAI.cpp:
/// combat-only countdowns, reset on evade/respawn, and retry a ready action until its cast succeeds.
/// Melee and victim selection remain with CreatureMapSystem.
/// </summary>
public abstract class RaidBossAI(Creature creature, uint? encounter) : AggressorAI(creature)
{
    private sealed class ActionTimer(Func<uint> initial, Func<bool> execute, Func<uint> repeat)
    {
        public uint Remaining = initial();
        public Func<uint> Initial { get; } = initial;
        public Func<bool> Execute { get; } = execute;
        public Func<uint> Repeat { get; } = repeat;
    }

    private readonly List<ActionTimer> _actions = [];
    protected InstanceData? Instance => Me.Map?.FindUpdater<InstanceData>();

    // vmangos CreatureAISelector.cpp selectAI: script before template AIName, never a controlled unit.
    public static CreatureAI? Create(Creature creature)
    {
        if (!creature.CharmerGuid.IsEmpty || creature.Summon is { Kind: Pets.SummonKind.Pet })
        {
            return null;
        }

        return (creature.Map?.FindUpdater<InstanceData>(), creature.Template.Entry) switch
        {
            (BlackwingLairInstance, 12017) => new BroodlordAI(creature),
            (BlackwingLairInstance, 11983) => new FiremawAI(creature),
            (BlackwingLairInstance, 11981) => new FlamegorAI(creature),
            (BlackwingLairInstance, 14601) => new EbonrocAI(creature),
            (BlackwingLairInstance, 14020) => new ChromaggusAI(creature),
            (BlackwingLairInstance, 13020) => new VaelastraszAI(creature),
            (BlackwingLairInstance, 12435) => new RazorgoreAI(creature),
            (BlackwingLairInstance, 10162) => new VictorNefariusAI(creature),
            (BlackwingLairInstance, BlackwingOrbAI.Entry) => new BlackwingOrbAI(creature),
            (BlackwingLairInstance, 11583) => new NefarianAI(creature),
            (RuinsOfAhnQirajInstance, 15348) => new KurinnaxxAI(creature),
            (RuinsOfAhnQirajInstance, 15340) => new MoamAI(creature),
            (RuinsOfAhnQirajInstance, 15370) => new BuruAI(creature),
            (RuinsOfAhnQirajInstance, 15369) => new AyamissAI(creature),
            (RuinsOfAhnQirajInstance, 15339) => new OssirianAI(creature),
            (RuinsOfAhnQirajInstance, 15514) => new BuruEggAI(creature),
            (RuinsOfAhnQirajInstance, 15471) => new AndorovAI(creature),
            (RuinsOfAhnQirajInstance, 15473) => new KaldoreiEliteAI(creature),
            (RuinsOfAhnQirajInstance, AnubisathGuardianAI.Entry) => new AnubisathGuardianAI(creature),
            (ZulGurubInstance, 14517) => new JeklikAI(creature),
            (ZulGurubInstance, 14507) => new VenoxisAI(creature),
            (ZulGurubInstance, 14510) => new MarliAI(creature),
            (ZulGurubInstance, 14509) => new ThekalAI(creature),
            (ZulGurubInstance, 11347) => new LorKhanAI(creature),
            (ZulGurubInstance, 11348) => new ZathAI(creature),
            (ZulGurubInstance, 14515) => new ArlokkAI(creature),
            (ZulGurubInstance, 11380) => new JindoAI(creature),
            (ZulGurubInstance, 11382) => new MandokirAI(creature),
            (ZulGurubInstance, 15114) => new GahzrankaAI(creature),
            // Gri'lek (15082) and Wushoolay (15085) have no entry here: classic-db z2815 gives both AIName 'EventAI' and no ScriptName
            // (creature_ai_scripts 1508201-1508202, 1508501-1508502), and neither reference core scripts them, so the host's
            // CreatureEventAI runs them. A factory entry would shadow that EventAI, because this lookup runs before AIName.
            (ZulGurubInstance, 15083) => new HazzarahAI(creature),
            (ZulGurubInstance, 15084) => new RenatakiAI(creature),
            (TempleOfAhnQirajInstance temple, 15263) => new SkeramAI(creature, temple),
            (TempleOfAhnQirajInstance temple, 15511) => new KriAI(creature, temple),
            (TempleOfAhnQirajInstance temple, 15543) => new YaujAI(creature, temple),
            (TempleOfAhnQirajInstance temple, 15544) => new VemAI(creature, temple),
            (TempleOfAhnQirajInstance, 15509) => new HuhuranAI(creature),
            (TempleOfAhnQirajInstance, AnubisathSentinelAI.Entry) => new AnubisathSentinelAI(creature),
            (TempleOfAhnQirajInstance, AnubisathDefenderAI.Entry) => new AnubisathDefenderAI(creature),
            (TempleOfAhnQirajInstance temple, 15516) => new SarturaAI(creature, temple),
            (TempleOfAhnQirajInstance temple, 15984) => new SarturaRoyalGuardAI(creature, temple),
            (TempleOfAhnQirajInstance, 15510) => new FankrissAI(creature),
            (TempleOfAhnQirajInstance, 15630) => new SpawnOfFankrissAI(creature),
            (TempleOfAhnQirajInstance, 15962) => new FankrissHatchlingAI(creature),
            (TempleOfAhnQirajInstance, 15299) => new ViscidusAI(creature),
            (TempleOfAhnQirajInstance, 15667) => new ViscidusGlobAI(creature),
            (TempleOfAhnQirajInstance, 15922) when creature.System?.SummonerOf(creature)?.Entry == 15727
                => new CthunPuntAI(creature),
            (TempleOfAhnQirajInstance, 15922) => new ViscidusToxinTriggerAI(creature),
            (TempleOfAhnQirajInstance, 15275) => new VeknilashAI(creature),
            (TempleOfAhnQirajInstance, 15276) => new VeklorAI(creature),
            (TempleOfAhnQirajInstance, 15316 or 15317) => new TwinBugAI(creature),
            (TempleOfAhnQirajInstance, 15957) => new OuroSpawnerAI(creature),
            (TempleOfAhnQirajInstance, 15517) => new OuroAI(creature),
            (TempleOfAhnQirajInstance, 15712) => new OuroMoundAI(creature),
            (TempleOfAhnQirajInstance, 15718) => new OuroScarabAI(creature),
            (TempleOfAhnQirajInstance, 15727) => new CthunBodyAI(creature),
            (TempleOfAhnQirajInstance, 15589) => new CthunEyeAI(creature),
            (TempleOfAhnQirajInstance, 15725 or 15726 or 15728 or 15334 or 15802) => new CthunTentacleAI(creature),
            (ZulGurubInstance, 14834) => new HakkarAI(creature),
            (ZulGurubInstance, SoulflayerAI.Entry) => new SoulflayerAI(creature),
            (ZulGurubInstance, GurubashiBatRiderAI.Entry) => new GurubashiBatRiderAI(creature),
            (NaxxramasInstance raid, 15956 or 15953 or 15952 or 15954 or 15936 or 16011)
                => new NaxxramasBossAI(creature, raid),
            (NaxxramasInstance raid, 16573) => new NaxxramasCryptGuardAI(creature, raid),
            (NaxxramasInstance, 16061) => new RazuviousAI(creature),
            (NaxxramasInstance, 16060) => new GothikAI(creature),
            (NaxxramasInstance, 16065 or 16062 or 16064 or 16063) => new HorsemanAI(creature),
            (NaxxramasInstance, 16028) => new PatchwerkAI(creature),
            (NaxxramasInstance, 15931) => new GrobbulusAI(creature),
            (NaxxramasInstance, 15932) => new GluthAI(creature),
            (NaxxramasInstance, 15929 or 15930) => new ThaddiusAddAI(creature),
            (NaxxramasInstance, 15928) => new ThaddiusAI(creature),
            (NaxxramasInstance, 15989) => new SapphironAI(creature),
            (NaxxramasInstance, 15990) => new KelThuzadAI(creature),
            (NaxxramasInstance, LivingPoisonAI.Entry) => new LivingPoisonAI(creature),
            (NaxxramasInstance, StoneskinGargoyleAI.Entry) => new StoneskinGargoyleAI(creature),
            (NaxxramasInstance, DiseasedMaggotAI.Diseased or DiseasedMaggotAI.Rotting) => new DiseasedMaggotAI(creature),
            (NaxxramasInstance, IcecrownGuardianAI.Entry) => new IcecrownGuardianAI(creature),
            _ => null,
        };
    }

    protected uint RandomDelay(int min, int max) => (uint)(System?.RandomInt(min, max) ?? min);

    protected void AddAction(uint initial, Func<bool> execute, Func<uint> repeat)
        => _actions.Add(new ActionTimer(() => initial, execute, repeat));

    /// <summary>SD2 CombatAI AddCombatAction(action, min, max): the first delay is rolled again on every reset.</summary>
    protected void AddAction(int initialMin, int initialMax, Func<bool> execute, Func<uint> repeat)
        => _actions.Add(new ActionTimer(() => RandomDelay(initialMin, initialMax), execute, repeat));

    protected bool Cast(uint spell, Unit? target = null, bool triggered = false)
        => DoCast(target, spell, triggered) == CreatureCastResult.Ok;

    /// <summary>Health at or below <paramref name="percent"/> (ScriptDev2 <c>GetHealthPercent() &lt;= pct</c>).</summary>
    protected bool Below(uint percent) => (ulong)Me.Health * 100 <= (ulong)Me.MaxHealth * percent;

    /// <summary>
    /// cmangos UnitAI::SetMeleeEnabled: the flag for later AttackStart calls and the swing already running at the victim (a bare
    /// <see cref="CreatureAI.MeleeEnabled"/> set leaves an active swing going, because the map's melee loop reads the combat state).
    /// </summary>
    protected void SetMeleeEnabled(bool enabled)
    {
        MeleeEnabled = enabled;
        if (Victim is { } victim)
        {
            System?.SetMelee(Me, victim, enabled);
        }
    }

    /// <summary>
    /// cmangos UnitAI::SetCombatMovement: on chases the victim at once, off drops the chase and stops where the creature stands
    /// (<see cref="CreatureMapSystem.ApplyCombatMovement"/>; a bare <see cref="CreatureAI.CombatMovement"/> set keeps the chase running).
    /// </summary>
    protected void SetCombatMovement(bool enabled)
    {
        CombatMovement = enabled;
        System?.ApplyCombatMovement(Me);
    }


    /// <summary>Health strictly below <paramref name="percent"/>: exactly ScriptDev2's <c>GetHealthPercent() &lt; pct</c>, in integers.</summary>
    protected bool HealthBelowPct(uint percent) => (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * percent;

    protected Unit? RandomTarget()
    {
        Unit[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target)
            .Where(t => t.IsAlive && t.IsInWorld && ReferenceEquals(t.Map, Me.Map))];
        return targets.Length == 0 ? null : targets[System!.RandomInt(0, targets.Length - 1)];
    }

    protected void ResetThreat()
    {
        foreach (var entry in Me.Combat.Threat.Entries.ToArray())
            Me.Combat.Threat.ModifyThreatPercent(entry.Target, -100);
    }

    public override void OnAggro(Unit target)
    {
        if (encounter is { } slot) Instance?.SetData(slot, EncounterState.InProgress);
    }

    public override void OnDeath(Unit? killer)
    {
        if (encounter is { } slot) Instance?.SetData(slot, EncounterState.Done);
    }

    public override void OnReachedHome()
    {
        if (encounter is { } slot) Instance?.SetData(slot, EncounterState.Fail);
    }

    public override void OnEvade() => ResetActions();

    public override void OnRespawn() => ResetActions();

    protected virtual void ResetActions()
    {
        foreach (ActionTimer action in _actions)
        {
            action.Remaining = action.Initial();
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim())
        {
            return;
        }

        UpdateCombat(diffMs);
    }

    protected virtual void UpdateCombat(uint diffMs)
    {
        foreach (ActionTimer action in _actions)
        {
            action.Remaining = action.Remaining > diffMs ? action.Remaining - diffMs : 0;
            if (action.Remaining == 0 && action.Execute())
            {
                action.Remaining = action.Repeat();
            }
        }
    }
}
