using System.Diagnostics;
using System.Runtime.InteropServices;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>
/// An <see cref="IOutputPathPlatform"/> that forwards to the real one except where a test replaces a query. Lets the refusal cases
/// (a drive letter the session does not define, a network or CD drive, a round trip that opens something else, extra mount points)
/// be tested without administrator rights and without redefining a real drive letter.
/// </summary>
internal sealed class FakeOutputPathPlatform : IOutputPathPlatform
{
    private readonly IOutputPathPlatform _real = SystemOutputPathPlatform.Instance;

    public Func<char, string?>? DosDevice { get; init; }

    public Func<string, WindowsDriveType?>? DriveType { get; init; }

    /// <summary>Replaces an open: return null to let the real platform open the path.</summary>
    public Func<string, OpenMode, OpenResult?>? OpenOverride { get; init; }

    /// <summary>Replaces a volume's mount paths: return null to use the real list.</summary>
    public Func<string, IReadOnlyList<string>?>? MountPaths { get; init; }

    public Func<string, EntryProbe?>? ProbeOverride { get; init; }

    public Func<string, UnixEntry>? LstatOverride { get; init; }

    public Action<string>? AfterCreateDirectory { get; init; }

    /// <summary>Every path given to <see cref="Open"/>, in order.</summary>
    public List<(string Path, OpenMode Mode)> Opened { get; } = [];

    public string? QueryDosDevice(char letter) => DosDevice is null ? _real.QueryDosDevice(letter) : DosDevice(letter);

    public WindowsDriveType GetDriveType(string root) => DriveType?.Invoke(root) ?? _real.GetDriveType(root);

    public OpenResult Open(string path, OpenMode mode)
    {
        lock (Opened)
        {
            Opened.Add((path, mode));
        }

        return OpenOverride?.Invoke(path, mode) ?? _real.Open(path, mode);
    }

    public IReadOnlyList<string>? VolumeMountPaths(string volumeRoot, out string? failure)
    {
        if (MountPaths?.Invoke(volumeRoot) is { } paths)
        {
            failure = null;
            return paths;
        }

        return _real.VolumeMountPaths(volumeRoot, out failure);
    }

    public EntryProbe Probe(string path) => ProbeOverride?.Invoke(path) ?? _real.Probe(path);

    public UnixEntry Lstat(string path) => LstatOverride is null ? _real.Lstat(path) : LstatOverride(path);

    public void CreateDirectory(string path)
    {
        _real.CreateDirectory(path);
        AfterCreateDirectory?.Invoke(path);
    }
}

/// <summary>
/// A drive letter made for one test (by <c>subst</c> or <c>DefineDosDevice</c>), removed on dispose. Drive letters belong to the whole
/// logon session, so every test process that makes one first takes the named mutex <c>Local\ArcaneCore.Atlas.SubstDriveTest</c>;
/// a letter that turns out to be taken (by something outside the tests) is skipped and the next free one tried. Dispose never throws:
/// a failed removal is recorded in <see cref="CleanupError"/> for the test to assert after its finally block.
/// </summary>
internal sealed class SessionDrive : IDisposable
{
    private const string MutexName = @"Local\ArcaneCore.Atlas.SubstDriveTest";
    private const uint RawTargetPath = 0x1;
    private const uint RemoveDefinition = 0x2;
    private const uint ExactMatchOnRemove = 0x4;

    private readonly MutexHolder _mutex;
    private readonly string? _rawTarget;
    private bool _disposed;

    private SessionDrive(MutexHolder mutex, char letter, string? rawTarget)
    {
        _mutex = mutex;
        Letter = letter;
        _rawTarget = rawTarget;
    }

    public char Letter { get; }

    /// <summary>Why removing the letter failed, or null.</summary>
    public string? CleanupError { get; private set; }

    /// <summary><c>subst X: folder</c> on the first free letter.</summary>
    public static SessionDrive Subst(string folder) => Make(letter => Run("subst.exe", $"{letter}: \"{folder}\""), rawTarget: null);

    /// <summary><c>DefineDosDevice(DDD_RAW_TARGET_PATH, "X:", target)</c> on the first free letter: a letter that names an NT device path directly.</summary>
    public static SessionDrive RawTarget(string ntTarget)
        => Make(letter => Native.DefineDosDevice(RawTargetPath, $"{letter}:", ntTarget) ? null : $"DefineDosDevice failed with Windows error {Marshal.GetLastPInvokeError()}", ntTarget);

    /// <summary>The NT device path of the volume holding <paramref name="path"/> plus the rest of the path, for <see cref="RawTarget"/>.</summary>
    public static string NtPathOf(string path)
    {
        string device = ClientOutputPath.QueryDosDevice(path[0]) ?? throw new InvalidOperationException($"drive {path[0]}: has no DOS device name");
        return device + path[2..];
    }

