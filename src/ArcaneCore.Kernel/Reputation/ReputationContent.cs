namespace ArcaneCore.Kernel.Reputation;

/// <summary>
/// One spillover target of a reputation_spillover_template row (columns factionN, rate_N, rank_N): the faction that
/// receives <c>standing * Rate</c>, only while the player's rank with it is at most <see cref="MaxRank"/>.
/// </summary>
public readonly record struct ReputationSpillover(uint Faction, float Rate, byte MaxRank);

/// <summary>reputation_spillover_template: the targets (up to four, vmangos MAX_SPILLOVER_FACTIONS) of one source faction.</summary>
public sealed record ReputationSpilloverTemplate(uint Faction, IReadOnlyList<ReputationSpillover> Targets)
{
    public const int MaxTargets = 4;
}

/// <summary>reputation_reward_rate: per-faction multipliers per source; a rate of zero disables gain from that source.</summary>
public readonly record struct ReputationRewardRate(uint Faction, float QuestRate, float CreatureRate, float SpellRate);

/// <summary>Source of the reputation_spillover_template and reputation_reward_rate tables (world database).</summary>
public interface IReputationContentSource
{
    Task<ReputationContentRows> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>The raw rows of both tables, before validation.</summary>
public sealed record ReputationContentRows(
    IReadOnlyList<ReputationSpilloverTemplate> Spillovers,
    IReadOnlyList<ReputationRewardRate> Rates)
{
    public static ReputationContentRows Empty { get; } = new([], []);
}

/// <summary>
/// The validated, immutable spillover and reward-rate content. Swapped as a whole (reload), never edited.
/// Validation follows vmangos ObjectMgr::LoadReputationSpilloverTemplate (ObjectMgr.cpp:8971-9070) and
/// LoadReputationRewardRate (8827-8892): a row with an unknown faction is dropped; a negative rate drops its row;
/// a spillover target without a client reputation slot or with an out-of-range rank is only reported (vmangos logs
/// it inside a loop whose <c>continue</c> does not skip the row), and the later duplicate row of a key wins.
/// </summary>
public sealed class ReputationContent
{
    public const int RankCount = 8;

    private readonly Dictionary<uint, ReputationSpilloverTemplate> _spillovers;
    private readonly Dictionary<uint, ReputationRewardRate> _rates;

    private ReputationContent(Dictionary<uint, ReputationSpilloverTemplate> spillovers, Dictionary<uint, ReputationRewardRate> rates, IReadOnlyList<string> warnings)
    {
        _spillovers = spillovers;
        _rates = rates;
        Warnings = warnings;
    }

    public static ReputationContent Empty { get; } = new([], [], []);

    public int SpilloverCount => _spillovers.Count;

    public int RateCount => _rates.Count;

    /// <summary>One line per row that failed validation (dropped) or that vmangos only logs about (kept).</summary>
    public IReadOnlyList<string> Warnings { get; }

    public ReputationSpilloverTemplate? Spillover(uint faction) => _spillovers.GetValueOrDefault(faction);

    public ReputationRewardRate? Rate(uint faction) => _rates.TryGetValue(faction, out ReputationRewardRate rate) ? rate : null;

    public IEnumerable<ReputationSpilloverTemplate> Spillovers => _spillovers.Values;

    public IEnumerable<ReputationRewardRate> Rates => _rates.Values;

    public static ReputationContent Create(ReputationContentRows rows, FactionCatalog factions)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(factions);
        var warnings = new List<string>();
        var spillovers = new Dictionary<uint, ReputationSpilloverTemplate>();
        foreach (ReputationSpilloverTemplate template in rows.Spillovers)
        {
            if (factions.Find(template.Faction) is null)
            {
                warnings.Add($"reputation_spillover_template: faction {template.Faction} does not exist, row dropped");
                continue;
            }

            var targets = new List<ReputationSpillover>();
            bool unknown = false;
            foreach (ReputationSpillover target in template.Targets.Take(ReputationSpilloverTemplate.MaxTargets))
            {
                if (target.Faction == 0)
                {
                    continue; // an unused slot
                }

                if (factions.Find(target.Faction) is not { } spill)
                {
                    warnings.Add($"reputation_spillover_template: spillover faction {target.Faction} of faction {template.Faction} does not exist, row dropped");
                    unknown = true;
                    break;
                }

                if (!spill.CanHaveReputation)
                {
                    warnings.Add($"reputation_spillover_template: spillover faction {target.Faction} of faction {template.Faction} has no client reputation slot, useless");
                }

                if (target.MaxRank >= RankCount)
                {
                    warnings.Add($"reputation_spillover_template: rank {target.MaxRank} for spillover faction {target.Faction} of faction {template.Faction} is not a rank, never limits");
                }

                targets.Add(target);
            }

            if (!unknown)
            {
                spillovers[template.Faction] = new ReputationSpilloverTemplate(template.Faction, targets);
            }
        }

        var rates = new Dictionary<uint, ReputationRewardRate>();
        foreach (ReputationRewardRate rate in rows.Rates)
        {
            if (factions.Find(rate.Faction) is null)
            {
                warnings.Add($"reputation_reward_rate: faction {rate.Faction} does not exist, row dropped");
                continue;
            }

            if (rate.QuestRate < 0f || rate.CreatureRate < 0f || rate.SpellRate < 0f)
            {
                warnings.Add($"reputation_reward_rate: faction {rate.Faction} has a negative rate, row dropped");
                continue;
            }

            rates[rate.Faction] = rate;
        }

        return new ReputationContent(spillovers, rates, warnings);
    }
}
