using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>What a summon job uses to bring the member: a warlock's Ritual of Summoning, or (non-vanilla) a dungeon meeting stone.</summary>
internal enum SummonKind : byte { Ritual, Stone }

/// <summary>
/// One summon under way: who summons whom, the portal or stone it works at, and the group bots that help. Created and dropped by
/// <see cref="PlayerbotSummons"/>; mutable, world thread only.
/// </summary>
internal sealed class SummonJob(SummonKind kind, ObjectGuid summoner, ObjectGuid target, uint mapId, uint startMs, int needed)
{
    public SummonKind Kind { get; } = kind;
    public ObjectGuid Summoner { get; } = summoner;
    public ObjectGuid Target { get; } = target;
    public uint MapId { get; } = mapId;
    public uint StartMs { get; } = startMs;

    /// <summary>The helpers the job takes besides the summoner: the ritual's participants less the owner (two), or one partner at a stone.</summary>
    public int Needed { get; } = needed;

    /// <summary>The portal (ritual) or the stone: empty until the summoner found it.</summary>
    public ObjectGuid Anchor { get; set; }

    public Vector3 AnchorPosition { get; set; }

    /// <summary>The bots that took a helper place (they hold still while the job lasts).</summary>
    public HashSet<ObjectGuid> Helpers { get; } = [];

    /// <summary>The ritual spell cast (<see cref="SummonKind.Ritual"/>) and what the server made of the request.</summary>
    public TravelCastLeg? Cast { get; set; }

    public bool Announced { get; set; }

    /// <summary>When the summoner last used the stone, and how often (a stone job only; a ritual helper keeps its own count in the bot's state).</summary>
    public uint LastUseMs { get; set; }

    public int UseTries { get; set; }
}

/// <summary>
/// Bots bring far group members to the group (docs/areas/playbots-groups.md, Summons). Driven from the managed feature's think
/// (<c>ManagedPlayerbotFeature.SummonHolds</c>) for every bot, whichever driver owns it: <see cref="Drive"/> accepts summons from the bot's own
/// group, takes a part in a summon under way (the summoner, a helper) and answers whether the bot has to hold still for it, else maybe starts one.
/// <para>
/// <b>Ritual.</b> A warlock that knows Ritual of Summoning (found by its TRANS_DOOR effect on the Summoning Portal), carries its reagents (the Soul
/// Shard), stands still out of combat in a group and sees a member on another map or farther than <see cref="PlayerbotSummonOptions.FarYards"/> for
/// <see cref="FarDebounceMs"/> selects the member (CMSG_SET_SELECTION) and casts the spell (CMSG_CAST_SPELL). Group bots near the portal walk to it and
/// click it (CMSG_GAMEOBJ_USE) until the server turns it active; the server then sends the member SMSG_SUMMON_REQUEST. The warlock and the helpers hold
/// still meanwhile (the think is theirs), so the channel is not broken.
/// </para>
/// <para>
/// <b>Meeting stone</b> (non-vanilla, <see cref="PlayerbotSummonOptions.MeetingStones"/>). With no warlock in the group able to summon, a bot standing at
/// a dungeon meeting stone with another group bot beside it selects the member and uses the stone (CMSG_GAMEOBJ_USE); the server sends the same request.
/// </para>
/// <para>
/// <b>Accepting.</b> A bot with a summon request from a member of its group accepts with CMSG_SUMMON_RESPONSE unless it fights, is dead, flies, rides a
/// transport, casts, is in a battleground or has a part in a summon itself. A real player answers on his own client.
/// </para>
/// <para>
/// <b>Limits.</b> One job per summoner and per target; a target is not tried again for <see cref="PlayerbotSummonOptions.RetrySeconds"/> after an
/// attempt, whatever its end (declined, ignored, failed). State is per instance (one per managed feature), world thread only.
/// </para>
/// </summary>
internal sealed class PlayerbotSummons
{
    /// <summary>A member must stay far this long before it is summoned, so a bot just logged in or teleporting is not.</summary>
    internal const uint FarDebounceMs = 10_000;

    /// <summary>
    /// A far-since entry not refreshed for this long (picks gated meanwhile) is restarted, not trusted. Known limit: it is three <see cref="EvalGapMs"/>, so a host whose think rate
    /// starves the evaluations to a gap above this restarts the <see cref="FarDebounceMs"/> debounce on every look and no summon starts until the thinks catch up (a stricter gate than
    /// not having the rule; the failure is a missing summon, never a wrong one).
    /// </summary>
    internal const uint FarStaleMs = 6_000;

    /// <summary>How often one bot looks for a summon to start.</summary>
    internal const uint EvalGapMs = 2_000;

    /// <summary>The longest a ritual may take from the first cast, and a stone use from the first walk.</summary>
    internal const uint RitualMaxMs = 120_000;

    internal const uint StoneMaxMs = 60_000;

