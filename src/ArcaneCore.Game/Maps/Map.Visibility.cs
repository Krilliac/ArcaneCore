using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

public sealed partial class Map
{
    /// <summary>Tests only: build visibility candidates by collecting and sorting (the pre-wave-18 path) instead of merging.</summary>
    internal bool UseReferenceVisibilityOrder { get; set; }
    private bool _visibilityScratchInUse;

    /// <summary>
    /// Attach a per-viewer visibility veto, consulted by every visibility evaluation after the range check
    /// (vmangos <c>WorldObject::IsVisibleForInState</c>, the stealth/invisibility part). Docs: docs/integration/rogue-stealth-core.md.
    /// </summary>
    public void AddVisibilityRule(IVisibilityRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        _visibilityRules.Add(rule);
    }

    /// <summary>
    /// Re-evaluate visibility of <paramref name="obj"/> against everything near it at once (vmangos <c>WorldObject::UpdateObjectVisibility</c> /
    /// <c>Unit::UpdateVisibilityAndView</c>, called when a unit's stealth state changes). A player is evaluated in both directions.
    /// </summary>
    public void RefreshVisibility(WorldObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        EnsureWorldThread();
        if (!ReferenceEquals(obj.Map, this))
        {
            return;
        }

        if (obj is Player player)
        {
            UpdateVisibility(player);
        }
        else
        {
            UpdateObjectVisibility(obj);
        }
    }

