using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Events;

/// <summary>What <see cref="PlayerbotWorldPvpObjectives.Update"/> asks of the brain this think.</summary>
/// <param name="Busy">The controller drove the bot this think (walked, clicked, waited): the brain does nothing else.</param>
/// <param name="Goal">The goal kind the brain reports while busy.</param>
/// <param name="TargetEntry">The entry the bot is working on (the geyser while it seeks one; 0 while it carries).</param>
internal readonly record struct WorldPvpStep(bool Busy, PlayerbotGoalKind Goal = PlayerbotGoalKind.WorldPvp, uint TargetEntry = 0)
{
    /// <summary>Nothing for the controller to do: the brain goes on down its ladder.</summary>
    internal static WorldPvpStep None => default;
}

/// <summary>
/// The world PvP objectives of one bot (docs/areas/playbots-social.md, World PvP: Silithyst; docs/design/playerbot-social.md, P7c phase 1):
/// a bot in Silithus (zone 1377) at level 55 or more, that rolls in (<see cref="PlayerbotSilithystOptions.ParticipationChance"/>, higher
/// under War Mode), walks to the nearest silithyst geyser or mound (181598 / 181597), uses it for the Silithyst flag (aura 29519), carries it
/// on foot to its own faction's turn-in trigger (4162 Alliance, 4168 Horde) and walks into it; the server's area trigger handler does the
/// rest (counter, Traces of Silithyst 29534). One episode at a time:
/// <list type="bullet">
/// <item><b>Seek</b>: the nearest spawned geyser the ledger has not set aside, that is ready and that fewer than
/// <see cref="PlayerbotSilithystOptions.MaxBotsPerGeyser"/> other bots claim (<see cref="PlayerbotClaims"/>); the bot joins its claim.</item>
/// <item><b>Use</b>: walk to within <see cref="InteractYards"/>, stop, CMSG_GAMEOBJ_USE. <b>Await</b>: <see cref="AwaitAuraMs"/> for the flag.</item>
/// <item><b>Carry</b>: walk to the trigger centre; a bot that picked the flag up inside the volume (or stands at the centre with it) walks
/// out and in again, because the trigger fires on entering. A carrier never mounts (<c>PlayerbotMount.CanMountHere</c>; a mount-up cast under
/// way is cancelled), stealths (<c>RotationState.NoStealth</c>) or Vanishes as a retreat (<c>EscapeSituation.CarriesSilithyst</c>): the server
/// drops the dust when it does. The carry timer counts only the time this rung runs (not rest or fights). <b>Deliver</b> is done when the flag
/// is gone near the trigger and Traces of Silithyst was newly put on by this turn-in (an older one still running does not count).</item>
/// <item><b>Give-up</b>: a taken geyser, a lost flag away from the trigger, a death, no route, a refused turn-in or a timeout ends the episode;
/// the geyser is set aside in the ledger (60 s, doubling to 5 min for faults); a death while carrying only ends the run (the flag dies with
/// the bot and the geyser is spent anyway). A carry given up while the flag is still held cancels the
/// flag (CMSG_CANCEL_AURA, <see cref="MaxDropAttempts"/> tries) and does not take it up again for <see cref="GiveUpBackoffMs"/>: the aura never
/// expires, so without that the next think would open the same failing carry again. The fault is reported once per held flag. After a
/// delivery or any give-up no new run starts for <see cref="RunCooldownMs"/>.</item>
/// </list>
/// The brain calls <see cref="Update"/> twice per think: high on the ladder with <c>mayBegin: false</c> (an episode already running, or a
/// bot found carrying the flag) and low with <c>mayBegin: true</c> (the seek).
/// <para>
/// Anomalies (area <c>world-pvp</c>): <c>silithyst-use-no-aura</c> (the use gave no flag within <see cref="AwaitAuraMs"/>),
/// <c>silithyst-carry-timeout</c> (the carry ran past <see cref="CarryMaxMs"/>), <c>silithyst-unreachable-trigger</c> (no route or no trigger),
/// once per episode; nothing on success or on an early loss of the flag.
/// </para>
/// Thread affinity: world thread only, owned by one brain. Cost: while the bot is eligible in Silithus and not cooling down, a seek every
/// <see cref="ScanEveryMs"/> that does one dictionary lookup per geyser spawn of the map (16 in the real data; no enumerator, no allocation).
/// The geyser guids come from the map's spawn table, so they are complete whichever grids are loaded (an unloaded one is skipped at the lookup
/// and found as soon as its grid loads); the table is read once per map, shared by its bots in a per-map weak table, and again every
/// <see cref="GeyserCacheRefreshMs"/>. Mounds are not in the spawn table: the loaded objects of the map are scanned for them every
/// <see cref="MoundScanMs"/> (one enumerator per map per minute), so a mound in a grid that loads in between is seen up to a minute late.
/// Nothing per think otherwise but an options read and a zone test; a carrier adds an aura-list walk per think.
/// </summary>
internal sealed class PlayerbotWorldPvpObjectives
{
    /// <summary>Silithyst Geyser (type 10 goober, spell 29518 gives the flag).</summary>
    internal const uint GeyserEntry = 181598;

