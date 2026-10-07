using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// M6 end to end: logout (countdown, cancel, instant, refusals) with its root/stand-state
/// orders and acks, small player requests, and the chat commands with their security levels.
/// </summary>
public sealed class M6LogoutAndCommandTests
{
    /// <summary>The packed GUID of character 1: mask 0x01, then the single non-zero byte.</summary>
    private static readonly byte[] PackedGuid1 = [0x01, 0x01];

    // --- logout ----------------------------------------------------------------------

    [Fact]
    public async Task Logout_CountsDown_ThenReturnsToTheCharacterScreen()
    {
        await using var host = WorldTestHost.Start(configure: o => o.LogoutDelayMs = 300);
        await using WorldTestClient leaver = await host.EnterWorldAsync("LEAVER", "Leaver");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await Drain(leaver, watcher);
        int savesBefore = host.Characters.SaveCount;

        await leaver.SendAsync(WorldOpcode.CmsgLogoutRequest, []);

        // vmangos HandleLogoutRequestOpcode: sit, root, then the response.
        AssertPacket(WorldOpcode.SmsgStandstateUpdate, new byte[] { (byte)StandState.Sit }, await leaver.ReadAsync());
        AssertPacket(WorldOpcode.SmsgForceMoveRoot, (byte[])[.. PackedGuid1, 0, 0, 0, 0], await leaver.ReadAsync());
        AssertPacket(WorldOpcode.SmsgLogoutResponse, new byte[] { 0, 0, 0, 0, 0 }, await leaver.ReadAsync());
        Assert.True(await host.PlayerStateAsync("Leaver", p => p.IsLoggingOut && (p.UnitFlags & UnitFlags.Stunned) != 0));

        Assert.Empty(await leaver.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete));
        Assert.Equal(U64(1), await watcher.ReadUntilAsync(WorldOpcode.SmsgDestroyObject));
        Assert.False(host.World.IsOnline(ObjectGuid.Player(1)));
        await WorldTestHost.WaitForAsync(() => host.Characters.SaveCount > savesBefore, "the logout save");

