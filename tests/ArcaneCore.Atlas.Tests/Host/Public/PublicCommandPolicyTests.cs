using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Host.Public;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>The public command policy (design 0.3.1, I9): an explicit allowlist, so a command nobody has listed is denied, including ones added tomorrow.</summary>
public sealed class PublicCommandPolicyTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "atlas-policy-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }

    private AtlasContext ComposeEverything()
        => HostComposer.Compose(
            new AtlasPaths(null, null),
            NullLoggerFactory.Instance,
            headless: true,
            [.. AtlasModules.Discover(typeof(AtlasModules).Assembly).Select(m => m is HostModule ? new HostModule(new AtlasState(_state)) : m)]);

    [Fact]
    public void EveryRegisteredCommand_ThatIsNotOnTheAllowlist_IsDenied_ToEveryKindOfVisitor()
    {
        AtlasContext context = ComposeEverything();
        string[] registered = [.. context.Commands.All.Select(c => c.Name)];
        var options = new PublicOptions();
        HashSet<string> allowlist = [.. PublicCommandPolicy.GuestCommands, .. PublicCommandPolicy.AccountCommands, .. PublicCommandPolicy.ActionCommands];

        // The enumeration must really see the registry: these Local commands are what the policy exists to keep off the Internet.
        Assert.True(registered.Length >= 40, $"only {registered.Length} commands are registered; the registry was not composed");
        foreach (string local in new[] { "status", "help", "maps.list", "layers.list", "view.set", "camera.goto", "capture", "public.status", "public.kick", "public.sessions", "public.revoke" })
        {
            Assert.Contains(local, registered);
        }

        var denied = new List<string>();
        foreach (string name in registered)
        {
            foreach ((PublicTier tier, int security) in new[] { (PublicTier.Guest, 0), (PublicTier.Account, 0), (PublicTier.Account, 1), (PublicTier.Account, 3) })
            {
                bool allowed = PublicCommandPolicy.IsAllowed(name, tier, security, options);
                if (!allowlist.Contains(name))
                {
                    Assert.False(allowed, $"{name} is on no allowlist but a {tier} at level {security} may run it");
                }
            }

            if (!allowlist.Contains(name))
            {
                denied.Add(name);
            }
        }

        Assert.True(denied.Count >= 35, $"only {denied.Count} registered commands were checked as denied");
        foreach (string name in new[] { "server.console", "live.inject", "live.send", "live.tap.start", "live.tap.stop", "export.tiles", "query.los", "query.path", "query.pick" }.Where(registered.Contains))
        {
            Assert.DoesNotContain(name, PublicCommandPolicy.AllowedFor(PublicTier.Account, 3, options));
        }

        // A name nobody registered is denied the same way.
        Assert.False(PublicCommandPolicy.IsAllowed("brand.new.command", PublicTier.Account, 3, options));
        Assert.False(PublicCommandPolicy.IsAllowed("GUEST.STATUS", PublicTier.Guest, 0, options));
        Assert.False(PublicCommandPolicy.IsAllowed("guest.status ", PublicTier.Guest, 0, options));
        Assert.False(PublicCommandPolicy.IsAllowed(string.Empty, PublicTier.Guest, 0, options));
    }

    [Fact]
    public void TheCommandsAGuestMayRun_AreExactlyTheListedOnes_AndEachAccountExtraNeedsItsTier()
    {
        var options = new PublicOptions();

        Assert.Equal(
            ["bookmarks.list", "guest.help", "guest.layers", "guest.maps", "guest.status", "live.entities", "live.find", "pick.public"],
            PublicCommandPolicy.AllowedFor(PublicTier.Guest, 0, options).ToArray());
        Assert.Equal(
            PublicCommandPolicy.AllowedFor(PublicTier.Guest, 0, options).Append("me.characters").Order(StringComparer.Ordinal).ToArray(),
            PublicCommandPolicy.AllowedFor(PublicTier.Account, 0, options).ToArray());
        Assert.Equal(
            PublicCommandPolicy.GuestCommands.Concat(PublicCommandPolicy.AccountCommands).Concat(PublicCommandPolicy.ActionCommands).Order(StringComparer.Ordinal).ToArray(),
            PublicCommandPolicy.AllowedFor(PublicTier.Account, 1, options).ToArray());

        // Actions need the account to reach MinActionSecurity, and the listener to offer them at all.
        options.MinActionSecurity = PublicSecurity.GameMaster;
        Assert.DoesNotContain("action.run", PublicCommandPolicy.AllowedFor(PublicTier.Account, 1, options));
        Assert.Contains("action.run", PublicCommandPolicy.AllowedFor(PublicTier.Account, 2, options));
        options.AccountActions = false;
        Assert.DoesNotContain("action.run", PublicCommandPolicy.AllowedFor(PublicTier.Account, 3, options));
        Assert.DoesNotContain("action.run", PublicCommandPolicy.AllowedFor(PublicTier.Guest, 3, options));
    }
}