    /// <summary>Silithyst Mound, the same click.</summary>
    internal const uint MoundEntry = 181597;

    /// <summary>Silithyst (aura 29519): the flag a carrier holds.</summary>
    internal const uint SilithystFlag = SilithusCatalog.SpellSilithystFlag;

    /// <summary>The least level that takes part.</summary>
    internal const int MinLevel = 55;

    /// <summary>How often the geysers are looked over while the bot is eligible.</summary>
    internal const uint ScanEveryMs = 5_000;

    /// <summary>The bot uses a geyser from this close (centre to centre).</summary>
    internal const float InteractYards = 4f;

    /// <summary>How long the bot waits for the flag after the use.</summary>
    internal const uint AwaitAuraMs = 3_000;

    /// <summary>A walk to a geyser that has not finished in this long is given up (no anomaly).</summary>
    internal const uint UseMaxMs = 180_000;

    /// <summary>A carry that has not delivered in this long is reported (<c>silithyst-carry-timeout</c>) and given up.</summary>
    internal const uint CarryMaxMs = 300_000;

    /// <summary>The roll of the participation chance is made once per window.</summary>
    internal const uint ParticipationWindowMs = 120_000;

    /// <summary>A geyser another bot or a player has taken is left alone this long.</summary>
    internal const uint ContestedMs = 30_000;

    /// <summary>A geyser delivered from is looked at again after this (it respawns on its own).</summary>
    internal const uint DeliveredRecheckMs = 60_000;

    /// <summary>An episode before the carry that was not stepped for this long (a higher goal held the bot) is dropped.</summary>
    internal const uint StaleMs = 30_000;

    /// <summary>The flag is gone near the trigger: Traces of Silithyst is given this long to show before the loss counts.</summary>
    internal const uint TracesGraceMs = 2_000;

    /// <summary>Steps of the walk that stopped short before the way is given up as unreachable.</summary>
    internal const int MaxWalkFailures = 3;

    /// <summary>Within this of the trigger centre (plus the trigger's radius) a flag that vanished was turned in, not lost.</summary>
    internal const float DeliveryMarginYards = 8f;

    /// <summary>Arrived at the trigger centre; the area trigger has long fired.</summary>
    internal const float ArrivedYards = 1.5f;

    /// <summary>
    /// A bot that picked the flag up inside the trigger volume walks out to this far beyond its radius, then back in: the trigger fires on
    /// entering. Far enough that the exit point and the centre are not one goal to the route plan watch (10 yards), or the walk back in is
    /// refused as a replan loop.
    /// </summary>
    internal const float ExitMarginYards = 8f;

    /// <summary>Times the bot walks into the volume with the flag still on afterwards before it reports the turn-in as refused.</summary>
    internal const int MaxEntries = 2;

    /// <summary>After a delivery or a give-up the bot starts no new run for this long (one participation window).</summary>
    internal const uint RunCooldownMs = ParticipationWindowMs;

    /// <summary>After a give-up with the flag in hand the bot does not take up the same flag again for this long (the flag never expires).</summary>
    internal const uint GiveUpBackoffMs = 600_000;

    /// <summary>Times the bot tries to cancel a flag it gave up carrying, one per <see cref="DropRetryMs"/>.</summary>
    internal const int MaxDropAttempts = 5;

    /// <summary>The gap between two attempts to cancel the flag.</summary>
    internal const uint DropRetryMs = 1_000;

    /// <summary>The geyser spawn table of a map is read again after this (a content reload).</summary>
    internal const uint GeyserCacheRefreshMs = 600_000;

    /// <summary>The mounds loaded on a map (they are not in the spawn table) are looked up again after this.</summary>
    internal const uint MoundScanMs = 60_000;

    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;
    private readonly PlayerbotClaims _claims;
    private readonly PlayerbotOpportunityLedger _ledger = new();
    private Episode? _episode;
    private bool _scanned;
    private uint _nextScanMs;
    private TileWait _tileWait;
    private PlayerbotRoute? _route;
    private Vector3 _routeGoal;
    private bool _cooling;
    private uint _coolUntilMs;
    private bool _noAdopt;
    private uint _noAdoptUntilMs;
    private bool _dropFlag;
    private int _dropAttempts;
    private uint _dropNextMs;
    private bool _carryFaultReported;

