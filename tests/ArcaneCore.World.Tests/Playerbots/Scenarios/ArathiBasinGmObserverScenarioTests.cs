using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// A game master who enters a running Arathi Basin match with <c>.gm on</c> and <c>.goname &lt;participant&gt;</c> is bound to the match
/// (BattlegroundFeature.BeginGmMove/CommitMove) but is never one of its participants. The match's broadcasts (Announce, PlaySoundToAll,
/// UpdateWorldState) still have to reach that observer, and the observer still has to receive the match's initial world states on arrival
/// (the resource maximum 1780 = 2000, ArathiBasin.WorldStateResourcesMax / MaxTeamScore), while it stays out of everything that counts or
/// rewards participants: the participant list, the per-team counts and invitation slots a queue balances from, the score table, the final
/// scoreboard and the win/lose outcome. The match is the real one of <see cref="ArathiBasinWorldScenarioTests"/> (<see cref="ArathiBasinTestContent"/>).
/// The move into the match is the real command: the GM's socket client says <c>.goname</c> in chat, as
/// GmGoBattlegroundAndInstanceTests does for Warsong Gulch. GM mode is switched on with Player.SetGameMaster, the method <c>.gm on</c> calls.
/// </summary>
public sealed class ArathiBasinGmObserverScenarioTests
{
    private const uint ArathiBasinMap = 529;

    /// <summary>Bound of each wait on the GM's socket client (the world runs on a manual clock, the packets travel in wall time).</summary>
    private static readonly TimeSpan PacketWait = TimeSpan.FromSeconds(10);

    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    /// <summary>What the match counts before the GM arrives: the numbers a queue balances from.</summary>
    private sealed record Baseline(int PlayerCount, uint AlliancePlayers, uint HordePlayers, uint AllianceInvited, uint HordeInvited, uint AllianceFree, uint HordeFree);

    private static Baseline BaselineOf(Battleground match) => new(
        match.PlayerCount, match.PlayersCountByTeam(Team.Alliance), match.PlayersCountByTeam(Team.Horde),
        match.InvitedCount(Team.Alliance), match.InvitedCount(Team.Horde), match.FreeSlotsForTeam(Team.Alliance), match.FreeSlotsForTeam(Team.Horde));

    /// <summary>
    /// A socket client that records every packet it receives and, like a game client, acknowledges the server's teleports (near and far), so the
    /// GM can be moved into the match. Owns the client.
    /// </summary>
    private sealed class GmTap : IAsyncDisposable
    {
        private readonly WorldTestClient _client;
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly List<(long Sequence, WorldOpcode Opcode, byte[] Payload)> _received = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _reader;
        private long _sequence;
        private int _disposed;

        public GmTap(WorldTestClient client, string name)
        {
            _client = client;
            Name = name;
            _reader = Task.Run(ReadLoopAsync);
        }

        public string Name { get; }

        /// <summary>The sequence number the next received packet gets.</summary>
        public long Mark() => Interlocked.Read(ref _sequence) + 1;

        public async Task SayAsync(string text)
        {
            await _send.WaitAsync();
            try
            {
                await _client.SendChatAsync(ChatType.Say, Language.Common, text);
            }
            finally
            {
                _send.Release();
            }
        }

        /// <summary>The payload of every <paramref name="opcode"/> packet received at or after <paramref name="since"/>.</summary>
        public byte[][] Received(WorldOpcode opcode, long since)
        {
            lock (_received)
            {
                return [.. _received.Where(p => p.Sequence >= since && p.Opcode == opcode).Select(p => p.Payload)];
            }
        }

        private async Task ReadLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                (WorldOpcode opcode, byte[] payload) packet;
                try
                {
                    packet = await _client.ReadAsync(TimeSpan.FromMinutes(10));
                }
                catch (Exception error) when (_stop.IsCancellationRequested || error is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }

                lock (_received)
                {
                    _received.Add((Interlocked.Increment(ref _sequence), packet.opcode, packet.payload));
                }

