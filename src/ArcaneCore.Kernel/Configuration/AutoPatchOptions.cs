namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The logon auto-patcher (<see cref="SectionName"/>): a client whose build is not 5875 is offered an MPQ patch over the logon stream
/// (XFER_INITIATE / XFER_DATA, vmangos realmd AuthSocket.cpp:596-662 and 1152-1338; AscEmu logonserver/Auth/AutoPatcher.cpp). Off by
/// default. Every field is read again on each challenge, so an edited appsettings file (reloadOnChange) takes effect for the next
/// connection without a restart.
/// </summary>
public sealed class AutoPatchOptions
{
    public const string SectionName = "Auth:AutoPatch";

    /// <summary>Offer patches at all. Default false: a non-5875 client gets WOW_FAIL_VERSION_INVALID as before.</summary>
    public bool Enabled { get; set; }

    /// <summary>The folder patches are read from (vmangos PatchesDir, default "./patches"); relative paths are from the working directory.</summary>
    public string Directory { get; set; } = "patches";

    /// <summary>
    /// The file looked up when no <see cref="Patches"/> entry matches: <c>{build}</c> and <c>{locale}</c> are replaced (vmangos
    /// "%d%s.mpq", e.g. 5464enUS.mpq). Empty disables the fallback so only listed patches are served.
    /// </summary>
    public string FileNamePattern { get; set; } = "{build}{locale}.mpq";

    /// <summary>Explicit patches, checked first: an exact build and locale, then the same build with an empty (any) locale.</summary>
    public List<AutoPatchEntry> Patches { get; set; } = [];
}

/// <summary>One served patch: the client build and locale it upgrades, and the file under <see cref="AutoPatchOptions.Directory"/>.</summary>
public sealed class AutoPatchEntry
{
    public ushort Build { get; set; }

    /// <summary>A client locale such as enUS; empty matches every locale.</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>File name (or path) relative to <see cref="AutoPatchOptions.Directory"/>.</summary>
    public string File { get; set; } = string.Empty;
}
