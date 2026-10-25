namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The route planner (docs/areas/playbots.md, Travel): a pure function of <see cref="TravelFacts"/> and two places. It builds a small graph
/// (the start, the goal, every ship port, the bind point, every own-teleport destination, every portal's mage place and destination) and
/// searches it for the fewest seconds whose copper fits <see cref="TravelFacts.SpendCap"/>. Pure: no world, no session, no state kept between
/// calls; safe on any thread. Allocation: a handful of small lists per call (the graph is under 64 nodes); not a per-tick call.
/// </summary>
internal static class PlayerbotTravelPlanner
{
    /// <summary>Ground speed of a walking bot, yards per second (run speed 7).</summary>
    internal const float WalkYardsPerSecond = 7f;

    /// <summary>Path length over straight distance: terrain is never a straight line.</summary>
    internal const float WalkDetour = 1.3f;

    /// <summary>Same-map places closer than this are one place: the trip is empty.</summary>
    internal const float ArrivedYards = 5f;

    /// <summary>Seconds a hearthstone or a Teleport spell takes to cast (10) and the loading screen after it (5).</summary>
    internal const float CastAndLoadSeconds = 15f;

    /// <summary>Seconds a requester waits for a mage to cast a portal (20) and load through it (5).</summary>
    internal const float PortalSeconds = 25f;

    /// <summary>Own teleports beyond this many are ignored (each needs a bit of the used-mask).</summary>
    private const int MaxTeleports = 62;

    private const ulong HearthBit = 1UL << 63;

    /// <summary>The seconds a walk between two same-map places takes, or infinity across maps.</summary>
    internal static float WalkSeconds(TravelPlace a, TravelPlace b) => a.DistanceTo(b) * WalkDetour / WalkYardsPerSecond;

    private sealed class Label(int node, bool lastWalk, ulong used, float seconds, uint copper, int legs, Label? parent, TravelLeg? leg)
    {
        public readonly int Node = node;
        public readonly bool LastWalk = lastWalk;
        public readonly ulong Used = used;
        public readonly float Seconds = seconds;
        public readonly uint Copper = copper;
        public readonly int Legs = legs;
        public readonly Label? Parent = parent;
        public readonly TravelLeg? Leg = leg;
    }

    /// <summary>
    /// The fastest route from <paramref name="from"/> to <paramref name="to"/> whose total copper is within <see cref="TravelFacts.SpendCap"/>
    /// (ties: fewer legs, then less copper, then the first found in a fixed order), or null when <paramref name="to"/> cannot be reached. A
    /// start already at the goal (same map, under <see cref="ArrivedYards"/>) is an empty route. Two walk legs never follow each other; the
    /// hearthstone and each own teleport are used at most once.
    /// </summary>
    internal static TravelRoute? Plan(TravelFacts facts, TravelPlace from, TravelPlace to)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (from.MapId == to.MapId && from.DistanceTo(to) < ArrivedYards) return new TravelRoute([]);

        List<TravelPlace> nodes = [];
        Dictionary<TravelPlace, int> index = [];
        int Node(TravelPlace p)
        {
            if (index.TryGetValue(p, out int i)) return i;
            index[p] = nodes.Count;
            nodes.Add(p);
            return nodes.Count - 1;
        }

        int start = Node(from);
        int goal = Node(to);
        foreach (ShipRoute s in facts.Ships) { Node(s.PortA); Node(s.PortB); }
        int bind = facts.HearthReady && facts.Bind is { } b ? Node(b) : -1;
        int teleportCount = Math.Min(facts.OwnTeleports.Count, MaxTeleports);
        for (int i = 0; i < teleportCount; i++) Node(facts.OwnTeleports[i].Destination);
        foreach (PortalOffer o in facts.Portals) { Node(o.MagePlace); Node(o.Destination); }

        PriorityQueue<Label, (float Seconds, int Legs, uint Copper, int Order)> open = new();
        Dictionary<(int Node, bool LastWalk, ulong Used), List<Label>> kept = [];
        int order = 0;

        void Push(Label l)
        {
            if (l.Copper > facts.SpendCap) return;
            if (kept.TryGetValue((l.Node, l.LastWalk, l.Used), out List<Label>? same) && Dominated(same, l)) return;
            open.Enqueue(l, (l.Seconds, l.Legs, l.Copper, order++));
        }

