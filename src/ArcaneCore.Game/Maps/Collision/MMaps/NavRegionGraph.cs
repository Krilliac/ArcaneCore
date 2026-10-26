using ArcaneCore.Kernel.Diagnostics;
using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision.MMaps;

/// <summary>What <see cref="NavRegionGraph.FindRoute"/> found.</summary>
public enum NavRegionRouteStatus
{
    /// <summary>The regions are joined: the portals lead from the first to the second.</summary>
    Found,

    /// <summary>No chain of regions joins them: no walk over the mesh (with these flags) leads from one to the other.</summary>
    Unreachable,

    /// <summary>The search ran out of its node budget before it knew.</summary>
    Exhausted,
}

/// <summary>A <c>.mmtile</c> that <see cref="NavRegionGraph.Build"/> could not read and left out of the graph: the file, the Detour tile coordinates in its name (-1 when the name has none) and why.</summary>
public sealed record SkippedNavTile(string File, int TileX, int TileY, Exception Error);

/// <summary>
/// The coarse connectivity of one map's whole navigation mesh: every <c>.mmtile</c> of the map is read once, its polygons that pass a
/// filter are split into regions (the parts of the tile its own polygon links join), and regions of adjacent tiles are joined where their
/// border edges meet, by the same rule a path query crosses a tile border by (<see cref="NavMesh.CollectNeighbours"/>). Each joint is a
/// <em>portal</em>: a point on the shared border, one per stretch of at most <see cref="MaxPortalYards"/>.
/// <para>
/// A chain of regions is a chain of mesh polygons a path query could walk, so the graph knows exactly which places of a map are joined
/// on foot (for the filter it was built with). A search over its portals with straight-line costs (<see cref="FindRoute"/>) gives the way
/// across a continent coarsely: a few thousand portals instead of millions of polygons, and the far parts of the mesh never loaded. The
/// fine way between two portals is still the path query's. A region graph is a hierarchical path-finding level in the sense of
/// HPA* (Botea, Müller and Schaeffer, 2004) built from the Detour tiles' own connectivity.
/// </para>
/// <para>Immutable once built; safe to read from any thread. Building reads the files and touches no live mesh.</para>
/// </summary>
public sealed class NavRegionGraph
{
    /// <summary>The longest stretch of a shared border one portal stands for; a longer meeting of two regions has several.</summary>
    public const float MaxPortalYards = 64f;

    /// <summary>
    /// How far inside the region it enters a portal lies (off the border line itself). A point exactly on a tile border belongs to
    /// whichever tile the rounding picks: a path query that ends there may look it up on the tile not loaded, or start the next one in
    /// the region it just left.
    /// </summary>
    public const float PortalInsetYards = 1f;

    /// <summary>Border pieces closer than this along the border are one stretch.</summary>
    private const float JoinGapYards = 0.5f;

    private readonly Dictionary<(int X, int Y), int[]> _polyRegions;
    private readonly int[] _firstLink;
    private readonly Link[] _links;

