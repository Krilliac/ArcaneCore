using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.DebugDraw;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Gm.DebugDraw;

/// <summary>A marker a command asks for: what it shows, where, the text a click on it prints, and whether it gets a glow companion.</summary>
public readonly record struct DebugMarkerRequest(DebugMarkerKind Kind, Vector3 Position, string Label, bool Emphasis = false);

/// <summary>What <see cref="DebugDrawFeature.Draw"/> did: markers sent (glows included), requests left out by the cap, and older markers removed to make room.</summary>
public readonly record struct DebugDrawResult(int Placed, int Dropped, int Evicted);

/// <summary>
/// The <c>.debug vis</c> markers of every GM (docs/areas/debug-draw.md). A drawing is a batch of client-only game objects
/// (<see cref="DebugMarkerPackets"/>): created in the GM's client alone, never in a map, never saved. This feature remembers them per GM so
/// they can be removed: by <c>.debug vis clear</c>, when their lifetime ends (<see cref="DebugDrawOptions.LifetimeSeconds"/>, checked on the
/// world tick), when the GM logs out, and (forgotten without a packet, because the client already dropped them) when the GM changes map.
/// At most <see cref="DebugDrawOptions.MaxMarkersPerGm"/> markers per GM: a new drawing removes the oldest drawings to fit.
/// <para>Thread affinity: world thread (commands, the tick, logout, the use handler).</para>
/// </summary>
public sealed class DebugDrawFeature(IServiceProvider services) : IWorldFeature
{
    private readonly Dictionary<ObjectGuid, MarkerSet> _sets = [];
    private WorldRuntime? _world;
    private uint _counter;

    public DebugDrawOptions Options { get; private set; } = new();

    private DebugMarkerStyle[] _styles = [.. DebugMarkerStyles.All];

    /// <summary>The marker look of every kind this server draws with (built-in models plus validated overrides, <see cref="DebugMarkerModels"/>).</summary>
    public IReadOnlyList<DebugMarkerStyle> Styles => _styles;

    /// <summary>The look of <paramref name="kind"/> (<see cref="Styles"/>).</summary>
    public DebugMarkerStyle StyleOf(DebugMarkerKind kind) => (int)kind < _styles.Length ? _styles[(int)kind] : _styles[0];