        void Step(Label from_, int toNode, TravelLeg leg, bool walk, ulong used) =>
            Push(new Label(toNode, walk, used, from_.Seconds + leg.Seconds, from_.Copper + leg.Copper, from_.Legs + 1, from_, leg));

        Push(new Label(start, false, 0, 0, 0, 0, null, null));
        while (open.TryDequeue(out Label? cur, out _))
        {
            var key = (cur.Node, cur.LastWalk, cur.Used);
            if (!kept.TryGetValue(key, out List<Label>? list)) kept[key] = list = [];
            if (Dominated(list, cur)) continue;
            list.Add(cur);

            if (cur.Node == goal) return Build(cur);

            TravelPlace here = nodes[cur.Node];

            // Walking and flying between nodes of one map. A walk never follows a walk (they merge into one).
            for (int v = 0; v < nodes.Count; v++)
            {
                if (v == cur.Node) continue;
                TravelPlace there = nodes[v];
                if (there.MapId != here.MapId) continue;
                float walk = WalkSeconds(here, there);
                bool canWalk = !cur.LastWalk && (facts.Walkable?.Invoke(here, there) ?? true);
                (float Seconds, uint Copper)? flight = facts.Flight?.Invoke(here, there);
                if (flight is { } f && f.Copper <= facts.SpendCap && f.Seconds >= 0f && (f.Seconds < walk || !canWalk))
                    Step(cur, v, new TravelLeg(TravelMeans.FlightPath, here, there, f.Seconds, f.Copper), false, cur.Used);
                if (canWalk)
                    Step(cur, v, new TravelLeg(TravelMeans.Walk, here, there, walk, 0), true, cur.Used);
            }

            // Ships, in both directions, from a port the bot stands at.
            // Never straight back on the ship just left (the same ship, the ends reversed): that only undoes the ride.
            foreach (ShipRoute s in facts.Ships)
            {
                bool justRode = cur.Leg is { } prev && prev.TransportEntry == s.Entry && prev.Means == s.Means && prev.To == here;
                if (s.PortA == here && !(justRode && cur.Leg!.From == s.PortB))
                    Step(cur, index[s.PortB], new TravelLeg(s.Means, s.PortA, s.PortB, s.PeriodSeconds, 0) { TransportEntry = s.Entry }, false, cur.Used);
                if (s.PortB == here && !(justRode && cur.Leg!.From == s.PortA))
                    Step(cur, index[s.PortA], new TravelLeg(s.Means, s.PortB, s.PortA, s.PeriodSeconds, 0) { TransportEntry = s.Entry }, false, cur.Used);
            }

            // The hearthstone, once, from anywhere.
            if (bind >= 0 && bind != cur.Node && (cur.Used & HearthBit) == 0)
                Step(cur, bind, new TravelLeg(TravelMeans.Hearthstone, here, nodes[bind], CastAndLoadSeconds, 0), false, cur.Used | HearthBit);

            // Each own teleport, once, from anywhere.
            for (int i = 0; i < teleportCount; i++)
            {
                ulong bit = 1UL << i;
                OwnTeleport t = facts.OwnTeleports[i];
                if ((cur.Used & bit) != 0 || t.Destination == here) continue;
                Step(cur, index[t.Destination], new TravelLeg(TravelMeans.MageTeleport, here, t.Destination, CastAndLoadSeconds, 0) { SpellId = t.SpellId }, false, cur.Used | bit);
            }

            // A mage's portal, from where the mage stands.
            foreach (PortalOffer o in facts.Portals)
            {
                if (o.MagePlace != here || o.Destination == here) continue;
                Step(cur, index[o.Destination],
                    new TravelLeg(TravelMeans.MagePortal, o.MagePlace, o.Destination, PortalSeconds, o.FeeCopper) { SpellId = o.PortalSpellId, MageGuid = o.MageGuid },
                    false, cur.Used);
            }
        }

        return null;
    }

    /// <summary>Whether a kept label of the same state is at least as good in seconds, copper and legs.</summary>
    private static bool Dominated(List<Label> kept, Label l)
    {
        for (int i = 0; i < kept.Count; i++)
        {
            Label k = kept[i];
            if (k.Seconds <= l.Seconds && k.Copper <= l.Copper && k.Legs <= l.Legs) return true;
        }
        return false;
    }

    private static TravelRoute Build(Label end)
    {
        List<TravelLeg> legs = [];
        for (Label? l = end; l?.Leg is { } leg; l = l.Parent) legs.Add(leg);
        legs.Reverse();
        return new TravelRoute(legs);
    }
}
