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
    /// may hold the port, so the caller takes the next candidate). True when claimed, when reservation is disabled, or when the lock directory vanished and cannot be recreated (true without a lock: the caller's own listener probe decides).
    /// </summary>
    public bool TryReserve(int port)
    {
        if (!Enabled)
        {
            return true;
        }

        string path = Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"port-{port}.lock"));
        FileStream held;
        try
        {
            held = Open(path);
        }
        catch (DirectoryNotFoundException)
        {
            // The lock directory vanished after construction (a temp cleaner, a deleted run folder). That is not a held port: recreate it and retry once.
            // If it cannot be recreated either, nothing can be reserved, so the caller's own listener probe decides (as when reservation is disabled) instead of every port counting as held.
            try
            {
                Directory.CreateDirectory(_directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true; // the directory cannot be recreated: nothing is reservable, and that is not a held port
            }

            try
            {
                held = Open(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false; // the directory is back, but the file is held or foreign (a sharing violation, no permission, or a second deletion race): not reservable here
            }
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

    private static FileStream Open(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

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
