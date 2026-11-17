namespace ArcaneCore.Atlas.Extract;

/// <summary>
/// The write side shared by the three extractors: which names may become files and the one staged write. Stateless, and
/// safe to call in parallel for different targets; two calls for the same target at the same time (for example two
/// extraction runs into one folder) are not supported. None of these members protects against a link being swapped in
/// after the check (time of check versus time of use); that is out of scope for a maintainer-run tool.
/// </summary>
public static class ExtractOutput
{
    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"];

    /// <summary>
    /// Whether <paramref name="name"/> is one plain file name that means the same on every platform. True only when it is
    /// not empty; every character is printable ASCII (U+0020 to U+007E); none is <c>/</c>, <c>\</c>, <c>:</c>, <c>&lt;</c>,
    /// <c>&gt;</c>, <c>"</c>, <c>|</c>, <c>?</c> or <c>*</c>; it is not <c>.</c> or <c>..</c> and does not start with
    /// <c>..</c>; it does not end in a dot or a space; and its base name (the part before the first dot, trailing spaces
    /// ignored) is not a Windows device name (CON, PRN, AUX, NUL, CONIN$, CONOUT$, COM0-COM9, LPT0-LPT9), compared without
    /// regard to case. The answer is the same on Windows and Unix. It says nothing about whether the file exists or may be
    /// written.
    /// </summary>
    /// <param name="name">The candidate file name; may be null.</param>
    /// <returns>True when the name is plain.</returns>
    public static bool IsPlainFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)
            || !IsPrintableAscii(name)
            || name.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0
            || name is "." or ".."
            || name.StartsWith("..", StringComparison.Ordinal)
            || name[^1] is '.' or ' ')
        {
            return false;
        }

        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        return !IsDeviceName(stem);
    }

    /// <summary>
    /// The path of file <paramref name="fileName"/> directly inside <paramref name="directory"/>. Guarantees that the name
    /// is plain (<see cref="IsPlainFileName"/>) and that the resulting full path has exactly that file name and the full
    /// path of <paramref name="directory"/> as its parent. It does not resolve links and does not create anything.
    /// </summary>
    /// <param name="directory">The folder the file belongs in.</param>
    /// <param name="fileName">The file name, usually taken from client data.</param>
    /// <returns><c>Path.Combine(directory, fileName)</c>.</returns>
    /// <exception cref="InvalidDataException">The name is not plain, or the path would land outside the folder.</exception>
    public static string FileIn(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!IsPlainFileName(fileName))
        {
            throw new InvalidDataException($"'{fileName}' is not a plain file name.");
        }

        string path = Path.Combine(directory, fileName);
        string full = Path.GetFullPath(path);
        string expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string? parent = Path.GetDirectoryName(full);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (parent is null
            || !string.Equals(Path.TrimEndingDirectorySeparator(parent), expectedParent, comparison)
            || !string.Equals(Path.GetFileName(full), fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{fileName}' does not stay inside '{directory}'.");
        }

        return path;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="target"/> through <c>target + ".part"</c>, so a stopped run never
    /// leaves a cut-off file under the real name. Any existing staging entry is deleted first (which removes a link or hard
    /// link entry, not what it points to; a directory or directory junction at the staging name is not removed, and the call
    /// then fails with the exception <see cref="File.Delete"/> throws, before anything is written), the staging file is
    /// created new and exclusive, written, then moved over the target. When the write or the move fails, the staging file
    /// is removed and the original exception is rethrown. It does not check <paramref name="target"/> itself (see <see cref="FileIn"/>) and does not make the
    /// replacement atomic against readers on every file system.
    /// </summary>
    /// <param name="target">The final path; its folder must exist.</param>
    /// <param name="bytes">The whole file.</param>
    /// <returns>The number of bytes written.</returns>
    public static long WriteWhole(string target, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(bytes);

        string staging = target + ".part";
        File.Delete(staging);
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
            }

            File.Move(staging, target, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(staging);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Best effort: the original failure is the one worth reporting, and the next run deletes a left-over staging entry first.
            }

            throw;
        }

        return bytes.Length;
    }

    private static bool IsDeviceName(string stem)
    {
        foreach (string device in DeviceNames)
        {
            if (string.Equals(stem, device, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (stem.Length != 4
            || !(stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // The superscript digits Windows also treats as device numbers are not printable ASCII, so IsPlainFileName refused them already.
        return stem[3] is >= '0' and <= '9';
    }

    private static bool IsPrintableAscii(string name)
    {
        foreach (char c in name)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