        // Back at the character screen: the list works and the character can log in again.
        await leaver.SendAsync(WorldOpcode.CmsgCharEnum, []);
        Assert.Equal(1, (await leaver.ReadUntilAsync(WorldOpcode.SmsgCharEnum))[0]);
        await leaver.LoginAsync(1);
        Assert.False(await host.PlayerStateAsync("Leaver", p => p.IsLoggingOut));
    }

    [Fact]
    public async Task LogoutCancel_UnrootsStandsUp_AndStopsTheCountdown()
    {
        await using var host = WorldTestHost.Start(configure: o => o.LogoutDelayMs = 300);
        await using WorldTestClient client = await host.EnterWorldAsync("STAYER", "Stayer");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutResponse);
        await client.SendAsync(WorldOpcode.CmsgLogoutCancel, []);

        Assert.Empty(await client.ReadUntilAsync(WorldOpcode.SmsgLogoutCancelAck));
        AssertPacket(WorldOpcode.SmsgForceMoveUnroot, (byte[])[.. PackedGuid1, 1, 0, 0, 0], await client.ReadAsync()); // counter advanced
        AssertPacket(WorldOpcode.SmsgStandstateUpdate, new byte[] { (byte)StandState.Stand }, await client.ReadAsync());

        await Task.Delay(600);
        Assert.DoesNotContain(await client.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgLogoutComplete);
        Assert.False(await host.PlayerStateAsync("Stayer", p => p.IsLoggingOut || (p.UnitFlags & UnitFlags.Stunned) != 0));
    }

    [Fact]
    public async Task Logout_IsInstant_ForStaffAndWhileResting()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Moderator);
        await using WorldTestClient rester = await host.EnterWorldAsync("RESTER", "Rester");
        await Drain(gm, rester);

        await gm.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1 }, await gm.ReadUntilAsync(WorldOpcode.SmsgLogoutResponse));
        Assert.Equal(WorldOpcode.SmsgLogoutComplete, (await gm.ReadAsync()).Opcode);

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rester")!.Flags |= PlayerFlags.Resting);
        await rester.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1 }, await rester.ReadUntilAsync(WorldOpcode.SmsgLogoutResponse));
        await rester.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
    }

    [Fact]
    public async Task Logout_IsRefused_WhileFallingOrInCombat()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("FALLER", "Faller");
        await client.CollectAsync();

        var jump = new MovementInfo
        {
            Flags = MovementFlags.Forward | MovementFlags.Jumping, Time = 100,
            X = -8949.95f, Y = -132.493f, Z = 84f, JumpZSpeed = -7.95f, JumpCosAngle = 1, JumpXySpeed = 7,
        };
        var packet = new PacketWriter(64);
        jump.Write(packet);
        await client.SendAsync(WorldOpcode.MsgMoveJump, packet.ToArray());
        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        Assert.Equal(new byte[] { (byte)LogoutResult.JumpingOrFalling, 0, 0, 0, 0 }, await client.ReadUntilAsync(WorldOpcode.SmsgLogoutResponse));

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Faller")!.UnitFlags |= UnitFlags.InCombat);
        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        Assert.Equal(new byte[] { (byte)LogoutResult.InCombat, 0, 0, 0, 0 }, await client.ReadUntilAsync(WorldOpcode.SmsgLogoutResponse));
        Assert.False(await host.PlayerStateAsync("Faller", p => p.IsLoggingOut));
    }

    [Fact]
    public async Task RootAck_IsRelayedToObservers_AndStaleAcksAreNot()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient rooted = await host.EnterWorldAsync("ROOTED", "Rooted");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await Drain(rooted, watcher);

        await rooted.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await rooted.ReadUntilAsync(WorldOpcode.SmsgLogoutResponse);

        // An unroot ack while rooted is stale: ignored.
        await rooted.SendAsync(WorldOpcode.CmsgForceMoveUnrootAck, RootAck(counter: 0, MovementFlags.None));
        await rooted.SendAsync(WorldOpcode.CmsgForceMoveRootAck, RootAck(counter: 0, MovementFlags.Root));

        byte[] relayed = await watcher.ReadUntilAsync(WorldOpcode.MsgMoveRoot);
        Assert.Equal(PackedGuid1, relayed[..2]);
        var reader = new PacketReader(relayed.AsSpan(2));
        MovementInfo movement = MovementInfo.Read(ref reader);
        Assert.Equal(MovementFlags.Root, movement.Flags);
        Assert.Equal(0, reader.Remaining);
        Assert.DoesNotContain(await watcher.CollectAsync(), p => p.Opcode == WorldOpcode.MsgMoveUnroot);
    }

    // --- small player requests -------------------------------------------------------

    [Fact]
    public async Task StandState_Selection_TimeSkips_AndZoneChanges()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient actor = await host.EnterWorldAsync("ACTOR", "Actor");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await Drain(actor, watcher);

        await actor.SendAsync(WorldOpcode.CmsgStandstatechange, U32((uint)StandState.Dead)); // not client-selectable: ignored
        await actor.SendAsync(WorldOpcode.CmsgStandstatechange, U32((uint)StandState.Sit));
        Assert.Equal(new byte[] { (byte)StandState.Sit }, await actor.ReadUntilAsync(WorldOpcode.SmsgStandstateUpdate));

        await actor.SendAsync(WorldOpcode.CmsgSetSelection, U64(2));
        await actor.SendAsync(WorldOpcode.CmsgMoveTimeSkipped, [.. U64(1), .. U32(250)]);
        await actor.SendAsync(WorldOpcode.CmsgMoveTimeSkipped, [.. U64(2), .. U32(999)]); // not its own GUID: ignored
        Assert.Equal((byte[])[.. PackedGuid1, .. U32(250)], await watcher.ReadUntilAsync(WorldOpcode.MsgMoveTimeSkipped));

        await actor.SendAsync(WorldOpcode.CmsgZoneupdate, U32(12)); // unchanged: nothing sent
        await actor.SendAsync(WorldOpcode.CmsgZoneupdate, U32(1519));
        Assert.Equal((byte[])[.. U32(0), .. U32(1519), 0, 0], await actor.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates));

        Assert.Equal((StandState.Sit, 2ul, 2ul, 1519u),
            await host.PlayerStateAsync("Actor", p => (p.StandState, p.Selection.Value, p.Target.Value, p.ZoneId)));
        Assert.DoesNotContain(await watcher.CollectAsync(), p => p.Opcode == WorldOpcode.MsgMoveTimeSkipped);
    }

    // --- commands ----------------------------------------------------------------------

    [Fact]
    public async Task PlayerCommands_ShowOnlyWhatThePlayerMayUse_AndAreNeverSaid()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player");
        await using WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await Drain(player, listener);

        // The vertical list in retail table order (Chat.cpp:2081-2116), "..." marking a group; ".help" shows
        // the help of ".help" first and then the same list. Commands above the caller are not listed.
        string[] list = ["Commands available to you:", "    server ...", "    commands", "    help", "    save"];
        await player.SendChatAsync(ChatType.Say, Language.Common, "!commands");
        foreach (string line in list)
        {
            Assert.Equal(line, (await player.ReadChatAsync()).Text);
        }

        // Known but above the caller: "not available" (resolve first, then authorise); unknown: "no such command".
        Assert.Equal("This command is not available to you.", await CommandAsync(player, ".gm on"));
        Assert.Equal("This command is not available to you.", await CommandAsync(player, ".kick Listener"));
        Assert.Equal("There is no such command", await CommandAsync(player, ".nonsense"));
        Assert.Equal("There is no such subcommand", await CommandAsync(player, ".server"));
        await player.CollectAsync();   // the sub-command list that follows
        Assert.Equal("Syntax: .server motd", await CommandAsync(player, ".help server motd"));
        await player.CollectAsync();
        Assert.DoesNotContain(await listener.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        // Not commands (vmangos ParseCommands): a lone prefix and repeated prefixes are chat.
        await player.SendChatAsync(ChatType.Say, Language.Common, "...");
        Assert.Equal("...", (await listener.ReadChatAsync()).Text);
        await player.SendChatAsync(ChatType.Say, Language.Common, "!");
        Assert.Equal("!", (await listener.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task PlayerCommands_CanBeDisabled()
    {
        await using var host = WorldTestHost.Start(configure: o => o.PlayerCommands = false);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player");
        await using WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await Drain(player, listener);

        await player.SendChatAsync(ChatType.Say, Language.Common, ".help");
        Assert.Equal(".help", (await listener.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task ServerInfo_Motd_AndAbbreviations()
    {
        await using var host = WorldTestHost.Start(configure: o => o.Motd = "Be excellent@to each other");
        await using WorldTestClient client = await host.EnterWorldAsync("ASKER", "Asker");
        await client.CollectAsync();

        await client.SendChatAsync(ChatType.Say, Language.Common, ".serv i"); // abbreviations, as vmangos hasStringAbbr
        Assert.StartsWith("Core revision: ArcaneCore ", (await client.ReadChatAsync()).Text);   // ServerCommands.cpp:310
        Assert.Equal("Players online: 1 (0 queued). Max online: 1 (0 queued).", (await client.ReadChatAsync()).Text);
        Assert.StartsWith("Server uptime: ", (await client.ReadChatAsync()).Text);
        foreach (string prefix in new[] { "Tick target:", "Tick frames:", "Tick work:", "Commands: pending=", "Managed bots:",
            "Tick schedule: late=", "Tick phases mean/max: commands=", "Slowest features: " })
            Assert.StartsWith("Server diagnostics: " + prefix, (await client.ReadChatAsync()).Text);

        // vmangos prints the message as one text (LANG_MOTD_CURRENT); '@' only splits the login greeting.
        await client.SendChatAsync(ChatType.Say, Language.Common, ".server motd");
        Assert.Equal("Current Message of the day: \r", (await client.ReadChatAsync()).Text);
        Assert.Equal("Be excellent@to each other", (await client.ReadChatAsync()).Text);

        // ".s" is ambiguous; the first entry in the retail table that starts with it wins (Chat.cpp:1185-1366:
        // "server" precedes "save"), and ".server" alone is a group without a handler.
        await client.SendChatAsync(ChatType.Say, Language.Common, ".s");
        Assert.Equal("There is no such subcommand", (await client.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task Gm_TogglesGmModeAndFaction()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        // No argument: the state as a notification only (MiscCommands.cpp:106-109).
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm");
        Assert.Equal("GM mode is OFF", new PacketReader(await gm.ReadUntilAsync(WorldOpcode.SmsgNotification)).ReadCString());
        Assert.DoesNotContain(await gm.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        // "on"/"off" answer with the chat line AND the notification (Player::SetGameMaster, Player.cpp:2642-2646).
        Assert.Equal("GM mode is ON", await CommandAsync(gm, ".gm on"));
        Assert.Equal("GM mode is ON", new PacketReader(await gm.ReadUntilAsync(WorldOpcode.SmsgNotification)).ReadCString());
        Assert.Equal((true, Player.GameMasterFactionTemplate), await host.PlayerStateAsync("Staff", p => (p.IsGameMaster, p.FactionTemplate)));

        Assert.Equal("GM mode is OFF", await CommandAsync(gm, ".gm off"));
        Assert.Equal((false, 1u), await host.PlayerStateAsync("Staff", p => (p.IsGameMaster, p.FactionTemplate))); // the race's template again

        // ExtractOnOff takes only on/off (Chat.cpp:2964); anything else answers LANG_USE_BOL, no syntax line.
        Assert.Equal("Incorrect value, use on or off", await CommandAsync(gm, ".gm maybe"));
        Assert.Equal("Incorrect value, use on or off", await CommandAsync(gm, ".gm 1"));
        Assert.Equal("Incorrect value, use on or off", await CommandAsync(gm, ".gm On"));
    }

    [Fact]
    public async Task ModifyMoney_TargetsTheSelection_AndRespectsSecurity()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Administrator);   // BASIC_ADMIN (4) needs the top account
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player");
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await Drain(gm, player, admin);

        const string staff = "|cffffffff|Hplayer:Staff|h[Staff]|h|r";
        const string target = "|cffffffff|Hplayer:Player|h[Player]|h|r";
        Assert.Equal($"You give 500 copper to {staff}.", await CommandAsync(gm, ".modify money 500"));
        Assert.Equal($"You take all copper of {staff}.", await CommandAsync(gm, ".mod mon -1000")); // CharacterCommands.cpp:4482: taking more than held takes all
        Assert.StartsWith("Syntax: .modify money", await CommandAsync(gm, ".modify money lots"));
        await gm.CollectAsync();   // the help text's second line

        await gm.SendAsync(WorldOpcode.CmsgSetSelection, U64(2));
        Assert.Equal($"You give 7 copper to {target}.", await CommandAsync(gm, ".modify money 7"));
        Assert.Equal($"{staff} gave you 7 copper.", (await player.ReadChatAsync()).Text);
        Assert.Equal(7u, await host.PlayerStateAsync("Player", p => p.Money));

        // The same account level is allowed (GM.LowerSecurity applies to higher accounts only); the
        // refusal of a higher account is covered by Kick_IsForGameMasters_AndDisconnects.
        await gm.SendAsync(WorldOpcode.CmsgSetSelection, U64(3));
        Assert.StartsWith("You give 7 copper to", await CommandAsync(gm, ".modify money 7"));
        Assert.Equal(7u, await host.PlayerStateAsync("Admin", p => p.Money));
    }

    [Fact]
    public async Task Kick_IsForGameMasters_AndDisconnects()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient moderator = await host.EnterWorldAsync("MOD", "Moderator", AccountSecurity.Moderator);
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gamemaster", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("VICTIM", "Victim");
        await Drain(moderator, gm, admin, victim);

        Assert.Equal("This command is not available to you.", await CommandAsync(moderator, ".kick Victim"));
        Assert.Equal("You can't kick self, logout instead", await CommandAsync(gm, ".kick gamemaster"));
        Assert.Equal("You can't kick self, logout instead", await CommandAsync(gm, ".kick"));   // nothing selected: the caller itself
        Assert.Equal("You have low security level for this.", await CommandAsync(gm, ".kick Admin"));
        Assert.Equal("Player not found!", await CommandAsync(gm, ".kick Nobody"));
        Assert.Equal("Player |cffffffff|Hplayer:Victim|h[Victim]|h|r kicked.", await CommandAsync(gm, ".kick victim"));
        Assert.True(await victim.IsClosedByServerAsync());
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Victim") is null, "the victim to leave");
    }

    [Fact]
    public async Task AnnounceAndNotify_ReachEveryone()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Administrator);   // BASIC_ADMIN (4)
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player");
        await host.PlaceAsync("Player", 5000, 5000, 0); // anywhere in the world
        await Drain(gm, player);

        // LANG_SYSTEMMESSAGE (ServerCommands.cpp:46-53) and "Global notify: " (:55-69).
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".announce Restart in 5 minutes");
        ChatMessage announcement = await player.ReadChatAsync();
        Assert.Equal((ChatType.System, "|cffff0000[System Message]: Restart in 5 minutes|r"), (announcement.Type, announcement.Text));
        Assert.Equal(announcement, await gm.ReadChatAsync());

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".notify Hello all");
        Assert.Equal("Global notify: Hello all", new PacketReader(await player.ReadUntilAsync(WorldOpcode.SmsgNotification)).ReadCString());
        Assert.StartsWith("Syntax: .notify", await CommandAsync(gm, ".notify"));
    }

    [Fact]
    public async Task Save_IsThrottledForPlayers_SaveAllIsNot()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player");
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Administrator);   // .saveall is ADMINISTRATOR (6)
        await Drain(player, gm);

        // vmangos answers "Player saved." every time (CharacterCommands.cpp:1256); only the first save in 20 s is real.
        int saves = host.Characters.SaveCount;
        await player.SendChatAsync(ChatType.Say, Language.Common, ".save");
        await player.SendChatAsync(ChatType.Say, Language.Common, ".save"); // within 20 s: no second save
        Assert.Equal("Player saved.", (await player.ReadChatAsync()).Text);
        Assert.Equal("Player saved.", (await player.ReadChatAsync()).Text);
        await WorldTestHost.WaitForAsync(() => host.Characters.SaveCount == saves + 1, "one save");
        Assert.Empty(await player.CollectAsync());

        Assert.Equal("Player saved.", await CommandAsync(gm, ".save"));
        Assert.Equal("All players saved.", await CommandAsync(gm, ".saveall"));
        await WorldTestHost.WaitForAsync(() => host.Characters.SaveCount == saves + 4, "staff save and save-all");
    }

    [Fact]
    public async Task Gps_ReportsTheSelectionOrSelf()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Moderator);
        await gm.CollectAsync();

        Assert.Equal("Staff: map 0, zone 12, X -8949.950, Y -132.493, Z 83.531, orientation 0.000", await CommandAsync(gm, ".gps"));
    }

    // --- helpers -------------------------------------------------------------------------

    private static void AssertPacket(WorldOpcode opcode, byte[] payload, (WorldOpcode Opcode, byte[] Payload) actual)
    {
        Assert.Equal(opcode, actual.Opcode);
        Assert.Equal(payload, actual.Payload);
    }

    private static async Task Drain(params WorldTestClient[] clients)
    {
        foreach (WorldTestClient client in clients)
        {
            await client.CollectAsync();
        }
    }

    /// <summary>Send a chat line and return the first system reply.</summary>
    private static async Task<string> CommandAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    private static byte[] RootAck(uint counter, MovementFlags flags)
    {
        var info = new MovementInfo { Flags = flags, Time = 500, X = -8949.95f, Y = -132.493f, Z = 83.5312f };
        var writer = new PacketWriter(48);
        writer.WriteUInt64(1);
        writer.WriteUInt32(counter);
        info.Write(writer);
        return writer.ToArray();
    }

    private static byte[] U32(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] U64(ulong value)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        return b;
    }
}
