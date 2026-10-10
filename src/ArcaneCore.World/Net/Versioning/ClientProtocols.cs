using ArcaneCore.Kernel;

namespace ArcaneCore.World.Net.Versioning;

/// <summary>
/// Picks the <see cref="IClientProtocol"/> for the build a client reports (multi-version design §3.2). Only build 5875
/// is registered. Any other build is refused exactly as before S0 (AUTH_VERSION_MISMATCH).
/// </summary>
public static class ClientProtocols
{
    /// <summary>The protocol a connection frames with before CMSG_AUTH_SESSION has named its build.</summary>
    public static IClientProtocol PreAuth => Build5875Protocol.Instance;

    public static bool TryGet(uint build, out IClientProtocol protocol)
    {
        if (ClientBuild.IsSupported(build) && build == Build5875Protocol.Instance.Build)
        {
            protocol = Build5875Protocol.Instance;
            return true;
        }

        protocol = null!;
        return false;
    }
}