    /// <summary>The geyser and mound objects of each map (their guids), found by one scan and looked up by guid afterwards.</summary>
    private static readonly ConditionalWeakTable<Map, GeyserCache> GeyserCaches = [];

    private sealed class GeyserCache
    {
        public ObjectGuid[] Geysers = [];
        public uint GeysersBuiltMs;
        public bool GeysersBuilt;
        public ObjectGuid[] Mounds = [];
        public uint MoundsBuiltMs;
        public bool MoundsBuilt;
    }

    internal PlayerbotWorldPvpObjectives(WorldSession session, PlayerbotOptions options, PlayerbotClaims? claims)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _claims = claims ?? new PlayerbotClaims(64);
    }

    /// <summary>The clock the controller reads (world milliseconds); tests replace it to step through backoffs.</summary>
    internal Func<uint>? Clock { get; set; }

    private uint Now => Clock?.Invoke() ?? _session.World.NowMs;

    /// <summary>What the bot sets aside (tests).</summary>
    internal PlayerbotOpportunityLedger Ledger => _ledger;

    /// <summary>The geyser the bot is on, or null (tests and inspection).</summary>
    internal ulong? ActiveKey => _episode is { Key: not 0 } e ? e.Key : null;

    /// <summary>Whether an episode is running (any phase).</summary>
    internal bool Active => _episode is not null;

    /// <summary>Whether the running episode is in its carry (tests and inspection).</summary>
    internal bool IsCarrying => _episode is { Phase: Phase.Carry };

    /// <summary>Flags picked up by this bot (tests and inspection).</summary>
    internal int Pickups { get; private set; }

    /// <summary>Flags this bot delivered (tests and inspection).</summary>
    internal int Deliveries { get; private set; }

    /// <summary>How the latest episode ended (tests and inspection); null: none yet.</summary>
    internal string? LastOutcome { get; private set; }

    /// <summary>The episode in words (debugging).</summary>
    internal string Description => _episode is { } e ? $"worldpvp=silithyst entry={e.Entry} phase={e.Phase.ToString().ToLowerInvariant()}" : LastOutcome is { } last ? $"worldpvp={last}" : "worldpvp=none";

    /// <summary>
    /// Whether <paramref name="player"/> holds the Silithyst flag. Called by the mount check and the class upkeep: world thread, no
    /// allocation (a service lookup and a scan of the aura list).
    /// </summary>
    internal static bool Carries(Player player)
        => player.Session is WorldSession session && session.Services.GetService<SpellFeature>() is { } spells && spells.System.HasAura(player, SilithystFlag);

    /// <summary>Whether the bot holds the flag; <paramref name="traces"/> is its Traces of Silithyst holder, if any (no allocation).</summary>
    private bool Carries(Player player, out SpellAuraHolder? traces)
    {
        traces = null;
        if (_session.Services.GetService<SpellFeature>()?.System is not { } spells) return false;
        List<SpellAuraHolder> holders = spells.GetAuras(player);
        for (int i = 0; i < holders.Count; i++)
        {
            if (holders[i].Spell.Id == SilithusCatalog.SpellTracesOfSilithyst && !holders[i].IsRemoved) traces = holders[i];
        }

        return spells.HasAura(player, SilithystFlag);
    }

    private static bool Past(uint untilMs, uint now) => unchecked((int)(now - untilMs)) >= 0;

    /// <summary>Whether the bot is where the objective is offered: Silithus proper, at the level, alive.</summary>
    internal static bool Eligible(Player player)
        => player.MapId == SilithusCatalog.MapId && player.ZoneId == SilithusCatalog.SilithusZone && player.Level >= MinLevel;

    // --- the think --------------------------------------------------------------------------------------------------

    /// <summary>
    /// One think, out of combat. <paramref name="mayBegin"/>: the bot may start a new run (the low rung); false still carries on a run
    /// in progress and takes up a bot that holds the flag already (the high rung).
    /// </summary>
    internal WorldPvpStep Update(Player player, uint elapsedMs, bool mayBegin)
    {
        ArgumentNullException.ThrowIfNull(player);
        PlayerbotSilithystOptions options = _options.WorldPvp.Silithyst;
        if (elapsedMs == 0 || !player.IsInWorld || !player.IsAlive || player.Map is not { } map)
        {
            if (_episode is not null)
            {
                // A death before the flag is in hand costs the geyser a strike; a map change or a disabled bot just ends the run.
                if (player.IsInWorld && !player.IsAlive && _episode.Phase != Phase.Carry) _ledger.Defer(_episode.Key, Now);
                End("ended");
            }

            return WorldPvpStep.None;
        }

        uint now = Now;
        if (_episode is null)
        {
            bool carrying = Carries(player, out SpellAuraHolder? tracesBefore);
            if (!carrying)
            {
                _carryFaultReported = false;
                _dropFlag = false;
            }

            if (carrying)
            {
                // A flag the bot gave up carrying is let go of, not taken up again: it never expires, so taking it up would start the same
                // failing run at every think.
                if (_dropFlag) TryDropFlag(player, now);
                if (_noAdopt && !Past(_noAdoptUntilMs, now)) return WorldPvpStep.None;
                if (player.MapId != SilithusCatalog.MapId) return WorldPvpStep.None;
                _noAdopt = false;
                _episode = new Episode { StartedMs = now, LastStepMs = now, Phase = Phase.Carry, Reported = _carryFaultReported };
                NoteCarryStart(_episode, tracesBefore);
            }
            else if (mayBegin && options.Enabled)
            {
                if (_cooling && !Past(_coolUntilMs, now)) return WorldPvpStep.None;
                if (_scanned && unchecked((int)(now - _nextScanMs)) < 0) return WorldPvpStep.None;
                _scanned = true;
                _nextScanMs = unchecked(now + ScanEveryMs);
                if (!Eligible(player) || player.Combat.IsInCombat || !Participates(player, options, now)) return WorldPvpStep.None;
                Begin(player, map, now, options);
                if (_episode is null) return WorldPvpStep.None;
            }
            else
            {
                return WorldPvpStep.None;
            }
        }

        return Step(player, map, elapsedMs, now, options);
    }

    /// <summary>
    /// The brain's stall watch gave the goal up (the walk made no progress): the run ends. A geyser run sets its geyser aside (a strike); a
    /// carry drops the flag and holds off new runs (<see cref="GiveUpCarry"/>), or the next think would take the same flag up again.
    /// </summary>
    internal void Stalled(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_episode is not { } episode) return;
        uint now = Now;
        if (episode.Phase == Phase.Carry)
        {
            GiveUpCarry(player, now);
        }
        else if (episode.Key != 0)
        {
            _ledger.Defer(episode.Key, now);
        }

        StartCooldown(now);
        End("stalled");
    }

    /// <summary>The bot died (the brain tells at the first think of the death): a run before the carry costs the geyser a strike.</summary>
    internal void BotDied()
    {
        if (_episode is null) return;
        if (_episode.Phase != Phase.Carry) _ledger.Defer(_episode.Key, Now);
        End("died");
    }

    private bool Participates(Player player, PlayerbotSilithystOptions options, uint now)
    {
        float chance = _options.Social.WarMode.Enabled ? options.WarModeParticipationChance : options.ParticipationChance;
        if (chance >= 1f) return true;
        if (chance <= 0f) return false;
        double roll = (PlayerbotContention.Mix(player.Guid.Value, 0x51117157UL ^ (ulong)(now / ParticipationWindowMs)) >> 11) * (1.0 / 9007199254740992.0);
        return roll < chance;
    }

    // --- seeking ----------------------------------------------------------------------------------------------------

    private void Begin(Player player, Map map, uint now, PlayerbotSilithystOptions options)
    {
        GeyserCache cache = CacheOf(map, now);
        GameObject? best = null;
        float nearest = float.MaxValue;
        Consider(cache.Geysers, map, player, now, options, ref best, ref nearest);
        Consider(cache.Mounds, map, player, now, options, ref best, ref nearest);
        if (best is not { } chosen) return;
        ulong key = chosen.Guid.Value;
        uint epoch = _claims.Join(key, player.Guid.Value, now);
        if (TooMany(key, player.Guid.Value, now, options))
        {
            // Two bots chose it in one tick: the first claimants keep it.
            _claims.Leave(key, epoch);
            return;
        }

        _episode = new Episode
        {
            Guid = chosen.Guid, Entry = chosen.Entry, At = new Vector3(chosen.X, chosen.Y, chosen.Z), StartedMs = now, LastStepMs = now,
            ClaimEpoch = epoch, Phase = Phase.Use,
        };
    }

    /// <summary>One dictionary lookup per guid (<see cref="Map.FindObject"/>; an object in an unloaded grid is skipped); no allocation.</summary>
    private void Consider(ObjectGuid[] guids, Map map, Player player, uint now, PlayerbotSilithystOptions options, ref GameObject? best, ref float nearest)
    {
        for (int i = 0; i < guids.Length; i++)
        {
            if (map.FindObject(guids[i]) is not GameObject go || go.Entry is not (GeyserEntry or MoundEntry) || !go.IsSpawned) continue;
            if (_ledger.IsDeferred(go.Guid.Value, now) || !IsReady(go)) continue;
            if (_claims.OthersOf(go.Guid.Value, mine: false, now) >= options.MaxBotsPerGeyser) continue;
            float dx = go.X - player.X, dy = go.Y - player.Y, dz = go.Z - player.Z;
            float distance = (dx * dx) + (dy * dy) + (dz * dz);
            if (distance < nearest)
            {
                nearest = distance;
                best = go;
            }
        }
    }

    /// <summary>
    /// The geyser and mound guids of <paramref name="map"/>, shared by the bots of the map. The geysers come from the spawn table
    /// (<see cref="GameObjectMapSystem.SpawnGuidsOf"/>): complete whichever grids are loaded, read again every <see cref="GeyserCacheRefreshMs"/>
    /// (a content reload). Mounds are not in the spawn table (the server puts them down at run time), so they come from the objects loaded now,
    /// scanned again every <see cref="MoundScanMs"/>: a mound in a grid that loads in between is seen at the next scan.
    /// </summary>
    private static GeyserCache CacheOf(Map map, uint now)
    {
        GeyserCache cache = GeyserCaches.GetValue(map, static _ => new GeyserCache());
        if (map.FindUpdater<GameObjectMapSystem>() is not { } objects) return cache;
        if (!cache.GeysersBuilt || unchecked((int)(now - cache.GeysersBuiltMs - GeyserCacheRefreshMs)) >= 0)
        {
            cache.GeysersBuilt = true;
            cache.GeysersBuiltMs = now;
            cache.Geysers = objects.SpawnGuidsOf(GeyserEntry);
        }

        if (!cache.MoundsBuilt || unchecked((int)(now - cache.MoundsBuiltMs - MoundScanMs)) >= 0)
        {
            cache.MoundsBuilt = true;
            cache.MoundsBuiltMs = now;
            var found = new List<ObjectGuid>();
            foreach (GameObject go in objects.GameObjects)
            {
                if (go.Entry == MoundEntry) found.Add(go.Guid);
            }

            cache.Mounds = [.. found];
        }

        return cache;
    }

    private bool TooMany(ulong key, ulong self, uint now, PlayerbotSilithystOptions options)
        => _claims.OthersOf(key, mine: true, now) >= options.MaxBotsPerGeyser && _claims.OwnerOf(key, now) != self;

    /// <summary>A geyser nobody has used and that has not cooled down: ready, not in use.</summary>
    private static bool IsReady(GameObject go) => go.LootState == GameObjectLootState.Ready && (go.Flags & GameObjectFlags.InUse) == 0;

    // --- the episode ------------------------------------------------------------------------------------------------

    private enum Phase : byte
    {
        Use,
        Await,
        Carry,
    }

    private sealed class Episode
    {
        public ObjectGuid Guid;
        public uint Entry;
        public Vector3 At;
        public uint StartedMs;
        public uint LastStepMs;
        public uint ClaimEpoch;
        public Phase Phase;
        public uint AwaitUntilMs;
        public uint CarryRunMs;
        public uint TracesGraceUntilMs;
        public int WalkFailures;
        public bool Reported;
        public bool Checked;
        public bool ExitFirst;
        public bool HasExit;
        public Vector3 ExitPoint;
        public int Entries;
        public SpellAuraHolder? TracesBefore;
        public int TracesBeforeDuration;

        public ulong Key => Guid.Value;
    }

    private WorldPvpStep Busy(Episode episode) => new(true, PlayerbotGoalKind.WorldPvp, episode.Phase == Phase.Carry ? 0u : episode.Entry);

    private WorldPvpStep Step(Player player, Map map, uint elapsedMs, uint now, PlayerbotSilithystOptions options)
    {
        Episode episode = _episode!;
        bool stale = unchecked(now - episode.LastStepMs) > StaleMs;
        episode.LastStepMs = now;
        if (player.Combat.IsInCombat) return WorldPvpStep.None; // the brain's combat path has the think; the walk goes on after it

        bool carries = Carries(player, out SpellAuraHolder? traces);
        if (carries && episode.Phase != Phase.Carry)
        {
            // The flag is in hand (the use landed, or another source gave it): the geyser's claim is not needed any more.
            Pickups++;
            if (episode.Key != 0) _claims.Leave(episode.Key, episode.ClaimEpoch);
            episode.ClaimEpoch = 0;
            episode.Phase = Phase.Carry;
            NoteCarryStart(episode, traces);
            _route = null;
        }

        return episode.Phase == Phase.Carry
            ? StepCarry(player, episode, carries, traces, elapsedMs, now)
            : StepUse(player, map, episode, stale, elapsedMs, now, options);
    }

    private WorldPvpStep StepUse(Player player, Map map, Episode episode, bool stale, uint elapsedMs, uint now, PlayerbotSilithystOptions options)
    {
        if (!options.Enabled) return Quit(episode, "disabled");
        if (stale) return Quit(episode, "stale");
        if (unchecked(now - episode.StartedMs) > UseMaxMs && episode.Phase == Phase.Use)
        {
            _ledger.Defer(episode.Key, now);
            return Quit(episode, "use-timeout", cooldown: true);
        }

        if (episode.Phase == Phase.Await)
        {
            if (unchecked((int)(episode.AwaitUntilMs - now)) > 0)
            {
                PlayerbotMovementControl.Stop(_session, player);
                return Busy(episode);
            }

            ReportAnomaly(player, episode, "silithyst-use-no-aura",
                $"used the {(episode.Entry == GeyserEntry ? "geyser" : "mound")} {episode.Entry} ({episode.Guid.Value:X}) and got no Silithyst (aura {SilithystFlag}) within {AwaitAuraMs} ms");
            _ledger.Defer(episode.Key, now);
            return Quit(episode, "no-aura", cooldown: true);
        }

        GameObject? go = map.FindObject(episode.Guid) as GameObject;
        if (go is null || !go.IsSpawned || !IsReady(go))
        {
            // Taken by someone else (or cooling down): left alone for a while, no strike.
            _ledger.SetAside(episode.Key, now, ContestedMs);
            return Quit(episode, "taken");
        }

        if (!_claims.Refresh(episode.Key, episode.ClaimEpoch, now)) episode.ClaimEpoch = _claims.Join(episode.Key, player.Guid.Value, now);
        if (TooMany(episode.Key, player.Guid.Value, now, options))
        {
            _ledger.SetAside(episode.Key, now, ContestedMs);
            return Quit(episode, "contested");
        }

        float dx = go.X - player.X, dy = go.Y - player.Y, dz = go.Z - player.Z;
        if ((dx * dx) + (dy * dy) + (dz * dz) > InteractYards * InteractYards)
        {
            switch (WalkTo(player, new Vector3(go.X, go.Y, go.Z), elapsedMs, now))
            {
                case WalkResult.Failed:
                    _ledger.Defer(episode.Key, now);
                    return Quit(episode, "no-route", cooldown: true);
                case WalkResult.Stuck when ++episode.WalkFailures >= MaxWalkFailures:
                    _ledger.Defer(episode.Key, now);
                    return Quit(episode, "no-route", cooldown: true);
            }

            return Busy(episode);
        }

        _route = null;
        if (!PlayerbotMovementControl.Stop(_session, player) || _session.ManagedBudget is not { Remaining: > 0 }) return Busy(episode);
        if (!_session.TryManagedAction(WorldOpcode.CmsgGameobjUse, PlayerbotNavigation.GuidPayload(go.Guid.Value))) return Busy(episode);
        episode.Phase = Phase.Await;
        episode.AwaitUntilMs = unchecked(now + AwaitAuraMs);
        return Busy(episode);
    }

    private static void NoteCarryStart(Episode episode, SpellAuraHolder? traces)
    {
        episode.CarryRunMs = 0;
        episode.TracesBefore = traces;
        episode.TracesBeforeDuration = traces?.Duration ?? 0;
    }

    /// <summary>Traces of Silithyst was put on (or renewed) since the carry began: this turn-in gave it, not an earlier one still running.</summary>
    private static bool TurnedIn(Episode episode, SpellAuraHolder? traces)
        => traces is not null
            && (episode.TracesBefore is null || !ReferenceEquals(traces, episode.TracesBefore) || traces.Duration > episode.TracesBeforeDuration);

    private WorldPvpStep StepCarry(Player player, Episode episode, bool carries, SpellAuraHolder? traces, uint elapsedMs, uint now)
    {
        AreaTriggerTemplate? trigger = WorldMaps.Of(_session.World).FindAreaTrigger(
            player.Team == Team.Alliance ? SilithusCatalog.AreaTriggerAlliance : SilithusCatalog.AreaTriggerHorde);
        if (trigger is null || trigger.MapId != player.MapId)
        {
            ReportAnomaly(player, episode, "silithyst-unreachable-trigger", "the turn-in area trigger is not in the loaded world data");
            return GiveUp(player, episode, "no-trigger", now, carries);
        }

        var centre = new Vector3(trigger.X, trigger.Y, trigger.Z);
        float dx = trigger.X - player.X, dy = trigger.Y - player.Y, dz = trigger.Z - player.Z;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        float volume = trigger.Radius > 0f ? trigger.Radius : ArrivedYards;

        if (!carries)
        {
            if (distance > trigger.Radius + DeliveryMarginYards)
            {
                // The flag went early (dropped, removed): not a fault and not a turn-in.
                if (episode.Key != 0) _ledger.SetAside(episode.Key, now, DeliveredRecheckMs);
                return Quit(episode, "lost", cooldown: true);
            }

            if (TurnedIn(episode, traces))
            {
                Deliveries++;
                if (episode.Key != 0) _ledger.Complete(episode.Key, now, DeliveredRecheckMs);
                return Quit(episode, "delivered", cooldown: true);
            }

            if (episode.TracesGraceUntilMs == 0) episode.TracesGraceUntilMs = unchecked(now + TracesGraceMs);
            if (unchecked((int)(episode.TracesGraceUntilMs - now)) > 0) return Busy(episode);
            if (episode.Key != 0) _ledger.SetAside(episode.Key, now, DeliveredRecheckMs);
            return Quit(episode, "lost", cooldown: true);
        }

        episode.TracesGraceUntilMs = 0;
        // Only the time the carry rung runs counts: rest, fights and higher goals hold the bot without it being slow.
        episode.CarryRunMs = episode.CarryRunMs > uint.MaxValue - elapsedMs ? uint.MaxValue : episode.CarryRunMs + elapsedMs;
        if (episode.CarryRunMs > CarryMaxMs)
        {
            ReportAnomaly(player, episode, "silithyst-carry-timeout",
                $"carried the Silithyst for {CarryMaxMs} ms without turning it in ({distance:F0} yards from trigger {trigger.Id})");
            return GiveUp(player, episode, "carry-timeout", now, carries);
        }

        // A mount-up cast under way would drop the dust when it lands (the server drops it on the mount aura); a mount already on is taken off.
        PlayerbotMount.CancelPendingMountCast(_session, player);
        if (MountService.IsMounted(player)) PlayerbotMount.Dismount(_session, player);

        if (!episode.Checked)
        {
            // The trigger fires on entering its volume: a flag picked up inside it needs a walk out and back in.
            episode.Checked = true;
            episode.ExitFirst = distance <= volume;
        }

        if (episode.ExitFirst && distance >= volume + ExitMarginYards - 1f) episode.ExitFirst = false; // at the exit point: the walk back in plans from there
        if (!episode.ExitFirst && distance <= ArrivedYards)
        {
            // At the centre with the flag still on: the entry did not turn it in. Out and in again, a few times, then it is reported.
            PlayerbotMovementControl.Stop(_session, player);
            if (++episode.Entries > MaxEntries)
            {
                ReportAnomaly(player, episode, "silithyst-unreachable-trigger",
                    $"walked into the turn-in trigger {trigger.Id} {MaxEntries + 1} times and still holds the Silithyst");
                return GiveUp(player, episode, "no-turn-in", now, carries);
            }

            episode.ExitFirst = true;
            episode.HasExit = false;
            _route = null;
            return Busy(episode);
        }

        Vector3 goal = centre;
        if (episode.ExitFirst)
        {
            if (!episode.HasExit)
            {
                Vector3 away = new(player.X - trigger.X, player.Y - trigger.Y, 0f);
                away = away.LengthSquared() < 0.0001f ? Vector3.UnitX : Vector3.Normalize(away);
                episode.ExitPoint = centre + (away * (volume + ExitMarginYards));
                episode.HasExit = true;
            }

            goal = episode.ExitPoint;
        }

        // Out and in again between the same two places is what the motion's loop guard refuses and blacklists; here it is the plan.
        if (episode.Entries > 0 || episode.ExitFirst) PlayerbotMotion.Unblacklist(player, goal);
        switch (WalkTo(player, goal, elapsedMs, now))
        {
            case WalkResult.Failed:
                ReportAnomaly(player, episode, "silithyst-unreachable-trigger", episode.ExitFirst
                    ? $"no route out of the turn-in trigger {trigger.Id} volume to walk back in ({distance:F0} yards from its centre)"
                    : $"no route to the turn-in trigger {trigger.Id} ({distance:F0} yards away)");
                return GiveUp(player, episode, episode.ExitFirst ? "no-exit" : "no-route", now, carries);
            case WalkResult.Stuck when ++episode.WalkFailures >= MaxWalkFailures:
                ReportAnomaly(player, episode, "silithyst-unreachable-trigger", episode.ExitFirst
                    ? $"the walk out of the turn-in trigger {trigger.Id} volume stopped {MaxWalkFailures} times ({distance:F0} yards from its centre)"
                    : $"the walk to the turn-in trigger {trigger.Id} stopped {MaxWalkFailures} times ({distance:F0} yards away)");
                return GiveUp(player, episode, episode.ExitFirst ? "no-exit" : "no-route", now, carries);
        }

        return Busy(episode);
    }

    /// <summary>Gives the carry up: the geyser is set aside, the flag (if still held) is let go of, no new run starts for a while.</summary>
    private WorldPvpStep GiveUp(Player player, Episode episode, string outcome, uint now, bool carries)
    {
        if (carries) GiveUpCarry(player, now);
        return Quit(episode, outcome, defer: true, cooldown: true);
    }

    /// <summary>
    /// The bot holds a flag it can no longer carry. The aura never expires, so the next think would take it up and start the same run again:
    /// cancel it (CMSG_CANCEL_AURA, retried by <see cref="Update"/> for <see cref="MaxDropAttempts"/> tries) and do not take the flag up again
    /// for <see cref="GiveUpBackoffMs"/> whether or not it went.
    /// </summary>
    private void GiveUpCarry(Player player, uint now)
    {
        if (!Carries(player, out _)) return;
        _noAdopt = true;
        _noAdoptUntilMs = unchecked(now + GiveUpBackoffMs);
        _dropFlag = true;
        _dropAttempts = 0;
        _dropNextMs = now;
        TryDropFlag(player, now);
    }

    private void TryDropFlag(Player player, uint now)
    {
        if (_dropAttempts >= MaxDropAttempts)
        {
            _dropFlag = false;
            return;
        }

        if (!Past(_dropNextMs, now) || _session.ManagedBudget is { Remaining: <= 0 }) return;
        if (!_session.TryManagedAction(WorldOpcode.CmsgCancelAura, BitConverter.GetBytes(SilithystFlag))) return;
        _dropAttempts++;
        _dropNextMs = unchecked(now + DropRetryMs);
    }

    private void StartCooldown(uint now)
    {
        _cooling = true;
        _coolUntilMs = unchecked(now + RunCooldownMs);
    }

    // --- walking ----------------------------------------------------------------------------------------------------

    private enum WalkResult : byte
    {
        Moving,
        Waiting,
        Stuck,
        Failed,
    }

    private WalkResult WalkTo(Player player, Vector3 goal, uint elapsedMs, uint now)
    {
        if (_route is { Complete: true }) _route = null;
        if (_route is null || Vector3.DistanceSquared(_routeGoal, goal) > 1f)
        {
            PlanOutcome planned = PlayerbotNavigation.PlanToward(player, goal, _options, ref _tileWait, now, out _route);
            if (planned == PlanOutcome.Pending) return WalkResult.Waiting;
            if (planned == PlanOutcome.Refused || _route is null)
            {
                _route = null;
                return WalkResult.Failed;
            }

            _routeGoal = goal;
        }

        if (PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, now)) return WalkResult.Moving;
        bool finishedLeg = _route.Complete;
        _route = null;
        return finishedLeg ? WalkResult.Moving : WalkResult.Stuck;
    }

    // --- ending -----------------------------------------------------------------------------------------------------

    private void ReportAnomaly(Player player, Episode episode, string code, string detail)
    {
        if (episode.Reported || _session.IsInChaosProbe) return;
        episode.Reported = true;
        if (episode.Phase == Phase.Carry) _carryFaultReported = true; // the same flag taken up again later does not report the same fault again
        PlayerbotAnomalies.Report(player, "world-pvp", code, detail);
    }

    private WorldPvpStep Quit(Episode episode, string outcome, bool defer = false, bool cooldown = false)
    {
        if (defer && episode.Key != 0) _ledger.Defer(episode.Key, Now);
        if (cooldown) StartCooldown(Now);
        End(outcome);
        return WorldPvpStep.None;
    }

    private void End(string outcome)
    {
        if (_episode is { } episode)
        {
            if (episode.Key != 0 && episode.ClaimEpoch != 0) _claims.Leave(episode.Key, episode.ClaimEpoch);
            LastOutcome = outcome;
        }

        _episode = null;
        _route = null;
        _routeGoal = default;
        _tileWait = default;
    }
}
