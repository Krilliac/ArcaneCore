using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Realm.Net;
using Xunit;

namespace ArcaneCore.Realm.Tests;

/// <summary>
/// Pins the strings LogonFaults shares. They feed the FaultLedger signature id (a signature hashes the site), so a changed value
/// would split a fault's history between releases.
/// </summary>
public sealed class LogonFaultsTests
{
    [Theory]
    [InlineData(LogonFaults.RecordedKey, "arcanecore.logon.recorded")]
    [InlineData(LogonFaults.StageKey, "arcanecore.logon.stage")]
    [InlineData(LogonFaults.OpcodeKey, "arcanecore.logon.opcode")]
    [InlineData(LogonFaults.EndpointKey, "arcanecore.logon.endpoint")]
    [InlineData(LogonFaults.StackKey, "arcanecore.logon.stack")]
    [InlineData(LogonFaults.ResilienceSite, "logon.resilience")]
    [InlineData(LogonFaults.SessionSite, "logon.session")]
    [InlineData(LogonFaults.IpBanCacheSite, "logon.ip-ban-cache")]
    [InlineData(LogonFaults.ListenerSite, "logon/listener")]
    [InlineData(LogonFaults.AdmitSite, "logon/admit")]
    [InlineData(LogonFaults.TagFaultContextSite, "logon/tag-fault-context")]
    [InlineData(LogonFaults.VerifyVersionFormatSite, "logon/verify-version/format")]
    public void SharedKeys_KeepTheirOriginalValues(string actual, string expected) => Assert.Equal(expected, actual);

    [Theory]
    [InlineData("logon-challenge", "crypto")]
    [InlineData("realm-list", "database")]
    [InlineData("logon-proof-autocreate", "resilience")]
    public void SessionFaultSite_MatchesTheOriginalInterpolation(string stage, string category)
        => Assert.Equal($"logon/{stage}/{category}", LogonFaults.SessionFaultSite(stage, category));

    [Fact]
    public void FaultLedgerSignatureIds_AreIdenticalForConstantsAndTheOriginalLiterals()
    {
        InvalidOperationException exception = new("fault");
        (string Literal, string Constant)[] sites =
        [
            ("logon.resilience", LogonFaults.ResilienceSite),
            ("logon.session", LogonFaults.SessionSite),
            ("logon.ip-ban-cache", LogonFaults.IpBanCacheSite),
            ("logon/listener", LogonFaults.ListenerSite),
            ("logon/admit", LogonFaults.AdmitSite),
            ("logon/tag-fault-context", LogonFaults.TagFaultContextSite),
            ("logon/verify-version/format", LogonFaults.VerifyVersionFormatSite),
            ("logon/logon-challenge/crypto", LogonFaults.SessionFaultSite("logon-challenge", "crypto")),
            ("logon/realm-list/resilience", LogonFaults.SessionFaultSite("realm-list", "resilience")),
        ];

        foreach ((string literal, string constant) in sites)
        {
            Assert.Equal(
                FaultLedger.SignatureOf(literal, exception, out _, out _),
                FaultLedger.SignatureOf(constant, exception, out _, out _));
        }
    }
}
