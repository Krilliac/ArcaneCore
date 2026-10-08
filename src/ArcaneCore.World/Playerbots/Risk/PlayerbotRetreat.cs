using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Combat;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

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
/// <see cref="MaxMs"/> or when <see cref="MaxExtensions"/> further runs did not shake its pursuers off. World thread.
/// </summary>
internal sealed class PlayerbotRetreat(WorldSession session, PlayerbotOptions options, PlayerbotCombatSpells spells)
{
    internal const uint MaxMs = 60_000;
    internal const int MaxExtensions = 3;
    internal const float ExtendYards = 30f;

    /// <summary>The margin past an enemy's leash the retreat runs to.</summary>
    internal const float LeashMarginYards = 5f;

    private readonly List<(Vector3 Anchor, float Safe)> _zones = [];
    private readonly List<string> _used = [];
    private readonly List<Creature> _enemies = [];
    private PlayerbotRoute? _route;
    private uint _startedMs;
    private int _extensions;
    private bool _attackStopped;

    internal bool Active { get; private set; }

    /// <summary>Why the current (or last) retreat began.</summary>
    internal string? Reason { get; private set; }

    /// <summary>How the last retreat ended: "safe" (nothing threatens the bot), "cornered" (it gave up and fights) or "died".</summary>
    internal string? Outcome { get; private set; }

    /// <summary>The escapes the current (or last) retreat used, in order.</summary>
    internal IReadOnlyList<string> UsedEscapes => _used;

    /// <summary>Retreats begun so far.</summary>
    internal int Count { get; private set; }

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
        _attackStopped = false;
        _startedMs = session.World.NowMs;
        CreatureMapSystem? system = player.Map?.FindUpdater<CreatureMapSystem>();
        foreach (Creature enemy in enemies)
        {
            Vector3 anchor = anchors.TryGetValue(enemy.Guid, out Vector3 start) ? start : new Vector3(enemy.Home.X, enemy.Home.Y, enemy.Home.Z);
            float aggro = system?.GetAttackDistance(enemy, player) ?? CreatureTemplate.DefaultDetectionRange;
            float threat = system?.Options.ThreatRadius ?? 50f;
            float safe = MathF.Max(MathF.Max(aggro * 1.5f, threat), enemy.Template.Leash) + LeashMarginYards;
            _zones.Add((anchor, safe));
        }

        Goal = ChooseGoal(player, crumbs);
        if (Goal is { } goal) _route = Plan(player, goal, crumbs);
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

        // Feign Death holds the bot still until the creatures turn away (moving would end it).
        if (HasAura(player, PlayerbotEscapes.FeignDeath))
        {
            if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(session, player);
            return true;
        }

        TryEscape(player);
        if (HasAura(player, PlayerbotEscapes.FeignDeath)) return true;

        if (_route is null || _route.Complete)
        {
            if (_extensions >= MaxExtensions) { Stop("cornered"); return false; }
            _extensions++;
            Vector3 here = new(player.X, player.Y, player.Z);
            Goal = here + (Away(player) * ExtendYards);
            _route = Plan(player, Goal.Value, []);
            if (_route is null) return true;
        }

        if (!PlayerbotNavigation.TryAdvance(session, _route, options, interval, session.World.NowMs)) _route = null;
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
        Creature? victim = _enemies.Where(e => e.IsAlive && ReferenceEquals(e.Map, player.Map))
            .OrderBy(e => Distance(player, e)).FirstOrDefault();
        PlayerbotAbilities known = spells.Escapes(player);
        if (known.Count == 0) return;
        int within8 = 0, within10 = 0;
        foreach (Creature enemy in _enemies)
        {
            if (!enemy.IsAlive || !ReferenceEquals(enemy.Map, player.Map)) continue;
            float d = Distance(player, enemy);
            if (d <= 8f) within8++;
            if (d <= 10f) within10++;
        }

