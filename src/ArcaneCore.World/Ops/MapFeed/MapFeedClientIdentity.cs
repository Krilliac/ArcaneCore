using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Who the other end of a map-feed pipe is, as the world can tell it (docs/areas/atlas-actions.md, "The local actor"): the client process id
/// (<c>GetNamedPipeClientProcessId</c>) and the user of that process's token (<c>OpenProcessToken</c>, <c>GetTokenInformation(TokenUser)</c>,
/// the SID translated to <c>DOMAIN\user</c>). It is not <c>NamedPipeServerStream.GetImpersonationUserName</c>: Atlas opens the pipe with
/// <c>TokenImpersonationLevel.Identification</c>, for which that call fails. Because the pipe's access list admits only the daemon's account
/// and SYSTEM, the user is always one of those: the identity names the process and the account, never a human operator. Any step that
/// fails gives the user <c>unknown</c>. Pure reads of the OS; any thread.
/// </summary>
internal static partial class MapFeedClientIdentity
{
    /// <summary>The prefix of the local actor's invoker name.</summary>
    public const string LocalPrefix = "atlas-local:";

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;

    /// <summary>The identity of the pipe client behind <paramref name="stream"/>; user <c>unknown</c> and pid 0 for anything that is not a Windows named pipe.</summary>
    public static ClientIdentity Of(Stream stream)
    {
        if (!OperatingSystem.IsWindows() || stream is not NamedPipeServerStream pipe)
        {
            return new ClientIdentity("unknown", 0);
        }

        try
        {
            if (!MapFeedNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid))
            {
                return new ClientIdentity("unknown", 0);
            }

            return new ClientIdentity(UserOfProcess(pid) ?? "unknown", pid);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
        {
            return new ClientIdentity("unknown", 0);
        }
    }

    /// <summary>The user of process <paramref name="pid"/>'s token as <c>DOMAIN\user</c>, or null when any step fails (the process is gone, access is denied).</summary>
    [SupportedOSPlatform("windows")]
    internal static string? UserOfProcess(uint pid)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token))
            {
                return null;
            }

            using (token)
            {
                return UserOfToken(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? UserOfToken(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out int needed);
        if (needed is <= 0 or > 4096)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(token, TokenUserClass, buffer, needed, out _))
            {
                return null;
            }

            // TOKEN_USER: a SID_AND_ATTRIBUTES whose first field is the pointer to the SID (stored inside this buffer).
            IntPtr sidPointer = Marshal.ReadIntPtr(buffer);
            if (sidPointer == IntPtr.Zero)
            {
                return null;
            }

            var sid = new SecurityIdentifier(sidPointer);
            try
            {
                return ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            }
            catch (IdentityNotMappedException)
            {
                return sid.Value;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returnLength);
}

/// <summary>The pipe client of a connection: the user of its process token and its process id.</summary>
/// <param name="User">The token user (<c>DOMAIN\user</c>), or <c>unknown</c>.</param>
/// <param name="ProcessId">The client process id, 0 when unknown.</param>
internal readonly record struct ClientIdentity(string User, uint ProcessId)
{
    /// <summary>The local actor's invoker name: <c>atlas-local:&lt;token user&gt;#&lt;pid&gt;</c>.</summary>
    public string ActorName => string.Create(CultureInfo.InvariantCulture, $"{MapFeedClientIdentity.LocalPrefix}{User}#{ProcessId}");

    /// <summary>The operator the <c>caps</c> answer names: <c>atlas-local:&lt;token user&gt;</c>.</summary>
    public string Operator => MapFeedClientIdentity.LocalPrefix + User;
}
