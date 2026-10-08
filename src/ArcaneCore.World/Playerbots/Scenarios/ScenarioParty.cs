using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// The real player a <see cref="PartyScenario"/> runs with: a game client connected to the world by socket, never a managed bot (a bot's
/// party AI only takes a real player as its master). It behaves like a game client: it acknowledges the server's teleports by itself
/// (MSG_MOVE_TELEPORT_ACK, MSG_MOVE_WORLDPORT_ACK) and records every packet it receives.
/// </summary>
public interface IPartyScenarioMaster
{
    /// <summary>The master's character name.</summary>
    string Name { get; }

    /// <summary>Send one client packet over the socket.</summary>
    Task SendAsync(WorldOpcode opcode, byte[] payload);

    /// <summary>A mark for "packets received from now on" (pass to <see cref="Received"/>).</summary>
    long Mark();

    /// <summary>Every packet the client received at or after <paramref name="since"/>, in order (thread-safe).</summary>
    IReadOnlyList<(WorldOpcode Opcode, byte[] Payload)> Received(long since);
}

/// <summary>
/// <c>party-master</c>: a real player and a managed bot that runs autonomously (vmangos PartyBotAI; <see cref="PlayerbotPartyAI"/>).
/// The master is on the bot's invite allowlist (<c>World:Playerbots:Party:Allowlist</c>; befriending a bot is not consent, only the bot's
/// own friend list is) and invites it, and it accepts; it follows a 40-yard walk;
/// the master targets a hostile creature and whispers 'attack', and the bot fights it; the group loot roll on its corpse gets the bot's
/// vote at once, so it resolves as soon as the master votes; after 'stay' the bot holds while the master walks off; 'status' gets a
/// whispered answer; the master takes The Deadmines entrance (area trigger 78) and the bot lands in the same instance, and comes out
/// with it through the exit (119); the master leaves the group and the bot goes back to its own goals.
/// <para>
/// It needs a master (<see cref="PartyScenario(IPartyScenarioMaster, uint)"/>; the tests connect one) whom the invite policy lets invite
/// the bot (on the allowlist, or the policy Anyone), the Deadmines content of
/// <see cref="DungeonEntryScenario"/>, a hostile creature near the master whose loot has an item at the group's loot threshold, and the
/// dungeon map among <c>World:Playerbots:AllowedMaps</c>. Without a master it fails at its first step.
/// </para>
/// </summary>
public sealed class PartyScenario : IPlayerbotScenario
{
    /// <summary>The bot this scenario creates (or reuses) and runs autonomously.</summary>
    public const string BotName = "Scnfollower";

    /// <summary>Game time the bot may take to answer a loot roll: far below any roll timer.</summary>
    public static readonly TimeSpan RollAnswerBound = TimeSpan.FromSeconds(2);

    private readonly IPartyScenarioMaster? _master;
    private readonly uint _targetEntry;

    /// <summary>The catalog's instance: no master, so it fails at its first step (a live run has no socket master to give it).</summary>
    public PartyScenario()
    {
    }