                if (packet.opcode == WorldOpcode.MsgMoveTeleportAck)
                {
                    var reader = new PacketReader(packet.payload);
                    ulong guid = reader.ReadPackedGuid();
                    uint counter = reader.ReadUInt32();
                    var reply = new PacketWriter(16);
                    reply.WriteUInt64(guid);
                    reply.WriteUInt32(counter);
                    reply.WriteUInt32(0);
                    await SendRawAsync(WorldOpcode.MsgMoveTeleportAck, reply.ToArray());
                }
                else if (packet.opcode == WorldOpcode.SmsgNewWorld)
                {
                    await SendRawAsync(WorldOpcode.MsgMoveWorldportAck, []);
                }
            }
        }

        private async Task SendRawAsync(WorldOpcode opcode, byte[] payload)
        {
            await _send.WaitAsync();
            try
            {
                await _client.SendAsync(opcode, payload);
            }
            finally
            {
                _send.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _stop.CancelAsync();
            await _client.DisposeAsync();
            try
            {
                await _reader;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // closed under the reader
            }

            _stop.Dispose();
            _send.Dispose();
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> in an Arathi Basin match of an Alliance and a Horde bot with a game master observer in it: the GM is a
    /// game master (SetGameMaster), then says <c>.goname</c> to the Alliance bot, and the body starts once the GM stands in the match's map instance, bound to the
    /// match, and is not one of its participants.
    /// </summary>
    private static async Task RunWithObserverAsync(string name, string account, string character, Func<ScenarioContext, ScenarioBot, ScenarioBot, ArathiBasin, GmTap, Baseline, Task> body)
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(ArathiBasinTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario(name, async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(
                BattlegroundType.ArathiBasin, character + "a", character + "h", TimeSpan.FromMinutes(3));
            var ab = (ArathiBasin)match;
            Baseline baseline = await context.ReadAsync(() => BaselineOf(match));
            ScenarioContext.ExpectEqual(2, baseline.PlayerCount, "participants before the GM arrives");

            await using var gm = new GmTap(await world.EnterWorldAsync(account, character, AccountSecurity.Administrator), character);
            BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();

            await context.StepAsync("the GM switches GM mode on", async () =>
            {
                // The chat command `.gm on` (BuiltinCommands.GmMode) is just Player.SetGameMaster(true) plus a reply; the command did not take
                // effect in this harness (cause not established without a run: the chat packet is processed on the server's own time while the
                // manual clock spends its game budget), and the GM mode is not what is under test. Call the method the command calls.
                // The move into the match below still goes through the real `.goname` chat command.
                await context.ReadAsync(() =>
                {
                    context.World.FindOnlinePlayer(gm.Name)!.SetGameMaster(true);
                    return true;
                });
                ScenarioContext.Expect(await context.ReadAsync(() => context.World.FindOnlinePlayer(gm.Name)!.IsGameMaster), "the GM is not a game master");
            });

            await context.StepAsync("the GM gonames to the Alliance bot inside the match", async () =>
            {
                long mark = gm.Mark();
                await gm.SayAsync(".goname " + ally.Name);
                await context.WaitUntilAsync("the GM is in the match's map instance and bound to the match, or was refused", () =>
                    context.World.FindOnlinePlayer(gm.Name) is { IsInWorld: true, Map: { } map } p && map.MapId == ArathiBasinMap && map.InstanceId == match.InstanceId
                        && feature.BattlegroundOf(p.Guid) == match
                    || gm.Received(WorldOpcode.SmsgMessagechat, mark).Any(c => Encoding.UTF8.GetString(c).Contains("Target map or coordinates is invalid", StringComparison.Ordinal)));
                ObjectGuid gmGuid = await context.ReadAsync(() => context.World.FindOnlinePlayer(gm.Name)!.Guid);
                ScenarioContext.Expect(await context.ReadAsync(() => feature.BattlegroundOf(gmGuid) == match), "the GM is not bound to the match");
                ScenarioContext.Expect(await context.ReadAsync(() => match.PlayerTeam(gmGuid) is null), "the GM became a participant of the match");
            });

            await body(context, ally, horde, ab, gm, baseline);
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report.ToString());
    }

    /// <summary>SMSG_INIT_WORLD_STATES: u32 map, u32 zone, u16 count, count * (u32 state, i32 value) (WorldStatePackets.BuildInit, vmangos Misc.cpp).</summary>
    private static (uint Map, uint Zone, List<(uint State, int Value)> States) InitWorldStates(byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint map = reader.ReadUInt32();
        uint zone = reader.ReadUInt32();
        ushort count = reader.ReadUInt16();
        var states = new List<(uint, int)>(count);
        for (int i = 0; i < count; i++)
        {
            states.Add((reader.ReadUInt32(), reader.ReadInt32()));
        }

        return (map, zone, states);
    }

    /// <summary>
    /// The GM, standing in the match as an observer, is told the match's state like a participant: on arrival the initial world states with the
    /// resource maximum 2000; then, when the Alliance bot opens the neutral Stables banner, the node's world state update, the announcement
    /// chat line and the sound the Horde participant receives (compared byte for byte with the participant's own packets).
    /// </summary>
    [Fact]
    public async Task GmObserver_ReceivesInitialWorldStates_AndTheBannerClaimBroadcasts()
    {
        await RunWithObserverAsync("ab-gm-observer-broadcasts", "GMABOBS", "Gmabobs", async (context, ally, horde, ab, gm, _) =>
        {
            await context.StepAsync("the GM got the match's initial world states on arrival", () => context.WaitUntilAsync(
                "SMSG_INIT_WORLD_STATES for map 529 with the resource maximum (field 1780) = 2000", () => gm.Received(WorldOpcode.SmsgInitWorldStates, 0)
                    .Select(InitWorldStates).Any(i => i.Map == ArathiBasinMap && i.States.Contains((ArathiBasin.WorldStateResourcesMax, ArathiBasin.MaxTeamScore))),
                PacketWait));

            await context.StepAsync("only the neutral stables banner stands", () => context.WaitUntilAsync("the neutral banner is spawned",
                () => Spawned(ally, ArathiBasinTestContent.NeutralBanner) && !Spawned(ally, ArathiBasinTestContent.AllianceContestedBanner)));

            long gmMark = gm.Mark();
            long hordeMark = horde.Mark();
            await context.StepAsync("the Alliance bot opens the neutral banner: the stables are claimed", async () =>
            {
                await context.PlaceAsync(ally, ArathiBasinMap, ArathiBasinTestContent.Stables.X + 1f, ArathiBasinTestContent.Stables.Y, ArathiBasinTestContent.Stables.Z);
                (SpellCastResult opened, uint uses) = await context.ReadAsync(() => OpenBanner(context, ally, ArathiBasinTestContent.NeutralBanner));
                ScenarioContext.ExpectEqual(SpellCastResult.CastOk, opened, "Opening cast result");
                ScenarioContext.ExpectEqual(1u, uses, "neutral banner uses (the open reached the goober)");
                ScenarioContext.ExpectEqual(ArathiBasin.StatusAllianceContested, await context.ReadAsync(() => ab.NodeStatus(ArathiBasin.NodeStables)), "stables status");
            });

            // The Horde participant receives all three; they are the reference of what the GM has to receive.
            byte[] hordeChat = await horde.WaitForPacketAsync(WorldOpcode.SmsgMessagechat, p => p, since: hordeMark);
            byte[] hordeSound = await horde.WaitForPacketAsync(WorldOpcode.SmsgPlaySound, p => p, since: hordeMark);
            await horde.WaitForPacketAsync(WorldOpcode.SmsgUpdateWorldState, ScenarioBattlegrounds.WorldState, s => s.Field == 1767 + 2 && s.Value == 1, hordeMark);

            await context.StepAsync("the GM gets the node's world state update", () => context.WaitUntilAsync(
                "SMSG_UPDATE_WORLD_STATE 1769 = 1", () => gm.Received(WorldOpcode.SmsgUpdateWorldState, gmMark)
                    .Select(ScenarioBattlegrounds.WorldState).Any(s => s.Field == 1767 + 2 && s.Value == 1), PacketWait));
            await context.StepAsync("the GM gets the announcement chat line", () => context.WaitUntilAsync(
                "the participant's SMSG_MESSAGECHAT", () => gm.Received(WorldOpcode.SmsgMessagechat, gmMark).Any(c => c.SequenceEqual(hordeChat)), PacketWait));
            await context.StepAsync("the GM gets the sound", () => context.WaitUntilAsync(
                "the participant's SMSG_PLAY_SOUND", () => gm.Received(WorldOpcode.SmsgPlaySound, gmMark).Any(s => s.SequenceEqual(hordeSound)), PacketWait));
        });
    }

    /// <summary>
    /// Guard (passes with and without the observer fix): the GM observer is not a participant and is invisible to everything that counts
    /// participants - the participant list, the player and per-team counts, the invitation counts and free slots a queue over-invites from - and
    /// the score table has no entry for it.
    /// </summary>
    [Fact]
    public async Task GmObserver_IsNotAParticipant_NotCounted_AndHasNoScore()
    {
        await RunWithObserverAsync("ab-gm-observer-counts", "GMABCNT", "Gmabcnt", async (context, ally, horde, ab, gm, baseline) =>
        {
            ObjectGuid gmGuid = await context.ReadAsync(() => context.World.FindOnlinePlayer(gm.Name)!.Guid);

            await context.StepAsync("the GM is outside the participant list and every count", async () =>
            {
                ScenarioContext.Expect(!await context.ReadAsync(() => ab.Participants().Any(p => p.Guid == gmGuid)), "the GM is in Participants()");
                ScenarioContext.Expect(!await context.ReadAsync(() => ab.Players.Contains(gmGuid)), "the GM is in Players");
                ScenarioContext.Expect(await context.ReadAsync(() => ab.PlayerTeam(gmGuid) is null), "the GM has a team");
                ScenarioContext.Expect(!await context.ReadAsync(() => ab.TeamMembers(Team.Alliance).Contains(gmGuid) || ab.TeamMembers(Team.Horde).Contains(gmGuid)),
                    "the GM is a member of a team's battleground raid");
                ScenarioContext.ExpectEqual(baseline, await context.ReadAsync(() => BaselineOf(ab)), "player counts, invitations and free slots with the GM on the map");
            });

            await context.StepAsync("the GM has no score entry and the participants have", async () =>
            {
                ScenarioContext.Expect(await context.ReadAsync(() => ab.ScoreOf(gmGuid) is null), "the GM has a score entry");
                ScenarioContext.Expect(await context.ReadAsync(() => ab.ScoreOf(ally.Guid) is not null), "the Alliance participant has no score entry");
                ScenarioContext.Expect(await context.ReadAsync(() => ab.ScoreOf(horde.Guid) is not null), "the Horde participant has no score entry");
            });
        });
    }

    /// <summary>
    /// Guard (passes with and without the observer fix): when the match ends the GM observer is neither on the final scoreboard nor given the
    /// win or lose outcome that participants receive, while the participants are (the Alliance bot's SMSG_BATTLEFIELD_WIN is awaited first, so
    /// a quiet GM is not an outcome that simply has not arrived yet). Honor lives on the score entry (BonusHonor of the scoreboard rows) and the
    /// mark spell is cast per participant; neither exists for the GM.
    /// </summary>
    [Fact]
    public async Task GmObserver_IsNotOnTheFinalScoreboard_AndGetsNoMatchOutcome()
    {
        await RunWithObserverAsync("ab-gm-observer-end", "GMABEND", "Gmabend", async (context, ally, horde, ab, gm, baseline) =>
        {
            ObjectGuid gmGuid = await context.ReadAsync(() => context.World.FindOnlinePlayer(gm.Name)!.Guid);
            long gmMark = gm.Mark();
            long allyMark = ally.Mark();
            long hordeMark = horde.Mark();

            await context.StepAsync("the Alliance wins", async () =>
            {
                await context.ReadAsync(() =>
                {
                    ab.EndBattleground(Team.Alliance);
                    return true;
                });
            });

            await ally.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldWin, p => p, since: allyMark);
            await horde.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldLose, p => p, since: hordeMark);

            await context.StepAsync("the GM is not on the final scoreboard", async () =>
            {
                PvpLogSnapshot final = await context.ReadAsync(() => ab.FinalScore) ?? throw new ScenarioAssertionException("the ended match has no final scoreboard");
                ScenarioContext.ExpectEqual(baseline.PlayerCount, final.Rows.Count, "rows of the final scoreboard");
                ScenarioContext.Expect(final.Rows.All(r => r.Player != gmGuid), "the GM has a row on the final scoreboard");
                ScenarioContext.Expect(await context.ReadAsync(() => ab.ScoreOf(gmGuid) is null), "the GM has a score entry after the end");
                ScenarioContext.ExpectEqual(baseline.PlayerCount, await context.ReadAsync(() => ab.PlayerCount), "participants after the end");
            });

            await context.StepAsync("the GM is given no win or lose outcome", async () =>
            {
                // Let the packets of the end of the match cross the loopback before looking for an outcome that did not come.
                await context.IdleAsync(TimeSpan.FromSeconds(2));
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                ScenarioContext.ExpectEqual(0, gm.Received(WorldOpcode.SmsgBattlefieldWin, gmMark).Length, "SMSG_BATTLEFIELD_WIN received by the GM");
                ScenarioContext.ExpectEqual(0, gm.Received(WorldOpcode.SmsgBattlefieldLose, gmMark).Length, "SMSG_BATTLEFIELD_LOSE received by the GM");
            });
        });
    }

    private static GameObjectMapSystem Objects(ScenarioBot bot) => bot.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!;

    private static bool Spawned(ScenarioBot bot, uint spawnGuid) => Objects(bot).GameObjects.Any(g => g.Spawn?.Guid == spawnGuid && g.IsSpawned);

    /// <summary>
    /// The bot casts Opening (21651) on the banner, as a client's click on a locked banner does once its cast bar ran (see
    /// ArathiBasinWorldScenarioTests.OpenBanner); also returns how often the banner was used once the cast returned.
    /// </summary>
    private static (SpellCastResult Result, uint Uses) OpenBanner(ScenarioContext context, ScenarioBot bot, uint spawnGuid)
    {
        GameObject banner = Objects(bot).GameObjects.Single(g => g.Spawn?.Guid == spawnGuid && g.IsSpawned);
        SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;
        SpellCastResult result = spells.CastSpell(bot.RequirePlayer(), ArathiBasinTestContent.OpeningSpell,
            new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = banner.Guid }, triggered: true);
        return (result, banner.UseCount);
    }
}
