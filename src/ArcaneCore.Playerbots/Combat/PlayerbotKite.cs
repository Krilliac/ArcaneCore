using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// Kiting for one bot (<c>World:Playerbots:Combat:Kite</c>; the choice is <see cref="PlayerbotKiteModel"/>): a hurt bot that can attack
/// from range slows the creature on it, then walks a short leg back out to its casting range, so the rotation keeps casting in the
/// same think it arrives. One instance per brain, party AI and group AI (the battleground AI does not kite). It is the fight-on branch
/// of the risk model: the caller runs <c>PlayerbotRisk.ObserveFight</c> first and a retreat always wins (a running retreat drops a
/// step under way).
/// <para>
/// State: this object owns its route and the per-fight counters; everything runs on the world thread. A fight is a stretch of
/// continuous combat: the counters reset when the bot leaves combat, dies, or the option is turned off.
/// </para>
/// <para>
/// Cost: the per-think gates allocate nothing (a handful of field reads, a walk of the attacker set with its struct enumerator).
/// Only a think that actually decides to step searches for a spot: at most seven directions, each a few floor probes, one clipped
/// line-of-sight cast and a sampled walk over the visible aggro radii; no path search and no I/O. A step is at most once every
/// <see cref="PlayerbotKiteModel.StepCooldownMs"/> and at most <c>MaxStepsPerFight</c> times.
/// </para>
/// </summary>
internal sealed class PlayerbotKite(WorldSession session, PlayerbotOptions options, PlayerbotCombatSpells spells)
{
    private const uint ItemClassWeapon = 2;
    private const uint SubClassWand = 19;
    private const uint SubClassThrown = 16;

    private static readonly IReadOnlyDictionary<ObjectGuid, Vector3> NoAnchors = new Dictionary<ObjectGuid, Vector3>();

    private readonly List<Creature> _pursuers = [];
    private readonly List<PlayerbotThreat> _threats = [];
    private readonly Vector3[] _leg = new Vector3[2];

    private PlayerbotRoute? _routeSlot;
    private int _steps;
    private int _failedSpots;
    private uint _stepEndedMs;
    private bool _hasStepped;
    private uint _kiteStartMs;
    private bool _over;
    private uint _lastUpdateMs;

    /// <summary>A gap this long between two kite thinks (the bot was elsewhere) starts a new fight's counters.</summary>
    internal const uint FightGapMs = 10_000;

    private PlayerbotAbilities? _book;
    private SpellInfo? _nova;
    private SpellInfo[] _slows = [];
    private SpellInfo? _ready;

    // Scratch for the attacker scan (no allocation per think).
    private Creature? _nearest;
    private float _nearestYards;
    private bool _anyEvading;
    private float _scanStart;

    // Read by the cached slow filter (Frost Nova needs the pursuer within its radius).
    private float _slowTargetYards;

    /// <summary>Steps taken in the current fight (inspection and tests).</summary>
    internal int Steps => _steps;

    /// <summary>Kiting ended for this fight, by a give-up or an evading pursuer (inspection and tests).</summary>
    internal bool Over => _over;

    /// <summary>Whether a step is being walked.</summary>
    internal bool Walking => _routeSlot is { Complete: false };

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

    /// <summary>
    /// One think of the kite against <paramref name="victim"/> (the fight's target). True: the kite owns this think (a slow was cast, or a
    /// step is being walked); the caller keeps its goal on the fight and returns. False: fight as usual (the arrival think also answers
    /// false, after facing the victim, so the rotation casts at once).
    /// </summary>
    /// <param name="leashCentre">A party or group bot's leash centre (its master or leader, a siege waypoint), or null (the brain).</param>
    /// <param name="leashYards">The yards from <paramref name="leashCentre"/> a step may end within.</param>
    /// <param name="risk">The bot's risk object (null: no hazard check, no fight anchors).</param>
    internal bool Update(Player player, Unit victim, uint interval, Vector3? leashCentre, float leashYards, PlayerbotRisk? risk)
    {
        PlayerbotKiteOptions kite = options.Combat.Kite;
        uint now = session.World.NowMs;
        if (!kite.Enabled || !player.IsAlive || !player.Combat.IsInCombat)
        {
            EndFight(player, stop: false);
            return false;
        }

        if (_lastUpdateMs != 0 && unchecked(now - _lastUpdateMs) > FightGapMs) EndFight(player, stop: true);
        _lastUpdateMs = Math.Max(1u, now);

        if (_over) return false;
        if (risk is { Retreat.Active: true })
        {
            EndStep(player, stop: false);
            return false;
        }

        if (_routeSlot is not null) return Continue(player, victim, interval);

        if (player.MaxHealth == 0) return false;
        float healthPct = player.Health * 100f / player.MaxHealth;
        if (healthPct > kite.HealthPct) return false;
        if (player.Map is not { } map || !ReferenceEquals(victim.Map, map)) return false;
        if (PlayerbotMotion.ControlLost(player) || (player.Movement.Flags & MovementFlags.Swimming) != 0) return false;
        if (session.Services.GetService<SpellFeature>() is not { } feature) return false;
        if (feature.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting }) return false;

