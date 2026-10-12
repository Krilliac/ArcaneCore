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
    private readonly record struct Boss(Func<Creature, CreatureAI> Create, string Name);

    private static Boss B<T>(Func<Creature, T> factory) where T : CreatureAI => new(c => factory(c), typeof(T).Name);

    private static readonly Dictionary<(uint MapId, uint Entry), Boss> Table = new()
    {
        [(229, 10339)] = B(c => new GythAI(c)),
        [(229, 9816)] = B(c => new PyroguardEmberseerAI(c)),
        [(229, 9568)] = B(c => new OverlordWyrmthalakAI(c)),
        [(289, 1853)] = B(c => new DarkmasterGandlingAI(c)),
        [(289, 10503)] = B(c => new JandiceBarovAI(c)),
        [(289, 10498)] = B(c => new SpectralTutorAI(c)),
        [(329, 10384)] = B(c => new SpectralGhostlyCitizenAI(c)),
        [(329, 10385)] = B(c => new SpectralGhostlyCitizenAI(c)),
        [(329, 10436)] = B(c => new BaronessAnastariAI(c)),
        [(329, 10438)] = B(c => new MalekiThePallidAI(c)),
        [(329, 10997)] = B(c => new CannonMasterWilleyAI(c)),
        [(329, 10812)] = B(c => new DathrohanBalnazzarAI(c)),
    };

    public static CreatureAI? Create(Creature creature, uint mapId)
        => Table.TryGetValue((mapId, creature.Template.Entry), out Boss boss) ? boss.Create(creature) : null;

    /// <summary>The implementing type name of the boss AI of <paramref name="entry"/> on <paramref name="mapId"/>, or null when this table has none.</summary>
    public static string? Describe(uint mapId, uint entry) => Table.TryGetValue((mapId, entry), out Boss boss) ? boss.Name : null;
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
