using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Game.Tests.Hygiene;

/// <summary>One file the hygiene guard refuses, with the rule that fired.</summary>
internal sealed record HygieneViolation(string Path, string Rule);

internal static class RepoHygieneScanner
{
    /// <summary>Walks up from the test binary to the directory holding <c>ArcaneCore.slnx</c>
    /// (override with <c>ARCANECORE_HYGIENE_ROOT</c>, used to point the guard at a scratch copy).</summary>
    public static string FindRepoRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable("ARCANECORE_HYGIENE_ROOT");
        if (!string.IsNullOrEmpty(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ArcaneCore.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ArcaneCore.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Repo-relative, '/'-separated paths of every file git would track or show as untracked
    /// (so .gitignore'd local data is excluded exactly as git excludes it). Without a <c>.git</c> entry
    /// (a scratch copy) it walks the tree, skipping build output and the ignored local directories.</summary>
    public static IReadOnlyList<string> EnumerateRepoFiles(string root)
    {
        if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
        {
            return GitListFiles(root);
        }

        return WalkFiles(root);
    }

    /// <summary>Client-data and extraction-output extensions that never belong in the repo.</summary>
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dbc", ".mpq", ".map", ".vmtree", ".vmtile", ".vmo", ".mmtile", ".mmap", ".adt", ".wdt", ".wmo", ".m2", ".blp",
    };

    /// <summary>Directories reserved for local (never committed) client/world data, at the repo root.</summary>
    private static readonly string[] LocalDataRoots = ["data/", "local-data/"];

    /// <summary>Largest .sql file accepted outside <c>tests/**/Baselines/</c>; world dumps are megabytes.</summary>
    private const long MaxSqlBytes = 64 * 1024;

    private const int HeaderProbeBytes = 4096;

    /// <summary>File magics, taken from the production readers so the guard cannot drift from them:
    /// WDBC (DbcFile.Magic, 0x43424457 LE), MAPS (TerrainTile.MapMagic), VMAP_7.0 (VMapFormat.Magic),
    /// MMAP (NavMeshFormat.MmapMagic LE). WDBC is the only literal: DbcFile lives in ArcaneCore.Data,
    /// which this project does not reference.</summary>
    private static readonly (string Name, byte[] Bytes)[] Magics =
    [
        ("WDBC (client DBC)", "WDBC"u8.ToArray()),
        ("MAPS (extracted terrain tile)", LittleEndian(TerrainTile.MapMagic)),
        ("VMAP_7.0 (extracted vmap data)", Encoding.ASCII.GetBytes(VMapFormat.Magic)),
        ("MMAP (extracted navmesh tile)", LittleEndian(NavMeshFormat.MmapMagic)),
    ];

    private static readonly string[] SqlDumpBanners = ["-- MySQL dump", "-- MariaDB dump", "-- Host:", "LOCK TABLES `"];

    /// <summary>Applies every rule to each repo-relative path (files read from <paramref name="root"/>).</summary>
    public static IReadOnlyList<HygieneViolation> Scan(string root, IEnumerable<string> relativePaths)
    {
        List<HygieneViolation> violations = [];
        foreach (string path in relativePaths)
        {
            string normalized = path.Replace('\\', '/');
            foreach (string rule in Check(root, normalized))
            {
                violations.Add(new HygieneViolation(normalized, rule));
            }
        }

        return violations;
    }

    private static IEnumerable<string> Check(string root, string path)
    {
        foreach (string prefix in LocalDataRoots)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return $"under reserved local data directory /{prefix}";
            }
        }

        string extension = Path.GetExtension(path);
        if (ForbiddenExtensions.Contains(extension))
        {
            yield return $"forbidden client-data extension {extension}";
        }

        string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        byte[] probe = ReadProbe(full);
        foreach ((string name, byte[] bytes) in Magics)
        {
            if (probe.AsSpan().StartsWith(bytes))
            {
                yield return $"{name} magic header";
            }
        }

        if (extension.Equals(".sql", StringComparison.OrdinalIgnoreCase))
        {
            string text = Encoding.UTF8.GetString(probe);
            if (SqlDumpBanners.Any(b => text.Contains(b, StringComparison.Ordinal)))
            {
                yield return "SQL dump banner";
            }

            if (!IsBaseline(path) && new FileInfo(full).Length > MaxSqlBytes)
            {
                yield return $"SQL file larger than {MaxSqlBytes} bytes outside tests/**/Baselines/";
            }
        }
    }

    /// <summary><c>tests/**/Baselines/*.sql</c>: synthetic frozen schema baselines (directly in a Baselines directory).</summary>
    private static bool IsBaseline(string path)
    {
        string[] parts = path.Split('/');
        return parts.Length >= 3
            && parts[0].Equals("tests", StringComparison.OrdinalIgnoreCase)
            && parts[^2].Equals("Baselines", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReadProbe(string full)
    {
        using FileStream stream = File.OpenRead(full);
        byte[] buffer = new byte[HeaderProbeBytes];
        int read = stream.Read(buffer, 0, buffer.Length);
        return buffer.AsSpan(0, read).ToArray();
    }

    private static byte[] LittleEndian(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static List<string> GitListFiles(string root)
    {
        ProcessStartInfo psi = new("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git ls-files failed ({process.ExitCode}): {stderr.Result}");
        }

        // A path deleted in the working tree but still in the index is listed by --cached; skip it.
        return stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => File.Exists(Path.Combine(root, p)))
            .ToList();
    }

    private static readonly HashSet<string> WalkSkippedDirectories =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "artifacts", ".vs", ".idea", ".vscode", "refs" };

    private static List<string> WalkFiles(string root)
    {
        List<string> files = [];
        Walk(root, string.Empty, files);
        return files;
    }

    private static void Walk(string root, string relativeDir, List<string> files)
    {
        string full = relativeDir.Length == 0 ? root : Path.Combine(root, relativeDir);
        foreach (string file in Directory.EnumerateFiles(full))
        {
            files.Add(Join(relativeDir, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.EnumerateDirectories(full))
        {
            string name = Path.GetFileName(dir);
            if (WalkSkippedDirectories.Contains(name) || (relativeDir.Length == 0 && IsLocalDataDirectory(name)))
            {
                continue;
            }

            Walk(root, Join(relativeDir, name), files);
        }
    }

    private static bool IsLocalDataDirectory(string name) => name is "data" or "local-data";

    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;
}
