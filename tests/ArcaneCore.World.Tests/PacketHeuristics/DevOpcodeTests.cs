using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Diagnostics;
using ArcaneCore.World.Ops.MapFeed;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.PacketHeuristics;

/// <summary>
/// The stripped developer/GM/cheat opcodes (the 1.12.1 release client has no UI or Lua path that sends them): a client that sends one is
/// dropped without a reply and flagged as <c>packet/dev-opcode</c>, a staff session is audited instead, a flood is rate limited, and the
/// opcodes stay out of the unhandled-opcode counters so those show only real gaps. Real sessions over loopback sockets, every packet through
/// <c>WorldSession.DispatchAsync</c>. The opcode lists are the lane's inventory (class a: INVALID_PACKET Unhandled in vmangos Opcodes.cpp and a
/// name that is an explicit dev/GM/cheat/debug command; class b: real gameplay opcodes that stay unhandled), each with its wire value.
/// </summary>
public sealed class DevOpcodeTests
{
    private static string Key => "packet/" + PacketChecks.Code(PacketCheck.DevOpcode);

    /// <summary>Class (a): the stripped dev/GM/cheat/debug client opcodes with their wire value (build 5875 opcode table; vmangos drops each as INVALID_PACKET Unhandled).</summary>
    private static readonly (ushort Value, WorldOpcode Opcode)[] StrippedInventory =
    [
        (0x0001, WorldOpcode.CmsgBootme),
        (0x0002, WorldOpcode.CmsgDblookup),
        (0x0004, WorldOpcode.CmsgQueryObjectPosition),
        (0x0006, WorldOpcode.CmsgQueryObjectRotation),
        (0x000A, WorldOpcode.CmsgZoneMap),
        (0x000C, WorldOpcode.CmsgDebugChangecellzone),
        (0x000F, WorldOpcode.CmsgRecharge),
        (0x0010, WorldOpcode.CmsgLearnSpell),
        (0x0011, WorldOpcode.CmsgCreatemonster),
        (0x0012, WorldOpcode.CmsgDestroymonster),
        (0x0013, WorldOpcode.CmsgCreateitem),
        (0x0014, WorldOpcode.CmsgCreategameobject),
        (0x0016, WorldOpcode.CmsgMakemonsterattackguid),
        (0x0018, WorldOpcode.CmsgForceaction),
        (0x0019, WorldOpcode.CmsgForceactiononother),
        (0x001A, WorldOpcode.CmsgForceactionshow),
        (0x001C, WorldOpcode.CmsgPetgodmode),
        (0x001F, WorldOpcode.CmsgWeatherSpeedCheat),
        (0x0020, WorldOpcode.CmsgUndressplayer),
        (0x0021, WorldOpcode.CmsgBeastmaster),
        (0x0022, WorldOpcode.CmsgGodmode),
        (0x0024, WorldOpcode.CmsgCheatSetmoney),
        (0x0025, WorldOpcode.CmsgLevelCheat),
        (0x0026, WorldOpcode.CmsgPetLevelCheat),
        (0x0027, WorldOpcode.CmsgSetWorldstate),
        (0x0028, WorldOpcode.CmsgCooldownCheat),
        (0x0029, WorldOpcode.CmsgUseSkillCheat),
        (0x002A, WorldOpcode.CmsgFlagQuest),
        (0x002B, WorldOpcode.CmsgFlagQuestFinish),
        (0x002C, WorldOpcode.CmsgClearQuest),
        (0x002D, WorldOpcode.CmsgSendEvent),
        (0x002E, WorldOpcode.CmsgDebugAistate),
        (0x0030, WorldOpcode.CmsgDisablePvpCheat),
        (0x0031, WorldOpcode.CmsgAdvanceSpawnTime),
        (0x0044, WorldOpcode.CmsgGametimeSet),
        (0x0046, WorldOpcode.CmsgGamespeedSet),
        (0x0048, WorldOpcode.CmsgServertime),
        (0x00C4, WorldOpcode.MsgMoveToggleLogging),
        (0x00C8, WorldOpcode.MsgMoveToggleFallLogging),
        (0x00CC, WorldOpcode.MsgMoveSetRunSpeedCheat),
        (0x00CE, WorldOpcode.MsgMoveSetRunBackSpeedCheat),
        (0x00D0, WorldOpcode.MsgMoveSetWalkSpeedCheat),
        (0x00D2, WorldOpcode.MsgMoveSetSwimSpeedCheat),
        (0x00D4, WorldOpcode.MsgMoveSetSwimBackSpeedCheat),
        (0x00D6, WorldOpcode.MsgMoveSetAllSpeedCheat),
        (0x00D7, WorldOpcode.MsgMoveSetTurnRateCheat),
        (0x00D9, WorldOpcode.MsgMoveToggleCollisionCheat),
        (0x00F8, WorldOpcode.CmsgTriggerCinematicCheat),
        (0x0126, WorldOpcode.CmsgSetFactionCheat),
        (0x0153, WorldOpcode.CmsgSavePlayer),
        (0x0154, WorldOpcode.CmsgSetdeathbindpoint),
        (0x01A6, WorldOpcode.CmsgTaxiclearallnodes),
        (0x01A7, WorldOpcode.CmsgTaxienableallnodes),
        (0x01A8, WorldOpcode.CmsgTaxishownodes),
        (0x01D8, WorldOpcode.CmsgSetSkillCheat),
        (0x01E5, WorldOpcode.CmsgGhost),
        (0x01E6, WorldOpcode.CmsgGmInvis),
        (0x01E8, WorldOpcode.MsgGmBindOther),
        (0x01E9, WorldOpcode.MsgGmSummon),
        (0x01EF, WorldOpcode.MsgGmShowlabel),
        (0x01F9, WorldOpcode.CmsgGmSetSecurityGroup),
        (0x01FA, WorldOpcode.CmsgGmNuke),
        (0x01FD, WorldOpcode.CmsgRwhois),
        (0x0201, WorldOpcode.CmsgUnlearnSpell),
        (0x0204, WorldOpcode.CmsgDecharge),
        (0x020F, WorldOpcode.CmsgGmTeach),
        (0x0210, WorldOpcode.CmsgGmCreateItemTarget),
        (0x0213, WorldOpcode.CmsgUnlearnTalents),
        (0x021D, WorldOpcode.CmsgSetStatCheat),
        (0x0221, WorldOpcode.CmsgXpCheat),
        (0x0223, WorldOpcode.CmsgCharacterPointCheat),
        (0x0226, WorldOpcode.CmsgGmVision),
        (0x0227, WorldOpcode.CmsgServerCommand),
        (0x0228, WorldOpcode.CmsgGmSilence),
        (0x0229, WorldOpcode.CmsgGmRevealto),
        (0x022A, WorldOpcode.CmsgGmResurrect),
        (0x022B, WorldOpcode.CmsgGmSummonmob),
        (0x022C, WorldOpcode.CmsgGmMovecorpse),
        (0x022D, WorldOpcode.CmsgGmFreeze),
        (0x022E, WorldOpcode.CmsgGmUberinvis),
        (0x022F, WorldOpcode.CmsgGmRequestPlayerInfo),
        (0x0237, WorldOpcode.CmsgClearExploration),
        (0x0241, WorldOpcode.CmsgTaxiclearnode),
        (0x0242, WorldOpcode.CmsgTaxienablenode),
        (0x026C, WorldOpcode.CmsgPlayerAiCheat),
        (0x026E, WorldOpcode.MsgGmAccountOnline),
        (0x027D, WorldOpcode.CmsgEnableDamageLog),
        (0x0281, WorldOpcode.CmsgResetFactionCheat),
        (0x0287, WorldOpcode.CmsgSetDurabilityCheat),
        (0x0288, WorldOpcode.CmsgSetPvpRankCheat),
        (0x0289, WorldOpcode.CmsgAddPvpMedalCheat),
        (0x028A, WorldOpcode.CmsgDelPvpMedalCheat),
        (0x028B, WorldOpcode.CmsgSetPvpTitle),
        (0x0294, WorldOpcode.CmsgMeetingstoneCheat),
        (0x029A, WorldOpcode.CmsgGmticketsystemToggle),
        (0x02AD, WorldOpcode.MsgMoveToggleGravityCheat),
        (0x02B2, WorldOpcode.CmsgServerBroadcast),
        (0x02B5, WorldOpcode.CmsgRunScript),
        (0x02BE, WorldOpcode.CmsgSetExploration),
        (0x02D8, WorldOpcode.CmsgMoveStartSwimCheat),
        (0x02D9, WorldOpcode.CmsgMoveStopSwimCheat),
        (0x02E5, WorldOpcode.CmsgGmUnteach),
        (0x030F, WorldOpcode.CmsgGmNukeAccount),
        (0x0310, WorldOpcode.MsgGmDestroyCorpse),
        (0x0311, WorldOpcode.CmsgGmDestroyOnlineCorpse),
        (0x0315, WorldOpcode.CmsgDebugActionsStart),
        (0x0316, WorldOpcode.CmsgDebugActionsStop),
        (0x031B, WorldOpcode.CmsgSetExplorationAll),
        (0x032C, WorldOpcode.CmsgIgnoreKnockbackCheat),
    ];

