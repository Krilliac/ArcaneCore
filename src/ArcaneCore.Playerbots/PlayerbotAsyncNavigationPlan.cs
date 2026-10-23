using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>Owner-thread cache for a detached series of navigation queries.</summary>
internal sealed class PlayerbotAsyncNavigationPlan
{
    internal const int MaxQueries = 64;
    internal const int MaxRetainedBytes = 2 * 1024 * 1024;
    [ThreadStatic] private static PlayerbotAsyncNavigationPlan? t_current;

    private readonly uint _mapId;
    private readonly Vector3 _start;
    private readonly NavMeshDatasetDescriptor? _dataset;
    private readonly Dictionary<QueryKey, PlayerbotNavigationResult> _answers = [];
    private PlayerbotNavigationRequest? _next;
    private int _bytes;

    internal PlayerbotAsyncNavigationPlan(uint mapId, Vector3 start, NavMeshDatasetDescriptor? dataset = null,
        Player? owner = null, Vector3? goal = null)
    {
        _mapId = mapId;
        _start = start;
        _dataset = dataset;
        Owner = owner;
        Goal = goal;
        PlayerbotNavigationExecutor.AttachCurrentLease(this);
    }

    internal static PlayerbotAsyncNavigationPlan? Current => t_current;
    internal uint MapId => _mapId;
    internal Vector3 Start => _start;
    internal NavMeshDatasetDescriptor? Dataset => _dataset;
    internal Player? Owner { get; set; }
    internal Vector3? Goal { get; set; }
    internal bool Completed { get; set; }
    internal bool ReportedOutcome { get; set; }
    internal PlayerbotNavigationRequest? NextRequest => _next;
    internal int RetainedBytes => _bytes;
    internal int QueryCount => _answers.Count;
    internal bool Exhausted { get; private set; }
    internal bool Deferred { get; private set; }

    internal IDisposable Activate()
    {
        PlayerbotAsyncNavigationPlan? previous = t_current;
        t_current = this;
        return new Scope(previous);
    }

    internal bool AddResult(PlayerbotNavigationRequest request, PlayerbotNavigationResult result)
    {
        if (request.MapId != _mapId)
        {
            ReportStale($"result map {request.MapId} != plan map {_mapId}");
            return false;
        }

        if (Exhausted)
            return false;

        if (result.Status != PlayerbotNavigationResultStatus.Completed || result.Path is null || !Finite(result.Path.Points))
        {
            ReportDropped($"query status={result.Status}, hasPath={result.Path is not null} toward ({result.QueryDestination.X:F1},{result.QueryDestination.Y:F1},{result.QueryDestination.Z:F1})");
            return false;
        }

        PathOptions keyOptions = request.MaxRouteYards > 0
            ? request.Options with { MaxSearchNodes = PlayerbotNavigation.SearchNodes(request.Start, result.QueryDestination) }
            : request.Options;
        QueryKey key = Key(request.Start, result.QueryDestination, keyOptions);
        int bytes = result.Path.Points.Count * (sizeof(float) * 3) + 96;
        bool replacing = _answers.TryGetValue(key, out PlayerbotNavigationResult? previous);
        int replacedBytes = previous?.Path is { } oldPath ? oldPath.Points.Count * (sizeof(float) * 3) + 96 : 0;
        int retained = _bytes - replacedBytes + bytes;
        if ((!replacing && _answers.Count >= MaxQueries) || retained > MaxRetainedBytes)
        {
            Exhausted = true;
            ReportDropped($"cache exhausted (queries={_answers.Count}/{MaxQueries}, bytes={retained}/{MaxRetainedBytes})");
            return false;
        }
        _bytes = retained;
        _answers[key] = result;
        if (_next is { } next && Key(next.Start, result.QueryDestination,
            request.MaxRouteYards > 0 ? next.Options with { MaxSearchNodes = PlayerbotNavigation.SearchNodes(next.Start, result.QueryDestination) } : next.Options) == key)
            _next = null;
        return true;
    }

