using ArcaneCore.Game.Maps;
using ArcaneCore.World.Playerbots.Combat;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A bot's risk against reward (<see cref="PlayerbotRiskOptions"/>, docs/areas/playbots-risk.md): the world side of
/// <see cref="PlayerbotRiskModel"/>. Before a pull it gathers the facts of each candidate (who would join it, by the creatures' own
/// aggro and assistance rules, its health, the bot's health, mana, damage and escapes, the experience, quest credit and loot it
/// gives, and what the bot remembers) and takes the first one worth it, walks round a pack on the way, rests first, or passes. In a
/// fight it watches the damage rates (<see cref="PlayerbotFightTracker"/>) and retreats from a lost one
/// (<see cref="PlayerbotRetreat"/>), remembering the creatures and the place (<see cref="PlayerbotDangerMemory"/>), then recovers to
/// <see cref="PlayerbotRiskOptions.RecoverHealthPct"/> before it pulls again. World thread; owned by one bot's brain.
/// </summary>
internal sealed class PlayerbotRisk
{
    /// <summary>At most this many candidates are weighed per decision (each costs a path query).</summary>
    internal const int MaxCandidates = 5;

    /// <summary>A verdict on a creature stands this long before it is weighed again.</summary>
    internal const uint VerdictMs = 5_000;

    /// <summary>The longest wait to recover (out of combat) after a retreat or before a pull.</summary>
    internal const uint MaxWaitMs = 90_000;

    /// <summary>Non-objectives at most this many levels above the bot are weighed at all (a red creature is not).</summary>
    internal const int MaxLevelsAbove = 3;

    private const uint SkinningSkill = 393;
    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;
    private readonly PlayerbotCombatSpells _spells;
    private readonly Dictionary<(ObjectGuid Creature, bool Quest), (PlayerbotEngagement Verdict, uint UntilMs)> _verdicts = [];
    private uint _waitUntilMs;
    private bool _cornered;
    private (ObjectGuid Guid, uint Entry)[] _fledFrom = [];
    private (uint MapId, Vector3 Where) _fledAt;

    internal PlayerbotRisk(WorldSession session, PlayerbotOptions options, PlayerbotCombatSpells spells)
    {
        _session = session;
        _options = options;
        _spells = spells;
        Retreat = new PlayerbotRetreat(session, options, spells);
    }

    internal bool Enabled => _options.Risk.Enabled;

    internal PlayerbotBreadcrumbs Breadcrumbs { get; } = new();

    internal PlayerbotFightTracker Tracker { get; } = new();

    internal PlayerbotDangerMemory Memory { get; } = new();

    /// <summary>The places the bot keeps out of on its walks (<see cref="PlayerbotHazards"/>).</summary>
    internal PlayerbotHazards Hazards { get; } = new();

    /// <summary>How often the visible creatures are looked over for hazards.</summary>
    internal const uint HazardScanMs = 2_000;
    private uint _nextScanMs;

    internal PlayerbotRetreat Retreat { get; }

    /// <summary>The last pre-engagement verdict (the target taken, or the best one passed over).</summary>
    internal PlayerbotEngagement? LastEngagement { get; private set; }

    /// <summary>A route round the creatures on the way to the target just chosen (taken once by the brain).</summary>
    internal PlayerbotRoute? Detour { get; set; }

    /// <summary>Waiting to recover before the next pull.</summary>
    internal bool Waiting => _waitUntilMs != 0;

    /// <summary>The line BOTINSPECT and <c>.playerbot status</c> show: the retreat, else the fight, else the last pull verdict.</summary>
    internal string Report
    {
        get
        {
            if (Retreat.Active)
                return $"retreat reason={Retreat.Reason} escapes={(Retreat.UsedEscapes.Count == 0 ? "none" : string.Join('+', Retreat.UsedEscapes.Select(s => s.Replace(' ', '_'))))}";
            if (Waiting) return "recover " + (LastEngagement?.ToString() ?? $"after retreat ({Retreat.Reason})");
            if (Tracker.LastVerdict is { } fight) return fight.ToString();
            return LastEngagement?.ToString() ?? "decision=none";
        }
    }

