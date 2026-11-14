using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Kernel.Safety;
using Microsoft.Win32.SafeHandles;

namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>How a drive letter's DOS device name (from <c>QueryDosDevice</c>) reads as text.</summary>
/// <remarks>
/// Not a security decision: where a path really leads, and on what kind of drive, is decided by opening it (see
/// <see cref="ClientOutputPath"/>). The classification only gives a network drive its own refusal message.
/// </remarks>
public enum DriveKind
{
    /// <summary>A kernel device name with no redirector in it (<c>\Device\HarddiskVolumeN</c>, <c>\Device\CdRom0</c>, a raw path such as <c>\Device\HarddiskVolumeN\folder</c>).</summary>
    Plain,

    /// <summary>A <c>subst</c> drive: the letter stands for a folder of another drive.</summary>
    Substituted,

    /// <summary>A network share or any other object-manager name (<c>\??\UNC\...</c>, a volume GUID, GLOBALROOT).</summary>
    Foreign,
}

/// <summary>A parsed <c>QueryDosDevice</c> result.</summary>
/// <param name="Kind">What the letter is.</param>
/// <param name="Path">For <see cref="DriveKind.Substituted"/>, the folder the letter stands for (with a drive letter and a separator); otherwise null.</param>
internal readonly record struct DriveTarget(DriveKind Kind, string? Path);

/// <summary>
/// Decides where Atlas may write client data and data derived from it. It must never end up in a git working tree or bare
/// repository (it is not ours to commit) or inside the client installation or another protected folder (which stay read-only),
/// whatever links, drive letters or mount points lead there.
/// </summary>
/// <remarks>
/// <para>
/// On Windows the deepest existing part of the path is opened by handle (desired access 0, every share mode, backup semantics) and
/// Windows is asked for its normalized final name, in volume GUID form and in DOS form. That resolves junctions, symbolic links,
/// <c>subst</c> and <c>DefineDosDevice</c> letters (raw targets too), mount points and short names in one step. The DOS form must be a
/// drive-letter path on a fixed, removable or RAM disk, and opening it again must reach the same volume serial number and file id
/// (a drive letter defined differently in this session than in the mount manager fails that). The rest of the path, which does not
/// exist yet, is appended as typed. The canonical path is returned and is the path the caller writes to.
/// </para>
/// <para>
/// The working-tree walk runs on every alias of the path: as typed, canonical, and through every mount-manager path of its volume
/// (transitively, refused past <see cref="MaxMountAliases"/>). Each ancestor's <c>.git</c> is probed tri-state (present refuses, an
/// error refuses, only "not found" continues), and an ancestor holding <c>HEAD</c>, <c>objects</c> and <c>refs</c> is a bare
/// repository. Protected folders are canonicalised the same way (one that cannot be refuses the command) and compared in volume GUID
/// space and by name across the aliases.
/// </para>
/// <para>
/// On Unix the path is resolved by walking it with <c>lstat</c> (every link's target spliced in and walked again, at most
/// <see cref="MaxUnixLinks"/> links, a dangling link refused), <c>/proc</c>, <c>/sys</c> and <c>/dev</c> are refused, and protected
/// folders are compared without regard to case or Unicode normalisation (a coarser fold than NFC plus case folding, because the build
/// runs in invariant globalization mode where string.Normalize does nothing; it over-refuses on a case-sensitive file system).
/// </para>
/// <para>
/// Not covered (residual): a change made after the check (narrowed by <see cref="HoldDirectory(string, string, string)"/>), a link or
/// mount elsewhere that points at the output (it cannot be enumerated), file-system filter redirection (bind links, ProjFS,
/// container layers), a protected folder reached over a loopback share, a repository without any marker (<c>GIT_DIR</c>,
/// <c>core.worktree</c>) or created after the check, Linux bind mounts and other mount aliases, and volumes unknown to the mount
/// manager (refused). Stateless and thread-safe.
/// </para>
/// </remarks>
public static partial class ClientOutputPath
{
    private const string ClientRead = "client.read";

    // The most paths a volume may be reached by (mount points of mount points); more is a loop or not worth guessing at.
    private const int MaxMountAliases = 32;

    // The most symbolic links one Unix path may pass through (Linux's own limit for a path lookup).
    private const int MaxUnixLinks = 40;

    private static readonly string[] KernelFileSystems = ["/proc", "/sys", "/dev"];

    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"];

    /// <summary>
    /// Validates <paramref name="requested"/> as the file <c>client.read</c> writes and returns the real path to write to.
    /// </summary>
    /// <param name="requested">The caller's <c>out</c> argument; must be absolute.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <param name="protectedRoots">Folders the output must not be inside of (for example the client root).</param>
    /// <returns>The canonical path (every link, drive mapping and mount point resolved, the missing part appended as typed); write to this path, not to <paramref name="requested"/>.</returns>
    /// <exception cref="CommandException">The path is relative, a directory, inside a git working tree, a bare repository or a protected folder, cannot be resolved, already exists without <paramref name="overwrite"/>, or (on Windows) not a plain drive-letter path on a local drive (device, volume, UNC, <c>\??\</c> and network forms, a drive mapped to a share, a CD drive), a stream, an 8.3 short name, a reserved device name, or a path with a component ending in a space or dot.</exception>
    public static string Check(string requested, bool overwrite, IEnumerable<string> protectedRoots)
        => Check(requested, overwrite, protectedRoots, SystemOutputPathPlatform.Instance);

    internal static string Check(string requested, bool overwrite, IEnumerable<string> protectedRoots, IOutputPathPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        ArgumentNullException.ThrowIfNull(platform);
        string full = FullPath(ClientRead, requested);
        if (full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar))
        {
            throw Refuse($"'out' names a directory ('{full}'); give a file path.");
        }

        Judged judged = Judge(ClientRead, full, isDirectory: false, protectedRoots, platform);
        if (judged.LeafExists && !overwrite)
        {
            throw Refuse($"'{judged.Path}' already exists; pass overwrite=true to replace it.");
        }