    /// <summary>GMs that currently have markers.</summary>
    public int ActiveGmCount => _sets.Count;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        Options = DebugDrawOptions.Bind(services.GetService<IConfiguration>());
        _styles = DebugMarkerModels.Resolve(Options, services.GetService<ILogger<DebugDrawFeature>>() ?? (ILogger)NullLogger.Instance);
        world.WorldTick += OnTick;
        world.PlayerLoggingOut += OnLoggingOut;
    }

    /// <summary>The markers <paramref name="gm"/>'s client holds now (glow companions included).</summary>
    public int MarkerCount(Player gm) => _sets.TryGetValue(gm.Guid, out MarkerSet? set) ? set.Count : 0;

    /// <summary>The GUIDs of <paramref name="gm"/>'s markers, oldest first.</summary>
    public IReadOnlyList<ObjectGuid> MarkerGuids(Player gm)
        => _sets.TryGetValue(gm.Guid, out MarkerSet? set) ? [.. set.Batches.SelectMany(b => b.Markers).Select(m => m.Guid)] : [];

    /// <summary>The drawings of <paramref name="gm"/>, oldest first: title, marker count, seconds left.</summary>
    public IReadOnlyList<(string Title, int Markers, int SecondsLeft)> Drawings(Player gm)
    {
        if (!_sets.TryGetValue(gm.Guid, out MarkerSet? set))
        {
            return [];
        }

        uint now = Now;
        return [.. set.Batches.Select(b => (b.Title, b.Markers.Count, (int)Math.Max(0, ((long)LifetimeMs - unchecked(now - b.CreatedMs)) / 1000)))];
    }

    /// <summary>
    /// Send a drawing to <paramref name="gm"/>'s client: one create per request (and a glow companion for an emphasised one when
    /// <see cref="DebugDrawOptions.Glow"/> is on and its kind has a glow). Requests beyond the cap are dropped (the earlier ones are kept,
    /// so a command lists its key markers first); older drawings are removed until the new one fits.
    /// </summary>
    public DebugDrawResult Draw(Player gm, string title, IReadOnlyList<DebugMarkerRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(gm);
        ArgumentNullException.ThrowIfNull(requests);
        if (gm.Map is not { } map || requests.Count == 0)
        {
            return new DebugDrawResult(0, requests.Count, 0);
        }

        int max = Options.MaxMarkersPerGm;
        var batch = new Batch(title, Now);
        int accepted = 0;
        foreach (DebugMarkerRequest request in requests)
        {
            DebugMarkerStyle style = StyleOf(request.Kind);
            bool glow = request.Emphasis && Options.Glow && style.GlowDisplayId != 0;
            int cost = glow ? 2 : 1;
            if (batch.Markers.Count + cost > max)
            {
                break;
            }

            accepted++;
            ObjectGuid guid = DebugMarkerPackets.MarkerGuid(request.Kind, NextCounter());
            batch.Markers.Add(new DebugMarker(guid, request.Kind, request.Position, style.DisplayId));
            batch.Labels[guid] = request.Label;
            if (glow)
            {
                // The glow is a particle model: it cannot be hovered or clicked, so it carries no label of its own.
                batch.Markers.Add(new DebugMarker(DebugMarkerPackets.MarkerGuid(request.Kind, NextCounter()), request.Kind, request.Position, style.GlowDisplayId));
            }
        }

        if (batch.Markers.Count == 0)
        {
            return new DebugDrawResult(0, requests.Count, 0);
        }

        if (!_sets.TryGetValue(gm.Guid, out MarkerSet? set) || !ReferenceEquals(set.Map, map))
        {
            // Markers of another map are already gone from the client (a far teleport rebuilds its world).
            set = new MarkerSet(map);
            _sets[gm.Guid] = set;
        }

        int evicted = 0;
        while (set.Count + batch.Markers.Count > max && set.Batches.Count > 0)
        {
            Batch oldest = set.Batches[0];
            evicted += oldest.Markers.Count;
            Remove(gm, set, oldest, sendDestroy: true);
        }

        set.Batches.Add(batch);
        set.Count += batch.Markers.Count;
        DebugMarkerPackets.SendCreates(gm, batch.Markers, (opcode, payload) => gm.Session.Send(opcode, payload),
            _world?.Options.UpdateCompressionThreshold ?? 128, Now);
        return new DebugDrawResult(batch.Markers.Count, requests.Count - accepted, evicted);
    }

    /// <summary>Remove every marker of <paramref name="gm"/> from its client. Returns how many were removed.</summary>
    public int Clear(Player gm)
    {
        ArgumentNullException.ThrowIfNull(gm);
        if (!_sets.Remove(gm.Guid, out MarkerSet? set))
        {
            return 0;
        }

        int removed = set.Count;
        foreach (Batch batch in set.Batches)
        {
            SendDestroys(gm, batch);
        }

        return removed;
    }

    /// <summary>
    /// A client used (right-clicked) a marker: print its label to that GM. Returns false when the GUID is not one of that GM's live markers
    /// (another handler may own it).
    /// </summary>
    public bool OnMarkerUsed(Player gm, ObjectGuid guid)
    {
        if (!DebugMarkerStyles.IsMarkerGuid(guid))
        {
            return false;
        }

        // Always swallow a marker GUID: no real object carries a marker entry.
        if (_sets.TryGetValue(gm.Guid, out MarkerSet? set))
        {
            foreach (Batch batch in set.Batches)
            {
                if (batch.Labels.TryGetValue(guid, out string? label))
                {
                    gm.Session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(
                        string.Create(CultureInfo.InvariantCulture, $"[{batch.Title}] {label}")));
                    break;
                }
            }
        }

        return true;
    }

    private uint LifetimeMs => (uint)Options.LifetimeSeconds * 1000u;

    private uint Now => _world?.NowMs ?? 0;

    private uint NextCounter()
    {
        _counter = (_counter + 1) & 0x00FFFFFF;
        if (_counter == 0)
        {
            _counter = 1;
        }

        return _counter;
    }

    private void OnTick(uint diffMs)
    {
        if (_sets.Count == 0 || _world is not { } world)
        {
            return;
        }

        uint now = world.NowMs;
        foreach (ObjectGuid owner in _sets.Keys.ToArray())
        {
            MarkerSet set = _sets[owner];
            Player? gm = world.FindOnlinePlayer(owner);
            if (gm is null || !ReferenceEquals(gm.Map, set.Map))
            {
                // Gone from the world or moved to another map: the client no longer has these objects.
                _sets.Remove(owner);
                continue;
            }

            while (set.Batches.Count > 0 && unchecked(now - set.Batches[0].CreatedMs) >= LifetimeMs)
            {
                Remove(gm, set, set.Batches[0], sendDestroy: true);
            }

            if (set.Batches.Count == 0)
            {
                _sets.Remove(owner);
            }
        }
    }

    private void OnLoggingOut(Player player)
    {
        if (_sets.ContainsKey(player.Guid))
        {
            Clear(player);
        }
    }

    private static void Remove(Player gm, MarkerSet set, Batch batch, bool sendDestroy)
    {
        set.Batches.Remove(batch);
        set.Count -= batch.Markers.Count;
        if (sendDestroy)
        {
            SendDestroys(gm, batch);
        }
    }

    private static void SendDestroys(Player gm, Batch batch)
    {
        foreach (DebugMarker marker in batch.Markers)
        {
            gm.Session.Send(WorldOpcode.SmsgDestroyObject, DebugMarkerPackets.Destroy(marker.Guid));
        }
    }

    private sealed class MarkerSet(Map map)
    {
        public Map Map { get; } = map;

        public List<Batch> Batches { get; } = [];

        public int Count { get; set; }
    }

    private sealed class Batch(string title, uint createdMs)
    {
        public string Title { get; } = title;

        public uint CreatedMs { get; } = createdMs;

        public List<DebugMarker> Markers { get; } = [];

        public Dictionary<ObjectGuid, string> Labels { get; } = [];
    }
}