    private static SessionDrive Make(Func<char, string?> define, string? rawTarget)
    {
        var mutex = new MutexHolder();
        var failures = new List<string>();
        try
        {
            foreach (char letter in FreeLetters())
            {
                if (define(letter) is { } failure)
                {
                    failures.Add($"{letter}: {failure}");
                    continue;
                }

                return new SessionDrive(mutex, letter, rawTarget);
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        mutex.Dispose();
        throw new InvalidOperationException("no drive letter could be defined for the test: " + string.Join("; ", failures));
    }

    private static IEnumerable<char> FreeLetters()
    {
        HashSet<char> used = [.. DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0]))];
        for (char letter = 'Z'; letter >= 'H'; letter--)
        {
            if (!used.Contains(letter) && ClientOutputPath.QueryDosDevice(letter) is null)
            {
                yield return letter;
            }
        }
    }

    private static string? Run(string file, string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? null : $"{file} {arguments} exited with {process.ExitCode}: {output.Trim()}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            CleanupError = _rawTarget is null
                ? Run("subst.exe", $"{Letter}: /d")
                : Native.DefineDosDevice(RawTargetPath | RemoveDefinition | ExactMatchOnRemove, $"{Letter}:", _rawTarget)
                    ? null
                    : $"removing {Letter}: failed with Windows error {Marshal.GetLastPInvokeError()}";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            CleanupError = $"removing {Letter}: threw {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            _mutex.Dispose();
        }
    }

    /// <summary>
    /// Holds the named mutex on a thread of its own: a mutex belongs to the thread that took it, and an async test may finish on
    /// another thread than it started on. Created on any thread, disposed on any thread.
    /// </summary>
    private sealed class MutexHolder : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private Exception? _failure;

        public MutexHolder()
        {
            _thread = new Thread(Hold) { IsBackground = true, Name = "SessionDrive mutex" };
            _thread.Start();
            _acquired.Wait();
            if (_failure is not null)
            {
                _thread.Join();
                throw new TimeoutException(_failure.Message, _failure);
            }
        }

        private void Hold()
        {
            using var mutex = new Mutex(initiallyOwned: false, MutexName);
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromMinutes(2)))
                {
                    _failure = new TimeoutException($"another test process held {MutexName} for two minutes");
                    _acquired.Set();
                    return;
                }
            }
            catch (AbandonedMutexException)
            {
                // A test process died holding it: the mutex is this thread's now, and any letter it left behind is skipped as taken.
            }

            _acquired.Set();
            _release.Wait();
            mutex.ReleaseMutex();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _acquired.Dispose();
            _release.Dispose();
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string? lpTargetPath);
    }
}

/// <summary>Folders the guard tests build: fake working trees and bare repositories under a scratch root.</summary>
internal static class GuardFixtures
{
    /// <summary>A folder holding a <c>.git</c> directory, with an empty <c>inside</c> folder; returns the tree.</summary>
    public static string WorkingTree(string parent, string name, out string inside)
    {
        string tree = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(tree, ".git"));
        inside = Path.Combine(tree, "inside");
        Directory.CreateDirectory(inside);
        return tree;
    }

    /// <summary>The 8.3 short form of an existing path, or null when the volume makes no short names (or the path needs none).</summary>
    public static string? ShortPath(string path)
    {
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c for %I in (\"{path}\") do @echo %~sI")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && output.Length > 0 && !string.Equals(output, path, StringComparison.OrdinalIgnoreCase) ? output : null;
    }

    /// <summary>Runs <c>icacls</c> with the arguments and asserts it succeeded.</summary>
    public static string? Icacls(params string[] arguments)
    {
        var info = new ProcessStartInfo("icacls.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? null : $"icacls {string.Join(' ', arguments)} exited with {process.ExitCode}: {output.Trim()}";
    }

    /// <summary>
    /// A junction made by PowerShell's <c>New-Item -ItemType Junction</c>, which (unlike <c>mklink /J</c>, which answers "Local volumes are
    /// required") accepts a target on a <c>subst</c> drive and stores it as <c>\??\X:\...</c>. Delete it with <see cref="DirectoryLinks.Delete"/>.
    /// </summary>
    public static void JunctionTo(string link, string target)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'", "''", StringComparison.Ordinal)}' -Target '{target.Replace("'", "''", StringComparison.Ordinal)}' | Out-Null");
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(link), $"could not create the junction {link} -> {target} for the test: {output.Trim()}");
    }
}

/// <summary>A fact that runs only off Windows (the Unix path rules); skipped, visibly, on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix path rules.";
        }
    }
}