    internal void ReportStale(string reason, AnomalySeverity severity = AnomalySeverity.Info)
    {
        if (ReportedOutcome || Owner is not { IsInWorld: true } player) return;
        if (player.Session is WorldSession { IsInChaosProbe: true }) return;
        ReportedOutcome = true;
        Vector3 goal = Goal ?? default;
        PlayerbotAnomalies.Report(player, "nav", "plan-stale",
            string.Create(CultureInfo.InvariantCulture,
                $"async plan from ({_start.X:F1},{_start.Y:F1},{_start.Z:F1}) toward ({goal.X:F1},{goal.Y:F1},{goal.Z:F1}) on map {_mapId} stale: {reason}"),
            severity);
    }

    internal void ReportDropped(string reason, AnomalySeverity severity = AnomalySeverity.Warning)
    {
        if (ReportedOutcome || Owner is not { IsInWorld: true } player) return;
        if (player.Session is WorldSession { IsInChaosProbe: true }) return;
        ReportedOutcome = true;
        Vector3 goal = Goal ?? default;
        PlayerbotAnomalies.Report(player, "nav", "plan-dropped",
            string.Create(CultureInfo.InvariantCulture,
                $"async plan from ({_start.X:F1},{_start.Y:F1},{_start.Z:F1}) toward ({goal.X:F1},{goal.Y:F1},{goal.Z:F1}) on map {_mapId} dropped: {reason}"),
            severity);
    }

    internal void OnLeaseReleased(bool executorDisposed)
    {
        if (Completed || ReportedOutcome || executorDisposed || Owner is not { IsInWorld: true } player) return;
        if (player.Session is WorldSession { IsInChaosProbe: true }) return;
        if (player.Map is not { } map || map.MapId != _mapId)
        {
            ReportStale($"bot moved to map {player.Map?.MapId.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            return;
        }

        if (!player.IsAlive)
        {
            ReportStale("bot died before async plan completed");
            return;
        }

        if (_dataset is { } ds && map.Collision.Pathfinder is NavMeshPathfinder nav && nav.Dataset != ds)
        {
            ReportStale("navmesh dataset changed while plan was in flight");
            return;
        }

        Vector3 current = PlayerbotMotion.CurrentPosition(player);
        if (Vector3.DistanceSquared(current, _start) > 1f)
        {
            ReportStale(string.Create(CultureInfo.InvariantCulture,
                $"bot moved {Vector3.Distance(current, _start):F1} yd from plan start"));
            return;
        }

        ReportDropped(Exhausted ? "plan exhausted query/byte cap" : "plan abandoned before completion");
    }

    internal bool TryGet(Vector3 from, Vector3 to, PathOptions options, out PlayerbotNavigationResult result)
    {
        if (_answers.TryGetValue(Key(from, to, options), out result!)) return true;
        if (_next is null && !Exhausted && from.IsFinite() && to.IsFinite())
            _next = new PlayerbotNavigationRequest(_mapId, from, to, options with { CancellationToken = default, LoadTiles = options.LoadTiles, NonBlockingTiles = false },
                MaxRouteYards: 0, MaxPathPoints: options.MaxPoints, Dataset: _dataset);
        Deferred = true;
        result = null!;
        return false;
    }

    internal void ResetPass() => Deferred = false;

    private static QueryKey Key(Vector3 from, Vector3 to, PathOptions o)
        => new(from, to, o.MaxPoints, o.MaxSearchNodes, o.EffectiveIncludeFlags, o.ExcludeFlags, o.AllowPartial, o.LoadTiles);

    private static bool Finite(IReadOnlyList<Vector3> points) => points.Count >= 1 && points.All(static p => p.IsFinite());
    private readonly record struct QueryKey(Vector3 From, Vector3 To, int MaxPoints, int MaxSearchNodes,
        NavTerrain Include, NavTerrain Exclude, bool Partial, bool LoadTiles);
    private sealed class Scope(PlayerbotAsyncNavigationPlan? previous) : IDisposable
    {
        public void Dispose() => t_current = previous;
    }
}
