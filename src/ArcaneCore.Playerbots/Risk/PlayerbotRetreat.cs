using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Combat;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The places a living bot walked through out of combat, newest last, every <see cref="SpacingYards"/>: the known-safe way back a
/// retreat runs along. A teleport (a jump of more than <see cref="JumpYards"/>) or another map starts a new trail. World thread.
/// </summary>
internal sealed class PlayerbotBreadcrumbs
{
    internal const float SpacingYards = 4f;
    internal const float JumpYards = 40f;
    private const int Max = 96;

    private readonly List<Vector3> _points = [];
    private uint _mapId;

    internal IReadOnlyList<Vector3> Points => _points;

    internal void Track(Player player)
    {
        var here = new Vector3(player.X, player.Y, player.Z);
        if (!float.IsFinite(here.X) || !float.IsFinite(here.Y) || !float.IsFinite(here.Z)) return;
        if (player.MapId != _mapId || _points.Count > 0 && Vector3.Distance(_points[^1], here) > JumpYards)
        {
            _points.Clear();
            _mapId = player.MapId;
        }

        if (_points.Count > 0 && Vector3.Distance(_points[^1], here) < SpacingYards) return;
        if (_points.Count >= Max) _points.RemoveAt(0);
        _points.Add(here);
    }

    internal void Clear() => _points.Clear();
}

