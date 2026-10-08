using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// A <see cref="WorldTestHost"/> (real world thread and handlers, real clock) with managed playerbots on the host's own account and
/// character stores, so a socket client from <see cref="WorldTestHost.EnterWorldAsync"/> and a managed bot share one world.
/// </summary>
internal static class PartyTestHost
{
    /// <summary>The creature entry whose corpses hold <see cref="CorpseGold"/> copper (and nothing else): <see cref="Start"/>'s loot.</summary>
    public const uint GoldCreatureEntry = 990_400;

    public const uint CorpseGold = 25;

    public static WorldTestHost Start(Action<PlayerbotOptions>? configure = null)
        => WorldTestHost.Start(configureServices: services =>
        {
            services.AddScoped<ILootDataStore>(_ => new GoldLoot());
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

    public static PlayerbotOptions PlayerbotOptionsOf(WorldTestHost host) => host.WorldServices.GetRequiredService<IOptions<PlayerbotOptions>>().Value;

    /// <summary>
    /// A managed session in the world that is NOT one of the feature's bots (nothing ticks it), so a test can drive a
    /// <see cref="Party.PlayerbotPartyAI"/> by hand, one think at a time.
    /// </summary>
    public static async Task<WorldSession> EnterManagedAsync(WorldTestHost host, string account, string name)
    {
        Account owner = await host.Accounts.CreateAsync(new Account { Username = account, Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes,
            host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        var create = new PacketWriter();
        create.WriteCString(name);
        create.WriteByte(1);
        create.WriteByte(1);
        for (int i = 0; i < 8; i++) create.WriteByte(0);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        var login = new PacketWriter();
        login.WriteUInt64((ulong)(await session.Services.GetRequiredService<ICharacterStore>().GetByAccountAsync(owner.Id)).Single().Id);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => session.Player is { IsInWorld: true }, name + " logs in");
        return session;
    }

    private static readonly CreatureTemplate GoldTemplate = new()
    {
        Entry = GoldCreatureEntry, Name = "loot target", CreatureType = 1, MinLevelHealth = 20, MaxLevelHealth = 20,
    };

    /// <summary>World thread: a creature system for <see cref="GoldCreatureEntry"/> on <paramref name="near"/>'s map, updated with it.</summary>
    public static CreatureMapSystem GoldCreatures(Player near)
    {
        var system = new CreatureMapSystem(near.Map!, new CreatureContent([GoldTemplate], [], [], [], []), random: new Random(1));
        near.Map!.AddUpdater(system);
        return system;
    }

    /// <summary>
    /// World thread: a <see cref="GoldCreatureEntry"/> creature <paramref name="xOffset"/> yards from <paramref name="near"/>, killed at
    /// once by <paramref name="killer"/> (its whole health as damage, the ordinary kill path); returns the corpse.
    /// </summary>
    public static Creature KillGoldCreature(CreatureMapSystem system, Player near, float xOffset, Player killer, float yOffset = 0f)
    {
        Creature creature = system.SpawnTemporary(GoldTemplate, near.X + xOffset, near.Y + yOffset, near.Z, 0);
        killer.Map!.Combat.DealDamage(killer, creature, creature.Health, direct: false);
        if (creature.DeathState != CreatureDeathState.Corpse) throw new InvalidOperationException("the creature did not die");
        return creature;
    }

    /// <summary>
    /// Flat open ground at <paramref name="z"/> everywhere (the test maps have no terrain), so a bot can plan its walks: the planner
    /// refuses a no-mmap route without heights.
    /// </summary>
    public static Task InstallFlatGroundAsync(WorldTestHost host, float z) => host.World.InvokeAsync(() =>
    {
        WorldCollision.Of(host.World).Install(lineOfSight: new FlatGround(z));
        return true;
    });

    private sealed class FlatGround(float z) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z2, float maxSearchDistance) => z;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z2, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    private sealed class GoldLoot : ILootDataStore
    {
        public Task<LootContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LootContent([], [new CreatureLootInfo(GoldCreatureEntry, 0, 0, CorpseGold, CorpseGold)]));
    }

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
