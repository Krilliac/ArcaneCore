using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Kernel.Safety;

namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>How a drive letter's DOS device name (from <c>QueryDosDevice</c>) maps to a location.</summary>
public enum DriveKind
{
    /// <summary>An ordinary local volume (<c>\Device\HarddiskVolumeN</c> and the like); the path is already the real one.</summary>
    Plain,

    /// <summary>A <c>subst</c> drive: the letter stands for a folder of another drive.</summary>
    Substituted,

    /// <summary>A network share or any other device or object-manager name the path rules cannot follow.</summary>
    Foreign,
}

/// <summary>A parsed <c>QueryDosDevice</c> result.</summary>
/// <param name="Kind">What the letter is.</param>
/// <param name="Path">For <see cref="DriveKind.Substituted"/>, the folder the letter stands for (with a drive letter and a separator); otherwise null.</param>
internal readonly record struct DriveTarget(DriveKind Kind, string? Path);

/// <summary>
/// Decides where <c>client.read</c> may write. Client data must never end up in a git working tree (it is not ours to
/// commit) or inside the client installation itself (which stays read-only). Stateless and thread-safe.
/// </summary>
public static partial class ClientOutputPath
{
    /// <summary>
    /// Validates <paramref name="requested"/> and returns the full path to write to.
    /// </summary>
    /// <param name="requested">The caller's <c>out</c> argument; must be absolute.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <param name="protectedRoots">Folders the output must not be inside of (for example the client root).</param>
    /// <exception cref="CommandException">The path is relative, a directory, inside a git working tree or a protected folder, already exists without <paramref name="overwrite"/>, or (on Windows) not a plain drive-letter path (device, volume, UNC, <c>\??\</c> and network forms, a drive mapped to a share), a stream, an 8.3 short name, or a path with a component ending in a space or dot; a <c>subst</c> drive is judged by the folder it stands for.</exception>
    public static string Check(string requested, bool overwrite, IEnumerable<string> protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        if (OperatingSystem.IsWindows())
        {
            RefuseWindowsRooted(requested);
        }

        if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested))
        {
            throw Refuse($"'out' must be an absolute path; '{requested}' is not.");
        }

        if (OperatingSystem.IsWindows())
        {
            RefuseWindowsSpecials(requested);
        }

        string full = Path.GetFullPath(requested);
        if (full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar) || Directory.Exists(full))
        {
            throw Refuse($"'out' names a directory ('{full}'); give a file path.");
        }

        string real = ResolveLinks(OperatingSystem.IsWindows() ? MapDrive(full, "client.read", refuseForeign: true) : full);
        if (FindWorkingTree(real) is { } tree)
        {
            throw Refuse($"'out' is inside the git working tree '{tree}'; client data must not be written into a repository.");
        }

        foreach (string root in protectedRoots)
        {
            string protectedReal = ResolveLinks(MappedRoot(Path.GetFullPath(root)));
            if (IsInside(real, protectedReal))
            {
                throw Refuse($"'out' is inside '{protectedReal}', which is read-only for Atlas.");
            }
        }

        if (File.Exists(full) && !overwrite)
        {
            throw Refuse($"'{full}' already exists; pass overwrite=true to replace it.");
        }

        return full;
    }

    /// <summary>
    /// The nearest enclosing git working tree of <paramref name="fullPath"/> (a folder holding a <c>.git</c> directory or a
    /// <c>.git</c> file, as worktrees have), or null.
    /// </summary>
    public static string? FindWorkingTree(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        for (string? directory = Path.GetDirectoryName(fullPath); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string marker = Path.Combine(directory, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>
    /// Replaces every existing symbolic link or junction along <paramref name="fullPath"/> by its final target, so a link
    /// into a repository cannot hide the repository. Components that do not exist yet are kept as given.
    /// </summary>
    public static string ResolveLinks(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string current = root;
        string[] parts = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            string next = Path.Combine(current, part);
            FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next) : File.Exists(next) ? new FileInfo(next) : null;
            if (info?.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                next = target.FullName;
            }

            current = next;
        }

        return current;
    }

    // On Windows only a path that starts with a drive letter, a colon and a separator is a plain local path. Everything else
    // that begins with a separator is a device, volume, UNC or object-manager form (backslash-backslash-dot or -question-mark,
    // backslash-question-question-backslash, a share, GLOBALROOT, a Volume GUID): fully qualified to Windows, kept as written by
    // GetFullPath, so the string-prefix test against the protected folders never matches while the file APIs write to the plain
    // location. Refused before anything else looks at the string.
    private static void RefuseWindowsRooted(string requested, string command = "client.read")
    {
        if (requested.Length > 0 && (requested[0] == Path.DirectorySeparatorChar || requested[0] == Path.AltDirectorySeparatorChar))
        {
            throw Refuse($"'out' is a device or network path ('{requested}'); give a local drive path such as D:/folder/file.", command);
        }
    }

    // After the path is known to be fully qualified: the drive-letter rule itself, NTFS alternate data streams, 8.3 short names
    // (which defeat the string-prefix test against the protected folders) and components Windows silently rewrites.
    private static void RefuseWindowsSpecials(string requested, string command = "client.read")
    {
        if (!DrivePath().IsMatch(requested))
        {
            throw Refuse($"'out' is a device or network path ('{requested}'); give a local drive path such as D:/folder/file.", command);
        }

        if (requested.IndexOf(':', 2) >= 0)
        {
            throw Refuse($"'out' contains a ':' after the drive letter (an NTFS stream or device name): '{requested}'.", command);
        }

        foreach (string part in requested.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (ShortName().IsMatch(part))
            {
                throw Refuse($"'out' contains the 8.3 short name '{part}'; spell the folder out in full.", command);
            }

            // Windows drops a trailing space or dot when it opens a path, so "repo " is the folder "repo" to the file APIs but a
            // different string to the working-tree and protected-folder checks. "." and ".." end in a dot too: an absolute out never needs them.
            if (part.Length > 0 && (part[^1] == ' ' || part[^1] == '.'))
            {
                throw Refuse($"'out' has a path component that ends in a space or a dot ('{part}'); Windows drops it, so give the folder name without it.", command);
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"^[^~]{1,6}~[0-9]+(\.[^.]{0,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortName();

    // The most drive letters followed in a chain of subst drives (subst X: Y:\dir where Y: is itself a subst drive).
    private const int MaxDriveHops = 8;

    /// <summary>
    /// Classifies the target <c>QueryDosDevice</c> returns for a drive letter. A <c>subst</c> drive returns <c>\??\D:\folder</c>; a
    /// local volume returns <c>\Device\HarddiskVolumeN</c>; a mapped network drive returns a name under <c>\Device\LanmanRedirector\</c>
    /// or <c>\Device\Mup\</c>; a <c>subst</c> to anything but a drive-letter folder (a UNC path, a volume GUID) returns another
    /// <c>\??\</c> form. Pure function of the text.
    /// </summary>
    internal static DriveTarget ParseDosDeviceTarget(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        const string objectPrefix = @"\??\";
        if (target.StartsWith(objectPrefix, StringComparison.Ordinal))
        {
            string rest = target[objectPrefix.Length..];
            if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && (rest.Length == 2 || rest[2] == '\\'))
            {
                return new DriveTarget(DriveKind.Substituted, rest.Length == 2 ? rest + '\\' : rest);
            }

            return new DriveTarget(DriveKind.Foreign, null);
        }

        foreach (string segment in target.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals("Mup", StringComparison.OrdinalIgnoreCase) || segment.EndsWith("Redirector", StringComparison.OrdinalIgnoreCase))
            {
                return new DriveTarget(DriveKind.Foreign, null);
            }
        }

        return new DriveTarget(DriveKind.Plain, null);
    }

    /// <summary>The DOS device target of drive <paramref name="letter"/> (for example <c>\Device\HarddiskVolume3</c>), or null when the letter is not defined or this is not Windows.</summary>
    internal static string? QueryDosDevice(char letter)
    {
        if (!OperatingSystem.IsWindows() || !char.IsAsciiLetter(letter))
        {
            return null;
        }

        // A subst drive can have a chain of names; the first one is the current target. 32768 chars is the longest a name can be.
        var buffer = new char[32768];
        uint length = Native.QueryDosDevice($"{letter}:", buffer, (uint)buffer.Length);
        if (length == 0)
        {
            return null;
        }

        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? (int)length : end);
    }

    // A protected folder may sit on a subst drive too; one the paths cannot follow is compared as it is.
    private static string MappedRoot(string full) => OperatingSystem.IsWindows() ? MapDrive(full, string.Empty, refuseForeign: false) : full;

    // Replaces a subst drive letter by the folder it stands for (following chains), so the working-tree and protected-folder tests
    // see where the files really land. A letter mapped to a network share or a device is refused (or, for a protected folder, left as it is).
    private static string MapDrive(string full, string command, bool refuseForeign)
    {
        for (int hop = 0; hop < MaxDriveHops; hop++)
        {
            if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':')
            {
                return full;
            }

            char letter = full[0];
            string? raw = QueryDosDevice(letter);
            DriveTarget target = raw is null ? new DriveTarget(DriveKind.Plain, null) : ParseDosDeviceTarget(raw);
            if (target.Kind == DriveKind.Plain && IsNetworkDrive(letter))
            {
                target = new DriveTarget(DriveKind.Foreign, null);
            }

            switch (target.Kind)
            {
                case DriveKind.Plain:
                    return full;
                case DriveKind.Foreign when refuseForeign:
                    throw Refuse($"'out' is a device or network path: drive {char.ToUpperInvariant(letter)}: is mapped to '{raw ?? "a network share"}'; give a local drive path such as D:/folder/file.", command);
                case DriveKind.Foreign:
                    return full;
                default:
                    full = Path.GetFullPath(Path.Combine(target.Path!, full[3..]));
                    break;
            }
        }

        return full;
    }

    private static bool IsNetworkDrive(char letter)
    {
        try
        {
            return new DriveInfo($"{letter}").DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [AuditedUnsafe("one P/Invoke of kernel32 QueryDosDeviceW (Windows only, behind OperatingSystem.IsWindows): the name is a two-character drive string, the output is a managed char array whose length is passed, the result is checked, and no handle or unmanaged memory is held")]
    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint QueryDosDevice(string lpDeviceName, [Out] char[] lpTargetPath, uint ucchMax);
    }

    private static bool IsInside(string path, string folder)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// Validates <paramref name="requested"/> as the folder a command writes derived client data into and returns its full path.
    /// The same rules as <see cref="Check"/>: absolute, outside every git working tree and outside the protected folders. The
    /// folder may exist already; a file of that name may not.
    /// </summary>
    /// <param name="command">The command's name, for the refusal message.</param>
    /// <param name="requested">The caller's <c>out</c> argument; must be absolute.</param>
    /// <param name="protectedRoots">Folders the output must not be, or be inside of (for example the client root).</param>
    /// <exception cref="CommandException">The path is relative, a file, inside a git working tree or a protected folder, or (on Windows) not a plain drive-letter path (device, volume, UNC, <c>\??\</c> and network forms, a drive mapped to a share), a stream, an 8.3 short name, or a path with a component ending in a space or dot; a <c>subst</c> drive is judged by the folder it stands for.</exception>
    public static string CheckDirectory(string command, string requested, IEnumerable<string> protectedRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        if (OperatingSystem.IsWindows())
        {
            RefuseWindowsRooted(requested, command);
        }

        if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested))
        {
            throw Refuse($"'out' must be an absolute path; '{requested}' is not.", command);
        }

        if (OperatingSystem.IsWindows())
        {
            RefuseWindowsSpecials(requested, command);
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        if (File.Exists(full))
        {
            throw Refuse($"'out' names a file ('{full}'); give a folder.", command);
        }

        // A file inside the folder stands for the folder: the working-tree walk starts at a path's parent.
        string real = ResolveLinks(OperatingSystem.IsWindows() ? MapDrive(full, command, refuseForeign: true) : full);
        if (FindWorkingTree(Path.Combine(real, "file")) is { } tree)
        {
            throw Refuse($"'out' is inside the git working tree '{tree}'; client data must not be written into a repository.", command);
        }

        foreach (string root in protectedRoots)
        {
            string protectedReal = ResolveLinks(MappedRoot(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))));
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(real, protectedReal, comparison) || IsInside(real, protectedReal))
            {
                throw Refuse($"'out' is inside '{protectedReal}', which is read-only for Atlas.", command);
            }
        }

        return full;
    }

    private static CommandException Refuse(string message, string command = "client.read") => new(CommandException.BadArguments, $"{command}: {message}");
}
