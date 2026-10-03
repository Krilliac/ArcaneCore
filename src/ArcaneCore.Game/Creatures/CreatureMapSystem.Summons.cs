using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Temporary EventAI summons (cmangos EventAI SUMMON).</summary>
public sealed partial class CreatureMapSystem
{
    private readonly List<(Creature Creature, long DespawnAtMs)> _summons = [];

    /// <summary>
    /// cmangos EventAI SUMMON: a temporary creature at the summoner's position that attacks
    /// <paramref name="target"/> and despawns after <paramref name="despawnMs"/> (0 = stays until
    /// it dies or its grid unloads). A missing template is reported once and summons nothing.
    /// </summary>
    public Creature? Summon(Creature summoner, uint entry, Unit? target, uint despawnMs)
    {
        ArgumentNullException.ThrowIfNull(summoner);
        if (_content.FindTemplate(entry) is not { } template)
        {
            if (_reportedAi.Add($"summon:{entry}"))
            {
                _logger.LogWarning("{Creature} EventAI summons missing creature_template {Entry}; skipped", summoner.Guid, entry);
            }

            return null;
        }

        Creature summoned = SpawnTemporary(template, summoner.X, summoner.Y, summoner.Z, summoner.Orientation);
        if (despawnMs > 0)
        {
            _summons.Add((summoned, _clockMs + despawnMs));
        }

        if (target is not null && target.IsAlive)
        {
            if (summoned.AI is { } ai)
            {
                ai.AttackStart(target);
            }
            else
            {
                AttackStart(summoned, target);
            }
        }

        return summoned;
    }
}
