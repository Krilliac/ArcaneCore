using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.GridTerrain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>
/// Dungeon instances end to end over real sessions: the Deadmines area trigger (level
/// requirement, then SMSG_TRANSFER_PENDING → SMSG_NEW_WORLD → MSG_MOVE_WORLDPORT_ACK) puts
/// different groups into different instance maps, a teleport takes a player back out, and
/// CMSG_RESET_INSTANCES / CMSG_REQUEST_RAID_INFO answer from the instance system; deleting a
/// character drops its binds in memory and in storage.
/// </summary>
public sealed class InstanceLoopbackTests
{
    private const uint Deadmines = 36;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task TwoGroups_ThroughTheAreaTrigger_GetSeparateInstances_AndMembersShareOne()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient a = await host.EnterWorldAsync("INSTA", "Insta");
        await using WorldTestClient b = await host.EnterWorldAsync("INSTB", "Instb");
        await using WorldTestClient c = await host.EnterWorldAsync("INSTC", "Instc");
        await using WorldTestClient d = await host.EnterWorldAsync("INSTD", "Instd");
        await GroupAsync(host, a, b, "Insta", "Instb");
        await GroupAsync(host, c, d, "Instc", "Instd");

        await EnterThroughTriggerAsync(host, a, "Insta");
        await EnterThroughTriggerAsync(host, c, "Instc");
        await EnterThroughTriggerAsync(host, b, "Instb");
        await EnterThroughTriggerAsync(host, d, "Instd");

