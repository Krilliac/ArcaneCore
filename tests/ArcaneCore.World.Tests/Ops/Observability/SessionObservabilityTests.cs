using System.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.Observability;

/// <summary>
/// The observability hooks driven through a real <see cref="WorldSession"/>: <c>Send</c> -> <c>ObserveOutbound</c> for refusals and
/// duplicates, and the handlers' "did I answer" comparisons (docs/ops/observability.md).
/// </summary>
public sealed class SessionObservabilityTests
{
    private sealed class Capture : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add((logLevel, formatter(state, exception)));
            }
        }

        public (LogLevel Level, string Message)[] Snapshot()
        {
            lock (Lines)
            {
                return [.. Lines];
            }
        }

        public string[] Messages() => [.. Snapshot().Select(l => l.Message)];
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Capture Rejections { get; } = new();

        public Capture Duplicates { get; } = new();

        public WorldTestHost Host { get; }

        public Rig(bool includeBots = false)
        {
            Host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton(new ClientRejectionLog(new ClientRejectionOptions(), Rejections));
                services.AddSingleton(new DuplicatePacketDetector(new DuplicatePacketOptions { IncludeBots = includeBots }, Duplicates, Stopwatch.GetTimestamp));
            });
        }

        public async Task<(WorldTestClient Client, WorldSession Session)> EnterAsync(string account, string name)
        {
            WorldTestClient client = await Host.EnterWorldAsync(account, name);
            Player player = await Host.PlayerAsync(name);
            return (client, (WorldSession)player.Session);
        }

        public async Task<WorldSession> NewBotAsync(string username)
        {
            Account owner = await Host.Accounts.CreateAsync(new Account { Username = username, Salt = new byte[32], Verifier = new byte[32] });
            return await WorldSession.CreateManagedAsync(owner, null, Host.WorldServices, Host.Opcodes, Host.World, Host.Registry,
                new WorldSessionOptions(), NullLogger.Instance);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static byte[] ItemPush(uint entry)
    {
        var w = new PacketWriter(45);
        w.WriteUInt64(0x1234);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(1);
        w.WriteByte(255);
        w.WriteUInt32(0xFFFFFFFF);
        w.WriteUInt32(entry);
        w.WriteUInt32(0);
        w.WriteInt32(0);
        w.WriteUInt32(1);
        return w.ToArray();
    }

    private static (LogLevel Level, string Message)[] DuplicateLines(Rig rig)
        => [.. rig.Duplicates.Snapshot().Where(l => l.Message.Contains("SMSG_ITEM_PUSH_RESULT", StringComparison.Ordinal))];

    [Fact]
    public async Task APlayerSessionSendingTheSamePacketTwice_LogsADuplicateWarning_WithTheAccountAndStacks()
    {
        await using var rig = new Rig();
        (WorldTestClient client, WorldSession session) = await rig.EnterAsync("OBSDUPA", "Obsdupaa");
        await using WorldTestClient _ = client;
        byte[] payload = ItemPush(9100);

        session.Send(WorldOpcode.SmsgItemPushResult, payload);
        session.Send(WorldOpcode.SmsgItemPushResult, payload);

        (LogLevel level, string line) = Assert.Single(DuplicateLines(rig));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("account " + session.AccountId, line);
        Assert.Contains("second send:", line);
        Assert.True(session.HasDuplicateState);
    }

    [Fact]
    public async Task AManagedBotSession_IsNeverHashed_WhileBotsAreExcluded_ButIsWhenIncluded()
    {
        await using (var excluded = new Rig())
        {
            WorldSession bot = await excluded.NewBotAsync("OBSBOTA");
            byte[] payload = ItemPush(9200);

            bot.Send(WorldOpcode.SmsgItemPushResult, payload);
            bot.Send(WorldOpcode.SmsgItemPushResult, payload);

            Assert.True(bot.IsManaged);
            Assert.Empty(DuplicateLines(excluded));
            Assert.False(bot.HasDuplicateState); // not even the ring is allocated
            bot.Kick();
        }

        await using (var included = new Rig(includeBots: true))
        {
            WorldSession bot = await included.NewBotAsync("OBSBOTB");
            byte[] payload = ItemPush(9201);

            bot.Send(WorldOpcode.SmsgItemPushResult, payload);
            bot.Send(WorldOpcode.SmsgItemPushResult, payload);

            Assert.Single(DuplicateLines(included)); // the control: the same packets are caught once bots are included
            bot.Kick();
        }
    }

    [Fact]
    public async Task ARefusedCmsgLoot_IsAnsweredByTheReleaseResponse_AndLogsNoSilentDrop()
    {
        await using var rig = new Rig();
        (WorldTestClient client, WorldSession session) = await rig.EnterAsync("OBSLOOT", "Obslootaa");
        await using WorldTestClient _ = client;
        int answersBefore = session.ClientLootAnswersSent;

        await client.SendAsync(WorldOpcode.CmsgLoot, BitConverter.GetBytes(0xF1300000DEADBEEFUL)); // no such corpse: LootService.Open refuses
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);
        await rig.Host.OnWorldAsync(() => 0); // the handler has returned (it runs inside a tick; this runs between ticks)

        Assert.Equal(answersBefore + 1, session.ClientLootAnswersSent);
        Assert.DoesNotContain(rig.Rejections.Messages(), m => m.Contains("CMSG_LOOT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AShortCmsgLoot_StillLogsTheSilentDrop_SoTheAbsenceAboveIsMeaningful()
    {
        await using var rig = new Rig();
        (WorldTestClient client, WorldSession _) = await rig.EnterAsync("OBSLOOS", "Obslootbb");
        await using WorldTestClient __ = client;

        await client.SendAsync(WorldOpcode.CmsgLoot, [1, 2, 3, 4]);
        await rig.Host.WaitForWorldAsync(
            () => rig.Rejections.Messages().Any(m => m.Contains("CMSG_LOOT", StringComparison.Ordinal) && m.Contains("short payload", StringComparison.Ordinal)),
            "the short-payload silent-drop line");
    }

    [Fact]
    public async Task TheSpellHandlersAnsweredComparison_CountsAFailedCastResultOnly()
    {
        await using var rig = new Rig();
        (WorldTestClient client, WorldSession session) = await rig.EnterAsync("OBSSPEL", "Obsspellaa");
        await using WorldTestClient _ = client;

        int start = session.ClientRejectionsSent;
        session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(6478, SpellCastResult.CastOk));
        Assert.Equal(start, session.ClientRejectionsSent); // a success is not an answer to a refusal
        session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(6478, SpellCastResult.BadTargets));
        Assert.Equal(start + 1, session.ClientRejectionsSent);
        Assert.Contains(rig.Rejections.Messages(), m => m.Contains("BadTargets", StringComparison.Ordinal));

        // End to end: a cast of a spell that does not exist is refused without any reply, so the handler logs the silent drop.
        await client.SendAsync(WorldOpcode.CmsgCastSpell, [0xFF, 0xFF, 0xFF, 0x7F, 0, 0]);
        await rig.Host.WaitForWorldAsync(
            () => rig.Rejections.Messages().Any(m => m.Contains("cast refused without a reply: NotFound", StringComparison.Ordinal)),
            "the silent-drop line of the unanswered cast");
    }
}
