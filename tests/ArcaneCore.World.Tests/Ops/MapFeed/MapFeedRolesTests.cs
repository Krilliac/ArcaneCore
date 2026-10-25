using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Ops.Control;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// Connection roles (design 0.10, review item B2): every feed connection is <c>local</c> or <c>web</c>, fixed once, by the first extension op
/// (<c>role</c>) on the shared pipe or by the pipe itself on the separate web pipe. The tier is the connection's, never the request's: a web
/// connection cannot claim <c>tier:local</c> and cannot reach the packet tools even with those on. Spy commands count what ran.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MapFeedRolesTests
{
    private const string WebTicket = "T-ROLE";

    private static FakeFeedTickets Tickets() => new FakeFeedTickets().Set(WebTicket, 77, "WEBGM", AccountSecurity.GameMaster);

    private static FeedWorldSetup World(bool tools = false, Action<WebFeedPipeOptions>? webPipe = null, bool strictWebPipeAccount = false) => new()
    {
        StrictWebPipeAccount = strictWebPipeAccount,
        Terrain = true,
        Tickets = Tickets(),
        Actions = o =>
        {
            o.Enabled = true;
            o.WebLogin = true;
            o.LocalSecurity = AccountSecurity.Administrator;
        },
        PacketTools = tools ? o => o.Enabled = true : null,
        WebPipe = webPipe,
    };

    private static async Task<JsonElement> RoleAsync(FeedClient client, long id)
    {
        await client.SendAsync($$"""{"op":"role","id":{{id}},"role":"web"}""");
        return await ActionWire.ReadResultAsync(client, id);
    }

    [WindowsFact]
    public async Task OnAWebConnection_ATierLocalActor_IsRoleMismatch_AndNothingRuns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(World());
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync("web");

        (await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zplayer")).AssertRefused("role-mismatch");
        (await ActionWire.ActAsync(client, 2, ActionWire.Local(ignoredName: "Administrator"), command: ".zadmin")).AssertRefused("role-mismatch");

        Assert.Equal(0, spies.Total);
        Assert.Contains("-> role-mismatch", world.ActionLog[0].Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task OnALocalConnection_AWebTicket_IsRoleMismatch_AndNothingRuns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(World());
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync("local");

        (await ActionWire.ActAsync(client, 1, ActionWire.Web(WebTicket), command: ".zplayer")).AssertRefused("role-mismatch");

        Assert.Equal(0, spies.Total);
    }

    [Theory]
    [InlineData("""{"op":"console","id":10,"line":"zplayer"}""", "console")]
    [InlineData("""{"op":"inject","id":10,"target":"x","opcode":"CMSG_QUERY_TIME"}""", "inject")]
    [InlineData("""{"op":"send","id":10,"target":"x","opcode":"SMSG_NOTIFICATION"}""", "send")]
    [InlineData("""{"op":"tap","id":10,"target":"x"}""", "tap")]
    [InlineData("""{"op":"untap","id":10}""", "untap")]
    [InlineData("""{"op":"query","id":10,"kind":"los","map":0,"instance":0,"a":[0,0,0],"b":[1,1,1]}""", "query")]
    public async Task OnAWebConnection_ThePacketToolsAndQueries_AreRefused_EvenWithTheToolsOn(string line, string kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(World(tools: true));
        Assert.NotNull(world.Feed.Tools);
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient web = await world.ConnectAsync("web");

        JsonElement refused = await ActionWire.RequestAsync(web, line, 10);

        refused.AssertRefused("role-mismatch");
        Assert.Equal(kind, refused.GetProperty("kind").GetString());
        Assert.Equal(0, spies.Total);

        // The same line on a local connection is served: the refusal is the role's, not the tools being off.
        if (kind == "console")
        {
            FeedClient local = await world.ConnectAsync("local");
            JsonElement served = await ActionWire.RequestAsync(local, line, 10);
            Assert.True(served.Ok(), served.ToString());
            Assert.Equal(1, spies.Count("zplayer"));
        }
    }

    [WindowsFact]
    public async Task ASecondRoleOp_IsRoleLocked_AndALateRoleOpOnALocalConnectionToo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(World());
        ActionSpies.Install(world.Host);

        FeedClient web = await world.ConnectAsync("web"); // role op id 900 acknowledged
        (await RoleAsync(web, 11)).AssertRefused("role-locked");

        // A connection whose first extension op is anything but "role" is local, for good: it cannot become web afterwards.
        FeedClient local = await world.ConnectAsync();
        Assert.True((await ActionWire.ActAsync(local, 1, ActionWire.Local(), command: ".zplayer")).Ok());
        (await RoleAsync(local, 12)).AssertRefused("role-locked");
        Assert.True((await ActionWire.ActAsync(local, 2, ActionWire.Local(), command: ".zplayer")).Ok(), "the failed rebinding left the connection local");
    }

    [WindowsFact]
    public async Task ARoleOtherThanWeb_IsBadArgs_AndLeavesTheRoleUnbound()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(World());
        FeedClient client = await world.ConnectAsync();

        JsonElement bad = await ActionWire.RequestAsync(client, """{"op":"role","id":1,"role":"local"}""", 1);
        bad.AssertRefused(MapFeedProtocol.BadArgs);
        JsonElement admin = await ActionWire.RequestAsync(client, """{"op":"role","id":2,"role":"admin"}""", 2);
        admin.AssertRefused(MapFeedProtocol.BadArgs);

        // Still unbound: the first valid role op binds.
        JsonElement ok = await RoleAsync(client, 3);
        Assert.True(ok.Ok(), ok.ToString());
        Assert.Equal("web", ok.GetProperty("role").GetString());
    }

    [WindowsFact]
    public async Task OnAWebConnection_AnActionWithoutATicket_IsNoTicket_AndCapsReportsTheRole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(World());
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient web = await world.ConnectAsync("web");

        (await ActionWire.RequestAsync(web, ActionWire.Action(1, new System.Text.Json.Nodes.JsonObject { ["tier"] = "web" }, command: ".zplayer"), 1)).AssertRefused("no-ticket");
        JsonElement caps = await ActionWire.RequestAsync(web, """{"op":"caps","id":2}""", 2);

        Assert.Equal("web", caps.GetProperty("role").GetString());
        Assert.False(caps.TryGetProperty("operator", out JsonElement op) && op.ValueKind == JsonValueKind.String, "a web connection is told nothing about the daemon account");
        Assert.Equal(0, spies.Total);
    }

    [WindowsFact]
    public async Task AConnectionThatBindsWeb_DropsFromThePacketToolsLineLimit_ToTheWebLimit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(World(tools: true));
        string pad = new('x', MapFeedProtocol.MaxLineBytes + 100);
        string line = $$"""{"op":"caps","id":5,"pad":"{{pad}}"}""";

        // With the packet tools on, a local connection may send a line this long.
        FeedClient local = await world.ConnectAsync();
        Assert.True((await ActionWire.RequestAsync(local, line, 5)).Ok());

        // A connection that bound the web role on the shared pipe is held to the web limit from the next line on.
        FeedClient web = await world.ConnectAsync("web");
        await web.SendAsync(line);
        JsonElement reply = await web.ReadAsync();

        Assert.Equal("error", reply.Op());
        Assert.Equal(MapFeedProtocol.LineTooLong, reply.Code());
    }

    // ---- the separate web pipe ----------------------------------------------------------------------------------------------

    // A created account's SID (RID 1001) of a made-up domain: not this process's, and acceptable as the web pipe's one client, for the access list read back below.
    private static string LocalServiceSid => "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [WindowsFact]
    public void ClientAccount_RefusesGroupsSystemAndTheDaemon_ButNotACreatedAccount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SecurityIdentifier daemon = ControlPipeSecurity.CurrentUser();
        foreach (string text in new[] { "S-1-1-0", "S-1-5-11", "S-1-5-18", "S-1-5-19", "S-1-5-20", "S-1-5-32-545", "S-1-5-32-544", "S-1-5-21-1-2-3-513", "S-1-5-21-1-2-3-500" })
        {
            Assert.NotNull(WebFeedPipeOptions.RefuseClientSid(new SecurityIdentifier(text), daemon));
        }

        Assert.NotNull(WebFeedPipeOptions.RefuseClientSid(daemon, daemon));
        Assert.Null(WebFeedPipeOptions.RefuseClientSid(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), daemon));
    }

    [WindowsFact]
    public async Task TheWebPipe_RefusesToStart_WhenTheClientAccountIsTheDaemonItself()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string self = WindowsIdentity.GetCurrent().Name;
        await using FeedWorld world = await FeedWorld.StartAsync(World(strictWebPipeAccount: true, webPipe: o =>
        {
            o.Enabled = true;
            o.ClientAccount = self;
        }));

        WebFeedPipe pipe = Assert.IsType<WebFeedPipe>(world.WebPipe);
        Assert.False(pipe.IsListening);
        Assert.Contains(world.Logs.Entries, e => e.Level >= LogLevel.Warning && e.Message.Contains("ClientAccount", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task AConnectionOnTheWebPipe_IsWeb_WithoutARoleOp_AndTheRoleOpIsANoOp()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string self = WindowsIdentity.GetCurrent().User!.Value;
        await using FeedWorld world = await FeedWorld.StartAsync(World(tools: true, webPipe: o =>
        {
            o.Enabled = true;
            o.ClientAccount = self;
        }));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectWebPipeAsync();

        JsonElement caps = await ActionWire.RequestAsync(client, """{"op":"caps","id":1}""", 1);
        Assert.Equal("web", caps.GetProperty("role").GetString());

        (await ActionWire.ActAsync(client, 2, ActionWire.Local(), command: ".zplayer")).AssertRefused("role-mismatch");
        (await ActionWire.RequestAsync(client, """{"op":"console","id":3,"line":"zplayer"}""", 3)).AssertRefused("role-mismatch");
        Assert.True((await ActionWire.ActAsync(client, 4, ActionWire.Web(WebTicket), command: ".zspy")).Ok());

        // "The role op there is a no-op": acknowledged as web, never role-locked.
        JsonElement role = await RoleAsync(client, 5);
        Assert.True(role.Ok(), role.ToString());
        Assert.Equal("web", role.GetProperty("role").GetString());
        Assert.Equal(1, spies.Count("zspy"));
        Assert.Equal(1, spies.Total);
    }

    [WindowsFact]
    public async Task TheWebPipesAccessList_AdmitsOnlyTheConfiguredAccount_AndRejectsRemoteClients()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(World(webPipe: o =>
        {
            o.Enabled = true;
            o.ClientAccount = LocalServiceSid;
            o.MaxConnections = 3;
        }));
        WebFeedPipe pipe = Assert.IsType<WebFeedPipe>(world.WebPipe);
        Assert.True(pipe.IsListening);
        Assert.Equal(3, pipe.Servers.Count);
        SecurityIdentifier configured = new(LocalServiceSid);
        SecurityIdentifier daemon = WindowsIdentity.GetCurrent().User!;

        foreach (NamedPipeServerStream server in pipe.Servers)
        {
            PipeSecurity security = server.GetAccessControl();
            Assert.True(security.AreAccessRulesProtected, "the descriptor must not inherit entries from its container");
            PipeAccessRule[] rules = [.. security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()];
            PipeAccessRule[] allowed = [.. rules.Where(r => r.AccessControlType == AccessControlType.Allow)];

            // Only the configured account: not the daemon account, not SYSTEM, not Everyone, Administrators, Users or the network.
            Assert.Equal(configured, Assert.Single(allowed).IdentityReference);
            Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(daemon) && r.AccessControlType == AccessControlType.Allow);
            Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Equals(ControlPipeSecurity.Network));
            string sddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            foreach (string other in new[] { "WD", "BA", "AU", "AN", "BU", "IU", "SY" })
            {
                Assert.DoesNotContain(";;;" + other + ")", sddl, StringComparison.Ordinal);
            }

            Assert.True(LocalPipeServer.RejectsRemoteClients(server.SafePipeHandle), "every web pipe instance must carry PIPE_REJECT_REMOTE_CLIENTS");
        }
    }

    [WindowsFact]
    public async Task TheWebPipe_RefusesToStartWithoutAClientAccount_AndSaysWhy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(World(webPipe: o => o.Enabled = true));

        WebFeedPipe pipe = Assert.IsType<WebFeedPipe>(world.WebPipe);
        Assert.False(pipe.IsListening);
        Assert.Contains(world.Logs.Entries, e => e.Level >= LogLevel.Warning && e.Message.Contains("ClientAccount", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task TheSharedPipesAccessList_IsUnchanged_WhenTheWebPipeIsOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(World(webPipe: o =>
        {
            o.Enabled = true;
            o.ClientAccount = LocalServiceSid;
        }));
        SecurityIdentifier daemon = ControlPipeSecurity.CurrentUser();

        foreach (NamedPipeServerStream server in world.Feed.Servers)
        {
            PipeAccessRule[] rules = [.. server.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()];
            Assert.Equal(3, rules.Length);
            Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(LocalServiceSid)));
            Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(daemon));
        }
    }
}