    /// <summary>Lay the trail a retreat runs back along; out of combat, close a finished fight.</summary>
    internal void Track(Player player)
    {
        // The trail runs on through a pull (a bot is in combat from its first swing, often well short of where it fights).
        Breadcrumbs.Track(player);
        ScanHazards(player);
        if (player.Combat.IsInCombat) return;
        _cornered = false;
        if (Tracker.Active) Tracker.End();
    }

    /// <summary>The bot died: remember its killers and the place, and forget the fight.</summary>
    /// <param name="attackers">The creatures attacking the bot at its last living update (the server clears a dead player's attackers).</param>
    internal void OnDeath(Player player, IEnumerable<(ObjectGuid Guid, uint Entry)>? attackers = null)
    {
        // The killers: those attackers, and the enemies of the fight being watched (not those of an earlier one).
        (ObjectGuid Guid, uint Entry)[] killers = [.. (attackers ?? [])
            .Concat((Tracker.Active ? Tracker.LastEnemies : []).Select(c => (c.Guid, c.Entry))).Distinct()];
        if (Enabled && killers.Length > 0)
            Memory.Remember(player.MapId, new Vector3(player.X, player.Y, player.Z), killers, _session.World.NowMs,
                _options.Risk.DangerMemorySeconds);
        if (Enabled)
            Hazards.Add(player.MapId, new Vector3(player.X, player.Y, player.Z), PlayerbotHazards.DeathYards, _session.World.NowMs,
                _options.Risk.DangerMemorySeconds, "death");
        Retreat.Stop("died");
        Tracker.End();
        Breadcrumbs.Clear();
        Detour = null;
        _waitUntilMs = 0;
    }

    internal bool IsRemembered(Creature creature) => Enabled && Memory.IsRemembered(creature.Guid, _session.World.NowMs);

    // --- before a pull ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The first candidate (quest objectives first, then nearest) whose reward justifies its risk; null when none does. A candidate
    /// fine at full health and mana starts a wait to recover (<see cref="Waiting"/>) when nothing else is worth it.
    /// </summary>
    internal Creature? ChooseTarget(Player player, uint preferredEntry, Func<Creature, bool> skip, bool questObjective)
    {
        PlayerbotEngagement? first = null, rest = null;
        foreach (Creature candidate in PlayerbotBrain.FindTargets(player, preferredEntry,
                     creature => skip(creature) || IsRemembered(creature), MaxLevelsAbove).Take(MaxCandidates))
        {
            PlayerbotEngagement verdict = Assess(player, candidate, questObjective && candidate.Entry == preferredEntry, out PlayerbotRoute? detour);
            first ??= verdict;
            switch (verdict.Decision)
            {
                case PlayerbotEngageDecision.Engage:
                    LastEngagement = verdict;
                    return candidate;
                case PlayerbotEngageDecision.Detour when detour is not null:
                    LastEngagement = verdict;
                    Detour = detour;
                    return candidate;
                case PlayerbotEngageDecision.Rest:
                    rest ??= verdict;
                    break;
            }
        }

        LastEngagement = rest ?? first ?? LastEngagement;
        if (rest is not null) BeginWait();
        return null;
    }

    /// <summary>Start waiting to recover (at most <see cref="MaxWaitMs"/>).</summary>
    internal void BeginWait() => _waitUntilMs = Math.Max(1u, unchecked(_session.World.NowMs + MaxWaitMs));

    /// <summary>Still waiting: below the recovery threshold and within the wait. False ends the wait.</summary>
    internal bool KeepWaiting(Player player)
    {
        if (_waitUntilMs == 0) return false;
        bool timeLeft = unchecked((int)(_waitUntilMs - _session.World.NowMs)) > 0;
        if (Enabled && timeLeft && !Recovered(player)) return true;
        _waitUntilMs = 0;
        return false;
    }

