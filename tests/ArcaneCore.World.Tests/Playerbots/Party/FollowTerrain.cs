using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>A theory that needs the live server's terrain extraction, skipped without it like <see cref="RealTerrainBotFactAttribute"/>.</summary>
internal sealed class RealTerrainBotTheoryAttribute : TheoryAttribute
{
    public RealTerrainBotTheoryAttribute()
    {
        string? root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "mmaps")))
            Skip = $"{RealTerrainBotFactAttribute.Variable} is not set to a terrain data root with mmaps/: the real-terrain follow did NOT run.";
    }
}

/// <summary>
/// A real player (a socket client, the group's master) and a hand-driven <see cref="PlayerbotPartyAI"/> follower on the live
/// server's terrain extraction (<see cref="RealTerrainBotFactAttribute"/>), on the manual world clock: every world tick the
/// follower's client half runs (<see cref="PlayerbotMotion.Pump"/>, the order acknowledgements) and its party AI thinks as
/// <see cref="ManagedPlayerbotFeature"/> drives it. The run records what the follower did: how far it walked, the jumps no walk
/// explains (a teleport), how close it came to each hostile, and when it stood beside its master.
/// </summary>
internal sealed class FollowTerrain : IAsyncDisposable
{
    /// <summary>Faction 14 monsters are hostile to the human follower (the scenario world's factions).</summary>
    internal const uint HostileFaction = 14;

    private FollowTerrain(WorldTestHost host, WorldTestClient master, WorldSession session, PlayerbotOptions options)
    {
        Host = host;
        MasterClient = master;
        Session = session;
        Options = options;
        AI = new PlayerbotPartyAI(session, options);
    }

    public WorldTestHost Host { get; }

    public WorldTestClient MasterClient { get; }

    public WorldSession Session { get; }

    public PlayerbotOptions Options { get; }

    public PlayerbotPartyAI AI { get; }

    public Player Bot => Session.Player!;

    public Player Master => Host.World.FindOnlinePlayer(MasterName)!;

    public const string MasterName = "Followmaster";

    private TeleportService Teleports => Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;

    public static async Task<FollowTerrain> StartAsync(Action<PlayerbotOptions>? configure = null)
    {
        string root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
        var options = new PlayerbotOptions
        {
            Enabled = true, ThinkIntervalMs = 100, MaxActionsPerTick = 8, Chat = { Enabled = false },
            Party = { InvitePolicy = PlayerbotInvitePolicy.Anyone },
        };
        configure?.Invoke(options);
        options.Validate();
        WorldTestHost host = WorldTestHost.Start(configure: o => o.Maps.DataDirectory = root, configureServices: services =>
        {
            services.AddSingleton<IWorldFeature, ManualClock>();
            services.AddSingleton<IOptions<PlayerbotOptions>>(Microsoft.Extensions.Options.Options.Create(options));
            services.AddSingleton(new FactionTemplateCatalog(
            [
                new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
                new FactionTemplateRecord(HostileFaction, HostileFaction, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
            ]));
        });
        WorldTestClient master = await host.EnterWorldAsync("FOLLOWMASTER", MasterName);
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, "FOLLOWBOT", "Followbot");
        var terrain = new FollowTerrain(host, master, session, options);
        await host.OnWorldAsync(() =>
        {
            GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
            groups.Invite(terrain.Master, terrain.Bot.Name);
            groups.Accept(terrain.Bot);
            Assert.True(groups.AreInSameGroup(terrain.Master.Guid, terrain.Bot.Guid));
        });
        return terrain;
    }

    /// <summary>Teleport the follower and acknowledge like a client until it stands there (eastern kingdoms unless <paramref name="map"/>).</summary>
    public async Task PlaceBotAsync(Vector3 at, uint map = 0)
    {
        Assert.True(await Host.OnWorldAsync(() => Teleports.TeleportTo(Bot, map, at.X, at.Y, at.Z, 0)), "teleport refused");
        bool there = await Host.World.AdvanceClockUntilAsync(10_000, () =>
        {
            PlayerbotMovementControl.Update(Session, Bot);
            return Bot.IsInWorld && Teleports.StageOf(Bot) is null && Bot.MapId == map
                && Vector2.Distance(new(Bot.X, Bot.Y), new(at.X, at.Y)) < 1f;
        });
        Assert.True(there, $"the follower did not arrive at {at}");
        await Host.OnWorldAsync(() => PlayerbotMotion.Reset(Bot));
    }

    /// <summary>Put the master (a socket client, which does not move by itself) at <paramref name="at"/> on its map.</summary>
    public Task PlaceMasterAsync(Vector3 at) => Host.PlaceAsync(MasterName, at.X, at.Y, at.Z);

    /// <summary>World thread: spawn a hostile creature of <paramref name="template"/> (its faction made hostile to the follower).</summary>
    public Creature SpawnHostile(CreatureTemplate template, Vector3 at)
    {
        Map map = Bot.Map!;
        CreatureMapSystem system = map.FindUpdater<CreatureMapSystem>() ?? AddSystem(map);
        return system.SpawnTemporary(template, at.X, at.Y, at.Z, 0);
    }

    private static CreatureMapSystem AddSystem(Map map)
    {
        var system = new CreatureMapSystem(map, new CreatureContent([], [], [], [], []), random: new Random(5875));
        map.AddUpdater(system);
        return system;
    }