/// <summary>
/// A retreat from a lost fight. The bot stops attacking, uses the class escapes it has ready (<see cref="PlayerbotEscapes"/>), and
/// runs back the way it came (<see cref="PlayerbotBreadcrumbs"/>) to the first place outside every enemy's leash: vmangos
/// Creature::IsOutOfThreatArea (Objects/Creature.cpp:2796-2815) lets a creature give up a target once both are more than
/// <c>max(1.5 x aggro radius, ThreatRadius)</c> from where its fight began (and twelve seconds passed), and the template's hard leash
/// (cmangos <c>Leash</c>, CheckHardLeash) does the same from the combat start; the creature then evades home
/// (CreatureAI::EnterEvadeMode). The retreat is over once nothing threatens the bot; it gives up (and fights) after
/// <see cref="MaxMs"/>, when <see cref="MaxExtensions"/> further runs did not shake its pursuers off, or after
/// <see cref="MaxFailedReplans"/> thinks in a row with no way out in any direction it tries (<see cref="PlanAway"/>). Inside an instanceable
/// map there is no soft leash (CreatureMapSystem.IsOutOfThreatArea is false there), so a wiped group does not use this retreat there: it walks
/// out through the instance exit (PlayerbotGroupAI). World thread.
/// </summary>
internal sealed class PlayerbotRetreat(WorldSession session, PlayerbotOptions options, PlayerbotCombatSpells spells,
    PlayerbotHazards? hazards = null)
{
    internal const uint MaxMs = 60_000;
    internal const int MaxExtensions = 8;
    internal const float ExtendYards = 30f;

    /// <summary>Thinks in a row with no way out at all (<see cref="PlanAway"/>) before the retreat gives up cornered.</summary>
    internal const int MaxFailedReplans = 8;

    /// <summary>The margin past an enemy's leash the retreat runs to.</summary>
    internal const float LeashMarginYards = 5f;

    private readonly List<(Vector3 Anchor, float Safe)> _zones = [];
    private readonly List<string> _used = [];
    private readonly List<Creature> _enemies = [];
    private PlayerbotRoute? _routeSlot;

    /// <summary>The route this retreat walks. Letting go of one (null or another route) marks it released
    /// (<see cref="PlayerbotRoute.Released"/>): <see cref="PlayerbotMotion.SettleAfterDriver"/> stops a walk nobody holds.</summary>
    private PlayerbotRoute? _route
    {
        get => _routeSlot;
        set
        {
            if (ReferenceEquals(_routeSlot, value)) return;
            if (_routeSlot is { } old) old.Released = true;
            _routeSlot = value;
        }
    }
    private uint _startedMs;
    private int _extensions;
    private int _failedReplans;
    private bool _attackStopped;

    internal bool Active { get; private set; }

    /// <summary>When the current (or last) retreat began (world clock ms; 0: never).</summary>
    internal uint StartedMs => _startedMs;

    /// <summary>Why the current (or last) retreat began.</summary>
    internal string? Reason { get; private set; }

    /// <summary>How the last retreat ended: "safe" (nothing threatens the bot), "cornered" (it gave up and fights) or "died".</summary>
    internal string? Outcome { get; private set; }

    /// <summary>The escapes the current (or last) retreat used, in order.</summary>
    internal IReadOnlyList<string> UsedEscapes => _used;

    /// <summary>Retreats begun so far.</summary>
    internal int Count { get; private set; }

    /// <summary>Where the retreat first ran to (a place on the trail, or straight away from the enemies).</summary>
    internal Vector3? FirstGoal { get; private set; }

    /// <summary>The last steps of the retreat (inspection).</summary>
    internal string? Trace { get; private set; }

    /// <summary>Where the retreat runs to.</summary>
    internal Vector3? Goal { get; private set; }

    internal void Start(Player player, IReadOnlyList<Creature> enemies, IReadOnlyDictionary<ObjectGuid, Vector3> anchors,
        IReadOnlyList<Vector3> crumbs, string reason)
    {
        Active = true;
        Reason = reason;
        Outcome = null;
        Count++;
        _used.Clear();
        _zones.Clear();
        _enemies.Clear();
        _enemies.AddRange(enemies);
        _route = null;
        _extensions = 0;
        _failedReplans = 0;
        _attackStopped = false;
        _startedMs = session.World.NowMs;
        _zones.AddRange(BuildZones(player, enemies, anchors));
        Goal = ChooseGoal(player, crumbs, _zones, _enemies);
        if (Goal is { } goal) _route = Plan(player, goal, crumbs);
        FirstGoal = Goal;
        Trace = $"goal {Goal} route {(_route is null ? "none" : _route.Points.Count + " points")}";
    }

    /// <summary>The areas round the enemies' fight starts a pursuer gives up outside of: (centre, radius), two for each enemy.</summary>
    private static List<(Vector3 Anchor, float Safe)> BuildZones(Player player, IReadOnlyList<Creature> enemies,
        IReadOnlyDictionary<ObjectGuid, Vector3> anchors)
    {
        var zones = new List<(Vector3 Anchor, float Safe)>(enemies.Count * 2);
        CreatureMapSystem? system = player.Map?.FindUpdater<CreatureMapSystem>();
        foreach (Creature enemy in enemies)
        {
            Vector3 anchor = anchors.TryGetValue(enemy.Guid, out Vector3 start) ? start : new Vector3(enemy.Home.X, enemy.Home.Y, enemy.Home.Z);
            float aggro = system?.GetAttackDistance(enemy, player) ?? CreatureTemplate.DefaultDetectionRange;
            float threat = system?.Options.ThreatRadius ?? 50f;
            float safe = MathF.Max(MathF.Max(aggro * 1.5f, threat), enemy.Template.Leash) + LeashMarginYards;
            zones.Add((anchor, safe));
            // Where it was placed too: an idle creature's fight begins at its home, and the leash is measured from there.
            zones.Add((new Vector3(enemy.Home.X, enemy.Home.Y, enemy.Home.Z), safe));
        }

        return zones;
    }

    /// <summary>
    /// What a retreat from <paramref name="enemies"/> would face now, without starting one (<see cref="PlayerbotRiskModel.RetreatSucceeds"/>):
    /// the yards to the goal it would choose (along the trail when the goal is a crumb of it, else straight), the bot's current run
    /// speed (<see cref="Unit.RunSpeed"/>: snares and speed auras are in it), each living enemy's current run speed and damage per
    /// second nearest first, and the escapes ready. <paramref name="damagePerSecond"/> gives an enemy's damage per second against the bot.
    /// </summary>
    /// <param name="fightSeconds">How long the bot has been in this fight (null: unknown, no leash floor): the pursuers' leash clocks
    /// started about then (<see cref="PlayerbotPursuit.MinContactSeconds"/>).</param>
    internal PlayerbotPursuit Assess(Player player, IReadOnlyList<Creature> enemies, IReadOnlyDictionary<ObjectGuid, Vector3> anchors,
        IReadOnlyList<Vector3> crumbs, Func<Creature, float> damagePerSecond, float? fightSeconds = null)
    {
        Creature[] chasing = [.. enemies.Where(e => e.IsAlive && ReferenceEquals(e.Map, player.Map)).OrderBy(e => Distance(player, e))];
        List<(Vector3 Anchor, float Safe)> zones = BuildZones(player, chasing, anchors);
        Vector3 here = new(player.X, player.Y, player.Z);
        Vector3? goal = ChooseGoal(player, crumbs, zones, chasing);
        float yards = 0f;
        if (goal is { } target)
        {
            yards = Vector3.Distance(here, target);
            int end = -1;
            for (int index = crumbs.Count - 1; index >= 0; index--)
                if (crumbs[index] == target) { end = index; break; }
            if (end >= 0)
            {
                yards = Vector3.Distance(here, crumbs[^1]);
                for (int index = crumbs.Count - 1; index > end; index--) yards += Vector3.Distance(crumbs[index], crumbs[index - 1]);
            }
        }

        var pursuers = new List<RiskPursuer>(chasing.Length);
        foreach (Creature enemy in chasing) pursuers.Add(new RiskPursuer(enemy.RunSpeed, damagePerSecond(enemy)));
        PlayerbotEscapeKit kit = PlayerbotEscapes.Kit(ReadyEscapes(player, chasing, ignoreUsed: true));
        // The leash clock counts whole seconds and must be more than LeashExtensionSeconds old (CreatureMapSystem.IsOutOfThreatArea), and
        // IsOutOfThreatArea only starts and advances that clock once both creature and target are outside the threat radius (until then
        // GetLeashFacts reports ClockAgeSeconds = -1). A pursuer without a shorter hard template leash therefore stays in contact for the
        // run out of the threat area plus the remaining leash extension.
        float leash = 0f;
        if (chasing.Length > 0)
        {
            CreatureMapSystem? creatures = player.Map?.FindUpdater<CreatureMapSystem>();
            uint extension = creatures?.Options.LeashExtensionSeconds ?? 12u;
            float botSpeed = MathF.Max(0.1f, player.RunSpeed * kit.SelfSpeed);
            float runSeconds = yards / botSpeed;
            if (creatures is not null)
            {
                foreach (Creature enemy in chasing)
                {
                    if (creatures.GetLeashFacts(enemy, player) is not { } facts) continue;
                    if (enemy.Template.Leash > 0f && enemy.Template.Leash <= facts.Radius) continue;
                    float enemyLeash = facts.ClockAgeSeconds >= 0
                        ? MathF.Max(0f, extension + 1f - facts.ClockAgeSeconds)
                        : runSeconds + extension + 1f;
                    leash = MathF.Max(leash, enemyLeash);
                }
            }
            else if (fightSeconds is { } fought && float.IsFinite(fought))
            {
                leash = MathF.Max(0f, extension + 1f - MathF.Max(0f, fought));
            }
        }

        float flee = PlayerbotRiskModel.FleeDamageFactor(player.GetFloat(UpdateFields.PlayerDodgePercentage),
            player.GetFloat(UpdateFields.PlayerParryPercentage), player.GetFloat(UpdateFields.PlayerBlockPercentage));
        return new PlayerbotPursuit(yards, player.RunSpeed, pursuers, kit, leash, flee);
    }

    internal void Stop(string outcome)
    {
        if (!Active) return;
        Active = false;
        Outcome = outcome;
        _route = null;
        _enemies.Clear();
    }

    /// <summary>One think of the retreat; true while it owns the bot.</summary>
    internal bool Update(Player player, uint interval)
    {
        if (!Active) return false;
        if (!player.IsAlive) { Stop("died"); return false; }
        if (!Threatened(player)) { Stop("safe"); PlayerbotMovementControl.Stop(session, player); return false; }
        if (unchecked(session.World.NowMs - _startedMs) > MaxMs) { Stop("cornered"); return false; }

        if (!_attackStopped)
        {
            _attackStopped = player.Combat.Victim is null || session.TryManagedAction(WorldOpcode.CmsgAttackstop, []);
            if (!_attackStopped) return true;
        }

        // Feign Death or an escape spell cast in progress holds the bot still (moving would cancel either).
        if (HasAura(player, PlayerbotEscapes.FeignDeath) || IsCasting(player))
        {
            if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(session, player);
            return true;
        }

        TryEscape(player);
        if (HasAura(player, PlayerbotEscapes.FeignDeath) || IsCasting(player)) return true;

        if (_route is null || _route.Complete)
        {
            if (_extensions >= MaxExtensions) { Stop("cornered"); return false; }
            _route = PlanAway(player, out Vector3 goal);
            Goal = goal;
            if (_route is null)
            {
                Trace += $"; no way out at {player.X:F1},{player.Y:F1}";
                if (++_failedReplans >= MaxFailedReplans) { Stop("cornered"); return false; }
                return true;
            }

            _failedReplans = 0;
            _extensions++;
        }

        if (!PlayerbotNavigation.TryAdvance(session, _route, options, interval, session.World.NowMs))
        {
            Trace += $"; advance refused at {player.X:F1},{player.Y:F1} next {_route.NextPoint}/{_route.Points.Count}";
            _route = null;
        }

        return true;
    }

    /// <summary>A living enemy still attacks the bot or has it on its threat list (an evading creature does not count).</summary>
    internal static bool Threatened(Player player)
    {
        foreach (Unit attacker in player.Combat.Attackers)
            if (attacker.IsAlive && ReferenceEquals(attacker.Map, player.Map) && attacker is not Creature { IsInEvadeMode: true }) return true;
        foreach (Unit threat in player.Combat.ThreatenedBy)
            if (threat.IsAlive && ReferenceEquals(threat.Map, player.Map) && threat is not Creature { IsInEvadeMode: true }) return true;
        return false;
    }

    private void TryEscape(Player player)
    {
        PlayerbotAbilities known = spells.Escapes(player);
        if (known.Count == 0) return;
        EscapeSituation situation = SituationOf(player, _enemies, out Creature? victim);
        EscapeAction? action = PlayerbotEscapes.Choose(situation, candidate => IsReady(player, known, candidate, victim, ignoreUsed: false));
        if (action is not { } chosen || known[chosen.Spell] is not { } chosenSpell) return;
        if (chosenSpell.CastTime.Base > 0 && player.Movement.HasFlag(MovementFlags.MaskMoving)
            && (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)))
            return;
        if (chosen.FaceAway)
        {
            Vector3 away = Away(player);
            PlayerbotMotion.Face(session, player, MathF.Atan2(away.Y, away.X));
        }

        if (spells.CastAt(player, chosenSpell, chosen.Target == EscapeTarget.Self ? player : victim!)
            || HasAura(player, chosen.Spell))
            _used.Add(chosen.Spell);
    }

    private bool IsCasting(Player player)
        => session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
            { State: SpellCastState.Preparing or SpellCastState.Casting };

    private bool HasAura(Player player, string name)
        => session.Services.GetService<SpellFeature>() is { } feature
            && feature.System.GetAuras(player).Any(holder => string.Equals(holder.Spell.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The newest place on the trail outside every enemy's leash and farther from the enemies than the bot is now.</summary>
    private Vector3? ChooseGoal(Player player, IReadOnlyList<Vector3> crumbs, IReadOnlyList<(Vector3 Anchor, float Safe)> zones,
        IReadOnlyList<Creature> enemyList)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        Vector3 enemies = Centroid(player, enemyList, zones);
        Vector3? fallbackCrumb = null;
        uint now = session.World.NowMs;
        for (int index = crumbs.Count - 1; index >= 0; index--)
        {
            Vector3 crumb = crumbs[index];
            if (Safe(crumb, zones) && Vector3.Distance(crumb, enemies) > Vector3.Distance(here, enemies))
            {
                fallbackCrumb ??= crumb;
                if (hazards?.FirstOnRoute(player.MapId, [here, crumb], now, strong: true) is null) return crumb;
            }
        }

        if (fallbackCrumb is not null) return fallbackCrumb;

        // No trail out of reach: straight away from the enemies, past the farthest leash.
        float needed = 0;
        foreach ((Vector3 anchor, float safe) in zones) needed = MathF.Max(needed, safe - Vector3.Distance(here, anchor));
        return here + (Away(player, enemyList, zones) * MathF.Max(ExtendYards, needed + LeashMarginYards));
    }

    private static bool Safe(Vector3 point, IReadOnlyList<(Vector3 Anchor, float Safe)> zones)
    {
        foreach ((Vector3 anchor, float safe) in zones)
            if (Vector3.Distance(point, anchor) < safe) return false;
        return true;
    }

    /// <summary>The turns off straight away from the enemies a further run tries, in order (degrees): away first, never back toward them.</summary>
    private static ReadOnlySpan<float> FanDegrees => [0f, 45f, -45f, 90f, -90f];

    /// <summary>
    /// A further run of <see cref="ExtendYards"/>: straight away from the enemies when the navigation has a way there, else the first of
    /// the turns of <see cref="FanDegrees"/> that has one, else the walkable point nearest the straight one
    /// (<see cref="PlayerbotNavigation.TryPlanToward"/>); null when none. Each goal stands on the ground there (the bot's own height
    /// is yards off it on a slope). <paramref name="goal"/> is where the run goes (the straight one when there is none).
    /// <para>
    /// The retreat once asked only for the straight one, every think: where that was a cliff edge, water or a wall, all
    /// <see cref="MaxExtensions"/> replans were refused alike within a few thinks and the bot gave up cornered where it stood, although
    /// the ground to either side was open (live 2026-10-16, Krezelou on Teldrassil: 14 of 16 directions round it plan, the two either
    /// side of its retreat's own did not). Cost of a think with no way: five plans and the nearest-point search; each plan the hazards
    /// (not the mesh) refuse also runs the hazard detour search (held to the think's budget, PlayerbotThinkProbe.Fallback) and records
    /// one refusal toward the boxed-in escape (PlayerbotRisk.Refused), up to five a think.
    /// </para>
    /// </summary>
    private PlayerbotRoute? PlanAway(Player player, out Vector3 goal)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        Vector3 away = Away(player);
        float straight = MathF.Atan2(away.Y, away.X);
        goal = OnGround(player, here + (away * ExtendYards));
        PlayerbotRoute? fallbackRoute = null;
        Vector3 fallbackGoal = goal;
        foreach (float degrees in FanDegrees)
        {
            float angle = straight + (degrees * MathF.PI / 180f);
            Vector3 candidate = degrees == 0f ? goal
                : OnGround(player, here + (new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * ExtendYards));
            if (PlayerbotNavigation.TryPlan(player, candidate, options, out PlayerbotRoute? route) && route is not null)
            {
                if (!CrossesNewHazard(player, route))
                {
                    goal = candidate;
                    return route;
                }

                if (fallbackRoute is null)
                {
                    fallbackRoute = route;
                    fallbackGoal = candidate;
                }
            }
        }

        if (fallbackRoute is not null)
        {
            goal = fallbackGoal;
            return fallbackRoute;
        }

        return PlayerbotNavigation.TryPlanToward(player, goal, options, out PlayerbotRoute? toward) ? toward : null;
    }

    private bool CrossesNewHazard(Player player, PlayerbotRoute route)
    {
        if (hazards is null || route.Points.Count == 0) return false;
        uint now = session.World.NowMs;
        Vector3 here = new(player.X, player.Y, player.Z);
        foreach (PlayerbotHazard hazard in hazards.Active(player.MapId, now))
        {
            if (hazard.Covers(here)) continue;
            for (int i = 1; i < route.Points.Count; i++)
            {
                Vector3 a = route.Points[i - 1], b = route.Points[i];
                int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / 3f));
                for (int s = 0; s <= steps; s++)
                    if (hazard.Covers(Vector3.Lerp(a, b, (float)s / steps))) return true;
            }
        }

        return false;
    }

    /// <summary><paramref name="point"/> on the ground below it (searched from 20 yards above), or as it is where there is none within 30 yards.</summary>
    private static Vector3 OnGround(Player player, Vector3 point)
    {
        if (player.Map is not { } map) return point;
        float floor = map.Collision.GetHeight(point.X, point.Y, point.Z + 20f);
        return PlayerbotNavigation.InvalidHeight(floor) || MathF.Abs(floor - point.Z) > 30f ? point : point with { Z = floor };
    }

    /// <summary>A route to <paramref name="goal"/>: the navigation's, else back along the trail itself.</summary>
    private PlayerbotRoute? Plan(Player player, Vector3 goal, IReadOnlyList<Vector3> crumbs)
    {
        if ((PlayerbotNavigation.TryPlan(player, goal, options, out PlayerbotRoute? route)
                || PlayerbotNavigation.TryPlanToward(player, goal, options, out route))
            && route is not null && !CrossesNewHazard(player, route))
            return route;
        int end = -1;
        for (int index = crumbs.Count - 1; index >= 0; index--)
            if (crumbs[index] == goal) { end = index; break; }
        if (end >= 0 && TrailRoute(player, crumbs, end) is { } trail && !CrossesNewHazard(player, trail))
            return trail;
        if (route is not null) return route;
        if (end >= 0) return TrailRoute(player, crumbs, end);
        return null;
    }

    /// <summary>
    /// The way back along the trail from the bot to crumb <paramref name="end"/>. Each leg is checked against models (doodads included)
    /// and the mesh (<see cref="PlayerbotMoveCheck.TrailRoute"/>) and the first blocked leg is cut and ends the way: the trail is where
    /// the bot has been, not proof that the straight chord between two crumbs is walkable (it may have walked round a fence).
    /// </summary>
    internal static PlayerbotRoute? TrailRoute(Player player, IReadOnlyList<Vector3> crumbs, int end)
        => player.Map is { } map ? PlayerbotMoveCheck.TrailRoute(map, new Vector3(player.X, player.Y, player.Z), crumbs, end) : null;

    private static Vector3 Centroid(Player player, IReadOnlyList<Creature> enemyList, IReadOnlyList<(Vector3 Anchor, float Safe)> zones)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (Creature enemy in enemyList)
        {
            if (!ReferenceEquals(enemy.Map, player.Map)) continue;
            sum += new Vector3(enemy.X, enemy.Y, enemy.Z);
            count++;
        }

        if (count == 0)
            foreach ((Vector3 anchor, _) in zones) { sum += anchor; count++; }
        return count == 0 ? new Vector3(player.X, player.Y, player.Z) : sum / count;
    }

    /// <summary>The flat direction from the enemies to the bot (behind the bot when they stand on it).</summary>
    private Vector3 Away(Player player) => Away(player, _enemies, _zones);

    private static Vector3 Away(Player player, IReadOnlyList<Creature> enemyList, IReadOnlyList<(Vector3 Anchor, float Safe)> zones)
    {
        Vector3 from = Centroid(player, enemyList, zones);
        var flat = new Vector3(player.X - from.X, player.Y - from.Y, 0);
        if (flat.Length() < 0.5f) flat = new Vector3(-MathF.Cos(player.Orientation), -MathF.Sin(player.Orientation), 0);
        return Vector3.Normalize(flat);
    }

    /// <summary>What <see cref="PlayerbotEscapes"/> reads about the retreat; <paramref name="victim"/> is the nearest living enemy.</summary>
    internal static EscapeSituation SituationOf(Player player, IReadOnlyList<Creature> enemyList, out Creature? victim)
    {
        victim = enemyList.Where(e => e.IsAlive && ReferenceEquals(e.Map, player.Map)).OrderBy(e => Distance(player, e)).FirstOrDefault();
        int within8 = 0, within10 = 0;
        foreach (Creature enemy in enemyList)
        {
            if (!enemy.IsAlive || !ReferenceEquals(enemy.Map, player.Map)) continue;
            float d = Distance(player, enemy);
            if (d <= 8f) within8++;
            if (d <= 10f) within10++;
        }

        return new EscapeSituation(player.Class, player.MaxHealth == 0 ? 0 : player.Health * 100f / player.MaxHealth,
            player.Combat.IsInCombat, within8, within10, victim is not null && Distance(player, victim) <= PlayerbotClassRotation.MeleeRange + 1f,
            victim is not null, ShapeshiftService.GetForm(player) == ShapeshiftForm.Cat, Events.PlayerbotWorldPvpObjectives.Carries(player));
    }

    /// <summary>An escape that is known, castable at its target now and (unless <paramref name="ignoreUsed"/>) not used yet this retreat.</summary>
    private bool IsReady(Player player, PlayerbotAbilities known, EscapeAction candidate, Creature? victim, bool ignoreUsed)
        => (ignoreUsed || !_used.Contains(candidate.Spell)) && known[candidate.Spell] is { } spell
            && (candidate.Target == EscapeTarget.Self || victim is not null)
            && spells.CanCastNow(player, spell, candidate.Target == EscapeTarget.Self ? player : victim!);

    /// <summary>Every escape of the bot's class that is ready now, in the order the retreat would cast them.</summary>
    private List<(EscapeAction Action, SpellInfo? Spell)> ReadyEscapes(Player player, IReadOnlyList<Creature> enemyList, bool ignoreUsed)
    {
        var ready = new List<(EscapeAction Action, SpellInfo? Spell)>();
        PlayerbotAbilities known = spells.Escapes(player);
        if (known.Count == 0) return ready;
        EscapeSituation situation = SituationOf(player, enemyList, out Creature? victim);
        foreach (EscapeAction candidate in PlayerbotEscapes.Candidates(situation))
            if (IsReady(player, known, candidate, victim, ignoreUsed)) ready.Add((candidate, known[candidate.Spell]));
        return ready;
    }

}
