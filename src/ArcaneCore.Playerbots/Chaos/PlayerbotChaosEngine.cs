using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Chaos;

/// <summary>The adversarial probe family executed by a chaos bot.</summary>
public enum ChaosProbeCategory
{
    TruncatedWire,
    MutatedWire,
    DeathAndResurrection,
    CombatAndSpell,
    InventoryAndBag,
    QuestAndNpc,
    LootVendorEconomy,
    SocialTradeMailBg,
    BoundaryMovement,
    TalentAndSkill,
    PetAndStable,
    AuctionMailBank,
    WorldTransitAndInstance,
    ChatChannelAndQuery,
    StateRaceCombo,
}

/// <summary>One unhandled server exception or invariant breach discovered by a chaos bot probe.</summary>
public sealed record ChaosFaultRecord(
    DateTimeOffset AtUtc,
    Guid BotId,
    string BotName,
    ChaosProbeCategory Category,
    string ProbeName,
    WorldOpcode Opcode,
    int PayloadLength,
    string PayloadHex,
    string ExceptionType,
    string Message);

/// <summary>Point-in-time summary of the chaos engine (<c>.playerbot chaos status</c>).</summary>
public sealed record ChaosEngineStatus(
    bool Enabled,
    int ConfiguredActiveBots,
    int AssignedBots,
    int ProbesPerThink,
    int Seed,
    bool MalformedPayloads,
    bool StateRaces,
    bool BoundaryMovement,
    long ProbesExecuted,
    long MalformedRejected,
    long FaultsCaught,
    long InvariantRepairs,
    IReadOnlyDictionary<ChaosProbeCategory, long> ProbesByCategory,
    IReadOnlyList<ChaosFaultRecord> RecentFaults,
    IReadOnlyList<string> AssignedBotIds);

/// <summary>
/// Adversarial server-breaker engine for managed playerbots (<c>World:Playerbots:Chaos</c>).
/// <para>
/// Each active chaos bot continues its normal world play (movement, combat, quests, death/resurrection) so it reaches real
/// server states, and on every think (or on an operator <c>.playerbot chaos burst</c>) dispatches bounded adversarial probes
/// directly through <see cref="WorldSession.TryManagedAction"/> against the live world handlers:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="ChaosProbeCategory.TruncatedWire"/> and <see cref="ChaosProbeCategory.MutatedWire"/>: 0-byte, 1-byte, partial-GUID, boundary-integer and random-byte payloads across every registered <see cref="OpcodeHandler.World"/> handler.</description></item>
/// <item><description><see cref="ChaosProbeCategory.DeathAndResurrection"/>: <c>CMSG_REPOP_REQUEST</c>, <c>CMSG_RECLAIM_CORPSE</c>, <c>CMSG_RESURRECT_RESPONSE</c>, <c>CMSG_SPIRIT_HEALER_ACTIVATE</c> and <c>CMSG_SELF_RES</c> fired out of order (while alive, while already a ghost, with bogus spirit healer/corpse GUIDs).</description></item>
/// <item><description><see cref="ChaosProbeCategory.CombatAndSpell"/>: self-attack, bogus-GUID attack, rapid attack-swing/stop toggles, cast spell 0 / <see cref="uint.MaxValue"/> / unknown spell / self-harm / stale target GUID, cancel cast, cancel aura and cancel channel races.</description></item>
/// <item><description><see cref="ChaosProbeCategory.InventoryAndBag"/>: self-swap, out-of-range bag/slot indices (255, 254, 127), autoequip/autobank/autostore boundary slots, destroy item 0, split item with 0 or <see cref="byte.MaxValue"/> count, and use-item with invalid cast flags.</description></item>
/// <item><description><see cref="ChaosProbeCategory.QuestAndNpc"/>: gossip/questgiver hello, query, accept, complete, choose-reward and cancel against self, bogus GUIDs or nearby NPCs with out-of-range reward indices, plus trainer/binder/stable/petition packets.</description></item>
/// <item><description><see cref="ChaosProbeCategory.LootVendorEconomy"/>: loot/release/autostore/money against self, dead/alive creatures or bogus GUIDs, buy/sell/buyback with 0 or max counts, and auction/mail queries.</description></item>
/// <item><description><see cref="ChaosProbeCategory.SocialTradeMailBg"/>: initiate/accept/cancel/set-gold/set-item trade races, duel accept/cancelled without a duel, group invite/uninvite/disband/loot-method races, guild/channel/friend/ignore boundary strings, and battleground port/status/leave packets.</description></item>
/// <item><description><see cref="ChaosProbeCategory.BoundaryMovement"/>: <c>MSG_MOVE_*</c> and <c>CMSG_MOVE_*</c> packets with boundary flags, extreme orientations, fall/jump/swim transitions, time-skipped extremes and non-finite coordinate probes (verifying the movement handler rejects NaN/Infinity and rolling back if any ever slipped through).</description></item>
/// </list>
/// <para>
/// Containment: expected <see cref="MalformedPacket"/> rejections (<see cref="ArgumentOutOfRangeException"/> with
/// <c>ParamName == MalformedPacket.ParamName</c>) are counted as clean rejections. Any other exception is caught per probe,
/// recorded in <see cref="FaultLedger.Shared"/> and logged in the <c>Playerbot {BotId} chaos failed ({Type}: {Message})</c>
/// format so Bot QA (<c>.playerbot qa</c>) aggregates and ranks it automatically — without kicking or quarantining the chaos bot.
/// After every probe, life, combat, spell and visibility invariants are verified and self-healed.
/// </para>
/// </summary>
public sealed class PlayerbotChaosEngine
{
    public const int MaxRecentFaults = 32;
    public const int MaxBurstCount = 256;
    public const uint PostProbeStuckTimeoutMs = 20_000;
    public const float PostProbeMinProgressYards = 1.5f;

    private readonly PlayerbotOptions _options;
    private readonly OpcodeTable _opcodes;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, byte> _assignedBots = new();
    private readonly ConcurrentDictionary<Guid, Random> _botRandoms = new();
    private readonly ConcurrentDictionary<Guid, PostProbeWatch> _postProbeWatches = new();
    private readonly long[] _categoryCounts = new long[Enum.GetValues<ChaosProbeCategory>().Length];
    private readonly object _faultsGate = new();
    private readonly List<ChaosFaultRecord> _recentFaults = new(MaxRecentFaults);
    private WorldOpcode[]? _worldOpcodes;
    private long _probesExecuted;
    private long _malformedRejected;
    private long _faultsCaught;
    private long _invariantRepairs;
    private int _lastSeed;

    /// <summary>Optional test seam invoked at the end of <see cref="VerifyAndHealAfterProbe"/> before the post-heal verification check.</summary>
    internal Action<Player>? AfterHealHook { get; set; }

    public PlayerbotChaosEngine(PlayerbotOptions options, OpcodeTable opcodes, ILogger logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(opcodes);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _opcodes = opcodes;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _lastSeed = options.Chaos.Seed;
    }

    /// <summary>Total probes dispatched since startup or the last <see cref="ClearStats"/>.</summary>
    public long ProbesExecuted => Interlocked.Read(ref _probesExecuted);

    /// <summary>Probes cleanly rejected by handler <see cref="MalformedPacket"/> guards.</summary>
    public long MalformedRejected => Interlocked.Read(ref _malformedRejected);

    /// <summary>Unhandled handler exceptions or invariant violations caught by chaos probes.</summary>
    public long FaultsCaught => Interlocked.Read(ref _faultsCaught);

    /// <summary>Post-probe state/invariant repairs triggered by chaos probes.</summary>
    public long InvariantRepairs => Interlocked.Read(ref _invariantRepairs);

    /// <summary>Assign a specific bot to run chaos probes whenever chaos mode is enabled.</summary>
    public bool AssignBot(Guid botId) => botId != Guid.Empty && _assignedBots.TryAdd(botId, 1);

    /// <summary>Remove a specific bot's explicit chaos assignment.</summary>
    public bool UnassignBot(Guid botId) => _assignedBots.TryRemove(botId, out _);

    /// <summary>Whether <paramref name="botId"/> is explicitly assigned to chaos mode.</summary>
    public bool IsAssigned(Guid botId) => _assignedBots.ContainsKey(botId);

    /// <summary>Clear explicit bot assignments and reset counters and recent faults.</summary>
    public void ClearStats(bool clearAssignments = false)
    {
        if (clearAssignments) _assignedBots.Clear();
        _postProbeWatches.Clear();
        Interlocked.Exchange(ref _probesExecuted, 0);
        Interlocked.Exchange(ref _malformedRejected, 0);
        Interlocked.Exchange(ref _faultsCaught, 0);
        Interlocked.Exchange(ref _invariantRepairs, 0);
        for (int i = 0; i < _categoryCounts.Length; i++)
            Interlocked.Exchange(ref _categoryCounts[i], 0);
        lock (_faultsGate) _recentFaults.Clear();
    }