    /// <summary>Health and mana at or above <see cref="PlayerbotRiskOptions.RecoverHealthPct"/>.</summary>
    internal bool Recovered(Player player)
    {
        float wanted = _options.Risk.RecoverHealthPct;
        if (player.MaxHealth > 0 && player.Health * 100f / player.MaxHealth < wanted) return false;
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        return player.PowerType != PowerType.Mana || maxMana == 0 || SpellSystem.GetPower(player, PowerType.Mana) * 100f / maxMana >= wanted;
    }

    /// <summary>The verdict on one candidate (cached for <see cref="VerdictMs"/>); a detour route when the verdict is to walk round.</summary>
    internal PlayerbotEngagement Assess(Player player, Creature target, bool questObjective, out PlayerbotRoute? detour)
    {
        detour = null;
        uint now = _session.World.NowMs;
        if (_verdicts.TryGetValue((target.Guid, questObjective), out var cached) && unchecked((int)(cached.UntilMs - now)) > 0
            && cached.Verdict.Decision != PlayerbotEngageDecision.Detour)
            return cached.Verdict;
        if (_verdicts.Count >= 64)
            foreach ((ObjectGuid, bool) old in _verdicts.Where(v => unchecked((int)(v.Value.UntilMs - now)) <= 0).Select(v => v.Key).ToArray())
                _verdicts.Remove(old);

        PlayerbotEngagementFacts facts = Facts(player, target, questObjective, out Vector3 spot, out List<PlayerbotThreat> pathThreats);
        PlayerbotEngagement verdict = PlayerbotRiskModel.Assess(facts, _options.Risk);
        if (verdict.Decision == PlayerbotEngageDecision.Detour)
        {
            detour = FindDetour(player, target, spot, pathThreats);
            if (detour is null) verdict = verdict with { Decision = PlayerbotEngageDecision.Avoid, Reason = $"pack-of-{facts.Enemies.Count}" };
        }

        if (verdict.Decision == PlayerbotEngageDecision.Engage || verdict.Decision == PlayerbotEngageDecision.Detour)
            Tracker.Prime(facts.Enemies.Sum(e => e.Dps), facts.BotDps * PlayerbotRiskModel.ManaFactor(facts.ManaDependence, facts.BotManaPct));
        if (_verdicts.Count < 64) _verdicts[(target.Guid, questObjective)] = (verdict, unchecked(now + VerdictMs));
        return verdict;
    }

