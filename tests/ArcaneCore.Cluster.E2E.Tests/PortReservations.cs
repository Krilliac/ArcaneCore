using System.Globalization;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>
/// Cross-process port reservation for the cluster fixture: a port is claimed by an exclusive lock file (<see cref="FileShare.None"/>, an exclusive sharing mode on
/// Windows and an flock on Linux) in a shared directory, held until <see cref="Dispose"/> (the OS drops it when the process dies, the file stays behind and is reused).
/// Two holders of one directory can never hold one port. State: the open lock streams, guarded by a private lock; safe to call from any thread, <see cref="Dispose"/> once.
/// </summary>
internal sealed class PortReservations : IDisposable
{
    private readonly string _directory;
    private readonly List<FileStream> _held = [];
    private readonly object _gate = new();

    /// <summary>Creates the directory when needed. A directory that cannot be created or used disables reservation (<see cref="Enabled"/> false); nothing else does.</summary>
    public PortReservations(string directory)
    {
        _directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            Enabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Enabled = false; // a file where the directory belongs, a full disk, no permission: no reservation, the caller's own probe decides
        }
    }

    /// <summary>False when the lock directory is unusable: <see cref="TryReserve"/> then claims nothing and always succeeds, so callers can tell this host apart from one where reservation works.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// Claims <paramref name="port"/> until disposal. False when another holder has it, or when its lock file cannot be opened by this user (another user's file, EACCES: that user's run
    /// may hold the port, so the caller takes the next candidate). True when claimed, or when reservation is disabled.
    /// </summary>
    public bool TryReserve(int port)
    {
        if (!Enabled)
        {
            return true;
        }

        FileStream held;
        try
        {
            held = new FileStream(
                Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"port-{port}.lock")),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        lock (_gate)
        {
            _held.Add(held);
        }

        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (FileStream stream in _held)
            {
                stream.Dispose();
            }

            _held.Clear();
        }
    }
}
