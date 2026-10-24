using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ArcaneCore.World.Ops.Control;

/// <summary>
/// Creates the control pipe's server end with <c>CreateNamedPipeW</c>, because the runtime's own constructors
/// (<see cref="NamedPipeServerStreamAcl"/> included) cannot pass <c>PIPE_REJECT_REMOTE_CLIENTS</c>: their pipe mode carries only the
/// transmission-mode bits. The pipe is created with the descriptor it is given, <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> (a name another
/// process created first fails with access denied instead of being joined), one instance, byte mode, overlapped I/O and
/// <c>PIPE_REJECT_REMOTE_CLIENTS</c> (the pipe file system refuses any client that does not open it from this machine). Any thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class LocalPipeServer
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;

    /// <summary><c>WRITE_DAC</c> in the open mode: the creator's handle may replace the pipe's DACL later (<c>SetAccessControl</c>).</summary>
    private const uint WriteDacRight = 0x00040000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    /// <summary>The most instances <see cref="Create(string, PipeSecurity, uint, bool, bool)"/> accepts (255 is PIPE_UNLIMITED_INSTANCES, which no caller here wants).</summary>
    public const uint MaxFiniteInstances = 254;

    /// <summary><c>FILE_PIPE_REJECT_REMOTE_CLIENTS</c> in <c>FILE_PIPE_LOCAL_INFORMATION.NamedPipeType</c> (ntifs.h).</summary>
    private const uint FilePipeRejectRemoteClients = 0x00000002;

    /// <summary><c>FilePipeLocalInformation</c> (FILE_INFORMATION_CLASS 24).</summary>
    private const int FilePipeLocalInformation = 24;

    /// <summary>
    /// A single-instance, local-only, asynchronous byte pipe named <c>\\.\pipe\<paramref name="name"/></c> carrying
    /// <paramref name="security"/>, not yet connected. Throws <see cref="UnauthorizedAccessException"/> when the name already exists
    /// (whoever created it), <see cref="IOException"/> for any other failure.
    /// </summary>
    public static NamedPipeServerStream Create(string name, PipeSecurity security)
        => Create(name, security, maxInstances: 1, firstInstance: true);

    /// <summary>
    /// A local-only, asynchronous byte pipe like <see cref="Create(string, PipeSecurity)"/> that allows <paramref name="maxInstances"/>
    /// instances of the name (1 to <see cref="MaxFiniteInstances"/>; every instance of one name must pass the same limit). The first
    /// instance (<paramref name="firstInstance"/> true) carries <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c>, so a name another process created
    /// first is refused instead of joined. A further instance (<paramref name="firstInstance"/> false) has no such flag: it joins the name,
    /// and the system admits it only while the first instance's descriptor grants the caller the right to create instances and the limit
    /// is not reached (otherwise the call fails with <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/>). A caller
    /// that creates all <paramref name="maxInstances"/> itself, before it serves any client, therefore holds every slot of the name: nobody
    /// else can add an instance, and a client beyond the limit finds the pipe busy. The map feed uses this for its connection limit
    /// (<c>World:Control:MapFeed</c>); the control pipe stays one instance. With <paramref name="writeDac"/> the handle is also opened with
    /// <c>WRITE_DAC</c>, so the creator can swap the descriptor after all instances exist (the web pipe, whose final access list does not
    /// name the daemon and so could not create the further instances).
    /// </summary>
    public static NamedPipeServerStream Create(string name, PipeSecurity security, uint maxInstances, bool firstInstance, bool writeDac = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(security);
        ArgumentOutOfRangeException.ThrowIfZero(maxInstances);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxInstances, MaxFiniteInstances);
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        SafePipeHandle handle;
        unsafe
        {
            fixed (byte* sd = descriptor)
            {
                var attributes = new SecurityAttributes
                {
                    Length = (uint)sizeof(SecurityAttributes),
                    SecurityDescriptor = sd,
                    InheritHandle = 0,
                };
                handle = CreateNamedPipeW(
                    @"\\.\pipe\" + name,
                    PipeAccessDuplex | (firstInstance ? FileFlagFirstPipeInstance : 0) | FileFlagOverlapped | (writeDac ? WriteDacRight : 0),
                    PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                    maxInstances,
                    outBufferSize: 0,
                    inBufferSize: 0,
                    defaultTimeout: 0,
                    &attributes);
            }
        }

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            var cause = new Win32Exception(error);
            throw error == 5 // ERROR_ACCESS_DENIED: the name exists (FILE_FLAG_FIRST_PIPE_INSTANCE) or its descriptor refuses us.
                ? new UnauthorizedAccessException($"could not create \\\\.\\pipe\\{name}: {cause.Message}", cause)
                : new IOException($"could not create \\\\.\\pipe\\{name}: {cause.Message}", cause);
        }

        try
        {
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether the pipe file system will refuse remote clients of <paramref name="pipe"/> (read back from the kernel, not from what
    /// was asked for); throws <see cref="IOException"/> when the query fails.
    /// </summary>
    public static bool RejectsRemoteClients(SafePipeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        FilePipeLocalInfo info;
        int status;
        unsafe
        {
            status = NtQueryInformationFile(pipe, out _, &info, (uint)sizeof(FilePipeLocalInfo), FilePipeLocalInformation);
        }

        if (status < 0)
        {
            throw new IOException($"NtQueryInformationFile(FilePipeLocalInformation) failed: NTSTATUS 0x{status:X8}");
        }

        return (info.NamedPipeType & FilePipeRejectRemoteClients) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SecurityAttributes
    {
        public uint Length;
        public void* SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilePipeLocalInfo
    {
        public uint NamedPipeType;
        public uint NamedPipeConfiguration;
        public uint MaximumInstances;
        public uint CurrentInstances;
        public uint InboundQuota;
        public uint ReadDataAvailable;
        public uint OutboundQuota;
        public uint WriteQuotaAvailable;
        public uint NamedPipeState;
        public uint NamedPipeEnd;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial SafePipeHandle CreateNamedPipeW(
        string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout,
        SecurityAttributes* securityAttributes);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationFile(
        SafePipeHandle file, out IoStatusBlock ioStatus, void* information, uint length, int informationClass);
}