    private NavRegionGraph(uint mapId, Dictionary<(int X, int Y), int[]> polyRegions, int[] firstLink, Link[] links, long polygons, SkippedNavTile[] skipped)
    {
        SkippedTiles = skipped;
        MapId = mapId;
        _polyRegions = polyRegions;
        _firstLink = firstLink;
        _links = links;
        PolygonCount = polygons;
        _components = new Lazy<(int[], int[], int[])>(LabelComponents, LazyThreadSafetyMode.ExecutionAndPublication);
        _articulations = new Lazy<(int[], int[], int[])>(FindArticulations, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// A graph of <paramref name="polygonsPerRegion"/>.Count regions (region <c>i</c> holds that many polygons) joined by
    /// <paramref name="joints"/> (each an undirected portal, linked both ways at a point of no meaning), on no tile files: for tests of what
    /// the graph answers about its shape (components, articulation regions) on a graph made by hand.
    /// </summary>
    internal static NavRegionGraph FromJoints(uint mapId, IReadOnlyList<int> polygonsPerRegion, IReadOnlyList<(int A, int B)> joints)
    {
        ArgumentNullException.ThrowIfNull(polygonsPerRegion);
        ArgumentNullException.ThrowIfNull(joints);
        int count = polygonsPerRegion.Count;
        int[] first = new int[count + 1];
        foreach ((int a, int b) in joints)
        {
            if ((uint)a >= (uint)count || (uint)b >= (uint)count) throw new ArgumentOutOfRangeException(nameof(joints), $"joint ({a}, {b}) names a region outside 0..{count - 1}");
            first[a + 1]++;
            first[b + 1]++;
        }

        for (int i = 0; i < count; i++) first[i + 1] += first[i];
        var links = new Link[first[count]];
        int[] fill = first[..count];
        foreach ((int a, int b) in joints)
        {
            links[fill[a]++] = new Link(b, new Vector3(a, b, 0f));
            links[fill[b]++] = new Link(a, new Vector3(a, b, 0f));
        }

        var polygons = new List<int>();
        for (int region = 0; region < count; region++)
            for (int p = 0; p < polygonsPerRegion[region]; p++) polygons.Add(region);
        return new NavRegionGraph(mapId, new Dictionary<(int X, int Y), int[]> { [(0, 0)] = [.. polygons] }, first, links, polygons.Count, []);
    }

    /// <summary>One way out of a region: into region <c>To</c> across the portal <c>At</c> (world position).</summary>
    private readonly record struct Link(int To, Vector3 At);

    public uint MapId { get; }

    /// <summary>Tiles read.</summary>
    public int TileCount => _polyRegions.Count;

    /// <summary>Polygons of those tiles that passed the filter.</summary>
    public long PolygonCount { get; }

    /// <summary>The tiles of the map that were unreadable or corrupt and are missing from the graph (empty when every file parsed); the caller reports them, this static builder has no logger.</summary>
    public IReadOnlyList<SkippedNavTile> SkippedTiles { get; }

    public int RegionCount => _firstLink.Length - 1;

    /// <summary>Directed portal links (each joint counts once in each direction).</summary>
    public int LinkCount => _links.Length;

    /// <summary>The region of polygon <paramref name="poly"/> of Detour tile (<paramref name="tileX"/>, <paramref name="tileY"/>), or -1 (none: not read, or filtered out).</summary>
    public int RegionOf(int tileX, int tileY, int poly)
        => _polyRegions.TryGetValue((tileX, tileY), out int[]? regions) && (uint)poly < (uint)regions.Length ? regions[poly] : -1;

    /// <summary>
    /// The portals, in order, of the shortest way (straight lines between portals) from <paramref name="from"/> in region
    /// <paramref name="fromRegion"/> to <paramref name="to"/> in region <paramref name="toRegion"/>; empty when both lie in one region.
    /// A* over the portals with the straight distance to <paramref name="to"/> as its heuristic, at most <paramref name="maxNodes"/>
    /// portals settled. When the way is not found (<see cref="NavRegionRouteStatus.Unreachable"/>, <see cref="NavRegionRouteStatus.Exhausted"/>)
    /// the portals lead to the settled portal nearest to <paramref name="to"/> in a straight line, as Detour's partial path ends at the
    /// visited polygon nearest its goal; empty when none is nearer than <paramref name="from"/>. A region of -1 (none) is unreachable.
    /// </summary>
    public NavRegionRouteStatus FindRoute(int fromRegion, Vector3 from, int toRegion, Vector3 to, int maxNodes, out List<Vector3> portals)
        => FindRoute(fromRegion, from, toRegion, to, maxNodes, out portals, out _);

    /// <summary>As <see cref="FindRoute(int, Vector3, int, Vector3, int, out List{Vector3})"/>; <paramref name="settled"/> is the number of portals the search settled (its work).</summary>
    public NavRegionRouteStatus FindRoute(int fromRegion, Vector3 from, int toRegion, Vector3 to, int maxNodes, out List<Vector3> portals, out int settled)
    {
        portals = [];
        settled = 0;
        if ((uint)fromRegion >= (uint)RegionCount)
        {
            return NavRegionRouteStatus.Unreachable;
        }

        if (fromRegion == toRegion)
        {
            return NavRegionRouteStatus.Found;
        }

        // The search's sets are the calling thread's, kept between searches (cleared, their capacity kept): a search across a
        // continent settles thousands of portals and would otherwise allocate them anew on every plan.
        SearchBuffers buffers = t_buffers ??= new SearchBuffers();
        buffers.Clear();
        Dictionary<int, float> cost = buffers.Cost;
        Dictionary<int, int> parent = buffers.Parent;
        HashSet<int> closed = buffers.Closed;
        PriorityQueue<int, float> open = buffers.Open;
        int nearest = -1;
        float nearestYards = Vector3.Distance(from, to);
        for (int l = _firstLink[fromRegion]; l < _firstLink[fromRegion + 1]; l++)
        {
            Offer(l, -1, Vector3.Distance(from, _links[l].At));
        }

        while (open.TryDequeue(out int link, out _))
        {
            if (!closed.Add(link))
            {
                continue;
            }

            int region = _links[link].To;
            if (region == toRegion)
            {
                Chain(link, portals);
                settled = closed.Count;
                return NavRegionRouteStatus.Found;
            }

            // A portal back into the region the search began in only returns there: never the nearest end.
            float left = Vector3.Distance(_links[link].At, to);
            if (left < nearestYards && region != fromRegion)
            {
                (nearest, nearestYards) = (link, left);
            }

            if (closed.Count >= maxNodes)
            {
                Chain(nearest, portals);
                settled = closed.Count;
                return NavRegionRouteStatus.Exhausted;
            }

            float here = cost[link];
            Vector3 at = _links[link].At;
            for (int l = _firstLink[region]; l < _firstLink[region + 1]; l++)
            {
                if (!closed.Contains(l))
                {
                    Offer(l, link, here + Vector3.Distance(at, _links[l].At));
                }
            }
        }

        Chain(nearest, portals);
        settled = closed.Count;
        return NavRegionRouteStatus.Unreachable;

        void Chain(int last, List<Vector3> into)
        {
            for (int l = last; l >= 0; l = parent[l])
            {
                into.Add(_links[l].At);
            }

            into.Reverse();
        }

        void Offer(int link, int from, float g)
        {
            if (cost.TryGetValue(link, out float known) && known <= g)
            {
                return;
            }

            cost[link] = g;
            parent[link] = from;
            open.Enqueue(link, g + Vector3.Distance(_links[link].At, to));
        }
    }

    /// <summary>The regions reachable from <paramref name="region"/> (itself included), at most <paramref name="max"/> counted (diagnostics).</summary>
    public int ReachableCount(int region, int max)
    {
        var seen = new HashSet<int> { region };
        var queue = new Queue<int>([region]);
        while (queue.TryDequeue(out int r) && seen.Count < max)
        {
            for (int l = _firstLink[r]; l < _firstLink[r + 1]; l++)
            {
                if (seen.Add(_links[l].To))
                {
                    queue.Enqueue(_links[l].To);
                }
            }
        }

        return seen.Count;
    }

    private readonly Lazy<(int[] Id, int[] Size, int[] Polygons)> _components;

    private (int[] Id, int[] Size, int[] Polygons) LabelComponents()
    {
        int count = RegionCount;
        int[] id = new int[count];
        Array.Fill(id, -1);
        var sizes = new List<int>();
        int[] queue = new int[count];
        for (int start = 0; start < count; start++)
        {
            if (id[start] >= 0)
            {
                continue;
            }

            int label = sizes.Count, head = 0, tail = 0;
            id[start] = label;
            queue[tail++] = start;
            while (head < tail)
            {
                int region = queue[head++];
                for (int l = _firstLink[region]; l < _firstLink[region + 1]; l++)
                {
                    int next = _links[l].To;
                    if (id[next] < 0)
                    {
                        id[next] = label;
                        queue[tail++] = next;
                    }
                }
            }

            sizes.Add(tail);
        }

        int[] polygons = new int[sizes.Count];
        foreach (int[] regions in _polyRegions.Values)
        {
            foreach (int region in regions)
            {
                if ((uint)region < (uint)count)
                {
                    polygons[id[region]]++;
                }
            }
        }

        return (id, [.. sizes], polygons);
    }

    private readonly Lazy<(int[] Parts, int[] SecondPolygons, int[] Polygons)> _articulations;

    /// <summary>
    /// Tarjan's articulation points over the portal links, O(regions + links), iterative (a continent's regions would overflow the
    /// recursion). For each region: into how many parts its component falls when it is taken out (0 or 1: not an articulation region), and
    /// how many polygons the second-largest of those parts holds (0 when there are fewer than two). A part is a piece of the mesh the
    /// region is the only way between and the rest of the component; with the region's own polygons left out of every part.
    /// </summary>
    private (int[] Parts, int[] SecondPolygons, int[] Polygons) FindArticulations()
    {
        int count = RegionCount;
        int[] weight = new int[count];
        foreach (int[] regions in _polyRegions.Values)
        {
            foreach (int region in regions)
            {
                if ((uint)region < (uint)count) weight[region]++;
            }
        }

        int[] disc = new int[count], low = new int[count], parent = new int[count], next = new int[count], sub = new int[count];
        int[] separated = new int[count], largest = new int[count], second = new int[count], parts = new int[count];
        int[] stack = new int[count], order = new int[count];
        int clock = 0, visited = 0;
        for (int root = 0; root < count; root++)
        {
            if (disc[root] != 0) continue;
            int begin = visited, depth = 0;
            parent[root] = -1;
            disc[root] = low[root] = ++clock;
            sub[root] = weight[root];
            next[root] = _firstLink[root];
            order[visited++] = root;
            stack[depth++] = root;
            while (depth > 0)
            {
                int region = stack[depth - 1];
                if (next[region] < _firstLink[region + 1])
                {
                    int to = _links[next[region]++].To;
                    if (disc[to] == 0)
                    {
                        parent[to] = region;
                        disc[to] = low[to] = ++clock;
                        sub[to] = weight[to];
                        next[to] = _firstLink[to];
                        order[visited++] = to;
                        stack[depth++] = to;
                    }
                    else if (disc[to] < low[region])
                    {
                        low[region] = disc[to];
                    }

                    continue;
                }

                depth--;
                int above = parent[region];
                if (above < 0) continue;
                if (low[region] < low[above]) low[above] = low[region];
                sub[above] += sub[region];
                if (low[region] >= disc[above])
                {
                    // Everything below this child reaches the rest of the component only through the region above it.
                    separated[above] += sub[region];
                    AddPart(above, sub[region]);
                }
            }

            // The part holding the DFS root is whatever is left once the region and its separated parts are taken out.
            int total = sub[root];
            for (int i = begin; i < visited; i++)
            {
                int region = order[i];
                if (region != root && parts[region] > 0) AddPart(region, total - weight[region] - separated[region]);
            }
        }

        return (parts, second, weight);

        void AddPart(int region, int polygons)
        {
            parts[region]++;
            if (polygons > largest[region])
            {
                second[region] = largest[region];
                largest[region] = polygons;
            }
            else if (polygons > second[region])
            {
                second[region] = polygons;
            }
        }
    }

    /// <summary>
    /// Whether taking <paramref name="region"/> out of the graph would split its component in two or more parts (a cut region, an
    /// articulation point of the portal links): the only way between them is through it, as a tunnel is between its two ends. False for no
    /// region. Computed once for the whole graph on first use (<see cref="Prepare"/> does it off the world thread); then an array read.
    /// </summary>
    public bool IsArticulation(int region) => (uint)region < (uint)RegionCount && _articulations.Value.Parts[region] >= 2;

    /// <summary>
    /// Whether <paramref name="region"/> is a cut region (<see cref="IsArticulation"/>) whose removal leaves at least two parts of
    /// <paramref name="minPolygons"/> polygons or more: it joins two large pieces of the mesh, not a large piece and a pocket.
    /// </summary>
    public bool JoinsLargeParts(int region, int minPolygons) => (uint)region < (uint)RegionCount
        && _articulations.Value.Parts[region] >= 2 && _articulations.Value.SecondPolygons[region] >= minPolygons;

    /// <summary>
    /// How many polygons <paramref name="region"/> itself holds (0 for no region): the size of a cut region (<see cref="JoinsLargeParts"/>),
    /// which a caller bounds before it treats the whole region as a corridor. Computed with the cut regions; then an array read.
    /// </summary>
    public int RegionPolygons(int region) => (uint)region < (uint)RegionCount ? _articulations.Value.Polygons[region] : 0;

    /// <summary>
    /// Compute what the graph answers lazily (the components, <see cref="ComponentOf"/>, and the cut regions, <see cref="IsArticulation"/>)
    /// now, on the calling thread: the builder calls it on its pool thread so that the first question of a bot, on the world thread, is
    /// an array read.
    /// </summary>
    public void Prepare()
    {
        _ = _components.Value;
        _ = _articulations.Value;
    }

    /// <summary>
    /// The connected component (regions joined by portals, so places a walk joins) that <paramref name="region"/> belongs to, or -1 for no
    /// region. Labelled once for the whole graph on first use (linear in regions and links, a few milliseconds for a continent); every
    /// later call is an array read. Safe from any thread.
    /// </summary>
    public int ComponentOf(int region) => (uint)region < (uint)RegionCount ? _components.Value.Id[region] : -1;

    /// <summary>How many polygons the component of <paramref name="region"/> holds (0 for no region): its area, for telling a small fragment from a large plaza of few regions.</summary>
    public int ComponentPolygons(int region) => (uint)region < (uint)RegionCount ? _components.Value.Polygons[_components.Value.Id[region]] : 0;

    /// <summary>How many regions the component of <paramref name="region"/> holds (0 for no region): a few mean a fragment of mesh.</summary>
    public int ComponentSize(int region) => (uint)region < (uint)RegionCount ? _components.Value.Size[_components.Value.Id[region]] : 0;

    /// <summary>
    /// Read every <c>NNNXXYY.mmtile</c> of <paramref name="mapId"/> in <paramref name="directory"/> and build the graph for polygons with
    /// some flag of <paramref name="include"/> and none of <paramref name="exclude"/> (off-mesh connections never count, as in a path query).
    /// Unreadable or corrupt tiles are left out and listed in <see cref="SkippedTiles"/>; the live mesh load only reports a tile when a path
    /// query lazily loads it, so a tile no query has needed yet would otherwise go unnoticed.
    /// </summary>
    public static NavRegionGraph Build(string directory, uint mapId, NavTerrain include, NavTerrain exclude, CancellationToken cancel = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var tiles = new Dictionary<(int X, int Y), TileRegions>();
        int regionCount = 0;
        long polygons = 0;
        var skipped = new List<SkippedNavTile>();
        string prefix = $"{mapId:D3}";
        IEnumerable<string> files = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, prefix + "????.mmtile") : [];
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            cancel.ThrowIfCancellationRequested();
            if (Path.GetFileName(file).Length != 14)
            {
                continue;
            }

            NavMeshTile tile;
            try
            {
                tile = NavMeshTile.ParseFile(File.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                FaultLedger.Shared.Record("collision.mmap-regiongraph", ex);
                AnomalyLedger.Shared.Report("collision", "tile-load-failed", file, ex.Message);
                // left out of the graph and listed in SkippedTiles for the caller to report (this static builder has no logger)
                string name = Path.GetFileName(file);
                bool named = int.TryParse(name.AsSpan(3, 2), out int nameX) & int.TryParse(name.AsSpan(5, 2), out int nameY);
                skipped.Add(new SkippedNavTile(file, named ? nameX : -1, named ? nameY : -1, ex));
                continue;
            }

            if (tiles.ContainsKey((tile.X, tile.Y)))
            {
                continue; // the live mesh refuses a second tile in the same Detour slot too
            }

            int[] regions = SplitRegions(tile, include, exclude, ref regionCount, ref polygons);
            var borders = new List<(int Region, Vector3 A, Vector3 B)>[8];
            foreach (int side in (int[])[0, 2, 4, 6])
            {
                borders[side] = [.. tile.GetBorderEdges(side).Where(e => regions[e.Poly] >= 0).Select(e => (regions[e.Poly], e.A, e.B))];
            }

            tiles[(tile.X, tile.Y)] = new TileRegions(regions, borders, tile.WalkableClimb);
        }

        // Join each tile to its +x and +z neighbours (side 0 and 2 against their side 4 and 6), as NavMesh.CollectNeighbours does. A key's first
        // region lies in the tile on the lower side of the border: a pair of regions (each inside one tile) meets on one border only.
        var pieces = new Dictionary<(int Low, int High), List<(Vector3 P, Vector3 Q, bool AlongZ)>>();
        foreach (((int x, int y), TileRegions here) in tiles)
        {
            foreach (int side in (int[])[0, 2])
            {
                (int dx, int dy) = NavMeshFormat.SideOffset(side)!.Value;
                if (!tiles.TryGetValue((x + dx, y + dy), out TileRegions? there))
                {
                    continue;
                }

                bool alongZ = side == 0;
                float climb = MathF.Max(MathF.Max(here.WalkableClimb, there.WalkableClimb), 0.5f);
                foreach ((int a, Vector3 ea, Vector3 eb) in here.Borders[side])
                {
                    foreach ((int b, Vector3 ec, Vector3 ed) in there.Borders[side + 4])
                    {
                        if (NavMesh.TryOverlap(ea, eb, ec, ed, alongZ, climb, out Vector3 pa, out Vector3 pb))
                        {
                            (int, int) key = (a, b);
                            if (!pieces.TryGetValue(key, out var list))
                            {
                                pieces[key] = list = [];
                            }

                            list.Add((pa, pb, alongZ));
                        }
                    }
                }
            }
        }

        var outgoing = new List<Link>[regionCount];
        for (int i = 0; i < regionCount; i++)
        {
            outgoing[i] = [];
        }

        foreach (((int low, int high), var list) in pieces)
        {
            // The border's normal, towards the higher tile (Recast +x for a border along z, else +z).
            Vector3 normal = list[0].AlongZ ? Vector3.UnitX : Vector3.UnitZ;
            foreach (Vector3 portal in Portals(list))
            {
                outgoing[low].Add(new Link(high, NavMeshFormat.ToWorld(portal + (normal * PortalInsetYards))));
                outgoing[high].Add(new Link(low, NavMeshFormat.ToWorld(portal - (normal * PortalInsetYards))));
            }
        }

        int[] first = new int[regionCount + 1];
        var links = new List<Link>();
        for (int i = 0; i < regionCount; i++)
        {
            first[i] = links.Count;
            links.AddRange(outgoing[i]);
        }

        first[regionCount] = links.Count;
        return new NavRegionGraph(mapId, tiles.ToDictionary(t => t.Key, t => t.Value.Regions), first, [.. links], polygons, [.. skipped]);
    }

    /// <summary>The tile's passable polygons grouped by the tile's own links (union-find); -1 for a polygon that does not pass.</summary>
    private static int[] SplitRegions(NavMeshTile tile, NavTerrain include, NavTerrain exclude, ref int regionCount, ref long polygons)
    {
        int count = tile.Polys.Length;
        int[] root = new int[count];
        bool[] passes = new bool[count];
        for (int i = 0; i < count; i++)
        {
            root[i] = i;
            passes[i] = tile.Passes(i, include, exclude);
        }

        for (int i = 0; i < count; i++)
        {
            if (!passes[i])
            {
                continue;
            }

            foreach (ushort nei in tile.Polys[i].Neighbours)
            {
                int j = nei - 1;
                if (nei != 0 && (nei & NavMeshFormat.ExternalLink) == 0 && j < count && passes[j])
                {
                    int ri = Find(root, i), rj = Find(root, j);
                    if (ri != rj)
                    {
                        root[Math.Max(ri, rj)] = Math.Min(ri, rj);
                    }
                }
            }
        }

        int[] regions = new int[count];
        var ids = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            if (!passes[i])
            {
                regions[i] = -1;
                continue;
            }

            polygons++;
            int r = Find(root, i);
            if (!ids.TryGetValue(r, out int id))
            {
                ids[r] = id = regionCount++;
            }

            regions[i] = id;
        }

        return regions;
    }

