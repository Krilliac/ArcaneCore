using ArcaneCore.Kernel;

namespace ArcaneCore.Realm.Protocol.Versioning;

/// <summary>
/// The logon (auth server) side of one client build (multi-version design §3.2): which build it is and which
/// configured integrity hashes (ClientIntegrityHashOptions.Build) apply to it. The SRP6 math is shared by every build.
/// The challenge, proof and realm-list layouts are the 5875 ones until 2.4.3 (S3) adds its own.
/// </summary>
public interface IAuthProtocol
{
    ushort Build { get; }
}

/// <summary>Build 5875: the logon protocol LogonSession has always spoken.</summary>
public sealed class Build5875AuthProtocol : IAuthProtocol
{
    public static readonly Build5875AuthProtocol Instance = new();

    private Build5875AuthProtocol()
    {
    }

    public ushort Build => ClientBuild.Vanilla1121;
}

/// <summary>Picks the logon protocol for the build in a logon (or reconnect) challenge. Only 5875 is registered.</summary>
public static class AuthProtocols
{
    public static bool TryGet(uint build, out IAuthProtocol protocol)
    {
        if (ClientBuild.IsSupported(build) && build == Build5875AuthProtocol.Instance.Build)
        {
            protocol = Build5875AuthProtocol.Instance;
            return true;
        }

        protocol = null!;
        return false;
    }
}
