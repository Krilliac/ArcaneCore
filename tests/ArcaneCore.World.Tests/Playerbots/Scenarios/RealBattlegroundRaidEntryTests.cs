using ArcaneCore.Data;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>A refreshed SQLite world database produced from classic-db z2815 and the build-5875 DBCs.</summary>
internal sealed class RealWorldContentFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_WORLD_DB";

    public RealWorldContentFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } path || !File.Exists(path))
        {
            Skip = $"{Variable} must name a refreshed SQLite world.db; real battleground and raid entry did NOT run.";
        }
    }
}

// Loading the imported 40 MB world and advancing three match clocks is expensive enough to
// starve unrelated loopback packet tests when xUnit runs their collections beside this one.
[CollectionDefinition("Real battleground raid content", DisableParallelization = true)]
public sealed class RealBattlegroundRaidContentCollection;

/// <summary>
/// Real classic-db battlemaster spawns, battleground templates, Map.dbc maps and area-trigger rows, driven through the
/// real world handlers by two managed clients. vmangos BattleGroundMgr.cpp:870-975,1007-1042 and
/// BattleGroundHandler.cpp:40-73,83-264,361-507 are the queue, invitation and port reference;
/// MapManager.cpp:180-218 is the raid-group gate; DungeonResetScheduler::ScheduleAllDungeonResets/Update
/// (Maps/MapPersistentStateMgr.cpp:448-528,570-619) is exercised by the loaded map_template reset delays.
/// The only match rule changed for this two-player test is the minimum per team:
/// classic-db requires 5/8/20 players in WSG/AB/AV, which two clients cannot satisfy.
/// </summary>
[Collection("Real battleground raid content")]
public sealed class RealBattlegroundRaidEntryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-real-bg-raid-" + Guid.NewGuid().ToString("N"));
    private readonly CaptureLogger _logs = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [RealWorldContentFact]
    public Task WarsongGulch_RealBattlemaster_Invite_Port_AndStart() => BattlegroundAsync(BattlegroundType.WarsongGulch, 5);

    [RealWorldContentFact]
    public Task ArathiBasin_RealBattlemaster_Invite_Port_AndStart() => BattlegroundAsync(BattlegroundType.ArathiBasin, 8);

    [RealWorldContentFact]
    public Task AlteracValley_RealBattlemaster_Invite_Port_AndStart() => BattlegroundAsync(BattlegroundType.AlteracValley, 20);

    private async Task BattlegroundAsync(BattlegroundType type, uint retailMinimum)
    {
        await using ScenarioTestWorld world = await StartAsync();
        BattlegroundFeature feature = world.Services.GetRequiredService<BattlegroundFeature>();
        BattlegroundTemplate imported = await world.Host.OnWorldAsync(() => feature.Manager.TemplateOf(type)!);
        Assert.NotNull(imported);
        Assert.Equal(retailMinimum, imported.MinPlayersPerTeam);
        Assert.Equal(24, feature.Battlemasters.Count);

        // A test-only override of the player count; the map, safe locations, spawns, brackets and match rules remain imported.
        await world.Host.OnWorldAsync(() => feature.Manager.RegisterTemplate(imported with { MinPlayersPerTeam = 1 }));
        ScenarioReport report = await world.RunAsync(new Scenario(type.ToString(), async context =>
        {
            uint mapId = BattlegroundManager.MapOfType(type);
            ScenarioBot ally = await context.LoginAsync("Scnrealally", race: 1);
            ScenarioBot horde = await context.LoginAsync("Scnrealhorde", race: 2);
            foreach (ScenarioBot bot in new[] { ally, horde })
            {
                await bot.ReadAsync(player => { player.Level = 60; return true; });
                uint continent = await bot.ReadAsync(player => player.MapId);
                var master = context.FindBattlemaster(continent, type)
                    ?? throw new ScenarioAssertionException($"no imported {type} battlemaster on map {continent}");
                await context.PlaceAsync(bot, continent, master.X + 2f, master.Y, master.Z);
                await context.WaitUntilAsync($"{bot.Name} sees the imported battlemaster", () =>
                    context.Services.GetRequiredService<QuestNpcFeature>().Services.InteractableNpc(bot.RequirePlayer(), master.Guid, NpcFlags.BattleMaster) is not null);
                long mark = bot.Mark();
                ScenarioContext.Expect(await bot.BattlemasterHelloAsync(master.Guid), "battlemaster hello refused");
                await bot.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldList, payload => payload, since: mark);
                ScenarioContext.Expect(await bot.JoinBattlegroundAsync(master.Guid, mapId), "battlemaster join refused");
                await bot.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldStatus, ScenarioBattlegrounds.BattlefieldStatus,
                    status => status.MapId == mapId && status.Status is BattlegroundStatus.WaitQueue or BattlegroundStatus.WaitJoin, mark);
            }

            foreach (ScenarioBot bot in new[] { ally, horde })
            {
                await bot.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldStatus, ScenarioBattlegrounds.BattlefieldStatus,
                    status => status.MapId == mapId && status.Status == BattlegroundStatus.WaitJoin);
                ScenarioContext.Expect(await bot.PortBattlegroundAsync(mapId), "port refused");
                await context.WaitUntilAsync($"{bot.Name} enters {type}", () => bot.Session!.Player is { IsInWorld: true, Map: { } map } p
                    && map.MapId == mapId && map.InstanceId != 0 && feature.BattlegroundOf(p.Guid)?.PlayerTeam(p.Guid) is not null);
            }

            Battleground? match = await context.BattlegroundOfAsync(ally);
            ScenarioContext.Expect(match is not null && ReferenceEquals(match, await context.BattlegroundOfAsync(horde)), "players entered different matches");
            await context.WaitUntilAsync($"{type} starts", () => match!.Status == BattlegroundStatus.InProgress, TimeSpan.FromMinutes(3));
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report + "\n" + _logs);
    }

    [RealWorldContentFact]
    public async Task MoltenCoreAndOnyxia_RealPortals_EnforceRequirements_BindRaid_AndScheduleResets()
    {
        await using ScenarioTestWorld world = await StartAsync();
        InstanceManager instances = world.Services.GetRequiredService<InstanceFeature>().Instances;
        Assert.Equal(7u, await world.Host.OnWorldAsync(() => ArcaneCore.Game.Maps.Templates.WorldMaps.Of(world.Host.World).Registry.Find(409)!.ResetDelay));
        Assert.Equal(5u, await world.Host.OnWorldAsync(() => ArcaneCore.Game.Maps.Templates.WorldMaps.Of(world.Host.World).Registry.Find(249)!.ResetDelay));
        Assert.True(await world.Host.OnWorldAsync(() => instances.GetRaidResetTime(409)) > 0);
        Assert.True(await world.Host.OnWorldAsync(() => instances.GetRaidResetTime(249)) > 0);
        long moltenReset = await world.Host.OnWorldAsync(() => instances.GetRaidResetTime(409));
        long onyxiaReset = await world.Host.OnWorldAsync(() => instances.GetRaidResetTime(249));

        ScenarioReport report = await world.RunAsync(new Scenario("real-raid-portals", async context =>
        {
            ScenarioBot leader = await context.LoginAsync("Scnraidlead");
            ScenarioBot member = await context.LoginAsync("Scnraidmate");
            ScenarioContext.Expect(await leader.InviteAsync(member.Name), "raid invite refused");
            ScenarioContext.Expect(await member.AcceptInviteAsync(), "raid accept refused");
            await context.WaitUntilAsync("party formed", () => context.Services.GetRequiredService<SocialFeature>().Context.Groups.GetGroup(leader.Guid)?.MemberCount == 2);
            ScenarioContext.Expect(await leader.SendAsync(WorldOpcode.CmsgGroupRaidConvert, []), "raid conversion refused");
            await context.WaitUntilAsync("raid formed", () => context.Services.GetRequiredService<SocialFeature>().Context.Groups.GetGroup(leader.Guid)?.IsRaid == true);

            // Onyxia's real trigger 2848 requires level 50 and Drakefire Amulet (16309).
            await TriggerAsync(context, leader, 2848, 1, expectedMap: 1, refusal: true);
            await leader.ReadAsync(player => { player.Level = 60; return true; });
            await member.ReadAsync(player => { player.Level = 60; return true; });
            await TriggerAsync(context, leader, 2848, 1, expectedMap: 1, refusal: true);
            await context.GiveItemAsync(leader, 16309);
            await context.GiveItemAsync(member, 16309);
            await TriggerAsync(context, leader, 2848, 1, expectedMap: 249, refusal: false);
            await TriggerAsync(context, member, 2848, 1, expectedMap: 249, refusal: false);
            await AssertSharedGroupBind(context, leader, member, instances, 249);

            // Molten Core's window entrance requires the rewarded attunement quest (7848).
            await TriggerAsync(context, leader, 3528, 0, expectedMap: 0, refusal: true);
            await context.ReadAsync(() =>
            {
                QuestNpcServices quests = context.Services.GetRequiredService<QuestNpcFeature>().Services;
                var state = quests.StateOf(leader.RequirePlayer()) ?? throw new ScenarioAssertionException("no loaded quest journal");
                QuestStatusData reward = state.Quests.GetOrAdd(7848);
                reward.Status = QuestStatus.Complete;
                reward.Rewarded = true;
                return true;
            });
            await TriggerAsync(context, leader, 3528, 0, expectedMap: 409, refusal: false);
            await TriggerAsync(context, member, 2886, 230, expectedMap: 409, refusal: false);
            await AssertSharedGroupBind(context, leader, member, instances, 409);

            // The manual time provider advances past both imported raid periods; update the same schedule
            // that the world feature drives every five seconds. No wall-clock wait is involved.
            world.Time.Advance(TimeSpan.FromDays(8));
            await context.ReadAsync(() => { instances.UpdateSchedule(); return true; });
            await context.WaitUntilAsync("the global resets send the raid home", () =>
                leader.Session!.Player is { MapId: 0 } && member.Session!.Player is { MapId: 0 });
            await context.ReadAsync(() =>
            {
                var group = context.Services.GetRequiredService<SocialFeature>().Context.Groups.GetGroup(leader.Guid)!;
                ScenarioContext.Expect(instances.GetGroupBind(group, 249) is null && instances.GetGroupBind(group, 409) is null,
                    "global reset left a raid bind");
                ScenarioContext.Expect(instances.GetRaidResetTime(249) > onyxiaReset && instances.GetRaidResetTime(409) > moltenReset,
                    "global reset did not advance both schedules");
                return true;
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report + "\n" + _logs);
    }

    private static async Task TriggerAsync(ScenarioContext context, ScenarioBot bot, uint triggerId, uint sourceMap, uint expectedMap, bool refusal)
    {
        var trigger = context.FindAreaTrigger(triggerId) ?? throw new ScenarioAssertionException($"missing imported trigger {triggerId}");
        ScenarioContext.ExpectEqual(sourceMap, trigger.MapId, "trigger source map");
        await context.PlaceAsync(bot, trigger.MapId, trigger.X, trigger.Y, trigger.Z);
        long mark = bot.Mark();
        ScenarioContext.Expect(await bot.SendAsync(WorldOpcode.CmsgAreatrigger, ScenarioPackets.UInt32(triggerId)), "area trigger handler refused");
        if (refusal)
        {
            await bot.WaitForPacketAsync(WorldOpcode.SmsgAreaTriggerMessage, payload => payload, since: mark);
        }
        else
        {
            await context.WaitUntilAsync($"{bot.Name} enters map {expectedMap}", () => bot.Session!.Player is { IsInWorld: true, MapId: var map } && map == expectedMap);
        }

        ScenarioContext.ExpectEqual(expectedMap, await bot.ReadAsync(player => player.MapId), $"map after trigger {triggerId}");
    }

    private static async Task AssertSharedGroupBind(ScenarioContext context, ScenarioBot a, ScenarioBot b, InstanceManager instances, uint mapId)
    {
        await context.ReadAsync(() =>
        {
            var group = context.Services.GetRequiredService<SocialFeature>().Context.Groups.GetGroup(a.Guid)!;
            var bind = instances.GetGroupBind(group, mapId);
            ScenarioContext.Expect(bind is not null, $"raid is not bound to map {mapId}");
            ScenarioContext.ExpectEqual(bind!.Value.Save.InstanceId, a.RequirePlayer().Map!.InstanceId, "leader instance id");
            ScenarioContext.ExpectEqual(bind.Value.Save.InstanceId, b.RequirePlayer().Map!.InstanceId, "member instance id");
            ScenarioContext.ExpectEqual(instances.GetRaidResetTime(mapId), bind.Value.Save.ResetTime, "instance reset schedule");
            return true;
        });
    }

    private async Task<ScenarioTestWorld> StartAsync()
    {
        Directory.CreateDirectory(_directory);
        string world = Path.Combine(_directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!, world);
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:World:Provider"] = "Sqlite",
            ["Database:World:ConnectionString"] = $"Data Source={world};Pooling=False",
        }).Build();
        ScenarioTestWorld result = await ScenarioTestWorld.StartAsync(services =>
        {
            // The classic-db importer has world content but not ChrRaces/ChrClasses DBC appearance rows;
            // keep the loopback host's synthetic character appearance store for managed client login.
            ServiceDescriptor appearance = services.Last(d => d.ServiceType == typeof(IWorldDataStore));
            WarsongGulchTestContent.Register(services); // only the two player factions and spell auras are used; EF stores below own content.
            // This DBC directory has no FactionTemplate.dbc. Supply test-only neutral reaction rows for the
            // ten faction-template ids used by the imported battlemasters; their spawns and flags remain real.
            services.AddSingleton(new FactionTemplateCatalog(
                [
                    new FactionTemplateRecord(1, 1, 0, 2, 6, 0),
                    new FactionTemplateRecord(2, 2, 0, 4, 6, 0),
                    .. new uint[] { 412, 1214, 1215, 1216, 1217, 1514, 1515, 1577, 1641, 1642 }
                        .Select(id => new FactionTemplateRecord(id, 0, 0, 6, 6, 0)),
                ]));
            services.AddSingleton(config);
            services.AddWorldDatabase(config);
            services.Add(appearance);
            services.AddSingleton<ILogger<ArcaneCore.World.Playerbots.ManagedPlayerbotFeature>>(_logs);
        });
        Assert.Equal(44, await result.Host.OnWorldAsync(() => ArcaneCore.Game.Maps.Templates.WorldMaps.Of(result.Host.World).Registry.All.Count()));
        return result;
    }

    private sealed class Scenario(string name, Func<ScenarioContext, Task> run) : IPlayerbotScenario
    {
        public string Name => name;
        public string Description => name;
        public Task RunAsync(ScenarioContext context) => run(context);
    }

    private sealed class CaptureLogger : ILogger<ArcaneCore.World.Playerbots.ManagedPlayerbotFeature>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Scope.Instance;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Enqueue(formatter(state, exception) + ": " + exception);
        public override string ToString() => string.Join("\n", _messages);
        private sealed class Scope : IDisposable { public static Scope Instance { get; } = new(); public void Dispose() { } }
    }
}
