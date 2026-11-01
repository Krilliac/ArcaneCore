using System.Text.RegularExpressions;
using ArcaneCore.World.Tests.Docs;
using Xunit;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>
/// The characters write fence (<c>CharacterDatabaseWriterFence</c>) sees a connection only when it is opened through EF. A raw <c>DbConnection.Open()</c> / <c>OpenAsync(</c> in the data layer
/// would bypass it, so none may appear outside the places that are meant to: schema and content import (before the world runs), the SQLite checkpointer, the advisory writer lock (its own raw
/// connection, which the fence must never touch) and <c>DbConnectionOpen</c> itself.
/// </summary>
public sealed class DataConnectionOpenFenceTests
{
    private static readonly Regex Open = new(@"\.Open\(\)|\.OpenAsync\(", RegexOptions.Compiled);

    private static readonly string[] AllowedFiles =
    [
        "Resilience/SqliteCheckpointService.cs",
        "Characters/Ownership/ServerAdvisoryWriterLock.cs",
        "DbConnectionOpen.cs",
    ];

    private static readonly string[] AllowedFolders = ["Schema/", "Content/Import/"];

    [Fact]
    public void TheDataLayer_OpensNoRawConnection_OutsideTheAllowedPlaces()
    {
        string data = Path.Combine(RepoRoot.Find(), "src", "ArcaneCore.Data");
        Assert.True(Directory.Exists(data), data);
        var hits = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.EnumerateFiles(data, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(data, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)) continue;
            scanned++;
            if (AllowedFiles.Contains(relative, StringComparer.Ordinal) || AllowedFolders.Any(f => relative.StartsWith(f, StringComparison.Ordinal))) continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (Open.IsMatch(line)) hits.Add($"{relative}:{i + 1}: {line}");
            }
        }

        Assert.True(scanned > 100, $"scanned only {scanned} files under src/ArcaneCore.Data");
        Assert.True(hits.Count == 0, "a raw connection open in the data layer bypasses the characters write fence; open it through DbConnectionOpen.EnsureOpenAsync(DatabaseFacade) or add the place to this test:\n" + string.Join('\n', hits));
    }
}
