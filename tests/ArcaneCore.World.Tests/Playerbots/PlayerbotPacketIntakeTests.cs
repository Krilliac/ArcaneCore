using System.Text.RegularExpressions;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Tests.Docs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// <c>World:Playerbots:ClientFidelity</c>: a lean bot session keeps only the opcodes some bot code reads back
/// (<see cref="PlayerbotPacketIntake"/>), its map viewer builds no update-object blocks, and Full or an attached observer keeps everything.
/// </summary>
public sealed class PlayerbotPacketIntakeTests
{
    private static readonly byte[] LootResponse = [1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] Notification = [3, 0];

    [Fact]
    public void EveryOpcodeABotReadsBackIsInTheDeclaredSet()
    {
        const string call = "DrainManagedPackets(";
        string root = Path.Combine(RepoRoot.Find(), "src", "ArcaneCore.Playerbots");
        int calls = 0;
        var read = new SortedSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            for (int at = text.IndexOf(call, StringComparison.Ordinal); at >= 0; at = text.IndexOf(call, at + 1, StringComparison.Ordinal))
            {
                int open = at + call.Length;
                int depth = 1, end = open;
                while (depth > 0) depth += text[end++] switch { '(' => 1, ')' => -1, _ => 0 };
                string args = text[open..(end - 1)].Trim();
                if (args.Length == 0) continue; // drains everything: a flush of the bounded queue, not a read of one opcode
                calls++;
                string rest = Regex.Replace(args, @"WorldOpcode\.(\w+)", m => { read.Add(m.Groups[1].Value); return string.Empty; });
                rest = rest.Replace("PlayerbotSocialPackets.IntakeOpcodes", string.Empty).Replace("IntakeOpcodes", string.Empty);
                if (Regex.IsMatch(rest, "[A-Za-z_]"))
                    problems.Add($"{Path.GetFileName(file)}: DrainManagedPackets({args}) names an opcode source this test cannot read");
            }
        }

