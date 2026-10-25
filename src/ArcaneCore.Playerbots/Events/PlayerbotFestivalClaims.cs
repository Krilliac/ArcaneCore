using System.Numerics;

namespace ArcaneCore.World.Playerbots.Events;

/// <summary>The crowd limits (<c>MaxPerGossipAnchor</c>, <c>MaxPerServiceAnchor</c>, <c>MaxPerTown</c>), read live at every claim.</summary>
internal readonly record struct PlayerbotFestivalCaps(int PerGossipAnchor, int PerServiceAnchor, int PerTown);

/// <summary>A festival NPC bots gather around: its spawn <see cref="Guid"/>, its <see cref="Entry"/>, where it stands, what it offers and the festival root it belongs to.</summary>
internal sealed record PlayerbotFestivalAnchor(ulong Guid, uint Entry, Vector3 Position, PlayerbotAnchorKind Kind, ushort Root);

/// <summary>
/// One place held at an anchor: who holds it (<see cref="Bot"/>), where it stands (<see cref="Spot"/>), in which town and for which team (the
/// caps count by these), when it was taken and last renewed and a <see cref="Serial"/> that is new for every claim taken (a bot that gives up
/// its place and takes one again has arrived anew).
/// </summary>
internal readonly record struct PlayerbotFestivalClaim(
    Guid Bot, PlayerbotFestivalAnchor Anchor, Vector3 Spot, string Town, uint Team, long RenewedAtMs, long SinceMs = 0, long Serial = 0);

/// <summary>
/// The places bots hold around the festival anchors of a world (docs/areas/playbots-social.md, World events). A bot takes a place when it
/// decides to go (<see cref="TryClaim"/>), so the caps hold when many bots decide in the same pass; every held think renews it
/// (<see cref="Renew"/>), it expires <see cref="ExpiryMs"/> after its last renewal (a bot that logs out or is despawned frees its place with
/// no call), and leaving frees it at once (<see cref="Release"/>). A bot holds one place at a time.
/// <para>
/// Time is the caller's: every method takes the world milliseconds it means, and an expired claim is absent for every question asked at or
/// after its expiry, whether or not anything swept it yet. Thread affinity: world thread (plain dictionaries); the sweep and the counts walk
/// the claims, at most <c>MaxPerTown</c> per town and team.
/// </para>
/// </summary>
internal sealed class PlayerbotFestivalClaims
{
    /// <summary>A claim that is not renewed for this long (ms) is gone.</summary>
    internal const long ExpiryMs = 60_000;

    private readonly Dictionary<Guid, PlayerbotFestivalClaim> _byBot = [];
    private readonly List<Guid> _sweepScratch = [];
    private readonly List<PlayerbotFestivalClaim> _activeScratch = [];
    private long _serial;

    /// <summary>How many claims have been taken in all (the serial of the latest; inspection, tests).</summary>
    internal long TakenCount => _serial;

    private static bool Alive(in PlayerbotFestivalClaim claim, long nowMs) => nowMs - claim.RenewedAtMs <= ExpiryMs;

    /// <summary>
    /// Take a place at <paramref name="anchor"/> for <paramref name="bot"/>, standing at <paramref name="spot"/>. True when it holds one there
    /// afterwards: it already held one at this anchor (renewed, its spot and town unchanged), or the anchor and the town have room under
    /// <paramref name="caps"/> (a bot that moves from another anchor gives its old place up first, and does not count against itself).
    /// A refused bot holds nothing, its old place included only when it was taken at this same anchor.
    /// </summary>
    internal bool TryClaim(PlayerbotFestivalAnchor anchor, Vector3 spot, Guid bot, string town, uint team, PlayerbotFestivalCaps caps, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(town);
        Sweep(nowMs);
        if (_byBot.TryGetValue(bot, out PlayerbotFestivalClaim held))
        {
            if (held.Anchor.Guid == anchor.Guid)
            {
                _byBot[bot] = held with { RenewedAtMs = nowMs };
                return true;
            }
        }

        int cap = anchor.Kind == PlayerbotAnchorKind.Service ? caps.PerServiceAnchor : caps.PerGossipAnchor;
        if (cap <= 0) return false;
        int atAnchor = 0, inTown = 0;
        foreach (KeyValuePair<Guid, PlayerbotFestivalClaim> entry in _byBot)
        {
            if (entry.Key == bot) continue; // the bot's own place does not count against it
            PlayerbotFestivalClaim other = entry.Value;
            if (other.Anchor.Guid == anchor.Guid) atAnchor++;
            if (other.Team == team && string.Equals(other.Town, town, StringComparison.Ordinal)) inTown++;
        }

        if (atAnchor >= cap || inTown >= caps.PerTown) return false;
        _byBot[bot] = new PlayerbotFestivalClaim(bot, anchor, spot, town, team, nowMs, nowMs, ++_serial);
        return true;
    }