        bool hunter = player.Class == Class.Hunter;
        int pursuers = Scan(player, hunter);
        if (_anyEvading)
        {
            Finish(player, report: false, "a pursuer is evading");
            return false;
        }

        if (pursuers == 0 || _nearest is not { } nearest) return false;
        if (!Qualifies(player, out float preferredRange)) return false;
        if (PlayerbotWater.Submerged(player) || feature.System.IsRooted(player)) return false;

        ResolveSlows(player);
        _slowTargetYards = _nearestYards;
        bool slowReady = pursuers <= PlayerbotKiteModel.MaxPursuers && SlowReady(player, nearest);
        bool caster = CastsSpells(nearest, feature);
        var facts = new KiteFacts(healthPct, kite.HealthPct, pursuers, caster, feature.System.IsRooted(nearest), nearest.RunSpeed,
            player.RunSpeed, slowReady, _hasStepped ? unchecked(now - _stepEndedMs) : uint.MaxValue, _steps, kite.MaxStepsPerFight,
            _kiteStartMs == 0 ? 0u : unchecked(now - _kiteStartMs), _failedSpots);
        switch (PlayerbotKiteModel.Decide(facts))
        {
            case KiteDecision.GiveUp:
                Finish(player, report: true, $"steps {_steps}/{kite.MaxStepsPerFight}, {facts.KiteMs} ms, failed spots {_failedSpots}");
                return false;
            case KiteDecision.Slow:
                if (_kiteStartMs == 0) _kiteStartMs = Math.Max(1u, now);
                return CastSlow(player, nearest);
            case KiteDecision.Step:
                if (_kiteStartMs == 0) _kiteStartMs = Math.Max(1u, now);
                return BeginStep(player, map, victim, hunter, preferredRange, leashCentre, leashYards, risk, interval, kite);
            default:
                return false;
        }
    }

    /// <summary>Forget the fight: the bot died, left combat or was reset.</summary>
    internal void Reset(Player? player = null) => EndFight(player, stop: false);

    // --- the step under way --------------------------------------------------------------------------------------------------

    private bool Continue(Player player, Unit victim, uint interval)
    {
        PlayerbotRoute route = _routeSlot!;
        if (PlayerbotMotion.ControlLost(player) || (player.Movement.Flags & MovementFlags.Swimming) != 0
            || session.Services.GetService<SpellFeature>() is not { } feature
            || feature.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting }
            || feature.System.IsRooted(player))
        {
            EndStep(player, stop: true);
            return false;
        }

        bool advanced = PlayerbotNavigation.TryAdvance(session, route, options, interval, session.World.NowMs);
        if (advanced && !route.Complete) return true;

        // The walk is over: arrived (the route is complete) or refused (the motion would not take it).
        bool arrived = route.Complete;
        EndStep(player, stop: true);
        if (arrived) _failedSpots = 0;
        else _failedSpots = PlayerbotKiteModel.MaxFailedSpots;
        Face(player, victim);
        return false;
    }

    private void EndStep(Player player, bool stop)
    {
        if (_routeSlot is null) return;
        _route = null;
        _stepEndedMs = session.World.NowMs;
        _hasStepped = true;
        if (stop && player.IsAlive) PlayerbotMovementControl.Stop(session, player);
    }

    private void Face(Player player, Unit victim)
    {
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0 && !PlayerbotMovementControl.Stop(session, player)) return;
        PlayerbotMotion.Face(session, player, MathF.Atan2(victim.Y - player.Y, victim.X - player.X));
    }

    private bool BeginStep(Player player, Map map, Unit victim, bool hunter, float preferredRange, Vector3? leashCentre, float leashYards,
        PlayerbotRisk? risk, uint interval, PlayerbotKiteOptions kite)
    {
        uint now = session.World.NowMs;
        if (!FindSpot(player, map, victim, hunter, preferredRange, leashCentre, leashYards, risk, kite, out Vector3 reach))
        {
            _failedSpots++;
            _stepEndedMs = now;
            _hasStepped = true;
            return false;
        }

        // The leg walks from where the bot is now (its in-flight motion position) to the accepted end.
        Vector3 here = MotionPosition(player);
        _route = new PlayerbotRoute([here, reach], Vector3.Distance(here, reach));
        _steps++;
        if (PlayerbotNavigation.TryAdvance(session, _route, options, interval, now)) return true;

        // Refused at once: the walk cannot start from here.
        _route = null;
        _failedSpots = PlayerbotKiteModel.MaxFailedSpots;
        _stepEndedMs = now;
        _hasStepped = true;
        return false;
    }

    // --- the attackers ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The living, non-evading creatures attacking the bot within the kite start distance, into <see cref="_pursuers"/>; the nearest
    /// in <see cref="_nearest"/> and <see cref="_nearestYards"/>. Any attacker creature in evade mode sets <see cref="_anyEvading"/>.
    /// O(attackers) and allocation-free (the set is enumerated through its struct enumerator).
    /// </summary>
    private int Scan(Player player, bool hunter)
    {
        _pursuers.Clear();
        _nearest = null;
        _nearestYards = float.MaxValue;
        _anyEvading = false;
        _scanStart = PlayerbotKiteModel.StartYards(hunter);
        if (player.Combat.Attackers is HashSet<Unit> set)
        {
            foreach (Unit attacker in set) Consider(player, attacker);
        }
        else
        {
            foreach (Unit attacker in player.Combat.Attackers) Consider(player, attacker);
        }

        return _pursuers.Count;
    }

    private void Consider(Player player, Unit attacker)
    {
        if (attacker is not Creature creature || !creature.IsAlive || !ReferenceEquals(creature.Map, player.Map)) return;
        if (creature.IsInEvadeMode)
        {
            _anyEvading = true;
            return;
        }

        float yards = Distance(player, creature);
        if (yards > _scanStart) return;
        _pursuers.Add(creature);
        if (yards < _nearestYards)
        {
            _nearest = creature;
            _nearestYards = yards;
        }
    }

    private static bool CastsSpells(Creature creature, SpellFeature feature)
    {
        CreatureSpellThreat threat = PlayerbotCreatureSpells.Of(creature, feature.System.Store);
        return threat.Dps > 0f || threat.MaxHit > 0 || threat.Instakill;
    }

    // --- who may kite ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The bot fights from range (its rotation's preferred range is past melee), or is a warrior or rogue with a ranged weapon, its
    /// ammunition and a known shot. <paramref name="preferredRange"/> is where it wants to stand (a melee bot with a shot: the casting
    /// range of a hunter-less bot, <see cref="PlayerbotClassRotation.CasterRange"/>).
    /// </summary>
    private bool Qualifies(Player player, out float preferredRange)
    {
        preferredRange = spells.PreferredRange(player);
        if (preferredRange > PlayerbotClassRotation.MeleeRange) return true;
        if (player.Class is not (Class.Warrior or Class.Rogue)) return false;
        PlayerbotAbilities abilities = spells.Abilities(player);
        if (!abilities.Has(PlayerbotClassRotation.ShootBow) && !abilities.Has(PlayerbotClassRotation.ShootGun)
            && !abilities.Has(PlayerbotClassRotation.ShootCrossbow) && !abilities.Has(PlayerbotClassRotation.Throw))
            return false;
        if (!HasUsableRangedWeapon(player)) return false;
        preferredRange = PlayerbotClassRotation.CasterRange;
        return true;
    }

    private static bool HasUsableRangedWeapon(Player player)
    {
        Item? ranged = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged);
        if (ranged?.Template is not { Class: ItemClassWeapon } template || template.SubClass == SubClassWand) return false;
        if (ranged.MaxDurability > 0 && ranged.Durability == 0) return false;
        if (template.SubClass == SubClassThrown) return true;
        uint ammo = player.Inventory.AmmoId;
        return ammo != 0 && player.Inventory.GetItemCount(ammo) > 0;
    }

    // --- the slows ---------------------------------------------------------------------------------------------------------------

    /// <summary>The class's slows the bot knows whose spell data holds or slows (rebuilt only when the escape book is).</summary>
    private void ResolveSlows(Player player)
    {
        PlayerbotAbilities book = spells.Escapes(player);
        if (ReferenceEquals(book, _book)) return;
        _book = book;
        _nova = null;
        var found = new List<SpellInfo>(3);
        foreach (string name in PlayerbotKiteModel.SlowsOf(player.Class))
        {
            if (book[name] is not { } spell || !PlayerbotKiteModel.IsSlow(name, spell)) continue;
            if (name == PlayerbotEscapes.FrostNova) _nova = spell;
            else found.Add(spell);
        }

        _slows = [.. found];
    }

    /// <summary>Whether a slow can be cast now; the one to cast is kept in <see cref="_ready"/> (Frost Nova at the bot itself, the rest at the pursuer).</summary>
    private bool SlowReady(Player player, Creature nearest)
    {
        _ready = null;
        if (_nova is { } nova && _slowTargetYards <= PlayerbotKiteModel.FrostNovaYards && spells.CanCastNow(player, nova, player))
        {
            _ready = nova;
            return true;
        }

        if (_slows.Length > 0 && spells.FirstCastable(player, _slows, nearest) is { } slow)
        {
            _ready = slow;
            return true;
        }

        return false;
    }

    private bool CastSlow(Player player, Creature nearest)
        => _ready is { } slow && CastIfStill(player, slow, slow.Name == PlayerbotEscapes.FrostNova ? player : nearest);

    private bool CastIfStill(Player player, SpellInfo spell, Unit target)
    {
        // A cast with a cast time is made standing; the same stop-then-cast the rotation makes.
        if (spell.CastTime.Base > 0 && (player.Movement.Flags & MovementFlags.MaskMoving) != 0
            && (!PlayerbotMovementControl.Stop(session, player) || (player.Movement.Flags & MovementFlags.MaskMoving) != 0))
            return true;
        return spells.CastAt(player, spell, target);
    }

    // --- the spot ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The first of up to seven directions (away from the pursuers' centroid, then 35, 70 and 105 degrees either side) whose end
    /// satisfies the spot rules: ground within 3 yards of the bot's height (probed from 20 yards above, and from 2 for a cave, the
    /// nearer to the bot's height), a clipped walk of at least 75% of the leg, not under water, outside the aggro of every creature
    /// that is not in the fight, no hazard on the leg, inside every pursuer's leash zone less 8 yards (or no farther from its anchor
    /// than the bot is now), spell line of sight to the victim with the victim within casting range, and within the leash of the
    /// party or group centre. No path search.
    /// </summary>
    private bool FindSpot(Player player, Map map, Unit victim, bool hunter, float preferredRange, Vector3? leashCentre, float leashYards,
        PlayerbotRisk? risk, PlayerbotKiteOptions kite, out Vector3 reach)
    {
        reach = default;
        Vector3 here = MotionPosition(player);
        if (!here.IsFinite()) return false;

        Vector3 sum = Vector3.Zero;
        foreach (Creature pursuer in _pursuers) sum += new Vector3(pursuer.X, pursuer.Y, pursuer.Z);
        Vector3 centroid = sum / _pursuers.Count;
        var away = new Vector2(here.X - centroid.X, here.Y - centroid.Y);
        if (away.LengthSquared() < 0.25f) away = new Vector2(-MathF.Cos(player.Orientation), -MathF.Sin(player.Orientation));
        away = Vector2.Normalize(away);

        float wanted = PlayerbotKiteModel.WantedYards(preferredRange, hunter);
        float length = PlayerbotKiteModel.StepLength(wanted, _nearestYards, kite.StepYards);
        float castRange = MathF.Max(preferredRange, wanted);

        PlayerbotRecovery.Threats(player, _threats);
        var zones = PlayerbotRetreat.BuildZones(player, _pursuers, risk?.Tracker.Anchors ?? NoAnchors);
        var victimAt = new Vector3(victim.X, victim.Y, victim.Z);

        foreach (float degrees in PlayerbotKiteModel.FanDegrees)
        {
            Vector2 direction = PlayerbotKiteModel.Turned(away, degrees);
            float x = here.X + (direction.X * length), y = here.Y + (direction.Y * length);

            // (a) the floor under the end, near the bot's height.
            float floor = FloorNear(map, x, y, here.Z);
            if (PlayerbotNavigation.InvalidHeight(floor) || MathF.Abs(floor - here.Z) > PlayerbotKiteModel.MaxFloorDeltaYards) continue;
            var end = new Vector3(x, y, floor);

            // (b) the leg may be walked for most of its length.
            if (!PlayerbotMoveCheck.TryClip(map, here, end, out Vector3 clipped)
                || Vector2.Distance(Flat(here), Flat(clipped)) < length * PlayerbotKiteModel.MinClipFraction) continue;

            // (c) dry.
            if (PlayerbotWater.Submerged(map, clipped, player.Locomotion.CollisionHeight)) continue;

            // (d) nothing outside the fight wakes up along the leg or at its end.
            if (LegReachesThreat(player, here, clipped)) continue;

            // (e) no hazard (brain only).
            if (risk is not null)
            {
                _leg[0] = here;
                _leg[1] = clipped;
                if (risk.Blocking(player, _leg) is not null) continue;
            }

            // (f) the pursuers keep their grip: the end stays inside every leash zone, with room to spare.
            if (!InsideLeash(here, clipped, zones)) continue;

            // (g) the rotation can cast from there.
            if (Vector3.Distance(clipped, victimAt) > castRange || !PlayerbotMoveCheck.LineClear(map.Collision, clipped, victimAt)) continue;

            // (h) a party or group bot stays near its master or leader.
            if (leashCentre is { } centre && Vector3.Distance(clipped, centre) > leashYards) continue;

            reach = clipped;
            return true;
        }

        return false;
    }

    /// <summary>The floor at (x, y) nearest the bot's height among the probe from 20 yards above and the one from 2 (a cave's floor).</summary>
    private static float FloorNear(Map map, float x, float y, float z)
    {
        float high = map.Collision.GetHeight(x, y, z + 20f);
        float low = map.Collision.GetHeight(x, y, z);
        bool highValid = !PlayerbotNavigation.InvalidHeight(high), lowValid = !PlayerbotNavigation.InvalidHeight(low);
        if (highValid && lowValid) return MathF.Abs(high - z) <= MathF.Abs(low - z) ? high : low;
        return highValid ? high : lowValid ? low : high;
    }

    private bool LegReachesThreat(Player player, Vector3 from, Vector3 to)
    {
        if (_threats.Count == 0) return false;
        int samples = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to) / PlayerbotKiteModel.SampleYards));
        for (int sample = 1; sample <= samples; sample++)
        {
            Vector3 point = Vector3.Lerp(from, to, (float)sample / samples);
            foreach (PlayerbotThreat threat in _threats)
            {
                if (threat.Source is { } source && (player.Combat.Attackers.Contains(source) || player.Combat.ThreatenedBy.Contains(source))) continue;
                if (threat.Reaches(point, PlayerbotKiteModel.ThreatMarginYards)) return true;
            }
        }

        return false;
    }

    private static bool InsideLeash(Vector3 here, Vector3 end, List<(Vector3 Anchor, float Safe)> zones)
    {
        foreach ((Vector3 anchor, float safe) in zones)
        {
            float after = Vector3.Distance(end, anchor);
            if (after < safe - PlayerbotKiteModel.LeashMarginYards) continue;
            if (after <= Vector3.Distance(here, anchor)) continue; // already far out: a step that does not go farther is fine
            return false;
        }

        return true;
    }

    // --- the end of a kite ---------------------------------------------------------------------------------------------------

    private void Finish(Player player, bool report, string why)
    {
        EndStep(player, stop: true);
        _over = true;
        if (report)
            PlayerbotAnomalies.Report(player, "combat", "kite-gave-up", $"kiting given up for this fight: {why}", AnomalySeverity.Info);
    }

    private void EndFight(Player? player, bool stop)
    {
        if (_routeSlot is not null)
        {
            _route = null;
            if (stop && player is { IsAlive: true }) PlayerbotMovementControl.Stop(session, player);
        }

        _steps = 0;
        _failedSpots = 0;
        _stepEndedMs = 0;
        _hasStepped = false;
        _kiteStartMs = 0;
        _over = false;
    }
}