        Assert.True(calls >= 20, $"the scan found only {calls} reader call sites; it is looking in the wrong place");
        Assert.Empty(problems);
        foreach (string name in read)
            Assert.True(PlayerbotPacketIntake.Contains(Enum.Parse<WorldOpcode>(name)), $"{name} is read back by a bot but missing from PlayerbotPacketIntake.RingOpcodes");
        foreach (WorldOpcode opcode in PlayerbotPartyAI.IntakeOpcodes.Concat(PlayerbotSocialPackets.IntakeOpcodes))
            Assert.True(PlayerbotPacketIntake.Contains(opcode), opcode + " (an IntakeOpcodes entry) is missing");
        Assert.False(PlayerbotPacketIntake.Contains(WorldOpcode.SmsgUpdateObject));
        Assert.False(PlayerbotPacketIntake.Contains(WorldOpcode.SmsgCompressedUpdateObject));
    }

    [Fact]
    public async Task Lean_KeepsOnlyTheReadBackOpcodes_AndCountsWhatItSkipped()
    {
        await using WorldTestHost host = BotFidelity.Start(PlayerbotClientFidelity.Lean);
        WorldSession bot = await EnterAsync(host, "LEANBOT", "Leanone");
        try
        {
            Assert.False(bot.WantsUpdateObjects);
            bot.DrainManagedPackets();
            long skippedBefore = bot.LeanRingPacketsSkipped; // per session: BotLeanStats.RingPackets is process-wide and other tests' lean bots bump it
            bot.Send(WorldOpcode.SmsgNotification, Notification);
            bot.Send(WorldOpcode.SmsgLootResponse, LootResponse);
            Assert.Equal(skippedBefore + 1, bot.LeanRingPacketsSkipped); // the notification, and only it, was counted as skipped
            Assert.Equal(WorldOpcode.SmsgLootResponse, Assert.Single(bot.DrainManagedPackets()).Opcode);

            // An observer sees everything, and the map builds update blocks for the bot again.
            // The map tick runs on its own thread and, now that the bot wants update objects, may deliver one at any point: look only at the opcodes this test sends.
            var seen = new List<WorldOpcode>();
            bot.ManagedPacketObserver = (opcode, _) => { lock (seen) seen.Add(opcode); };
            Assert.True(bot.WantsUpdateObjects);
            bot.Send(WorldOpcode.SmsgNotification, Notification);
            lock (seen) Assert.Equal([WorldOpcode.SmsgNotification], seen.Where(o => o is not (WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgCompressedUpdateObject)));
            Assert.Equal(WorldOpcode.SmsgNotification, Assert.Single(bot.DrainManagedPackets(WorldOpcode.SmsgNotification)).Opcode);
        }
        finally { bot.Kick(); await bot.ManagedClosed; }
    }

    [Fact]
    public async Task Full_KeepsEveryPacketLikeARealClientDoes()
    {
        await using WorldTestHost host = BotFidelity.Start(PlayerbotClientFidelity.Full);
        WorldSession bot = await EnterAsync(host, "FULLBOT", "Fullone");
        try
        {
            Assert.True(bot.WantsUpdateObjects);
            bot.DrainManagedPackets();
            bot.Send(WorldOpcode.SmsgNotification, Notification);
            bot.Send(WorldOpcode.SmsgLootResponse, LootResponse);
            Assert.Equal([WorldOpcode.SmsgNotification, WorldOpcode.SmsgLootResponse], bot.DrainManagedPackets().Select(p => p.Opcode));
        }
        finally { bot.Kick(); await bot.ManagedClosed; }
    }

    /// <summary>Two bots meet: the watcher's visible set is the same in both modes; only Full builds and rings the update packets.</summary>
    [Theory]
    [InlineData(PlayerbotClientFidelity.Lean)]
    [InlineData(PlayerbotClientFidelity.Full)]
    public async Task ABotInViewOfAnotherPlayerSeesItInBothModes_ButOnlyFullBuildsUpdatePackets(PlayerbotClientFidelity fidelity)
    {
        await using WorldTestHost host = BotFidelity.Start(fidelity);
        long blocksBefore = BotLeanStats.UpdateBlocks;
        WorldSession watcher = await EnterAsync(host, "WATCHERBOT", "Watchera");
        WorldSession other = await EnterAsync(host, "OTHERBOT", "Otherb");
        try
        {
            await host.WaitForWorldAsync(() => watcher.Player!.VisibleObjects.Contains(other.Player!.Guid), "watcher sees the other bot");
            int updatePackets = watcher.DrainManagedPackets(WorldOpcode.SmsgUpdateObject, WorldOpcode.SmsgCompressedUpdateObject).Count;
            if (fidelity == PlayerbotClientFidelity.Full)
            {
                Assert.True(updatePackets > 0, "Full builds the create block");
            }
            else
            {
                Assert.Equal(0, updatePackets);
                Assert.True(BotLeanStats.UpdateBlocks > blocksBefore, "Lean counts the blocks it did not build");
            }
        }
        finally
        {
            watcher.Kick(); other.Kick();
            await watcher.ManagedClosed; await other.ManagedClosed;
        }
    }

    [Fact]
    public async Task ARealPlayerSessionAlwaysWantsUpdateObjects_UnderLean()
    {
        await using WorldTestHost host = BotFidelity.Start(PlayerbotClientFidelity.Lean);
        await using WorldTestClient client = await host.EnterWorldAsync("REALONE", "Realone");
        int accountId = (await host.Accounts.FindByUsernameAsync("REALONE"))!.Id;
        Assert.True(host.Registry.Find(accountId)!.WantsUpdateObjects);
    }

    private static async Task<WorldSession> EnterAsync(WorldTestHost host, string username, string character)
    {
        Account owner = await host.Accounts.CreateAsync(new Account { Username = username, Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes,
            host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        var create = new PacketWriter(); create.WriteCString(character); create.WriteByte(1); create.WriteByte(1);
        for (int i = 0; i < 8; i++) create.WriteByte(0);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        var login = new PacketWriter();
        login.WriteUInt64((ulong)(await session.Services.GetRequiredService<ICharacterStore>().GetByAccountAsync(owner.Id)).Single().Id);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => session.Player is not null, character + " login");
        return session;
    }
}
