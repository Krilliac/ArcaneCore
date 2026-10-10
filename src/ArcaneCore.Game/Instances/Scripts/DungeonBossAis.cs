using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.BlackrockSpire;
using ArcaneCore.Game.Instances.Scripts.Scholomance;
using ArcaneCore.Game.Instances.Scripts.Stratholme;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>
/// The ScriptDev2 boss scripts selected by map and creature entry. CreatureMapSystem uses this
/// after any explicitly registered map AI, before the template AIName (mangos-classic
/// src/game/AI/ScriptDevAI/scripts/{eastern_kingdoms,kalimdor}, AddSC_boss_*).
/// </summary>
public static class DungeonBossAis
{
    public static CreatureAI? Create(Creature creature, uint mapId) => (mapId, creature.Template.Entry) switch
    {
        (229, 10339) => new GythAI(creature),
        (229, 9816) => new PyroguardEmberseerAI(creature),
        (229, 9568) => new OverlordWyrmthalakAI(creature),
        (289, 1853) => new DarkmasterGandlingAI(creature),
        (289, 10503) => new JandiceBarovAI(creature),
        (289, 10498) => new SpectralTutorAI(creature),
        (329, 10384) or (329, 10385) => new SpectralGhostlyCitizenAI(creature),
        (329, 10436) => new BaronessAnastariAI(creature),
        (329, 10438) => new MalekiThePallidAI(creature),
        (329, 10997) => new CannonMasterWilleyAI(creature),
        (329, 10812) => new DathrohanBalnazzarAI(creature),
        _ => null,
    };
}

/// <summary>Common SD2 timer rule: count while fighting and retry a failed cast on the next tick.</summary>
public abstract class ScriptDevBossAI(Creature creature) : AggressorAI(creature)
{
    protected bool CastWhenReady(ref uint timer, uint diffMs, Unit? target, uint spell, int nextMinMs, int nextMaxMs)
    {
        if (timer > diffMs)
        {
            timer -= diffMs;
            return false;
        }

        timer = 0;
        if (target is null || DoCast(target, spell) != CreatureCastResult.Ok)
        {
            return false;
        }

        timer = (uint)(System?.RandomInt(nextMinMs, nextMaxMs) ?? nextMinMs);
        return true;
    }

    protected Unit? RandomThreatTarget(bool skipVictim = false, bool playerOnly = false, Func<Unit, bool>? filter = null)
    {
        Unit[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target)
            .Where(u => u.IsAlive && (!skipVictim || !ReferenceEquals(u, Victim)) && (!playerOnly || u is Player)
                && (filter?.Invoke(u) ?? true))];
        return targets.Length == 0 ? null : targets[System?.RandomInt(0, targets.Length - 1) ?? 0];
    }

    protected bool InCombat() => UpdateVictim() && Victim is not null;
}
