using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// A <see cref="WorldTestHost"/> (real world thread and handlers, real clock) with managed playerbots on the host's own account and
/// character stores, so a socket client from <see cref="WorldTestHost.EnterWorldAsync"/> and a managed bot share one world.
/// </summary>
internal static class PartyTestHost
{
    public static WorldTestHost Start(Action<PlayerbotOptions>? configure = null)
        => WorldTestHost.Start(configureServices: services =>
        {
            var options = new PlayerbotOptions
            {
                Enabled = true, MaxBots = 4, ThinkIntervalMs = 100, MaxActionsPerTick = 8, MaxPathPoints = 64, MaxRouteYards = 200,
                AllowedMaps = [0, 1],
            };
            configure?.Invoke(options);
            services.AddSingleton<IManagedPlayerbotStore>(new MemoryBotStore());
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(options));
            services.AddSingleton<IManagedPlayerbotProvisionStore>(sp => new MemoryProvisionStore(sp.GetRequiredService<IAccountStore>()));
        });

    /// <summary>Create and start an autonomous bot; returns its id once it is in the world.</summary>
    public static async Task<Guid> StartBotAsync(WorldTestHost host, string name)
    {
        ManagedPlayerbotFeature bots = Feature(host);
        await bots.StartupAsync(default);
        PlayerbotOperationResult created = await bots.CreateAsync(name, 1, 1);
        if (!created.Success || created.BotId is not { } id) throw new InvalidOperationException("create: " + created.Code);
        PlayerbotOperationResult started = await bots.StartAsync(id.ToString());
        if (!started.Success) throw new InvalidOperationException("start: " + started.Code);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name) is { IsInWorld: true }, name + " logs in");
        return id;
    }

    public static ManagedPlayerbotFeature Feature(WorldTestHost host) => host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();

    public static byte[] CString(string text)
    {
        var writer = new PacketWriter(text.Length + 1);
        writer.WriteCString(text);
        return writer.ToArray();
    }

    /// <summary>Read until the bot's whisper to this client arrives (decoded), skipping everything else.</summary>
    public static async Task<ChatMessage> ReadWhisperFromAsync(WorldTestClient client, ulong bot)
    {
        while (true)
        {
            ChatMessage line = ChatMessage.Parse(await client.ReadUntilAsync(WorldOpcode.SmsgMessagechat));
            if (line.Type == ChatType.Whisper && line.Sender == bot) return line;
        }
    }

    private sealed class MemoryProvisionStore(IAccountStore accounts) : IManagedPlayerbotProvisionStore
    {
        private readonly ConcurrentDictionary<Guid, ManagedPlayerbotProvision> _pending = new();

        public async Task<Account> CreateAsync(Guid botId, Account account, CancellationToken cancellationToken = default)
        {
            Account created = await accounts.CreateAsync(account, cancellationToken);
            _pending[botId] = new ManagedPlayerbotProvision(botId, created.Id, created.Username, 1);
            return created;
        }

        public Task<IReadOnlyList<ManagedPlayerbotProvision>> LoadPendingAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ManagedPlayerbotProvision>>([.. _pending.Values]);

        public Task<bool> CompleteAsync(Guid botId, int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(_pending.TryRemove(botId, out ManagedPlayerbotProvision? proof) && proof.AccountId == accountId);

        public Task<bool> RollbackEmptyOwnerAsync(Guid botId, int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(_pending.TryRemove(botId, out ManagedPlayerbotProvision? proof) && proof.AccountId == accountId);
    }

    private sealed class MemoryBotStore : IManagedPlayerbotStore
    {
        private readonly ConcurrentDictionary<Guid, ManagedPlayerbot> _items = new();

        public Task<IReadOnlyList<ManagedPlayerbot>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ManagedPlayerbot>>([.. _items.Values.OrderBy(item => item.BotId)]);

        public Task<ManagedPlayerbot?> FindAsync(Guid botId, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.GetValueOrDefault(botId));

        public Task CreateAsync(ManagedPlayerbot bot, CancellationToken cancellationToken = default)
            => _items.TryAdd(bot.BotId, bot) ? Task.CompletedTask : throw new InvalidOperationException("duplicate-owner");

        public Task<bool> UpdateAsync(ManagedPlayerbot bot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            while (_items.TryGetValue(bot.BotId, out ManagedPlayerbot? current))
            {
                if (current.Revision != expectedRevision) return Task.FromResult(false);
                if (_items.TryUpdate(bot.BotId, bot, current)) return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }
}
