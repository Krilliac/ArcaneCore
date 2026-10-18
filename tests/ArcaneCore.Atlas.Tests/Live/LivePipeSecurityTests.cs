using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>
/// The client against a pipe secured the way the world daemon secures its own (a protected descriptor owned by the daemon's account: deny
/// NETWORK, allow that account full control, allow SYSTEM read and write), and the owner check that decides whom to trust. Windows only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LivePipeSecurityTests
{
    // ref: src/ArcaneCore.World/Ops/Control/ControlPipeSecurity.cs:39-50 (Create, with its [SupportedOSPlatform] attribute: protected descriptor :44, SetOwner at :45, deny NETWORK :46, allow user :47, allow SYSTEM :48)
    private static PipeSecurity DaemonSecurity(SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    private static NamedPipeServerStream SecuredPipe(string name, SecurityIdentifier user)
        => NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, FakeFeedServer.BufferBytes, FakeFeedServer.BufferBytes, DaemonSecurity(user));

    [WindowsFact]
    public async Task APipeSecuredLikeTheDaemonsIsAcceptedAndServed()
    {
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        string name = FakeFeedServer.NewName();
        await using var rig = new LiveRig(o => o with { PipeNameFor = _ => name });
        await using var server = new FakeFeedServer(name, SecuredPipe(name, me.User!));

        Task accept = server.AcceptAsync();
        Task<JsonObject> connect = rig.Feed.ConnectAsync(4242, CancellationToken.None);
        await accept;
        JsonObject hello = await server.AnswerHelloAsync(Wire.Hello(pid: 4242));
        JsonObject status = await connect.WaitAsync(FakeFeedServer.Patience);

        Assert.Equal("hello", hello["op"]!.GetValue<string>());
        Assert.True(status["connected"]!.GetValue<bool>());
        Assert.True(rig.Feed.IsConnected);
    }

    [WindowsFact]
    public async Task TheOwnerThePipeReportsIsTheDaemonsAccount_AndTheClientTrustsIt()
    {
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        string name = FakeFeedServer.NewName();
        await using var server = new FakeFeedServer(name, SecuredPipe(name, me.User!));
        Task accept = server.AcceptAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await client.ConnectAsync(5000);
        await accept;

        var owner = client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        Assert.Equal(me.User, owner);
        Assert.True(FeedSession.IsTrustedOwner(owner, me.User, me.Owner));
    }

    [WindowsFact]
    public void TheOwnerCheckAcceptsTheUserOrTheTokensDefaultOwner_AndNothingElse()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var otherUser = new SecurityIdentifier("S-1-5-21-1-2-3-1002");

        // An unelevated token owns what it creates as itself; an elevated administrator's token owns it as Administrators.
        Assert.True(FeedSession.IsTrustedOwner(user, user, user));
        Assert.True(FeedSession.IsTrustedOwner(user, user, administrators));
        Assert.True(FeedSession.IsTrustedOwner(administrators, user, administrators));

        Assert.False(FeedSession.IsTrustedOwner(otherUser, user, administrators));
        Assert.False(FeedSession.IsTrustedOwner(everyone, user, administrators));
        Assert.False(FeedSession.IsTrustedOwner(system, user, user));
        Assert.False(FeedSession.IsTrustedOwner(administrators, user, user));
        Assert.False(FeedSession.IsTrustedOwner(null, user, administrators));
        Assert.False(FeedSession.IsTrustedOwner(otherUser, null, null));
        Assert.False(FeedSession.IsTrustedOwner(null, null, null));
    }
}