    /// <summary>Current engine snapshot for <c>.playerbot chaos status</c> and tests.</summary>
    public ChaosEngineStatus Status()
    {
        PlayerbotChaosOptions cfg = _options.Chaos;
        var byCat = new Dictionary<ChaosProbeCategory, long>(_categoryCounts.Length);
        foreach (ChaosProbeCategory cat in Enum.GetValues<ChaosProbeCategory>())
            byCat[cat] = Interlocked.Read(ref _categoryCounts[(int)cat]);

        ChaosFaultRecord[] faults;
        lock (_faultsGate) faults = [.. _recentFaults];

        string[] assigned = [.. _assignedBots.Keys.OrderBy(id => id).Select(id => id.ToString("D", CultureInfo.InvariantCulture))];
        return new ChaosEngineStatus(
            cfg.Enabled,
            cfg.ActiveChaosBots,
            assigned.Length,
            cfg.ProbesPerThink,
            cfg.Seed,
            cfg.MalformedPayloads,
            cfg.StateRaces,
            cfg.BoundaryMovement,
            ProbesExecuted,
            MalformedRejected,
            FaultsCaught,
            InvariantRepairs,
            byCat,
            faults,
            assigned);
    }

    /// <summary>
    /// True when the bot at index <paramref name="botOrdinal"/> (0-based in deterministic <c>BotId</c> order) should run chaos
    /// probes on this think.
    /// </summary>
    public bool ShouldRunForBot(Guid botId, int botOrdinal)
    {
        PlayerbotChaosOptions cfg = _options.Chaos;
        if (!cfg.Enabled) return false;
        if (_assignedBots.ContainsKey(botId)) return true;
        return botOrdinal >= 0 && botOrdinal < cfg.ActiveChaosBots;
    }

    /// <summary>
    /// True when <paramref name="player"/> is currently aboard an elevator, tram, transport, or taxi flight, or in the middle of a travel leg
    /// or journey (a ship leg, the hearthstone, a mage leg or portal service: <see cref="PlayerbotTravel.IsMidLeg"/>).
    /// </summary>
    public static bool IsTraveling(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return PlayerbotElevator.IsTraveling(player)
            || PlayerbotTram.IsTraveling(player)
            || PlayerbotTravel.IsMidLeg(player)
            || player.Movement.HasFlag(MovementFlags.OnTransport)
            || player.Movement.TransportGuid != 0UL
            || (player.UnitFlags & UnitFlags.TaxiFlight) != 0;
    }

    /// <summary>
    /// Run <see cref="PlayerbotChaosOptions.ProbesPerThink"/> adversarial probes for <paramref name="botId"/> on the world thread.
    /// Returns the number of probes executed.
    /// </summary>
    public int StepBot(Guid botId, int botOrdinal, WorldSession session, Player player)
    {
        ObserveBotProgress(botId, player, session.World.NowMs);
        if (!ShouldRunForBot(botId, botOrdinal)) return 0;
        if (IsTraveling(player))
        {
            return 0;
        }

        int count = Math.Clamp(_options.Chaos.ProbesPerThink, 1, PlayerbotChaosOptions.MaxProbesPerThink);
        return RunProbes(botId, session, player, count);
    }

    /// <summary>
    /// Execute an immediate burst of <paramref name="probesPerBot"/> chaos probes on <paramref name="session"/> / <paramref name="player"/>
    /// (called by <c>.playerbot chaos burst</c> or tests on the world thread).
    /// </summary>
    public int RunBurst(Guid botId, WorldSession session, Player player, int probesPerBot)
    {
        ObserveBotProgress(botId, player, session.World.NowMs);
        int bounded = Math.Clamp(probesPerBot, 1, MaxBurstCount);
        return RunProbes(botId, session, player, bounded);
    }

    /// <summary>
    /// Observe movement progress on <paramref name="player"/> and report <c>bot-chaos/probe-left-bot-stuck</c> once per episode if a bot
    /// whose movement route was active when a chaos probe hit it makes no progress for <see cref="PostProbeStuckTimeoutMs"/> after it.
    /// The episode ends when the bot moves on, or its motion ends or something else takes it over (a fight, death, a flight); progress is
    /// tracked before any probe too, so a bot already stalled when the probe came (<see cref="PreProbeStallMs"/>) does not start one.
    /// GitHub #814: with a watch that never ended, different probes "left" bots stuck at the same spot (1601.0,427.6,-3.1) minutes after them.
    /// </summary>
    public void ObserveBotProgress(Guid botId, Player player, uint nowMs)
    {
        bool moving = IsMovingFreely(player);
        if (!_postProbeWatches.TryGetValue(botId, out PostProbeWatch? watch))
        {
            if (!moving) return;
            watch = _postProbeWatches.GetOrAdd(botId, static _ => new PostProbeWatch());
        }

        if (!moving)
        {
            watch.HasProbed = false;
            watch.WatchingMotion = false;
            watch.Reported = false;
            return;
        }

        if (!watch.WatchingMotion || watch.MapId != player.MapId)
        {
            StartWatching(watch, player, nowMs);
            return;
        }

        float dx = player.X - watch.StartX;
        float dy = player.Y - watch.StartY;
        float dz = player.Z - watch.StartZ;
        float distSq = (dx * dx) + (dy * dy) + (dz * dz);
        if (distSq >= PostProbeMinProgressYards * PostProbeMinProgressYards)
        {
            // The bot moved on: a probe before this did not leave it stuck, and its episode ends here.
            watch.HasProbed = false;
            StartWatching(watch, player, nowMs);
            return;
        }

        if (!watch.HasProbed) return; // a stall no probe preceded is not the chaos engine's to report

        uint elapsedMs = unchecked(nowMs - watch.MotionSinceMs);
        if (!watch.Reported && elapsedMs >= PostProbeStuckTimeoutMs)
        {
            watch.Reported = true;
            PlayerbotAnomalies.Report(
                player,
                "chaos",
                "probe-left-bot-stuck",
                $"bot made no movement progress ({MathF.Sqrt(distSq):F2} yd) for {elapsedMs} ms after chaos probe '{watch.LastProbe}' while motion remained active",
                AnomalySeverity.Warning);
        }
    }

    /// <summary>How long a bot may already have been making no progress when a probe hits it before the stall is counted as its own.</summary>
    public const uint PreProbeStallMs = PostProbeStuckTimeoutMs / 4;

    private static bool IsMovingFreely(Player player)
        => player.IsInWorld && player.Map is not null && player.IsAlive && !IsTraveling(player)
            && !player.Combat.IsInCombat && PlayerbotMotion.IsActive(player);

    private static void StartWatching(PostProbeWatch watch, Player player, uint nowMs)
    {
        watch.WatchingMotion = true;
        watch.MotionSinceMs = nowMs;
        watch.StartX = player.X;
        watch.StartY = player.Y;
        watch.StartZ = player.Z;
        watch.MapId = player.MapId;
        watch.Reported = false;
    }

    /// <summary>Test seam: run one named probe exactly as a burst runs it (budget lifted, verified and healed after), on the world thread.</summary>
    internal void RunProbe(Guid botId, WorldSession session, Player player, ChaosProbeCategory category, string probeName,
        WorldOpcode opcode, byte[] payload)
    {
        ManagedActionBudget? savedBudget = session.ManagedBudget;
        session.ManagedBudget = null;
        try { ExecuteSingleProbe(botId, session, player, category, probeName, opcode, payload, new Random(0)); }
        finally { session.ManagedBudget = savedBudget; }
    }

