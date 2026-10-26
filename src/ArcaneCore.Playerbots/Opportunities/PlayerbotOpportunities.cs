using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Bots.Progression;

namespace ArcaneCore.World.Playerbots;

/// <summary>What <see cref="PlayerbotOpportunities.Update"/> asks of the brain this think.</summary>
/// <param name="Busy">The controller drove the bot this think (walked, opened, waited): the brain does nothing else.</param>
/// <param name="Pull">A guard (or the rare itself) to take as the bot's target now: the brain fights it through its ordinary combat path.</param>
/// <param name="Abandon">A live guard (or rare) an earlier think handed the brain as its target and the episode has now given up on (it was
/// refused, set aside or failed): the brain drops it as its target unless the bot is already in combat with it.</param>
internal readonly record struct OpportunityStep(bool Busy, Creature? Pull = null, PlayerbotGoalKind Goal = PlayerbotGoalKind.Loot, uint TargetEntry = 0,
    Creature? Abandon = null)
{
    /// <summary>Nothing for the controller to do: the brain goes on down its ladder.</summary>
    internal static OpportunityStep None => default;
}

/// <summary>
/// The opportunity goal of one bot (docs/areas/playbots-opportunities.md): every <see cref="ScanEveryMs"/> it looks over what it sees for
/// chests it can open (<see cref="OpportunityKind.Chest"/>, <see cref="OpportunityKind.LockedChest"/>: a rogue with Pick Lock at the
/// lock's skill, or a bot that carries the key) and rare creatures with good loot, weighs each against the guards beside it
/// (<see cref="PlayerbotOpportunityModel"/>), and goes for the best one worth it: it clears the guards one pull at a time through the
/// brain's own combat path, then opens the chest with the ordinary game object use (or Pick Lock) and takes the loot the way the brain
/// takes any (<c>CMSG_AUTOSTORE_LOOT_ITEM</c>, <c>CMSG_LOOT_MONEY</c>, <c>CMSG_LOOT_RELEASE</c> through the brain's loot queue).
/// <list type="bullet">
/// <item>Gathering nodes are <see cref="PlayerbotCrafting"/>'s: this adds only the risk side (<see cref="NodeVeto"/>, asked before it walks to a node).</item>
/// <item>A fight that goes badly (a retreat or a death in the bot's <see cref="PlayerbotDangerMemory"/>), an unreachable find or a timeout
/// sets the find aside for 60 s, doubling to 5 min (<see cref="PlayerbotOpportunityLedger"/>); a find taken is looked at again after its respawn;
/// one someone else took is dropped at once; one another bot claims (<see cref="PlayerbotClaims"/>) or a player stands at is skipped.</item>
/// <item>Anomalies (area <c>opportunity</c>): <c>chest-no-loot</c> (the chest never answered), <c>chest-empty</c> (it opened and held nothing),
/// <c>chest-pick-failed</c> (Pick Lock failed for a reason a second cast does not mend, or on every one of 8 casts),
/// <c>unreachable</c> (no route), once per episode; nothing on success.</item>
/// </list>
/// Thread affinity: world thread only, owned by one brain. Cost: a scan every 2 seconds over the visible objects (a type test each), a
/// loot estimate for at most <see cref="MaxCandidates"/> finds, cached a minute; nothing per think while idle.
/// </summary>
internal sealed partial class PlayerbotOpportunities
{
    /// <summary>How often the visible objects are looked over.</summary>
    internal const uint ScanEveryMs = 2_000;

    /// <summary>The finds weighed in one scan (the nearest).</summary>
    internal const int MaxCandidates = 4;

    /// <summary>The bot opens a chest from this close (centre to centre; the server allows 5).</summary>
    internal const float InteractYards = 4f;

    /// <summary>An episode that has not finished in this long is given up.</summary>
    internal const uint EpisodeMaxMs = 180_000;

    /// <summary>How long the bot waits for the loot window after it used the chest.</summary>
    internal const uint AwaitLootMs = 8_000;

    /// <summary>Pick Lock casts tried on one chest before it is given up (a pick can fail with "try again", as a player clicks again).</summary>
    internal const int MaxPickAttempts = 8;

    /// <summary>How long a node's verdict is kept (<see cref="NodeVeto"/>).</summary>
    internal const uint VetoCacheMs = 10_000;

    /// <summary>How long a find another bot or a player is on is left alone.</summary>
    internal const uint ContestedMs = 30_000;

    /// <summary>A find the bot weighed and refused is not weighed again for this long (the scan looks every <see cref="ScanEveryMs"/>).</summary>
    internal const uint RefusedRecheckMs = 20_000;

    /// <summary>A player this near the find is on it.</summary>
    internal const float PlayerNearYards = 12f;

    /// <summary>The loot estimate of a table is kept this long.</summary>
    internal const uint LootCacheMs = 60_000;

    private const uint PickLockSpellId = 1804;

    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;
    private readonly Func<PlayerbotRisk> _risk;
    private readonly PlayerbotClaims _claims;
    private readonly Action<WorldOpcode, byte[]> _lootSink;
    private readonly PlayerbotOpportunityLedger _ledger = new();
    private readonly Dictionary<(LootTableKind Kind, uint Entry), (float Units, uint UntilMs)> _lootCache = [];
    private readonly Dictionary<ulong, (bool Veto, uint UntilMs)> _vetoes = [];
    private Episode? _episode;
    private Creature? _released;
    private bool _scanned;
    private uint _nextScanMs;
    private TileWait _tileWait;
    private PlayerbotRoute? _route;
    private ObjectGuid _routeTarget;