    private PlayerbotEngagementFacts Facts(Player player, Creature target, bool questObjective, out Vector3 spot,
        out List<PlayerbotThreat> pathThreats)
    {
        pathThreats = [];
        Map? map = player.Map;
        CreatureMapSystem? system = map?.FindUpdater<CreatureMapSystem>();
        Vector3 here = new(player.X, player.Y, player.Z), there = new(target.X, target.Y, target.Z);
        float range = _spells.PreferredRange(player);
        float distance = Vector3.Distance(here, there);
        spot = distance <= range || distance < 0.01f ? here : there + ((here - there) * (range / distance));

        var enemies = new List<RiskEnemy> { Enemy(target, RiskJoin.Target, player) };
        var counted = new HashSet<ObjectGuid> { target.Guid };
        List<PlayerbotThreat> threats = PlayerbotRecovery.Threats(player);

        // vmangos Creature::CallAssistance: the target calls idle creatures it may call within its assistance radius (a defensive
        // creature answers too, so this looks at every visible creature, not only the ones that aggro on sight).
        if (system is not null && map is not null && system.AssistanceRadiusOf(target.Template) is var assist && assist > 0)
            foreach (ObjectGuid guid in player.VisibleObjects)
                if (map.FindObject(guid) is Creature helper && !counted.Contains(helper.Guid) && helper.IsAlive
                    && system.CanAssist(helper, target, player, assist))
                {
                    counted.Add(helper.Guid);
                    enemies.Add(Enemy(helper, RiskJoin.Assist, player));
                }

        // vmangos BasicAI::MoveInLineOfSight: any creature whose aggro radius covers where the bot will fight.
        foreach (PlayerbotThreat threat in threats)
            if (threat.Source is { } creature && !counted.Contains(creature.Guid) && threat.Reaches(spot, 0f))
            {
                counted.Add(creature.Guid);
                enemies.Add(Enemy(creature, RiskJoin.FightSpot, player));
            }

        // ... and those whose radius covers the way there (the planned route, sampled every 3 yards).
        if (PlayerbotNavigation.TryPlan(player, spot, _options, out PlayerbotRoute? approach) && approach is not null)
            foreach (PlayerbotThreat threat in threats)
                if (threat.Source is { } creature && !counted.Contains(creature.Guid) && Covers(threat, approach.Points))
                {
                    counted.Add(creature.Guid);
                    enemies.Add(Enemy(creature, RiskJoin.Path, player));
                    pathThreats.Add(threat);
                }

        float? mana = null;
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        if (player.PowerType == PowerType.Mana && maxMana > 0) mana = SpellSystem.GetPower(player, PowerType.Mana) * 100f / maxMana;
        uint now = _session.World.NowMs;
        return new PlayerbotEngagementFacts
        {
            BotLevel = player.Level,
            BotHealth = player.Health,
            BotMaxHealth = player.MaxHealth,
            BotManaPct = mana,
            ManaDependence = ManaDependence(player.Class),
            BotDps = Tracker.ObservedDps ?? PlayerbotRiskModel.PriorBotDps(player.Level),
            ReadyEscapes = ReadyEscapes(player),
            Enemies = enemies,
            QuestObjective = questObjective,
            LootValue = LootValue(player, target.Template),
            DangerHits = Enabled ? Memory.Hits(player.MapId, there, target.Entry, now) : 0,
            Remembered = IsRemembered(target),
        };
    }

    private RiskEnemy Enemy(Creature creature, RiskJoin join, Player bot)
    {
        CreatureTemplate t = creature.Template;
        bool elite = t.Rank is 1 or 2 or 3;
        CreatureSpellThreat spells = SpellThreat(creature);
        return new RiskEnemy(creature.Entry, creature.Level, t.Rank, creature.Health,
            PlayerbotRiskModel.CreatureDps(t.MinMeleeDamage, t.MaxMeleeDamage, t.MeleeBaseAttackTime, creature.Level, elite) + spells.Dps,
            join, spells.IsLethalTo(bot.MaxHealth));
    }

    /// <summary>The creature's own spells (<see cref="PlayerbotCreatureSpells"/>).</summary>
    internal CreatureSpellThreat SpellThreat(Creature creature)
        => PlayerbotCreatureSpells.Of(creature, _session.Services.GetService<SpellFeature>()?.System.Store);

