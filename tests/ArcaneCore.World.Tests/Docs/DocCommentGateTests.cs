using System.Text.RegularExpressions;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// The doc-comment gate (Directory.Build.props): with the documentation file generated, a cref that names nothing (CS1574), badly
/// formed doc XML (CS1570) or a <c>paramref</c> naming a gone parameter (CS1734) is a build error under TreatWarningsAsErrors. These
/// tests scan, as text, for the settings that would turn that off: in the projects of ArcaneCore.slnx (tests/ThirdParty excepted)
/// and every .props/.targets at the root or under src, tests and tools, a GenerateDocumentationFile or TreatWarningsAsErrors element
/// set false, CopyDocumentationFileToOutputDirectory set true, WarningLevel 0, or a doc code in a NoWarn or WarningsNotAsErrors
/// element (attributes allowed, CS-prefixed or bare number); a doc-code <c>dotnet_diagnostic</c> severity in a root or nested
/// .editorconfig/.globalconfig; a <c>#pragma warning disable</c> of a doc code, or of everything, in a .cs file under src, tests or
/// tools; and a property, nowarn or warnaserror- switch on a command line of the CI workflows, the Dockerfile, a .sh/.ps1 script
/// under tools or a root .rsp file. MSBuild evaluation is not run, so a property set some other way (an environment variable, an
/// imported file outside these roots) is not seen. One more test checks this test assembly's own build: the documentation file in
/// obj holds a member only this class documents, and the output folder holds no ArcaneCore .xml.
/// </summary>
public sealed class DocCommentGateTests
{
    private static readonly string[] Noise = ["CS1573", "CS1591"];