    /// <summary>A helper clicks within this distance of the portal (the server's interaction distance is 5).</summary>
    internal const float UseYards = 4f;

    /// <summary>A bot counts as beside the stone within this many yards of it.</summary>
    internal const float StonePartnerYards = 8f;

    /// <summary>The gap between two clicks of a helper, or two uses of a stone.</summary>
    private const uint MinRetryGapMs = 1_000;

    private readonly Dictionary<ObjectGuid, SummonJob> _bySummoner = [];
    private readonly Dictionary<ObjectGuid, uint> _cooldownUntil = [];
    private readonly Dictionary<ObjectGuid, (uint Since, uint Seen)> _farSince = [];
    private readonly Dictionary<ObjectGuid, BotState> _bots = [];
    /// <summary>
    /// The meeting stones of each map, rescanned every <see cref="StoneScanMs"/>. The key and the cached objects are strong references, so a closed instance map stays referenced until
    /// <see cref="Prune"/> ages it out (five minutes, and Prune runs only from a grouped bot's <see cref="Drive"/>) or the feature is switched off. A stone that was gone or not yet
    /// grid-loaded at scan time is misread for up to one scan interval; <c>DriveStone</c> catches a gone stone when it is used, at the cost of a Fail and the target's cooldown.
    /// </summary>
    private readonly Dictionary<Map, StoneCache> _stones = [];
    private readonly List<ObjectGuid> _goneHelpers = [];
    private uint _lastPruneMs;
    private uint _groupsRetryMs;
    private bool _groupsFaulted;

    /// <summary>The meeting stones of one map, scanned from its objects at <c>BuiltMs</c> (the stones are spawns that do not come and go, so the scan is not repeated every think).</summary>
    private readonly record struct StoneCache(uint BuiltMs, GameObject[] Stones);

    /// <summary>How long a map's stone list is used before the map's objects are scanned again.</summary>
    private const uint StoneScanMs = 60_000;

    /// <summary>The summons begun since the start (tests).</summary>
    internal int Started { get; private set; }

    /// <summary>The summon requests the bots accepted since the start (tests).</summary>
    internal int Accepted { get; private set; }

    /// <summary>The jobs under way (tests).</summary>
    internal int Active => _bySummoner.Count;

    /// <summary>The job of <paramref name="summoner"/>, or null (tests).</summary>
    internal SummonJob? JobOf(ObjectGuid summoner) => _bySummoner.GetValueOrDefault(summoner);

    /// <summary>The portal clicks the bot has sent for its latest helper job, 0 for a bot with no state (tests). World thread.</summary>
    internal int ClicksOf(ObjectGuid bot) => _bots.TryGetValue(bot, out BotState? state) ? state.ClickTries : 0;

    /// <summary>How many meeting stones the cached list of <paramref name="map"/> holds, or -1 when the map was never scanned (tests). World thread.</summary>
    internal int StonesCached(Map map) => _stones.TryGetValue(map, out StoneCache cache) ? cache.Stones.Length : -1;

    private sealed class BotState
    {
        public uint NextEvalMs;
        public uint LastAcceptMs;
        public uint SeenMs;
        public uint RitualScanMs;
        public uint RitualSpellId;
        public SummonJob? WalkJob;
        public readonly TravelWalk Walk = new();

        /// <summary>The ritual job this bot clicked the portal for as a helper, and its own click count and last click (not shared with the other helpers).</summary>
        public SummonJob? ClickJob;
        public int ClickTries;
        public uint ClickLastMs;
        public ObjectGuid RefusedTarget;
        public uint RefusedUntilMs;
    }

    /// <summary>
    /// One think of a bot. True when the bot has a part in a summon and holds still for it (the caller skips every other driver); false otherwise.
    /// Cost when nothing is going on: the option read, a group lookup and one dictionary probe.
    /// </summary>
    /// <param name="groupState">The state of the bot-led group the bot is in, or null when it is in none.</param>
    internal bool Drive(WorldSession session, Player player, PlayerbotOptions options, PlayerbotGroupState? groupState, uint elapsedMs)
    {
        if (!options.Summon.Enabled)
        {
            if (_bots.Count > 0 || _bySummoner.Count > 0 || _stones.Count > 0) Clear(session);
            return false;
        }

        uint now = session.World.NowMs;
        if (!player.IsInWorld || !player.IsAlive || player.Map is not { } map || Groups(session, now) is not { } groups
            || groups.GetGroup(player.Guid) is not { IsBattlegroundGroup: false, MemberCount: >= 2 } group
            || map.Template is { IsBattleground: true })
        {
            Forget(session, player);
            return false;
        }

        BotState state = _bots.TryGetValue(player.Guid, out BotState? known) ? known : (_bots[player.Guid] = new BotState());
        state.SeenMs = now;
        Prune(now);
        DropStale(session, now);

        TryAccept(session, player, groups, state, now);

        if (_bySummoner.TryGetValue(player.Guid, out SummonJob? own)) return DriveSummoner(session, player, options, group, own, state, elapsedMs, now);
        if (_bySummoner.Count > 0 && HelperJob(player, groups, state, options, now) is { } helping)
            return DriveHelper(session, player, options, helping, state, elapsedMs, now);

        if (state.NextEvalMs != 0 && unchecked((int)(now - state.NextEvalMs)) < 0) return false; // 0 is a fresh state, never a time to wait for
        state.NextEvalMs = now + EvalGapMs == 0 ? 1 : now + EvalGapMs;
        return TryStart(session, player, options, group, groupState, state, elapsedMs, now);
    }

