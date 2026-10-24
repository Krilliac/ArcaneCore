using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Playerbots.Party;
using ArcaneCore.World.Tests.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>Every client opcode the bots' managed actions dispatched to a handler (<see cref="WorldSession.ManagedDispatchObserver"/>), by bot name.</summary>
internal sealed class DispatchLog
{
    private readonly ConcurrentQueue<(string Bot, WorldOpcode Opcode)> _entries = new();

    public void Add(string bot, WorldOpcode opcode) => _entries.Enqueue((bot, opcode));

    public int Count(WorldOpcode opcode) => _entries.Count(entry => entry.Opcode == opcode);

    public int Count(string bot, WorldOpcode opcode)
        => _entries.Count(entry => entry.Opcode == opcode && string.Equals(entry.Bot, bot, StringComparison.OrdinalIgnoreCase));

    /// <summary>The bots that dispatched <paramref name="opcode"/>, in order.</summary>
    public IReadOnlyList<string> Who(WorldOpcode opcode) => [.. _entries.Where(entry => entry.Opcode == opcode).Select(entry => entry.Bot)];
}

/// <summary>Shared set-up of the bot social tests: a host with the guild system loaded, bots of a chosen race, and a hand-built pass.</summary>
internal static class SocialTestKit
{
    /// <summary>Race ids of the 1.12 character-creation packet (human = 1, orc = 2).</summary>
    public const byte Human = 1, Orc = 2;

    /// <summary>The guild charter item of the 1.12 client (vmangos item 5863).</summary>
    public const uint CharterItem = 5863;

    /// <summary>
    /// A world host with managed bots (chat off, party invites from anyone off) and the social system loaded. Bots walk at a crawl, so
    /// the ones a test places stay where they are. With <paramref name="petitioner"/> a petitioner and tabard designer stands where new
    /// characters appear and the charter item exists.
    /// </summary>
    public static async Task<WorldTestHost> StartAsync(Action<PlayerbotOptions>? configure = null, Action<IServiceCollection>? services = null, bool petitioner = false)
    {
        Action<PlayerbotOptions> prepare = options =>
        {
            options.MaxBots = 16;
            options.MoveSpeed = 0.1f;
            configure?.Invoke(options);
        };
        WorldTestHost host;
        if (petitioner)
        {
            var items = new ItemTestContent();
            items.Templates.Templates.Add(new ItemTemplate { Entry = CharterItem, Class = 12, Name = "Guild Charter", DisplayId = 16161, Quality = 1, Flags = 0x2000, MaxCount = 1, Bonding = 1 });
            PetitionNpcServices.Current.Value = new object();
            try
            {
                using (items.Use()) host = PartyTestHost.Start(prepare, services);
            }
            finally
            {
                PetitionNpcServices.Current.Value = null;
            }
        }
        else
        {
            host = PartyTestHost.Start(prepare, services);
        }

        try
        {
            SocialFeature social = host.WorldServices.GetRequiredService<SocialFeature>();
            await social.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
            await host.WorldServices.GetRequiredService<SocialPetitionFeature>().PetitionsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public static SocialFeature Social(WorldTestHost host) => host.WorldServices.GetRequiredService<SocialFeature>();

    public static PlayerbotSocialFeature Feature(WorldTestHost host) => host.WorldServices.GetRequiredService<PlayerbotSocialFeature>();

    /// <summary>Create and start an autonomous bot of <paramref name="race"/> (a warrior unless <paramref name="characterClass"/> says otherwise) and wait until it is in the world.</summary>
    public static async Task<Guid> StartBotAsync(WorldTestHost host, string name, byte race = Human, byte characterClass = 1)
    {
        ManagedPlayerbotFeature bots = PartyTestHost.Feature(host);
        await bots.StartupAsync(default);
        PlayerbotOperationResult created = await bots.CreateAsync(name, race, characterClass);
        if (!created.Success || created.BotId is not { } id) throw new InvalidOperationException("create: " + created.Code);
        PlayerbotOperationResult started = await bots.StartAsync(id.ToString());
        if (!started.Success) throw new InvalidOperationException("start: " + started.Code);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name) is { IsInWorld: true }, name + " logs in");
        return id;
    }