    /// <summary>Class (b): real gameplay opcodes with no handler; they must keep going through the unhandled path.</summary>
    private static readonly (ushort Value, WorldOpcode Opcode)[] GameplayGaps =
    [
        (0x0109, WorldOpcode.CmsgStoreLootInSlot),
        (0x021F, WorldOpcode.CmsgSkillBuyStep),
    ];

    /// <summary>Class (c) samples (obsolete / server-to-client / doubtful), kept unhandled on purpose so a doubtful name never raises a finding.</summary>
    private static readonly WorldOpcode[] ObsoleteSamples =
    [
        WorldOpcode.CmsgNewSpellSlot, // 0x012D obsolete (vmangos Opcodes.cpp:382)
        WorldOpcode.MsgMoveTimeSkipped, // 0x0319 only ever broadcast by the server (vmangos MovementHandler.cpp:1014)
    ];

    private static WorldOpcode[] Stripped => [.. StrippedInventory.Select(i => i.Opcode)];

    private static byte[] Junk => [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]; // never parsed: a money cheat of 0xFFFFFFFF must do nothing

    private static async Task<List<(WorldOpcode Opcode, byte[] Payload)>> PingAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgPing, PacketHeuristicsTestKit.PingPayload(1));
        return await client.CollectFromAsync(WorldOpcode.SmsgPong, TimeSpan.FromMilliseconds(250)); // everything the server sent up to the pong and after it
    }

    [Fact]
    public async Task TheInventory_IsExplicit_ItsWireValuesMatchTheEnum_AndItNeverOverlapsAHandledOrGameplayOpcode()
    {
        await using var host = WorldTestHost.Start();
        Assert.Equal(109, StrippedInventory.Length);
        Assert.Equal(StrippedInventory.Length, StrippedInventory.Select(i => i.Opcode).Distinct().Count());
        Assert.All(StrippedInventory, i => Assert.Equal(i.Value, (ushort)i.Opcode));
        Assert.All(GameplayGaps, i => Assert.Equal(i.Value, (ushort)i.Opcode));

        Assert.Equal(Stripped.Order(), StrippedDevOpcodes.All.Order());
        Assert.Empty(StrippedDevOpcodes.All.Intersect(GameplayGaps.Select(g => g.Opcode)));
        Assert.Empty(StrippedDevOpcodes.All.Intersect(ObsoleteSamples));

        // vmangos has real handlers for these (Opcodes.cpp:79/80/181/279/306) and this server too (GmClientHandlers.cs): they are not stripped.
        foreach (WorldOpcode real in new[] { WorldOpcode.CmsgWorldTeleport, WorldOpcode.CmsgTeleportToUnit, WorldOpcode.CmsgWhois, WorldOpcode.MsgMoveTeleportCheat, WorldOpcode.CmsgMoveSetRawPosition })
        {
            Assert.False(StrippedDevOpcodes.All.Contains(real), WorldOpcodeNames.GetName(real));
            Assert.False(Handler(real).Stripped, WorldOpcodeNames.GetName(real));
        }

        foreach (WorldOpcode opcode in Stripped)
        {
            OpcodeHandler handler = Handler(opcode);
            Assert.True(handler.Stripped, WorldOpcodeNames.GetName(opcode));
            Assert.Null(handler.World);
            Assert.Null(handler.Session);
        }

        foreach (WorldOpcode opcode in GameplayGaps.Select(g => g.Opcode).Concat(ObsoleteSamples))
        {
            Assert.False(host.Opcodes.TryGet(opcode, out _), WorldOpcodeNames.GetName(opcode));
        }

        OpcodeHandler Handler(WorldOpcode opcode)
        {
            Assert.True(host.Opcodes.TryGet(opcode, out OpcodeHandler found), WorldOpcodeNames.GetName(opcode));
            return found;
        }
    }

    [Fact]
    public async Task EveryStrippedOpcode_FromAPlayerInWorld_IsDroppedFlaggedAndNeverAnswered_WithoutTouchingTheCharacter()
    {
        var dev = new DevOpcodeTracker();
        var unhandled = new UnhandledOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: c => { c.AddSingleton(dev); c.AddSingleton(unhandled); });
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVALL", "Devallone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();
            Player player = await host.PlayerAsync("Devallone");
            (uint money, uint level, float x, float y, float z) = await host.OnWorldAsync(() => (player.Money, player.Level, player.X, player.Y, player.Z));
            await client.CollectAsync();

            foreach (WorldOpcode opcode in Stripped)
            {
                await client.SendAsync(opcode, Junk);
            }

            List<(WorldOpcode Opcode, byte[] Payload)> received = await PingAsync(client);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal([WorldOpcode.SmsgPong], received.Select(r => r.Opcode)); // nothing but the ping's own answer: no reply to any dev opcode
            Assert.Equal(Stripped.Length, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Player));
            IReadOnlyList<AnomalyNotice> notices = [.. capture.Notices.Where(n => n.Key == Key)];
            Assert.Equal(Stripped.Length, notices.Count);
            Assert.All(notices, n =>
            {
                Assert.Equal(AnomalySeverity.Warning, n.Severity);
                Assert.StartsWith("account ", n.Subject, StringComparison.Ordinal);
            });
            Assert.Equal(0, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Player)); // a dev opcode is not "unknown"
            Assert.Equal(Stripped.Length, dev.Total);
            Assert.All(dev.Snapshot(), c => Assert.Equal(1, c.Count));
            Assert.Equal(Stripped.Select(WorldOpcodeNames.GetName).Order(), dev.Snapshot().Select(c => c.Opcode).Order());
            Assert.Empty(unhandled.Snapshot());
            Assert.Equal(0, unhandled.Total);
            Assert.Equal(SessionState.InWorld, session.State);
            Assert.Equal((money, level, x, y, z), await host.OnWorldAsync(() => (player.Money, player.Level, player.X, player.Y, player.Z)));
        }
    }

    [Fact]
    public async Task EveryStrippedOpcode_AtTheCharacterScreen_IsDroppedAndFlagged_TheSessionStaysOpen()
    {
        var dev = new DevOpcodeTracker();
        var unhandled = new UnhandledOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: c => { c.AddSingleton(dev); c.AddSingleton(unhandled); });
        (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host);
        byte[] key = await host.AddAccountAsync("DEVSCR");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("DEVSCR", key);
        await client.CreateCharacterAsync("Devscrone");
        await client.CollectAsync();
        using AnomalyCapture capture = ledger.Capture();

        foreach (WorldOpcode opcode in Stripped)
        {
            await client.SendAsync(opcode, Junk);
        }

        List<(WorldOpcode Opcode, byte[] Payload)> received = await PingAsync(client);

        Assert.Equal([WorldOpcode.SmsgPong], received.Select(r => r.Opcode));
        Assert.Equal(Stripped.Length, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Player));
        Assert.Equal(Stripped.Length, capture.Notices.Count(n => n.Key == Key));
        Assert.Equal(0, feature.Count(PacketCheck.WrongState, PacketTraffic.Player));
        Assert.Equal(Stripped.Length, dev.Total);
        Assert.Empty(unhandled.Snapshot());
        Account account = (await host.Accounts.FindByUsernameAsync("DEVSCR"))!;
        Assert.Equal(SessionState.CharacterSelect, host.Registry.Find(account.Id)!.State);
    }

    [Fact]
    public async Task WithTheHeuristicsOff_TheShippedDefault_AStrippedOpcodeIsStillCountedLoggedDroppedAndNeverAnswered()
    {
        var dev = new DevOpcodeTracker();
        var unhandled = new UnhandledOpcodeTracker();
        var log = new LevelLogger();
        await using var host = WorldTestHost.Start(configureServices: c => { c.AddSingleton(dev); c.AddSingleton(unhandled); }, sessionLogger: log);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVOFF", "Devoffone");
        await using (client)
        {
            PacketHeuristicsFeature feature = host.WorldServices.GetRequiredService<PacketHeuristicsFeature>();
            Player player = await host.PlayerAsync("Devoffone");
            (uint money, uint level) = await host.OnWorldAsync(() => (player.Money, player.Level));
            await client.CollectAsync();

            await client.SendAsync(WorldOpcode.CmsgCheatSetmoney, Junk);
            await client.SendAsync(WorldOpcode.CmsgCheatSetmoney, Junk);
            await client.SendAsync(WorldOpcode.CmsgLevelCheat, Junk);
            List<(WorldOpcode Opcode, byte[] Payload)> received = await PingAsync(client);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal([WorldOpcode.SmsgPong], received.Select(r => r.Opcode));
            Assert.Equal(3, dev.Total);
            Assert.Equal(0, PacketHeuristicsTestKit.Total(feature, PacketCheck.DevOpcode)); // no ledger finding without the heuristics
            Assert.Empty(unhandled.Snapshot());
            Assert.Equal(1, log.Warnings.Count(m => m.Contains("CMSG_CHEAT_SETMONEY", StringComparison.Ordinal) && m.Contains($"account {session.AccountId}", StringComparison.Ordinal)));
            Assert.Single(log.Warnings, m => m.Contains("stripped developer opcode", StringComparison.Ordinal)); // one line per session per interval: the other two are coalesced, not lost
            Assert.Equal((money, level), await host.OnWorldAsync(() => (player.Money, player.Level)));
            Assert.Equal(SessionState.InWorld, session.State);
        }
    }

    [Fact]
    public async Task WithTheDevOpcodeCheckSwitchedOff_NoFindingIsRaised_ButTheDropTheCountAndTheLogRemain()
    {
        var dev = new DevOpcodeTracker();
        var unhandled = new UnhandledOpcodeTracker();
        var log = new LevelLogger();
        await using var host = WorldTestHost.Start(configureServices: c => { c.AddSingleton(dev); c.AddSingleton(unhandled); }, sessionLogger: log);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVSWOFF", "Devswoffone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host, o => o.DevOpcode = false);
            using AnomalyCapture capture = ledger.Capture();
            await client.CollectAsync();

            await client.SendAsync(WorldOpcode.CmsgGodmode, Junk);
            List<(WorldOpcode Opcode, byte[] Payload)> received = await PingAsync(client);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal([WorldOpcode.SmsgPong], received.Select(r => r.Opcode));
            Assert.Equal(0, PacketHeuristicsTestKit.Total(feature, PacketCheck.DevOpcode));
            Assert.DoesNotContain(capture.Notices, n => n.Key == Key);
            Assert.Equal(1, dev.Total);
            Assert.Empty(unhandled.Snapshot());
            Assert.Single(log.Warnings, m => m.Contains("CMSG_GODMODE", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AStaffSession_IsAuditedForADevOpcode_WithNoFindingNoReplyAndNoEffect()
    {
        var dev = new DevOpcodeTracker();
        var unhandled = new UnhandledOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: c => { c.AddSingleton(dev); c.AddSingleton(unhandled); });
        (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVGM", "Devgmone", AccountSecurity.GameMaster);
        await using (client)
        {
            using AnomalyCapture capture = ledger.Capture();
            GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
            Player player = await host.PlayerAsync("Devgmone");
            (uint money, uint level, float x, float y, float z) = await host.OnWorldAsync(() => (player.Money, player.Level, player.X, player.Y, player.Z));
            await client.CollectAsync();
            WorldOpcode[] sent = [WorldOpcode.CmsgGodmode, WorldOpcode.CmsgCheatSetmoney, WorldOpcode.MsgGmSummon];

            foreach (WorldOpcode opcode in sent)
            {
                await client.SendAsync(opcode, Junk);
            }

            List<(WorldOpcode Opcode, byte[] Payload)> received = await PingAsync(client);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            GmAuditEntry[] entries = [.. audit.Tail(1000).Where(e => e.Command.StartsWith("dev-opcode", StringComparison.Ordinal))];
            Assert.Equal(3, entries.Length);
            foreach (WorldOpcode opcode in sent)
            {
                string name = WorldOpcodeNames.GetName(opcode);
                Assert.Single(entries, e => e.Command == "dev-opcode " + name && e.AccountId == session.AccountId && e.Line.Contains(name, StringComparison.Ordinal));
            }

            Assert.DoesNotContain(capture.Notices, n => n.Key == Key);
            Assert.Equal(0, PacketHeuristicsTestKit.Total(feature, PacketCheck.DevOpcode));
            Assert.Equal(0, PacketHeuristicsTestKit.Total(feature, PacketCheck.UnknownOpcode));
            Assert.Equal([WorldOpcode.SmsgPong], received.Select(r => r.Opcode)); // no reply
            Assert.Empty(unhandled.Snapshot());
            Assert.Equal((money, level, x, y, z), await host.OnWorldAsync(() => (player.Money, player.Level, player.X, player.Y, player.Z)));
        }
    }

    [Fact]
    public async Task APlayerAccountInGmMode_IsTreatedAsStaff_AuditedWithNoFinding()
    {
        var dev = new DevOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: c => c.AddSingleton(dev));
        (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVGMM", "Devgmmode");
        await using (client)
        {
            using AnomalyCapture capture = ledger.Capture();
            GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
            Player player = await host.PlayerAsync("Devgmmode");
            Assert.False(session.IsHeuristicsStaff);

            await host.OnWorldAsync(() => player.Flags |= PlayerFlags.Gm);
            await client.SendAsync(WorldOpcode.CmsgSetSelection, new byte[8]); // a world packet: the session learns the GM mode on dispatch
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);
            Assert.True(session.HeuristicsGmMode);
            Assert.True(session.IsHeuristicsStaff);

            await client.SendAsync(WorldOpcode.CmsgGodmode, Junk);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            string name = WorldOpcodeNames.GetName(WorldOpcode.CmsgGodmode);
            Assert.Single(audit.Tail(1000), e => e.Command == "dev-opcode " + name && e.AccountId == session.AccountId);
            Assert.DoesNotContain(capture.Notices, n => n.Key == Key);
            Assert.Equal(0, PacketHeuristicsTestKit.Total(feature, PacketCheck.DevOpcode));
            Assert.Equal(1, dev.Total);
        }
    }

    [Fact]
    public async Task WithTheHeuristicsOff_APlayerAccountInGmMode_IsAuditedNotWarned()
    {
        var dev = new DevOpcodeTracker();
        var log = new LevelLogger();
        await using var host = WorldTestHost.Start(configureServices: c => c.AddSingleton(dev), sessionLogger: log);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVGMOFF", "Devgmoff");
        await using (client)
        {
            Assert.False(host.WorldServices.GetRequiredService<PacketHeuristicsFeature>().Active);
            GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
            Player player = await host.PlayerAsync("Devgmoff");
            Assert.False(session.IsHeuristicsStaff);

            await host.OnWorldAsync(() => player.Flags |= PlayerFlags.Gm);
            await client.SendAsync(WorldOpcode.CmsgSetSelection, new byte[8]); // a world packet: the session learns the GM mode on dispatch
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);
            Assert.True(session.HeuristicsGmMode);
            Assert.True(session.IsHeuristicsStaff);

            await client.SendAsync(WorldOpcode.CmsgGodmode, Junk);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            string name = WorldOpcodeNames.GetName(WorldOpcode.CmsgGodmode);
            Assert.Single(audit.Tail(1000), e => e.Command == "dev-opcode " + name && e.AccountId == session.AccountId);
            Assert.DoesNotContain(log.Warnings, m => m.Contains("stripped developer opcode", StringComparison.Ordinal));
            Assert.Equal(1, dev.Total);
            Assert.Equal(SessionState.InWorld, session.State);
        }
    }

    [Fact]
    public async Task AGmModeCharacterThatLeavesTheWorld_NoLongerCountsAsStaff()
    {
        await using var host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVGMLO", "Devgmlogout");
        await using (client)
        {
            Player player = await host.PlayerAsync("Devgmlogout");
            await host.OnWorldAsync(() => player.Flags |= PlayerFlags.Gm);
            await client.SendAsync(WorldOpcode.CmsgSetSelection, new byte[8]); // a world packet: the session learns the GM mode on dispatch
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);
            Assert.True(session.HeuristicsGmMode);

            await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
            await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
            await host.WaitForWorldAsync(() => session.State == SessionState.CharacterSelect, "the session is back at the character screen");

            Assert.False(session.HeuristicsGmMode);
            Assert.False(session.IsHeuristicsStaff);
        }
    }

    [Fact]
    public async Task AFloodOfOneDevOpcode_IsCountedInFull_ButReportedAndLoggedOncePerInterval()
    {
        long now = 1_000_000;
        var dev = new DevOpcodeTracker(() => Volatile.Read(ref now));
        var log = new LevelLogger();
        await using var host = WorldTestHost.Start(configureServices: c => c.AddSingleton(dev), sessionLogger: log);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVFLOOD", "Devfloodone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host, now: () => Volatile.Read(ref now));
            using AnomalyCapture capture = ledger.Capture();

            for (int i = 0; i < 200; i++)
            {
                await client.SendAsync(WorldOpcode.CmsgBootme, []);
            }

            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal(200, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Player));
            Assert.Equal(200, dev.Total);
            AnomalyNotice first = Assert.Single(capture.Notices, n => n.Key == Key);
            Assert.StartsWith("CMSG_BOOTME", first.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("more since the last report", first.Detail, StringComparison.Ordinal);
            Assert.Equal(1, log.Warnings.Count(m => m.Contains("CMSG_BOOTME", StringComparison.Ordinal) && m.Contains($"account {session.AccountId}", StringComparison.Ordinal)));

            Interlocked.Add(ref now, 11_000); // past the default ReportIntervalSeconds (10)
            await client.SendAsync(WorldOpcode.CmsgBootme, []);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal(201, dev.Total);
            AnomalyNotice second = Assert.Single(capture.Notices, n => n.Key == Key && n.Detail.Contains("(+199 more since the last report)", StringComparison.Ordinal));
            Assert.StartsWith("CMSG_BOOTME", second.Detail, StringComparison.Ordinal);
            Assert.Equal(2, capture.Notices.Count(n => n.Key == Key));
            Assert.Equal(2, log.Warnings.Count(m => m.Contains("CMSG_BOOTME", StringComparison.Ordinal)));
            Assert.Contains(log.Warnings, m => m.Contains("(199 more since the last line", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AFloodOfStaffDevOpcodes_IsCoalescedInTheAuditTail_PerOpcode()
    {
        long now = 1_000_000;
        var dev = new DevOpcodeTracker(() => Volatile.Read(ref now));
        await using var host = WorldTestHost.Start(configureServices: c => c.AddSingleton(dev));
        _ = PacketHeuristicsTestKit.Enable(host);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVGMF", "Devgmflood", AccountSecurity.GameMaster);
        await using (client)
        {
            GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
            for (int i = 0; i < 50; i++)
            {
                await client.SendAsync(WorldOpcode.CmsgGodmode, Junk);
            }

            await client.SendAsync(WorldOpcode.CmsgBootme, Junk);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal(51, dev.Total);
            Assert.Equal(1, audit.Tail(1000).Count(e => e.Command == "dev-opcode CMSG_GODMODE"));
            Assert.Equal(1, audit.Tail(1000).Count(e => e.Command == "dev-opcode CMSG_BOOTME"));

            Interlocked.Add(ref now, 11_000);
            await client.SendAsync(WorldOpcode.CmsgGodmode, Junk);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            GmAuditEntry[] godmode = [.. audit.Tail(1000).Where(e => e.Command == "dev-opcode CMSG_GODMODE")];
            Assert.Equal(2, godmode.Length);
            Assert.Contains(godmode, e => e.Line.Contains("(+49 more coalesced)", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheUnhandledCounters_ShowOnlyRealGaps_AndTheWiringAuditListsDevDropsSeparately()
    {
        var dev = new DevOpcodeTracker();
        var unhandled = new UnhandledOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: c =>
        {
            c.AddSingleton(dev);
            c.AddSingleton(unhandled);
            c.AddSingleton(sp => new WiringAudit(sp));
        });
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVMIX", "Devmixone");
        await using (client)
        {
            await client.SendAsync(WorldOpcode.CmsgBootme, []);
            await client.SendAsync(WorldOpcode.CmsgNewSpellSlot, []);
            await client.SendAsync(WorldOpcode.CmsgBootme, []);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal([new UnhandledOpcodeCount("CMSG_NEW_SPELL_SLOT", 1)], unhandled.Snapshot());
            Assert.Equal(2, dev.Total);

            WiringReport report = host.WorldServices.GetRequiredService<WiringAudit>().Run();
            Assert.Contains(report.Findings, f => f is { Category: WiringAudit.UnhandledOpcode, Subject: "CMSG_NEW_SPELL_SLOT" });
            Assert.DoesNotContain(report.Findings, f => f.Subject == "CMSG_BOOTME");

            await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
            var lines = new List<string>();
            WorldConsoleExecutor executor = WorldConsoleExecutor.Create(
                scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance, new ListWriter(lines));
            await host.World.InvokeAsync(() =>
            {
                executor.Execute(".server wiring");
                return 0;
            });

            string[] output;
            lock (lines)
            {
                output = [.. lines];
            }

            Assert.Contains(output, l => l.Contains("[unhandled-opcode] CMSG_NEW_SPELL_SLOT", StringComparison.Ordinal));
            Assert.DoesNotContain(output, l => l.Contains("[unhandled-opcode] CMSG_BOOTME", StringComparison.Ordinal));
            Assert.Contains(output, l => l.Contains("dev opcodes dropped: 2", StringComparison.Ordinal) && l.Contains("CMSG_BOOTME", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ARealGameplayGap_IsUnaffected_StillUnknownInfoAndCountedAsUnhandled()
    {
        var dev = new DevOpcodeTracker();
        var unhandled = new UnhandledOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: c => { c.AddSingleton(dev); c.AddSingleton(unhandled); });
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVREAL", "Devrealone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            foreach ((ushort _, WorldOpcode opcode) in GameplayGaps)
            {
                await client.SendAsync(opcode, []);
            }

            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal(0, dev.Total);
            Assert.Equal(0, PacketHeuristicsTestKit.Total(feature, PacketCheck.DevOpcode));
            Assert.Equal(GameplayGaps.Length, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Player));
            Assert.Equal(GameplayGaps.Select(g => WorldOpcodeNames.GetName(g.Opcode)).Order(), unhandled.Snapshot().Select(c => c.Opcode).Order());
            Assert.DoesNotContain(capture.Notices, n => n.Key == Key);
        }
    }

    [Fact]
    public async Task ABot_IsRefusedAndFlaggedThroughTheSamePath_AsBotTraffic()
    {
        var dev = new DevOpcodeTracker();
        var unhandled = new UnhandledOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: c => { c.AddSingleton(dev); c.AddSingleton(unhandled); });
        (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host, o => o.ExemptBots = false);
        using AnomalyCapture capture = ledger.Capture();
        WorldSession bot = await PacketHeuristicsTestKit.EnterBotAsync(host, "DEVBOT", "Devbotone");
        try
        {
            bool sent = await host.World.InvokeAsync(() => bot.TryManagedAction(WorldOpcode.CmsgGodmode, []));

            Assert.False(sent);
            Assert.Equal(1, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Bot));
            Assert.Equal(0, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Bot));
            Assert.Single(capture.Notices, n => n.Key == Key);
            Assert.Equal(1, dev.Total);
            Assert.Empty(unhandled.Snapshot());
        }
        finally
        {
            bot.Kick("test over");
            await bot.ManagedClosed;
        }
    }

    [Fact]
    public async Task AToolInjectedDevOpcode_IntoAManagedSession_IsDroppedAndReportedUnderTheTool()
    {
        var permit = new PacketToolsPermit(
            Options.Create(new WorldPacketToolsOptions { Enabled = true }),
            Options.Create(new WorldMapFeedOptions { Enabled = true }),
            new DevelopmentEnvironment(),
            liveServer: false);
        var dev = new DevOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: s => { s.AddSingleton(permit); s.AddSingleton(dev); });
        (PacketHeuristicsFeature feature, AnomalyLedger _) = PacketHeuristicsTestKit.Enable(host);
        WorldSession bot = await PacketHeuristicsTestKit.EnterBotAsync(host, "DEVBOTI", "Devbotinj");
        try
        {
            InjectVerdict verdict = await bot.InjectAsync(WorldOpcode.CmsgGodmode, [], TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.Equal(InjectVerdict.Handled, verdict);
            Assert.Equal(1, dev.Total);
            Assert.Equal(1, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Tool));
        }
        finally
        {
            bot.Kick("test over");
            await bot.ManagedClosed;
        }
    }

    [Fact]
    public async Task AToolInjectedDevOpcode_ReachesTheDispatcher_AndIsReportedUnderTheTool()
    {
        var permit = new PacketToolsPermit(
            Options.Create(new WorldPacketToolsOptions { Enabled = true }),
            Options.Create(new WorldMapFeedOptions { Enabled = true }),
            new DevelopmentEnvironment(),
            liveServer: false);
        var dev = new DevOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: s => { s.AddSingleton(permit); s.AddSingleton(dev); });
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "DEVTOOL", "Devtoolone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, AnomalyLedger ledger) = PacketHeuristicsTestKit.Enable(host);
            using AnomalyCapture capture = ledger.Capture();

            InjectVerdict verdict = await session.InjectAsync(WorldOpcode.CmsgGodmode, [], TimeSpan.FromSeconds(5), CancellationToken.None);
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal(InjectVerdict.Handled, verdict);
            AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == Key);
            Assert.Equal(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tool {session.AccountId}"), notice.Subject);
            Assert.Equal(1, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Tool));
            Assert.Equal(0, feature.Count(PacketCheck.DevOpcode, PacketTraffic.Player));
            Assert.Equal(1, dev.Total);
        }
    }

    [Fact]
    public void AHotModule_CannotClaimAStrippedOpcode()
    {
        var table = new OpcodeTable();
        table.OnStripped(WorldOpcode.CmsgGodmode);
        var claimant = new OpcodeTable();
        claimant.OnSession(WorldOpcode.CmsgGodmode, SessionStates.Authenticated, (_, _) => Task.CompletedTask);

        Assert.Null(table.TrySwap([], claimant, out string? error));
        Assert.Contains("CMSG_GODMODE", error, StringComparison.Ordinal);
        Assert.True(table.TryGet(WorldOpcode.CmsgGodmode, out OpcodeHandler handler));
        Assert.True(handler.Stripped);
        Assert.Null(handler.Session);
    }

    private sealed class LevelLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public string[] Warnings
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class ListWriter(List<string> lines) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (lines)
            {
                lines.Add(value ?? string.Empty);
            }
        }
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "DevOpcodeTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
