using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Protocol;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Scripting;

/// <summary>
/// IPlayerHooks.OnAddonMessage at the ChatHandlers addon branch. The client sends CMSG_MESSAGECHAT with language 0xFFFFFFFF and the
/// message "prefix\tbody" (Wow.exe 5875 SendAddonMessage 0x49F9A2, format "%s\t%s").
/// </summary>
public sealed class AddonChatHookTests
{
    private sealed class AddonProbe(bool allow) : IWorldFeature, IPlayerHooks
    {
        public ConcurrentQueue<ScriptAddonMessage> Seen { get; } = new();

        public void Attach(WorldRuntime world) => world.Scripts.Register(this);

        public bool OnAddonMessage(Player player, ScriptAddonMessage message)
        {
            Seen.Enqueue(message);
            return allow;
        }
    }

    /// <summary>Counts the addon lines that reach the <see cref="IChatMessageHandler"/> seam and consumes none.</summary>
    private sealed class ChatMessageSpy : IChatMessageHandler
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        public bool TryHandle(WorldSession session, Player player, ClientChatMessage message)
        {
            if (message.Language == Language.Addon)
            {
                Interlocked.Increment(ref _count);
            }

            return false;
        }
    }

    private static WorldTestHost StartHost(AddonProbe probe, ChatMessageSpy spy) => WorldTestHost.Start(configureServices: services =>
    {
        services.AddSingleton<IWorldFeature>(probe);
        services.AddSingleton<IChatMessageHandler>(spy);
    });

    [Fact]
    public async Task AddonMessage_ReachesTheHook_WithChatTypePrefixAndText()
    {
        var probe = new AddonProbe(allow: true);
        var spy = new ChatMessageSpy();
        await using WorldTestHost host = StartHost(probe, spy);
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await human.CollectAsync();

        await human.SendChatAsync(ChatType.Battleground, Language.Addon, "PFX\tbody");
        await human.SendChatAsync(ChatType.Party, Language.Addon, "no tab");
        await host.WaitForWorldAsync(() => probe.Seen.Count == 2, "both addon lines at the hook");

        ScriptAddonMessage[] seen = [.. probe.Seen];
        Assert.Equal(new ScriptAddonMessage(0x5C, "PFX", "body", null), seen[0]);
        Assert.Equal(new ScriptAddonMessage(0x01, null, "no tab", null), seen[1]);
        // SocialFeature (ahead of the spy in feature order) consumes the Party line; only the Battleground line is unserved and reaches the spy.
        await host.WaitForWorldAsync(() => spy.Count == 1, "the Battleground line offered to the features");
    }

    [Fact]
    public async Task AHookReturningFalse_KeepsTheLineFromTheFeatures()
    {
        var probe = new AddonProbe(allow: false);
        var spy = new ChatMessageSpy();
        await using WorldTestHost host = StartHost(probe, spy);
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await human.CollectAsync();

        await human.SendChatAsync(ChatType.Battleground, Language.Addon, "PFX\tbody");
        await human.SendChatAsync(ChatType.Say, Language.Common, "sync"); // a later message proves the addon line was handled
        await human.ReadChatAsync();

        Assert.Single(probe.Seen);
        Assert.Equal(0, spy.Count);
    }

    [Fact]
    public async Task AddonChannelOff_TheHookIsNotCalled()
    {
        var probe = new AddonProbe(allow: true);
        var spy = new ChatMessageSpy();
        await using WorldTestHost host = StartHost(probe, spy);
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await human.CollectAsync();
        host.WorldServices.GetRequiredService<ChatFeature>().Options.AddonChannel = false;

        await human.SendChatAsync(ChatType.Battleground, Language.Addon, "PFX\tbody");
        await human.SendChatAsync(ChatType.Say, Language.Common, "sync");
        await human.ReadChatAsync();

        Assert.Empty(probe.Seen);
        Assert.Equal(0, spy.Count);
    }

    [Fact]
    public async Task OrdinaryChat_DoesNotReachTheAddonHook()
    {
        var probe = new AddonProbe(allow: true);
        var spy = new ChatMessageSpy();
        await using WorldTestHost host = StartHost(probe, spy);
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await human.CollectAsync();

        await human.SendChatAsync(ChatType.Say, Language.Common, "PFX\tbody");
        await human.ReadChatAsync();

        Assert.Empty(probe.Seen);
    }
}