    private static bool Covers(PlayerbotThreat threat, IReadOnlyList<Vector3> points)
    {
        for (int index = 1; index < points.Count; index++)
        {
            Vector3 a = points[index - 1], b = points[index];
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / 3f));
            for (int step = 0; step <= steps; step++)
                if (threat.Reaches(Vector3.Lerp(a, b, (float)step / steps), 0f)) return true;
        }

        return false;
    }

    /// <summary>
    /// A way round the creatures on the approach: a waypoint to either side of the straight line (15, 25 or 35 yards off its
    /// middle), reached on the navigation, from which the route and the last straight leg to the fight spot stay out of every
    /// path creature's aggro reach (with a 2-yard margin). Null when none does.
    /// </summary>
    private PlayerbotRoute? FindDetour(Player player, Creature target, Vector3 spot, List<PlayerbotThreat> pathThreats)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        Vector3 flat = new(spot.X - here.X, spot.Y - here.Y, 0);
        if (flat.Length() < 1f) return null;
        Vector3 side = Vector3.Normalize(new Vector3(-flat.Y, flat.X, 0));
        Vector3 middle = (here + spot) / 2f;
        foreach (float offset in (float[])[15f, 25f, 35f])
            foreach (float sign in (float[])[1f, -1f])
            {
                Vector3 waypoint = middle + (side * offset * sign);
                if (!PlayerbotNavigation.TryPlan(player, waypoint, _options, out PlayerbotRoute? route) || route is null) continue;
                List<Vector3> points = [.. route.Points, spot];
                if (pathThreats.Any(threat => Covers(threat with { Radius = threat.Radius + 2f }, points))) continue;
                return route;
            }

        return null;
    }

    private int ReadyEscapes(Player player)
    {
        PlayerbotAbilities escapes = _spells.Escapes(player);
        int ready = 0;
        foreach (string name in PlayerbotEscapes.All)
            if (escapes[name] is { } spell && spell.Effects.All(e => e.TargetA != SpellImplicitTarget.UnitEnemy) && _spells.CanCastNow(player, spell, player))
                ready++;
        return ready;
    }

    /// <summary>How much of a class's damage needs mana: casters all of it, hybrids part, rage and energy none.</summary>
    internal static float ManaDependence(Class playerClass) => playerClass switch
    {
        Class.Mage or Class.Priest or Class.Warlock => 1f,
        Class.Druid or Class.Shaman or Class.Paladin => 0.4f,
        Class.Hunter => 0.3f,
        _ => 0f,
    };

    /// <summary>Loot and skinning: a humanoid carries coin and cloth, a beast meat and a hide (more for a skinner).</summary>
    private static float LootValue(Player player, CreatureTemplate template) => template.CreatureType switch
    {
        7 => 0.2f,
        1 => player.Skills?.Has(SkinningSkill) == true ? 0.3f : 0.1f,
        _ => 0.05f,
    };

    // --- hazards on the way -------------------------------------------------------------------------------------

    /// <summary>
    /// Every <see cref="HazardScanMs"/> (alive or a ghost): each visible creature that would attack the bot and has a spell that kills it outright, or that
    /// the bot fled from or died to, is a hazard round its aggro reach (moving with it while seen).
    /// </summary>
    internal void ScanHazards(Player player)
    {
        if (!Enabled || !player.IsInWorld) return;
        uint now = _session.World.NowMs;
        if (unchecked((int)(_nextScanMs - now)) > 0) return;
        _nextScanMs = unchecked(now + HazardScanMs);
        int seconds = _options.Risk.DangerMemorySeconds;
        foreach (PlayerbotThreat threat in PlayerbotRecovery.Threats(player))
        {
            if (threat.Source is not { } creature) continue;
            if (SpellThreat(creature).IsLethalTo(player.MaxHealth))
                Hazards.Add(player.MapId, threat.Position, threat.Radius + PlayerbotHazards.LethalMarginYards, now, seconds, "lethal", creature.Guid);
            else if (Memory.IsRemembered(creature.Guid, now))
                Hazards.Add(player.MapId, threat.Position, threat.Radius + PlayerbotHazards.FledMarginYards, now, seconds, "fled", creature.Guid);
        }
    }

    /// <summary>
    /// Whether a living bot may walk <paramref name="points"/>: none of them inside a hazard it did not start in. A retreat walks
    /// where it must; a dead bot (a ghost) is not attacked.
    /// </summary>
    internal PlayerbotHazard? Blocking(Player player, IReadOnlyList<Vector3> points)
        => !Enabled || !player.IsAlive || Retreat.Active ? null : Hazards.FirstOnRoute(player.MapId, points, _session.World.NowMs);

    /// <summary>
    /// The hazards as threats for the ghost's revive spot (<see cref="PlayerbotRecovery"/>): a revive in reach of a creature that kills
    /// outright, or of one the bot fled from or died to, is camped. Remembered places are not: the body lies at the place of death
    /// itself, and a camp that has gone is no reason to give the body up.
    /// </summary>
    internal IEnumerable<PlayerbotThreat> HazardThreats(Player player)
        => !Enabled ? [] : Hazards.Active(player.MapId, _session.World.NowMs).Where(h => !h.Creature.IsEmpty)
            .Select(h => new PlayerbotThreat(h.At, h.Radius, IgnoresHeight: true, Radii: 0));

    // --- in a fight ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One look at the fight against <paramref name="target"/>: the verdict from the observed rates; a losing bot starts a retreat
    /// (true). Only while the bot is in combat.
    /// </summary>
    internal bool ObserveFight(Player player, Creature target)
    {
        // A cornered bot (its retreat did not shake the pursuers off) fights this one out.
        if (!Enabled || !player.Combat.IsInCombat || Retreat.Active || _cornered) return false;
        var enemies = new List<Creature>();
        if (target.IsAlive && ReferenceEquals(target.Map, player.Map)) enemies.Add(target);
        foreach (Unit unit in player.Combat.Attackers.Concat(player.Combat.ThreatenedBy))
            if (unit is Creature creature && creature.IsAlive && ReferenceEquals(creature.Map, player.Map)
                && !creature.IsInEvadeMode && !enemies.Contains(creature))
                enemies.Add(creature);
        // Until the window is long enough the estimate's priors stand in: the creatures' template damage and the bot's damage.
        float priorIn = enemies.Sum(e => Enemy(e, RiskJoin.Target, player).Dps);
        float priorOut = Tracker.ObservedDps ?? PlayerbotRiskModel.PriorBotDps(player.Level);
        PlayerbotFightFacts facts = Tracker.Observe(player, enemies, target, _session.World.NowMs, priorIn, priorOut)
            with { Lethal = enemies.Any(e => SpellThreat(e).IsLethalTo(player.MaxHealth)) };
        PlayerbotFightVerdict verdict = PlayerbotRiskModel.Judge(facts, _options.Risk);
        Tracker.Record(verdict);
        if (!verdict.Retreat) return false;
        StartRetreat(player, enemies, verdict.Reason);
        return true;
    }

    /// <summary>
    /// Retreat from <paramref name="enemies"/> (also the party AI's wipe). When it ends the bot remembers them and the place where
    /// the fight turned, from then on (a death on the way is remembered by <see cref="OnDeath"/>).
    /// </summary>
    internal void StartRetreat(Player player, IReadOnlyList<Creature> enemies, string reason)
    {
        _fledFrom = [.. enemies.Select(c => (c.Guid, c.Entry))];
        _fledAt = (player.MapId, new Vector3(player.X, player.Y, player.Z));
        foreach (Creature enemy in enemies)
        {
            _verdicts.Remove((enemy.Guid, false));
            _verdicts.Remove((enemy.Guid, true));
        }
        Detour = null;
        Retreat.Start(player, enemies, Tracker.Anchors, Breadcrumbs.Points, reason);
    }

    /// <summary>One think of a running retreat; true while it owns the bot. A retreat that got away starts the recovery wait.</summary>
    internal bool UpdateRetreat(Player player, uint interval)
    {
        if (!Retreat.Active) return false;
        if (Retreat.Update(player, interval)) return true;
        if (Retreat.Outcome != "died")
        {
            Memory.Remember(_fledAt.MapId, _fledAt.Where, _fledFrom, _session.World.NowMs, _options.Risk.DangerMemorySeconds);
            Hazards.Add(_fledAt.MapId, _fledAt.Where, PlayerbotHazards.RetreatYards, _session.World.NowMs, _options.Risk.DangerMemorySeconds, "retreat");
        }
        Tracker.End();
        if (Retreat.Outcome == "safe") BeginWait();
        else if (Retreat.Outcome == "cornered") _cornered = true;
        return false;
    }
}