        uint ia = await InstanceOfAsync(host, "Insta"), ib = await InstanceOfAsync(host, "Instb");
        uint ic = await InstanceOfAsync(host, "Instc"), id = await InstanceOfAsync(host, "Instd");
        Assert.Equal(ia, ib);
        Assert.Equal(ic, id);
        Assert.NotEqual(ia, ic);
        Assert.True(await host.OnWorldAsync(() => !ReferenceEquals(host.World.FindMap(Deadmines, ia), host.World.FindMap(Deadmines, ic))));
        Assert.Null(await host.OnWorldAsync(() => host.World.FindMap(Deadmines)));
    }

    [Fact]
    public async Task TeleportOut_ThenResetInstances_ResetsIt_AndRaidInfoIsEmpty()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("INSTSOLO", "Instsolo");
        await EnterThroughTriggerAsync(host, client, "Instsolo");
        uint id = await InstanceOfAsync(host, "Instsolo");
        int character = (int)await host.PlayerStateAsync("Instsolo", p => p.Guid.Low);

        // Inside: the reset skips the instance the player is in.
        await client.CollectAsync(Quiet);
        await client.SendAsync(WorldOpcode.CmsgResetInstances, []);
        Assert.DoesNotContain(await client.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgInstanceReset);

        // Out through the teleport/worldport flow.
        await host.OnWorldAsync(() => TeleportFeatureOf(host.World.FindOnlinePlayer("Instsolo")!).Teleports
            .TeleportTo(host.World.FindOnlinePlayer("Instsolo")!, 0, -8913.23f, 554.633f, 93.7944f, 0.5f));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(await client.ReadUntilAsync(WorldOpcode.SmsgTransferPending)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld)));
        await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Instsolo")?.Map is { MapId: 0, InstanceId: 0 }, "the player is back on the continent");

        await client.CollectAsync(Quiet);
        await client.SendAsync(WorldOpcode.CmsgResetInstances, []);
        Assert.Equal(Deadmines, BinaryPrimitives.ReadUInt32LittleEndian(await client.ReadUntilAsync(WorldOpcode.SmsgInstanceReset)));

        await client.SendAsync(WorldOpcode.CmsgRequestRaidInfo, []);
        Assert.Equal(new byte[4], await client.ReadUntilAsync(WorldOpcode.SmsgRaidInstanceInfo));

        InstanceFeature feature = await host.PlayerStateAsync("Instsolo", p => Services(p).GetRequiredService<InstanceFeature>());
        await feature.FlushAsync();
        InMemoryInstanceStore store = await host.PlayerStateAsync("Instsolo", p => Services(p).GetRequiredService<InMemoryInstanceStore>());
        string[] writes = [.. store.Writes];
        Assert.Contains($"instance {id} map {Deadmines}", writes);
        Assert.Contains($"bind {character} {id} False", writes);
        Assert.Contains($"last {character} {Deadmines} {id}", writes);
        Assert.Contains($"delete {id}", writes);
        Assert.True(Array.IndexOf(writes, $"bind {character} {id} False") < Array.IndexOf(writes, $"delete {id}"));

        // Entering again creates a new instance.
        await EnterThroughTriggerAsync(host, client, "Instsolo");
        Assert.NotEqual(id, await InstanceOfAsync(host, "Instsolo"));
    }

    [Fact]
    public async Task ResetInstance_WorldportAckAfterUnloadExpiry_UnloadsExactSourceMap()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("INSTTRANSIT", "Insttransit");
        await using WorldTestClient outside = await host.EnterWorldAsync("INSTOUTSIDE", "Instoutside");
        await GroupAsync(host, client, outside, "Insttransit", "Instoutside");
        await EnterThroughTriggerAsync(host, client, "Insttransit");

        (Player player, Map source, InstanceFeature feature, TeleportService teleports) = await host.PlayerStateAsync(
            "Insttransit", p => (p, p.Map!, Services(p).GetRequiredService<InstanceFeature>(), TeleportFeatureOf(p).Teleports));
        var observer = new WorldportAckMapObserver(player, teleports);
        await host.OnWorldAsync(() =>
        {
            feature.Options.HomebindTimerMs = 1;
            source.AddUpdater(observer);
        });

        // The remaining group member is outside, so disbanding resets this occupied instance
        // and homebinds its last player. Keep the client in transit past the 1 ms unload delay.
        await client.SendAsync(WorldOpcode.CmsgGroupDisband, []);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld)));
        await host.WaitForWorldAsync(
            () => source.TransitCount == 1 && feature.Instances.StateOf(source)?.UnloadTimerMs == 0,
            "the reset map's unload timer expires while the client is in transit");
        await host.OnWorldAsync(() =>
        {
            Assert.Same(source, host.World.FindMap(Deadmines, source.InstanceId));
            Assert.Equal(0, source.PlayerCount);
            Assert.True(feature.Instances.StateOf(source)!.ResetAfterUnload);
            Assert.Null(feature.Instances.FindSave(source.InstanceId));
            Assert.Null(player.Map);
        });

        // The real world handler runs during source-map packet processing and posts arrival
        // to the next command pass; a direct service call between ticks skips this ordering.
        await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        Assert.Equal(1, await observer.AckTransitCount.WaitAsync(TimeSpan.FromSeconds(10)));
        await observer.ArrivalMapPass.WaitAsync(TimeSpan.FromSeconds(10));
        await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates);
        // The observer's arrival signal came from the map pass, so this next command runs
        // after that pass's deferred unloads, without a timing-based quiet-window assertion.
        await host.OnWorldAsync(() =>
        {
            Assert.Null(host.World.FindMap(Deadmines, source.InstanceId));
            Assert.True(source.IsUnloaded);
            Assert.Null(feature.Instances.StateOf(source));
            Assert.Equal(0, source.TransitCount);
            Assert.Same(player, host.World.FindOnlinePlayer(player.Guid));
            Assert.Same(host.World.FindMap(0), player.Map);
            Assert.False(player.Map!.IsUnloaded);
            Assert.Equal(2, host.World.OnlinePlayerCount);
        });
    }

    [Fact]
    public async Task EachInstance_LoadsItsOwnCreatures()
    {
        var template = new CreatureTemplate
        {
            Entry = 657, Name = "Defias Pirate", MinLevel = 18, MaxLevel = 18, DisplayIds = [2349],
            Faction = 17, CreatureType = 7, MinLevelHealth = 500, MaxLevelHealth = 500,
        };
        var spawn = new CreatureSpawn { Guid = 79000, Entry = 657, MapId = Deadmines, X = -10f, Y = -380f, Z = 61.8f };
        var context = new CreatureTestContext(new CreatureContent([template], [spawn], [], [], []));
        CreatureTestStore.Current.Value = context;
        WorldTestHost started;
        try
        {
            started = WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        await using WorldTestHost host = started;
        await using WorldTestClient a = await host.EnterWorldAsync("INSTCRA", "Instcra");
        await using WorldTestClient b = await host.EnterWorldAsync("INSTCRB", "Instcrb");
        await EnterThroughTriggerAsync(host, a, "Instcra");
        await EnterThroughTriggerAsync(host, b, "Instcrb");

        CreatureWorldFeature feature = await host.PlayerStateAsync("Instcra", p => Services(p).GetRequiredService<CreatureWorldFeature>());
        Map first = await host.PlayerStateAsync("Instcra", p => p.Map!);
        Map second = await host.PlayerStateAsync("Instcrb", p => p.Map!);
        Assert.NotSame(first, second);
        var guid = Game.ObjectGuid.WithEntry(Game.HighGuid.Unit, 657, 79000);
        await host.WaitForWorldAsync(
            () => feature.FindSystem(first)?.FindCreature(guid) is not null && feature.FindSystem(second)?.FindCreature(guid) is not null,
            "both instances spawn the pirate");
        (Creature one, Creature two, CreatureMapSystem s1, CreatureMapSystem s2) = await host.OnWorldAsync(() =>
            (feature.FindSystem(first)!.FindCreature(guid)!, feature.FindSystem(second)!.FindCreature(guid)!, feature.FindSystem(first)!, feature.FindSystem(second)!));
        Assert.NotSame(s1, s2);
        Assert.NotSame(one, two);
        Assert.Same(first, one.Map);
        Assert.Same(second, two.Map);
    }

    [Fact]
    public async Task CharacterDelete_DropsTheBindInMemoryAndInStorage()
    {
        Assert.Contains(typeof(InstanceFeature), Features.WorldFeatures.FeatureTypes.Where(typeof(ICharacterDeleteHook).IsAssignableFrom));
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient keeper = await host.EnterWorldAsync("INSTKEEP", "Instkeep");
        byte[] key = await host.AddAccountAsync("INSTGONE");
        WorldTestClient victim = await host.ConnectAsync();
        await victim.AuthenticateAsync("INSTGONE", key);
        await victim.CreateCharacterAsync("Instgone");
        Account account = (await host.Accounts.FindByUsernameAsync("INSTGONE"))!;
        int id = (await host.Characters.GetByAccountAsync(account.Id)).Single(c => c.Name == "Instgone").Id;
        var guid = Game.ObjectGuid.Player((uint)id);
        await victim.LoginAsync((ulong)id);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Instgone") is not null, "Instgone enters the world");
        await EnterThroughTriggerAsync(host, victim, "Instgone");
        uint instance = await InstanceOfAsync(host, "Instgone");

        InstanceFeature feature = await host.PlayerStateAsync("Instkeep", p => Services(p).GetRequiredService<InstanceFeature>());
        InMemoryInstanceStore store = await host.PlayerStateAsync("Instkeep", p => Services(p).GetRequiredService<InMemoryInstanceStore>());
        Assert.Equal(instance, await host.OnWorldAsync(() => feature.Instances.GetPlayerBind(guid, Deadmines)?.Save.InstanceId));

        await victim.DisposeAsync();
        await host.WaitForWorldAsync(() => !host.World.IsOnline(guid), "Instgone leaves the world");
        await using (WorldTestClient again = await host.ConnectAsync())
        {
            await again.AuthenticateAsync("INSTGONE", key);
            byte[] raw = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(raw, (ulong)id);
            await again.SendAsync(WorldOpcode.CmsgCharDelete, raw);
            Assert.Equal((byte)CharResult.CharDeleteSuccess, (await again.ReadUntilAsync(WorldOpcode.SmsgCharDelete))[0]);
        }

        Assert.Null(await host.OnWorldAsync(() => feature.Instances.GetPlayerBind(guid, Deadmines)));
        await feature.FlushAsync();
        string[] writes = [.. store.Writes];
        int bound = Array.IndexOf(writes, $"bind {id} {instance} False");
        int purged = Array.IndexOf(writes, $"delete character {id}");
        Assert.True(bound >= 0 && purged > bound);
        Assert.True(Array.IndexOf(writes, $"unbind {id} {instance}") is int unbound && unbound > bound && unbound < purged);
    }

    private static IServiceProvider Services(Player player) => ((WorldSession)player.Session).Services;

    private static TeleportFeature TeleportFeatureOf(Player player) => Services(player).GetRequiredService<TeleportFeature>();

    private static Task<uint> InstanceOfAsync(WorldTestHost host, string name)
        => host.PlayerStateAsync(name, p => p.Map is { MapId: Deadmines } map ? map.InstanceId : throw new InvalidOperationException($"{name} is not in the dungeon"));

    private static async Task GroupAsync(WorldTestHost host, WorldTestClient leader, WorldTestClient member, string leaderName, string memberName)
    {
        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, CString(memberName));
        await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await host.WaitForWorldAsync(
            () => host.World.FindOnlinePlayer(leaderName) is { } p && Services(p).GetRequiredService<SocialFeature>().Context.Groups.GetGroup(p.Guid)?.MemberCount == 2,
            "the group forms");
    }

    // Stand in the trigger box at level 20 and walk through it: the dungeon transfer, then the ack.
    private static async Task EnterThroughTriggerAsync(WorldTestHost host, WorldTestClient client, string name)
    {
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer(name)!.Level = 20);
        await host.PlaceAsync(name, -8962f, -130f, 84f);
        await client.CollectAsync(Quiet);
        byte[] trigger = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trigger, InMemoryMapDataStore.DeadminesTrigger);
        await client.SendAsync(WorldOpcode.CmsgAreatrigger, trigger);
        Assert.Equal(Deadmines, BinaryPrimitives.ReadUInt32LittleEndian(await client.ReadUntilAsync(WorldOpcode.SmsgTransferPending)));
        byte[] newWorld = await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        Assert.Equal(Deadmines, BinaryPrimitives.ReadUInt32LittleEndian(newWorld));
        await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates); // the last login packet on the new map
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)?.Map?.MapId == Deadmines, $"{name} enters the dungeon");
    }

    private static byte[] CString(string text) => [.. Encoding.UTF8.GetBytes(text), 0];

    private sealed class WorldportAckMapObserver(Player player, TeleportService teleports) : IMapUpdater
    {
        private readonly TaskCompletionSource<int> _ackTransitCount = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _arrivalMapPass = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> AckTransitCount => _ackTransitCount.Task;

        public Task ArrivalMapPass => _arrivalMapPass.Task;

        public void OnPlayerRemoved(Map map, Player removed) { }

        public void Update(Map map, uint diffMs)
        {
            if (teleports.StageOf(player) == TeleportStage.Arriving)
            {
                _ackTransitCount.TrySetResult(map.TransitCount);
            }

            if (player.Map is { MapId: 0 } && map.TransitCount == 0)
            {
                _arrivalMapPass.TrySetResult(true);
            }
        }
    }
}