    /// <summary>Extend the bot's claim to <paramref name="nowMs"/>. False (and the claim, if expired, removed) when the bot holds none that is alive.</summary>
    internal bool Renew(Guid bot, long nowMs)
    {
        if (!_byBot.TryGetValue(bot, out PlayerbotFestivalClaim claim)) return false;
        if (!Alive(claim, nowMs))
        {
            _byBot.Remove(bot);
            return false;
        }

        _byBot[bot] = claim with { RenewedAtMs = nowMs };
        return true;
    }

    /// <summary>Give the bot's place up at once. Harmless for a bot that holds none.</summary>
    internal void Release(Guid bot) => _byBot.Remove(bot);

    /// <summary>The bot's claim if it is alive at <paramref name="nowMs"/>.</summary>
    internal PlayerbotFestivalClaim? ClaimOf(Guid bot, long nowMs)
        => _byBot.TryGetValue(bot, out PlayerbotFestivalClaim claim) && Alive(claim, nowMs) ? claim : null;

    /// <summary>The live claims at an anchor.</summary>
    internal int CountAt(ulong anchorGuid, long nowMs)
    {
        int count = 0;
        foreach (PlayerbotFestivalClaim claim in _byBot.Values)
            if (claim.Anchor.Guid == anchorGuid && Alive(claim, nowMs)) count++;
        return count;
    }

    /// <summary>The live claims of a team in a town.</summary>
    internal int CountInTown(string town, uint team, long nowMs)
    {
        int count = 0;
        foreach (PlayerbotFestivalClaim claim in _byBot.Values)
            if (claim.Team == team && string.Equals(claim.Town, town, StringComparison.Ordinal) && Alive(claim, nowMs)) count++;
        return count;
    }

    /// <summary>
    /// The live claims, in no order, as a list the next call to this method reuses (copy it to keep it). The events lane's work list.
    /// </summary>
    internal IReadOnlyList<PlayerbotFestivalClaim> Active(long nowMs)
    {
        _activeScratch.Clear();
        foreach (PlayerbotFestivalClaim claim in _byBot.Values)
            if (Alive(claim, nowMs)) _activeScratch.Add(claim);
        return _activeScratch;
    }

    /// <summary>Claims held (alive or not yet swept; status and tests).</summary>
    internal int Count => _byBot.Count;

    /// <summary>Forget every claim (the feature went off, the board was rebuilt from nothing).</summary>
    internal void Clear() => _byBot.Clear();

    // Expired claims go when a claim is taken, which is the only time their absence matters for a cap; queries filter by time instead, so a
    // question about an earlier moment than a sweep still answers for that moment as long as the claim was not swept.
    private void Sweep(long nowMs)
    {
        _sweepScratch.Clear();
        foreach (KeyValuePair<Guid, PlayerbotFestivalClaim> entry in _byBot)
            if (!Alive(entry.Value, nowMs)) _sweepScratch.Add(entry.Key);
        for (int i = 0; i < _sweepScratch.Count; i++) _byBot.Remove(_sweepScratch[i]);
    }
}
