using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.ServerMail;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Reputation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.ServerMail;

/// <summary>Configuration section "ServerMail".</summary>
public sealed class ServerMailOptions
{
    public const string SectionName = "ServerMail";

    /// <summary>Send the mail_server_template letters at login (default on; with empty tables nothing is sent).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// AzerothCore's server mail (ServerMailMgr + scripts/World/server_mail.cpp ServerMailReward::OnPlayerLogin): at every login, each active
/// template the character has not had yet and whose every condition it meets is mailed to it once (the faction's money and items), and
/// mail_server_character records it. A letter whose conditions fail stays owed for a later login. The templates load at start.
/// </summary>
public sealed class ServerMailFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<ServerMailFeature> logger) : IWorldFeature
{
    private WorldRuntime? _world;

    public ServerMailOptions Options { get; } = Bind(services.GetService<IConfiguration>());

    public IReadOnlyList<ServerMailTemplate> Templates { get; private set; } = [];

    public static ServerMailOptions Bind(IConfiguration? configuration)
    {
        var options = new ServerMailOptions();
        configuration?.GetSection(ServerMailOptions.SectionName).Bind(options);
        return options;
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!Options.Enabled) return;
        _world = world;
        _ = Task.Run(LoadAsync);
        world.PlayerLoggedIn += OnLoggedIn;
    }

    private async Task LoadAsync()
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<IServerMailStore>() is not { } store) return;
            ServerMailContent content = await store.LoadAsync().ConfigureAwait(false);
            _world?.Post(() =>
            {
                Templates = ServerMailRules.Load(content, new Validation(services), message => logger.LogError("{Message}", message));
                logger.LogInformation("Loaded {Count} server mail templates", Templates.Count);
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "could not load the server mail templates");
        }
    }

    private void OnLoggedIn(Player player)
    {
        if (Templates.Count == 0 || _world is not { } world) return;
        int id = (int)player.Guid.Low;
        _ = Task.Run(async () =>
        {
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                if (scope.ServiceProvider.GetService<IServerMailStore>() is not { } store) return;
                IReadOnlyCollection<uint> sent = await store.GetSentAsync(id).ConfigureAwait(false);
                world.Post(() => SendOwed(world, player, sent));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "could not read the server mail sent to character {Id}", id);
            }
        });
    }

    private void SendOwed(WorldRuntime world, Player player, IReadOnlyCollection<uint> sent)
    {
        // AzerothCore's callback: only while the same character is still in the world.
        if (world.FindOnlinePlayer(player.Guid) is not { } online || !ReferenceEquals(online, player)) return;
        if (services.GetService<EconomyFeature>() is not { } economy) return;
        int id = (int)player.Guid.Low;
        foreach (ServerMailTemplate template in ServerMailRules.Owed(Templates, sent, new Facts(player, world, services)).ToList())
        {
            economy.SendServerMail(ServerMailRules.Letter(template, id, player.Team), ok =>
            {
                if (!ok)
                {
                    logger.LogWarning("server mail {Template} to character {Id} was not sent; it stays owed", template.Id, id);
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using IServiceScope scope = scopes.CreateScope();
                        if (scope.ServiceProvider.GetService<IServerMailStore>() is { } store)
                            await store.MarkSentAsync(id, template.Id).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "could not record server mail {Template} sent to character {Id}", template.Id, id);
                    }
                });
            });
        }
    }

    private sealed class Facts(Player player, WorldRuntime world, IServiceProvider services) : IServerMailFacts
    {
        public uint Level => player.Level;
        public uint PlayedSeconds => player.PlayedTimeAt(world.NowMs);
        public Team Team => player.Team;
        public Race Race => player.Race;
        public Class Class => player.Class;

        public (QuestStatus Status, bool Rewarded)? Quest(uint questId)
        {
            if (services.GetService<QuestNpcFeature>()?.Services.StateOf(player) is not { } state) return null;
            return state.Quests.Get(questId) is { } data ? (data.Status, data.Rewarded) : (QuestStatus.None, false);
        }

        public ReputationRank? Rank(uint factionId) => services.GetService<ReputationFeature>()?.Reputation.GetRank(player, factionId);
    }

    private sealed class Validation(IServiceProvider services) : IServerMailValidation
    {
        public uint MaxLevel => 60;

        public bool CreatureExists(uint entry) => services.GetService<CreatureContent>() is not { } creatures || creatures.FindTemplate(entry) is not null;

        public (uint Stackable, uint MaxCount)? Item(uint entry)
            => services.GetService<EconomyFeature>()?.Templates.Find(entry) is { } t ? (Math.Max(1u, t.Stackable), t.MaxCount) : null;

        // The quest catalogue is not reachable here; an unknown quest simply never matches.
        public bool QuestExists(uint questId) => questId != 0;

        public bool FactionExists(uint factionId)
            => services.GetService<ReputationFeature>()?.Service.Factions is not { Count: > 0 } factions || factions.All.Any(f => f.Id == factionId);
    }
}
