using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;

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

    internal PlayerbotAsyncNavigationPlan(uint mapId, Vector3 start, NavMeshDatasetDescriptor? dataset = null)
    {
        _mapId = mapId;
        _start = start;
        _dataset = dataset;
    }
    internal static PlayerbotAsyncNavigationPlan? Current => t_current;
    internal Vector3 Start => _start;
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
        if (Exhausted || request.MapId != _mapId || result.Status != PlayerbotNavigationResultStatus.Completed
            || result.Path is null || !Finite(result.Path.Points)) return false;
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
            return false;
        }
        _bytes = retained;
        _answers[key] = result;
        if (_next is { } next && Key(next.Start, result.QueryDestination,
            request.MaxRouteYards > 0 ? next.Options with { MaxSearchNodes = PlayerbotNavigation.SearchNodes(next.Start, result.QueryDestination) } : next.Options) == key)
            _next = null;
        return true;
    }

    internal bool TryGet(Vector3 from, Vector3 to, PathOptions options, out PlayerbotNavigationResult result)
    {
        if (_answers.TryGetValue(Key(from, to, options), out result!)) return true;
        if (_next is null && !Exhausted && from.IsFinite() && to.IsFinite())
            _next = new PlayerbotNavigationRequest(_mapId, from, to, options with { CancellationToken = default, LoadTiles = options.LoadTiles },
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

internal static class Vector3AsyncPlanExtensions
{
    internal static bool IsFinite(this Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