    /// <summary>
    /// One viewer/target visibility evaluation in detect mode (vmangos <c>IsVisibleForOrDetect(.., detect = true)</c> as run by
    /// <c>Player::HandleStealthedUnitsDetection</c>): the rules may now reveal a unit the regular movement-driven updates keep hidden.
    /// </summary>
    public void UpdateVisibilityWithDetection(Player viewer, WorldObject target)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(target);
        EnsureWorldThread();
        _detecting = true;
        try
        {
            UpdateVisibilityOf(viewer, target);
        }
        finally
        {
            _detecting = false;
        }
    }

    /// <summary>The cell-query bound around <paramref name="o"/>: view range, grey distance, its bounding radius and the largest in the grid (added left to right).</summary>
    private float VisibilityQueryRadius(WorldObject o) => VisibilityRange + VisibilityGreyDistance + o.BoundingRadius + _grid.MaxBoundingRadius;

    /// <summary>
    /// vmangos <c>WorldObject::IsWithinVisibilityDistanceOf</c> for two players on the same
    /// map (no transport, not taxi-flying): a 2D distance check (<c>IsWithinDistInMap(...,
    /// is3D = false)</c>) against the map visibility distance, plus the grey distance when the
    /// target is already visible, plus both bounding radii (<c>SizeFactor::BoundingRadius</c>),
    /// with a strict <c>&lt;</c> comparison (<c>WorldObject::IsWithinDist</c>).
    /// </summary>
    public static bool IsWithinVisibilityDistance(WorldObject viewer, WorldObject target, bool alreadyVisible)
    {
        float dx = viewer.X - target.X;
        float dy = viewer.Y - target.Y;
        float maxDist = VisibilityRange
            + (alreadyVisible ? VisibilityGreyDistance : 0.0f)
            + viewer.BoundingRadius
            + target.BoundingRadius;
        return (dx * dx) + (dy * dy) < maxDist * maxDist;
    }

    /// <summary>The players whose clients currently have <paramref name="obj"/> (the inverse of their visible sets).</summary>
    internal IReadOnlyCollection<Player> ObserversOf(WorldObject obj) =>
        _observers.TryGetValue(obj.Guid, out HashSet<Player>? observers) ? observers : [];

    /// <summary>
    /// Send <paramref name="obj"/> again to every player that has it: an out-of-range entry and a fresh create block in the same update
    /// (the packet writes the out-of-range list first), so the client rebuilds the object from its current state. The visible sets do not
    /// change. Players whose session reads no update objects (bots) are skipped. Returns the players it was sent to, in join order.
    /// World thread only.
    /// </summary>
    internal List<Player> ResendToObservers(WorldObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        EnsureWorldThread();
        var sent = new List<Player>();
        if (!ReferenceEquals(obj.Map, this) || !_observers.TryGetValue(obj.Guid, out HashSet<Player>? observers))
        {
            return sent;
        }

        sent.AddRange(observers);
        SortByJoinOrderDistinct(sent);
        sent.RemoveAll(viewer => !viewer.Session.WantsUpdateObjects);
        foreach (Player viewer in sent)
        {
            viewer.PendingUpdates.AddOutOfRange(obj.Guid);
            PacketWriter block = viewer.PendingUpdates.BeginBlock();
            UpdateBlockWriter.WriteCreateBlock(block, obj, viewer, _newObjects.Contains(obj.Guid), _world.NowMs);
            viewer.PendingUpdates.EndBlock();
        }

        return sent;
    }

    /// <summary>
    /// Sort candidates into map-join order and drop repeats. <see cref="WorldObject.MapSequence"/> is unique per
    /// object of a map (a fresh number on every join), so equal entries are adjacent after the sort.
    /// </summary>
    private static void SortByJoinOrderDistinct<T>(List<T> list)
        where T : WorldObject
    {
        if (list.Count < 2)
        {
            return;
        }

        list.Sort(static (a, b) => a.MapSequence.CompareTo(b.MapSequence));
        int write = 1;
        for (int read = 1; read < list.Count; read++)
        {
            if (!ReferenceEquals(list[read], list[write - 1]))
            {
                list[write++] = list[read];
            }
        }

        list.RemoveRange(write, list.Count - write);
    }

    /// <summary>
    /// The players whose view of the non-player object <paramref name="obj"/> could change: players in the cells
    /// within visibility range (+ grey distance + both radii) and whoever sees it now, in map-join order. These are
    /// exactly the players among <see cref="VisibilityCandidates"/>, found without walking the cells' creatures and
    /// game objects — vmangos <c>Map::UpdateObjectVisibility</c> (Map.cpp) visits only the
    /// <c>WorldTypeMapContainer</c> (players' cameras) with its <c>VisibleChangesNotifier</c>.
    /// </summary>
    internal List<Player> ObjectVisibilityViewers(WorldObject obj)
    {
        // One scratch list for the per-mover pass; a nested call (a visibility rule refreshing another object)
        // gets its own list.
        List<Player> viewers = _viewerScratchInUse ? [] : _viewerScratch;
        viewers.Clear();
        float radius = VisibilityQueryRadius(obj);
        if (!UseReferenceVisibilityOrder)
        {
            // The join-ordered cells merged, then the few viewers outside them merged in: the same list, no sort.
            List<Player> extras = _viewerExtras;
            extras.Clear();
            if (_grid.TryCollectPlayersInJoinOrder(obj.X, obj.Y, radius, viewers, extras, out CellArea area))
            {
                if (_observers.TryGetValue(obj.Guid, out HashSet<Player>? watching))
                {
                    foreach (Player observer in watching)
                    {
                        if (!_grid.IsCollectedIn(observer, area))
                        {
                            extras.Add(observer);
                        }
                    }
                }

                SortByJoinOrderDistinct(extras);
                JoinOrder.MergeDistinct(viewers, extras, _viewerMergeScratch);
                extras.Clear();
                return viewers;
            }
        }

        _grid.CollectPlayers(obj.X, obj.Y, radius, viewers);
        if (_observers.TryGetValue(obj.Guid, out HashSet<Player>? observers))
        {
            viewers.AddRange(observers);
        }

        SortByJoinOrderDistinct(viewers);
        return viewers;
    }

    /// <summary>
    /// Everything whose visibility relation with <paramref name="center"/> could change: objects
    /// in the cells within visibility range (+ grey distance + both radii), whatever it sees,
    /// and whoever sees it. Anything else is out of range in both directions and invisible in
    /// both, so evaluating it would change nothing.
    /// </summary>
    internal void VisibilityCandidates(WorldObject center, List<WorldObject> candidates)
    {
        float radius = VisibilityQueryRadius(center);
        List<WorldObject> extras = _candidateExtras;
        extras.Clear();
        if (UseReferenceVisibilityOrder || !_grid.TryCollectObjectsInJoinOrder(center.X, center.Y, radius, candidates, extras, out CellArea area))
        {
            extras.Clear();
            VisibilityCandidatesBySort(center, candidates);
            return;
        }

        // The cells came back merged in join order. What is not in them (an object seen from farther away, an observer
        // outside the area, a remote camera's surroundings, unplaced objects) is usually a handful: sort only those and
        // merge them in. The result is exactly VisibilityCandidatesBySort's.
        if (center is Player player)
        {
            foreach (ObjectGuid guid in player.VisibleObjects)
            {
                if (_objects.TryGetValue(guid, out WorldObject? seen) && !_grid.IsCollectedIn(seen, area))
                {
                    extras.Add(seen);
                }
            }

            foreach (WorldObject mapWide in _mapWideOrder)
            {
                if (!_grid.IsCollectedIn(mapWide, area))
                {
                    extras.Add(mapWide);
                }
            }

            if (player.ViewPoint is { } eye && !ReferenceEquals(eye, player))
            {
                float eyeRadius = VisibilityQueryRadius(eye);
                _grid.CollectObjects(eye.X, eye.Y, eyeRadius, extras);
            }
        }

        if (_observers.TryGetValue(center.Guid, out HashSet<Player>? observers))
        {
            foreach (Player observer in observers)
            {
                if (!_grid.IsCollectedIn(observer, area))
                {
                    extras.Add(observer);
                }
            }
        }

        SortByJoinOrderDistinct(extras);
        JoinOrder.MergeDistinct(candidates, extras, _candidateMergeScratch);
        extras.Clear();
    }

    /// <summary>
    /// The reference form of <see cref="VisibilityCandidates"/>: collect everything, then sort into join order and drop
    /// repeats. Used when the cells cannot be merged (a non-finite or oversized query), and by the equivalence tests.
    /// </summary>
    internal void VisibilityCandidatesBySort(WorldObject center, List<WorldObject> candidates)
    {
        if (center is Player player)
        {
            foreach (ObjectGuid guid in player.VisibleObjects)
            {
                if (_objects.TryGetValue(guid, out WorldObject? seen))
                {
                    candidates.Add(seen);
                }
            }

            // A camera somewhere else (vmangos Camera view point): what is around it is a candidate too.
            if (player.ViewPoint is { } eye && !ReferenceEquals(eye, player))
            {
                float eyeRadius = VisibilityQueryRadius(eye);
                _grid.CollectObjects(eye.X, eye.Y, eyeRadius, candidates);
            }

            candidates.AddRange(_mapWideOrder);
        }

        if (_observers.TryGetValue(center.Guid, out HashSet<Player>? observers))
        {
            candidates.AddRange(observers);
        }

        float radius = VisibilityQueryRadius(center);
        _grid.CollectObjects(center.X, center.Y, radius, candidates);
        SortByJoinOrderDistinct(candidates);
    }

    /// <summary>Re-evaluate visibility between a player and everything near it, in both directions.</summary>
    private void UpdateVisibility(Player player)
    {
        VerifyVisibleSetInvariants(player);

        // vmangos Map::UpdateObjectVisibility / Player::UpdateVisibilityOf: same candidates and join order.
        // A visibility rule may recursively refresh a different player; that call must not reuse our active list.
        bool ownsScratch = !_visibilityScratchInUse;
        List<WorldObject> candidates = ownsScratch ? _visibilityScratch : [];
        _visibilityScratchInUse |= ownsScratch;
        try
        {
            VisibilityCandidates(player, candidates);
            PlayerVisibilityCandidates += candidates.Count;
            foreach (WorldObject other in candidates)
            {
                if (ReferenceEquals(other, player) || !ReferenceEquals(other.Map, this))
                {
                    continue;
                }

                UpdateVisibilityOf(viewer: player, target: other);
                if (other is Player otherPlayer)
                {
                    UpdateVisibilityOf(viewer: otherPlayer, target: player);
                }
            }
        }
        finally
        {
            if (ownsScratch)
            {
                candidates.Clear();
                _visibilityScratchInUse = false;
            }
        }
    }

    private readonly ArcaneCore.Kernel.Logging.LogGate _orphanLogGate = new(TimeSpan.FromMinutes(1));

    /// <summary>Orphan visible objects removed by <see cref="VerifyVisibleSetInvariants"/>; counts every heal, including those whose log line was throttled (map thread, diagnostics, tests).</summary>
    public long OrphanVisibleHeals { get; private set; }

    public bool VerifyVisibleSetInvariants(Player player)
    {
        if (player.VisibleObjects.Count == 0)
        {
            return false;
        }

        List<ObjectGuid>? orphans = null;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (guid == player.Guid || !_objects.TryGetValue(guid, out WorldObject? seen) || !ReferenceEquals(seen.Map, this))
            {
                (orphans ??= []).Add(guid);
            }
        }

        if (orphans is null)
        {
            return false;
        }

        foreach (ObjectGuid orphan in orphans)
        {
            OrphanVisibleHeals++;
            // Checked on every visibility tick: one line per window; the held-back heals are counted on the next admitted line.
            if (_orphanLogGate.TryEnter(out int suppressed))
            {
                if (suppressed == 0)
                {
                    _logger.LogWarning(
                        "visibility invariant healed for player {Player} (guid={Guid}) on map {MapId}: removing orphan visible object {OrphanGuid} not present on this map",
                        player.Name, player.Guid.Low, MapId, orphan);
                }
                else
                {
                    _logger.LogWarning(
                        "visibility invariant healed for player {Player} (guid={Guid}) on map {MapId}: removing orphan visible object {OrphanGuid} not present on this map ({Suppressed} more since)",
                        player.Name, player.Guid.Low, MapId, orphan, suppressed);
                }
            }

            RemoveVisible(player, orphan);
            if (orphan != player.Guid && player.Session.WantsUpdateObjects)
            {
                player.PendingUpdates.AddOutOfRange(orphan);
            }
        }

        return true;
    }

    /// <summary>Re-evaluate which players see a non-player object (vmangos WorldObject::UpdateObjectVisibility).</summary>
    private void UpdateObjectVisibility(WorldObject obj)
    {
        List<Player> viewers = ObjectVisibilityViewers(obj);
        ObjectVisibilityCandidates += viewers.Count;
        bool ownsScratch = ReferenceEquals(viewers, _viewerScratch);
        _viewerScratchInUse |= ownsScratch;
        try
        {
            foreach (Player viewer in viewers)
            {
                if (ReferenceEquals(viewer.Map, this))
                {
                    UpdateVisibilityOf(viewer, obj);
                }
            }
        }
        finally
        {
            if (ownsScratch)
            {
                _viewerScratchInUse = false;
                viewers.Clear();
            }
        }
    }

    /// <summary>vmangos Player::UpdateVisibilityOf: create on entering range, out-of-range on leaving.</summary>
    private void UpdateVisibilityOf(Player viewer, WorldObject target)
    {
        bool inVisibleList = viewer.VisibleObjects.Contains(target.Guid);

        // vmangos Player::UpdateVisibilityOf(viewPoint, target): distance from the camera, and the camera's own body is always in range.
        WorldObject eye = viewer.ViewPoint;
        bool inRange = ReferenceEquals(eye, target) || _mapWide.Contains(target) || IsWithinVisibilityDistance(eye, target, inVisibleList);
        bool allowed = PassesVisibilityRules(viewer, target, inVisibleList);

        if (inVisibleList && (!inRange || !allowed))
        {
            RemoveVisible(viewer, target.Guid);
            if (viewer.Session.WantsUpdateObjects) viewer.PendingUpdates.AddOutOfRange(target.Guid);
            else BotLeanStats.SkippedUpdateBlock();
        }
        else if (!inVisibleList && inRange && allowed)
        {
            AddVisible(viewer, target.Guid);
            if (!viewer.Session.WantsUpdateObjects)
            {
                // The visible set above is the bot's whole view of the world; only the packet nobody reads is not built.
                BotLeanStats.SkippedUpdateBlock();
                return;
            }

            PacketWriter block = viewer.PendingUpdates.BeginBlock();
            UpdateBlockWriter.WriteCreateBlock(block, target, viewer, _newObjects.Contains(target.Guid), _world.NowMs);
            viewer.PendingUpdates.EndBlock();
        }
    }

    private bool PassesVisibilityRules(Player viewer, WorldObject target, bool inVisibleList)
    {
        foreach (IVisibilityRule rule in _visibilityRules)
        {
            if (!rule.CanSee(viewer, target, inVisibleList, _detecting))
            {
                return false;
            }
        }

        return true;
    }

    private void AddVisible(Player viewer, ObjectGuid target)
    {
        if (viewer.VisibleObjects.Add(target))
        {
            viewer.VisibilityVersion++;
        }

        if (!_observers.TryGetValue(target, out HashSet<Player>? observers))
        {
            observers = new HashSet<Player>(ReferenceEqualityComparer.Instance);
            _observers[target] = observers;
        }

        observers.Add(viewer);
    }

    private bool RemoveVisible(Player viewer, ObjectGuid target)
    {
        if (!viewer.VisibleObjects.Remove(target))
        {
            return false;
        }

        viewer.VisibilityVersion++;

        if (_observers.TryGetValue(target, out HashSet<Player>? observers))
        {
            observers.Remove(viewer);
            if (observers.Count == 0)
            {
                _observers.Remove(target);
            }
        }

        return true;
    }
}