    internal PlayerbotOpportunities(WorldSession session, PlayerbotOptions options, Func<PlayerbotRisk> risk, PlayerbotClaims? claims,
        Action<WorldOpcode, byte[]> lootSink)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _risk = risk ?? throw new ArgumentNullException(nameof(risk));
        _lootSink = lootSink ?? throw new ArgumentNullException(nameof(lootSink));
        _claims = claims ?? new PlayerbotClaims(64);
        NodeVeto = VetoNode;
    }

    /// <summary>
    /// The brain's filter for creatures it will not take as a target (unreachable, set aside as someone else's, outside the home area:
    /// <c>PlayerbotBrain.IsUntakeable</c>). A guard the controller needs to pull that this refuses ends the episode as unreachable, so a
    /// pull the brain would never carry out is not issued think after think. Null: every guard is takeable.
    /// </summary>
    internal Func<Player, Creature, bool>? Untakeable { get; set; }

    /// <summary>The clock the controller reads (world milliseconds); tests replace it to step through backoffs.</summary>
    internal Func<uint>? Clock { get; set; }

    private uint Now => Clock?.Invoke() ?? _session.World.NowMs;

    /// <summary>The bot's home area while it is on its home map: finds outside it are left alone (as <see cref="PlayerbotCrafting.Home"/>).</summary>
    internal PlayerbotHomeArea? Home { get; set; }

    /// <summary>The find the bot is on, or null (inspection and tests).</summary>
    internal ulong? ActiveKey => _episode?.Key;

    /// <summary>The verdict of the find the bot is on, or the last one it refused (inspection and tests).</summary>
    internal OpportunityVerdict? LastVerdict { get; private set; }

    /// <summary>What the bot sets aside (tests).</summary>
    internal PlayerbotOpportunityLedger Ledger => _ledger;

    /// <summary>
    /// Where each opportunity decision and its outcome are reported for the policy telemetry (world thread, inside a think). Null unless the
    /// telemetry is on. Calls are guarded: a sink fault never changes a decision.
    /// </summary>
    internal Policy.IPlayerbotOpportunitySink? Sink { get; set; }

    private readonly List<Find> _probe = new(MaxCandidates);

    /// <summary>
    /// Whether a find is in reach for the goal policy's open-goal probe (world thread, once per policy snapshot): the bot is on one, or
    /// the scan sees a find that is not set aside. It does not weigh the find (the controller does that when it takes the goal, and sets a
    /// refused find aside, so the next probe skips it). Reuses one list, so the probe allocates no list (the scan itself runs over the bot's visible objects).
    /// </summary>
    internal bool HasCandidate(Player player)
    {
        PlayerbotOpportunityOptions options = _options.Opportunities;
        if (!options.Enabled || !player.IsInWorld || !player.IsAlive || player.Map is not { } map) return false;
        if (_episode is not null) return true;
        if (_session.ManagedBudget is not { Remaining: > 0 } || PlayerbotTownGoals.FreeBagSlots(player) <= 0) return false;
        _probe.Clear();
        CollectFinds(player, map, Now, options, _probe);
        bool any = _probe.Count > 0;
        _probe.Clear();
        return any;
    }

    /// <summary>The find in progress, or the last verdict (tests and debugging).</summary>
    internal string Report => _episode is { } e
        ? $"opportunity={e.Kind.ToString().ToLowerInvariant()} entry={e.Entry} phase={e.Phase.ToString().ToLowerInvariant()}"
        : LastVerdict is { } last ? last.ToString() : "opportunity=none";

    /// <summary>
    /// The risk side of gathering (<see cref="PlayerbotCrafting.GatherVeto"/>): true when the node is guarded and the guards are not
    /// worth it as the bot is now. An unguarded node is never vetoed (its worth is the gathering's own business).
    /// </summary>
    internal Func<GameObject, bool> NodeVeto { get; }

    // --- the think --------------------------------------------------------------------------------------------------

    /// <summary>
    /// One think, out of combat, after the urgent goals. <paramref name="interrupting"/> says the bot would drop a quest or grind goal for
    /// a find (it then asks more of it, <see cref="PlayerbotOpportunityModel.InterruptValueFactor"/>).
    /// </summary>
    internal OpportunityStep Update(Player player, uint elapsedMs, bool interrupting)
    {
        ArgumentNullException.ThrowIfNull(player);
        OpportunityStep step = Think(player, elapsedMs, interrupting);
        if (_released is { } released)
        {
            // The episode that handed the brain this guard as its target ended without a fight: the brain lets go of it.
            _released = null;
            step = step with { Abandon = released };
        }

        return step;
    }

    /// <summary>
    /// Called by the brain at every think. Keeps the claim on the find alive only while the bot is in combat with a guard of the episode
    /// (the brain's combat path does not ask for a think, and a fight outlasts <see cref="PlayerbotClaims.ClaimLifeMs"/>), and only
    /// within <see cref="EpisodeMaxMs"/>: past that the episode ends as a timeout, with the backoff. An episode the goal has not
    /// stepped for <see cref="PlayerbotClaims.ClaimLifeMs"/> outside such a fight (a higher goal holds the think) is ended and its claim
    /// left to lapse, so other bots are not kept off the find. Returns the guard the brain was handed and must now let go of, or null.
    /// </summary>
    internal Creature? KeepClaim(Player player)
    {
        if (_episode is not { } episode) return null;
        uint now = Now;
        if (unchecked(now - episode.StartedMs) > EpisodeMaxMs)
        {
            _ledger.Defer(episode.Key, now);
            LastVerdict = new OpportunityVerdict(OpportunityDecision.Refuse, "timeout", 0f, 0f, 0f);
            return Release();
        }

        bool fighting = episode.Pull is { IsAlive: true } guard
            && (player.Combat.ThreatenedBy.Contains(guard) || ReferenceEquals(guard.Combat.Victim, player));
        if (fighting)
        {
            _claims.Refresh(episode.Key, episode.ClaimEpoch, now);
            return null;
        }

        return unchecked(now - episode.LastStepMs) > PlayerbotClaims.ClaimLifeMs ? Release() : null;
    }

    private Creature? Release()
    {
        EndEpisode();
        Creature? released = _released;
        _released = null;
        return released;
    }

    /// <summary>The bot died: the find it was on is set aside with the failure backoff (the fight went as badly as it can).</summary>
    internal void BotDied()
    {
        if (_episode is not { } episode) return;
        _ledger.Defer(episode.Key, Now);
        LastVerdict = new OpportunityVerdict(OpportunityDecision.Refuse, "died", 0f, 0f, 0f);
        episode.Died = true;
        EndEpisode();
        _released = null; // a dead bot has no target to take back
    }

    private OpportunityStep Think(Player player, uint elapsedMs, bool interrupting)
    {
        PlayerbotOpportunityOptions options = _options.Opportunities;
        if (!options.Enabled || elapsedMs == 0 || !player.IsInWorld || !player.IsAlive || player.Map is not { } map)
        {
            if (_episode is not null)
            {
                // A death ends the episode like any failure (the backoff); a disabled goal or a map change just ends it.
                if (options.Enabled && player.IsInWorld && !player.IsAlive) BotDied();
                else EndEpisode();
            }

            return OpportunityStep.None;
        }

        uint now = Now;
        if (_episode is null)
        {
            if (_scanned && unchecked((int)(now - _nextScanMs)) < 0) return OpportunityStep.None;
            _scanned = true;
            _nextScanMs = unchecked(now + ScanEveryMs);
            if (_session.ManagedBudget is not { Remaining: > 0 } || PlayerbotTownGoals.FreeBagSlots(player) <= 0) return OpportunityStep.None;
            Begin(player, map, now, interrupting);
            if (_episode is null) return OpportunityStep.None;
        }

        return Step(player, map, elapsedMs, now);
    }

    // --- scanning ---------------------------------------------------------------------------------------------------

    private readonly record struct Find(OpportunityKind Kind, ulong Key, ObjectGuid Guid, uint Entry, Vector3 At, float Distance,
        Creature? Rare, SpellInfo? PickLock, uint LootId, uint Gold, uint RespawnMs);

    private void Begin(Player player, Map map, uint now, bool interrupting)
    {
        PlayerbotOpportunityOptions options = _options.Opportunities;
        var finds = new List<Find>(MaxCandidates);
        CollectFinds(player, map, now, options, finds);
        if (finds.Count == 0) return;
        List<PlayerbotThreat> threats = PlayerbotRecovery.Threats(player);
        Find? best = null;
        OpportunityVerdict bestVerdict = default;
        int bestGuards = 0;
        foreach (Find find in finds)
        {
            OpportunityVerdict verdict = Evaluate(player, map, find, interrupting, committed: false, now, threats, out List<Creature> pack);
            LastVerdict = verdict;
            int guards = GuardsOf(find, pack);
            // A refused find is not weighed again at the next scan: its numbers move slowly. A wait (hurt, recovering) is looked at again at once.
            if (verdict.Decision == OpportunityDecision.Refuse)
            {
                _ledger.SetAside(find.Key, now, RefusedRecheckMs);
                ReportDecision(player, find, verdict, guards, engaged: false, now);
            }

            if (verdict.Decision != OpportunityDecision.Engage) continue;
            // The first guard to pull is one the brain would never take as a target (unreachable, someone else's, outside the home): leave the find.
            if (pack.Count > 0 && (Untakeable?.Invoke(player, pack[0]) == true
                || (map.Collision.Pathfinder is NavMeshPathfinder && pack.Any(g => Untakeable?.Invoke(player, g) == true))))
            {
                _ledger.SetAside(find.Key, now, RefusedRecheckMs);
                ReportDecision(player, find, verdict with { Decision = OpportunityDecision.Refuse, Reason = "untakeable" }, guards, engaged: false, now);
                continue;
            }

            if (find.Kind != OpportunityKind.Rare && find.Distance > InteractYards && map.Collision.Pathfinder is NavMeshPathfinder)
            {
                PlanOutcome onMesh = ChestNavMeshVerdict(player, find.At, now);
                if (onMesh == PlanOutcome.Pending) continue; // a trial search is under way: no verdict, not set aside, asked again at the next scan
                if (onMesh == PlanOutcome.Refused)
                {
                    _ledger.SetAside(find.Key, now, RefusedRecheckMs);
                    ReportDecision(player, find, verdict with { Decision = OpportunityDecision.Refuse, Reason = "untakeable" }, guards, engaged: false, now);
                    continue;
                }
            }

            if (_claims.OwnerOf(find.Key, now) is var owner and not 0 && owner != player.Guid.Value) continue; // another bot is on it
            if (best is null || verdict.Net > bestVerdict.Net)
            {
                // The find this one displaces was worth going for but not the best: a decision not to engage.
                if (best is { } displaced) ReportDecision(player, displaced, bestVerdict with { Reason = "passed-over" }, bestGuards, engaged: false, now);
                (best, bestVerdict, bestGuards) = (find, verdict, guards);
            }
            else ReportDecision(player, find, verdict with { Reason = "passed-over" }, guards, engaged: false, now);
        }

        if (best is not { } chosen) return;
        uint epoch = _claims.Join(chosen.Key, player.Guid.Value, now);
        if (_claims.OwnerOf(chosen.Key, now) is var winner and not 0 && winner != player.Guid.Value)
        {
            // Two bots chose it in one tick: the first claimant keeps it.
            _claims.Leave(chosen.Key, epoch);
            return;
        }

        LastVerdict = bestVerdict;
        _episode = new Episode { Origin = chosen, StartedMs = now, MemoryMark = _risk().Memory.Count, ClaimEpoch = epoch, LastStepMs = now, Bot = player.Guid.Value };
        ReportDecision(player, chosen, bestVerdict, bestGuards, engaged: true, now);
    }

    /// <summary>The guards at a find: the pack less the rare itself (it is the prize, not a guard).</summary>
    private static int GuardsOf(Find find, List<Creature> pack) => pack.Count - (find.Kind == OpportunityKind.Rare && pack.Count > 0 ? 1 : 0);

    /// <summary>Tell the sink about one decision (a verdict of engage or refuse; a wait is no decision). A sink fault never changes it.</summary>
    private void ReportDecision(Player player, in Find find, in OpportunityVerdict verdict, int guards, bool engaged, uint now)
    {
        if (Sink is not { } sink) return;
        try
        {
            sink.Decided(player, new Policy.OpportunityDecisionInfo(find.Key, find.Kind, find.Entry, find.Distance, guards, verdict.Net, verdict.Risk, verdict.Reason),
                engaged, now);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { FaultLedger.Shared.Record("playerbot.policy-telemetry", error); }
    }

    private void CollectFinds(Player player, Map map, uint now, PlayerbotOpportunityOptions options, List<Find> finds)
    {
        float radiusSq = options.ScanRadiusYards * options.ScanRadiusYards;
        GameObjectLootFeature? objects = _session.Services.GetService<GameObjectLootFeature>();
        GroupManager? groups = GroupsOf(player); // one lookup per scan, not per object
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (_ledger.IsDeferred(guid.Value, now)) continue;
            WorldObject? obj = map.FindObject(guid);
            if (obj is null) continue;
            float dx = obj.X - player.X, dy = obj.Y - player.Y, dz = obj.Z - player.Z;
            float distanceSq = (dx * dx) + (dy * dy) + (dz * dz);
            if (distanceSq > radiusSq) continue;
            Find? found = null;
            if (obj is GameObject { Type: GameObjectType.Chest } chest)
            {
                if (objects is not null && ClassifyChest(player, chest, objects, options, out OpportunityKind kind, out SpellInfo? pick))
                    found = new Find(kind, guid.Value, guid, chest.Entry, new Vector3(chest.X, chest.Y, chest.Z), MathF.Sqrt(distanceSq), null, pick,
                        chest.Template.GetData(1), (chest.Template.MinGold + chest.Template.MaxGold) / 2, RespawnMsOf(chest));
            }
            else if (obj is Creature { IsAlive: true, IsInWorld: true } rare && options.Rares && rare.Template.Rank is 2 or 4
                && map.Combat.Hooks.CanAttack(player, rare) && !PlayerbotBrain.IsSomeoneElses(player, rare, groups))
            {
                CreatureLootInfo? info = objects?.LootContent.FindCreature(rare.Entry);
                found = new Find(OpportunityKind.Rare, guid.Value, guid, rare.Entry, new Vector3(rare.X, rare.Y, rare.Z), MathF.Sqrt(distanceSq), rare, null,
                    info?.LootId ?? 0u, info is null ? 0u : (info.MinGold + info.MaxGold) / 2, 0u);
            }

            if (found is not { } find) continue;
            if (Home is { } home && obj.MapId == home.MapId && !home.Contains(obj.MapId, obj.X, obj.Y, obj.Z)) continue;
            Insert(finds, find);
        }
    }

    private static void Insert(List<Find> finds, Find find)
    {
        int at = finds.Count;
        while (at > 0 && finds[at - 1].Distance > find.Distance) at--;
        if (at >= MaxCandidates) return;
        finds.Insert(at, find);
        if (finds.Count > MaxCandidates) finds.RemoveAt(finds.Count - 1);
    }

    /// <summary>
    /// Whether <paramref name="chest"/> is a find for the bot: a chest nobody has opened, with no quest, no event and no trap, that the
    /// bot can open without a profession spell (no lock, a click-open lock, a key in the bags) or with Pick Lock at the lock's skill.
    /// A herb or mining node is not (<see cref="PlayerbotCrafting"/> gathers those).
    /// </summary>
    private bool ClassifyChest(Player player, GameObject chest, GameObjectLootFeature objects, PlayerbotOpportunityOptions options,
        out OpportunityKind kind, out SpellInfo? pickLock)
    {
        kind = OpportunityKind.Chest;
        pickLock = null;
        GameObjectTemplate template = chest.Template;
        if (!chest.IsSpawned || chest.LootState != GameObjectLootState.Ready || chest.Loot is not null || !chest.User.IsEmpty
            || (chest.Flags & (GameObjectFlags.InUse | GameObjectFlags.NoInteract)) != 0
            || template.GetData(GameObjectMapSystem.ChestMinOpensData) != 0 && template.GetData(GameObjectMapSystem.ChestMaxOpensData) > template.GetData(GameObjectMapSystem.ChestMinOpensData)
            || template.GetData(8) != 0 || template.GetData(6) != 0 || template.LinkedTrapEntry() != 0
            || template.GetData(GameObjectMapSystem.ChestLevelData) > player.Level + GameObjectMapSystem.ChestLevelLeeway
            || template.GetData(1) == 0)
            return false;

        uint lockId = GameObjectLocks.LockIdOf(template);
        if (lockId == 0) return options.Chests;
        LockEntry? lockEntry = objects.Content.FindLock(lockId);
        if (lockEntry is null) return false;
        if (HasSkillCase(lockEntry, LockType.Herbalism) || HasSkillCase(lockEntry, LockType.Mining)) return false;
        GameObjectUseResult direct = GameObjectLocks.CheckDirectUse(lockEntry, player);
        if (direct == GameObjectUseResult.Ok)
        {
            bool key = Enumerable.Range(0, Math.Min(LockEntry.Cases, lockEntry.Types.Count)).Any(i => (LockKeyType)lockEntry.Types[i] == LockKeyType.Item && lockEntry.Indexes[i] != 0);
            kind = key ? OpportunityKind.LockedChest : OpportunityKind.Chest;
            return key ? options.LockedChests : options.Chests;
        }

        if (direct != GameObjectUseResult.Locked || !options.LockedChests || !HasSkillCase(lockEntry, LockType.PickLock)) return false;
        pickLock = FindPickLock(player, lockId, lockEntry);
        kind = OpportunityKind.LockedChest;
        return pickLock is not null;
    }

    private static bool HasSkillCase(LockEntry entry, LockType type)
    {
        for (int i = 0; i < LockEntry.Cases && i < entry.Types.Count; i++)
            if ((LockKeyType)entry.Types[i] == LockKeyType.Skill && entry.Indexes[i] == (uint)type) return true;
        return false;
    }

    /// <summary>The bot's Pick Lock spell when its skill reaches the lock's requirement (the check <see cref="PlayerbotCrafting"/> makes for a locked item).</summary>
    private SpellInfo? FindPickLock(Player player, uint lockId, LockEntry lockEntry)
    {
        if (_session.Services.GetService<SpellFeature>() is not { } spells) return null;
        foreach (uint spellId in spells.Spellbook.GetSpells(player))
        {
            if (spells.System.Store.Get(spellId) is not { } spell) continue;
            if (spell.Id != PickLockSpellId && !spell.Effects.Any(e => e.Effect == SpellEffectName.OpenLock && e.MiscValue == (int)LockType.PickLock)) continue;
            SpellEffectInfo effect = spell.Effects[0];
            OpenLockCheck check = GatheringRules.CanOpenLock(lockId, lockEntry, (uint)LockType.PickLock, 0, false, effect.BasePoints + effect.BaseDice, true,
                id => player.Skills?.GetValue(id) ?? 0);
            if (check.Result == SpellCastResult.CastOk) return spell;
        }

        return null;
    }

    // --- the arithmetic's inputs -----------------------------------------------------------------------------------

    private OpportunityVerdict Evaluate(Player player, Map map, Find find, bool interrupting, bool committed, uint now, List<PlayerbotThreat> threats, out List<Creature> pack)
    {
        pack = Pack(player, map, find, threats);
        return Decide(player, find, pack, interrupting, committed, now);
    }

    private OpportunityVerdict Decide(Player player, Find find, List<Creature> pack, bool interrupting, bool committed, uint now, bool riskOnly = false)
    {
        PlayerbotRisk risk = _risk();
        var enemies = new List<RiskEnemy>(pack.Count);
        for (int i = 0; i < pack.Count; i++)
            enemies.Add(risk.Enemy(pack[i], i == 0 ? RiskJoin.Target : RiskJoin.Assist, player));
        PlayerbotEngagementFacts facts = risk.BotFacts(player, enemies) with
        {
            GroupSize = GroupSizeOf(player),
            DangerHits = _options.Risk.Enabled ? risk.Memory.Hits(player.MapId, find.At, find.Kind == OpportunityKind.Rare ? find.Entry : 0u, now) : 0,
            Remembered = _options.Risk.Enabled && pack.Any(risk.IsRemembered),
        };
        float units = LootUnitsOf(player, find.Kind == OpportunityKind.Rare ? LootTableKind.Creature : LootTableKind.GameObject, find.LootId, find.Gold, now);
        float speed = Math.Max(1f, _options.MoveSpeed);
        var opportunity = new OpportunityFacts
        {
            Engagement = facts,
            LootUnits = units,
            TravelSeconds = Math.Max(0f, find.Distance - InteractYards) / speed,
            Recovering = !risk.Recovered(player) || risk.Retreat.Active,
            Interrupting = interrupting,
            Committed = committed,
            RiskOnly = riskOnly,
            Personality = PlayerbotOpportunityModel.Personality(player.Guid.Value, _options.Opportunities.PersonalityVariance),
            FindIsEnemy = find.Kind == OpportunityKind.Rare,
        };
        return PlayerbotOpportunityModel.Decide(opportunity, _options.Opportunities, _options.Risk);
    }

    /// <summary>
    /// The creatures that would fight the bot at the find, nearest first (a rare last): those whose aggro reach covers the chest and the
    /// creatures that would answer their call for assistance (vmangos Creature::CallAssistance, as <see cref="PlayerbotRisk"/> reads it).
    /// For a rare, the rare itself plus those two sets round it.
    /// </summary>
    private List<Creature> Pack(Player player, Map map, Find find, List<PlayerbotThreat> threats)
    {
        var members = new List<Creature>();
        var counted = new HashSet<ObjectGuid>();
        if (find.Rare is { } rare && rare.IsAlive)
        {
            members.Add(rare);
            counted.Add(rare.Guid);
        }

        foreach (PlayerbotThreat threat in threats)
            if (threat.Source is { IsAlive: true } creature && !counted.Contains(creature.Guid) && threat.Reaches(find.At, 0f))
            {
                counted.Add(creature.Guid);
                members.Add(creature);
            }

        CreatureMapSystem? system = map.FindUpdater<CreatureMapSystem>();
        if (system is not null)
        {
            for (int index = 0; index < members.Count; index++)
            {
                Creature caller = members[index];
                float assist = system.AssistanceRadiusOf(caller.Template);
                if (assist <= 0f) continue;
                foreach (ObjectGuid guid in player.VisibleObjects)
                    if (map.FindObject(guid) is Creature helper && helper.IsAlive && !counted.Contains(helper.Guid) && system.CanAssist(helper, caller, player, assist))
                    {
                        counted.Add(helper.Guid);
                        members.Add(helper);
                    }
            }
        }

        // Nearest to the bot first (the first pull); the rare, the prize, last.
        members.Sort((a, b) =>
        {
            int rank = (ReferenceEquals(a, find.Rare) ? 1 : 0) - (ReferenceEquals(b, find.Rare) ? 1 : 0);
            if (rank != 0) return rank;
            return DistanceSq(player, a).CompareTo(DistanceSq(player, b));
        });
        return members;
    }

    private static float DistanceSq(WorldObject from, WorldObject to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private int GroupSizeOf(Player player)
    {
        if (GroupsOf(player)?.GetGroup(player.Guid) is not { } group || player.Map is not { } map) return 1;
        int near = 0;
        foreach (GroupMemberSlot member in group.Members)
            if (map.FindPlayer(member.Guid) is { IsAlive: true } other && (ReferenceEquals(other, player) || DistanceSq(player, other) <= 40f * 40f)) near++;
        return Math.Clamp(near, 1, PlayerbotRisk.MaxSignalGroupSize);
    }

    private static GroupManager? GroupsOf(Player player)
        => (player.Session as WorldSession)?.Services.GetService<ArcaneCore.World.Social.SocialFeature>()?.Context.Groups;

    /// <summary>The loot table's worth to this bot in same-level kills (<see cref="PlayerbotOpportunityModel.LootUnits"/>), kept a minute.</summary>
    private float LootUnitsOf(Player player, LootTableKind kind, uint lootId, uint gold, uint now)
    {
        if (lootId != 0 && _lootCache.TryGetValue((kind, lootId), out (float Units, uint UntilMs) cached) && unchecked((int)(cached.UntilMs - now)) > 0) return cached.Units;
        if (_lootCache.Count >= 64) _lootCache.Clear();
        LootContent? content = _session.Services.GetService<GameObjectLootFeature>()?.LootContent;
        if (content is null) return 0f;
        Dictionary<uint, float> items = PlayerbotOpportunityModel.ExpectedItems(content, kind, lootId);
        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        SpellStore? spells = _session.Services.GetService<SpellFeature>()?.System.Store;
        float units = PlayerbotOpportunityModel.LootUnits(items, gold, player.Level, id => player.Inventory.Templates.Find(id), template =>
        {
            if (template.Quality < 2) return null;
            float score = PlayerbotItemScore.Score(template, weights, spells);
            if (PlayerbotItemScore.UpgradeGain(player, template, score, weights, null, spells, out _) is not { } gain || gain <= PlayerbotItemScore.MinimumGain) return null;
            return gain / Math.Max(0.01f, score);
        });
        if (lootId != 0) _lootCache[(kind, lootId)] = (units, unchecked(now + LootCacheMs));
        return units;
    }

    private static uint RespawnMsOf(GameObject chest)
        => (uint)Math.Min(3_600_000UL, (ulong)Math.Max(chest.RespawnDelaySeconds, chest.Template.GetData(GameObjectMapSystem.ChestRestockData)) * 1000UL);

    // --- gathering nodes --------------------------------------------------------------------------------------------

    private bool VetoNode(GameObject node)
    {
        PlayerbotOpportunityOptions options = _options.Opportunities;
        if (!options.Enabled || !options.Nodes || _session.Player is not { IsAlive: true } player || player.Map is not { } map) return false;
        uint now = Now;
        if (_vetoes.TryGetValue(node.Guid.Value, out (bool Veto, uint UntilMs) cached) && unchecked((int)(cached.UntilMs - now)) > 0) return cached.Veto;
        if (_vetoes.Count >= 64) _vetoes.Clear();
        var find = new Find(OpportunityKind.Node, node.Guid.Value, node.Guid, node.Entry, new Vector3(node.X, node.Y, node.Z), MathF.Sqrt(DistanceSq(player, node)),
            null, null, node.Template.GetData(1), (node.Template.MinGold + node.Template.MaxGold) / 2, 0u);
        List<Creature> pack = Pack(player, map, find, PlayerbotRecovery.Threats(player));
        // An unguarded node is never vetoed. A guarded one is judged on the guards alone (the bot gathers it whatever it is worth, so a
        // net value never vetoes it): it is left when the guards are a hard refusal or too dangerous (or the bot too hurt) as it is now.
        bool veto = pack.Count > 0 && Decide(player, find, pack, interrupting: false, committed: false, now, riskOnly: true).Decision != OpportunityDecision.Engage;
        _vetoes[node.Guid.Value] = (veto, unchecked(now + VetoCacheMs));
        return veto;
    }

    // --- the episode ------------------------------------------------------------------------------------------------

    private enum EpisodePhase : byte
    {
        Clear,
        Open,
        Await,
    }

    private sealed class Episode
    {
        public required Find Origin;
        public ulong Key => Origin.Key;
        public OpportunityKind Kind => Origin.Kind;
        public ObjectGuid Guid => Origin.Guid;
        public uint Entry => Origin.Entry;
        public Vector3 At => Origin.At;
        public SpellInfo? PickLock => Origin.PickLock;
        public uint RespawnMs => Origin.RespawnMs;
        public uint StartedMs;
        public uint LastStepMs;
        public int MemoryMark;
        public uint ClaimEpoch;
        public EpisodePhase Phase;
        public uint AwaitUntilMs;
        public bool Reported;
        public int PickCasts;
        public Creature? Pull;

        /// <summary>The bot's guid (the telemetry's outcome is filed under it when the episode ends).</summary>
        public ulong Bot;

        /// <summary>The bot died during the episode.</summary>
        public bool Died;

        /// <summary>Copper looted from the chest (coins plus the vendor price of the items stored); meaningful when <see cref="LootMeasured"/>.</summary>
        public ulong LootCopper;

        /// <summary>The episode ended in success and the loot is known (a chest's window was read). A success whose loot is not seen
        /// (a rare's corpse is the brain's to loot, a chest taken before its window was read) leaves it false.</summary>
        public bool LootMeasured;

        /// <summary>The episode ended in success (the loot was taken, or the rare was killed).</summary>
        public bool Succeeded;

        /// <summary>A rare the bot went for was killed by another player (or group): not a success.</summary>
        public bool Lost;
    }

    private OpportunityStep Busy(Episode episode, PlayerbotGoalKind goal) => new(true, null, goal, episode.Entry);

    private OpportunityStep Step(Player player, Map map, uint elapsedMs, uint now)
    {
        Episode episode = _episode!;
        episode.LastStepMs = now;
        ulong self = player.Guid.Value;
        PlayerbotRisk risk = _risk();

        // Another bot on the same find: the first claimant keeps it.
        if (!_claims.Refresh(episode.Key, episode.ClaimEpoch, now)) episode.ClaimEpoch = _claims.Join(episode.Key, self, now);
        if (_claims.OwnerOf(episode.Key, now) is var owner and not 0 && owner != self) return Skip(episode, now);

        if (episode.Phase != EpisodePhase.Await)
        {
            if (risk.Memory.Count > episode.MemoryMark) return Fail(episode, now, "fight-went-badly");
            if (unchecked(now - episode.StartedMs) > EpisodeMaxMs) return Fail(episode, now, "timeout");
        }

        WorldObject? target = map.FindObject(episode.Guid);
        bool visible = player.VisibleObjects.Contains(episode.Guid);
        if (episode.Kind == OpportunityKind.Rare) return StepRare(player, map, episode, target as Creature, visible, now);
        if (target is not GameObject chest || !chest.IsSpawned || !visible)
        {
            // Gone: taken (by us, once the window opened, or by someone else before). Someone else's is dropped at once, no backoff.
            if (episode.Phase == EpisodePhase.Await) return Complete(episode, now);
            _ledger.Forget(episode.Key);
            EndEpisode();
            return OpportunityStep.None;
        }

        if (episode.Phase == EpisodePhase.Await) return AwaitLoot(player, episode, now);

        if ((!chest.User.IsEmpty && chest.User != player.Guid) || chest.LootState != GameObjectLootState.Ready
            || (chest.Flags & GameObjectFlags.InUse) != 0 || chest.Loot is not null)
        {
            // Someone else opened it (or is opening it): drop it. If it is back after its respawn the scan finds it again.
            _ledger.Forget(episode.Key);
            EndEpisode();
            return OpportunityStep.None;
        }

        if (PlayerNear(player, map, chest)) return Skip(episode, now);

        // Guards first, one pull at a time, weighed afresh against the bot as it is now.
        Find find = episode.Origin with { Distance = MathF.Sqrt(DistanceSq(player, chest)) };
        OpportunityVerdict verdict = Evaluate(player, map, find, interrupting: false, committed: true, now, PlayerbotRecovery.Threats(player), out List<Creature> pack);
        LastVerdict = verdict;
        if (verdict.Decision == OpportunityDecision.Refuse) return Fail(episode, now, verdict.Reason);
        if (verdict.Decision == OpportunityDecision.Wait)
        {
            risk.BeginWait(player);
            return Busy(episode, PlayerbotGoalKind.Rest);
        }

        if (pack.Count > 0) return Pull(player, episode, pack, now);
        episode.Pull = null;
        episode.Phase = EpisodePhase.Open;
        return Open(player, episode, chest, elapsedMs, now);
    }

    /// <summary>The next guard, nearest first (the one being walked to stays the pull until it dies or the pack changes).</summary>
    private OpportunityStep Pull(Player player, Episode episode, List<Creature> pack, uint now)
    {
        episode.Phase = EpisodePhase.Clear;
        Creature pull = episode.Pull is { IsAlive: true } held && pack.Contains(held) ? held : pack[0];
        // A guard the brain would not take as a target (unreachable, set aside, someone else's, outside the home) cannot be cleared: the
        // find is given up now, with a backoff, instead of the same pull being issued at every think until the timeout.
        if (Untakeable?.Invoke(player, pull) == true)
        {
            if (PlayerbotBrain.IsSomeoneElses(player, pull, GroupsOf(player))) return Skip(episode, now);
            if ((Home is { } home && pull.MapId == home.MapId && !home.Contains(pull)) || _risk().IsRemembered(pull)
                || pull.IsInEvadeMode || _risk().CoversPullHazard(player, pull) || player.Map?.Collision.Pathfinder is NavMeshPathfinder)
                return Fail(episode, now, "untakeable");
            return Unreachable(player, episode, now, $"the guard {pull.Entry} ({pull.Guid.Value:X}) cannot be taken as a target");
        }
        if (!ReferenceEquals(pull, episode.Pull))
        {
            PlayerbotRisk risk = _risk();
            float dpsIn = 0f;
            foreach (Creature creature in pack) dpsIn += risk.Enemy(creature, RiskJoin.Assist, player).Dps;
            risk.Tracker.Prime(dpsIn, risk.Tracker.ObservedDps ?? PlayerbotRiskModel.PriorBotDps(player.Level));
        }

        episode.Pull = pull;
        return new OpportunityStep(true, pull, PlayerbotGoalKind.Combat, pull.Entry);
    }

    private OpportunityStep StepRare(Player player, Map map, Episode episode, Creature? rare, bool visible, uint now)
    {
        PlayerbotRisk risk = _risk();
        if (rare is null || !rare.IsInWorld || !visible || !rare.IsAlive)
        {
            // Dead (the brain loots the corpse) or gone: the find is over. A rare that vanished unseen is dropped at once.
            if (rare is { IsAlive: false })
            {
                _ledger.Complete(episode.Key, now, RareRecheckMs);
                // Killed by someone else (the corpse is tapped by another): the bot lost it, whatever it was worth.
                // The server's own tap rule (the viewer's tapped flags: LootService.Filter, which knows groups and pets): a kill that earned the
                // bot or its group the corpse is a success; one another player tapped is lost; one that earned nobody credit (SettleKillCredit,
                // an NPC did most of the damage) is neither.
                if (rare.KillCreditAllowed)
                {
                    if (PlayerbotBrain.HasLootRights(player, rare)) episode.Succeeded = true;
                    else episode.Lost = true;
                }
            }
            else _ledger.Forget(episode.Key);
            EndEpisode();
            return OpportunityStep.None;
        }

        if (PlayerbotBrain.IsSomeoneElses(player, rare, GroupsOf(player))) return Skip(episode, now);
        Find find = episode.Origin with { At = new Vector3(rare.X, rare.Y, rare.Z), Distance = MathF.Sqrt(DistanceSq(player, rare)), Rare = rare };
        OpportunityVerdict verdict = Evaluate(player, map, find, interrupting: false, committed: true, now, PlayerbotRecovery.Threats(player), out List<Creature> pack);
        LastVerdict = verdict;
        if (verdict.Decision == OpportunityDecision.Refuse) return Fail(episode, now, verdict.Reason);
        if (verdict.Decision == OpportunityDecision.Wait)
        {
            risk.BeginWait(player);
            return Busy(episode, PlayerbotGoalKind.Rest);
        }

        return Pull(player, episode, pack, now);
    }

    /// <summary>A rare killed is looked at again after this (its respawn is long; the brain loots the corpse).</summary>
    internal const uint RareRecheckMs = 300_000;

    private OpportunityStep Open(Player player, Episode episode, GameObject chest, uint elapsedMs, uint now)
    {
        if (_session.ManagedBudget is not { Remaining: > 0 }) return Busy(episode, PlayerbotGoalKind.Loot);
        float distance = MathF.Sqrt(DistanceSq(player, chest));
        if (distance > InteractYards)
        {
            if (!(PlayerbotThinkProbe.Active?.HasOptionalPlanBudget ?? true)) return Busy(episode, PlayerbotGoalKind.Loot);
            if (_route is { Complete: true }) _route = null;
            if (_route is null || _routeTarget != chest.Guid)
            {
                PlanOutcome planned = PlayerbotNavigation.PlanExact(player, episode.At, _options, ref _tileWait, now, out _route);
                if (planned == PlanOutcome.Pending) return Busy(episode, PlayerbotGoalKind.Loot);
                // An exact search never asks the region graph, and within 100 yards it keeps the 2048-node budget however long the walk
                // is. A chest the graph joins (the way round a cliff, not the straight drop) is then dropped as "no route".
                if (planned == PlanOutcome.Refused || _route is null)
                {
                    planned = PlanChestTheGraphReaches(player, episode.At, now);
                }
                if ((planned == PlanOutcome.Refused || _route is null)
                    && PlayerbotNavigation.PlanExact(player, episode.At, _options, ref _tileWait, now, out PlayerbotRoute? nearRoute, nearestEnd: true) == PlanOutcome.Route
                    && nearRoute is { Points.Count: > 0 } && Vector3.Distance(nearRoute.Points[^1], episode.At) <= InteractYards)
                {
                    _route = nearRoute;
                    planned = PlanOutcome.Route;
                }
                if (planned == PlanOutcome.Pending) return Busy(episode, PlayerbotGoalKind.Loot);
                if (planned == PlanOutcome.Refused || _route is null)
                {
                    _route = null;
                    if (player.Map?.Collision.Pathfinder is NavMeshPathfinder) return Fail(episode, now, "unreachable");
                    return Unreachable(player, episode, now, "no route to the chest");
                }

                _routeTarget = chest.Guid;
            }

            if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, now))
            {
                bool finishedLeg = _route.Complete;
                _route = null;
                if (finishedLeg) return Busy(episode, PlayerbotGoalKind.Loot);
                if (player.Map?.Collision.Pathfinder is NavMeshPathfinder) return Fail(episode, now, "unreachable");
                return Unreachable(player, episode, now, "the walk to the chest stopped");
            }

            return Busy(episode, PlayerbotGoalKind.Loot);
        }

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(_session, player)) return Busy(episode, PlayerbotGoalKind.Loot);
        if (_session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
            { State: SpellCastState.Preparing or SpellCastState.Casting })
            return Busy(episode, PlayerbotGoalKind.Loot);

        bool sent;
        if (episode.PickLock is { } pick)
        {
            var writer = new PacketWriter(15);
            writer.WriteUInt32(pick.Id);
            writer.WriteUInt16((ushort)SpellCastTargetFlags.GameObject);
            writer.WritePackedGuid(chest.Guid.Value);
            sent = _session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray());
        }
        else
        {
            sent = _session.TryManagedAction(WorldOpcode.CmsgGameobjUse, PlayerbotNavigation.GuidPayload(chest.Guid.Value));
        }

        if (!sent) return Busy(episode, PlayerbotGoalKind.Loot);
        if (episode.PickLock is not null) episode.PickCasts++;
        episode.Phase = EpisodePhase.Await;
        episode.AwaitUntilMs = unchecked(now + AwaitLootMs);
        return Busy(episode, PlayerbotGoalKind.Loot);
    }

    /// <summary>
    /// The exact search refused the chest. When the region graph joins the bot to it, that refusal is the short budget or nodes spent
    /// toward the chest across a cliff, not a missing walk: one wide search inside the same region, otherwise a leg to a portal of the
    /// graph's way. Pending while the graph is still being built. Refused when the graph does not join them, so the caller reports it.
    /// </summary>
    private PlanOutcome ChestNavMeshVerdict(Player player, Vector3 goal, uint now)
    {
        if (!(PlayerbotThinkProbe.Active?.HasOptionalPlanBudget ?? true)) return PlanOutcome.Route;
        TileWait wait = _tileWait;
        bool pending = false;
        if (ChestFound(ChestExactVerdict(player, goal, now, ref wait), ref pending)
            || ChestFound(ChestGraphVerdict(player, goal, now), ref pending)
            || ChestFound(ChestNearestVerdict(player, goal, now, ref wait), ref pending))
            return PlanOutcome.Route;
        return pending ? PlanOutcome.Pending : PlanOutcome.Refused;
    }

    private PlanOutcome ChestExactVerdict(Player player, Vector3 goal, uint now, ref TileWait wait)
    {
        PlanOutcome exact = PlayerbotNavigation.PlanExact(player, goal, _options, ref wait, now, out PlayerbotRoute? route, trial: true);
        return exact == PlanOutcome.Route && route is null ? PlanOutcome.Refused : exact;
    }

    private PlanOutcome ChestGraphVerdict(Player player, Vector3 goal, uint now)
    {
        PlayerbotRoute? saved = _route;
        PlanOutcome graph = PlanChestTheGraphReaches(player, goal, now);
        PlayerbotRoute? found = _route;
        _route = saved;
        return graph == PlanOutcome.Route && found is null ? PlanOutcome.Refused : graph;
    }

    private PlanOutcome ChestNearestVerdict(Player player, Vector3 goal, uint now, ref TileWait wait)
    {
        PlanOutcome nearest = PlayerbotNavigation.PlanExact(player, goal, _options, ref wait, now, out PlayerbotRoute? end, trial: true, nearestEnd: true);
        return nearest == PlanOutcome.Route && end is { Points.Count: > 0 } && Vector3.Distance(end.Points[^1], goal) <= InteractYards
            ? PlanOutcome.Route : nearest == PlanOutcome.Pending ? PlanOutcome.Pending : PlanOutcome.Refused;
    }

    /// <summary>
    /// One trial plan of the verdict on a chest (<see cref="ChestNavMeshVerdict"/>): true when it found a route; notes in
    /// <paramref name="pending"/> that it answered pending (a deferred trial search or a tile still being read: no verdict yet, the caller asks
    /// again on a later scan and does not set the find aside). The trials are the exact search, the region graph's way and the nearest end
    /// of the mesh within reach, each asked only when the ones before did not find a route; the chest is Refused only when every one refused.
    /// </summary>
    private static bool ChestFound(PlanOutcome planned, ref bool pending)
    {
        pending |= planned == PlanOutcome.Pending;
        return planned == PlanOutcome.Route;
    }

    /// <summary>The verdict of <see cref="ChestNavMeshVerdict"/> from the three outcomes (all given; the real chain asks them lazily).</summary>
    internal static PlanOutcome ChestVerdict(PlanOutcome exact, PlanOutcome graph, PlanOutcome nearest)
    {
        bool pending = false;
        if (ChestFound(exact, ref pending) || ChestFound(graph, ref pending) || ChestFound(nearest, ref pending)) return PlanOutcome.Route;
        return pending ? PlanOutcome.Pending : PlanOutcome.Refused;
    }

    private PlanOutcome PlanChestTheGraphReaches(Player player, Vector3 goal, uint now)
    {
        _route = null;
        if (player.Map is not { } map || map.Collision.Pathfinder is not NavMeshPathfinder nav)
            return PlanOutcome.Refused;
        NavTerrain include = PathMover.Player.IncludeFlags;
        if (nav.RegionGraph(map.MapId, include, NavTerrain.SteepSlopes) is not { } graph)
            return nav.RegionGraphTask(map.MapId, include, NavTerrain.SteepSlopes) is { IsCompleted: false }
                ? PlanOutcome.Pending : PlanOutcome.Refused;

        Vector3 start = PlayerbotMotion.CurrentPosition(player);
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
        int from = nav.RegionAt(graph, start, locate), to = nav.RegionAt(graph, goal, locate);
        if (from < 0 || to < 0) return PlanOutcome.Refused;
        // Cached per region pair and, under a planning scope, searched off the world thread: Pending until the pair is answered.
        using PlayerbotPathJobs.DeferrableScope deferrable = PlayerbotPathJobs.Deferrable();
        bool mayDefer = PlayerbotPathJobs.InDeferrableScope && PlayerbotAsyncNavigationPlan.Current is null;
        PlayerbotPathJobs? jobs = PlayerbotPathJobs.Bound;
        long wallMs = jobs?.WallMs() ?? System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
        if (!PlayerbotRegionRoutes.TryRoute(graph, from, start, to, goal, mayDefer, out NavRegionRouteStatus routeStatus, out List<Vector3> portals,
            jobs?.Logger, wallMs, _options.PathJobWaitMaxMs, jobs?.RegionSyncBudget))
        {
            PlayerbotPathJobs.NoteExternalPending();
            return PlanOutcome.Pending;
        }

        if (routeStatus != NavRegionRouteStatus.Found) return PlanOutcome.Refused;
        // One region: the polygons already join, and the exact 2048-node search was too small for the long way round.
        if (portals.Count == 0)
            return PlayerbotNavigation.PlanFar(player, goal, _options, PlayerbotNavigation.MaxSearchNodes, ref _tileWait, now, out _route);

        // The way leaves this region through a portal. Walk to that, not straight at the chest (the near side of the cliff).
        portals.Add(goal);
        int pick = 0;
        float leg = MathF.Min(PlayerbotNavigation.RegionLegYards, _options.MaxRouteYards * 0.5f), along = Vector3.Distance(start, portals[0]);
        for (int i = 1; i < portals.Count; i++)
        {
            along += Vector3.Distance(portals[i - 1], portals[i]);
            if (along > leg) break;
            pick = i;
        }

        PlanOutcome planned = PlayerbotNavigation.PlanFar(player, portals[pick], _options, PlayerbotNavigation.MaxSearchNodes, ref _tileWait, now, out _route);
        if (planned == PlanOutcome.Route || planned == PlanOutcome.Pending || pick == 0) return planned;
        return PlayerbotNavigation.PlanFar(player, portals[0], _options, PlayerbotNavigation.MaxSearchNodes, ref _tileWait, now, out _route);
    }

    private OpportunityStep AwaitLoot(Player player, Episode episode, uint now)
    {
        if (!PlayerbotMovementControl.Stop(_session, player)) return Busy(episode, PlayerbotGoalKind.Loot);
        bool hasSpace = PlayerbotTownGoals.FreeBagSlots(player) > 0;
        if (episode.PickLock is { } pick)
        {
            foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgCastResult))
            {
                if (packet.Payload.Length < 6) continue;
                var reader = new PacketReader(packet.Payload);
                if (reader.ReadUInt32() != pick.Id || reader.ReadByte() != (byte)SpellCastResultStatus.Failure) continue;
                var reason = (SpellCastResult)reader.ReadByte();
                if (reason == SpellCastResult.TryAgain && episode.PickCasts < MaxPickAttempts)
                {
                    // The pick slipped: cast it again, as a player clicks the chest again.
                    episode.Phase = EpisodePhase.Open;
                    return Busy(episode, PlayerbotGoalKind.Loot);
                }

                ReportAnomaly(player, episode, "chest-pick-failed", $"chest {episode.Entry} ({episode.Guid.Value:X}): Pick Lock failed ({reason}) after {episode.PickCasts} cast(s)");
                return Fail(episode, now, "pick-failed");
            }
        }

        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse))
        {
            if (packet.Opcode != WorldOpcode.SmsgLootResponse || !TryParseLoot(packet.Payload, out LootWindow window) || window.Source != episode.Guid.Value) continue;
            if (window.Type == 1)
            {
                if (hasSpace)
                    foreach (byte slot in window.Slots)
                        _lootSink(WorldOpcode.CmsgAutostoreLootItem, [slot]);
                if (window.Coins > 0) _lootSink(WorldOpcode.CmsgLootMoney, []);
            }

            _lootSink(WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(episode.Guid.Value));
            if (Sink is not null && window.Type == 1)
            {
                // The worth of what the window held that the bot can take: its coins and the vendor price of the items it stores.
                ulong copper = window.Coins;
                // Only as many items as the bags have room for (one slot each; a stack that joins another would add a few the count misses).
                int room = PlayerbotTownGoals.FreeBagSlots(player);
                if (hasSpace && window.Items is { } items)
                    foreach ((uint item, uint count) in items)
                        if (player.Inventory.Templates.Find(item) is { } template && room-- > 0) copper += (ulong)template.SellPrice * Math.Max(1u, count);
                episode.LootCopper = copper;
                episode.LootMeasured = true;
            }

            if (window.Slots.Count == 0 && window.Coins == 0) ReportAnomaly(player, episode, "chest-empty", $"chest {episode.Entry} ({episode.Guid.Value:X}) opened and held no loot");
            return Complete(episode, now);
        }

        if (unchecked((int)(episode.AwaitUntilMs - now)) <= 0)
        {
            ReportAnomaly(player, episode, "chest-no-loot", $"chest {episode.Entry} ({episode.Guid.Value:X}) gave no loot window within {AwaitLootMs} ms of the use");
            return Fail(episode, now, "no-loot");
        }

        return Busy(episode, PlayerbotGoalKind.Loot);
    }

    private OpportunityStep Complete(Episode episode, uint now)
    {
        episode.Succeeded = true;
        _ledger.Complete(episode.Key, now, episode.RespawnMs);
        OpportunityStep step = Busy(episode, PlayerbotGoalKind.Loot);
        EndEpisode();
        return step;
    }

    /// <summary>The find cannot go on: set aside with a growing backoff (60 s up to 5 min), and the episode ends. No anomaly: callers that failed for a real fault report it first.</summary>
    private OpportunityStep Fail(Episode episode, uint now, string reason)
    {
        _ledger.Defer(episode.Key, now);
        LastVerdict = new OpportunityVerdict(OpportunityDecision.Refuse, reason, 0f, 0f, 0f);
        EndEpisode();
        return OpportunityStep.None;
    }

    private OpportunityStep Unreachable(Player player, Episode episode, uint now, string detail)
    {
        ReportAnomaly(player, episode, "unreachable", $"{episode.Kind.ToString().ToLowerInvariant()} {episode.Entry} ({episode.Guid.Value:X}): {detail}");
        return Fail(episode, now, "unreachable");
    }

    /// <summary>Another bot or a player is on it: left alone for a while, no strike against the find.</summary>
    private OpportunityStep Skip(Episode episode, uint now)
    {
        _ledger.SetAside(episode.Key, now, ContestedMs);
        EndEpisode();
        return OpportunityStep.None;
    }

    private void ReportAnomaly(Player player, Episode episode, string code, string detail)
    {
        if (episode.Reported || _session.IsInChaosProbe) return;
        episode.Reported = true;
        PlayerbotAnomalies.Report(player, "opportunity", code, detail);
    }

    private void EndEpisode()
    {
        if (_episode is { } episode)
        {
            ReportEnd(episode);
            _claims.Leave(episode.Key, episode.ClaimEpoch);
            // A live guard handed to the brain as its target is let go of with the episode (a dead one is the brain's to loot).
            if (episode.Pull is { IsAlive: true } pulled) _released = pulled;
        }

        _episode = null;
        _route = null;
        _routeTarget = default;
    }

    /// <summary>
    /// Tell the sink how the episode came out: the copper looted (0 for an abandoned find; unknown, null, for a success whose loot the
    /// controller did not see), whether the bot died, and the seconds from the decision to now.
    /// </summary>
    private void ReportEnd(Episode episode)
    {
        if (Sink is not { } sink) return;
        uint now = Now;
        ulong? loot = episode.LootMeasured ? episode.LootCopper : episode.Succeeded ? null : 0UL;
        try { sink.Ended(episode.Bot, episode.Key, new Policy.OpportunityOutcome(loot, episode.Died, unchecked(now - episode.StartedMs), episode.Lost), now); }
        catch (Exception error) when (error is not OutOfMemoryException) { FaultLedger.Shared.Record("playerbot.policy-telemetry", error); }
    }

    /// <summary>A real player within reach of the chest (other bots settle it through the shared claims).</summary>
    private static bool PlayerNear(Player player, Map map, GameObject chest)
    {
        foreach (ObjectGuid guid in player.VisibleObjects)
            if (map.FindObject(guid) is Player { IsAlive: true } other && !ReferenceEquals(other, player) && (other.Session as WorldSession)?.IsPlayerbot != true
                && DistanceSq(chest, other) <= PlayerNearYards * PlayerNearYards
                && GroupsOf(player)?.AreInSameGroup(player.Guid, other.Guid) != true)
                return true;
        return false;
    }

    // --- the loot window --------------------------------------------------------------------------------------------

    /// <summary>One SMSG_LOOT_RESPONSE: the source, the loot type (1 = a corpse or chest), the coins and the slots of the items (slot type 0 only) and the item ids with their counts.</summary>
    internal readonly record struct LootWindow(ulong Source, byte Type, uint Coins, List<byte> Slots, List<(uint Item, uint Count)>? Items = null);

    /// <summary>Read an SMSG_LOOT_RESPONSE the way the brain does (<c>PlayerbotBrain.LootManagedPackets</c>): guid, type, coins, count, then 22 bytes an item.</summary>
    internal static bool TryParseLoot(byte[] payload, out LootWindow window)
    {
        window = default;
        if (payload.Length < 9) return false;
        var reader = new PacketReader(payload);
        ulong source = reader.ReadUInt64();
        byte type = reader.ReadByte();
        var slots = new List<byte>();
        List<(uint Item, uint Count)>? items = null;
        uint coins = 0;
        if (type == 1 && payload.Length >= 14)
        {
            coins = reader.ReadUInt32();
            byte count = reader.ReadByte();
            for (int index = 0; index < count && reader.Remaining >= 22; index++)
            {
                byte slot = reader.ReadByte();
                uint itemId = reader.ReadUInt32();
                uint itemCount = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadInt32();
                byte slotType = reader.ReadByte();
                if (slotType == 0)
                {
                    slots.Add(slot);
                    (items ??= []).Add((itemId, itemCount));
                }
            }
        }

        window = new LootWindow(source, type, coins, slots, items);
        return true;
    }
}
