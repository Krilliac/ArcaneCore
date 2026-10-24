using ArcaneCore.Game;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Events;
using ArcaneCore.World.Tests.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Party;
using ArcaneCore.World.Tests.Playerbots.Social;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// Shared set-up of the chat-side P7a world tests: the social test world (frozen social feature, hand-built passes, a fake model client)
/// with the festival game events of <see cref="FestivalContent"/>, festival claims put on the board by hand for bots that stand where
/// they are (the board and the brains decide who claims what; these tests are about what the events lane and the initiative say and do
/// once a bot holds a spot), and the helpers to read what a real client received.
/// </summary>
internal static class EventsSocialKit
{
    internal const string Town = "town:test";

    /// <summary>Caps wide enough that a test decides who is held, not the crowd limits.</summary>
    internal static readonly PlayerbotFestivalCaps WideCaps = new(PerGossipAnchor: 50, PerServiceAnchor: 50, PerTown: 200);

    internal static Task<SocialWorld> StartAsync(FakeBotChatClient client, IReadOnlyList<FestivalNpc>? npcs = null, Action<PlayerbotOptions>? configure = null)
    {
        var content = new FestivalContent(npcs ?? []);
        return SocialWorld.StartAsync(options =>
        {
            options.Chat.Enabled = true;
            options.Chat.PerPlayerCooldownSeconds = 0;
            options.Chat.Safety.RequireOptIn = false;
            ChatSamples.NoScreeningDeadline(options.Chat);
            options.Chat.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_EVENTS_KEY" }];
            configure?.Invoke(options);
        }, frozen: true, services: services =>
        {
            services.AddSingleton<IBotChatClient>(client);
            services.AddSingleton<IBotChatEnvironment>(new FakeBotChatEnvironment(new() { ["TEST_EVENTS_KEY"] = "sk-events" }));
            services.AddSingleton<ICreatureDataStore>(content);
            services.AddSingleton<IQuestContentStore>(content);
            services.AddSingleton<IGameEventDataStore>(content);
        });
    }

    internal static async Task StartEventAsync(SocialWorld world, ushort id)
    {
        GameEventFeature events = world.Host.WorldServices.GetRequiredService<GameEventFeature>();
        await world.Host.WaitForWorldAsync(() => events.Service is { IsInitialised: true }, "the game events to initialise");
        await world.Host.OnWorldAsync(() => events.Service!.StartEvent(id, overwrite: true));
    }

    internal static async Task StopEventAsync(SocialWorld world, ushort id)
    {
        GameEventFeature events = world.Host.WorldServices.GetRequiredService<GameEventFeature>();
        await world.Host.OnWorldAsync(() => events.Service!.StopEvent(id, overwrite: true));
    }

    internal static PlayerbotWorldEventBoard Board(SocialWorld world) => PartyTestHost.Feature(world.Host).EventBoard;

    internal static PlayerbotEventLane Lane(SocialWorld world) => SocialTestKit.Feature(world.Host).Events;

    /// <summary>
    /// The bot takes a place at an anchor of the festival <paramref name="root"/> and holds it where it stands (its spot is its own
    /// position), as a new arrival: a claim it already held is released first. World thread; true when the board granted it.
    /// </summary>
    internal static Task<bool> ClaimAsync(SocialWorld world, string bot, ushort root, long nowMs, ulong anchorGuid = 5000, PlayerbotAnchorKind kind = PlayerbotAnchorKind.Gossip)
        => world.Host.OnWorldAsync(() =>
        {
            Player player = HelpTestKit.Online(world, bot);
            Guid id = world.Bots[bot];
            var spot = new Vector3(player.X, player.Y, player.Z);
            var anchor = new PlayerbotFestivalAnchor(anchorGuid, 15310, spot + new Vector3(10f, 0f, 0f), kind, root);
            Board(world).Claims.Release(id);
            return Board(world).Claims.TryClaim(anchor, spot, id, Town, (uint)player.Team, WideCaps, nowMs);
        });

    /// <summary>A held think of every bot that holds a claim: each renews its own.</summary>
    internal static Task RenewAllAsync(SocialWorld world, long nowMs)
        => world.Host.OnWorldAsync(() =>
        {
            foreach (Guid id in world.Bots.Values) Board(world).Claims.Renew(id, nowMs);
        });

    /// <summary>One pass of the events lane at world time <paramref name="nowMs"/> over the running bots.</summary>
    internal static Task RunLaneAsync(SocialWorld world, long nowMs)
        => world.Host.OnWorldAsync(() => Lane(world).Run(SocialTestKit.BotPass(world.Host, nowMs)));

    /// <summary>One pass of the initiative lane at world time <paramref name="nowMs"/>.</summary>
    internal static Task RunInitiativeAsync(SocialWorld world, long nowMs)
        => world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Initiative.Run(SocialTestKit.BotPass(world.Host, nowMs)));

    /// <summary>Every /say line the client has received so far (waits a quiet moment for the chat worker's lines to arrive).</summary>
    internal static async Task<List<ChatMessage>> SaysAsync(WorldTestClient client, TimeSpan? quiet = null)
        => [.. (await client.CollectAsync(quiet ?? TimeSpan.FromMilliseconds(400))).Where(p => p.Opcode == WorldOpcode.SmsgMessagechat)
            .Select(p => ChatMessage.Parse(p.Payload)).Where(m => m.Type == ChatType.Say)];

    /// <summary>The next /say line from <paramref name="sender"/> (a guid), or a failure after the client's read timeout.</summary>
    internal static async Task<ChatMessage> SayFromAsync(WorldTestClient client, ulong sender)
    {
        while (true)
        {
            ChatMessage line = await client.ReadChatAsync();
            if (line.Type == ChatType.Say && line.Sender == sender) return line;
        }
    }

    /// <summary>Whether an SMSG_TEXT_EMOTE payload names a target (vmangos EmoteChatBuilder: u64 emoter, u32 emote, u32 number, u32 name length incl. NUL).</summary>
    internal static bool TextEmoteHasTarget(byte[] payload) => BitConverter.ToUInt32(payload, 16) > 1;

    internal static ulong TextEmoteSpeaker(byte[] payload) => BitConverter.ToUInt64(payload, 0);

    /// <summary>The player's strikes: one flagged line from them with a cut-off after one strike makes the chat safety cut them off.</summary>
    internal static async Task CutOffAsync(SocialWorld world, string player)
    {
        ObjectGuid guid = await world.Host.PlayerStateAsync(player, p => p.Guid);
        Assert.Equal(BotChatAdmission.Queued, PartyTestHost.Feature(world.Host).Chat!.TryAsk(ChatSamples.Ask("kys", sender: guid.Value, name: player)));
        await HelpTestKit.UntilAsync(() => PartyTestHost.Feature(world.Host).Chat!.Safety.Player(player)?.CutoffSeconds > 0, player + " to be cut off");
    }

    /// <summary>The player says "ai off" to the chat service.</summary>
    internal static async Task AiOffAsync(SocialWorld world, string player)
    {
        ObjectGuid guid = await world.Host.PlayerStateAsync(player, p => p.Guid);
        Assert.Equal(BotChatAdmission.Choice, PartyTestHost.Feature(world.Host).Chat!.TryAsk(ChatSamples.Ask("ai off", sender: guid.Value, name: player)));
        Assert.Equal(false, PartyTestHost.Feature(world.Host).Chat!.Safety.Player(player)!.AiReplies);
    }
}