    /// <param name="master">The real player.</param>
    /// <param name="targetEntry">The creature the master orders attacked (0: the nearest hostile one within 100 yards of the master).</param>
    public PartyScenario(IPartyScenarioMaster master, uint targetEntry = 0)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));
        _targetEntry = targetEntry;
    }

    public string Name => "party-master";

    public string Description => "a real player invites an autonomous bot; it follows, fights, rolls, holds, reports and enters a dungeon with its master";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IPartyScenarioMaster master = await context.StepAsync("a real player is the master", () =>
            Task.FromResult(_master ?? throw new ScenarioAssertionException("no master: this scenario needs a real client (PartyScenario(master))"))).ConfigureAwait(false);
        var run = new Run(context, master);
        await run.ExecuteAsync(_targetEntry).ConfigureAwait(false);
    }

    private sealed class Run(ScenarioContext context, IPartyScenarioMaster master)
    {
        private readonly ManagedPlayerbotFeature _bots = context.Services.GetRequiredService<ManagedPlayerbotFeature>();
        private readonly TeleportService _teleports = context.Services.GetRequiredService<TeleportFeature>().Teleports;
        private Guid _botId;
        private ObjectGuid _botGuid;
        private ObjectGuid _masterGuid;

        private GroupManager Groups => context.Services.GetRequiredService<SocialFeature>().Context.Groups;

        private Player Bot => _bots.FindSession(_botId)?.Player is { IsInWorld: true } player ? player
            : throw new ScenarioAssertionException($"{BotName} is not in the world");

        /// <summary>The bot's player wherever it is, also between maps (waits must not throw there).</summary>
        private Player? BotAnywhere => _bots.FindSession(_botId)?.Player;

        private Player Master => context.World.FindOnlinePlayer(master.Name)
            ?? throw new ScenarioAssertionException($"{master.Name} is not online");

        public async Task ExecuteAsync(uint targetEntry)
        {
            _masterGuid = await context.StepAsync($"{master.Name} is in the world", () => context.ReadAsync(() => Master.Guid)).ConfigureAwait(false);
            bool started = await LoginBotAsync().ConfigureAwait(false);
            try
            {
                await InviteAsync().ConfigureAwait(false);
                Vector3 home = await context.ReadAsync(() => Position(Master)).ConfigureAwait(false);
                Creature target = await FindTargetAsync(targetEntry).ConfigureAwait(false);
                Vector3 away = Away(home, new Vector3(target.X, target.Y, target.Z), 40f);
                await context.StepAsync("the bot follows a 40-yard walk", async () =>
                {
                    await WalkMasterAsync(away).ConfigureAwait(false);
                    await WaitBesideMasterAsync("stands beside", 6f).ConfigureAwait(false);
                    ScenarioContext.ExpectEqual(PlayerbotGoalKind.Follow, Goal(), "bot goal");
                }).ConfigureAwait(false);
                await context.StepAsync("the master walks back; the bot follows", async () =>
                {
                    await WalkMasterAsync(home).ConfigureAwait(false);
                    await WaitBesideMasterAsync("stands beside", 6f).ConfigureAwait(false);
                }).ConfigureAwait(false);
                await AttackAsync(target).ConfigureAwait(false);
                await LootRollAsync(target).ConfigureAwait(false);
                await StayAsync().ConfigureAwait(false);
                await StatusAsync().ConfigureAwait(false);
                await DungeonAsync().ConfigureAwait(false);
                await DisbandAsync().ConfigureAwait(false);
            }
            finally
            {
                await CleanupAsync(started).ConfigureAwait(false);
            }
        }

        // --- steps -------------------------------------------------------------------------------------------------

        /// <summary>Create the bot when it does not exist and start it autonomous (or switch it back to autonomous). True: this run started it.</summary>
        private Task<bool> LoginBotAsync() => context.StepAsync($"login {BotName}, autonomous", async () =>
        {
            PlayerbotStatus? existing = _bots.Snapshot().FirstOrDefault(s => s.Name.Equals(BotName, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                PlayerbotOperationResult created = await _bots.CreateAsync(BotName, 1, 1, context.CancellationToken).ConfigureAwait(false);
                if (!created.Success || created.BotId is not { } id) throw new ScenarioAssertionException($"creating {BotName} failed: {created.Code}");
                _botId = id;
            }
            else
            {
                _botId = existing.BotId;
            }

            bool started = false;
            if (_bots.FindSession(_botId) is null)
            {
                PlayerbotOperationResult result = await _bots.StartAsync(_botId.ToString(), context.CancellationToken).ConfigureAwait(false);
                if (!result.Success) throw new ScenarioAssertionException($"starting {BotName} failed: {result.Code}");
                started = true;
            }
            else if (_bots.IsScripted(_botId))
            {
                ScenarioContext.Expect(await _bots.SetControllerAsync(_botId, null).ConfigureAwait(false), $"{BotName} could not be made autonomous");
            }

            await context.WaitUntilAsync($"{BotName} is in the world", () => _bots.FindSession(_botId)?.Player is { IsInWorld: true }).ConfigureAwait(false);
            _botGuid = await context.ReadAsync(() => Bot.Guid).ConfigureAwait(false);
            ScenarioContext.Expect(!_bots.IsScripted(_botId), $"{BotName} is scripted");
            return started;
        });

        private Task InviteAsync() => context.StepAsync($"{master.Name} invites {BotName}; the bot accepts", async () =>
        {
            if (await context.ReadAsync(() => Groups.GetGroup(_masterGuid) is not null).ConfigureAwait(false))
                await master.SendAsync(WorldOpcode.CmsgGroupDisband, []).ConfigureAwait(false);
            await context.WaitUntilAsync("neither is grouped", () => Groups.GetGroup(_masterGuid) is null && Groups.GetGroup(_botGuid) is null).ConfigureAwait(false);
            PlayerbotPartyOptions party = (context.Services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new PlayerbotOptions()).Party;
            ScenarioContext.Expect(party.InvitePolicy == PlayerbotInvitePolicy.Anyone
                || party.Allowlist.Contains(master.Name, StringComparer.OrdinalIgnoreCase),
                $"{master.Name} may not invite {BotName}: put the master on World:Playerbots:Party:Allowlist");
            await master.SendAsync(WorldOpcode.CmsgGroupInvite, CString(BotName)).ConfigureAwait(false);
            await context.WaitUntilAsync($"{BotName} joined {master.Name}'s group", () => Groups.AreInSameGroup(_masterGuid, _botGuid)
                && Groups.GetGroup(_masterGuid)?.LeaderGuid == _masterGuid).ConfigureAwait(false);
            await context.WaitUntilAsync($"{BotName}'s party AI drives it", () => _bots.IsPartyDriven(_botId)).ConfigureAwait(false);
        });

        private Task<Creature> FindTargetAsync(uint entry) => context.StepAsync("a hostile creature is near the master", () => context.ReadAsync(() =>
        {
            Player player = Master;
            Map map = player.Map ?? throw new ScenarioAssertionException($"{master.Name} is in no map");
            return player.VisibleObjects.Select(map.FindObject).OfType<Creature>()
                .Where(c => c.IsAlive && (entry == 0 || c.Entry == entry) && map.Combat.Hooks.CanAttack(player, c)
                    && Vector3.Distance(Position(c), Position(player)) <= 100f)
                .OrderBy(c => Vector3.Distance(Position(c), Position(player)))
                .FirstOrDefault() ?? throw new ScenarioAssertionException(entry == 0 ? "no hostile creature within 100 yards" : $"no creature {entry} within 100 yards");
        }));

        private Task AttackAsync(Creature target) => context.StepAsync($"{master.Name} targets {target.Template.Name} and whispers 'attack'; the bot fights", async () =>
        {
            // Within group reward range of the kill (the loot roll below is among both), out of the creature's aggro range.
            (Vector3 at, Vector3 from) = await context.ReadAsync(() => (Position(target), Position(Master))).ConfigureAwait(false);
            if (Vector3.Distance(at, from) > 25f) await WalkMasterAsync(Away(at, from, -25f)).ConfigureAwait(false);
            await WaitBesideMasterAsync("stands beside", 6f).ConfigureAwait(false);
            await master.SendAsync(WorldOpcode.CmsgSetSelection, Guid(target.Guid)).ConfigureAwait(false);
            await context.WaitUntilAsync($"{master.Name} has {target.Template.Name} selected", () => Master.Selection == target.Guid).ConfigureAwait(false);
            long mark = master.Mark();
            await WhisperAsync("attack").ConfigureAwait(false);
            await WaitForWhisperAsync(mark, text => text.StartsWith("Attacking", StringComparison.Ordinal)).ConfigureAwait(false);
            await context.WaitUntilAsync($"{BotName} attacks {target.Template.Name}", () => ReferenceEquals(Bot.Combat.Victim, target) || !target.IsAlive).ConfigureAwait(false);
            ScenarioContext.Expect(await context.ReadAsync(() => !target.IsAlive || Inspect().Goal == PlayerbotGoalKind.Assist).ConfigureAwait(false),
                "the bot's goal is not Assist while it fights");
            await context.WaitUntilAsync($"{target.Template.Name} dies", () => !target.IsAlive, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        });

        /// <summary>The master opens the corpse: a group roll starts, the bot votes at once, and the master's vote ends it (no roll timer).</summary>
        private Task LootRollAsync(Creature corpse) => context.StepAsync("the group loot roll does not wait for the bot", async () =>
        {
            Vector3 at = await context.ReadAsync(() => Position(corpse)).ConfigureAwait(false);
            Vector3 from = await context.ReadAsync(() => Position(Master)).ConfigureAwait(false);
            await WalkMasterAsync(Away(at, from, -2f)).ConfigureAwait(false);
            long mark = master.Mark();
            await master.SendAsync(WorldOpcode.CmsgLoot, Guid(corpse.Guid)).ConfigureAwait(false);
            byte[] start = await WaitForMasterPacketAsync(mark, WorldOpcode.SmsgLootStartRoll, _ => true,
                "a group loot roll starts (the corpse's loot needs an item at the group's threshold)").ConfigureAwait(false);
            uint slot = BitConverter.ToUInt32(start, 8);
            await WaitForMasterPacketAsync(mark, WorldOpcode.SmsgLootRoll, payload => payload.Length >= 20
                && BitConverter.ToUInt64(payload, 12) == _botGuid.Value, $"{BotName}'s vote", RollAnswerBound).ConfigureAwait(false);
            var vote = new PacketWriter(13);
            vote.WriteUInt64(corpse.Guid.Value);
            vote.WriteUInt32(slot);
            vote.WriteByte((byte)RollVote.Need);
            await master.SendAsync(WorldOpcode.CmsgLootRoll, vote.ToArray()).ConfigureAwait(false);
            await context.WaitUntilAsync("the roll is decided", () => Master.Map is { } map
                && context.Services.GetRequiredService<GameObjectLootFeature>().FindSystem(map)?.Loot?.Rolls.ActiveRollCount == 0,
                RollAnswerBound).ConfigureAwait(false);
            await master.SendAsync(WorldOpcode.CmsgLootRelease, Guid(corpse.Guid)).ConfigureAwait(false);
        });

        private Task StayAsync() => context.StepAsync("after 'stay' the bot holds while the master walks off", async () =>
        {
            long mark = master.Mark();
            await WhisperAsync("stay").ConfigureAwait(false);
            await WaitForWhisperAsync(mark, text => text == "Staying here.").ConfigureAwait(false);
            await context.WaitUntilAsync($"{BotName} stands still", () => (Bot.Movement.Flags & MovementFlags.MaskMoving) == 0).ConfigureAwait(false);
            Vector3 held = await context.ReadAsync(() => Position(Bot)).ConfigureAwait(false);
            Vector3 from = await context.ReadAsync(() => Position(Master)).ConfigureAwait(false);
            await WalkMasterAsync(from + new Vector3(0, 30f, 0)).ConfigureAwait(false);
            await context.IdleAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            float moved = await context.ReadAsync(() => Vector3.Distance(Position(Bot), held)).ConfigureAwait(false);
            ScenarioContext.Expect(moved < 0.5f, $"{BotName} moved {moved:F1} yards while told to stay");
            ScenarioContext.ExpectEqual(PlayerbotPartyMode.Stay, await context.ReadAsync(() => Inspect().PartyMode).ConfigureAwait(false), "party mode");
        });

        private Task StatusAsync() => context.StepAsync("'status' gets a whispered answer", async () =>
        {
            long mark = master.Mark();
            await WhisperAsync("status").ConfigureAwait(false);
            string answer = await WaitForWhisperAsync(mark, text => text.StartsWith("Level ", StringComparison.Ordinal)).ConfigureAwait(false);
            ScenarioContext.Expect(answer.Contains("health ", StringComparison.Ordinal) && answer.Contains("mana ", StringComparison.Ordinal)
                && answer.Contains("staying", StringComparison.Ordinal), $"status answer: {answer}");
        });

        private async Task DungeonAsync()
        {
            AreaTriggerTemplate entrance = await context.StepAsync("the Deadmines content is there", () => context.ReadAsync(() =>
            {
                WorldMaps maps = WorldMaps.Of(context.World);
                ScenarioContext.Expect(maps.Registry.Find(DungeonEntryScenario.Deadmines) is { IsDungeon: true }, $"map {DungeonEntryScenario.Deadmines} is not a known dungeon");
                return maps.FindAreaTrigger(DungeonEntryScenario.EntranceTrigger) ?? throw new ScenarioAssertionException($"no areatrigger_template {DungeonEntryScenario.EntranceTrigger}");
            })).ConfigureAwait(false);
            await context.StepAsync("'follow' ends the stay", async () =>
            {
                long mark = master.Mark();
                await WhisperAsync("follow").ConfigureAwait(false);
                await WaitForWhisperAsync(mark, text => text == "Following.").ConfigureAwait(false);
                await WaitBesideMasterAsync("stands beside", 6f).ConfigureAwait(false);
            }).ConfigureAwait(false);
            byte level = await context.ReadAsync(() => WorldMaps.Of(context.World).FindAreaTriggerTeleport(DungeonEntryScenario.EntranceTrigger)?.RequiredLevel ?? 0).ConfigureAwait(false);
            await context.StepAsync($"the master is level {level} and at the Deadmines entrance; the bot teleports to it", async () =>
            {
                ProgressionFeature progression = context.Services.GetRequiredService<ProgressionFeature>();
                await context.ReadAsync(() =>
                {
                    if (Master.Level < level) progression.Progression.GiveLevel(Master, level);
                    return true;
                }).ConfigureAwait(false);
                await PlaceMasterAsync(entrance.MapId, entrance.X, entrance.Y, entrance.Z).ConfigureAwait(false);
                await WaitBesideMasterAsync("teleported beside", 10f).ConfigureAwait(false);
            }).ConfigureAwait(false);
            uint instance = await context.StepAsync($"{master.Name} takes area trigger {DungeonEntryScenario.EntranceTrigger}; the bot lands in the same instance", async () =>
            {
                await master.SendAsync(WorldOpcode.CmsgAreatrigger, UInt32(DungeonEntryScenario.EntranceTrigger)).ConfigureAwait(false);
                await context.WaitUntilAsync($"{master.Name} is in The Deadmines", () => Arrived(Master, DungeonEntryScenario.Deadmines)).ConfigureAwait(false);
                await context.WaitUntilAsync($"{BotName} is in The Deadmines", () => BotAnywhere is { } bot && Arrived(bot, DungeonEntryScenario.Deadmines)).ConfigureAwait(false);
                (uint masterInstance, uint botInstance) = await context.ReadAsync(() => (Master.Map!.InstanceId, Bot.Map!.InstanceId)).ConfigureAwait(false);
                ScenarioContext.Expect(masterInstance != 0, "The Deadmines is not an instance");
                ScenarioContext.ExpectEqual(masterInstance, botInstance, "the bot's instance");
                return masterInstance;
            }).ConfigureAwait(false);
            await context.StepAsync($"{master.Name} leaves through area trigger {DungeonEntryScenario.ExitTrigger}; the bot comes out too", async () =>
            {
                AreaTriggerTemplate exit = await context.ReadAsync(() => WorldMaps.Of(context.World).FindAreaTrigger(DungeonEntryScenario.ExitTrigger)
                    ?? throw new ScenarioAssertionException($"no areatrigger_template {DungeonEntryScenario.ExitTrigger}")).ConfigureAwait(false);
                await PlaceMasterAsync(exit.MapId, exit.X, exit.Y, exit.Z).ConfigureAwait(false);
                await master.SendAsync(WorldOpcode.CmsgAreatrigger, UInt32(DungeonEntryScenario.ExitTrigger)).ConfigureAwait(false);
                await context.WaitUntilAsync($"{master.Name} is out", () => Arrived(Master, entrance.MapId)).ConfigureAwait(false);
                await WaitBesideMasterAsync("is out beside", 10f).ConfigureAwait(false);
                ScenarioContext.Expect(instance != 0, "instance");
            }).ConfigureAwait(false);
        }

        private Task DisbandAsync() => context.StepAsync($"{master.Name} leaves the group; the bot goes back to its own goals", async () =>
        {
            await master.SendAsync(WorldOpcode.CmsgGroupDisband, []).ConfigureAwait(false);
            await context.WaitUntilAsync($"{BotName} is not grouped", () => Groups.GetGroup(_botGuid) is null).ConfigureAwait(false);
            await context.WaitUntilAsync($"{BotName}'s brain drives it again", () => !_bots.IsPartyDriven(_botId)
                && Goal() is not (PlayerbotGoalKind.Follow or PlayerbotGoalKind.Assist)).ConfigureAwait(false);
        });

        /// <summary>A failed run must not leave the bot grouped or in the dungeon; a bot this run started is stopped again.</summary>
        private async Task CleanupAsync(bool started)
        {
            bool grouped = await context.ReadAsync(() => Groups.GetGroup(_masterGuid) is not null || (!_botGuid.IsEmpty && Groups.GetGroup(_botGuid) is not null)).ConfigureAwait(false);
            if (grouped)
            {
                await context.CleanupAsync("leave the group", async () =>
                {
                    if (await context.ReadAsync(() => Groups.GetGroup(_masterGuid) is not null).ConfigureAwait(false))
                        await master.SendAsync(WorldOpcode.CmsgGroupDisband, []).ConfigureAwait(false);
                    await context.WaitUntilAsync("nobody is grouped", () => Groups.GetGroup(_masterGuid) is null && Groups.GetGroup(_botGuid) is null).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            if (started && _botId != System.Guid.Empty)
                await context.CleanupAsync($"stop {BotName}", () => _bots.StopAsync(_botId.ToString(), context.CancellationToken)).ConfigureAwait(false);
        }

        // --- helpers -------------------------------------------------------------------------------------------------

        private PlayerbotGoalKind Goal() => _bots.Snapshot().FirstOrDefault(s => s.BotId == _botId)?.Goal ?? PlayerbotGoalKind.Explore;

        private PlayerbotInspection Inspect() => _bots.FindSession(_botId) is { } session
            ? PlayerbotInspector.Capture(session, BrainOf(), PartyOf()) ?? throw new ScenarioAssertionException($"{BotName} cannot be inspected")
            : throw new ScenarioAssertionException($"{BotName} is not running");

        private PlayerbotBrain BrainOf() => _bots.FindBrain(_botId) ?? throw new ScenarioAssertionException($"{BotName} has no brain");

        private PlayerbotPartyAI? PartyOf() => _bots.FindParty(_botId);

        /// <summary>Wait until the bot stands within <paramref name="within"/> yards of the master; a timeout reports where both are and what the bot does.</summary>
        private async Task WaitBesideMasterAsync(string what, float within)
        {
            try
            {
                await context.WaitUntilAsync($"{BotName} {what} {master.Name}", () => BesideMaster(within)).ConfigureAwait(false);
            }
            catch (ScenarioTimeoutException timeout)
            {
                string facts = await context.ReadAsync(() =>
                {
                    if (BotAnywhere is not { IsInWorld: true } bot) return "the bot is between maps";
                    Player owner = Master;
                    PlayerbotInspection inspection = Inspect();
                    return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"bot map {bot.Map?.MapId}/{bot.Map?.InstanceId} at ({bot.X:F1}, {bot.Y:F1}, {bot.Z:F1}), master map {owner.Map?.MapId}/{owner.Map?.InstanceId} at ({owner.X:F1}, {owner.Y:F1}, {owner.Z:F1}), distance {Vector3.Distance(Position(bot), Position(owner)):F1}; goal {inspection.Goal}, party {inspection.Master}/{inspection.PartyMode}, following {inspection.Following}, flags {bot.Movement.Flags}, victim {bot.Combat.Victim?.Guid.Value:X}, teleport {_teleports.StageOf(bot)}");
                }).ConfigureAwait(false);
                throw new ScenarioAssertionException(timeout.Message + " -- " + facts);
            }
        }

        private bool BesideMaster(float within)
        {
            if (BotAnywhere is not { IsInWorld: true } bot) return false;
            Player owner = Master;
            return owner.Map is { } map && ReferenceEquals(bot.Map, map) && _teleports.StageOf(bot) is null && _teleports.StageOf(owner) is null
                && Vector3.Distance(Position(bot), Position(owner)) <= within;
        }

        private bool Arrived(Player player, uint mapId)
            => player.IsInWorld && player.Map?.MapId == mapId && _teleports.StageOf(player) is null;

        /// <summary>The master's client walks: START_FORWARD, a heartbeat every 5 yards, STOP at <paramref name="to"/>.</summary>
        private async Task WalkMasterAsync(Vector3 to)
        {
            Vector3 from = await context.ReadAsync(() => Position(Master)).ConfigureAwait(false);
            float distance = Vector3.Distance(from, to);
            float facing = MathF.Atan2(to.Y - from.Y, to.X - from.X);
            if (facing < 0) facing += MathF.Tau;
            int steps = Math.Max(1, (int)MathF.Ceiling(distance / 5f));
            uint time = await context.ReadAsync(() => context.World.NowMs).ConfigureAwait(false);
            await master.SendAsync(WorldOpcode.MsgMoveStartForward, Movement(from, facing, MovementFlags.Forward, time)).ConfigureAwait(false);
            for (int i = 1; i < steps; i++)
                await master.SendAsync(WorldOpcode.MsgMoveHeartbeat, Movement(Vector3.Lerp(from, to, (float)i / steps), facing, MovementFlags.Forward, time + (uint)(i * 700))).ConfigureAwait(false);
            await master.SendAsync(WorldOpcode.MsgMoveStop, Movement(to, facing, MovementFlags.None, time + (uint)(steps * 700))).ConfigureAwait(false);
            await context.WaitUntilAsync($"{master.Name} reaches ({to.X:F1}, {to.Y:F1})", () => Vector3.Distance(Position(Master), to) < 0.5f).ConfigureAwait(false);
        }

        /// <summary>Setup: teleport the master through the ordinary teleport service; its client acknowledges.</summary>
        private async Task PlaceMasterAsync(uint mapId, float x, float y, float z)
        {
            bool started = await context.ReadAsync(() => _teleports.TeleportTo(Master, mapId, x, y, z, Master.Orientation)).ConfigureAwait(false);
            ScenarioContext.Expect(started, $"{master.Name} cannot be teleported to map {mapId}");
            await context.WaitUntilAsync($"{master.Name} arrives at ({x:F1}, {y:F1}) on map {mapId}", () => Arrived(Master, mapId)
                && MathF.Abs(Master.X - x) < 1f && MathF.Abs(Master.Y - y) < 1f).ConfigureAwait(false);
        }

        private Task WhisperAsync(string text)
        {
            var chat = new PacketWriter(24 + text.Length);
            chat.WriteUInt32((uint)ChatType.Whisper);
            chat.WriteUInt32((uint)Language.Common);
            chat.WriteCString(BotName);
            chat.WriteCString(text);
            return master.SendAsync(WorldOpcode.CmsgMessagechat, chat.ToArray());
        }

        /// <summary>Wait for the bot's whisper to the master whose text matches; returns the text.</summary>
        private async Task<string> WaitForWhisperAsync(long since, Func<string, bool> match)
        {
            string? found = null;
            await context.WaitUntilAsync($"{master.Name} gets {BotName}'s whisper", () =>
            {
                foreach ((WorldOpcode opcode, byte[] payload) in master.Received(since))
                {
                    if (opcode != WorldOpcode.SmsgMessagechat) continue;
                    ChatMessageView line = ScenarioDecoders.ChatMessage(payload);
                    if (line.Type == ChatType.Whisper && line.Sender == _botGuid.Value && match(line.Message))
                    {
                        found = line.Message;
                        return true;
                    }
                }

                return false;
            }).ConfigureAwait(false);
            return found!;
        }

        private async Task<byte[]> WaitForMasterPacketAsync(long since, WorldOpcode opcode, Func<byte[], bool> match, string what, TimeSpan? timeout = null)
        {
            byte[]? found = null;
            await context.WaitUntilAsync($"{master.Name} receives {opcode}: {what}", () =>
            {
                foreach ((WorldOpcode received, byte[] payload) in master.Received(since))
                {
                    if (received == opcode && match(payload))
                    {
                        found = payload;
                        return true;
                    }
                }

                return false;
            }, timeout).ConfigureAwait(false);
            return found!;
        }

        private static Vector3 Position(WorldObject o) => new(o.X, o.Y, o.Z);

        /// <summary><paramref name="distance"/> yards from <paramref name="from"/>, directly away from <paramref name="other"/> (negative: towards it).</summary>
        private static Vector3 Away(Vector3 from, Vector3 other, float distance)
        {
            Vector2 direction = new(from.X - other.X, from.Y - other.Y);
            direction = direction.LengthSquared() < 0.01f ? Vector2.UnitX : Vector2.Normalize(direction);
            return new Vector3(from.X + (direction.X * distance), from.Y + (direction.Y * distance), from.Z);
        }

        private static byte[] Movement(Vector3 at, float facing, MovementFlags flags, uint time)
        {
            var info = new MovementInfo { Flags = flags, Time = time, X = at.X, Y = at.Y, Z = at.Z, Orientation = facing };
            var writer = new PacketWriter(32);
            info.Write(writer);
            return writer.ToArray();
        }

        private static byte[] CString(string text)
        {
            var writer = new PacketWriter(text.Length + 1);
            writer.WriteCString(text);
            return writer.ToArray();
        }

        private static byte[] Guid(ObjectGuid guid)
        {
            var writer = new PacketWriter(8);
            writer.WriteUInt64(guid.Value);
            return writer.ToArray();
        }

        private static byte[] UInt32(uint value)
        {
            var writer = new PacketWriter(4);
            writer.WriteUInt32(value);
            return writer.ToArray();
        }
    }
}
