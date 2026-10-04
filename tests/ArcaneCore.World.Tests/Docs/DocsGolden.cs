using System.Text;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Golden-file check for a generated documentation page. The page is rendered from the code, compared with the
/// committed file after line endings are normalised (the repository is CRLF on Windows checkouts and LF on CI), and a
/// difference fails with the exact command that regenerates it. With <c>ARCANECORE_UPDATE_DOCS=1</c> the file is
/// rewritten instead (LF, so the committed bytes do not depend on the machine). A missing file is a failure, never
/// a skip.
/// </summary>
internal static class DocsGolden
{
    public const string UpdateVariable = "ARCANECORE_UPDATE_DOCS";

    public const string RegenerateCommand = "ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs";

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>Returns null when the committed page equals <paramref name="rendered"/>, otherwise the failure message.</summary>
    public static string? Compare(string root, string relativePath, string rendered, bool update)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string expected = Normalize(rendered);
        if (update)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected, new UTF8Encoding(false));
            return null;
        }

        if (!File.Exists(path))
        {
            return $"{relativePath} does not exist. Generate it: {RegenerateCommand}";
        }

        string actual = Normalize(File.ReadAllText(path));
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return null;
        }

        string[] a = actual.Split('\n');
        string[] e = expected.Split('\n');
        int line = 0;
        while (line < a.Length && line < e.Length && a[line] == e[line])
        {
            line++;
        }

        string committed = line < a.Length ? a[line] : "(end of file)";
        string generated = line < e.Length ? e[line] : "(end of file)";
        return $"{relativePath} is stale (first difference at line {line + 1}). Regenerate and review the diff: {RegenerateCommand}\n"
            + $"  committed: {committed}\n  generated: {generated}";
    }

    /// <summary>Asserts the committed page equals the render, honouring <c>ARCANECORE_UPDATE_DOCS</c>.</summary>
    public static void Verify(string relativePath, string rendered)
    {
        bool update = Environment.GetEnvironmentVariable(UpdateVariable) == "1";
        string? failure = Compare(RepoRoot.Find(), relativePath, rendered, update);
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure);
        }
    }
}
