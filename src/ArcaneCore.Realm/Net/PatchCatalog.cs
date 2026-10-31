using System.Collections.Concurrent;
using System.Security.Cryptography;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Realm.Net;

/// <summary>A patch file chosen for one client: its absolute path, size and MD5 (the XFER_INITIATE fields).</summary>
public sealed record ClientPatch(string Path, long Size, byte[] Md5);

/// <summary>
/// Finds the patch for a client build and locale from the current <see cref="AutoPatchOptions"/> (read per call, so a configuration
/// reload applies to the next challenge). MD5s are cached per file path, size and write time (vmangos ClientPatchCache).
/// Thread-safe.
/// </summary>
public sealed class PatchCatalog(Func<AutoPatchOptions> options)
{
    private readonly Func<AutoPatchOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ConcurrentDictionary<(string Path, long Size, DateTime Written), byte[]> _md5 = new();

    public AutoPatchOptions Options => _options();

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
        if (!path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
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
            byte[] md5 = _md5.GetOrAdd((path, info.Length, info.LastWriteTimeUtc), static key =>
            {
                using FileStream stream = File.OpenRead(key.Path);
                return MD5.HashData(stream);
            });
            return new ClientPatch(path, info.Length, md5);
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