    /// <summary>The character id of an online player.</summary>
    public static Task<uint> CharacterIdAsync(WorldTestHost host, string name) => host.PlayerStateAsync(name, p => p.Guid.Low);

    /// <summary>
    /// A pass over the host's live services with no bots gathered, for the rules that read the guild and charter state
    /// (<see cref="PlayerbotSocialPass.NameRules"/>). World thread only.
    /// </summary>
    public static PlayerbotSocialPass NewPass(WorldTestHost host, PlayerbotOptions? options = null, long nowMs = 0)
        => new(options ?? PartyTestHost.PlayerbotOptionsOf(host), Social(host).Context, host.World, host.WorldServices, chat: null,
            registered: new HashSet<uint>(), registryLoaded: true, new PlayerbotSocialTimings(), new Random(1), nowMs, elapsedMs: 1000, gather: () => []);

    public static byte[] GuildInvitePacket(string inviter, string guild)
    {
        var writer = new PacketWriter();
        writer.WriteCString(inviter);
        writer.WriteCString(guild);
        return writer.ToArray();
    }

    /// <summary>Make the social feature's passes quick: tests wait on world state, not on the clock.</summary>
    public static void Hasten(WorldTestHost host, Action<PlayerbotSocialTimings>? tune = null)
    {
        PlayerbotSocialTimings timings = Feature(host).Timings;
        timings.PassIntervalMs = 50;
        timings.RegistryMissGapMs = 50;
        timings.FoundingSpacingMs = 200;
        timings.RecruitSpacingMs = 100;
        timings.PromotionSpacingMs = 100;
        timings.OfferSpacingMs = 100;
        timings.IdleChatter = false;
        tune?.Invoke(timings);
    }

    /// <summary>Stop the feature's own passes for good, so a test drives every step itself with a hand-built pass.</summary>
    public static void Freeze(WorldTestHost host)
    {
        PlayerbotSocialTimings timings = Feature(host).Timings;
        timings.PassIntervalMs = int.MaxValue;
        timings.IdleChatter = false;
    }

    public static Guild? GuildOf(WorldTestHost host, string player)
        => Social(host).Context.Guilds.GetGuildOf(host.World.FindOnlinePlayer(player)!);

    /// <summary>Wait until the feature has run <paramref name="count"/> more passes.</summary>
    public static async Task WaitPassesAsync(WorldTestHost host, int count)
    {
        PlayerbotSocialFeature feature = Feature(host);
        int target = feature.Passes + count;
        await WorldTestHost.WaitForAsync(() => feature.Passes >= target, $"{count} more social passes");
    }

    /// <summary>The running, non-scripted bots as the feature gathers them (<see cref="PlayerbotSocialPass.Bots"/>). World thread only.</summary>
    public static List<PlayerbotSocialBot> Gather(WorldTestHost host, bool partyDriven = false)
    {
        ManagedPlayerbotFeature bots = PartyTestHost.Feature(host);
        var list = new List<PlayerbotSocialBot>();
        foreach (PlayerbotStatus status in bots.Snapshot())
        {
            if (status.State != ManagedPlayerbotState.Running || bots.IsScripted(status.BotId)) continue;
            if (bots.FindSession(status.BotId) is not { State: SessionState.InWorld, Player: { IsInWorld: true, Map: not null } player } session) continue;
            if (bots.FindPartyAI(status.BotId) is not { } party) continue;
            list.Add(new PlayerbotSocialBot(status.BotId, session, player, party, partyDriven || bots.IsPartyDriven(status.BotId), GroupDriven: false, bots.FindBrain(status.BotId)));
        }

        return list;
    }

