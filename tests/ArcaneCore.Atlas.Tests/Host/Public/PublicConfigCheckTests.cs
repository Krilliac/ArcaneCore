using System.Collections;
using ArcaneCore.Atlas.Host.Public;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>The start-up validation of the public listener (design 0.2, 0.10, 0.11): every refusal names what to change.</summary>
public sealed class PublicConfigCheckTests
{
    private static PublicEnvironmentInfo Env(PublicProcessMode mode = PublicProcessMode.Separate, int? loopback = null, bool fileExists = true, string? dns = null)
        => new(mode, loopback, _ => fileExists, _ => "secret", dns);

    private static PublicOptions Tls(Action<PublicOptions>? edit = null)
    {
        var options = new PublicOptions { Bind = "127.0.0.1:8443", Tls = new PublicTlsOptions { CertificatePath = "cert.pfx", CertificatePasswordEnv = "PW" } };
        edit?.Invoke(options);
        return options;
    }

    [Fact]
    public void WithoutTls_TheListenerRefuses_AndTheErrorNamesTheThreeWaysToFixIt()
    {
        PublicConfigResult result = PublicConfigCheck.Evaluate(new PublicOptions { Enabled = true, Bind = "127.0.0.1:8443" }, Env());

        Assert.False(result.Ok);
        string error = Assert.Single(result.Errors, e => e.Contains("without TLS", StringComparison.Ordinal));
        Assert.Contains("CertificatePath", error);
        Assert.Contains("BehindProxy", error);
        Assert.Contains("AllowInsecureDev", error);
    }

    [Fact]
    public void ACertificate_OnALiteralBind_NeedsNoOrigin_AndTheOriginIsDerived()
    {
        PublicConfigResult result = PublicConfigCheck.Evaluate(Tls(), Env());

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Equal(PublicTlsMode.Certificate, result.TlsMode);
        Assert.Equal("https://127.0.0.1:8443/", result.Origin!.AbsoluteUri);
        Assert.Contains("127.0.0.1:8443", result.AllowedHosts);
        Assert.True(result.LoginAllowed);
    }

