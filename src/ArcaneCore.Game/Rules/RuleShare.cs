namespace ArcaneCore.Game.Rules;

/// <summary>
/// The stable per-subject share of design section 5.4: a rule applies to a subject when
/// <c>Bucket(share_seed, rule_id, subject_guid) &lt; share_pct</c>. The bucket is a fixed function of its three inputs (two SplitMix64
/// finalisations, then mod 100; no process-randomised hashing), so the same subjects stay selected across ticks, threads and restarts,
/// and changing the seed reshuffles them. The values are pinned by <c>RuleShareTests</c>.
/// </summary>
public static class RuleShare
{
    /// <summary>
    /// Mixed into the seed when a rule's own <see cref="RuleFlags.StableShare"/> chance is tested, so it is drawn independently of the
    /// rule's filter share (otherwise a 30% filter and a 50% stable chance would select the same 30%, not 15%).
    /// </summary>
    public const uint StableChanceSalt = 0x5EED5EED;

    /// <summary>The bucket 0..99 of (<paramref name="seed"/>, <paramref name="ruleId"/>, <paramref name="guid"/>).</summary>
    public static int Bucket(int seed, int ruleId, ulong guid)
    {
        ulong h = Mix(((ulong)(uint)ruleId << 32) | (uint)seed);
        h = Mix(h ^ guid);
        return (int)(h % 100);
    }

    /// <summary>Whether a subject is in a <paramref name="sharePct"/>% share; 100 or more selects everyone, 0 nobody.</summary>
    public static bool Selects(int sharePct, int seed, int ruleId, ulong guid)
        => sharePct >= 100 || (sharePct > 0 && Bucket(seed, ruleId, guid) < sharePct);

    /// <summary>SplitMix64 (Steele, Lea, Flood 2014): add the golden gamma, then the two xor-shift-multiply rounds.</summary>
    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
