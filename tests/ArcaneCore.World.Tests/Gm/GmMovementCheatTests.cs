using System.Collections.Concurrent;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.AntiCheat;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>
/// The GM movement cheats: <c>.cheat fly|fixedz|wallclimb|featherfall</c>, <c>.gm fly</c> and <c>.knockback</c> (vmangos CharacterCommands.cpp
/// HandleCheatFlyCommand/FixedZ/Wallclimb, UnitCommands.cpp HandleKnockBackCommand), the packets they send and the movement checks that stand
/// down while a cheat is on. These were written against a tree without the commands and have not been run against one: on origin/main
/// every command assertion fails (the commands do not exist; the file does not even compile, <c>PlayerCheats.Fly</c> and
/// <c>MovementCheats</c> are new), and the one that pins the authority change is
/// <see cref="FlyOn_ASwimmingLevitatingFlyingBlockInMidAir_IsAcceptedWithoutAFindingOrAnAnomaly_AndTheSameBlockWithoutTheCheatIsStillScored"/>:
/// its second half (the player without the cheat) is what the first half would see on main, a Flag finding of 80.
/// </summary>
public sealed class GmMovementCheatTests
{
    private const string GmLink = "|cffffffff|Hplayer:Movegm|h[Movegm]|h|r";

    private static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    private static (ulong Guid, MovementInfo Info) DecodeHeartbeat(byte[] payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadPackedGuid();
        MovementInfo info = MovementInfo.Read(ref reader);
        return (guid, info);
    }

    private static byte[] Encode(MovementInfo info)
    {
        var writer = new PacketWriter(64);
        info.Write(writer);
        return writer.ToArray();
    }

    private static async Task<WorldTestClient> EnterGmAsync(WorldTestHost host, string name = "Movegm")
    {
        WorldTestClient gm = await host.EnterWorldAsync(name.ToUpperInvariant(), name, AccountSecurity.GameMaster);
        await gm.CollectAsync();
        return gm;
    }