        var situation = new EscapeSituation(player.Class, player.MaxHealth == 0 ? 0 : player.Health * 100f / player.MaxHealth,
            player.Combat.IsInCombat, within8, within10, victim is not null && Distance(player, victim) <= PlayerbotClassRotation.MeleeRange + 1f,
            victim is not null, ShapeshiftService.GetForm(player) == ShapeshiftForm.Cat);
        EscapeAction? action = PlayerbotEscapes.Choose(situation, candidate =>
            !_used.Contains(candidate.Spell) && known[candidate.Spell] is { } spell
            && (candidate.Target == EscapeTarget.Self || victim is not null)
            && spells.CanCastNow(player, spell, candidate.Target == EscapeTarget.Self ? player : victim!));
        if (action is not { } chosen || known[chosen.Spell] is not { } chosenSpell) return;
        if (chosen.FaceAway)
        {
            Vector3 away = Away(player);
            PlayerbotMotion.Face(session, player, MathF.Atan2(away.Y, away.X));
        }

        if (spells.CastAt(player, chosenSpell, chosen.Target == EscapeTarget.Self ? player : victim!)
            || HasAura(player, chosen.Spell))
            _used.Add(chosen.Spell);
    }

    private bool HasAura(Player player, string name)
        => session.Services.GetService<SpellFeature>() is { } feature
            && feature.System.GetAuras(player).Any(holder => string.Equals(holder.Spell.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The newest place on the trail outside every enemy's leash and farther from the enemies than the bot is now.</summary>
    private Vector3? ChooseGoal(Player player, IReadOnlyList<Vector3> crumbs)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        Vector3 enemies = Centroid(player);
        for (int index = crumbs.Count - 1; index >= 0; index--)
        {
            Vector3 crumb = crumbs[index];
            if (Safe(crumb) && Vector3.Distance(crumb, enemies) > Vector3.Distance(here, enemies)) return crumb;
        }

        // No trail out of reach: straight away from the enemies, past the farthest leash.
        float needed = 0;
        foreach ((Vector3 anchor, float safe) in _zones) needed = MathF.Max(needed, safe - Vector3.Distance(here, anchor));
        return here + (Away(player) * MathF.Max(ExtendYards, needed + LeashMarginYards));
    }

    private bool Safe(Vector3 point)
    {
        foreach ((Vector3 anchor, float safe) in _zones)
            if (Vector3.Distance(point, anchor) < safe) return false;
        return true;
    }

    /// <summary>A route to <paramref name="goal"/>: the navigation's, else back along the trail itself.</summary>
    private PlayerbotRoute? Plan(Player player, Vector3 goal, IReadOnlyList<Vector3> crumbs)
    {
        if (PlayerbotNavigation.TryPlan(player, goal, options, out PlayerbotRoute? route)
            || PlayerbotNavigation.TryPlanToward(player, goal, options, out route))
            return route;
        int end = -1;
        for (int index = crumbs.Count - 1; index >= 0; index--)
            if (crumbs[index] == goal) { end = index; break; }
        if (end < 0) return null;
        var points = new List<Vector3> { new(player.X, player.Y, player.Z) };
        float length = 0;
        for (int index = crumbs.Count - 1; index >= end; index--)
        {
            length += Vector3.Distance(points[^1], crumbs[index]);
            points.Add(crumbs[index]);
        }

        return points.Count >= 2 ? new PlayerbotRoute(points, length) : null;
    }

    private Vector3 Centroid(Player player)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (Creature enemy in _enemies)
        {
            if (!ReferenceEquals(enemy.Map, player.Map)) continue;
            sum += new Vector3(enemy.X, enemy.Y, enemy.Z);
            count++;
        }

        if (count == 0)
            foreach ((Vector3 anchor, _) in _zones) { sum += anchor; count++; }
        return count == 0 ? new Vector3(player.X, player.Y, player.Z) : sum / count;
    }

    /// <summary>The flat direction from the enemies to the bot (behind the bot when they stand on it).</summary>
    private Vector3 Away(Player player)
    {
        Vector3 from = Centroid(player);
        var flat = new Vector3(player.X - from.X, player.Y - from.Y, 0);
        if (flat.Length() < 0.5f) flat = new Vector3(-MathF.Cos(player.Orientation), -MathF.Sin(player.Orientation), 0);
        return Vector3.Normalize(flat);
    }

    private static float Distance(WorldObject a, WorldObject b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));
}