    /// <summary>
    /// Run the world for <paramref name="maxMs"/> of game time with the follower driven every tick as the feature drives a party
    /// bot, or until <paramref name="stop"/> holds. Returns what it did.
    /// </summary>
    public async Task<FollowRun> RunAsync(uint maxMs, IReadOnlyList<Creature>? hostiles = null, Func<FollowRun, bool>? stop = null)
    {
        var run = new FollowRun();
        Vector3 last = await Host.OnWorldAsync(() => Position(Bot));
        uint start = await Host.OnWorldAsync(() => Host.World.NowMs);
        await Host.World.AdvanceClockUntilAsync(maxMs, () =>
        {
            Player bot = Bot;
            uint now = Host.World.NowMs;
            PlayerbotMotion.Pump(Session, bot, now);
            Session.ManagedBudget = new ManagedActionBudget(Options.MaxActionsPerTick);
            AI.Intake(bot);
            if (AI.Drives(bot)) AI.Update(bot, 50);
            Session.ManagedBudget = null;

            Vector3 here = Position(bot);
            float step = Vector2.Distance(new(here.X, here.Y), new(last.X, last.Y));
            if (bot.MapId != run.LastMap || step > FollowRun.JumpYards || MathF.Abs(here.Z - last.Z) > FollowRun.JumpYards)
            {
                if (bot.MapId != run.LastMap || step > FollowRun.JumpYards) run.Jumps.Add((now - start, last, here));
                else run.Drops.Add((now - start, last, here));
            }
            else
            {
                run.Walked += step;
            }

            run.LastMap = bot.MapId;
            last = here;
            if (!bot.IsAlive) run.Died = true;
            if (hostiles is not null)
                foreach (Creature hostile in hostiles)
                {
                    float distance = Vector3.Distance(here, Position(hostile));
                    if (distance < run.ClosestHostile) run.ClosestHostile = distance;
                    if (bot.Combat.Victim == hostile && !run.Engaged.Contains(hostile.Entry)) run.Engaged.Add(hostile.Entry);
                }

            Player master = Master;
            run.MasterDistance = ReferenceEquals(master.Map, bot.Map) ? Vector3.Distance(here, Position(master)) : float.PositiveInfinity;
            run.HeightToMaster = here.Z - master.Z;
            if (run.ArrivedMs < 0 && run.MasterDistance <= FollowRun.BesideYards && MathF.Abs(run.HeightToMaster) < 3f) run.ArrivedMs = now - start;
            run.Goal = AI.Goal;
            run.Teleports = AI.Follower.Teleports;
            run.DropsTaken = AI.Follower.Drops;
            run.Replans = AI.Follower.Replans;
            run.CatchUp = AI.Follower.LastCatchUp;
            return stop?.Invoke(run) == true;
        });
        return run;
    }

    public static Vector3 Position(WorldObject unit) => new(unit.X, unit.Y, unit.Z);

    public async ValueTask DisposeAsync()
    {
        Session.Kick();
        await Session.ManagedClosed;
        await MasterClient.DisposeAsync();
        await Host.DisposeAsync();
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}

/// <summary>What a follower did in one <see cref="FollowTerrain.RunAsync"/>.</summary>
internal sealed class FollowRun
{
    /// <summary>A move farther than this in one 50 ms tick is no walk (a teleport or a fall): a bot runs at most 7 yards a second.</summary>
    internal const float JumpYards = 8f;

    /// <summary>Within this many yards (and 3 yards of height) the follower stands beside its master.</summary>
    internal const float BesideYards = 6f;

    public uint LastMap { get; set; }

    public float Walked { get; set; }

    public List<(uint Ms, Vector3 From, Vector3 To)> Jumps { get; } = [];

    public List<(uint Ms, Vector3 From, Vector3 To)> Drops { get; } = [];

    public float ClosestHostile { get; set; } = float.PositiveInfinity;

    public List<uint> Engaged { get; } = [];

    public bool Died { get; set; }

    public float MasterDistance { get; set; } = float.PositiveInfinity;

    public float HeightToMaster { get; set; }

    public long ArrivedMs { get; set; } = -1;

    public PlayerbotGoalKind Goal { get; set; }

    /// <summary>The follower's own counts: catch-up teleports, drops taken, re-plans after a stall, and why it last caught up.</summary>
    public int Teleports { get; set; }

    public int DropsTaken { get; set; }

    public int Replans { get; set; }

    public PlayerbotCatchUpReason CatchUp { get; set; }

    public override string ToString()
        => $"walked {Walked:F1} yd, jumps [{string.Join("; ", Jumps.Select(j => $"{j.Ms}ms {j.From}->{j.To}"))}], drops [{string.Join("; ", Drops.Select(j => $"{j.Ms}ms {j.From}->{j.To}"))}], "
            + $"closest hostile {ClosestHostile:F1} yd, engaged [{string.Join(",", Engaged)}], died {Died}, master {MasterDistance:F1} yd "
            + $"(height {HeightToMaster:F1}), arrived at {ArrivedMs} ms, goal {Goal}, teleports {Teleports}, drops taken {DropsTaken}, "
            + $"re-plans {Replans}, catch-up {CatchUp}";
}
