using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// Standing directive: every deliberate deviation from retail sits behind a config option that
/// defaults to retail. vmangos has no connection cap, no read timeout and no account-name
/// charset rule (AuthSocket.cpp), so those knobs default to off; the 300 s session cap is the
/// vmangos MaxSessionDuration default (AuthSocket.cpp:76-82) and stays on.
/// </summary>
public sealed class RetailDefaultsTests
{
    [Fact]
    public void HardeningKnobs_DefaultToRetail()
    {
        var o = new AuthOptions();
        Assert.Equal(0, o.MaxConnections);
        Assert.Equal(0, o.MaxConnectionsPerIp);
        Assert.Equal(0, o.ReadTimeoutSeconds);
        Assert.False(o.StrictUsernameCharset);
        Assert.False(o.AutocreateAccounts);
        Assert.Equal(300, o.MaxSessionDurationSeconds);
    }

    [Fact]
    public void HardeningKnobs_BindFromTheAuthSection()
    {
        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:MaxConnections"] = "10",
            ["Auth:MaxConnectionsPerIp"] = "2",
            ["Auth:ReadTimeoutSeconds"] = "7",
            ["Auth:StrictUsernameCharset"] = "true",
        }).Build();

        var o = new AuthOptions();
        cfg.GetSection(AuthOptions.SectionName).Bind(o);

        Assert.Equal(10, o.MaxConnections);
        Assert.Equal(2, o.MaxConnectionsPerIp);
        Assert.Equal(7, o.ReadTimeoutSeconds);
        Assert.True(o.StrictUsernameCharset);
    }
}