    [Fact]
    public void AMissingCertificateFile_IsAnError()
    {
        PublicConfigResult result = PublicConfigCheck.Evaluate(Tls(), Env(fileExists: false));

        Assert.Contains(result.Errors, e => e.Contains("does not exist", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[::]:8443")]
    [InlineData("0.0.0.0:8443")]
    [InlineData("atlas.example.org:8443")]
    public void AWildcardOrNamedBind_NeedsAnOrigin_UnlessTheCertificateNamesOne(string bind)
    {
        PublicConfigResult without = PublicConfigCheck.Evaluate(Tls(o => o.Bind = bind), Env());
        PublicConfigResult derived = PublicConfigCheck.Evaluate(Tls(o => o.Bind = bind), Env(dns: "atlas.example.org"));
        PublicConfigResult explicitOrigin = PublicConfigCheck.Evaluate(Tls(o => { o.Bind = bind; o.Origin = "https://atlas.example.org"; }), Env());

        Assert.Contains(without.Errors, e => e.Contains("Atlas:Public:Origin is required", StringComparison.Ordinal));
        Assert.True(derived.Ok, string.Join("; ", derived.Errors));
        Assert.Equal("atlas.example.org:8443", derived.Origin!.Authority);
        Assert.True(explicitOrigin.Ok, string.Join("; ", explicitOrigin.Errors));
        Assert.Contains("atlas.example.org", explicitOrigin.AllowedHosts);
        Assert.True(explicitOrigin.OriginRequired);
    }

    [Theory]
    [InlineData("http://atlas.example.org")]
    [InlineData("https://atlas.example.org/path")]
    [InlineData("https://user@atlas.example.org")]
    [InlineData("atlas.example.org")]
    public void AnOrigin_MustBeSchemeHostPortOnly(string origin)
    {
        PublicConfigResult result = PublicConfigCheck.Evaluate(Tls(o => o.Origin = origin), Env());

        Assert.Contains(result.Errors, e => e.Contains("Atlas:Public:Origin", StringComparison.Ordinal));
    }

    [Fact]
    public void ADefaultPortOrigin_IsAcceptedWithoutThePortInTheHostHeader()
    {
        PublicConfigResult result = PublicConfigCheck.Evaluate(Tls(o => { o.Bind = "[::]:443"; o.Origin = "https://atlas.example.org"; }), Env());

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Contains("atlas.example.org", result.AllowedHosts);
    }

    [Fact]
    public void AWildcardInAllowedHosts_IsRefused()
    {
        PublicConfigResult result = PublicConfigCheck.Evaluate(Tls(o => o.AllowedHosts = ["*"]), Env());

        Assert.Contains(result.Errors, e => e.Contains("wildcard", StringComparison.Ordinal));
    }

    [Fact]
    public void BehindProxy_NeedsTrustedProxies_AndALoopbackBind_AndAnOrigin()
    {
        var options = new PublicOptions { BehindProxy = true, Bind = "127.0.0.1:8080" };
        PublicConfigResult empty = PublicConfigCheck.Evaluate(options, Env());
        options.TrustedProxies = ["127.0.0.1"];
        options.Origin = "https://atlas.example.org";
        PublicConfigResult complete = PublicConfigCheck.Evaluate(options, Env());
        options.Bind = "0.0.0.0:8080";
        PublicConfigResult exposed = PublicConfigCheck.Evaluate(options, Env());

        Assert.Contains(empty.Errors, e => e.Contains("TrustedProxies", StringComparison.Ordinal));
        Assert.Contains(empty.Errors, e => e.Contains("Origin is required", StringComparison.Ordinal));
        Assert.True(complete.Ok, string.Join("; ", complete.Errors));
        Assert.Equal(PublicTlsMode.Proxy, complete.TlsMode);
        Assert.Contains(exposed.Errors, e => e.Contains("loopback bind", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("127.0.0.1:8443", true, true)]
    [InlineData("192.168.1.20:8443", true, false)]
    [InlineData("10.1.2.3:8443", true, false)]
    [InlineData("[fd00::1]:8443", true, false)]
    [InlineData("203.0.113.5:8443", false, false)]
    [InlineData("0.0.0.0:8443", false, false)]
    public void InsecureDev_BindsOnlyLoopbackOrPrivateAddresses_AndLoginNeedsLoopback(string bind, bool allowed, bool login)
    {
        PublicConfigResult result = PublicConfigCheck.Evaluate(new PublicOptions { AllowInsecureDev = true, Bind = bind }, Env());

        Assert.Equal(allowed, result.Errors.Count == 0 || result.Errors.All(e => !e.Contains("AllowInsecureDev", StringComparison.Ordinal)));
        if (allowed)
        {
            Assert.Equal(PublicTlsMode.InsecureDev, result.TlsMode);
            Assert.Equal(login, result.LoginAllowed);
            Assert.Contains(result.Warnings, w => w.Contains("INSECURE-DEV", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ThePublicPort_MustDifferFromTheLoopbackListenersPort_WhateverTheAddresses()
    {
        PublicConfigResult same = PublicConfigCheck.Evaluate(Tls(o => o.Bind = "[::]:9000"), Env(loopback: 9000));
        PublicConfigResult different = PublicConfigCheck.Evaluate(Tls(o => { o.Bind = "[::]:9001"; o.Origin = "https://a.example"; }), Env(loopback: 9000));

        Assert.Contains(same.Errors, e => e.Contains("Local listener's port", StringComparison.Ordinal));
        Assert.True(different.Ok, string.Join("; ", different.Errors));
    }

    [Fact]
    public void Maps_MustBeContinents_WithoutDuplicates_AndFitThePool()
    {
        PublicConfigResult instance = PublicConfigCheck.Evaluate(Tls(o => o.Guest.Maps = [new() { Id = 36, Name = "Deadmines" }]), Env());
        PublicConfigResult duplicate = PublicConfigCheck.Evaluate(Tls(o => o.Guest.Maps = [new() { Id = 0, Name = "A" }, new() { Id = 0, Name = "B" }]), Env());
        PublicConfigResult tooMany = PublicConfigCheck.Evaluate(Tls(o => o.Feed.WebMaxConnections = 1), Env());
        PublicConfigResult fits = PublicConfigCheck.Evaluate(Tls(o => o.Feed.WebMaxConnections = 2), Env());
        PublicConfigResult inProcess = PublicConfigCheck.Evaluate(Tls(o => { o.Feed.WorldActionsEnabled = false; o.Feed.WorldMaxConnections = 2; }), Env(PublicProcessMode.InProcess));

        Assert.Contains(instance.Errors, e => e.Contains("not a continent", StringComparison.Ordinal));
        Assert.Contains(duplicate.Errors, e => e.Contains("twice", StringComparison.Ordinal));
        Assert.Contains(tooMany.Errors, e => e.Contains("pooled feed connection", StringComparison.Ordinal));
        Assert.True(fits.Ok, string.Join("; ", fits.Errors));
        Assert.Contains(inProcess.Errors, e => e.Contains("pooled feed connection", StringComparison.Ordinal)); // 2 maps, 2 - 1 kept for Local
    }

    [Fact]
    public void InProcessMode_IsRefused_WhenTheWorldHasActions_OrIsTheLiveServer()
    {
        PublicOptions Options(Action<PublicFeedOptions> edit)
            => Tls(o =>
            {
                o.Guest.Maps = [new() { Id = 0, Name = "EK" }];
                o.Feed.WorldMaxConnections = 3;
                edit(o.Feed);
            });

        PublicConfigResult actions = PublicConfigCheck.Evaluate(Options(f => f.WorldActionsEnabled = true), Env(PublicProcessMode.InProcess));
        PublicConfigResult live = PublicConfigCheck.Evaluate(Options(f => { f.WorldActionsEnabled = false; f.LiveServer = true; }), Env(PublicProcessMode.InProcess));
        PublicConfigResult fine = PublicConfigCheck.Evaluate(Options(f => f.WorldActionsEnabled = false), Env(PublicProcessMode.InProcess));
        PublicConfigResult separate = PublicConfigCheck.Evaluate(Options(f => f.WorldActionsEnabled = true), Env(PublicProcessMode.Separate));

        Assert.Contains(actions.Errors, e => e.Contains("separate process", StringComparison.Ordinal));
        Assert.Contains(live.Errors, e => e.Contains("live server", StringComparison.Ordinal));
        Assert.True(fine.Ok, string.Join("; ", fine.Errors));
        Assert.True(separate.Ok, string.Join("; ", separate.Errors));
    }

    [Fact]
    public void ShowPlayers_WarnsOnce_AndALowCellMinimumWarns()
    {
        PublicConfigResult result = PublicConfigCheck.Evaluate(Tls(o => { o.Guest.ShowPlayers = true; o.Guest.ShowPlayerNames = true; o.Guest.PlayerMinCellCount = 2; }), Env());

        Assert.Single(result.Warnings, w => w.Contains("guests can see real players' live positions", StringComparison.Ordinal) && w.Contains("names on", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("PlayerMinCellCount", StringComparison.Ordinal));
        Assert.DoesNotContain(PublicConfigCheck.Evaluate(Tls(), Env()).Warnings, w => w.Contains("real players", StringComparison.Ordinal));
    }

    [Fact]
    public void OutOfRangeSettings_AreErrors()
    {
        Assert.Contains(PublicConfigCheck.Evaluate(Tls(o => o.Guest.FeedHz = 50), Env()).Errors, e => e.Contains("FeedHz", StringComparison.Ordinal));
        Assert.Contains(PublicConfigCheck.Evaluate(Tls(o => o.Guest.MaxBytesPerSecond = 10), Env()).Errors, e => e.Contains("MaxBytesPerSecond", StringComparison.Ordinal));
        Assert.Contains(PublicConfigCheck.Evaluate(Tls(o => o.Bind = "127.0.0.1:0"), Env()).Errors, e => e.Contains("fixed port", StringComparison.Ordinal));
        Assert.Contains(PublicConfigCheck.Evaluate(Tls(o => o.Bind = "nonsense"), Env()).Errors, e => e.Contains("Bind", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSettings_LoadFromTheFile_ThenTheEnvironment_ThenTheFlags()
    {
        string file = Path.Combine(Path.GetTempPath(), $"atlas-public-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, """{"Atlas":{"Public":{"Enabled":false,"Bind":"127.0.0.1:9100","MinActionSecurity":"GameMaster","Guest":{"ShowPlayers":true,"Maps":[{"Id":1,"Name":"Kalimdor"}]},"TrustedProxies":["10.0.0.0/8"]}}}""");
            var environment = new Hashtable
            {
                ["ARCANECORE_ATLAS_PUBLIC__Guest__ShowPlayerNames"] = "true",
                ["ARCANECORE_ATLAS_PUBLIC__Bind"] = "127.0.0.1:9200",
                ["UNRELATED"] = "x",
            };

            PublicOptions options = PublicOptions.Load(file, environment, [new("Enabled", "true"), new("Bind", "127.0.0.1:9300")]);

            Assert.True(options.Enabled); // flag over file
            Assert.Equal("127.0.0.1:9300", options.Bind); // flag over environment over file
            Assert.Equal(PublicSecurity.GameMaster, options.MinActionSecurity);
            Assert.True(options.Guest.ShowPlayers); // from the file
            Assert.True(options.Guest.ShowPlayerNames); // from the environment
            Assert.Equal(1u, Assert.Single(options.Guest.Maps).Id); // the file's list replaces the defaults
            Assert.Equal(["10.0.0.0/8"], options.TrustedProxies);
            Assert.Equal(64 * 1024, options.Guest.MaxBytesPerSecond); // default kept
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AnUnknownKeyInTheSettings_IsAnError_NotSilentlyIgnored()
    {
        string file = Path.Combine(Path.GetTempPath(), $"atlas-public-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, """{"Atlas":{"Public":{"Guest":{"MaxBytesPerSecnd":1}}}}""");
            InvalidDataException typo = Assert.Throws<InvalidDataException>(() => PublicOptions.Load(file));
            Assert.Contains("MaxBytesPerSecnd", typo.Message, StringComparison.Ordinal);

            File.WriteAllText(file, """{"Atlas":{"Public":{"Guest":{"MaxBytesPerSecond":8192}}}}""");
            Assert.Equal(8192, PublicOptions.Load(file).Guest.MaxBytesPerSecond);
        }
        finally
        {
            File.Delete(file);
        }

        var environment = new Hashtable { ["ARCANECORE_ATLAS_PUBLIC__Bnd"] = "127.0.0.1:1" };
        Assert.Throws<InvalidDataException>(() => PublicOptions.Load(Path.Combine(Path.GetTempPath(), "missing-atlas.json"), environment));
    }

    [Fact]
    public void AWildcardCertificateName_IsAConfigurationError_NotAnException()
    {
        PublicConfigResult wildcard = PublicConfigCheck.Evaluate(Tls(o => o.Bind = "[::]:8443"), Env(dns: "*.example.org"));

        Assert.False(wildcard.Ok);
        Assert.Contains(wildcard.Errors, e => e.Contains("Atlas:Public:Origin cannot be derived", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingSettingsFile_GivesTheDefaults_AndABrokenOneIsInvalidData()
    {
        PublicOptions defaults = PublicOptions.Load(Path.Combine(Path.GetTempPath(), "missing-atlas.json"));
        string file = Path.Combine(Path.GetTempPath(), $"atlas-public-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, "{ not json");
            Assert.Throws<InvalidDataException>(() => PublicOptions.Load(file));
        }
        finally
        {
            File.Delete(file);
        }

        Assert.False(defaults.Enabled);
        Assert.Equal("[::]:8443", defaults.Bind);
        Assert.Equal(PublicSecurity.Moderator, defaults.MinActionSecurity);
        Assert.False(defaults.Guest.ShowPlayers);
        Assert.Equal(250, defaults.Guest.PlayerCellYards);
        Assert.Equal(120, defaults.Guest.PlayerDelaySeconds);
        Assert.Equal(3, defaults.Guest.PlayerMinCellCount);
        Assert.Equal(2, defaults.Guest.Maps.Count);
    }
}