    /// <summary>
    /// The compiler's documentation-comment codes (CS1570-CS1599, CS1710-CS1739, written with or without the CS prefix) among the
    /// <c>;</c>, <c>,</c> or space separated tokens of a warning list, other than the two noise codes the root props silences.
    /// </summary>
    private static IEnumerable<string> DocCodesIn(string list) => list.Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(token => Regex.Match(token.Trim(), @"^(?:CS)?(15[7-9]\d|17[1-3]\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .Where(m => m.Success).Select(m => "CS" + m.Groups[1].Value).Where(code => !Noise.Contains(code));

    [Fact]
    public void TheRootProps_GeneratesDocs_KeepsThemInObj_AndSilencesOnlyTheNoiseCodes()
    {
        string props = RepoRoot.ReadText("Directory.Build.props");
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", props, StringComparison.Ordinal);
        Assert.Contains("<CopyDocumentationFileToOutputDirectory>false</CopyDocumentationFileToOutputDirectory>", props, StringComparison.Ordinal);
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", props, StringComparison.Ordinal);
        MatchCollection noWarn = Regex.Matches(props, @"<NoWarn(\s[^>]*)?>([^<]*)</NoWarn>", RegexOptions.CultureInvariant);
        Match only = Assert.Single(noWarn);
        Assert.True(only.Groups[1].Value.Length == 0, "the root NoWarn must apply unconditionally: " + only.Value);
        string[] silenced = [.. only.Groups[2].Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(code => code != "$(NoWarn)").Order(StringComparer.Ordinal)];
        string[] expected = ["CS0419", "CS1573", "CS1591"];
        Assert.Equal(expected, silenced);
        Assert.DoesNotContain("WarningsNotAsErrors", props, StringComparison.Ordinal);
    }

    [Fact]
    public void NoProjectEditorconfigOrSource_TurnsTheGateOff()
    {
        string root = RepoRoot.Find();
        string[] projects = [.. Regex.Matches(RepoRoot.ReadText("ArcaneCore.slnx"), @"Path=""([^""]+\.csproj)""").Select(m => m.Groups[1].Value)
            .Where(path => !path.StartsWith("tests/ThirdParty/", StringComparison.Ordinal))];
        var found = new List<string>();
        foreach (string project in projects)
            found.AddRange(Silencers(project, RepoRoot.ReadText(project)).Select(s => $"{project}: {s}"));

        int configs = 0;
        int sources = 0;
        int scripts = 0;
        foreach (string path in ((string[])["*.editorconfig", "*.globalconfig", "*.props", "*.targets", "*.rsp"]).SelectMany(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
            .Concat(((string[])["src", "tests", "tools"]).SelectMany(top => Directory.EnumerateFiles(Path.Combine(root, top), "*", SearchOption.AllDirectories))))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("tests/ThirdParty/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                continue;
            string extension = Path.GetExtension(path);
            if (extension is ".editorconfig" or ".globalconfig" || Path.GetFileName(path) == ".editorconfig")
                configs++;
            else if (extension == ".cs")
                sources++;
            else if (extension is ".sh" or ".ps1" or ".rsp")
            {
                scripts++;
                found.AddRange(BuildCommandSilencers(File.ReadAllText(path)).Select(s => $"{relative}: {s}"));
                continue;
            }
            else if (extension is not (".props" or ".targets"))
                continue;
            found.AddRange(Silencers(relative, File.ReadAllText(path)).Select(s => $"{relative}: {s}"));
        }

        foreach (string build in Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml").Append(Path.Combine(root, "apps", "docker", "Dockerfile")))
        {
            string relative = Path.GetRelativePath(root, build).Replace('\\', '/');
            found.AddRange(BuildCommandSilencers(File.ReadAllText(build)).Select(s => $"{relative}: {s}"));
        }

        Assert.True(projects.Length > 20, $"found only {projects.Length} projects in ArcaneCore.slnx");
        Assert.True(configs >= 1, "found no .editorconfig at the repository root");
        Assert.True(sources > 1000, $"scanned only {sources} source files under src/, tests/ and tools/");
        Assert.True(scripts > 5, $"scanned only {scripts} scripts under tools/");
        Assert.True(found.Count == 0, "the doc-comment gate is turned off or a doc code silenced: " + string.Join("; ", found));
    }

    [Fact]
    public void TheScan_FindsEveryWayToSilenceTheGate_AndPassesTheRepositorysOwnSettings()
    {
        Assert.Single(Silencers("a.csproj", "<GenerateDocumentationFile>false</GenerateDocumentationFile>"));
        Assert.Single(Silencers("a.csproj", "<GenerateDocumentationFile Condition=\"'$(CI)' == ''\">false</GenerateDocumentationFile>"));
        Assert.Single(Silencers("a.csproj", "<TreatWarningsAsErrors>false</TreatWarningsAsErrors>"));
        Assert.Single(Silencers("a.csproj", "<CopyDocumentationFileToOutputDirectory>true</CopyDocumentationFileToOutputDirectory>"));
        Assert.Single(Silencers("a.csproj", "<WarningLevel>0</WarningLevel>"));
        Assert.Single(Silencers("a.csproj", "<NoWarn>$(NoWarn);CA1001;CS1574</NoWarn>"));
        Assert.Single(Silencers("a.csproj", "<NoWarn>$(NoWarn);1574</NoWarn>"));
        Assert.Single(Silencers("a.props", "<NoWarn Condition=\"'$(Configuration)' == 'Release'\">$(NoWarn);CS1734</NoWarn>"));
        Assert.Single(Silencers("Directory.Build.props", "<NoWarn>$(NoWarn);CS1591</NoWarn>\n<NoWarn>$(NoWarn);CS1734</NoWarn>"));
        Assert.Single(Silencers("a.targets", "<WarningsNotAsErrors>CS1570</WarningsNotAsErrors>"));
        Assert.Single(Silencers(".editorconfig", "[*.cs]\ndotnet_diagnostic.CS1574.severity = none"));
        Assert.Single(Silencers("x.globalconfig", "dotnet_diagnostic.CS1734.severity = suggestion"));
        Assert.Single(Silencers("a.cs", "#pragma warning disable CS1574 // stale cref"));
        Assert.Single(Silencers("a.cs", "#pragma warning disable IDE0005, CS1570"));
        Assert.Single(Silencers("a.cs", "#pragma warning disable 1574"));
        Assert.Single(Silencers("a.cs", "#pragma warning disable"));
        Assert.Single(Silencers("a.cs", "#pragma warning disable // everything below"));

        Assert.Empty(Silencers("a.csproj", "<NoWarn>$(NoWarn);CA1001;CA1515;CS1591</NoWarn><TreatWarningsAsErrors>true</TreatWarningsAsErrors><WarningLevel>9999</WarningLevel>"));
        Assert.Empty(Silencers(".editorconfig", "dotnet_diagnostic.IDE0055.severity = warning"));
        Assert.Empty(Silencers("a.cs", "#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks"));
        Assert.Empty(Silencers("a.cs", "#pragma warning disable CA2254 // a constant template, logged 1574 times"));
        Assert.Empty(Silencers("a.cs", "/// <see cref=\"CS1574\"/> is only text here"));

        Assert.Single(BuildCommandSilencers("      run: dotnet build ArcaneCore.slnx -c Release -p:GenerateDocumentationFile=false"));
        Assert.Single(BuildCommandSilencers("RUN dotnet publish src/x.csproj /p:TreatWarningsAsErrors=false -o /out"));
        Assert.Single(BuildCommandSilencers("      run: dotnet build -nowarn:CS1574"));
        Assert.Single(BuildCommandSilencers("      run: dotnet build ArcaneCore.slnx -warnaserror-"));
        Assert.Single(BuildCommandSilencers("dotnet build ArcaneCore.slnx -p:WarningLevel=0"));
        Assert.Single(BuildCommandSilencers("-property:NoWarn=1574"));
        Assert.Empty(BuildCommandSilencers("      # TreatWarningsAsErrors=true, so a warning fails the build.\n      run: dotnet build ArcaneCore.slnx -c Release -m:1"));
        Assert.Empty(BuildCommandSilencers("dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false"));
    }

    /// <summary>
    /// The lines of a CI workflow, Dockerfile, script or response file (<c>#</c> comment lines skipped) that pass a property turning the
    /// gate off, a nowarn list, or warnaserror-.
    /// </summary>
    private static List<string> BuildCommandSilencers(string text) => [.. text.Split('\n').Select(line => line.Trim())
        .Where(line => !line.StartsWith('#'))
        .Where(line => Regex.IsMatch(line,
            @"(^|\s)(-p:|/p:|-property:|/property:|--property:)""?(GenerateDocumentationFile|CopyDocumentationFileToOutputDirectory|TreatWarningsAsErrors|WarningsNotAsErrors|WarningLevel|NoWarn)=|(^|\s)[-/]nowarn\b|(^|\s)[-/]warnaserror-",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))];

    /// <summary>
    /// Checks this test assembly's own build: the documentation file in obj documents this method (so the compiler that built this class
    /// wrote it), and the output folder, which is what the deploys copy, holds no ArcaneCore .xml.
    /// </summary>
    [Fact]
    public void ThisBuild_WroteTheDocumentationFile_AndLeftItOutOfTheOutputFolder()
    {
        string output = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] segments = output.Split(Path.DirectorySeparatorChar);
        int bin = Array.LastIndexOf(segments, "bin");
        Assert.True(bin >= 0, $"the test output folder {output} has no bin segment");
        segments[bin] = "obj";
        string intermediate = string.Join(Path.DirectorySeparatorChar, segments);
        string name = typeof(DocCommentGateTests).Assembly.GetName().Name!;
        string dll = Path.Combine(intermediate, name + ".dll");
        string xml = Path.Combine(intermediate, name + ".xml");

        Assert.True(File.Exists(dll), $"no intermediate assembly at {dll}");
        Assert.True(File.Exists(xml), $"the compiler wrote no documentation file at {xml}: GenerateDocumentationFile is off, so doc comments go unchecked");
        string member = $"<member name=\"M:{typeof(DocCommentGateTests).FullName}.{nameof(ThisBuild_WroteTheDocumentationFile_AndLeftItOutOfTheOutputFolder)}\">";
        Assert.True(File.ReadAllText(xml).Contains(member, StringComparison.Ordinal), $"{xml} does not document this test ({member}): it was not written by the compilation of this class");

        Assert.True(Directory.GetFiles(output, "ArcaneCore.*.dll").Length >= 5, $"expected the ArcaneCore assemblies in {output}");
        Assert.Empty(Directory.GetFiles(output, "ArcaneCore.*.xml"));
    }

    /// <summary>
    /// Each setting in one file that turns the gate off: by project property, by a doc code in a warning list or a severity, or by
    /// a pragma. In Directory.Build.props the one <c>NoWarn</c> element is pinned by
    /// <see cref="TheRootProps_GeneratesDocs_KeepsThemInObj_AndSilencesOnlyTheNoiseCodes"/>, so only a second one counts here.
    /// </summary>
    private static List<string> Silencers(string relativePath, string text)
    {
        var found = new List<string>();
        string file = Path.GetFileName(relativePath);
        string extension = Path.GetExtension(relativePath);
        if (extension is ".csproj" or ".props" or ".targets")
        {
            foreach (Match m in Regex.Matches(text,
                @"<(GenerateDocumentationFile|TreatWarningsAsErrors)(\s[^>]*)?>\s*false\s*</\1>|<(CopyDocumentationFileToOutputDirectory)(\s[^>]*)?>\s*true\s*</\3>|<(WarningLevel)(\s[^>]*)?>\s*0\s*</\5>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                found.Add(m.Value);
            bool rootProps = relativePath == "Directory.Build.props";
            int lists = 0;
            foreach (Match m in Regex.Matches(text, @"<(NoWarn|WarningsNotAsErrors)(\s[^>]*)?>([^<]*)</\1>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (rootProps && m.Groups[1].Value == "NoWarn" && lists++ == 0)
                    continue;
                if (DocCodesIn(m.Groups[3].Value).Any())
                    found.Add(m.Value);
            }
        }
        else if (extension is ".editorconfig" or ".globalconfig" || file == ".editorconfig")
        {
            foreach (Match m in Regex.Matches(text, @"^\s*dotnet_diagnostic\.(\w+)\.severity\s*=.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (DocCodesIn(m.Groups[1].Value).Any())
                    found.Add(m.Value.Trim());
            }
        }
        else if (extension == ".cs")
        {
            foreach (Match m in Regex.Matches(text, @"^\s*#pragma\s+warning\s+disable\b([^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant))
            {
                string codes = m.Groups[1].Value.Split("//")[0];
                if (codes.Trim().Length == 0 || DocCodesIn(codes).Any())
                    found.Add(m.Value.Trim());
            }
        }

        return found;
    }
}