    private int RunProbes(Guid botId, WorldSession session, Player player, int count)
    {
        if (session.State != SessionState.InWorld || !player.IsInWorld || player.Map is null) return 0;
        if (IsTraveling(player))
        {
            PlayerbotAnomalies.Report(
                player,
                "chaos",
                "probe-on-traveling",
                $"chaos probe requested while bot is traveling (transport={player.Movement.TransportGuid:X}, flags={player.Movement.Flags}, unitFlags={player.UnitFlags})",
                AnomalySeverity.Error);
            return 0;
        }

        PlayerbotChaosOptions cfg = _options.Chaos;
        Span<ChaosProbeCategory> enabledCategories = stackalloc ChaosProbeCategory[15];
        int catCount = 0;
        if (cfg.MalformedPayloads)
        {
            enabledCategories[catCount++] = ChaosProbeCategory.TruncatedWire;
            enabledCategories[catCount++] = ChaosProbeCategory.MutatedWire;
        }
        if (cfg.StateRaces)
        {
            enabledCategories[catCount++] = ChaosProbeCategory.DeathAndResurrection;
            enabledCategories[catCount++] = ChaosProbeCategory.CombatAndSpell;
            enabledCategories[catCount++] = ChaosProbeCategory.InventoryAndBag;
            enabledCategories[catCount++] = ChaosProbeCategory.QuestAndNpc;
            enabledCategories[catCount++] = ChaosProbeCategory.LootVendorEconomy;
            enabledCategories[catCount++] = ChaosProbeCategory.SocialTradeMailBg;
            enabledCategories[catCount++] = ChaosProbeCategory.TalentAndSkill;
            enabledCategories[catCount++] = ChaosProbeCategory.PetAndStable;
            enabledCategories[catCount++] = ChaosProbeCategory.AuctionMailBank;
            enabledCategories[catCount++] = ChaosProbeCategory.WorldTransitAndInstance;
            enabledCategories[catCount++] = ChaosProbeCategory.ChatChannelAndQuery;
            enabledCategories[catCount++] = ChaosProbeCategory.StateRaceCombo;
        }
        if (cfg.BoundaryMovement)
        {
            enabledCategories[catCount++] = ChaosProbeCategory.BoundaryMovement;
        }
        if (catCount == 0) return 0;

        Random rng = GetBotRandom(botId, cfg.Seed);
        ManagedActionBudget? savedBudget = session.ManagedBudget;
        session.ManagedBudget = null;
        int executed = 0;
        int startOffset = rng.Next(catCount);
        string lastProbeName = string.Empty;
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (session.State != SessionState.InWorld || !player.IsInWorld || player.Map is null) break;
                if (IsTraveling(player))
                {
                    PlayerbotAnomalies.Report(
                        player,
                        "chaos",
                        "probe-on-traveling",
                        $"chaos probe started while bot is traveling during burst (transport={player.Movement.TransportGuid:X}, flags={player.Movement.Flags}, unitFlags={player.UnitFlags})",
                        AnomalySeverity.Error);
                    break;
                }

                ChaosProbeCategory category = count >= catCount && i < catCount
                    ? enabledCategories[(startOffset + i) % catCount]
                    : enabledCategories[rng.Next(catCount)];
                (WorldOpcode opcode, string probeName, byte[] payload) = BuildProbe(category, rng, player);
                lastProbeName = $"{category}/{probeName}";
                ExecuteSingleProbe(botId, session, player, category, probeName, opcode, payload, rng);
                executed++;
            }
        }
        finally
        {
            session.ManagedBudget = savedBudget;
        }

        if (executed > 0)
        {
            PostProbeWatch watch = _postProbeWatches.GetOrAdd(botId, static _ => new PostProbeWatch());
            watch.LastProbe = lastProbeName;
            uint now = session.World.NowMs;
            if (!watch.HasProbed && IsMovingFreely(player))
            {
                // A new episode only for a bot that was making progress: one already stalled before the probe was stuck without it.
                bool stalledBefore = watch.WatchingMotion && watch.MapId == player.MapId
                    && unchecked(now - watch.MotionSinceMs) >= PreProbeStallMs;
                if (!stalledBefore)
                {
                    watch.HasProbed = true;
                    StartWatching(watch, player, now);
                }
            }
        }

        return executed;
    }

    private void ExecuteSingleProbe(
        Guid botId,
        WorldSession session,
        Player player,
        ChaosProbeCategory category,
        string probeName,
        WorldOpcode opcode,
        byte[] payload,
        Random rng)
    {
        uint prevMapId = player.MapId;
        Map? prevMap = player.Map;
        float prevX = player.X, prevY = player.Y, prevZ = player.Z, prevO = player.Orientation;
        MovementInfo prevMovement = player.Movement;
        var prevLastSafe = player.LastSafePosition;
        bool prevRecallArmed = player.UndermapRecallArmed;
        StandState prevStandState = player.StandState;
        bool prevAlive = player.IsAlive;
        bool prevMayReclaim = MayReclaimCorpse(player);
        _ = player.ConsumeRejectedNonFiniteRelocation();
        Interlocked.Increment(ref _probesExecuted);
        Interlocked.Increment(ref _categoryCounts[(int)category]);

        bool prevProbe = session.IsInChaosProbe;
        session.IsInChaosProbe = true;
        try
        {
            DispatchProbePacket(session, player, opcode, payload);
            if (category == ChaosProbeCategory.StateRaceCombo)
            {
                ExecuteStateRaceComboFollowUps(session, player, probeName, rng);
            }
        }
        catch (Exception ex) when (MalformedPacket.Is(ex))
        {
            Interlocked.Increment(ref _malformedRejected);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordFault(botId, player.Name, category, probeName, opcode, payload, ex);
        }
        finally
        {
            session.IsInChaosProbe = prevProbe;
        }

        // Verify post-probe state & self-heal any invariant breach.
        VerifyAndHealAfterProbe(
            botId,
            session,
            player,
            category,
            probeName,
            opcode,
            payload,
            prevMapId,
            prevMap,
            prevX,
            prevY,
            prevZ,
            prevO,
            in prevMovement,
            prevLastSafe,
            prevStandState,
            prevAlive,
            prevMayReclaim);

        // The heal restores LastSafePosition; its recall arming goes with it, so a probe neither arms nor spends the under-map recall.
        // Defensive: today a probe cannot change it (MovementHandlers skips the save during a probe, and the probe suppresses the observer).
        player.UndermapRecallArmed = prevRecallArmed;
    }

    private void DispatchProbePacket(WorldSession session, Player player, WorldOpcode opcode, byte[] payload)
    {
        if (IsNonProfessionUnlearnSkillProbe(player, opcode, payload))
        {
            // Guard chaos probes from spamming TryUnlearnSkill warnings on non-profession skills (weapon/defense/armor/language lines).
            return;
        }

        if (session.IsManaged)
        {
            session.TryManagedAction(opcode, payload);
        }
        else if (session.World.IsWorldThread
            && session.State == SessionState.InWorld
            && !player.IsQuestSettlementPending
            && payload.Length <= WorldSession.MaxClientPacketSize - 4
            && _opcodes.TryGet(opcode, out OpcodeHandler? handler)
            && handler.World is not null
            && handler.AllowsState(session.State)
            && (player.Map is not null || opcode == WorldOpcode.MsgMoveWorldportAck))
        {
            handler.World(session, player, payload);
        }
    }

    /// <summary>
    /// True when <paramref name="opcode"/> is <see cref="WorldOpcode.CmsgUnlearnSkill"/> with a complete 4-byte skill id that
    /// is not an unlearnable primary profession skill on <paramref name="player"/> (prevents live soak warning spam on weapon,
    /// defense, armor and class skill lines such as 43/44/54/95/136/162/172/415 while still allowing truncated &lt;4-byte frames
    /// to test <see cref="MalformedPacket"/> guards).
    /// </summary>
    public static bool IsNonProfessionUnlearnSkillProbe(Player player, WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (opcode != WorldOpcode.CmsgUnlearnSkill || payload.Length < sizeof(uint))
        {
            return false;
        }

        uint skillId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return !IsUnlearnablePrimaryProfessionSkill(player, skillId);
    }

    /// <summary>
    /// True only when <paramref name="skillId"/> is a known primary profession skill (<see cref="ArcaneCore.Kernel.Skills.SkillCategories.Profession"/>)
    /// carrying <see cref="ArcaneCore.Kernel.Skills.SkillRaceClassFlags.Unlearnable"/> for <paramref name="player"/>.
    /// </summary>
    public static bool IsUnlearnablePrimaryProfessionSkill(Player player, uint skillId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!IsPrimaryProfessionSkillId(skillId))
        {
            return false;
        }

        if (player.Skills is { } skills)
        {
            if (!skills.Has(skillId))
            {
                return false;
            }

            if (skills.Catalog.Line(skillId) is not { Category: ArcaneCore.Kernel.Skills.SkillCategories.Profession })
            {
                return false;
            }

            return skills.Catalog.RaceClassInfo(skillId, (byte)player.Race, (byte)player.Class) is { } rc
                && (rc.Flags & ArcaneCore.Kernel.Skills.SkillRaceClassFlags.Unlearnable) != 0;
        }

        return false;
    }

    private static bool IsPrimaryProfessionSkillId(uint skillId)
        => skillId is ArcaneCore.Kernel.Skills.SkillIds.Blacksmithing
            or ArcaneCore.Kernel.Skills.SkillIds.Leatherworking
            or ArcaneCore.Kernel.Skills.SkillIds.Alchemy
            or ArcaneCore.Kernel.Skills.SkillIds.Herbalism
            or ArcaneCore.Kernel.Skills.SkillIds.Mining
            or ArcaneCore.Kernel.Skills.SkillIds.Tailoring
            or ArcaneCore.Kernel.Skills.SkillIds.Engineering
            or ArcaneCore.Kernel.Skills.SkillIds.Enchanting
            or ArcaneCore.Kernel.Skills.SkillIds.Skinning;

    private void VerifyAndHealAfterProbe(
        Guid botId,
        WorldSession session,
        Player player,
        ChaosProbeCategory category,
        string probeName,
        WorldOpcode opcode,
        byte[] payload,
        uint prevMapId,
        Map? prevMap,
        float prevX,
        float prevY,
        float prevZ,
        float prevO,
        in MovementInfo prevMovement,
        (uint MapId, float X, float Y, float Z, float O)? prevLastSafe,
        StandState prevStandState,
        bool prevAlive,
        bool prevMayReclaim = false)
    {
        player.LastSafePosition = prevLastSafe;

        // Rescue bots previously persisted near (0, 0, 0) on continents 0/1 by earlier movement fuzzing.
        if (player.MapId is 0 or 1
            && MathF.Abs(prevX) < 100f
            && MathF.Abs(prevY) < 100f
            && player.Home is { } home
            && home.MapId == player.MapId
            && float.IsFinite(home.X) && float.IsFinite(home.Y) && float.IsFinite(home.Z)
            && (MathF.Abs(home.X) > 100f || MathF.Abs(home.Y) > 100f))
        {
            prevX = home.X;
            prevY = home.Y;
            prevZ = home.Z;
        }

        // Check whether deliberately invalid movement probes were accepted vs cleanly rejected before healing MovementInfo.
        const MovementFlags ConflictingForwardBack = MovementFlags.Forward | MovementFlags.Backward;
        const MovementFlags ConflictingStrafe = MovementFlags.StrafeLeft | MovementFlags.StrafeRight;
        bool hadConflictingFlagsBefore = (prevMovement.Flags & ConflictingForwardBack) == ConflictingForwardBack
            || (prevMovement.Flags & ConflictingStrafe) == ConflictingStrafe;
        bool hasConflictingFlagsAfter = (player.Movement.Flags & ConflictingForwardBack) == ConflictingForwardBack
            || (player.Movement.Flags & ConflictingStrafe) == ConflictingStrafe;
        bool acceptedConflictingFlags = !hadConflictingFlagsBefore && hasConflictingFlagsAfter;

        // 1. Coordinate finiteness invariant: a movement or teleport handler must never leave NaN/Infinity on a player
        // or attempt a non-finite relocation that WorldObject/Unit had to reject at the entity boundary.
        bool rejectedNonFinite = player.ConsumeRejectedNonFiniteRelocation();
        bool nonFiniteAfter = rejectedNonFinite
            || !float.IsFinite(player.X)
            || !float.IsFinite(player.Y)
            || !float.IsFinite(player.Z)
            || !float.IsFinite(player.Orientation);
        if (nonFiniteAfter)
        {
            PlayerbotAnomalies.Report(
                player,
                "chaos",
                "server-accepted-bad-input",
                $"server accepted non-finite coordinates on {category}/{probeName} ({opcode})",
                AnomalySeverity.Warning);
            player.Relocate(prevX, prevY, prevZ, float.IsFinite(player.Orientation) ? player.Orientation : prevO, session.World.NowMs);
            _ = player.ConsumeRejectedNonFiniteRelocation();
            Interlocked.Increment(ref _invariantRepairs);
            RecordFault(
                botId,
                player.Name,
                category,
                probeName,
                opcode,
                payload,
                new InvalidOperationException($"Non-finite player position after {opcode} ({probeName}); restored to ({prevX:F1},{prevY:F1},{prevZ:F1})."));
        }
        else
        {
            if (probeName is "move-nan-x-guard-check" or "move-inf-z-guard-check"
                || (probeName == "move-conflicting-flags" && !acceptedConflictingFlags))
            {
                // Expected rejection of deliberate invalid movement input in chaos mode.
                Interlocked.Increment(ref _malformedRejected);
            }

            if (player.X != prevX || player.Y != prevY || player.Z != prevZ || player.Orientation != prevO)
            {
                // Prevent fuzzed movement packets (e.g., all-zero or random-float MSG_MOVE_* frames) from relocating bots
                // to Null Island (0, 0, 0) or through walls/air during chaos probes.
                player.Relocate(prevX, prevY, prevZ, prevO, session.World.NowMs);
            }
        }

        if (acceptedConflictingFlags)
        {
            PlayerbotAnomalies.Report(
                player,
                "chaos",
                "server-accepted-bad-input",
                $"server accepted conflicting movement flags ({player.Movement.Flags}) on {category}/{probeName} ({opcode})",
                AnomalySeverity.Warning);
        }

        if (ReferenceEquals(player.Combat.Victim, player))
        {
            PlayerbotAnomalies.Report(
                player,
                "chaos",
                "server-accepted-bad-input",
                $"server accepted self-attack (Victim == self) on {category}/{probeName} ({opcode})",
                AnomalySeverity.Warning);
        }

        // CMSG_RECLAIM_CORPSE carries a corpse GUID the server never reads (vmangos HandleReclaimCorpseOpcode; CombatHandlers): a whole
        // packet, whatever GUID it names (the bot's own, a bogus one), from a ghost the reclaim rules allowed is a legitimate reclaim,
        // not bad input (GitHub #785, and the heal-mismatch it caused, #783). A truncated packet is still bad input: vmangos throws on
        // the short read and never reaches the handler.
        bool legitimateReclaim = !prevAlive && player.IsAlive && prevMayReclaim
            && opcode == WorldOpcode.CmsgReclaimCorpse && payload.Length >= sizeof(ulong);
        if (prevAlive != player.IsAlive && !legitimateReclaim
            && (category == ChaosProbeCategory.DeathAndResurrection || opcode is WorldOpcode.CmsgRepopRequest or WorldOpcode.CmsgSelfRes or WorldOpcode.CmsgReclaimCorpse or WorldOpcode.CmsgResurrectResponse))
        {
            PlayerbotAnomalies.Report(
                player,
                "chaos",
                "server-accepted-bad-input",
                $"server accepted out-of-state life transition (alive {prevAlive} -> {player.IsAlive}) on {category}/{probeName} ({opcode})",
                AnomalySeverity.Warning);
        }

        // Restore pre-probe MovementInfo flags & transport binding if a fuzzed MSG_MOVE_* packet or Relocate stripped them
        // (Unit.Relocate clears OnTransport and Forward, which otherwise aborts active PlayerbotMotion routes or drops bots off elevators).
        if (player.Movement.Flags != prevMovement.Flags || player.Movement.TransportGuid != prevMovement.TransportGuid)
        {
            MovementInfo restored = prevMovement;
            restored.X = player.X;
            restored.Y = player.Y;
            restored.Z = player.Z;
            restored.Orientation = float.IsFinite(player.Orientation) ? player.Orientation : 0f;
            player.ApplyMovement(restored, session.World.NowMs);
        }

        // Cancel any logout timer started by a fuzzed CMSG_LOGOUT_REQUEST so the chaos bot remains in the world.
        if (player.IsLoggingOut)
        {
            player.CancelLogout();
        }

        // Restore StandState if a fuzzed packet put a living bot into Dead/Sit/Sleep/Submerged.
        if (player.IsAlive && player.StandState != prevStandState && player.StandState != StandState.Stand)
        {
            player.StandState = StandState.Stand;
        }

        // Clear orphaned OnTransport flag if no transport is attached.
        if (player.Movement.TransportGuid == 0UL && player.Movement.HasFlag(MovementFlags.OnTransport))
        {
            player.RemoveMovementFlags(MovementFlags.OnTransport);
        }

        // 2. Life, combat and visibility invariants.
        if (player.Map is { } map)
        {
            bool lifeFixed = map.Combat.VerifyPlayerLifeInvariants(player);
            bool combatFixed = map.Combat.VerifyUnitCombatInvariants(player);
            bool visFixed = map.VerifyVisibleSetInvariants(player);
            if (!prevAlive && combatFixed)
            {
                PlayerbotAnomalies.Report(
                    player,
                    "chaos",
                    "server-accepted-bad-input",
                    $"server accepted combat/cast state while dead on {category}/{probeName} ({opcode})",
                    AnomalySeverity.Warning);
            }
            if (lifeFixed || combatFixed || visFixed)
            {
                Interlocked.Increment(ref _invariantRepairs);
            }
        }

        AfterHealHook?.Invoke(player);

        // 3. Verify that post-probe healing restored all pre-probe invariants.
        MovementFlags expectedFlags = prevMovement.TransportGuid == 0UL
            ? (prevMovement.Flags & ~MovementFlags.OnTransport)
            : prevMovement.Flags;
        bool posMismatch = !float.IsFinite(player.X)
            || !float.IsFinite(player.Y)
            || !float.IsFinite(player.Z)
            || !float.IsFinite(player.Orientation)
            || MathF.Abs(player.X - prevX) > 1e-3f
            || MathF.Abs(player.Y - prevY) > 1e-3f
            || MathF.Abs(player.Z - prevZ) > 1e-3f
            || MathF.Abs(player.Orientation - prevO) > 1e-3f;
        bool mapMismatch = player.MapId != prevMapId || !ReferenceEquals(player.Map, prevMap);
        bool moveMismatch = player.Movement.Flags != expectedFlags || player.Movement.TransportGuid != prevMovement.TransportGuid;
        bool safeMismatch = player.LastSafePosition != prevLastSafe;
        bool lifeMismatch = player.IsAlive != prevAlive && !legitimateReclaim;
        if (posMismatch || mapMismatch || moveMismatch || safeMismatch || lifeMismatch || player.IsLoggingOut)
        {
            PlayerbotAnomalies.Report(
                player,
                "chaos",
                "heal-mismatch",
                $"post-probe heal mismatch after {category}/{probeName} ({opcode}): posMismatch={posMismatch}, mapMismatch={mapMismatch} ({prevMapId}->{player.MapId}), moveMismatch={moveMismatch} ({expectedFlags}->{player.Movement.Flags}), safeMismatch={safeMismatch}, lifeMismatch={lifeMismatch} ({prevAlive}->{player.IsAlive}), loggingOut={player.IsLoggingOut}",
                AnomalySeverity.Error);
        }
    }

    /// <summary>
    /// Whether <paramref name="player"/> may reclaim its corpse right now by the server's rules (<see cref="MapCombat.TryReclaimCorpse"/>,
    /// vmangos HandleReclaimCorpseOpcode): a ghost with a corpse in its map whose reclaim delay has passed, strictly within
    /// <see cref="CombatConstants.CorpseReclaimRadius"/> plus both bounding radii (3D). Read before a probe: a reclaim removes the corpse.
    /// </summary>
    internal static bool MayReclaimCorpse(Player player)
    {
        if (player.IsAlive || (player.Flags & PlayerFlags.Ghost) == 0 || player.Combat.Corpse is not { } corpse
            || player.Map is not { } map || !ReferenceEquals(corpse.Map, map) || map.Combat.CorpseReclaimWaitSeconds(player) > 0)
            return false;
        float dx = corpse.X - player.X, dy = corpse.Y - player.Y, dz = corpse.Z - player.Z;
        float max = CombatConstants.CorpseReclaimRadius + corpse.BoundingRadius + player.BoundingRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) < max * max;
    }

    private sealed class PostProbeWatch
    {
        public bool HasProbed;
        public bool WatchingMotion;
        public uint MotionSinceMs;
        public float StartX;
        public float StartY;
        public float StartZ;
        public uint MapId;
        public string LastProbe = string.Empty;
        public bool Reported;
    }

    private void RecordFault(
        Guid botId,
        string botName,
        ChaosProbeCategory category,
        string probeName,
        WorldOpcode opcode,
        byte[] payload,
        Exception ex)
    {
        Interlocked.Increment(ref _faultsCaught);
        FaultLedger.Shared.Record("playerbot.chaos", ex);

        string hex = Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 32)));
        if (payload.Length > 32) hex += "...";

        var record = new ChaosFaultRecord(
            _time.GetUtcNow(),
            botId,
            botName,
            category,
            probeName,
            opcode,
            payload.Length,
            hex,
            ex.GetType().Name,
            ex.Message);

        lock (_faultsGate)
        {
            if (_recentFaults.Count >= MaxRecentFaults) _recentFaults.RemoveAt(0);
            _recentFaults.Add(record);
        }

        // Matches PlayerbotQaSignalParser.FaultPattern ("^Playerbot (?<bot>\S+) (?<what>[A-Za-z]{1,32}) failed \((?<type>...): (?<msg>...)\)")
        // so Bot QA automatically captures, deduplicates and ranks chaos faults under what="chaos".
        _logger.LogError(
            ex,
            "Playerbot {BotId} chaos failed ({Type}: {Message})",
            botId,
            ex.GetType().Name,
            $"{opcode} [{category}/{probeName} len={payload.Length} hex={hex}]: {ex.Message}");
    }

    private Random GetBotRandom(Guid botId, int configuredSeed)
    {
        if (configuredSeed != Volatile.Read(ref _lastSeed))
        {
            Volatile.Write(ref _lastSeed, configuredSeed);
            _botRandoms.Clear();
        }

        return _botRandoms.GetOrAdd(botId, id =>
        {
            int hash = id.GetHashCode();
            int seed = configuredSeed != 0
                ? unchecked(configuredSeed * 397 ^ hash)
                : unchecked(Environment.TickCount ^ hash);
            return new Random(seed);
        });
    }

    private WorldOpcode[] GetWorldOpcodes()
    {
        if (_worldOpcodes is { Length: > 0 } cached) return cached;
        var list = new List<WorldOpcode>(192);
        foreach (WorldOpcode op in _opcodes.Opcodes)
        {
            if (op is WorldOpcode.CmsgGmticketCreate or WorldOpcode.CmsgGuildCreate or WorldOpcode.CmsgCharDelete or WorldOpcode.CmsgLogoutRequest)
                continue;
            if (_opcodes.TryGet(op, out OpcodeHandler? handler) && handler.World is not null)
                list.Add(op);
        }
        list.Sort();
        _worldOpcodes = [.. list];
        return _worldOpcodes;
    }

    private (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildProbe(ChaosProbeCategory category, Random rng, Player player)
        => category switch
        {
            ChaosProbeCategory.TruncatedWire => BuildTruncatedWireProbe(rng),
            ChaosProbeCategory.MutatedWire => BuildMutatedWireProbe(rng, player),
            ChaosProbeCategory.DeathAndResurrection => BuildDeathAndResurrectionProbe(rng, player),
            ChaosProbeCategory.CombatAndSpell => BuildCombatAndSpellProbe(rng, player),
            ChaosProbeCategory.InventoryAndBag => BuildInventoryAndBagProbe(rng, player),
            ChaosProbeCategory.QuestAndNpc => BuildQuestAndNpcProbe(rng, player),
            ChaosProbeCategory.LootVendorEconomy => BuildLootVendorEconomyProbe(rng, player),
            ChaosProbeCategory.SocialTradeMailBg => BuildSocialTradeMailBgProbe(rng, player),
            ChaosProbeCategory.BoundaryMovement => BuildBoundaryMovementProbe(rng, player),
            ChaosProbeCategory.TalentAndSkill => BuildTalentAndSkillProbe(rng, player),
            ChaosProbeCategory.PetAndStable => BuildPetAndStableProbe(rng, player),
            ChaosProbeCategory.AuctionMailBank => BuildAuctionMailBankProbe(rng, player),
            ChaosProbeCategory.WorldTransitAndInstance => BuildWorldTransitAndInstanceProbe(rng, player),
            ChaosProbeCategory.ChatChannelAndQuery => BuildChatChannelAndQueryProbe(rng, player),
            ChaosProbeCategory.StateRaceCombo => BuildStateRaceComboProbe(rng, player),
            _ => BuildTruncatedWireProbe(rng),
        };

    private (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildTruncatedWireProbe(Random rng)
    {
        WorldOpcode[] ops = GetWorldOpcodes();
        WorldOpcode op = ops.Length > 0 ? ops[rng.Next(ops.Length)] : WorldOpcode.CmsgPing;
        int len = rng.Next(6) switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 5,
            _ => 7,
        };
        byte[] payload = len == 0 ? [] : new byte[len];
        if (len > 0) rng.NextBytes(payload);
        return (op, $"truncated-{len}b", payload);
    }

    private (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildMutatedWireProbe(Random rng, Player player)
    {
        WorldOpcode[] ops = GetWorldOpcodes();
        WorldOpcode op = ops.Length > 0 ? ops[rng.Next(ops.Length)] : WorldOpcode.CmsgAttackswing;
        int variant = rng.Next(5);
        switch (variant)
        {
            case 0:
            {
                // All 0xFF bytes (max integers, negative signed integers, un-terminated C-strings).
                int len = rng.Next(4, 48);
                byte[] buf = new byte[len];
                Array.Fill(buf, (byte)0xFF);
                return (op, $"all-ff-{len}b", buf);
            }
            case 1:
            {
                // All 0x00 bytes (zero GUIDs, empty C-strings, zero counts).
                int len = rng.Next(8, 64);
                return (op, $"all-zero-{len}b", new byte[len]);
            }
            case 2:
            {
                // Self GUID followed by boundary values (0xFFFFFFFF, 0x80000000, random bytes).
                var w = new PacketWriter();
                w.WriteUInt64(player.Guid.Value);
                w.WriteUInt32(uint.MaxValue);
                w.WriteInt32(int.MinValue);
                w.WriteUInt16(ushort.MaxValue);
                w.WriteByte(byte.MaxValue);
                return (op, "self-guid-boundary-ints", w.ToArray());
            }
            case 3:
            {
                // Nearby unit GUID (or bogus high-bit GUID) + random tail.
                ulong guid = PickCandidateGuid(rng, player);
                var w = new PacketWriter();
                w.WriteUInt64(guid);
                Span<byte> tail = stackalloc byte[16];
                rng.NextBytes(tail);
                foreach (byte b in tail) w.WriteByte(b);
                return (op, "candidate-guid-random-tail", w.ToArray());
            }
            default:
            {
                // Oversized random frame with embedded null terminators.
                int len = rng.Next(64, 192);
                byte[] buf = new byte[len];
                rng.NextBytes(buf);
                buf[len / 2] = 0;
                buf[len - 1] = 0;
                return (op, $"random-frame-{len}b", buf);
            }
        }
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildDeathAndResurrectionProbe(Random rng, Player player)
    {
        ulong candidateGuid = PickCandidateGuid(rng, player);
        return rng.Next(8) switch
        {
            0 => (WorldOpcode.CmsgRepopRequest, "repop-while-in-current-life-state", [0]),
            1 => (WorldOpcode.CmsgRepopRequest, "repop-empty-payload", []),
            2 => (WorldOpcode.CmsgReclaimCorpse, "reclaim-corpse-self-guid", GuidPayload(player.Guid.Value)),
            3 => (WorldOpcode.CmsgReclaimCorpse, "reclaim-corpse-bogus-guid", GuidPayload(candidateGuid)),
            4 => (WorldOpcode.CmsgResurrectResponse, "resurrect-accept-unsolicited", GuidAndBytePayload(candidateGuid, 1)),
            5 => (WorldOpcode.CmsgResurrectResponse, "resurrect-decline-self", GuidAndBytePayload(player.Guid.Value, 0)),
            6 => (WorldOpcode.CmsgSpiritHealerActivate, "spirit-healer-activate-while-alive-or-bogus", GuidPayload(candidateGuid)),
            _ => (WorldOpcode.CmsgSelfRes, "self-res-out-of-state", []),
        };
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildCombatAndSpellProbe(Random rng, Player player)
    {
        ulong targetGuid = PickCandidateGuid(rng, player);
        return rng.Next(12) switch
        {
            0 => (WorldOpcode.CmsgAttackswing, "attackswing-self", GuidPayload(player.Guid.Value)),
            1 => (WorldOpcode.CmsgAttackswing, "attackswing-zero-guid", GuidPayload(0UL)),
            2 => (WorldOpcode.CmsgAttackswing, "attackswing-max-guid", GuidPayload(ulong.MaxValue)),
            3 => (WorldOpcode.CmsgAttackstop, "attackstop-rapid", []),
            4 => (WorldOpcode.CmsgSetSelection, "set-selection-bogus", GuidPayload(targetGuid)),
            5 => (WorldOpcode.CmsgCastSpell, "cast-spell-zero", CastSpellPayload(0u, 0x00, 0UL)),
            6 => (WorldOpcode.CmsgCastSpell, "cast-spell-max-uint", CastSpellPayload(uint.MaxValue, 0x02, targetGuid)),
            7 => (WorldOpcode.CmsgCastSpell, "cast-spell-self-attack", CastSpellPayload(player.Class == Class.Mage ? 133u : 6603u, 0x02, player.Guid.Value)),
            8 => (WorldOpcode.CmsgCastSpell, "cast-spell-unknown-target-mask", CastSpellPayload(6603u, 0xFFFF, targetGuid)),
            9 => (WorldOpcode.CmsgCancelCast, "cancel-cast-bogus-id", UInt32Payload((uint)rng.Next(0, 30000))),
            10 => (WorldOpcode.CmsgCancelAura, "cancel-aura-bogus-id", UInt32Payload((uint)rng.Next(0, 30000))),
            _ => (WorldOpcode.CmsgCancelChannelling, "cancel-channelling-bogus", UInt32Payload(uint.MaxValue)),
        };
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildInventoryAndBagProbe(Random rng, Player player)
    {
        byte bagA = PickBoundarySlot(rng);
        byte slotA = PickBoundarySlot(rng);
        byte bagB = PickBoundarySlot(rng);
        byte slotB = PickBoundarySlot(rng);

        return rng.Next(8) switch
        {
            0 => (WorldOpcode.CmsgSwapInvItem, "swap-inv-self-slot", [slotA, slotA]),
            1 => (WorldOpcode.CmsgSwapInvItem, "swap-inv-boundary-slots", [slotA, slotB]),
            2 => (WorldOpcode.CmsgSwapItem, "swap-item-boundary-bags", [bagA, slotA, bagB, slotB]),
            3 => (WorldOpcode.CmsgAutoequipItem, "autoequip-boundary-slot", [bagA, slotA]),
            4 => (WorldOpcode.CmsgDestroyitem, "destroy-item-boundary", [bagA, slotA, (byte)rng.Next(0, 256), 0, 0, 0]),
            5 => (WorldOpcode.CmsgSplitItem, "split-item-boundary-count", [bagA, slotA, bagB, slotB, (byte)rng.Next(0, 256)]),
            6 => (WorldOpcode.CmsgUseItem, "use-item-boundary-slot", UseItemPayload(bagA, slotA, PickCandidateGuid(rng, player))),
            _ => (WorldOpcode.CmsgReadItem, "read-item-boundary-slot", [bagA, slotA]),
        };
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildQuestAndNpcProbe(Random rng, Player player)
    {
        ulong npcGuid = PickCandidateGuid(rng, player);
        uint questId = rng.Next(4) switch
        {
            0 => 0u,
            1 => uint.MaxValue,
            2 => 5441u,
            _ => (uint)rng.Next(1, 9500),
        };

        return rng.Next(10) switch
        {
            0 => (WorldOpcode.CmsgGossipHello, "gossip-hello-candidate", GuidPayload(npcGuid)),
            1 => (WorldOpcode.CmsgGossipSelectOption, "gossip-select-out-of-range", GossipSelectPayload(npcGuid, (uint)rng.Next(0, 255), "chaos")),
            2 => (WorldOpcode.CmsgQuestgiverStatusQuery, "questgiver-status-candidate", GuidPayload(npcGuid)),
            3 => (WorldOpcode.CmsgQuestgiverHello, "questgiver-hello-candidate", GuidPayload(npcGuid)),
            4 => (WorldOpcode.CmsgQuestgiverAcceptQuest, "questgiver-accept-unoffered", QuestNpcPayload(npcGuid, questId)),
            5 => (WorldOpcode.CmsgQuestgiverCompleteQuest, "questgiver-complete-incomplete", QuestNpcPayload(npcGuid, questId)),
            6 => (WorldOpcode.CmsgQuestgiverChooseReward, "questgiver-choose-bogus-reward", QuestRewardPayload(npcGuid, questId, (uint)rng.Next(0, 100))),
            7 => (WorldOpcode.CmsgQuestlogRemoveQuest, "questlog-remove-boundary-slot", [(byte)rng.Next(0, 256)]),
            8 => (WorldOpcode.CmsgTrainerList, "trainer-list-candidate", GuidPayload(npcGuid)),
            _ => (WorldOpcode.CmsgBinderActivate, "binder-activate-candidate", GuidPayload(npcGuid)),
        };
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildLootVendorEconomyProbe(Random rng, Player player)
    {
        ulong guid = PickCandidateGuid(rng, player);
        return rng.Next(9) switch
        {
            0 => (WorldOpcode.CmsgLoot, "loot-self-or-candidate", GuidPayload(guid)),
            1 => (WorldOpcode.CmsgLootMoney, "loot-money-without-open-loot", []),
            2 => (WorldOpcode.CmsgAutostoreLootItem, "autostore-loot-slot-boundary", [(byte)rng.Next(0, 256)]),
            3 => (WorldOpcode.CmsgLootRelease, "loot-release-double-or-bogus", GuidPayload(guid)),
            4 => (WorldOpcode.CmsgListInventory, "vendor-list-candidate", GuidPayload(guid)),
            5 => (WorldOpcode.CmsgBuyItem, "vendor-buy-boundary-qty", BuyItemPayload(guid, (uint)rng.Next(0, 30000), (byte)rng.Next(0, 256), (byte)rng.Next(0, 256))),
            6 => (WorldOpcode.CmsgSellItem, "vendor-sell-self-or-bogus", SellItemPayload(guid, PickCandidateGuid(rng, player), (byte)rng.Next(0, 256))),
            7 => (WorldOpcode.CmsgRepairItem, "repair-item-bogus-vendor", RepairPayload(guid, PickCandidateGuid(rng, player))),
            _ => (WorldOpcode.CmsgGetMailList, "mail-list-bogus-mailbox", GuidPayload(guid)),
        };
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildSocialTradeMailBgProbe(Random rng, Player player)
    {
        ulong guid = PickCandidateGuid(rng, player);
        return rng.Next(10) switch
        {
            0 => (WorldOpcode.CmsgInitiateTrade, "trade-initiate-self-or-bogus", GuidPayload(guid)),
            1 => (WorldOpcode.CmsgAcceptTrade, "trade-accept-without-session", UInt32Payload(1u)),
            2 => (WorldOpcode.CmsgCancelTrade, "trade-cancel-unsolicited", []),
            3 => (WorldOpcode.CmsgSetTradeGold, "trade-set-max-gold", UInt32Payload(uint.MaxValue)),
            4 => (WorldOpcode.CmsgDuelAccepted, "duel-accept-without-arbiter", GuidPayload(guid)),
            5 => (WorldOpcode.CmsgDuelCancelled, "duel-cancel-without-duel", GuidPayload(guid)),
            6 => (WorldOpcode.CmsgGroupInvite, "group-invite-self-or-long-name", CStringPayload(rng.Next(2) == 0 ? player.Name : new string('Z', 80))),
            7 => (WorldOpcode.CmsgGroupDisband, "group-disband-unsolicited", []),
            8 => (WorldOpcode.CmsgBattlefieldStatus, "bg-status-query", []),
            _ => (WorldOpcode.CmsgLeaveBattlefield, "bg-leave-outside-bg", [0, 0, 0, 0]),
        };
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildBoundaryMovementProbe(Random rng, Player player)
    {
        int variant = rng.Next(6);
        switch (variant)
        {
            case 0:
            {
                // Valid coordinates, conflicting MovementFlags (Forward + Backward + StrafeLeft + StrafeRight + Jumping + Swimming).
                uint flags = (uint)(MovementFlags.Forward | MovementFlags.Backward | MovementFlags.StrafeLeft | MovementFlags.StrafeRight | MovementFlags.Jumping | MovementFlags.Swimming);
                byte[] payload = BuildMovementPayload(flags, (uint)Environment.TickCount, player.X, player.Y, player.Z, player.Orientation, 0u);
                return (WorldOpcode.MsgMoveHeartbeat, "move-conflicting-flags", payload);
            }
            case 1:
            {
                // Extreme orientation (float.MaxValue or negative large radians) with valid X/Y/Z.
                float weirdO = rng.Next(2) == 0 ? 1e20f : -1e20f;
                byte[] payload = BuildMovementPayload((uint)MovementFlags.None, (uint)Environment.TickCount, player.X, player.Y, player.Z, weirdO, 0u);
                return (WorldOpcode.MsgMoveSetFacing, "move-extreme-orientation", payload);
            }
            case 2:
            {
                // NaN coordinate injection test: verifies MovementHandlers rejects non-finite coordinates cleanly.
                byte[] payload = BuildMovementPayload((uint)MovementFlags.Forward, (uint)Environment.TickCount, float.NaN, player.Y, player.Z, player.Orientation, 0u);
                return (WorldOpcode.MsgMoveStartForward, "move-nan-x-guard-check", payload);
            }
            case 3:
            {
                // PositiveInfinity coordinate injection test.
                byte[] payload = BuildMovementPayload((uint)MovementFlags.Forward, (uint)Environment.TickCount, player.X, player.Y, float.PositiveInfinity, player.Orientation, 0u);
                return (WorldOpcode.MsgMoveHeartbeat, "move-inf-z-guard-check", payload);
            }
            case 4:
            {
                // CMSG_MOVE_TIME_SKIPPED with self GUID and uint.MaxValue lag.
                var w = new PacketWriter();
                w.WriteUInt64(player.Guid.Value);
                w.WriteUInt32(uint.MaxValue);
                return (WorldOpcode.CmsgMoveTimeSkipped, "move-time-skipped-max-uint", w.ToArray());
            }
            default:
            {
                // Jump + fall reset with huge fall time.
                byte[] payload = BuildMovementPayload((uint)MovementFlags.FallingFar, (uint)Environment.TickCount, player.X, player.Y, player.Z, player.Orientation, uint.MaxValue);
                return (WorldOpcode.CmsgMoveFallReset, "move-fall-reset-max-falltime", payload);
            }
        }
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildTalentAndSkillProbe(Random rng, Player player)
    {
        int sub = rng.Next(7);
        switch (sub)
        {
            case 0:
            {
                var w = new PacketWriter();
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                w.WriteUInt32((uint)rng.Next(0, 10));
                return (WorldOpcode.CmsgLearnTalent, "talent-learn-boundary-id", w.ToArray());
            }
            case 1:
            {
                var w = new PacketWriter();
                w.WriteUInt32((uint)rng.Next(1, 2000));
                w.WriteUInt32(uint.MaxValue);
                return (WorldOpcode.CmsgLearnTalent, "talent-learn-max-rank", w.ToArray());
            }
            case 2:
            {
                var w = new PacketWriter();
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                return (WorldOpcode.MsgTalentWipeConfirm, "talent-wipe-confirm-boundary", w.ToArray());
            }
            case 3:
            {
                var w = new PacketWriter();
                uint[] primaryProfessions = [164u, 165u, 171u, 182u, 186u, 197u, 202u, 333u, 393u, 0u, uint.MaxValue];
                w.WriteUInt32(primaryProfessions[rng.Next(primaryProfessions.Length)]);
                return (WorldOpcode.CmsgUnlearnSkill, "skill-unlearn-boundary", w.ToArray());
            }
            case 4:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgTrainerList, "trainer-list-candidate", w.ToArray());
            }
            case 5:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                return (WorldOpcode.CmsgTrainerBuySpell, "trainer-buy-boundary-spell", w.ToArray());
            }
            default:
            {
                var w = new PacketWriter();
                w.WriteUInt32((uint)rng.Next(0, 512));
                w.WriteByte((byte)rng.Next(0, 3));
                w.WriteUInt16((ushort)rng.Next(0, 65535));
                return (WorldOpcode.CmsgSetActionButton, "action-button-fuzz", w.ToArray());
            }
        }
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildPetAndStableProbe(Random rng, Player player)
    {
        int sub = rng.Next(7);
        switch (sub)
        {
            case 0:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(unchecked((uint)rng.Next()));
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgPetAction, "pet-action-fuzz", w.ToArray());
            }
            case 1:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteCString(rng.Next(2) == 0 ? "" : "ChaosPet\u0001\u007F");
                return (WorldOpcode.CmsgPetRename, "pet-rename-control-chars", w.ToArray());
            }
            case 2:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgPetAbandon, "pet-abandon-candidate", w.ToArray());
            }
            case 3:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                w.WriteUInt16((ushort)rng.Next(0, 65535));
                return (WorldOpcode.CmsgPetSetAction, "pet-set-action-boundary", w.ToArray());
            }
            case 4:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.MsgListStabledPets, "stable-list-candidate", w.ToArray());
            }
            case 5:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteByte((byte)rng.Next(0, 255));
                return (WorldOpcode.CmsgStablePet, "stable-pet-boundary-slot", w.ToArray());
            }
            default:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(unchecked((uint)rng.Next()));
                w.WriteByte((byte)rng.Next(0, 255));
                return (WorldOpcode.CmsgUnstablePet, "unstable-pet-boundary-slot", w.ToArray());
            }
        }
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildAuctionMailBankProbe(Random rng, Player player)
    {
        int sub = rng.Next(8);
        switch (sub)
        {
            case 0:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                return (WorldOpcode.CmsgAuctionSellItem, "auction-sell-extreme-prices", w.ToArray());
            }
            case 1:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(unchecked((uint)rng.Next()));
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                return (WorldOpcode.CmsgAuctionPlaceBid, "auction-bid-boundary-amount", w.ToArray());
            }
            case 2:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(uint.MaxValue);
                w.WriteCString("%_%'");
                w.WriteByte(255);
                w.WriteByte(255);
                w.WriteUInt32(uint.MaxValue);
                w.WriteUInt32(uint.MaxValue);
                w.WriteUInt32(uint.MaxValue);
                w.WriteByte(1);
                return (WorldOpcode.CmsgAuctionListItems, "auction-list-max-offset", w.ToArray());
            }
            case 3:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteCString(rng.Next(2) == 0 ? player.Name : "NonExistentRecipient_Chaos");
                w.WriteCString("ChaosSubject");
                w.WriteCString("ChaosBody");
                w.WriteUInt32(0u);
                w.WriteUInt32(0u);
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(uint.MaxValue);
                w.WriteUInt32(uint.MaxValue);
                return (WorldOpcode.CmsgSendMail, "mail-send-max-money-cod", w.ToArray());
            }
            case 4:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                return (WorldOpcode.CmsgMailTakeMoney, "mail-take-money-boundary-id", w.ToArray());
            }
            case 5:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                return (WorldOpcode.CmsgMailTakeItem, "mail-take-item-boundary-id", w.ToArray());
            }
            case 6:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgBuyBankSlot, "bank-buy-slot-candidate", w.ToArray());
            }
            default:
            {
                var w = new PacketWriter();
                w.WriteByte(PickBoundarySlot(rng));
                w.WriteByte(PickBoundarySlot(rng));
                return (WorldOpcode.CmsgAutobankItem, "bank-autobank-boundary-slot", w.ToArray());
            }
        }
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildWorldTransitAndInstanceProbe(Random rng, Player player)
    {
        int sub = rng.Next(7);
        switch (sub)
        {
            case 0:
            {
                var w = new PacketWriter();
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                return (WorldOpcode.CmsgAreatrigger, "areatrigger-boundary-id", w.ToArray());
            }
            case 1:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                return (WorldOpcode.CmsgActivatetaxi, "taxi-activate-boundary-nodes", w.ToArray());
            }
            case 2:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(uint.MaxValue);
                w.WriteUInt32((uint)rng.Next(0, 500));
                for (int i = 0; i < 64; i++)
                {
                    w.WriteUInt32((uint)rng.Next(0, 1000));
                }
                return (WorldOpcode.CmsgActivatetaxiexpress, "taxi-express-oversized-path", w.ToArray());
            }
            case 3:
            {
                return (WorldOpcode.CmsgResetInstances, "instance-reset-spam", []);
            }
            case 4:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                w.WriteUInt32(uint.MaxValue);
                w.WriteByte((byte)rng.Next(0, 255));
                return (WorldOpcode.CmsgBattlefieldPort, "battlefield-port-invalid-slot", w.ToArray());
            }
            case 5:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgBinderActivate, "binder-activate-candidate", w.ToArray());
            }
            default:
            {
                return (WorldOpcode.CmsgRepopRequest, "repop-while-alive-or-ghost", []);
            }
        }
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildChatChannelAndQueryProbe(Random rng, Player player)
    {
        int sub = rng.Next(7);
        switch (sub)
        {
            case 0:
            {
                var w = new PacketWriter();
                w.WriteCString(rng.Next(2) == 0 ? "" : "ChaosChannel\u0007/\\..");
                w.WriteCString("pass\u0000word");
                return (WorldOpcode.CmsgJoinChannel, "channel-join-weird-name", w.ToArray());
            }
            case 1:
            {
                var w = new PacketWriter();
                w.WriteCString("ChaosChannel");
                return (WorldOpcode.CmsgLeaveChannel, "channel-leave-unjoined", w.ToArray());
            }
            case 2:
            {
                var w = new PacketWriter();
                w.WriteUInt32(uint.MaxValue);
                w.WriteUInt32(uint.MaxValue);
                w.WriteUInt32(uint.MaxValue);
                w.WriteUInt32(uint.MaxValue);
                w.WriteUInt32(64u);
                w.WriteUInt32(64u);
                for (int i = 0; i < 64; i++)
                {
                    w.WriteUInt32((uint)rng.Next());
                }
                w.WriteUInt32(10u);
                for (int i = 0; i < 10; i++)
                {
                    w.WriteCString($"ChaosWho_{i}");
                }
                return (WorldOpcode.CmsgWho, "who-query-oversized-filters", w.ToArray());
            }
            case 3:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgNameQuery, "query-name-candidate-guid", w.ToArray());
            }
            case 4:
            {
                var w = new PacketWriter();
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgCreatureQuery, "query-creature-boundary-entry", w.ToArray());
            }
            case 5:
            {
                var w = new PacketWriter();
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgGameobjectQuery, "query-gobj-boundary-entry", w.ToArray());
            }
            default:
            {
                var w = new PacketWriter();
                w.WriteUInt32(rng.Next(2) == 0 ? 0u : uint.MaxValue);
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgItemQuerySingle, "query-item-boundary-entry", w.ToArray());
            }
        }
    }

    private static (WorldOpcode Opcode, string ProbeName, byte[] Payload) BuildStateRaceComboProbe(Random rng, Player player)
    {
        int sub = rng.Next(4);
        switch (sub)
        {
            case 0:
            {
                var w = new PacketWriter();
                w.WriteUInt64(player.Guid.Value);
                return (WorldOpcode.CmsgInitiateTrade, "combo-trade-swap-cancel", w.ToArray());
            }
            case 1:
            {
                return (WorldOpcode.CmsgCastSpell, "combo-cast-cancel-standstate", CastSpellPayload(133u, 0x02, player.Guid.Value));
            }
            case 2:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgLoot, "combo-loot-take-release", w.ToArray());
            }
            default:
            {
                var w = new PacketWriter();
                w.WriteUInt64(PickCandidateGuid(rng, player));
                return (WorldOpcode.CmsgGossipHello, "combo-npc-gossip-vendor-quest", w.ToArray());
            }
        }
    }

    private void ExecuteStateRaceComboFollowUps(WorldSession session, Player player, string probeName, Random rng)
    {
        switch (probeName)
        {
            case "combo-trade-swap-cancel":
            {
                var setItem = new PacketWriter();
                setItem.WriteByte(0);
                setItem.WriteByte(255);
                setItem.WriteByte(23);
                DispatchFollowUp(session, player, WorldOpcode.CmsgSetTradeItem, setItem.ToArray());

                var setGold = new PacketWriter();
                setGold.WriteUInt32(uint.MaxValue);
                DispatchFollowUp(session, player, WorldOpcode.CmsgSetTradeGold, setGold.ToArray());

                DispatchFollowUp(session, player, WorldOpcode.CmsgAcceptTrade, [1, 0, 0, 0]);
                DispatchFollowUp(session, player, WorldOpcode.CmsgCancelTrade, []);
                break;
            }
            case "combo-cast-cancel-standstate":
            {
                var stand = new PacketWriter();
                stand.WriteUInt32((uint)StandState.Sit);
                DispatchFollowUp(session, player, WorldOpcode.CmsgStandstatechange, stand.ToArray());

                var cancel = new PacketWriter();
                cancel.WriteUInt32(133u);
                DispatchFollowUp(session, player, WorldOpcode.CmsgCancelCast, cancel.ToArray());

                var standUp = new PacketWriter();
                standUp.WriteUInt32((uint)StandState.Stand);
                DispatchFollowUp(session, player, WorldOpcode.CmsgStandstatechange, standUp.ToArray());
                break;
            }
            case "combo-loot-take-release":
            {
                ulong targetGuid = PickCandidateGuid(rng, player);
                var autostore = new PacketWriter();
                autostore.WriteByte(0);
                DispatchFollowUp(session, player, WorldOpcode.CmsgAutostoreLootItem, autostore.ToArray());

                DispatchFollowUp(session, player, WorldOpcode.CmsgLootMoney, []);

                var release = new PacketWriter();
                release.WriteUInt64(targetGuid);
                DispatchFollowUp(session, player, WorldOpcode.CmsgLootRelease, release.ToArray());
                break;
            }
            case "combo-npc-gossip-vendor-quest":
            {
                ulong npcGuid = PickCandidateGuid(rng, player);
                var gossipSel = new PacketWriter();
                gossipSel.WriteUInt64(npcGuid);
                gossipSel.WriteUInt32(uint.MaxValue);
                DispatchFollowUp(session, player, WorldOpcode.CmsgGossipSelectOption, gossipSel.ToArray());

                var vendor = new PacketWriter();
                vendor.WriteUInt64(npcGuid);
                DispatchFollowUp(session, player, WorldOpcode.CmsgListInventory, vendor.ToArray());

                var quest = new PacketWriter();
                quest.WriteUInt64(npcGuid);
                DispatchFollowUp(session, player, WorldOpcode.CmsgQuestgiverStatusQuery, quest.ToArray());
                break;
            }
        }
    }

    private void DispatchFollowUp(WorldSession session, Player player, WorldOpcode opcode, byte[] payload)
    {
        try
        {
            DispatchProbePacket(session, player, opcode, payload);
        }
        catch (Exception ex) when (MalformedPacket.Is(ex))
        {
            Interlocked.Increment(ref _malformedRejected);
        }
    }

    private static ulong PickCandidateGuid(Random rng, Player player)
    {
        int choice = rng.Next(6);
        switch (choice)
        {
            case 0:
                return 0UL;
            case 1:
                return ulong.MaxValue;
            case 2:
                return player.Guid.Value;
            case 3:
                return player.Combat.Victim?.Guid.Value ?? (0xF130000000000000UL | (ulong)(uint)rng.Next(1, 50000));
            case 4:
                foreach (ObjectGuid visible in player.VisibleObjects)
                {
                    if (visible != player.Guid) return visible.Value;
                }
                return 0xF13000000000FFFFUL;
            default:
                return unchecked((ulong)rng.NextInt64());
        }
    }

    private static byte PickBoundarySlot(Random rng)
        => rng.Next(7) switch
        {
            0 => 0,
            1 => 19,
            2 => 23,
            3 => 38,
            4 => 118,
            5 => 254,
            _ => 255,
        };

    private static byte[] GuidPayload(ulong guid)
    {
        byte[] buf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, guid);
        return buf;
    }

    private static byte[] UInt32Payload(uint value)
    {
        byte[] buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        return buf;
    }

    private static byte[] GuidAndBytePayload(ulong guid, byte flag)
    {
        var w = new PacketWriter();
        w.WriteUInt64(guid);
        w.WriteByte(flag);
        return w.ToArray();
    }

    private static byte[] CastSpellPayload(uint spellId, ushort targetMask, ulong targetGuid)
    {
        var w = new PacketWriter();
        w.WriteUInt32(spellId);
        w.WriteUInt16(targetMask);
        if (targetMask != 0) w.WriteUInt64(targetGuid);
        return w.ToArray();
    }

    private static byte[] UseItemPayload(byte bag, byte slot, ulong targetGuid)
    {
        var w = new PacketWriter();
        w.WriteByte(bag);
        w.WriteByte(slot);
        w.WriteByte(1); // spell_count
        w.WriteByte(0); // cast_count
        w.WriteUInt64(0UL); // item guid (0 forces slot mismatch check)
        w.WriteUInt16(0x02); // unit target mask
        w.WriteUInt64(targetGuid);
        return w.ToArray();
    }

    private static byte[] GossipSelectPayload(ulong npcGuid, uint menuItemId, string code)
    {
        var w = new PacketWriter();
        w.WriteUInt64(npcGuid);
        w.WriteUInt32(menuItemId);
        w.WriteCString(code);
        return w.ToArray();
    }

    private static byte[] QuestNpcPayload(ulong npcGuid, uint questId)
    {
        var w = new PacketWriter();
        w.WriteUInt64(npcGuid);
        w.WriteUInt32(questId);
        return w.ToArray();
    }

    private static byte[] QuestRewardPayload(ulong npcGuid, uint questId, uint rewardIndex)
    {
        var w = new PacketWriter();
        w.WriteUInt64(npcGuid);
        w.WriteUInt32(questId);
        w.WriteUInt32(rewardIndex);
        return w.ToArray();
    }

    private static byte[] BuyItemPayload(ulong vendorGuid, uint itemId, byte count, byte mu)
    {
        var w = new PacketWriter();
        w.WriteUInt64(vendorGuid);
        w.WriteUInt32(itemId);
        w.WriteByte(count);
        w.WriteByte(mu);
        return w.ToArray();
    }

    private static byte[] SellItemPayload(ulong vendorGuid, ulong itemGuid, byte count)
    {
        var w = new PacketWriter();
        w.WriteUInt64(vendorGuid);
        w.WriteUInt64(itemGuid);
        w.WriteByte(count);
        return w.ToArray();
    }

    private static byte[] RepairPayload(ulong vendorGuid, ulong itemGuid)
    {
        var w = new PacketWriter();
        w.WriteUInt64(vendorGuid);
        w.WriteUInt64(itemGuid);
        return w.ToArray();
    }

    private static byte[] CStringPayload(string value)
    {
        var w = new PacketWriter();
        w.WriteCString(value);
        return w.ToArray();
    }

    private static byte[] BuildMovementPayload(uint flags, uint timeMs, float x, float y, float z, float o, uint fallTime)
    {
        var w = new PacketWriter();
        w.WriteUInt32(flags);
        w.WriteUInt32(timeMs);
        w.WriteSingle(x);
        w.WriteSingle(y);
        w.WriteSingle(z);
        w.WriteSingle(o);
        w.WriteUInt32(fallTime);
        if ((flags & (uint)MovementFlags.Jumping) != 0)
        {
            w.WriteSingle(-7.955f);
            w.WriteSingle(1f);
            w.WriteSingle(0f);
            w.WriteSingle(7f);
        }
        return w.ToArray();
    }
}
