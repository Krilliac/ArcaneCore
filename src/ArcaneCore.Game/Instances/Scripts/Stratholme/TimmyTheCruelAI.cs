using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Stratholme;

/// <summary>boss_timmy_the_cruelAI (vmangos scripts/eastern_kingdoms/eastern_plaguelands/stratholme/boss_timmy_the_cruel.cpp:34-77): Ravenous Claw
/// every 12 s (first after 7 s) on the victim, Enrage once under 10 percent health. Timmy is a temporary summon, so his corpse is removed for
/// good when it decays (the source's CorpseRemoved DeleteLater) and he never respawns.</summary>
public sealed class TimmyTheCruelAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellRavenousClaw = 17470, SpellEnrage = 8599;
    public const uint ClawFirstMs = 7_000, ClawIntervalMs = 12_000;
    public const float EnrageBelowPercent = 10f;

    private uint _claw = ClawFirstMs;

    public override void OnRespawn() => _claw = ClawFirstMs;

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _claw, diffMs, Victim, SpellRavenousClaw, (int)ClawIntervalMs, (int)ClawIntervalMs);
        if (Me.HealthPercent < EnrageBelowPercent && System?.HasAura(Me, SpellEnrage) != true)
        {
            DoCast(Me, SpellEnrage, triggered: true);
        }
    }
}

/// <summary>npc_crimson_guardsmanAI (same file, lines 80-161), ClassicDB ScriptName on Crimson Guardsman 10418: Disarm, Shield Bash and Shield
/// Charge on the victim; the one placed with spawn guid 54070 summons Timmy the Cruel when it dies, once, and its corpse is removed. (The
/// source's flee under 15 percent health, DoFlee, is not ported.)</summary>
public sealed class CrimsonGuardsmanAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint TimmySpawnerGuid = 54070, SayTimmySpawn = 6150;
    public const uint SpellDisarm = 6713, SpellShieldBash = 11972, SpellShieldCharge = 15749;
    public const float TimmyX = 3614.7f, TimmyY = -3187.64f, TimmyZ = 131.406f;

    private uint _disarm = 6_000, _shieldBash = 4_000, _shieldCharge = 1_000;
    private bool _spawnerDone;

    public override void OnRespawn()
    {
        _disarm = 6_000;
        _shieldBash = 4_000;
        _shieldCharge = 1_000;
    }

    public override void OnDeath(Unit? killer)
    {
        if (_spawnerDone || Me.Spawn?.Guid != TimmySpawnerGuid || System is not { } system)
        {
            return;
        }

        _spawnerDone = true; // m_bIsTimmySpawner = false
        // A Timmy already in the map (the instance tracks him) is never summoned twice.
        if (Me.Map?.FindUpdater<StratholmeInstance>()?.Timmy is { IsAlive: true })
        {
            return;
        }

        // TEMPSUMMON_MANUAL_DESPAWN with SetRespawnTime(9999999): a temporary summon here never respawns.
        if (system.SummonDeadDespawn(Me, StratholmeInstance.NpcTimmy, TimmyX, TimmyY, TimmyZ, 0f) is { } timmy)
        {
            system.SayText(timmy, (int)SayTimmySpawn, killer);
        }

        system.ForcedDespawn(Me, 1); // m_creature->DeleteLater(): the spawner's corpse goes on the next update
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _disarm, diffMs, Victim, SpellDisarm, 15_000, 15_000);
        CastWhenReady(ref _shieldBash, diffMs, Victim, SpellShieldBash, 8_000, 8_000);
        CastWhenReady(ref _shieldCharge, diffMs, Victim, SpellShieldCharge, 12_000, 12_000);
    }
}