    private static int Find(int[] root, int i)
    {
        while (root[i] != i)
        {
            root[i] = root[root[i]];
            i = root[i];
        }

        return i;
    }

    /// <summary>
    /// The portals of one meeting of two regions: its border pieces (all on the one border line the two tiles share) sorted along it,
    /// joined into stretches of at most <see cref="MaxPortalYards"/>, each stretch's middle (world position).
    /// </summary>
    private static IEnumerable<Vector3> Portals(List<(Vector3 P, Vector3 Q, bool AlongZ)> pieces)
    {
        static float Along(Vector3 v, bool alongZ) => alongZ ? v.Z : v.X;
        var sorted = pieces.Select(p => Along(p.P, p.AlongZ) <= Along(p.Q, p.AlongZ) ? p : (p.Q, p.P, p.AlongZ))
            .OrderBy(p => Along(p.Item1, p.AlongZ)).ToList();
        Vector3 start = sorted[0].Item1, end = sorted[0].Item2;
        bool z = sorted[0].AlongZ;
        for (int i = 1; i < sorted.Count; i++)
        {
            (Vector3 p, Vector3 q, _) = sorted[i];
            if (Along(p, z) - Along(end, z) <= JoinGapYards && Along(q, z) - Along(start, z) <= MaxPortalYards)
            {
                if (Along(q, z) > Along(end, z))
                {
                    end = q;
                }

                continue;
            }

            yield return (start + end) * 0.5f;
            (start, end) = (p, q);
        }

        yield return (start + end) * 0.5f;
    }

    [ThreadStatic]
    private static SearchBuffers? t_buffers;

    /// <summary>One thread's sets for <see cref="FindRoute(int, Vector3, int, Vector3, int, out List{Vector3}, out int)"/>.</summary>
    private sealed class SearchBuffers
    {
        public Dictionary<int, float> Cost { get; } = [];

        public Dictionary<int, int> Parent { get; } = [];

        public HashSet<int> Closed { get; } = [];

        public PriorityQueue<int, float> Open { get; } = new();

        public void Clear()
        {
            Cost.Clear();
            Parent.Clear();
            Closed.Clear();
            Open.Clear();
        }
    }

    private sealed record TileRegions(int[] Regions, List<(int Region, Vector3 A, Vector3 B)>[] Borders, float WalkableClimb);
}