    // --- accepting -----------------------------------------------------------------------------------------------------

    private void TryAccept(WorldSession session, Player player, GroupManager groups, BotState state, uint now)
    {
        if (player.PendingSummon is not { } offer || offer.ExpiresAtMs < now) return;
        if (unchecked(now - state.LastAcceptMs) < 3_000 && state.LastAcceptMs != 0) return;
        if (player.Combat.IsInCombat || player.Transport is not null || PlayerbotTaxi.IsFlying(player) || !groups.AreInSameGroup(player.Guid, offer.Summoner)) return;
        if (_bySummoner.ContainsKey(player.Guid) || IsHelper(player.Guid)) return;
        if (session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting }) return;
        if (session.Services.GetService<ArcaneCore.World.Teleport.TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true) return;
        if (!session.TryManagedAction(WorldOpcode.CmsgSummonResponse, PlayerbotNavigation.GuidPayload(offer.Summoner.Value))) return;
        state.LastAcceptMs = now == 0 ? 1 : now;
        Accepted++;
    }

    /// <summary>Whether the bot holds a helper place of a job under way (the accept step and tests).</summary>
    internal bool IsHelper(ObjectGuid guid)
    {
        foreach (SummonJob job in _bySummoner.Values)
            if (job.Helpers.Contains(guid)) return true;
        return false;
    }

    // --- starting ------------------------------------------------------------------------------------------------------

    private bool TryStart(WorldSession session, Player player, PlayerbotOptions options, Group group, PlayerbotGroupState? groupState,
        BotState state, uint elapsedMs, uint now)
    {
        if (player.Combat.IsInCombat || player.Transport is not null || PlayerbotTaxi.IsFlying(player)) return false;
        if (groupState is { } gs && gs is not (PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling or PlayerbotGroupState.Regrouping)) return false;
        if (session.Services.GetService<SpellFeature>() is not { } spells) return false;

        // The ritual first (a warlock), the meeting stone only when no warlock of the group can summon.
        SpellInfo? ritual = player.Class == Class.Warlock ? RitualOf(spells, player, state, now) : null;
        bool ritualReady = ritual is not null && CanCast(spells, player, ritual);
        if (!ritualReady && (!options.Summon.MeetingStones || AnyWarlockCanSummon(session, spells, group))) return false;

        PlayerbotSummonOptions cfg = options.Summon;
        if (ritualReady && (player.Movement.HasFlag(MovementFlags.MaskMoving) || PlayerbotMotion.IsActive(player))) return false;
        if (ritualReady)
        {
            Player? target = PickTarget(session, player, group, cfg, now, anchor: null);
            if (target is null) return false;
            if (player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects) return false;
            int needed = Math.Max(1, (int)(objects.FindTemplate(GameObjectMapSystem.PlayerSummoningRitualEntry)?.GetData(GameObjectMapSystem.RitualParticipantsData) ?? 3) - 1);
            if (CountHelpers(session, player, target, group, new Vector3(player.X, player.Y, player.Z), cfg.HelperYards) < needed) return false;
            var job = new SummonJob(SummonKind.Ritual, player.Guid, target.Guid, player.MapId, now, needed);
            _bySummoner[player.Guid] = job;
            _cooldownUntil[target.Guid] = now + (uint)cfg.RetrySeconds * 1000u;
            Started++;
            return DriveSummoner(session, player, options, group, job, state, elapsedMs, now);
        }

        if (FindStone(player, now, cfg.HelperYards) is not { } stone) return false;
        Vector3 at = new(stone.X, stone.Y, stone.Z);
        Player? aimed = PickTarget(session, player, group, cfg, now, anchor: at);
        if (aimed is null || CountHelpers(session, player, aimed, group, at, cfg.HelperYards) < 1) return false;
        var stoneJob = new SummonJob(SummonKind.Stone, player.Guid, aimed.Guid, player.MapId, now, 1) { Anchor = stone.Guid, AnchorPosition = at };
        _bySummoner[player.Guid] = stoneJob;
        _cooldownUntil[aimed.Guid] = now + (uint)cfg.RetrySeconds * 1000u;
        Started++;
        return DriveSummoner(session, player, options, group, stoneJob, state, elapsedMs, now);
    }

    /// <summary>
    /// The first group member worth summoning: not the summoner, online, alive, out of combat, no summon pending, not tried within the retry gap, not in
    /// a battleground, not in a dungeon other than the summoner's map, and far (another map, or farther than <see cref="PlayerbotSummonOptions.FarYards"/>
    /// from the summoner or the stone) for <see cref="FarDebounceMs"/>.
    /// </summary>
    private Player? PickTarget(WorldSession session, Player summoner, Group group, PlayerbotSummonOptions cfg, uint now, Vector3? anchor)
    {
        Player? pick = null;
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (slot.Guid == summoner.Guid) continue;
            if (session.World.FindOnlinePlayer(slot.Guid) is not { IsInWorld: true, IsAlive: true, Map: { } map } member)
            {
                _farSince.Remove(slot.Guid); // offline or dead: the far time starts over when the member is back
                continue;
            }

            bool far = member.MapId != summoner.MapId
                || Vector3.Distance(new Vector3(member.X, member.Y, member.Z), anchor ?? new Vector3(summoner.X, summoner.Y, summoner.Z)) > cfg.FarYards;
            if (!far)
            {
                _farSince.Remove(slot.Guid);
                continue;
            }

            // An entry not looked at for FarStaleMs (the picks were gated: a group state outside the allowed list, combat) starts over: the member may
            // have come back and left since, so the debounce is not skipped when the gate lifts.
            uint since = FarStart(_farSince.TryGetValue(slot.Guid, out (uint Since, uint Seen) entry) ? entry : null, now);
            _farSince[slot.Guid] = (since, now);
            if (unchecked(now - since) < FarDebounceMs || pick is not null) continue;
            if (member.Combat.IsInCombat || member.Transport is not null || member.PendingSummon is { } pending && pending.ExpiresAtMs >= now) continue;
            if (_cooldownUntil.TryGetValue(slot.Guid, out uint until) && unchecked((int)(until - now)) > 0) continue;
            if (map.Template is { IsBattleground: true } || map.Template is { IsDungeon: true } && member.MapId != summoner.MapId) continue;
            if (PlayerbotTaxi.IsFlying(member) || IsTarget(slot.Guid)) continue;
            pick = member;
        }

        return pick;
    }

    /// <summary>The time a member counts as far from: the entry's start while it was looked at within <see cref="FarStaleMs"/>, else <paramref name="now"/>.</summary>
    internal static uint FarStart((uint Since, uint Seen)? entry, uint now)
        => entry is { } e && unchecked(now - e.Seen) < FarStaleMs ? e.Since : now;

    private bool IsTarget(ObjectGuid guid)
    {
        foreach (SummonJob job in _bySummoner.Values)
            if (job.Target == guid) return true;
        return false;
    }

    /// <summary>Group bots (not the summoner, not the target) alive, out of combat and on the summoner's map within <paramref name="yards"/> of <paramref name="at"/>.</summary>
    private static int CountHelpers(WorldSession session, Player summoner, Player target, Group group, Vector3 at, float yards)
    {
        int count = 0;
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (slot.Guid == summoner.Guid || slot.Guid == target.Guid) continue;
            if (session.World.FindOnlinePlayer(slot.Guid) is { IsInWorld: true, IsAlive: true } other && IsBot(other) && !other.Combat.IsInCombat
                && other.MapId == summoner.MapId && Vector3.Distance(new Vector3(other.X, other.Y, other.Z), at) <= yards) count++;
        }

        return count;
    }

    /// <summary>The spawned meeting stone nearest to the player within <paramref name="yards"/>, from the map's cached stone list (the object table is scanned once a minute per map, not on every think).</summary>
    private GameObject? FindStone(Player player, uint now, float yards)
    {
        if (player.Map is not { } map) return null;
        if (!_stones.TryGetValue(map, out StoneCache cache) || unchecked(now - cache.BuiltMs) >= StoneScanMs)
        {
            if (map.FindUpdater<GameObjectMapSystem>() is not { } objects) return null;
            List<GameObject> found = [];
            foreach (GameObject go in objects.GameObjects)
                if (go.Type == GameObjectType.MeetingStone) found.Add(go);
            cache = new StoneCache(now == 0 ? 1 : now, [.. found]);
            _stones[map] = cache;
        }

        GameObject? best = null;
        float bestDistance = yards;
        foreach (GameObject go in cache.Stones)
        {
            if (!go.IsSpawned) continue;
            float d = Vector3.Distance(new Vector3(go.X, go.Y, go.Z), new Vector3(player.X, player.Y, player.Z));
            if (d > bestDistance) continue;
            best = go;
            bestDistance = d;
        }

        return best;
    }

    // --- the ritual spell -----------------------------------------------------------------------------------------------

    /// <summary>The Ritual of Summoning the player knows: the known spell with a TRANS_DOOR effect on the warlock's Summoning Portal.</summary>
    private static SpellInfo? RitualOf(SpellFeature spells, Player player, BotState state, uint now)
    {
        if (state.RitualSpellId != 0 && spells.Spellbook.HasSpell(player, state.RitualSpellId)) return spells.System.Store.Get(state.RitualSpellId);
        if (state.RitualSpellId == 0 && unchecked(now - state.RitualScanMs) < 3_000 && state.RitualScanMs != 0) return null;
        state.RitualScanMs = now == 0 ? 1 : now;
        state.RitualSpellId = 0;
        foreach (uint id in spells.Spellbook.GetSpells(player))
        {
            if (spells.System.Store.Get(id) is not { } spell || !IsRitual(spell)) continue;
            state.RitualSpellId = id;
            return spell;
        }

        return null;
    }

    private static bool IsRitual(SpellInfo spell)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
            if (effect.Effect == SpellEffectName.TransDoor && effect.MiscValue == (int)GameObjectMapSystem.PlayerSummoningRitualEntry) return true;
        return false;
    }

    private static bool CanCast(SpellFeature spells, Player player, SpellInfo spell)
    {
        foreach (SpellReagent reagent in spell.Reagents)
            if (reagent.IsPresent && player.Inventory.GetItemCount(reagent.ItemId) < reagent.Count) return false;
        if (PlayerbotTravelCast.OnCooldown(spells.System, player, spell.Id, spell.Category)) return false;
        return spells.System.GetState(player.Guid)?.CurrentCast is not { State: SpellCastState.Preparing or SpellCastState.Casting };
    }

    /// <summary>Whether a living warlock of the group knows the ritual and has its reagents (then the group has a better way than the stone).</summary>
    private bool AnyWarlockCanSummon(WorldSession session, SpellFeature spells, Group group)
    {
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (session.World.FindOnlinePlayer(slot.Guid) is not { IsInWorld: true, IsAlive: true, Class: Class.Warlock } warlock || !IsBot(warlock)) continue;
            BotState state = _bots.TryGetValue(warlock.Guid, out BotState? known) ? known : (_bots[warlock.Guid] = new BotState());
            if (RitualOf(spells, warlock, state, session.World.NowMs) is not { } spell) continue;
            bool reagents = true;
            foreach (SpellReagent reagent in spell.Reagents)
                if (reagent.IsPresent && warlock.Inventory.GetItemCount(reagent.ItemId) < reagent.Count) reagents = false;
            if (reagents) return true;
        }

        return false;
    }

    // --- the summoner ---------------------------------------------------------------------------------------------------

    private bool DriveSummoner(WorldSession session, Player player, PlayerbotOptions options, Group group, SummonJob job, BotState state, uint elapsedMs, uint now)
    {
        Player? target = session.World.FindOnlinePlayer(job.Target);
        bool offered = target?.PendingSummon is { } pending && pending.Summoner == player.Guid && pending.ExpiresAtMs >= now;
        if (offered)
        {
            if (job.Kind == SummonKind.Ritual && job.Cast is not null) StopRitual(session, player, job.Cast.SpellId);
            return End(player);
        }

        uint limit = job.Kind == SummonKind.Ritual ? RitualMaxMs : StoneMaxMs;
        if (target is not { IsInWorld: true, IsAlive: true } || player.Combat.IsInCombat || group.Find(job.Target) is null
            || unchecked(now - job.StartMs) > limit || player.MapId != job.MapId || player.Transport is not null)
            return Fail(session, player, job);

        return job.Kind == SummonKind.Ritual
            ? DriveRitual(session, player, options, group, job, target, now)
            : DriveStone(session, player, options, group, job, target, state, elapsedMs, now);
    }

    private bool DriveRitual(WorldSession session, Player player, PlayerbotOptions options, Group group, SummonJob job, Player target, uint now)
    {
        if (session.Services.GetService<SpellFeature>() is not { } spells || !_bots.TryGetValue(player.Guid, out BotState? state)
            || RitualOf(spells, player, state, now) is not { } spell) return Fail(session, player, job);
        if (job.Cast is null)
        {
            job.Cast = new TravelCastLeg(TravelMeans.MagePortal, spell.Id, default, now);
            session.TryManagedAction(WorldOpcode.CmsgSetSelection, PlayerbotNavigation.GuidPayload(target.Guid.Value));
        }

        if (job.Cast.Stage == TravelCastStage.Start)
        {
            if (player.Selection != target.Guid) session.TryManagedAction(WorldOpcode.CmsgSetSelection, PlayerbotNavigation.GuidPayload(target.Guid.Value));
            if (unchecked(now - job.Cast.BeganMs) > PlayerbotTravelCast.StartBoundMs) return Fail(session, player, job);
            switch (PlayerbotTravelCast.Send(session, player, spells.System, job.Cast, WorldOpcode.CmsgCastSpell,
                        PlayerbotTravelCast.CastPayload(spell.Id, SpellCastTargets.ForSelf()), out _))
            {
                case PlayerbotTravelCast.Sent.Taken:
                    job.Cast.Stage = TravelCastStage.Casting;
                    job.Cast.CastMs = now;
                    Announce(session, player, options, group, job, target);
                    return true;
                case PlayerbotTravelCast.Sent.Retry:
                    return true;
                default:
                    return Fail(session, player, job);
            }
        }

        if (player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects) return Fail(session, player, job);
        if (job.Anchor.IsEmpty)
        {
            foreach (GameObject go in objects.GameObjects)
            {
                if (!go.IsSpawned || go.Entry != GameObjectMapSystem.PlayerSummoningRitualEntry || go.OwnerGuid != player.Guid) continue;
                job.Anchor = go.Guid;
                job.AnchorPosition = new Vector3(go.X, go.Y, go.Z);
                break;
            }

            // The portal is made when the channel starts: no channel and no portal after the cast was taken means the cast was broken.
            if (job.Anchor.IsEmpty && !PlayerbotTravelCast.IsCasting(spells.System, player, spell.Id) && unchecked(now - job.Cast.CastMs) > 3_000) return Fail(session, player, job);
        }
        else if (objects.Find(job.Anchor) is not { IsSpawned: true })
        {
            return Fail(session, player, job); // the portal went without a request for the member (the channel broke, or too few helpers)
        }

        if (player.Movement.HasFlag(MovementFlags.MaskMoving)) PlayerbotMovementControl.Stop(session, player);
        return true;
    }

    private bool DriveStone(WorldSession session, Player player, PlayerbotOptions options, Group group, SummonJob job, Player target, BotState state, uint elapsedMs, uint now)
    {
        if (player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects || objects.Find(job.Anchor) is not { IsSpawned: true } stone) return Fail(session, player, job);
        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), job.AnchorPosition) > UseYards)
        {
            if (state.WalkJob != job)
            {
                state.WalkJob = job;
                state.Walk.Reset();
            }

            return state.Walk.Step(session, player, options, job.AnchorPosition, elapsedMs, now) ? true : Fail(session, player, job);
        }

        if (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)) return true;
        int beside = 0;
        foreach (ObjectGuid helper in job.Helpers)
            if (session.World.FindOnlinePlayer(helper) is { IsInWorld: true, IsAlive: true } other
                && Vector3.Distance(new Vector3(other.X, other.Y, other.Z), job.AnchorPosition) <= StonePartnerYards) beside++;
        if (beside < job.Needed) return true; // the partner is still on its way (the job's time bound ends the wait)

        if (unchecked(now - job.LastUseMs) < MinRetryGapMs && job.LastUseMs != 0) return true;
        if (job.UseTries >= 3) return Fail(session, player, job);
        if (player.Selection != target.Guid) session.TryManagedAction(WorldOpcode.CmsgSetSelection, PlayerbotNavigation.GuidPayload(target.Guid.Value));
        if (!session.TryManagedAction(WorldOpcode.CmsgGameobjUse, PlayerbotNavigation.GuidPayload(stone.Guid.Value))) return true; // refused for budget: not a try
        job.LastUseMs = now == 0 ? 1 : now;
        job.UseTries++;
        Announce(session, player, options, group, job, target);
        if (target.PendingSummon is { } offer && offer.Summoner == player.Guid) return End(player);
        return true;
    }

    // --- helpers --------------------------------------------------------------------------------------------------------

    /// <summary>The job this bot helps, taking a place when one is free and the bot is near enough (<see cref="PlayerbotSummonOptions.HelperYards"/>).</summary>
    private SummonJob? HelperJob(Player player, GroupManager groups, BotState state, PlayerbotOptions options, uint now)
    {
        if (player.Combat.IsInCombat || player.Transport is not null || !IsBot(player)) return null;
        foreach (SummonJob job in _bySummoner.Values)
        {
            if (job.Anchor.IsEmpty || job.MapId != player.MapId || job.Target == player.Guid || !groups.AreInSameGroup(player.Guid, job.Summoner)) continue;
            if (job.Helpers.Contains(player.Guid)) return job;
            if (job.Helpers.Count >= job.Needed) continue;
            if (state.RefusedTarget == job.Target && unchecked((int)(state.RefusedUntilMs - now)) > 0) continue;
            if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), job.AnchorPosition) > options.Summon.HelperYards) continue;
            job.Helpers.Add(player.Guid);
            return job;
        }

        return null;
    }

    private bool DriveHelper(WorldSession session, Player player, PlayerbotOptions options, SummonJob job, BotState state, uint elapsedMs, uint now)
    {
        if (player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects || objects.Find(job.Anchor) is not { IsSpawned: true } anchor)
            return Release(player, job, state, now);
        float reach = job.Kind == SummonKind.Ritual ? UseYards : StonePartnerYards;
        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), job.AnchorPosition) > reach)
        {
            if (state.WalkJob != job)
            {
                state.WalkJob = job;
                state.Walk.Reset();
            }

            return state.Walk.Step(session, player, options, job.AnchorPosition, elapsedMs, now) || Release(player, job, state, now);
        }

        if (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)) return true;
        if (job.Kind == SummonKind.Ritual && !anchor.Participants.Contains(player.Guid))
        {
            // CMSG_GAMEOBJ_USE: counted once the owner channels; each helper tries up to twelve times a second apart (its own count and gap, not the other helper's);
            // after that the helper keeps its place until the job ends (its time bound).
            if (state.ClickJob != job)
            {
                state.ClickJob = job;
                state.ClickTries = 0;
                state.ClickLastMs = 0;
            }

            if (state.ClickTries < 12 && (state.ClickLastMs == 0 || unchecked(now - state.ClickLastMs) >= MinRetryGapMs))
            {
                if (session.TryManagedAction(WorldOpcode.CmsgGameobjUse, PlayerbotNavigation.GuidPayload(anchor.Guid.Value)))
                {
                    state.ClickLastMs = now == 0 ? 1 : now;
                    state.ClickTries++; // only a click that was sent counts; a tick the action budget refused is tried again
                }
            }
        }

        return true;
    }

    private static bool Release(Player player, SummonJob job, BotState state, uint now)
    {
        job.Helpers.Remove(player.Guid);
        state.RefusedTarget = job.Target;
        state.RefusedUntilMs = now + 30_000;
        state.WalkJob = null;
        return false;
    }

    // --- ends -----------------------------------------------------------------------------------------------------------

    private bool End(Player summoner)
    {
        _bySummoner.Remove(summoner.Guid);
        return false;
    }

    private bool Fail(WorldSession session, Player summoner, SummonJob job)
    {
        if (job.Kind == SummonKind.Ritual && job.Cast is { Stage: TravelCastStage.Casting }) StopRitual(session, summoner, job.Cast.SpellId);
        return End(summoner);
    }

    /// <summary>
    /// Ends the summoner's own ritual cast. The ritual is a channel. This repo's <see cref="SpellSystem.CancelCast"/> leaves a channel alone (pinned by
    /// SpellSeamTests), and CMSG_CANCEL_CHANNELLING is the packet a client sends to stop one (vmangos HandleCancelChanneling, SpellHandler.cpp:446-459), so a
    /// channel still running is stopped with that, and only a cast still being prepared gets CMSG_CANCEL_CAST. Left running it would keep the warlock "casting" with its portal gone and bar it from any further
    /// summon until the channel runs out. The job ends right after this and nothing retries, so the packet is sent unbudgeted (like the stop of a walk):
    /// a bot visited with no action left in the tick's shared budget must not be left channelling.
    /// </summary>
    internal static void StopRitual(WorldSession session, Player summoner, uint spellId)
    {
        if (session.Services.GetService<SpellFeature>()?.System is not { } spells
            || spells.GetState(summoner.Guid)?.CurrentCast is not { } cast || cast.Spell.Id != spellId) return;
        WorldOpcode opcode;
        if (cast.State == SpellCastState.Preparing) opcode = WorldOpcode.CmsgCancelCast;
        else if (cast.State == SpellCastState.Casting) opcode = WorldOpcode.CmsgCancelChannelling;
        else return;
        ManagedActionBudget? budget = session.ManagedBudget;
        session.ManagedBudget = null;
        try { session.TryManagedAction(opcode, BitConverter.GetBytes(spellId)); }
        finally { session.ManagedBudget = budget; }
    }

    private void Forget(WorldSession session, Player player)
    {
        if (_bots.Count == 0 && _bySummoner.Count == 0) return;
        _bots.Remove(player.Guid);
        if (_bySummoner.TryGetValue(player.Guid, out SummonJob? job)) Fail(session, player, job);
        foreach (SummonJob other in _bySummoner.Values) other.Helpers.Remove(player.Guid);
    }

    private void Clear(WorldSession session)
    {
        foreach (SummonJob job in _bySummoner.Values) Abandon(session, job);
        _bots.Clear();
        _bySummoner.Clear();
        _stones.Clear();
        _farSince.Clear();
        _cooldownUntil.Clear();
    }

    /// <summary>A job outlives its own bound by this much before any bot's think drops it (the summoner's own think ends it first).</summary>
    internal const uint StaleGraceMs = 5_000;

    /// <summary>
    /// Drops the jobs whose summoner is no longer in the world (logged out, kicked) or that outlived their time bound, whoever thinks. The summoner's own
    /// think ends a job normally; this is for a summoner that stopped reaching <see cref="Drive"/> (logged out, moved to a driver that skips it),
    /// which would otherwise freeze the stone's helper and bar the target from every other summon.
    /// </summary>
    internal void DropStale(WorldSession session, uint now)
    {
        if (_bySummoner.Count == 0) return;
        List<SummonJob>? stale = null;
        foreach (SummonJob job in _bySummoner.Values)
        {
            uint limit = (job.Kind == SummonKind.Ritual ? RitualMaxMs : StoneMaxMs) + StaleGraceMs;
            if (session.World.FindOnlinePlayer(job.Summoner) is not { IsInWorld: true } || unchecked(now - job.StartMs) > limit)
            {
                (stale ??= []).Add(job);
                continue;
            }

            DropGoneHelpers(session, job);
        }

        if (stale is null) return;
        foreach (SummonJob job in stale)
        {
            Abandon(session, job);
            _bySummoner.Remove(job.Summoner);
        }
    }

    /// <summary>Takes the helpers that left the world or died (and no longer think here) off a job, so they stop filling its places.</summary>
    private void DropGoneHelpers(WorldSession session, SummonJob job)
    {
        if (job.Helpers.Count == 0) return;
        _goneHelpers.Clear();
        foreach (ObjectGuid helper in job.Helpers)
            if (session.World.FindOnlinePlayer(helper) is not { IsInWorld: true, IsAlive: true }) _goneHelpers.Add(helper);
        foreach (ObjectGuid helper in _goneHelpers) job.Helpers.Remove(helper);
        _goneHelpers.Clear();
    }

    /// <summary>
    /// A job dropped without its summoner's own think (summons switched off, stale): a ritual channel the summoner still runs is stopped through the
    /// summoner's own session, as <see cref="Fail"/> does, so the warlock is not left casting with its portal gone.
    /// </summary>
    private static void Abandon(WorldSession session, SummonJob job)
    {
        if (job.Kind != SummonKind.Ritual || job.Cast is not { Stage: TravelCastStage.Casting } cast) return;
        if (session.World.FindOnlinePlayer(job.Summoner) is { } summoner && summoner.Session is WorldSession own) StopRitual(own, summoner, cast.SpellId);
    }

    /// <summary>Drop what has not been seen for five minutes (a bot logged out, a target long gone), once a minute.</summary>
    private void Prune(uint now)
    {
        if (unchecked(now - _lastPruneMs) < 60_000) return;
        _lastPruneMs = now;
        List<ObjectGuid>? gone = null;
        foreach ((ObjectGuid guid, BotState bot) in _bots)
            if (unchecked(now - bot.SeenMs) > 300_000) (gone ??= []).Add(guid);
        if (gone is not null)
            foreach (ObjectGuid guid in gone) _bots.Remove(guid);
        List<ObjectGuid>? cooled = null;
        foreach ((ObjectGuid guid, uint until) in _cooldownUntil)
            if (unchecked((int)(until - now)) <= 0) (cooled ??= []).Add(guid);
        if (cooled is not null)
            foreach (ObjectGuid guid in cooled) _cooldownUntil.Remove(guid);
        if (_farSince.Count > 256) _farSince.Clear();
        List<Map>? aged = null;
        foreach ((Map map, StoneCache cache) in _stones)
            if (unchecked(now - cache.BuiltMs) > 300_000) (aged ??= []).Add(map);
        if (aged is not null)
            foreach (Map map in aged) _stones.Remove(map);
    }

    // --- talking --------------------------------------------------------------------------------------------------------

    /// <summary>The party hears who is being summoned when that is a real player (template text only).</summary>
    private static void Announce(WorldSession session, Player player, PlayerbotOptions options, Group group, SummonJob job, Player target)
    {
        if (job.Announced) return;
        job.Announced = true;
        if (!options.Summon.AnnounceToPlayers || IsBot(target)) return;
        string text = job.Kind == SummonKind.Ritual
            ? $"Summoning {target.Name}, click the portal."
            : $"Summoning {target.Name} at the meeting stone, accept the summon.";
        Language language = player.Team == Team.Horde ? Language.Orcish : Language.Common;
        session.TryManagedAction(WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Message(group.IsRaid ? ChatType.Raid : ChatType.Party, language, text));
    }

    /// <summary>Whether the player is one of the managed bots (a real player's session is not managed).</summary>
    private static bool IsBot(Player player) => player.Session is WorldSession { IsManaged: true };

    /// <summary>
    /// The group manager, or null when the social feature is not attached (a world without it). That is no error of this think: it is recorded once,
    /// and the lookup is not tried again for <see cref="GroupsRetryMs"/> (the failed lookup throws).
    /// </summary>
    private GroupManager? Groups(WorldSession session, uint now)
    {
        if (_groupsRetryMs != 0 && unchecked((int)(now - _groupsRetryMs)) < 0) return null;
        try
        {
            GroupManager? groups = session.Services.GetService<SocialFeature>()?.Context.Groups;
            _groupsFaulted = false;
            _groupsRetryMs = 0;
            return groups;
        }
        catch (InvalidOperationException ex)
        {
            if (!_groupsFaulted) FaultLedger.Shared.Record("playerbot.summons", ex);
            _groupsFaulted = true;
            uint retry = now + GroupsRetryMs;
            _groupsRetryMs = retry == 0 ? 1 : retry;
            return null;
        }
    }

    private const uint GroupsRetryMs = 5_000;
}
