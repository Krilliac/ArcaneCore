namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Summon</c>: bots bring a far group member to the group (docs/areas/playbots-groups.md, Summons). A warlock bot of a group
/// that knows Ritual of Summoning and carries a Soul Shard casts it on a member who is on another map or far away; two more group bots click the
/// summoning portal; the member (a bot, or a real player through the ordinary summon prompt) accepts within two minutes. Where no warlock can,
/// two bots at a dungeon meeting stone may summon the member through it (not vanilla 1.12, off by default, see <see cref="MeetingStones"/>). Every key is live:
/// <c>.reload config</c> changes this object in place and a bot reads it at its next decision.
/// </summary>
public sealed class PlayerbotSummonOptions
{
    /// <summary>Let bots summon far group members with Ritual of Summoning and accept summons from their group (on by default).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Non-vanilla: let two group members at a dungeon meeting stone summon a third (TBC summoning-stone rules; strict 1.12 meeting stones only
    /// queue for the dungeon). On the server it lets CMSG_GAMEOBJ_USE on a meeting stone, with a group member selected, send that member a
    /// summon request, for a real player as well as for a bot (no reagent, no cooldown); the bots use it when no warlock can summon. Off by default (retail: the use of a stone is refused); on is a deliberate deviation.
    /// </summary>
    public bool MeetingStones { get; set; }

    /// <summary>A member on another map, or farther than this many yards from the summoner, is summoned (50..5000).</summary>
    public float FarYards { get; set; } = 200f;

    /// <summary>Seconds before the same member is summoned again after an attempt that was declined, ignored or failed (10..3600).</summary>
    public int RetrySeconds { get; set; } = 120;

    /// <summary>The farthest from the portal, in yards, a group bot walks over to help with a ritual (5..100).</summary>
    public float HelperYards { get; set; } = 40f;

    /// <summary>Whether the warlock tells the party who it is summoning and to click the portal, when the member is a real player (on by default).</summary>
    public bool AnnounceToPlayers { get; set; } = true;

    internal static string? CheckFarYards(float v) => float.IsFinite(v) && v is >= 50f and <= 5000f ? null : "must be 50..5000";

    internal static string? CheckRetrySeconds(int v) => v is >= 10 and <= 3600 ? null : "must be 10..3600";

    internal static string? CheckHelperYards(float v) => float.IsFinite(v) && v is >= 5f and <= 100f ? null : "must be 5..100";

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Summon";
        if (CheckFarYards(FarYards) is { } far) throw new InvalidOperationException($"{section}: FarYards {far}.");
        if (CheckRetrySeconds(RetrySeconds) is { } retry) throw new InvalidOperationException($"{section}: RetrySeconds {retry}.");
        if (CheckHelperYards(HelperYards) is { } helper) throw new InvalidOperationException($"{section}: HelperYards {helper}.");
    }
}
