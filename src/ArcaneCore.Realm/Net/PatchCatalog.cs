using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Realm.Net;

/// <summary>A patch file chosen for one client: its absolute path, size and MD5 (the XFER_INITIATE fields).</summary>
public sealed record ClientPatch(string Path, long Size, byte[] Md5);

/// <summary>
/// Finds the patch for a client build and locale from the current <see cref="AutoPatchOptions"/> (read per call, so a configuration
/// reload applies to the next challenge). MD5s are cached per file path, size and write time (vmangos ClientPatchCache).
/// <see cref="Warm"/> fills that cache for every configured patch at startup, so a logon proof finds the hash instead of reading the
/// whole file. Thread-safe.
/// </summary>
public sealed class PatchCatalog(Func<AutoPatchOptions> options)
{
    private readonly Func<AutoPatchOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ConcurrentDictionary<(string Path, long Size, DateTime Written), byte[]> _md5 = new();

    public AutoPatchOptions Options => _options();

    /// <summary>MD5s held (diagnostics and tests).</summary>
    public int CachedHashes => _md5.Count;

    /// <summary>
    /// Hash every patch the current options can offer, so no logon proof pays for it: each <see cref="AutoPatchOptions.Patches"/> entry,
    /// and the files of the patch folder that match <see cref="AutoPatchOptions.FileNamePattern"/> (a pattern with a path part is not
    /// scanned). Reads whole files: call it off the logon path (the server starts it on the thread pool). A file that cannot be read is
    /// skipped (<see cref="Find"/> meets it again at the first challenge, as before). A patch added or changed after this call is hashed
    /// by the first challenge that asks for it. Returns the number of files hashed or found already cached.
    /// </summary>
    public int Warm()
    {
        AutoPatchOptions current = _options();
        if (!current.Enabled)
        {
            return 0;
        }

        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(current.Directory) ? "." : current.Directory);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (AutoPatchEntry entry in current.Patches)
        {
            if (!string.IsNullOrWhiteSpace(entry.File))
            {
                string path = Path.GetFullPath(Path.Combine(root, entry.File));
                if (InsideFolder(root, path))
                {
                    paths.Add(path);
                }
            }
        }

        string pattern = current.FileNamePattern;
        if (pattern.Length > 0 && pattern.IndexOfAny(['/', '\\']) < 0 && Directory.Exists(root))
        {
            string expression = Regex.Escape(pattern)
                .Replace("\\{build}", "[0-9]{1,5}", StringComparison.Ordinal)
                .Replace("\\{locale}", "[A-Za-z]{4}", StringComparison.Ordinal);
            var name = new Regex("^" + expression + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            try
            {
                foreach (string file in Directory.EnumerateFiles(root))
                {
                    if (name.IsMatch(Path.GetFileName(file)))
                    {
                        paths.Add(Path.GetFullPath(file));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
            {
                FaultLedger.Shared.Record("realm.patch-warm", ex);
            }
        }

        int hashed = 0;
        foreach (string path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0)
                {
                    _ = Md5Of(path, info);
                    hashed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FaultLedger.Shared.Record("realm.patch-warm", ex);
            }
        }

        return hashed;
    }

    private static bool InsideFolder(string root, string path)
        => path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private byte[] Md5Of(string path, FileInfo info)
        => _md5.GetOrAdd((path, info.Length, info.LastWriteTimeUtc), static key =>
        {
            using FileStream stream = File.OpenRead(key.Path);
            return MD5.HashData(stream);
        });

    /// <summary>The patch to offer, or null when patching is off or no readable file matches.</summary>
    public ClientPatch? Find(ushort build, string locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        AutoPatchOptions current = _options();
        if (!current.Enabled || build == 0 || locale.Length != 4 || !locale.All(char.IsAsciiLetter))
        {
            return null;
        }

        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(current.Directory) ? "." : current.Directory);
        AutoPatchEntry? entry = current.Patches.FirstOrDefault(p => p.Build == build && string.Equals(p.Locale, locale, StringComparison.OrdinalIgnoreCase))
            ?? current.Patches.FirstOrDefault(p => p.Build == build && p.Locale.Length == 0);
        string? file = entry is not null
            ? entry.File
            : current.FileNamePattern.Length > 0
                ? current.FileNamePattern.Replace("{build}", build.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{locale}", locale, StringComparison.Ordinal)
                : null;
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        string path = Path.GetFullPath(Path.Combine(root, file));
        // A configured name may not climb out of the patch folder.
        if (!InsideFolder(root, path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return null;
        }

        try
        {
            return new ClientPatch(path, info.Length, Md5Of(path, info));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked by a writer or scanner, denied, or deleted since the Exists check: offer no patch (the client is answered
            // VersionInvalid) rather than fault the logon session. Nothing is cached, so the next challenge retries.
            AnomalyLedger.Shared.Report("logon", "patch-unreadable", Path.GetFileName(path), $"auto-patch file cannot be read: {ex.GetType().Name}: {ex.Message}", AnomalySeverity.Warning);
            return null;
        }
    }
}