        return judged.Path;
    }

    /// <summary>
    /// Validates <paramref name="requested"/> as the folder a command writes derived client data into and returns its real path.
    /// The same rules as <see cref="Check(string, bool, IEnumerable{string})"/>. The folder may exist already; a file of that name may not.
    /// </summary>
    /// <param name="command">The command's name, for the refusal message.</param>
    /// <param name="requested">The caller's <c>out</c> argument; must be absolute.</param>
    /// <param name="protectedRoots">Folders the output must not be, or be inside of (for example the client root).</param>
    /// <returns>The canonical folder (without a trailing separator, except a drive root); write into this folder, not into <paramref name="requested"/>.</returns>
    /// <exception cref="CommandException">The path is relative, a file, inside a git working tree, a bare repository or a protected folder, cannot be resolved, or (on Windows) not a plain drive-letter path on a local drive, a stream, an 8.3 short name, a reserved device name, or a path with a component ending in a space or dot.</exception>
    public static string CheckDirectory(string command, string requested, IEnumerable<string> protectedRoots)
        => CheckDirectory(command, requested, protectedRoots, SystemOutputPathPlatform.Instance);

    internal static string CheckDirectory(string command, string requested, IEnumerable<string> protectedRoots, IOutputPathPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        ArgumentNullException.ThrowIfNull(platform);
        string full = Path.TrimEndingDirectorySeparator(FullPath(command, requested));
        return Judge(command, full, isDirectory: true, protectedRoots, platform).Path;
    }

    /// <summary>
    /// Creates <paramref name="writePath"/> (the folder an extractor writes through), checks that it is now the folder
    /// <paramref name="expectedCanonical"/> that <see cref="CheckDirectory(string, string, IEnumerable{string})"/> judged, and on Windows
    /// holds it open (list access, no delete sharing) until the result is disposed, so neither the folder nor, on NTFS, any of its
    /// parents can be renamed or deleted, and a link at <paramref name="writePath"/> cannot be replaced, while the run writes. Unix has
    /// no share modes: there it creates and checks only.
    /// </summary>
    /// <param name="command">The command's name, for the refusal message.</param>
    /// <param name="writePath">The path the extractor will write through.</param>
    /// <param name="expectedCanonical">The canonical folder the check returned for it.</param>
    /// <returns>The hold; dispose it when the run has finished writing. Owned by the caller; may be disposed on any thread.</returns>
    /// <exception cref="CommandException">The created folder leads somewhere else than <paramref name="expectedCanonical"/> (it was changed after the check) or cannot be opened.</exception>
    /// <exception cref="IOException">The folder cannot be created.</exception>
    internal static IDisposable HoldDirectory(string command, string writePath, string expectedCanonical)
        => HoldDirectory(command, writePath, expectedCanonical, SystemOutputPathPlatform.Instance);

    internal static IDisposable HoldDirectory(string command, string writePath, string expectedCanonical, IOutputPathPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(writePath);
        ArgumentNullException.ThrowIfNull(expectedCanonical);
        ArgumentNullException.ThrowIfNull(platform);
        platform.CreateDirectory(writePath);
        if (!OperatingSystem.IsWindows())
        {
            UnixCanonical now = UnixRealPath(command, "'out'", Path.GetFullPath(writePath), platform);
            if (!now.LeafIsDirectory || !string.Equals(now.Path, expectedCanonical, StringComparison.Ordinal))
            {
                throw Changed(command, writePath, now.Path, expectedCanonical);
            }

            return NoHold.Instance;
        }

        OpenedEntry target = Opened(command, writePath, platform.Open(writePath, OpenMode.Hold));
        OpenedEntry? link = null;
        try
        {
            string? now = target.DosPath is { } dos && DosDriveFinal().IsMatch(dos) ? dos[4..] : target.DosPath;
            if (!target.IsDirectory || now is null || !string.Equals(now, expectedCanonical, StringComparison.OrdinalIgnoreCase))
            {
                throw Changed(command, writePath, now ?? "a location Windows cannot name", expectedCanonical);
            }

            // A link at writePath itself is held too (it cannot be deleted or renamed while open); then the path must still reach
            // the held folder, or the link was swapped between the two opens.
            link = Opened(command, writePath, platform.Open(writePath, OpenMode.HoldNoFollow));
            OpenResult again = platform.Open(writePath, OpenMode.Follow);
            using (again.Entry)
            {
                if (again.Outcome != OpenOutcome.Opened || again.Entry!.Identity != target.Identity)
                {
                    throw Changed(command, writePath, "another folder", expectedCanonical);
                }
            }

            return new HeldFolder(target, link);
        }
        catch
        {
            link?.Dispose();
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The nearest enclosing git working tree of <paramref name="fullPath"/> (a folder holding a <c>.git</c> directory or a
    /// <c>.git</c> file, as worktrees have, or whose <c>.git</c> cannot be probed: an unknown answer counts as a repository), or null.
    /// </summary>
    public static string? FindWorkingTree(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        for (string? directory = Path.GetDirectoryName(fullPath); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (SystemOutputPathPlatform.Instance.Probe(Path.Combine(directory, ".git")).Kind != EntryKind.Missing)
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>
    /// The real location of <paramref name="fullPath"/>: on Windows the final name Windows gives its deepest existing part (links,
    /// junctions, drive mappings, mount points and short names resolved), on Unix every symbolic link resolved; the part that does not
    /// exist yet appended as given.
    /// </summary>
    /// <exception cref="IOException">The location cannot be determined (a dangling link, an unreadable component, a name Windows cannot give).</exception>
    public static string ResolveLinks(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        try
        {
            string full = Path.GetFullPath(fullPath);
            return OperatingSystem.IsWindows()
                ? CanonicalizeWindows("resolve", "the path", full, SystemOutputPathPlatform.Instance, forRoot: true).Path
                : UnixRealPath("resolve", "the path", full, SystemOutputPathPlatform.Instance).Path;
        }
        catch (CommandException e)
        {
            throw new IOException(e.Message, e);
        }
    }

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
    internal static string? QueryDosDevice(char letter) => SystemOutputPathPlatform.Instance.QueryDosDevice(letter);

    /// <summary>
    /// Resolves an absolute Unix path the way the kernel walks it: each component is looked at with <c>lstat</c>; a symbolic link's
    /// text is spliced in place of it and walked again (from <c>/</c> when absolute), so a link whose target passes through further
    /// links is followed all the way. The first missing component ends the walk; the rest is appended as given. Pure function of what
    /// <see cref="IOutputPathPlatform.Lstat"/> answers, so it also runs off Unix against a test double.
    /// </summary>
    /// <param name="command">The command's name, for the refusal message.</param>
    /// <param name="what">How the message names the path (<c>'out'</c>, the protected folder).</param>
    /// <param name="full">An absolute, normalized path.</param>
    /// <param name="platform">Where <c>lstat</c> comes from.</param>
    /// <exception cref="CommandException">A component cannot be read, more than <see cref="MaxUnixLinks"/> links are passed, a link's target does not exist, or a file is passed through.</exception>
    internal static UnixCanonical UnixRealPath(string command, string what, string full, IOutputPathPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(platform);
        var pending = new LinkedList<(string Name, bool FromLink)>();
        foreach (string part in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            pending.AddLast((part, false));
        }

        string current = "/";
        int links = 0;
        bool missing = false;
        bool leafExists = true;
        bool leafIsDirectory = true;
        while (pending.First is { } node)
        {
            pending.RemoveFirst();
            (string name, bool fromLink) = node.Value;
            if (name == ".")
            {
                continue;
            }

            if (name == "..")
            {
                // Only a link's text can hold "..": the typed path is normalized. current has no links in it, so its parent is the real one.
                current = UnixParent(current);
                continue;
            }

            string candidate = current == "/" ? "/" + name : current + "/" + name;
            if (missing)
            {
                current = candidate;
                continue;
            }

            UnixEntry entry = platform.Lstat(candidate);
            switch (entry.Kind)
            {
                case UnixEntryKind.Missing when fromLink:
                    throw Refuse($"{what} passes through a symbolic link whose target does not exist (at '{candidate}'); remove the link or give another folder.", command);
                case UnixEntryKind.Missing:
                    missing = true;
                    leafExists = false;
                    leafIsDirectory = false;
                    current = candidate;
                    break;
                case UnixEntryKind.File when pending.Count > 0:
                    throw Refuse($"{what} passes through the file '{candidate}'; give a folder.", command);
                case UnixEntryKind.File:
                    leafExists = true;
                    leafIsDirectory = false;
                    current = candidate;
                    break;
                case UnixEntryKind.Directory:
                    leafExists = true;
                    leafIsDirectory = true;
                    current = candidate;
                    break;
                case UnixEntryKind.Link:
                    if (++links > MaxUnixLinks)
                    {
                        throw Refuse($"{what} passes through more than {MaxUnixLinks} symbolic links; refusing.", command);
                    }

                    string text = entry.LinkTarget ?? string.Empty;
                    if (text.Length == 0)
                    {
                        throw Refuse($"cannot check {what}: the symbolic link '{candidate}' is empty; refusing.", command);
                    }

                    if (text[0] == '/')
                    {
                        current = "/";
                    }

                    // A link to "/" or "." resolves to a folder without adding a component.
                    leafExists = true;
                    leafIsDirectory = true;
                    string[] parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (int i = parts.Length - 1; i >= 0; i--)
                    {
                        pending.AddFirst((parts[i], true));
                    }

                    break;
                case UnixEntryKind.Error:
                default:
                    throw Refuse($"cannot check {what}: '{candidate}' cannot be read ({entry.Error ?? "unknown error"}); refusing rather than guessing where it leads.", command);
            }
        }

        return new UnixCanonical(current, leafExists, leafIsDirectory);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="folder"/> or inside it, compared without regard to case or Unicode
    /// normalisation: a case-insensitive or normalising file system (WSL <c>/mnt/c</c>, casefold ext4, APFS, vfat, CIFS) opens either
    /// spelling. The fold is coarser than NFC plus case folding (see <see cref="Fold"/>), so it over-refuses on a case-sensitive file
    /// system and between different non-ASCII names of the same shape, which is the safe side. Pure function.
    /// </summary>
    internal static bool UnixInsideOrEqual(string path, string folder)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folder);
        string p = Fold(path);
        string f = Fold(folder);
        if (f.Length > 1)
        {
            f = f.TrimEnd('/');
        }

        string prefix = f == "/" ? "/" : f + "/";
        return string.Equals(p, f, StringComparison.Ordinal) || p.StartsWith(prefix, StringComparison.Ordinal);
    }

    // Case folding and Unicode normalisation without ICU: the build runs in invariant globalization mode, where string.Normalize
    // returns its input unchanged. ASCII is upper-cased, and every maximal run of non-ASCII characters and combining marks (with the
    // ASCII letter a mark combines with) becomes one '?'; '/' is never part of a run. Any two spellings that NFC normalisation and case
    // folding make equal fold equal (a precomposed letter and its decomposed form are both one run), and so do some that they would
    // not (two different non-ASCII names of the same shape), which only over-refuses.
    private static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        bool inRun = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '/')
            {
                folded.Append('/');
                inRun = false;
                continue;
            }

            bool combinesWithNext = i + 1 < text.Length && IsCombiningMark(text[i + 1]);
            if (c <= '\u007F' && !combinesWithNext)
            {
                folded.Append(char.ToUpperInvariant(c));
                inRun = false;
                continue;
            }

            if (!inRun)
            {
                folded.Append('?');
                inRun = true;
            }
        }

        return folded.ToString();
    }

    private static bool IsCombiningMark(char c)
        => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    // The checks every form of the request goes through before anything looks at the file system.
    private static string FullPath(string command, string requested)
    {
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

        string full = Path.GetFullPath(requested);
        if (OperatingSystem.IsWindows())
        {
            // GetFullPath may make something else of a path (a device path out of a device name on older Windows): judge what it made.
            RefuseWindowsRooted(full, command);
            RefuseWindowsSpecials(full, command);
        }

        return full;
    }

    private static Judged Judge(string command, string full, bool isDirectory, IEnumerable<string> protectedRoots, IOutputPathPlatform platform)
        => OperatingSystem.IsWindows()
            ? JudgeWindows(command, full, isDirectory, protectedRoots, platform)
            : JudgeUnix(command, full, isDirectory, protectedRoots, platform);

    private static Judged JudgeWindows(string command, string full, bool isDirectory, IEnumerable<string> protectedRoots, IOutputPathPlatform platform)
    {
        char letter = char.ToUpperInvariant(full[0]);
        string device = platform.QueryDosDevice(letter)
            ?? throw Refuse($"'out' is on drive {letter}:, which QueryDosDevice cannot resolve in this session; give a path on a drive that exists.", command);
        if (ParseDosDeviceTarget(device).Kind == DriveKind.Foreign)
        {
            throw Refuse($"'out' is a device or network path: drive {letter}: is mapped to '{device}'; give a local drive path such as D:/folder/file.", command);
        }

        RequireLocalDrive(command, full, $"{letter}:\\", platform);
        WindowsCanonical canonical = CanonicalizeWindows(command, "'out'", full, platform, forRoot: false);
        RequireLeafKind(command, full, isDirectory, canonical.LeafExists, canonical.LeafIsDirectory);

        var paths = new List<string>();
        AddAndWalk(command, full, isDirectory, platform, paths, StringComparison.OrdinalIgnoreCase);
        AddAndWalk(command, canonical.Path, isDirectory, platform, paths, StringComparison.OrdinalIgnoreCase);
        var volumePaths = new List<string> { canonical.GuidPath! };
        AddMountAliases(command, canonical, isDirectory, platform, paths, volumePaths);

        foreach (string root in protectedRoots)
        {
            string rootFull = RootFullPath(command, root);
            WindowsCanonical rootCanonical = CanonicalizeWindows(command, $"the protected folder '{root}'", rootFull, platform, forRoot: true);
            string[] rootPaths = [rootFull, rootCanonical.Path];
            bool inside = (rootCanonical.GuidPath is { } rootVolumePath && volumePaths.Any(p => InsideOrEqual(p, rootVolumePath, StringComparison.OrdinalIgnoreCase)))
                || paths.Any(p => rootPaths.Any(r => InsideOrEqual(p, r, StringComparison.OrdinalIgnoreCase)));
            if (inside)
            {
                throw Refuse($"'out' is inside '{rootCanonical.Path}', which is read-only for Atlas.", command);
            }
        }

        return new Judged(canonical.Path, canonical.LeafExists, canonical.LeafIsDirectory);
    }

    private static Judged JudgeUnix(string command, string full, bool isDirectory, IEnumerable<string> protectedRoots, IOutputPathPlatform platform)
    {
        RefuseKernelFileSystem(command, full);
        UnixCanonical canonical = UnixRealPath(command, "'out'", full, platform);
        RefuseKernelFileSystem(command, canonical.Path);
        RequireLeafKind(command, full, isDirectory, canonical.LeafExists, canonical.LeafIsDirectory);

        var paths = new List<string>();
        AddAndWalk(command, full, isDirectory, platform, paths, StringComparison.Ordinal);
        AddAndWalk(command, canonical.Path, isDirectory, platform, paths, StringComparison.Ordinal);
        foreach (string root in protectedRoots)
        {
            string rootFull = RootFullPath(command, root);
            UnixCanonical rootCanonical = UnixRealPath(command, $"the protected folder '{root}'", rootFull, platform);
            if (paths.Any(p => UnixInsideOrEqual(p, rootFull) || UnixInsideOrEqual(p, rootCanonical.Path)))
            {
                throw Refuse($"'out' is inside '{rootCanonical.Path}', which is read-only for Atlas.", command);
            }
        }

        return new Judged(canonical.Path, canonical.LeafExists, canonical.LeafIsDirectory);
    }

    private static string RootFullPath(string command, string root)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Refuse($"cannot check 'out' against the protected folder '{root}': {e.Message}; refusing.", command);
        }
    }

    private static void RequireLeafKind(string command, string full, bool isDirectory, bool exists, bool existsAsDirectory)
    {
        if (exists && isDirectory && !existsAsDirectory)
        {
            throw Refuse($"'out' names a file ('{full}'); give a folder.", command);
        }

        if (exists && !isDirectory && existsAsDirectory)
        {
            throw Refuse($"'out' names a directory ('{full}'); give a file path.", command);
        }
    }

    /// <summary>
    /// Opens the deepest existing part of <paramref name="full"/> by handle and asks Windows where it really is. Only "file not found"
    /// and "path not found" move up a level; any other failure, a dangling link, a file in the middle, a name Windows cannot give, a
    /// result that is not a local drive-letter path (a share is allowed for a protected folder) or a round trip that reaches another
    /// file id refuses.
    /// </summary>
    private static WindowsCanonical CanonicalizeWindows(string command, string what, string full, IOutputPathPlatform platform, bool forRoot)
    {
        string path = Path.TrimEndingDirectorySeparator(full);
        var remainder = new List<string>();
        OpenedEntry entry;
        while (true)
        {
            OpenResult opened = platform.Open(path, OpenMode.Follow);
            if (opened.Outcome == OpenOutcome.Opened)
            {
                entry = opened.Entry!;
                break;
            }

            if (opened.Outcome == OpenOutcome.Failed)
            {
                throw Unopenable(command, what, path, opened.Failure);
            }

            // Not found when followed, yet there when the link itself is opened: a link to nothing. The parts below it would be
            // created wherever its target appears, so it is refused rather than judged.
            OpenResult link = platform.Open(path, OpenMode.NoFollow);
            if (link.Outcome == OpenOutcome.Opened)
            {
                link.Entry!.Dispose();
                throw Refuse($"{what} passes through '{path}', a link whose target does not exist; remove the link or give another folder.", command);
            }

            if (link.Outcome == OpenOutcome.Failed)
            {
                throw Unopenable(command, what, path, link.Failure);
            }

            string? parent = Path.GetDirectoryName(path);
            if (parent is null)
            {
                throw Refuse($"cannot check {what}: no part of '{full}' exists, not even its drive root; give a path on a drive that exists.", command);
            }

            remainder.Insert(0, Path.GetFileName(path));
            path = parent;
        }

        using (entry)
        {
            if (remainder.Count > 0 && !entry.IsDirectory)
            {
                throw Refuse($"{what} passes through the file '{path}'; give a folder.", command);
            }

            string dos = entry.DosPath
                ?? throw Refuse($"cannot check {what}: Windows cannot name the real location of '{path}' ({entry.NameFailure}); refusing rather than guessing.", command);
            string ancestor;
            if (DosDriveFinal().IsMatch(dos))
            {
                ancestor = dos[4..];
            }
            else if (forRoot && dos.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                ancestor = @"\\" + dos[8..];
            }
            else
            {
                throw Refuse($"{what} is a device or network path: '{full}' leads to '{dos}', which is not on a local drive; give a local drive path such as D:/folder/file.", command);
            }

            Match volume = entry.GuidPath is { } guid ? VolumeGuidRoot().Match(guid) : Match.Empty;
            if (!volume.Success && !forRoot)
            {
                throw Refuse($"cannot check {what}: Windows cannot name the volume of '{path}' ({entry.NameFailure ?? "not a volume GUID path"}); refusing rather than guessing.", command);
            }

            RefuseRewrittenCanonical(command, what, ancestor);
            if (!forRoot)
            {
                RequireLocalDrive(command, full, ancestor[..3], platform);
                RequireLocalDrive(command, full, volume.Value, platform);
            }

            // The DOS name comes from the mount manager, but a path is opened through this session's drive letters: one defined
            // differently here would send the canonical path somewhere else. Open it again and require the same file.
            OpenResult again = platform.Open(ancestor, OpenMode.Follow);
            using (again.Entry)
            {
                if (again.Outcome != OpenOutcome.Opened || again.Entry!.Identity != entry.Identity)
                {
                    string reached = again.Outcome == OpenOutcome.Opened ? "another file id" : again.Failure ?? "nothing";
                    throw Refuse($"{what} resolves to '{ancestor}', but in this session that path opens something else ({reached}): a drive letter is defined differently here than in the mount manager; refusing.", command);
                }
            }

            string tail = string.Join('\\', remainder);
            string canonical = JoinWindows(ancestor, tail);
            if (!string.Equals(Path.GetFullPath(canonical), canonical, StringComparison.Ordinal))
            {
                throw Refuse($"cannot check {what}: its real location '{canonical}' is not a path Windows keeps as written; refusing.", command);
            }

            string? volumePath = volume.Success ? JoinWindows(entry.GuidPath!, tail) : null;
            string? relative = volume.Success ? JoinRelative(entry.GuidPath![volume.Length..].TrimEnd('\\'), tail) : null;
            return new WindowsCanonical(canonical, volumePath, volume.Success ? volume.Value : null, relative, remainder.Count == 0, remainder.Count == 0 && entry.IsDirectory);
        }
    }

    /// <summary>
    /// Every other path the mount manager knows for the output's volume, transitively (a mount folder sits on a volume with mount
    /// paths of its own), each walked for a repository as it is found; refuses past <see cref="MaxMountAliases"/> volume paths.
    /// </summary>
    private static void AddMountAliases(string command, WindowsCanonical canonical, bool startAtSelf, IOutputPathPlatform platform, List<string> paths, List<string> volumePaths)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonical.GuidPath! };
        var pending = new Queue<(string Volume, string Relative)>();
        pending.Enqueue((canonical.VolumeRoot!, canonical.Relative!));
        while (pending.TryDequeue(out (string Volume, string Relative) node))
        {
            IReadOnlyList<string> mounts = platform.VolumeMountPaths(node.Volume, out string? failure)
                ?? throw Refuse($"cannot check 'out': the mount points of volume '{node.Volume}' cannot be listed ({failure}); refusing rather than checking only some of the paths that reach it.", command);
            foreach (string mount in mounts)
            {
                AddAndWalk(command, JoinWindows(mount, node.Relative), startAtSelf, platform, paths, StringComparison.OrdinalIgnoreCase);
                string folder = Path.TrimEndingDirectorySeparator(mount);
                if (Path.GetDirectoryName(folder) is null)
                {
                    // A drive letter: nothing holds it.
                    continue;
                }

                // The mount folder itself (not the volume mounted on it) and where it sits.
                OpenResult host = platform.Open(folder, OpenMode.NoFollow);
                string? hostPath;
                using (host.Entry)
                {
                    if (host.Outcome != OpenOutcome.Opened)
                    {
                        throw Refuse($"cannot check 'out': the mount point '{folder}' cannot be opened ({host.Failure ?? "not found"}); refusing rather than checking only some of the paths that reach it.", command);
                    }

                    hostPath = host.Entry!.GuidPath;
                }

                Match volume = hostPath is null ? Match.Empty : VolumeGuidRoot().Match(hostPath);
                if (!volume.Success)
                {
                    throw Refuse($"cannot check 'out': Windows cannot name the volume holding the mount point '{folder}'; refusing rather than checking only some of the paths that reach it.", command);
                }

                string next = JoinRelative(hostPath![volume.Length..].TrimEnd('\\'), node.Relative);
                string key = JoinWindows(volume.Value, next);
                if (!visited.Add(key))
                {
                    continue;
                }

                if (visited.Count > MaxMountAliases)
                {
                    throw Refuse($"'out' is reached through more than {MaxMountAliases} mount-point paths (a volume mounted inside itself?); refusing rather than checking only some of them.", command);
                }

                volumePaths.Add(key);
                pending.Enqueue((volume.Value, next));
            }
        }
    }

    private static void AddAndWalk(string command, string path, bool startAtSelf, IOutputPathPlatform platform, List<string> paths, StringComparison comparison)
    {
        if (paths.Any(p => string.Equals(p, path, comparison)))
        {
            return;
        }

        paths.Add(path);
        WalkForRepository(command, path, startAtSelf, platform);
    }

    // Each ancestor (the folder itself for a folder, the parent for a file) is probed for .git, tri-state, and for a bare repository.
    private static void WalkForRepository(string command, string path, bool startAtSelf, IOutputPathPlatform platform)
    {
        for (string? directory = startAtSelf ? path : Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string marker = Path.Combine(directory, ".git");
            EntryProbe probe = platform.Probe(marker);
            if (probe.Kind == EntryKind.Error)
            {
                throw Unreadable(command, marker, probe.Error);
            }

            if (probe.Kind != EntryKind.Missing)
            {
                throw Refuse($"'out' is inside the git working tree '{directory}'; client data must not be written into a repository.", command);
            }

            if (IsBareRepository(command, directory, platform))
            {
                throw Refuse($"'out' is inside the bare git repository '{directory}' (it holds HEAD, objects and refs); client data must not be written into a repository.", command);
            }
        }
    }

    private static bool IsBareRepository(string command, string directory, IOutputPathPlatform platform)
    {
        EntryProbe head = Probed(command, Path.Combine(directory, "HEAD"), platform);
        if (head.Kind != EntryKind.File)
        {
            return false;
        }

        return Probed(command, Path.Combine(directory, "objects"), platform).Kind == EntryKind.Directory
            && Probed(command, Path.Combine(directory, "refs"), platform).Kind == EntryKind.Directory;
    }

    private static EntryProbe Probed(string command, string path, IOutputPathPlatform platform)
    {
        EntryProbe probe = platform.Probe(path);
        return probe.Kind == EntryKind.Error ? throw Unreadable(command, path, probe.Error) : probe;
    }

    private static void RequireLocalDrive(string command, string full, string root, IOutputPathPlatform platform)
    {
        WindowsDriveType type = platform.GetDriveType(root);
        if (type is WindowsDriveType.Fixed or WindowsDriveType.Removable or WindowsDriveType.RamDisk)
        {
            return;
        }

        string kind = type switch
        {
            WindowsDriveType.Remote => "a network drive",
            WindowsDriveType.CdRom => "a CD or DVD drive",
            WindowsDriveType.NoRootDirectory => "no mounted volume",
            _ => "a drive of unknown type",
        };
        throw Refuse($"'out' is a device or network path: '{full}' is on {kind} ({root}); give a path on a local fixed, removable or RAM disk.", command);
    }

    // The canonical name of the existing part comes from Windows, not from the operator, but it is returned as a plain path: a
    // component Win32 would rewrite there (a trailing dot or space, a device name; possible on disk through \\?\ names) would make
    // the plain path mean another folder.
    private static void RefuseRewrittenCanonical(string command, string what, string ancestor)
    {
        foreach (string part in ancestor.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (part[^1] is ' ' or '.' || IsDeviceName(part))
            {
                throw Refuse($"cannot check {what}: its real location '{ancestor}' has the component '{part}', which Windows rewrites in a plain path; refusing.", command);
            }
        }
    }

    private static void RefuseKernelFileSystem(string command, string path)
    {
        foreach (string kernel in KernelFileSystems)
        {
            if (string.Equals(path, kernel, StringComparison.Ordinal) || path.StartsWith(kernel + "/", StringComparison.Ordinal))
            {
                throw Refuse($"'out' is inside '{kernel}', a kernel file system; give an ordinary folder.", command);
            }
        }
    }

    private static OpenedEntry Opened(string command, string path, OpenResult result)
        => result.Outcome == OpenOutcome.Opened
            ? result.Entry!
            : throw Refuse($"'{path}' cannot be opened after it was created ({result.Failure ?? "not found"}); refusing.", command);

    private static CommandException Changed(string command, string writePath, string now, string expected)
        => Refuse($"'{writePath}' resolves to '{now}' after it was created, not to the checked folder '{expected}' (it was changed after the check); refusing.", command);

    private static CommandException Unopenable(string command, string what, string path, string? failure)
        => Refuse($"cannot check {what}: '{path}' cannot be opened ({failure ?? "unknown error"}); refusing rather than guessing where it leads.", command);

    private static CommandException Unreadable(string command, string path, string? error)
        => Refuse($"cannot check 'out' for a git repository: '{path}' cannot be read ({error ?? "unknown error"}); refusing rather than taking it for no repository.", command);

    private static bool InsideOrEqual(string path, string folder, StringComparison comparison)
    {
        string trimmed = folder.TrimEnd('\\', '/');
        return string.Equals(path.TrimEnd('\\', '/'), trimmed, comparison) || path.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison);
    }

    private static string JoinWindows(string folder, string tail)
    {
        if (tail.Length == 0)
        {
            return folder;
        }

        return folder.EndsWith('\\') ? folder + tail : folder + '\\' + tail;
    }

    private static string JoinRelative(string first, string second)
        => first.Length == 0 ? second : second.Length == 0 ? first : first + '\\' + second;

    private static string UnixParent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    // On Windows only a path that starts with a drive letter, a colon and a separator is a plain local path. Everything else
    // that begins with a separator is a device, volume, UNC or object-manager form (backslash-backslash-dot or -question-mark,
    // backslash-question-question-backslash, a share, GLOBALROOT, a Volume GUID): fully qualified to Windows, kept as written by
    // GetFullPath, so the string-prefix test against the protected folders never matches while the file APIs write to the plain
    // location. Refused before anything else looks at the string.
    private static void RefuseWindowsRooted(string requested, string command)
    {
        if (requested.Length > 0 && (requested[0] == Path.DirectorySeparatorChar || requested[0] == Path.AltDirectorySeparatorChar))
        {
            throw Refuse($"'out' is a device or network path ('{requested}'); give a local drive path such as D:/folder/file.", command);
        }
    }

    // After the path is known to be fully qualified: the drive-letter rule itself, NTFS alternate data streams, 8.3 short names
    // (which defeat the string-prefix test against the protected folders), components Windows silently rewrites, and device names.
    private static void RefuseWindowsSpecials(string requested, string command)
    {
        if (!DrivePath().IsMatch(requested))
        {
            throw Refuse($"'out' is a device or network path ('{requested}'); give a local drive path such as D:/folder/file.", command);
        }

        if (requested.IndexOf(':', 2) >= 0)
        {
            throw Refuse($"'out' contains a ':' after the drive letter (an NTFS stream or device name): '{requested}'.", command);
        }

        foreach (string part in requested.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Skip(1))
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

            if (IsDeviceName(part))
            {
                throw Refuse($"'out' contains the reserved device name '{part}'; Windows opens a device there instead of a folder, so give an ordinary name.", command);
            }
        }
    }

    // CON, PRN, AUX, NUL, CONIN$, CONOUT$, COM0-9 and LPT0-9 (and COM/LPT with a superscript 1-3), with or without an extension, any case.
    private static bool IsDeviceName(string part)
    {
        int dot = part.IndexOf('.', StringComparison.Ordinal);
        string stem = (dot >= 0 ? part[..dot] : part).TrimEnd(' ');
        foreach (string device in DeviceNames)
        {
            if (string.Equals(stem, device, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is (>= '0' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3';
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"^[^~]{1,6}~[0-9]+(\.[^.]{0,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortName();

    // What GetFinalPathNameByHandle gives for a drive-letter location: \\?\X:\...
    [GeneratedRegex(@"^\\\\\?\\[A-Za-z]:\\", RegexOptions.CultureInvariant)]
    private static partial Regex DosDriveFinal();

    // The volume part of a VOLUME_NAME_GUID final path: \\?\Volume{GUID}\
    [GeneratedRegex(@"^\\\\\?\\Volume\{[0-9A-Fa-f-]+\}\\", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeGuidRoot();

    private static CommandException Refuse(string message, string command = ClientRead) => new(CommandException.BadArguments, $"{command}: {message}");

    /// <summary>The checked path and what is at it now.</summary>
    private readonly record struct Judged(string Path, bool LeafExists, bool LeafIsDirectory);

    /// <summary>A Windows path resolved by handle.</summary>
    /// <param name="Path">The plain drive-letter path (or, for a protected folder, a UNC path).</param>
    /// <param name="GuidPath">The same location in volume GUID form, or null (a share).</param>
    /// <param name="VolumeRoot">The volume part of <paramref name="GuidPath"/> (<c>\\?\Volume{...}\</c>), or null.</param>
    /// <param name="Relative">The rest of <paramref name="GuidPath"/> without leading or trailing separators, or null.</param>
    /// <param name="LeafExists">Whether the whole path exists.</param>
    /// <param name="LeafIsDirectory">Whether it exists and is a directory.</param>
    private readonly record struct WindowsCanonical(string Path, string? GuidPath, string? VolumeRoot, string? Relative, bool LeafExists, bool LeafIsDirectory);
}

/// <summary>The result of <see cref="ClientOutputPath.UnixRealPath"/>.</summary>
/// <param name="Path">The resolved absolute path.</param>
/// <param name="LeafExists">Whether the last component exists.</param>
/// <param name="LeafIsDirectory">Whether the last component exists and is a directory.</param>
internal readonly record struct UnixCanonical(string Path, bool LeafExists, bool LeafIsDirectory);

/// <summary>What a path names, as a tri-state existence probe sees it.</summary>
internal enum EntryKind
{
    /// <summary>Nothing of that name (or a parent folder is missing).</summary>
    Missing,

    /// <summary>A file (or a link that is not a directory).</summary>
    File,

    /// <summary>A directory (or a link to one).</summary>
    Directory,

    /// <summary>The probe failed (access denied, device not ready and the like): the answer is unknown.</summary>
    Error,
}

/// <summary>One tri-state probe result.</summary>
/// <param name="Kind">What the path names.</param>
/// <param name="Error">For <see cref="EntryKind.Error"/>, why.</param>
internal readonly record struct EntryProbe(EntryKind Kind, string? Error);

/// <summary>What <c>lstat</c> sees at a path (the link itself, not what it points to).</summary>
internal enum UnixEntryKind
{
    /// <summary>Nothing of that name.</summary>
    Missing,

    /// <summary>A file or anything else that is not a directory or a link.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A symbolic link (<see cref="UnixEntry.LinkTarget"/> holds its text).</summary>
    Link,

    /// <summary>The query failed.</summary>
    Error,
}

/// <summary>One <c>lstat</c>-style result.</summary>
/// <param name="Kind">What the path is.</param>
/// <param name="LinkTarget">For <see cref="UnixEntryKind.Link"/>, the link's text as stored (absolute or relative).</param>
/// <param name="Error">For <see cref="UnixEntryKind.Error"/>, why.</param>
internal readonly record struct UnixEntry(UnixEntryKind Kind, string? LinkTarget, string? Error);

/// <summary>The Win32 <c>GetDriveType</c> answers.</summary>
internal enum WindowsDriveType : uint
{
    /// <summary>DRIVE_UNKNOWN.</summary>
    Unknown = 0,

    /// <summary>DRIVE_NO_ROOT_DIR: nothing is mounted there.</summary>
    NoRootDirectory = 1,

    /// <summary>DRIVE_REMOVABLE.</summary>
    Removable = 2,

    /// <summary>DRIVE_FIXED.</summary>
    Fixed = 3,

    /// <summary>DRIVE_REMOTE: a network drive.</summary>
    Remote = 4,

    /// <summary>DRIVE_CDROM.</summary>
    CdRom = 5,

    /// <summary>DRIVE_RAMDISK.</summary>
    RamDisk = 6,
}

/// <summary>How <see cref="IOutputPathPlatform.Open"/> opens a path.</summary>
internal enum OpenMode
{
    /// <summary>Follow every link; no access, every share mode (the handle blocks nothing).</summary>
    Follow,

    /// <summary>Follow links in the parents but open a link at the last component itself; no access, every share mode.</summary>
    NoFollow,

    /// <summary>Follow every link; list access without delete sharing, so the folder (and on NTFS its parents) cannot be renamed or deleted while the handle is open.</summary>
    Hold,

    /// <summary>As <see cref="Hold"/>, but a link at the last component is opened (and so held) itself.</summary>
    HoldNoFollow,
}

/// <summary>What <see cref="IOutputPathPlatform.Open"/> did.</summary>
internal enum OpenOutcome
{
    /// <summary>The entry is open; <see cref="OpenResult.Entry"/> holds it.</summary>
    Opened,

    /// <summary>ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND: nothing of that name.</summary>
    NotFound,

    /// <summary>Any other failure; <see cref="OpenResult.Failure"/> says which.</summary>
    Failed,
}

/// <summary>The identity of an open file or folder: the volume serial number and the 128-bit file id (FILE_ID_INFO).</summary>
/// <param name="VolumeSerial">The volume serial number.</param>
/// <param name="IdLow">The low 64 bits of the file id.</param>
/// <param name="IdHigh">The high 64 bits of the file id (zero when only the 64-bit index was available).</param>
internal readonly record struct FileIdentity(ulong VolumeSerial, ulong IdLow, ulong IdHigh);

/// <summary>The result of <see cref="IOutputPathPlatform.Open"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Entry">For <see cref="OpenOutcome.Opened"/>, the open entry; the caller owns and disposes it.</param>
/// <param name="Failure">For <see cref="OpenOutcome.Failed"/>, why.</param>
internal readonly record struct OpenResult(OpenOutcome Outcome, OpenedEntry? Entry, string? Failure);

/// <summary>
/// A file or folder opened by handle, with the names Windows gives its final location. Owns the handle (null for test doubles) and
/// closes it on <see cref="Dispose"/>; not thread-safe, used by one caller at a time.
/// </summary>
internal sealed class OpenedEntry : IDisposable
{
    private readonly SafeHandle? _handle;

    /// <summary>Wraps an open handle (or none, for test doubles) and what was read from it.</summary>
    /// <param name="handle">The handle, owned from here on; null for a test double.</param>
    /// <param name="isDirectory">Whether the entry is a directory.</param>
    /// <param name="identity">Its volume serial number and file id.</param>
    /// <param name="guidPath">The normalized final path in volume GUID form (<c>\\?\Volume{...}\...</c>), or null when Windows could not give it.</param>
    /// <param name="dosPath">The normalized final path in DOS form (<c>\\?\X:\...</c> or <c>\\?\UNC\...</c>), or null when Windows could not give it.</param>
    /// <param name="nameFailure">Why a final path is missing, or null.</param>
    public OpenedEntry(SafeHandle? handle, bool isDirectory, FileIdentity identity, string? guidPath, string? dosPath, string? nameFailure)
    {
        _handle = handle;
        IsDirectory = isDirectory;
        Identity = identity;
        GuidPath = guidPath;
        DosPath = dosPath;
        NameFailure = nameFailure;
    }

    /// <summary>Whether the entry is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>The volume serial number and file id.</summary>
    public FileIdentity Identity { get; }

    /// <summary>The final path in volume GUID form, or null.</summary>
    public string? GuidPath { get; }

    /// <summary>The final path in DOS form, or null.</summary>
    public string? DosPath { get; }

    /// <summary>Why a final path is missing, or null.</summary>
    public string? NameFailure { get; }

    /// <inheritdoc/>
    public void Dispose() => _handle?.Dispose();
}

/// <summary>
/// The handles <see cref="ClientOutputPath.HoldDirectory(string, string, string)"/> keeps open: the folder written into and the entry at
/// the path written through (the same folder, or a link to it). Owns both and closes both on <see cref="Dispose"/>; not thread-safe.
/// </summary>
internal sealed class HeldFolder : IDisposable
{
    private readonly OpenedEntry _target;
    private readonly OpenedEntry _link;

    /// <summary>Takes ownership of both entries.</summary>
    public HeldFolder(OpenedEntry target, OpenedEntry link)
    {
        _target = target;
        _link = link;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _link.Dispose();
        _target.Dispose();
    }
}

/// <summary>A disposable that holds nothing (Unix has no share modes to hold a folder with).</summary>
internal sealed class NoHold : IDisposable
{
    /// <summary>The one instance.</summary>
    public static readonly NoHold Instance = new();

    private NoHold()
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing is held.
    }
}

/// <summary>
/// The file-system and Windows queries <see cref="ClientOutputPath"/> makes, behind one seam so the refusal cases can be tested
/// without administrator rights and without redefining a real drive letter. Implementations must be thread-safe.
/// </summary>
internal interface IOutputPathPlatform
{
    /// <summary>The DOS device target of a drive letter (Windows), or null when the query fails.</summary>
    string? QueryDosDevice(char letter);

    /// <summary>The Win32 drive type of a root (<c>X:\</c> or <c>\\?\Volume{...}\</c>).</summary>
    WindowsDriveType GetDriveType(string root);

    /// <summary>Opens <paramref name="path"/> by handle (Windows).</summary>
    OpenResult Open(string path, OpenMode mode);

    /// <summary>The mount-manager paths (<c>X:\</c>, <c>X:\folder\</c>) of a volume given as <c>\\?\Volume{...}\</c> (Windows), or null with <paramref name="failure"/> set.</summary>
    IReadOnlyList<string>? VolumeMountPaths(string volumeRoot, out string? failure);

    /// <summary>Tri-state existence probe of <paramref name="path"/> (a link counts as present; it is not followed at the last component on Windows).</summary>
    EntryProbe Probe(string path);

    /// <summary>What <c>lstat</c> sees at <paramref name="path"/> (Unix).</summary>
    UnixEntry Lstat(string path);

    /// <summary>Creates a folder and any missing parents.</summary>
    void CreateDirectory(string path);
}

/// <summary>The real <see cref="IOutputPathPlatform"/>: Win32 by P/Invoke on Windows, managed APIs elsewhere. Stateless and thread-safe.</summary>
internal sealed class SystemOutputPathPlatform : IOutputPathPlatform
{
    /// <summary>The one instance.</summary>
    public static readonly SystemOutputPathPlatform Instance = new();

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorMoreData = 234;
    private const uint FileListDirectory = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;
    private const uint VolumeNameGuid = 0x1;

    private SystemOutputPathPlatform()
    {
    }

    /// <inheritdoc/>
    public string? QueryDosDevice(char letter)
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

    /// <inheritdoc/>
    public WindowsDriveType GetDriveType(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return OperatingSystem.IsWindows() ? (WindowsDriveType)Native.GetDriveType(root) : WindowsDriveType.Unknown;
    }

    /// <inheritdoc/>
    public OpenResult Open(string path, OpenMode mode)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Opening a path by handle to name its final location is Windows-only here.");
        }

        bool hold = mode is OpenMode.Hold or OpenMode.HoldNoFollow;
        uint access = hold ? FileListDirectory | FileReadAttributes : 0;
        uint share = hold ? FileShareRead | FileShareWrite : FileShareRead | FileShareWrite | FileShareDelete;
        uint flags = FileFlagBackupSemantics | (mode is OpenMode.NoFollow or OpenMode.HoldNoFollow ? FileFlagOpenReparsePoint : 0);
        SafeFileHandle handle = Native.CreateFile(LongForm(path), access, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error is ErrorFileNotFound or ErrorPathNotFound
                ? new OpenResult(OpenOutcome.NotFound, null, null)
                : new OpenResult(OpenOutcome.Failed, null, Describe(error));
        }

        try
        {
            if (!Native.GetAttributeTag(handle, out uint attributes))
            {
                return Fail(handle, "its attributes cannot be read", Marshal.GetLastPInvokeError());
            }

            if (!Native.GetIdentity(handle, out FileIdentity identity))
            {
                return Fail(handle, "its file id cannot be read", Marshal.GetLastPInvokeError());
            }

            string? guid = FinalPath(handle, VolumeNameGuid, out int guidError);
            string? dos = FinalPath(handle, VolumeNameDos, out int dosError);
            string? nameFailure = guid is null ? $"volume GUID name: {Describe(guidError)}" : dos is null ? $"DOS name: {Describe(dosError)}" : null;
            return new OpenResult(OpenOutcome.Opened, new OpenedEntry(handle, (attributes & FileAttributeDirectory) != 0, identity, guid, dos, nameFailure), null);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string>? VolumeMountPaths(string volumeRoot, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(volumeRoot);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Volume mount points are Windows-only here.");
        }

        var buffer = new char[1024];
        while (true)
        {
            if (Native.GetVolumePathNamesForVolumeName(volumeRoot, buffer, (uint)buffer.Length, out uint length))
            {
                var paths = new List<string>();
                int start = 0;
                for (int i = 0; i < Math.Min((int)length, buffer.Length); i++)
                {
                    if (buffer[i] == '\0')
                    {
                        if (i > start)
                        {
                            paths.Add(new string(buffer, start, i - start));
                        }

                        start = i + 1;
                    }
                }

                failure = null;
                return paths;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorMoreData || length <= buffer.Length || length > 1 << 20)
            {
                failure = Describe(error);
                return null;
            }

            buffer = new char[length];
        }
    }

    /// <inheritdoc/>
    public EntryProbe Probe(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return new EntryProbe((attributes & FileAttributes.Directory) != 0 ? EntryKind.Directory : EntryKind.File, null);
        }
        catch (FileNotFoundException)
        {
            return new EntryProbe(EntryKind.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new EntryProbe(EntryKind.Missing, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new EntryProbe(EntryKind.Error, e.Message);
        }
    }

    /// <inheritdoc/>
    public UnixEntry Lstat(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            // readlink first: it answers for a link whose target is missing too, where the attribute query may report "not found".
            if (new FileInfo(path).LinkTarget is { Length: > 0 } target)
            {
                return new UnixEntry(UnixEntryKind.Link, target, null);
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new UnixEntry(UnixEntryKind.Error, null, "a reparse point that is not a symbolic link");
            }

            return new UnixEntry((attributes & FileAttributes.Directory) != 0 ? UnixEntryKind.Directory : UnixEntryKind.File, null, null);
        }
        catch (FileNotFoundException)
        {
            return new UnixEntry(UnixEntryKind.Missing, null, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new UnixEntry(UnixEntryKind.Missing, null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new UnixEntry(UnixEntryKind.Error, null, e.Message);
        }
    }

    /// <inheritdoc/>
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    private static OpenResult Fail(SafeFileHandle handle, string what, int error)
    {
        handle.Dispose();
        return new OpenResult(OpenOutcome.Failed, null, $"{what}: {Describe(error)}");
    }

    // CreateFileW takes a plain path up to MAX_PATH; longer ones (already full and checked, so the prefix changes nothing else) get \\?\, as .NET does.
    private static string LongForm(string path)
    {
        if (path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    private static string? FinalPath(SafeFileHandle handle, uint volumeForm, out int error)
    {
        var buffer = new char[512];
        while (true)
        {
            uint length = Native.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FileNameNormalized | volumeForm);
            if (length == 0)
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            if (length < buffer.Length)
            {
                error = 0;
                return new string(buffer, 0, (int)length);
            }

            // Too small: the return value is the size needed, terminator included.
            buffer = new char[length + 1];
        }
    }

    private static string Describe(int error) => $"Windows error {error}: {new System.ComponentModel.Win32Exception(error).Message}";

    [AuditedUnsafe("kernel32 P/Invokes of the output-folder guard (Windows only; every caller is behind OperatingSystem.IsWindows): QueryDosDeviceW, GetDriveTypeW and GetVolumePathNamesForVolumeNameW fill managed char arrays whose length is passed and whose result is checked; CreateFileW returns a SafeFileHandle owned and disposed by the caller (desired access 0 or list-directory, never write); GetFinalPathNameByHandleW fills a managed char array, regrown to the size it reports; GetFileInformationByHandleEx and GetFileInformationByHandle fill blittable structs passed by out with their Marshal.SizeOf size; no unmanaged memory is allocated or held")]
    private static class Native
    {
        private const int FileAttributeTagInfoClass = 9;
        private const int FileIdInfoClass = 18;

        [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint QueryDosDevice(string lpDeviceName, [Out] char[] lpTargetPath, uint ucchMax);

        [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetDriveType(string lpRootPathName);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumePathNamesForVolumeName(string lpszVolumeName, [Out] char[] lpszVolumePathNames, uint cchBufferLength, out uint lpcchReturnLength);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileIdInfo(SafeFileHandle hFile, int fileInformationClass, out FileIdInfo lpFileInformation, uint dwBufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetAttributeTagInfo(SafeFileHandle hFile, int fileInformationClass, out FileAttributeTagInfo lpFileInformation, uint dwBufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

        /// <summary>The entry's attributes (FILE_ATTRIBUTE_TAG_INFO; readable with desired access 0).</summary>
        internal static bool GetAttributeTag(SafeFileHandle handle, out uint attributes)
        {
            bool ok = GetAttributeTagInfo(handle, FileAttributeTagInfoClass, out FileAttributeTagInfo info, (uint)Marshal.SizeOf<FileAttributeTagInfo>());
            attributes = info.FileAttributes;
            return ok;
        }

        /// <summary>The 128-bit FILE_ID_INFO identity, or the 64-bit index of BY_HANDLE_FILE_INFORMATION where a file system has no FILE_ID_INFO.</summary>
        internal static bool GetIdentity(SafeFileHandle handle, out FileIdentity identity)
        {
            if (GetFileIdInfo(handle, FileIdInfoClass, out FileIdInfo id, (uint)Marshal.SizeOf<FileIdInfo>()))
            {
                identity = new FileIdentity(id.VolumeSerialNumber, id.FileIdLow, id.FileIdHigh);
                return true;
            }

            if (GetFileInformationByHandle(handle, out ByHandleFileInformation info))
            {
                identity = new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, 0);
                return true;
            }

            identity = default;
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfo
        {
            public ulong VolumeSerialNumber;
            public ulong FileIdLow;
            public ulong FileIdHigh;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
