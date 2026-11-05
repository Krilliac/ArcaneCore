namespace ArcaneCore.Realm.Net;

/// <summary>
/// The strings the logon path shares between the places that record a fault and the places that read it back: the
/// <see cref="ArcaneCore.Kernel.Diagnostics.FaultLedger"/> site names and the <see cref="Exception.Data"/> keys a session tags onto
/// a fault so <see cref="LogonServer"/> does not record it twice. The values are the ones the code used as literals before, and they
/// are part of the FaultLedger signature id (a signature hashes the site), so changing one splits a fault's history: pin them in
/// tests/ArcaneCore.Realm.Tests (LogonFaultsTests) rather than editing them here.
/// </summary>
public static class LogonFaults
{
    /// <summary>Data key: the FaultLedger id the session already recorded for this exception.</summary>
    public const string RecordedKey = "arcanecore.logon.recorded";

    /// <summary>Data key: the session stage ("logon-challenge", "realm-list", ...).</summary>
    public const string StageKey = "arcanecore.logon.stage";

    /// <summary>Data key: the inbound opcode as <c>0xNN/Command</c>.</summary>
    public const string OpcodeKey = "arcanecore.logon.opcode";

    /// <summary>Data key: the remote endpoint.</summary>
    public const string EndpointKey = "arcanecore.logon.endpoint";

    /// <summary>Data key: the condensed stack the session computed.</summary>
    public const string StackKey = "arcanecore.logon.stack";

    /// <summary>Site: a database-resilience rejection caught in the session loop.</summary>
    public const string ResilienceSite = "logon.resilience";

    /// <summary>Site: an unexpected exception caught in the session loop.</summary>
    public const string SessionSite = "logon.session";

    /// <summary>Site: a refresh failure in the realm IP-ban cache.</summary>
    public const string IpBanCacheSite = "logon.ip-ban-cache";

    /// <summary>Site: the accept loop failed.</summary>
    public const string ListenerSite = "logon/listener";

    /// <summary>Site: admitting a connection failed.</summary>
    public const string AdmitSite = "logon/admit";

    /// <summary>Site: tagging fault context onto an exception failed.</summary>
    public const string TagFaultContextSite = "logon/tag-fault-context";

    /// <summary>Site: pre-hashing the configured client patches failed (the startup warm in <see cref="LogonServer"/> and the folder scan and file reads inside <see cref="PatchCatalog.Warm"/>).</summary>
    public const string PatchWarmSite = "realm.patch-warm";

    /// <summary>Site: a malformed IntegrityHashes hex entry while verifying a client version.</summary>
    public const string VerifyVersionFormatSite = "logon/verify-version/format";

    /// <summary>The site of a classified session fault: <c>logon/{stage}/{category}</c>.</summary>
    public static string SessionFaultSite(string stage, string category) => $"logon/{stage}/{category}";
}