    /// <summary>
    /// A pass over the live services and the running bots, at world time <paramref name="nowMs"/>; the registry holds every running bot.
    /// World thread only.
    /// </summary>
    public static PlayerbotSocialPass BotPass(WorldTestHost host, long nowMs, PlayerbotSocialTimings? timings = null, long elapsedMs = 1000,
        int seed = 1, bool partyDriven = false)
    {
        List<PlayerbotSocialBot> bots = Gather(host, partyDriven);
        HashSet<uint> registered = [.. bots.Select(bot => bot.Player.Guid.Low)];
        return new PlayerbotSocialPass(PartyTestHost.PlayerbotOptionsOf(host), Social(host).Context, host.World, host.WorldServices,
            PartyTestHost.Feature(host).Chat, registered, registryLoaded: true, timings ?? new PlayerbotSocialTimings(), new Random(seed), nowMs, elapsedMs, () => bots)
        {
            Recorder = PartyTestHost.Feature(host).Social,
            Coordinator = host.WorldServices.GetService<ArcaneCore.World.Playerbots.Groups.PlayerbotGroupCoordinator>(),
            Events = PartyTestHost.Feature(host).EventBoard,
        };
    }

    /// <summary>
    /// What the feature's intake does with the guild invitations in each bot's capture queue (the same call), for tests that drive
    /// <see cref="PlayerbotGuildMembership.AnswerInvites"/> by hand. World thread only.
    /// </summary>
    public static void TakeInvites(PlayerbotSocialPass pass, PlayerbotGuildMembership membership)
    {
        foreach (PlayerbotSocialBot bot in pass.Bots)
            foreach (ManagedSessionPacket packet in bot.Session.DrainManagedPackets(WorldOpcode.SmsgGuildInvite))
                if (PlayerbotSocialPackets.TryReadGuildInvite(packet.Payload, out string inviter, out string guild))
                    membership.NoteInvite(bot.Id, inviter, guild);
    }

    /// <summary>A CString-only client packet body (a name, a message).</summary>
    public static byte[] CString(string text)
    {
        var writer = new PacketWriter(text.Length + 1);
        writer.WriteCString(text);
        return writer.ToArray();
    }
}

/// <summary>
/// A running world for one bot social test: the host, a log of what each bot's managed actions dispatched, and helpers to start bots and
/// real players, send a bot's client packets and found guilds the way the tests need them.
/// </summary>
internal sealed class SocialWorld : IAsyncDisposable
{
    private SocialWorld(WorldTestHost host) => Host = host;

    public WorldTestHost Host { get; }

    public DispatchLog Log { get; } = new();