    private static async Task SelectAsync(WorldTestHost host, WorldTestClient gm, string gmName, string target)
    {
        Player player = await host.PlayerAsync(target);
        await gm.SendAsync(WorldOpcode.CmsgSetSelection, BitConverter.GetBytes(player.Guid.Value));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(gmName)!.Selection == player.Guid, $"{gmName} selecting {target}");
    }

    private static async Task SayAsync(WorldTestClient client, string command)
        => await client.SendChatAsync(ChatType.Say, Language.Common, command);

    [Fact]
    public void Commands_AreGameMasterLevelThree_AndAPlayerCannotResolveThem()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "cheat fly", "cheat fixedz", "cheat wallclimb", "cheat featherfall", "gm fly", "knockback" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Player));
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            ChatCommand? command = table.Resolve(path, AccountSecurity.GameMaster);
            Assert.NotNull(command);
            Assert.Equal(3, command.RequiredLevel(table.Gm));
        }
    }

    [Fact]
    public async Task CheatFly_On_SendsTheOwnClientAHeartbeatWithTheFourFlags_AndOff_SendsNone()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        ObjectGuid guid = await host.PlayerStateAsync("Movegm", p => p.Guid);

        await SayAsync(gm, ".cheat fly on");
        (ulong sender, MovementInfo on) = DecodeHeartbeat(await gm.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat));
        Assert.Equal(guid.Value, sender);
        Assert.Equal(0x01A00400u, (uint)on.Flags); // LEVITATING 0x400 | SWIMMING 0x200000 | MOVED 0x800000 | FLYING 0x1000000
        Assert.Equal(MovementCheats.FlyFlags, on.Flags);

        Assert.Contains("FLY MODE cheat is ON", Encoding.UTF8.GetString(await gm.ReadUntilAsync(WorldOpcode.SmsgNotification)));
        Assert.Equal($"You set fly mode to on for {GmLink}.", (await gm.ReadChatAsync()).Text);
        Assert.Equal("WARNING: Do not jump or flying mode will be removed.", (await gm.ReadChatAsync()).Text);
        (PlayerCheats cheats, MovementFlags flags) = await host.PlayerStateAsync("Movegm", p => (p.Cheats, p.Movement.Flags));
        Assert.Equal(PlayerCheats.Fly, cheats);
        Assert.Equal(0x01A00400u, (uint)flags);

        await SayAsync(gm, ".cheat status");
        Assert.Equal("Cheats active on Movegm:", (await gm.ReadChatAsync()).Text);
        Assert.Equal("- Fly", (await gm.ReadChatAsync()).Text);

        await SayAsync(gm, ".cheat fly off");
        (_, MovementInfo off) = DecodeHeartbeat(await gm.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat));
        Assert.Equal(0u, (uint)off.Flags);
        Assert.Contains("FLY MODE cheat is OFF", Encoding.UTF8.GetString(await gm.ReadUntilAsync(WorldOpcode.SmsgNotification)));
        Assert.Equal($"You set fly mode to off for {GmLink}.", (await gm.ReadChatAsync()).Text);
        (cheats, flags) = await host.PlayerStateAsync("Movegm", p => (p.Cheats, p.Movement.Flags));
        Assert.Equal(PlayerCheats.None, cheats);
        Assert.Equal(0u, (uint)flags);
    }

    [Fact]
    public async Task GmFly_IsTheSameCommand()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);

        await SayAsync(gm, ".gm fly on");
        (_, MovementInfo on) = DecodeHeartbeat(await gm.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat));
        Assert.Equal(0x01A00400u, (uint)on.Flags);
        Assert.Equal(PlayerCheats.Fly, await host.PlayerStateAsync("Movegm", p => p.Cheats));
        // The command also replied in chat ("You set fly mode ..." and the jump warning); read those so the next read sees the next reply.
        Assert.Equal($"You set fly mode to on for {GmLink}.", (await gm.ReadChatAsync()).Text);
        Assert.Equal("WARNING: Do not jump or flying mode will be removed.", (await gm.ReadChatAsync()).Text);

        await SayAsync(gm, ".gm fly off");
        (_, MovementInfo off) = DecodeHeartbeat(await gm.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat));
        Assert.Equal(0u, (uint)off.Flags);
        Assert.Equal(PlayerCheats.None, await host.PlayerStateAsync("Movegm", p => p.Cheats));
        Assert.Equal($"You set fly mode to off for {GmLink}.", (await gm.ReadChatAsync()).Text);

        await SayAsync(gm, ".gm fly maybe");
        Assert.Equal("Incorrect value, use on or off", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task CheatFly_OnTheSelectedPlayer_FliesThemAndTellsThem_AndAHigherRankIsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        await using WorldTestClient flyer = await host.EnterWorldAsync("FLYER", "Flyer");
        await using WorldTestClient admin = await host.EnterWorldAsync("FLYADMIN", "Flyadmin", AccountSecurity.Administrator);
        await flyer.CollectAsync();
        await admin.CollectAsync();

        await SelectAsync(host, gm, "Movegm", "Flyer");
        await SayAsync(gm, ".cheat fly on");
        (_, MovementInfo heartbeat) = DecodeHeartbeat(await flyer.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat));
        Assert.Equal(0x01A00400u, (uint)heartbeat.Flags);
        Assert.Equal($"You set fly mode to on for {Link("Flyer")}.", (await gm.ReadChatAsync()).Text);
        Assert.Equal($"Your fly mode has been turned on by {GmLink}.", (await flyer.ReadChatAsync()).Text);
        Assert.Equal("WARNING: Do not jump or flying mode will be removed.", (await flyer.ReadChatAsync()).Text);
        Assert.Equal(PlayerCheats.Fly, await host.PlayerStateAsync("Flyer", p => p.Cheats));

        await SelectAsync(host, gm, "Movegm", "Flyadmin");
        await SayAsync(gm, ".cheat fly on");
        Assert.Equal("You have low security level for this.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(PlayerCheats.None, await host.PlayerStateAsync("Flyadmin", p => p.Cheats));
    }

    [Fact]
    public async Task CheatFixedZ_AddsTheFlag0x800_AndOffRemovesIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        MovementFlags before = await host.PlayerStateAsync("Movegm", p => p.Movement.Flags);

        await SayAsync(gm, ".cheat fixedz on");
        (_, MovementInfo on) = DecodeHeartbeat(await gm.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat));
        Assert.Equal(0x800u, (uint)on.Flags & 0x800u);
        Assert.Equal(before | MovementFlags.FixedZ, on.Flags);
        Assert.Contains("FIXED Z cheat is ON", Encoding.UTF8.GetString(await gm.ReadUntilAsync(WorldOpcode.SmsgNotification)));
        Assert.Equal($"You set fixed z mode to on for {GmLink}.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(PlayerCheats.FixedZ, await host.PlayerStateAsync("Movegm", p => p.Cheats));

        await SayAsync(gm, ".cheat status");
        Assert.Equal("Cheats active on Movegm:", (await gm.ReadChatAsync()).Text);
        Assert.Equal("- Fixed Z", (await gm.ReadChatAsync()).Text);

        await SayAsync(gm, ".cheat fixedz off");
        (_, MovementInfo off) = DecodeHeartbeat(await gm.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat));
        Assert.Equal(before, off.Flags);
        Assert.Equal($"You set fixed z mode to off for {GmLink}.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(PlayerCheats.None, await host.PlayerStateAsync("Movegm", p => p.Cheats));
    }

    [Fact]
    public async Task CheatWallClimb_SetsServerControlledOnTheSelectedPlayer_AndStatusListsIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        await using WorldTestClient climber = await host.EnterWorldAsync("CLIMBER", "Climber");
        await climber.CollectAsync();

        await SelectAsync(host, gm, "Movegm", "Climber");
        await SayAsync(gm, ".cheat wallclimb on");
        Assert.Equal($"You set wallclimb mode to on for {Link("Climber")}.", (await gm.ReadChatAsync()).Text);
        Assert.Equal($"Your wallclimb mode has been turned on by {GmLink}.", (await climber.ReadChatAsync()).Text);
        Assert.Equal(1u, (uint)await host.PlayerStateAsync("Climber", p => p.UnitFlags & UnitFlags.ServerControlled));

        await SayAsync(gm, ".cheat status");
        Assert.Equal("Cheats active on Climber:", (await gm.ReadChatAsync()).Text);
        Assert.Equal("- Wall climbing", (await gm.ReadChatAsync()).Text);

        await SayAsync(gm, ".cheat wallclimb off");
        Assert.Equal($"You set wallclimb mode to off for {Link("Climber")}.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(0u, (uint)await host.PlayerStateAsync("Climber", p => p.UnitFlags & UnitFlags.ServerControlled));
    }

    [Fact]
    public async Task CheatFeatherFall_SendsTheFeatherFallOrder_242_AndTheNormalFallOrder_243()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        ObjectGuid guid = await host.PlayerStateAsync("Movegm", p => p.Guid);

        await SayAsync(gm, ".cheat featherfall on");
        byte[] on = await gm.ReadUntilAsync(WorldOpcode.SmsgMoveFeatherFall);
        Assert.Equal(242, (int)WorldOpcode.SmsgMoveFeatherFall);
        Assert.Equal(guid.Value, new PacketReader(on).ReadPackedGuid());
        Assert.Equal($"You set feather fall mode to on for {GmLink}.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(PlayerCheats.FeatherFall, await host.PlayerStateAsync("Movegm", p => p.Cheats));

        await SayAsync(gm, ".cheat status");
        Assert.Equal("Cheats active on Movegm:", (await gm.ReadChatAsync()).Text);
        Assert.Equal("- Feather fall", (await gm.ReadChatAsync()).Text);

        await SayAsync(gm, ".cheat featherfall off");
        byte[] off = await gm.ReadUntilAsync(WorldOpcode.SmsgMoveNormalFall);
        Assert.Equal(243, (int)WorldOpcode.SmsgMoveNormalFall);
        Assert.Equal(guid.Value, new PacketReader(off).ReadPackedGuid());
        Assert.Equal($"You set feather fall mode to off for {GmLink}.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(PlayerCheats.None, await host.PlayerStateAsync("Movegm", p => p.Cheats));
    }

    [Fact]
    public async Task APlayerWithoutGameMasterSecurity_IsRefused_AndNothingChanges()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await player.CollectAsync();

        foreach (string command in new[] { ".cheat fly on", ".gm fly on", ".cheat fixedz on", ".cheat wallclimb on", ".cheat featherfall on", ".knockback" })
        {
            await SayAsync(player, command);
            string reply = (await player.ReadChatAsync()).Text;
            Assert.True(
                reply.StartsWith("This command is not available to you", StringComparison.Ordinal)
                || reply.StartsWith("There is no such command", StringComparison.Ordinal)
                || reply.StartsWith("There is no such subcommand", StringComparison.Ordinal),
                $"{command}: {reply}");
        }

        (PlayerCheats cheats, MovementFlags flags, UnitFlags unit) = await host.PlayerStateAsync("Plain", p => (p.Cheats, p.Movement.Flags, p.UnitFlags));
        Assert.Equal(PlayerCheats.None, cheats);
        Assert.Equal(0u, (uint)flags & 0x01A00C00u);
        Assert.Equal(0u, (uint)(unit & UnitFlags.ServerControlled));
        Assert.DoesNotContain(await player.CollectAsync(), p => p.Opcode is WorldOpcode.MsgMoveHeartbeat or WorldOpcode.SmsgMoveFeatherFall or WorldOpcode.SmsgMoveKnockBack);
    }

    [Fact]
    public async Task Knockback_OnYourself_SendsTheOrder_WithTheDefaultTenAndTen_AndTheGivenSpeeds()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        (ObjectGuid guid, float orientation) = await host.PlayerStateAsync("Movegm", p => (p.Guid, p.Orientation));
        float angle = orientation + MathF.PI; // a knock back of yourself runs away from where you face

        await SayAsync(gm, ".knockback");
        (_, KnockbackInfo first) = ReadOrder(await gm.ReadUntilAsync(WorldOpcode.SmsgMoveKnockBack), guid);
        Assert.Equal(new KnockbackInfo(MathF.Cos(angle), MathF.Sin(angle), 10f, -10f), first); // the vertical speed goes out negated

        await SayAsync(gm, ".knockback 5 3");
        (_, KnockbackInfo second) = ReadOrder(await gm.ReadUntilAsync(WorldOpcode.SmsgMoveKnockBack), guid);
        Assert.Equal(new KnockbackInfo(MathF.Cos(angle), MathF.Sin(angle), 5f, -3f), second);

        await SayAsync(gm, ".knockback nonsense");
        (_, KnockbackInfo third) = ReadOrder(await gm.ReadUntilAsync(WorldOpcode.SmsgMoveKnockBack), guid);
        Assert.Equal(new KnockbackInfo(MathF.Cos(angle), MathF.Sin(angle), 10f, -10f), third); // an unreadable number leaves the default, as vmangos
    }

    private static (uint Counter, KnockbackInfo Info) ReadOrder(byte[] payload, ObjectGuid guid)
    {
        var reader = new PacketReader(payload);
        Assert.Equal(guid.Value, reader.ReadPackedGuid());
        uint counter = reader.ReadUInt32();
        return (counter, new KnockbackInfo(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
    }

    [Fact]
    public async Task Knockback_OnAnotherPlayer_PushesThemAwayFromTheGm()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        await using WorldTestClient other = await host.EnterWorldAsync("BOUNCED", "Bounced");
        await other.CollectAsync();
        ObjectGuid guid = await host.PlayerStateAsync("Bounced", p => p.Guid);

        await SelectAsync(host, gm, "Movegm", "Bounced");
        await SayAsync(gm, ".knockback 8 4");
        (_, KnockbackInfo info) = ReadOrder(await other.ReadUntilAsync(WorldOpcode.SmsgMoveKnockBack), guid);
        Assert.Equal(8f, info.SpeedXY);
        Assert.Equal(-4f, info.SpeedZ);
    }

    [Fact]
    public async Task CheatStatus_ListsEveryMovementCheat_InTheVmangosOrder()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        foreach (string on in new[] { ".cheat fly on", ".cheat fixedz on", ".cheat featherfall on", ".cheat god on" })
        {
            await SayAsync(gm, on);
        }

        await gm.CollectAsync();
        await SayAsync(gm, ".cheat status");
        string[] expected = ["Cheats active on Movegm:", "- Fly", "- God", "- Fixed Z", "- Feather fall"];
        foreach (string line in expected)
        {
            Assert.Equal(line, (await gm.ReadChatAsync()).Text);
        }
    }

    // --- the movement checks -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CheatFly_WithACreatureSelected_FliesTheInvoker()
    {
        // vmangos HandleCheatFlyCommand: getSelectedPlayer() is null for a creature, and the handler falls back to the invoker (CharacterCommands.cpp:105-107).
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        const uint creatureEntry = 4201;
        const uint creatureLow = 4201;
        var template = new CreatureTemplate { Entry = creatureEntry, Name = "Selected Dummy", MinLevel = 5, MaxLevel = 5, MinLevelHealth = 60, MaxLevelHealth = 60, Faction = 35 };
        (float gmX, float gmY, float gmZ) = await host.PlayerStateAsync("Movegm", p => (p.X, p.Y, p.Z));
        var spawn = new CreatureSpawn { Guid = creatureLow, Entry = creatureEntry, MapId = 0, X = gmX + 1, Y = gmY, Z = gmZ };
        var creature = new Creature(creatureLow, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
        await host.OnWorldAsync(() => host.World.GetMap(0).AddObject(creature));

        ulong creatureGuid = creature.Guid.Value;
        await gm.SendAsync(WorldOpcode.CmsgSetSelection, BitConverter.GetBytes(creatureGuid));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Movegm")!.Selection.Value == creatureGuid, "the creature selection");

        foreach (string command in new[] { ".cheat fly on", ".cheat fixedz on", ".cheat wallclimb on" })
        {
            await SayAsync(gm, command);
        }

        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Movegm")!.Cheats == (PlayerCheats.Fly | PlayerCheats.FixedZ), "fly and fixed z on the invoker");
        Assert.True(await host.PlayerStateAsync("Movegm", p => (p.UnitFlags & UnitFlags.ServerControlled) != 0));
    }

    [Fact]
    public async Task AFarTeleport_EndsFlyAndFixedZ_OnlyAfterArrival_ButANearOneKeepsThem()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Movegm")!;
            p.Cheats = PlayerCheats.Fly | PlayerCheats.FixedZ | PlayerCheats.NoCooldown;
            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            Assert.True(teleports.TeleportTo(p, p.MapId, p.X + 3, p.Y, p.Z, p.Orientation));
        });
        Assert.Equal(PlayerCheats.Fly | PlayerCheats.FixedZ | PlayerCheats.NoCooldown, await host.PlayerStateAsync("Movegm", p => p.Cheats));

        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Movegm")!;
            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            Assert.True(teleports.TeleportTo(p, p.MapId, p.X, p.Y, p.Z, p.Orientation, TeleportOptions.ForceFar));
        });
        await gm.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        Assert.Equal(PlayerCheats.Fly | PlayerCheats.FixedZ | PlayerCheats.NoCooldown, await host.PlayerStateAsync("Movegm", p => p.Cheats));

        await gm.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Movegm")?.Map is not null, "the player to arrive in the world");
        Assert.Equal(PlayerCheats.NoCooldown, await host.PlayerStateAsync("Movegm", p => p.Cheats));
    }

    [Fact]
    public async Task AFarTeleport_ThatBailsOutOrIsRefused_KeepsFlyAndFixedZCheats()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        Guid op = Guid.NewGuid();
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Movegm")!;
            p.Cheats = PlayerCheats.Fly | PlayerCheats.FixedZ | PlayerCheats.NoCooldown;
            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            Assert.True(teleports.TeleportTo(p, p.MapId, p.X, p.Y, p.Z, p.Orientation, TeleportOptions.ForceFar));
            Assert.True(p.BeginQuestSettlement(op));
        });

        await host.OnWorldAsync(() => true);
        Assert.Equal(PlayerCheats.Fly | PlayerCheats.FixedZ | PlayerCheats.NoCooldown, await host.PlayerStateAsync("Movegm", p => p.Cheats));
        Assert.NotNull(await host.PlayerStateAsync("Movegm", p => p.Map));
    }

    [Fact]
    public async Task CheatFlyOff_KeepsTheFlagsFlightDoesNotOwn()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Movegm")!;
            p.Cheats = PlayerCheats.Fly;
            p.SetMovementFlags(MovementCheats.FlyFlags | MovementFlags.WaterWalking | MovementFlags.SafeFall | MovementFlags.Root);
        });

        await SayAsync(gm, ".cheat fly off");
        (_, MovementInfo off) = DecodeHeartbeat(await gm.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat));
        Assert.Equal(MovementFlags.WaterWalking | MovementFlags.SafeFall | MovementFlags.Root, off.Flags);
        Assert.Equal(MovementFlags.WaterWalking | MovementFlags.SafeFall | MovementFlags.Root, await host.PlayerStateAsync("Movegm", p => p.Movement.Flags));
        Assert.Equal(PlayerCheats.None, await host.PlayerStateAsync("Movegm", p => p.Cheats));
    }

    [Fact]
    public async Task AJumpEndsFlyMode_AndOnlyFlyMode()
    {
        // The command's own warning: "Do not jump or flying mode will be removed." The client walks again after a jump, so the cheat must not stay.
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("JUMPER", "Jumper");
        await client.CollectAsync();
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Jumper")!.Cheats = PlayerCheats.Fly | PlayerCheats.FeatherFall | PlayerCheats.FixedZ);

        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, FallBlock(MovementCheats.FlyFlags, 100));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Jumper")!.Movement.Z == 100f, "the flying block");
        Assert.Equal(PlayerCheats.Fly | PlayerCheats.FeatherFall | PlayerCheats.FixedZ, await host.PlayerStateAsync("Jumper", p => p.Cheats));

        await client.SendAsync(WorldOpcode.MsgMoveJump, FallBlock(MovementFlags.Jumping, 101));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Jumper")!.Movement.Z == 101f, "the jump block");
        Assert.Equal(PlayerCheats.FeatherFall | PlayerCheats.FixedZ, await host.PlayerStateAsync("Jumper", p => p.Cheats));
    }

    [Fact]
    public void FlyGrantsTheLevitatingAndFlyingFlags_FixedZGrantsNone()
    {
        var table = new Dictionary<PlayerCheats, MovementFlags>
        {
            [PlayerCheats.Fly] = MovementFlags.Levitating | MovementFlags.Flying,
            [PlayerCheats.FixedZ] = MovementFlags.None,
            [PlayerCheats.WaterWalk] = MovementFlags.WaterWalking,
            [PlayerCheats.FeatherFall] = MovementFlags.SafeFall,
        };
        Assert.Equal(MovementFlags.Levitating | MovementFlags.Flying, MovementCheats.GrantedFor(PlayerCheats.Fly));
        foreach ((PlayerCheats cheat, MovementFlags flags) in table)
        {
            Assert.Equal(flags, MovementCheats.GrantedFor(cheat));
        }
    }

    private static AntiCheatFeature AntiCheatOf(WorldTestHost host) => host.WorldServices.GetRequiredService<AntiCheatFeature>();

    [Fact]
    public async Task FlyOn_ASwimmingLevitatingFlyingBlockInMidAir_IsAcceptedWithoutAFindingOrAnAnomaly_AndTheSameBlockWithoutTheCheatIsStillScored()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.OnWorldAsync(() => AntiCheatOf(host).ApplyOptions(new AntiCheatOptions
        {
            Action = AntiCheatAction.Rubberband, ScoreGmAlert = 1, ScoreRubberband = 1, ScoreKick = 1000, BaselineGapMs = 600_000,
        }));
        var seen = new ConcurrentQueue<(string Player, AntiCheatFinding Finding)>();
        AntiCheatOf(host).FindingRecorded += (player, finding, _, _) => seen.Enqueue((player.Name, finding));

        await using WorldTestClient gm = await EnterGmAsync(host);
        await using WorldTestClient flyer = await host.EnterWorldAsync("FLYER", "Flyer");
        await using WorldTestClient walker = await host.EnterWorldAsync("WALKER", "Walker");
        await flyer.CollectAsync();
        await walker.CollectAsync();
        using AnomalyCapture anomalies = AnomalyLedger.Shared.Capture(n =>
            n.Subject.StartsWith("player Flyer ", StringComparison.Ordinal) || n.Subject.StartsWith("player Walker ", StringComparison.Ordinal));

        await SelectAsync(host, gm, "Movegm", "Flyer");
        await SayAsync(gm, ".cheat fly on");
        Assert.Equal($"You set fly mode to on for {Link("Flyer")}.", (await gm.ReadChatAsync()).Text);

        // The first 100 ms after the order are rejected (RejectMovementPacketsFor); keep sending until a block is stored.
        (float x, float y, float z) = await host.PlayerStateAsync("Flyer", p => (p.X, p.Y, p.Z));
        uint time = 100_000;
        float wantedX = x + 1f;
        for (int attempt = 0; attempt < 100 && MathF.Abs(await host.PlayerStateAsync("Flyer", p => p.Movement.X) - wantedX) > 0.01f; attempt++)
        {
            await flyer.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(FlyingBlock(time += 30, wantedX, y, z + 30f)));
            await Task.Delay(30);
        }

        // Then a flight: forward and up, swimming in the air, far above any ground.
        for (int step = 1; step <= 5; step++)
        {
            wantedX += 0.5f;
            await Task.Delay(100);
            await flyer.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(FlyingBlock(time += 100, wantedX, y, z + 30f + (step * 4f))));
        }

        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Flyer")!.Movement.X == wantedX, "the last flight block to be stored");
        (MovementFlags stored, float storedZ) = await host.PlayerStateAsync("Flyer", p => (p.Movement.Flags, p.Movement.Z));
        Assert.Equal(MovementCheats.FlyFlags, stored & MovementCheats.FlyFlags);
        Assert.Equal(z + 50f, storedZ, 2);                                  // never rubber-banded back to the ground
        Assert.DoesNotContain(seen, s => s.Player == "Flyer");              // no finding, so no score, alert or rubberband
        Assert.False(anomalies.Notices.Any(n => n.Subject.StartsWith("player Flyer ", StringComparison.Ordinal)), anomalies.Describe());

        // Control: the same block from a player nobody gave the cheat is the flag hack the checks exist for.
        (float wx, float wy, float wz) = await host.PlayerStateAsync("Walker", p => (p.X, p.Y, p.Z));
        await walker.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(FlyingBlock(200_000, wx + 1f, wy, wz + 30f)));
        await host.WaitForWorldAsync(() => seen.Any(s => s.Player == "Walker"), "the Walker's flag finding");
        Assert.Contains(seen, s => s.Player == "Walker" && s.Finding.Type == AntiCheatViolation.Flag);
    }

    private static MovementInfo FlyingBlock(uint time, float x, float y, float z) => new()
    {
        Flags = MovementCheats.FlyFlags, Time = time, X = x, Y = y, Z = z, Orientation = 0.5f, Pitch = 0.3f,
    };

    [Fact]
    public async Task FlyOff_TheSameBlockIsJudgedAgain()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.OnWorldAsync(() => AntiCheatOf(host).ApplyOptions(new AntiCheatOptions
        {
            Action = AntiCheatAction.Log, BaselineGapMs = 600_000,
        }));
        var seen = new ConcurrentQueue<(string Player, AntiCheatFinding Finding)>();
        AntiCheatOf(host).FindingRecorded += (player, finding, _, _) => seen.Enqueue((player.Name, finding));
        await using WorldTestClient gm = await EnterGmAsync(host);
        await using WorldTestClient flyer = await host.EnterWorldAsync("FLYER", "Flyer");
        await flyer.CollectAsync();

        await SelectAsync(host, gm, "Movegm", "Flyer");
        await SayAsync(gm, ".cheat fly on");
        await gm.ReadChatAsync();
        await SayAsync(gm, ".cheat fly off");
        await gm.ReadChatAsync();
        Assert.Equal(PlayerCheats.None, await host.PlayerStateAsync("Flyer", p => p.Cheats));

        (float x, float y, float z) = await host.PlayerStateAsync("Flyer", p => (p.X, p.Y, p.Z));
        uint time = 300_000;
        for (int attempt = 0; attempt < 100 && !seen.Any(s => s.Player == "Flyer"); attempt++)
        {
            await flyer.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(FlyingBlock(time += 30, x + 1f, y, z + 30f)));
            await Task.Delay(30);
        }

        Assert.Contains(seen, s => s.Player == "Flyer" && s.Finding.Type == AntiCheatViolation.Flag);
    }

    private static byte[] FallBlock(MovementFlags flags, float z, uint fallTime = 0)
        => Encode(new MovementInfo
        {
            Flags = flags, Time = 100, X = -8949.95f, Y = -132.493f, Z = z, Orientation = 1, FallTime = fallTime,
            JumpZSpeed = -7.95f, JumpCosAngle = 1, JumpSinAngle = 0, JumpXySpeed = 7,
        });

    [Fact]
    public Task AFortyYardFall_DoesNotHurt_WhileFeatherFallIsOn() => AFortyYardFall_DoesNotHurt(PlayerCheats.FeatherFall);

    [Theory]
    [InlineData(PlayerCheats.Fly)]
    [InlineData(PlayerCheats.FixedZ)]
    public async Task AFortyYardFall_StillHurts_UnderFlyOrFixedZ(PlayerCheats cheat)
    {
        // vmangos reads PLAYER_CHEAT_FLY / PLAYER_CHEAT_FIXED_Z only in the flag checks (MovementAnticheat.cpp:789-806, 1090): a player who
        // really falls (a jump removes flying) is hurt as usual, 1432 of 3000 health.
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("FALLER", "Faller");
        await client.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Faller")!;
            p.MaxHealth = 3000;
            p.Health = 3000;
            p.Cheats = cheat;
        });

        // A heartbeat, not MSG_MOVE_JUMP: a jump ends fly mode (MovementCheats.EndFlyOnJump), and this row is about a fall while the cheat is still on.
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, FallBlock(MovementFlags.Jumping, 100));
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, FallBlock(MovementFlags.Jumping | MovementFlags.FallingFar, 80, 1500));
        await host.OnWorldAsync(() => true);
        Assert.Equal(cheat, await host.PlayerStateAsync("Faller", p => p.Cheats));
        await client.SendAsync(WorldOpcode.MsgMoveFallLand, FallBlock(MovementFlags.None, 60, 3000));
        byte[] log = await client.ReadUntilAsync(WorldOpcode.SmsgEnvironmentaldamagelog);
        Assert.Equal(1432u, BitConverter.ToUInt32(log, 9));
    }

    private static async Task AFortyYardFall_DoesNotHurt(PlayerCheats cheat)
    {
        // Without a cheat the same fall costs 1432 of 3000 health (FallDamageEndToEndTests.AFortyYardFall_SendsOneFallDamageLog_AndHurtsThePlayer).
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("FALLER", "Faller");
        await client.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Faller")!;
            p.MaxHealth = 3000;
            p.Health = 3000;
            p.Cheats = cheat;
        });

        await client.SendAsync(WorldOpcode.MsgMoveJump, FallBlock(MovementFlags.Jumping, 100));
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, FallBlock(MovementFlags.Jumping | MovementFlags.FallingFar, 80, 1500));
        await client.SendAsync(WorldOpcode.MsgMoveFallLand, FallBlock(MovementFlags.None, 60, 3000));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Faller")!.Movement.Z == 60f, "the landing to be stored");

        Assert.Equal(3000u, await host.PlayerStateAsync("Faller", p => p.Health));
        Assert.DoesNotContain(await client.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog);
    }

    [Theory]
    [InlineData(PlayerCheats.Fly)]
    [InlineData(PlayerCheats.FixedZ)]
    public async Task BelowTheVoidHeight_StillTakesHalfTheHealth_UnderFlyOrFixedZ(PlayerCheats cheat)
    {
        // The void kill (-500) is not one of the places vmangos reads the two cheats (MovementAnticheat.cpp:789-806, 1090).
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("VOID", "Voider");
        await client.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Voider")!;
            p.MaxHealth = 2000;
            p.Health = 2000;
            p.Cheats = cheat;
        });

        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, FallBlock(MovementFlags.Jumping | MovementFlags.FallingFar, -501f, 5000));
        byte[] log = await client.ReadUntilAsync(WorldOpcode.SmsgEnvironmentaldamagelog);
        Assert.Equal(1000u, BitConverter.ToUInt32(log, 9));
    }

    [Fact]
    public async Task FlyOn_ASpeedHopIsStillAnAnomaly_LikeTheSameHopWithoutTheCheat()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient flyer = await host.EnterWorldAsync("HOPFLYER", "Hopflyer");
        await using WorldTestClient walker = await host.EnterWorldAsync("HOPWALKER", "Hopwalker");
        await flyer.CollectAsync();
        await walker.CollectAsync();
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Hopflyer")!.Cheats = PlayerCheats.Fly);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n =>
            n.Key == "move/speed-violation" && (n.Subject.StartsWith("player Hopflyer ", StringComparison.Ordinal) || n.Subject.StartsWith("player Hopwalker ", StringComparison.Ordinal)));

        foreach ((WorldTestClient client, string name) in new[] { (flyer, "Hopflyer"), (walker, "Hopwalker") })
        {
            (float x, float y, float z) = await host.PlayerStateAsync(name, p => (p.X, p.Y, p.Z));
            await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(new MovementInfo { Time = 5_000_000, X = x + 1, Y = y, Z = z, Orientation = 0.5f }));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.Movement.X == x + 1, "the first heartbeat");
            await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(new MovementInfo { Time = 5_001_000, X = x + 201, Y = y, Z = z, Orientation = 0.5f }));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.Movement.X == x + 201, "the hop");
        }

        // Neither cheat is a speed grant (vmangos reads PLAYER_CHEAT_FLY only for the levitating and swim-fly flags).
        Assert.Equal(2, capture.Count("move/speed-violation"));
        Assert.Contains(capture.Notices, n => n.Subject.StartsWith("player Hopflyer ", StringComparison.Ordinal));
        Assert.Contains(capture.Notices, n => n.Subject.StartsWith("player Hopwalker ", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectWindow_DropsPacketsReceivedUpToTheDeadline_NeverShortens_AndSurvivesTheClockWrapping()
    {
        var state = new LocomotionState();
        Assert.False(state.ShouldRejectMovement(1000)); // nothing ordered

        state.RejectMovementPacketsFor(1000, MovementCheats.RejectWindowMs);
        Assert.True(state.ShouldRejectMovement(1000));
        Assert.True(state.ShouldRejectMovement(1100)); // "stime <= m_moveRejectTime"
        state.RejectMovementPacketsFor(1000, 10);       // a shorter window does not replace it
        Assert.True(state.ShouldRejectMovement(1100));
        Assert.False(state.ShouldRejectMovement(1101));
        Assert.False(state.ShouldRejectMovement(1000)); // expired for good

        var wrapping = new LocomotionState();
        wrapping.RejectMovementPacketsFor(uint.MaxValue - 50, 100); // the deadline is 49 on the far side of the wrap
        Assert.True(wrapping.ShouldRejectMovement(uint.MaxValue - 10));
        Assert.True(wrapping.ShouldRejectMovement(10));
        Assert.True(wrapping.ShouldRejectMovement(49));
        Assert.False(wrapping.ShouldRejectMovement(50));
    }

    [Fact]
    public async Task TheCheatsEndWithTheSession_ALogoutAndLoginStartsClean()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await EnterGmAsync(host);
        ObjectGuid guid = await host.PlayerStateAsync("Movegm", p => p.Guid);
        foreach (string on in new[] { ".cheat fly on", ".cheat fixedz on", ".cheat featherfall on", ".cheat waterwalk on" })
        {
            await SayAsync(gm, on);
        }

        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Movegm")!.Cheats
            == (PlayerCheats.Fly | PlayerCheats.FixedZ | PlayerCheats.FeatherFall | PlayerCheats.WaterWalk), "every cheat to be on");

        await gm.CollectAsync();
        await gm.SendAsync(WorldOpcode.CmsgLogoutRequest, []); // staff log out at once
        await gm.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await WorldTestHost.WaitForAsync(() => !host.World.IsOnline(guid), "the logout");

        await gm.SendAsync(WorldOpcode.CmsgCharEnum, []);
        await gm.ReadUntilAsync(WorldOpcode.SmsgCharEnum);
        await gm.LoginAsync(guid.Low);
        (PlayerCheats cheats, MovementFlags flags) = await host.PlayerStateAsync("Movegm", p => (p.Cheats, p.Movement.Flags));
        Assert.Equal(PlayerCheats.None, cheats);
        Assert.Equal(0u, (uint)flags & 0x01A00C00u);
    }
}
