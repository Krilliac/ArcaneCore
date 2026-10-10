namespace ArcaneCore.Kernel;

/// <summary>
/// Supported client build(s). ArcaneCore targets vanilla 1.12.1 only today (Charter §2); the multi-version design
/// (docs/design/multi-version.md) adds builds behind a protocol adapter for each build. Until then
/// <see cref="IsSupported"/> accepts exactly <see cref="Vanilla1121"/>.
/// </summary>
public static class ClientBuild
{
    /// <summary>WoW 1.12.1 client build number.</summary>
    public const ushort Vanilla1121 = 5875;

    /// <summary>Every build a session may be opened with, in ascending order.</summary>
    public static IReadOnlyList<ushort> Supported { get; } = [Vanilla1121];

    /// <summary>Whether a client reporting <paramref name="build"/> (logon challenge or CMSG_AUTH_SESSION) is accepted.</summary>
    public static bool IsSupported(uint build) => build == Vanilla1121;
}
