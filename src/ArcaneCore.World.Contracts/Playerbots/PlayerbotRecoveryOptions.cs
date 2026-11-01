namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Recovery</c>: how a dead bot treats living enemy players near its body. A ghost is invisible to the living,
/// so standing in reclaim range of the body is already out of their sight; the bot counts the enemy players who could attack it once
/// revived (and the allied players beside it) and, when outnumbered by <see cref="PvpCampMargin"/>, stays a ghost and waits for the
/// count to turn (cmangos/mangoszero ReviveFromCorpseAction weighs the same thing; the numbers here are ArcaneCore choices).
/// </summary>
public sealed class PlayerbotRecoveryOptions
{
    /// <summary>Weigh enemy players at the body before reclaiming (on by default).</summary>
    public bool PvpCampWait { get; set; } = true;

    /// <summary>Enemy and allied players are counted within this many yards (3D) of the body (10..100).</summary>
    public float PvpCampRadiusYards { get; set; } = 40f;

    /// <summary>The bot waits when enemies - (1 + allies) is at least this (0..20): 1 is two enemies against a lone bot, 0 waits at even numbers too.</summary>
    public int PvpCampMargin { get; set; } = 1;

    /// <summary>Total seconds the bot waits per death before it takes the spirit healer or the battleground spirit guide (5..600); in a battleground at most one spirit-guide wave (30 s).</summary>
    public int PvpCampMaxWaitSeconds { get; set; } = 90;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Recovery";
        if (!float.IsFinite(PvpCampRadiusYards) || PvpCampRadiusYards is < 10f or > 100f)
            throw new InvalidOperationException($"{section}: PvpCampRadiusYards must be 10..100.");
        if (PvpCampMargin is < 0 or > 20) throw new InvalidOperationException($"{section}: PvpCampMargin must be 0..20.");
        if (PvpCampMaxWaitSeconds is < 5 or > 600) throw new InvalidOperationException($"{section}: PvpCampMaxWaitSeconds must be 5..600.");
    }
}