    public Dictionary<string, Guid> Bots { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PlayerbotOptions Options => PartyTestHost.PlayerbotOptionsOf(Host);

    public GuildManager Guilds => SocialTestKit.Social(Host).Context.Guilds;

    /// <summary>Start a world. <paramref name="frozen"/>: the feature never runs a pass by itself; otherwise its passes are quick.</summary>
    public static async Task<SocialWorld> StartAsync(Action<PlayerbotOptions>? configure = null, bool frozen = false, bool petitioner = false,
        Action<IServiceCollection>? services = null, Action<PlayerbotSocialTimings>? tune = null)
    {
        WorldTestHost host = await SocialTestKit.StartAsync(configure, services, petitioner);
        if (frozen) SocialTestKit.Freeze(host);
        else SocialTestKit.Hasten(host, tune);
        return new SocialWorld(host);
    }

    /// <summary>Start a bot, record what it dispatches from now on and give it <paramref name="money"/> copper.</summary>
    public async Task<Guid> AddBotAsync(string name, uint money = 0, byte race = SocialTestKit.Human, byte characterClass = 1)
    {
        Guid id = await SocialTestKit.StartBotAsync(Host, name, race, characterClass);
        Bots[name] = id;
        await Host.OnWorldAsync(() =>
        {
            Player player = Host.World.FindOnlinePlayer(name)!;
            ((WorldSession)player.Session).ManagedDispatchObserver = opcode => Log.Add(name, opcode);
            if (money > 0) player.Money = money;
        });
        return id;
    }

    /// <summary>A real player on a socket client.</summary>
    public async Task<WorldTestClient> AddPlayerAsync(string name, byte race = SocialTestKit.Human)
    {
        WorldTestClient client = await Host.EnterWorldAsync(name.ToUpperInvariant(), name, race: race);
        await client.CollectAsync();
        return client;
    }

    /// <summary>One client packet from a bot, through its managed session as its own action (world thread). False when refused.</summary>
    public Task<bool> SendAsync(string bot, WorldOpcode opcode, byte[]? payload = null)
        => Host.OnWorldAsync(() => PlayerbotSocialPackets.Send((WorldSession)Host.World.FindOnlinePlayer(bot)!.Session, opcode, payload ?? []));

    public Task<Guild?> GuildOfAsync(string player) => Host.OnWorldAsync(() => SocialTestKit.GuildOf(Host, player));

    public Task<int> GuildCountAsync() => Host.OnWorldAsync(() => Guilds.All.Count());

    public Task<IReadOnlySet<uint>> BotCharacterIdsAsync()
        => Host.OnWorldAsync<IReadOnlySet<uint>>(() => Bots.Keys.Select(name => Host.World.FindOnlinePlayer(name)!.Guid.Low).ToHashSet());

    /// <summary>A bot founds the guild <paramref name="name"/> with CMSG_GUILD_CREATE and the guild exists.</summary>
    public async Task<Guild> FoundGuildAsync(string bot, string name)
    {
        Assert.True(await SendAsync(bot, WorldOpcode.CmsgGuildCreate, SocialTestKit.CString(name)));
        await Host.WaitForWorldAsync(() => Guilds.GetByName(name) is not null, $"the guild {name}");
        return (await Host.OnWorldAsync(() => Guilds.GetByName(name)))!;
    }

    /// <summary>
    /// <paramref name="inviter"/> (a bot) invites the bot <paramref name="invitee"/> and it accepts, both by their own client packets, and the
    /// guild has one more member.
    /// </summary>
    public async Task BotJoinsAsync(string inviter, string invitee, Guild guild)
    {
        int before = guild.MemberCount;
        await Host.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotSocialPackets.Send((WorldSession)Host.World.FindOnlinePlayer(inviter)!.Session, WorldOpcode.CmsgGuildInvite, SocialTestKit.CString(invitee)));
            Assert.True(PlayerbotSocialPackets.Send((WorldSession)Host.World.FindOnlinePlayer(invitee)!.Session, WorldOpcode.CmsgGuildAccept, []));
            return true;
        });
        await Host.WaitForWorldAsync(() => guild.MemberCount == before + 1, $"{invitee} joins {guild.Name}");
    }

    /// <summary>A bot invites a real player, who accepts with a client packet.</summary>
    public async Task PlayerJoinsAsync(string inviter, string player, WorldTestClient client, Guild guild)
    {
        int before = guild.MemberCount;
        Assert.True(await SendAsync(inviter, WorldOpcode.CmsgGuildInvite, SocialTestKit.CString(player)));
        await Host.WaitForWorldAsync(() => Guilds.InvitedTo(Host.World.FindOnlinePlayer(player)!) == guild.Id, $"{player} is invited");
        await client.SendAsync(WorldOpcode.CmsgGuildAccept, []);
        await Host.WaitForWorldAsync(() => guild.MemberCount == before + 1, $"{player} joins {guild.Name}");
    }

    /// <summary>
    /// Run <paramref name="act"/> on the world thread the moment every bot in <paramref name="bots"/> sees, and stands within a few yards
    /// of, every player in <paramref name="players"/> (each is moved next to the first bot until they do), all alive and out of combat.
    /// </summary>
    public async Task WhenNearAsync(string[] bots, string[] players, Action act)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            bool done = await Host.OnWorldAsync(() =>
            {
                Player anchor = Host.World.FindOnlinePlayer(bots[0])!;
                bool ready = true;
                foreach (string name in players)
                {
                    Player player = Host.World.FindOnlinePlayer(name)!;
                    bool near = player.IsAlive && !player.Combat.IsInCombat;
                    foreach (string bot in bots)
                    {
                        Player self = Host.World.FindOnlinePlayer(bot)!;
                        near &= self.IsAlive && !self.Combat.IsInCombat && self.VisibleObjects.Contains(player.Guid) && Distance(self, player) < 5f;
                    }

                    if (near) continue;
                    ready = false;
                    player.Relocate(anchor.X + 1f, anchor.Y, anchor.Z, player.Orientation, Host.World.NowMs);
                }

                if (!ready) return false;
                act();
                return true;
            });
            if (done) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the bots and players did not come within sight of each other");
            await Task.Delay(20);
        }
    }

    public static float Distance(WorldObject a, WorldObject b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
    }
}
