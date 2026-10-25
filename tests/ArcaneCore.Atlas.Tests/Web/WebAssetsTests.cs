using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ArcaneCore.Atlas.Core;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Web;

/// <summary>
/// The Web folder as the host will serve it: every file is an embedded resource of arcane-atlas, every file the page and
/// the modules point at exists, and nothing refers to a host outside this computer (the UI loads no CDN, font or script).
/// </summary>
public sealed class WebAssetsTests(ITestOutputHelper output)
{
    private static readonly Assembly Atlas = typeof(WorldGrid).Assembly;

    // The scheme prefixes are built from Uri constants so the source has no URL literal for secret/URL scanners to flag.
    private static readonly Regex AbsoluteUrl = new(
        $@"(?<![A-Za-z0-9_$}}])(?:{Uri.UriSchemeHttp}|{Uri.UriSchemeHttps}|{Uri.UriSchemeWs}|{Uri.UriSchemeWss}|{Uri.UriSchemeFtp}):\/\/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProtocolRelativeMarkup = new(
        @"(?:\b(?:src|href|action|poster|srcset|data)\s*=\s*[""']\s*//|url\(\s*[""']?\s*//|@import\s+[""']\s*//)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProtocolRelativeScript = new(
        @"\b(?:fetch|import|WebSocket|XMLHttpRequest|EventSource|Worker|SharedWorker|sendBeacon)\s*\(\s*[""'`]\s*//",
        RegexOptions.CultureInvariant);

    private static readonly Regex ImportSpecifier = new(
        @"(?:^|[\s;}])import\s+(?:[\w*{}\s,$]+?\s+from\s+)?['""]([^'""]+)['""]|\bimport\(\s*['""]([^'""]+)['""]\s*\)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly string[] ForbiddenExtensions =
    [
        ".dbc", ".mpq", ".map", ".vmtree", ".vmtile", ".vmo", ".mmtile", ".mmap", ".adt", ".wdt", ".wmo", ".m2", ".blp",
    ];

    private static readonly (string Name, Regex Pattern)[] Forbidden =
    [
        ("Math.random (use a deterministic source)", new Regex(@"Math\.random", RegexOptions.CultureInvariant)),
        ("eval", new Regex(@"\beval\s*\(", RegexOptions.CultureInvariant)),
        ("new Function", new Regex(@"\bnew\s+Function\b", RegexOptions.CultureInvariant)),
        ("document.write", new Regex(@"document\.write", RegexOptions.CultureInvariant)),
        ("innerHTML / outerHTML / insertAdjacentHTML (build nodes instead)", new Regex(@"\b(?:innerHTML|outerHTML|insertAdjacentHTML)\b", RegexOptions.CultureInvariant)),
        ("debugger statement", new Regex(@"\bdebugger\b", RegexOptions.CultureInvariant)),
        ("an unfinished marker", new Regex(@"\b(?:TODO|FIXME|XXX|HACK)\b", RegexOptions.CultureInvariant)),
    ];

    /// <summary>Files under <c>Web/</c> by path relative to it, with forward slashes (so <c>js/api.js</c>).</summary>
    private static List<string> WebFiles()
    {
        string root = WebTestEnvironment.WebSources;
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string ReadWeb(string relative) => File.ReadAllText(Path.Combine(WebTestEnvironment.WebSources, relative));

    /// <summary>Embedded resource names of the Web folder: normalized name (forward slashes) to the name as the assembly spells it.</summary>
    private static Dictionary<string, string> EmbeddedWeb()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in Atlas.GetManifestResourceNames())
        {
            string normalized = raw.Replace('\\', '/');
            if (normalized.StartsWith("web/", StringComparison.Ordinal))
            {
                Assert.True(map.TryAdd(normalized, raw), $"two embedded resources normalize to '{normalized}'");
            }
        }

        return map;
    }

    [Fact]
    public void EveryWebFile_IsAnEmbeddedResource_AndNothingElseIs()
    {
        List<string> files = WebFiles();
        Dictionary<string, string> embedded = EmbeddedWeb();

        Assert.Contains("index.html", files);
        Assert.True(files.Count >= 35, $"only {files.Count} Web files were found; the scan is looking in the wrong place");
        string[] expected = [.. files.Select(f => "web/" + f)];
        Assert.Equal(expected, embedded.Keys.Order(StringComparer.Ordinal).ToArray());

        int backslashed = embedded.Values.Count(raw => raw.Contains('\\', StringComparison.Ordinal));
        output.WriteLine($"{embedded.Count} embedded Web resources; {backslashed} are named with a backslash by this build (the host must look names up with '/' folded to the build's spelling).");
    }

    [Fact]
    public void EmbeddedResources_AreByteIdenticalToTheFiles()
    {
        Dictionary<string, string> embedded = EmbeddedWeb();
        int compared = 0;
        foreach (string file in WebFiles())
        {
            using Stream? stream = Atlas.GetManifestResourceStream(embedded["web/" + file]);
            Assert.NotNull(stream);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            byte[] onDisk = File.ReadAllBytes(Path.Combine(WebTestEnvironment.WebSources, file));
            Assert.True(SHA256.HashData(onDisk).AsSpan().SequenceEqual(SHA256.HashData(memory.ToArray())), $"{file} differs from its embedded copy; rebuild the arcane-atlas project");
            Assert.NotEmpty(onDisk);
            compared++;
        }

        Assert.True(compared >= 35, $"only {compared} files were compared");
    }

    [Fact]
    public void IndexHtml_PointsOnlyAtEmbeddedFiles_AndRunsNoInlineCode()
    {
        string html = ReadWeb("index.html");
        Dictionary<string, string> embedded = EmbeddedWeb();

        var targets = Regex.Matches(html, @"\b(?:src|href)\s*=\s*""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("/app/js/main.js", targets);
        Assert.Contains("/app/app.css", targets);
        foreach (string target in targets)
        {
            if (target.StartsWith("data:", StringComparison.Ordinal) || target.StartsWith('#'))
            {
                continue;
            }

            Assert.StartsWith("/app/", target, StringComparison.Ordinal);
            Assert.True(embedded.ContainsKey("web/" + target["/app/".Length..]), $"index.html points at {target}, which is not an embedded resource");
        }

        // The page is served with a script-src 'self' policy: an inline script, style block or handler attribute would be refused.
        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc\s*=)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), html);
        Assert.DoesNotMatch(new Regex(@"<style\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), html);
        Assert.DoesNotMatch(new Regex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), html);
        Assert.DoesNotMatch(new Regex(@"\sstyle\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), html);
    }

    [Fact]
    public void PublicHtml_PointsOnlyAtEmbeddedFiles_AndRunsNoInlineCode()
    {
        string html = ReadWeb("public.html");
        Dictionary<string, string> embedded = EmbeddedWeb();

        var targets = Regex.Matches(html, @"\b(?:src|href)\s*=\s*""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("/app/js/public/boot.js", targets);
        Assert.Contains("/app/app.css", targets);
        Assert.Contains("/app/public.css", targets);
        foreach (string target in targets)
        {
            if (target.StartsWith("data:", StringComparison.Ordinal) || target.StartsWith('#'))
            {
                continue;
            }

            Assert.StartsWith("/app/", target, StringComparison.Ordinal);
            Assert.True(embedded.ContainsKey("web/" + target["/app/".Length..]), $"public.html points at {target}, which is not an embedded resource");
        }

        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc\s*=)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), html);
        Assert.DoesNotMatch(new Regex(@"<style\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), html);
        Assert.DoesNotMatch(new Regex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), html);
        Assert.DoesNotMatch(new Regex(@"\sstyle\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), html);
    }

    [Fact]
    public void EveryModuleImport_ResolvesToAnEmbeddedFile()
    {
        Dictionary<string, string> embedded = EmbeddedWeb();
        int checkedImports = 0;
        foreach (string file in WebFiles().Where(f => f.EndsWith(".js", StringComparison.Ordinal)))
        {
            string source = StripComments(ReadWeb(file));
            foreach (Match m in ImportSpecifier.Matches(source))
            {
                string spec = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                Assert.True(spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal), $"{file} imports '{spec}': only relative module paths are allowed");
                string? resolved = Resolve(file, spec);
                Assert.True(resolved is not null && embedded.ContainsKey("web/" + resolved), $"{file} imports '{spec}', which is not an embedded resource");
                checkedImports++;
            }
        }

        Assert.True(checkedImports >= 100, $"only {checkedImports} imports were found; the import scan stopped matching");
    }

    [Fact]
    public void EntryModule_ReachesEveryJavaScriptFile()
    {
        // A module nothing imports is dead weight in the assembly (or a typo'd import that the page never loads).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue("js/main.js");
        queue.Enqueue("js/public/boot.js"); // the public page's own entry (public.html)
        queue.Enqueue("js/clientfs/index.js"); // nothing imports it yet: lane D2 (PublicApi.tile) and lane D3 (Local settings entry) will; drop this root once they do
        queue.Enqueue("js/clientfs/worker.js"); // started with new Worker(), not imported
        while (queue.Count > 0)
        {
            string file = queue.Dequeue();
            if (!seen.Add(file))
            {
                continue;
            }

            foreach (Match m in ImportSpecifier.Matches(StripComments(ReadWeb(file))))
            {
                string spec = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (Resolve(file, spec) is { } next)
                {
                    queue.Enqueue(next);
                }
            }
        }

        string[] unreachable = [.. WebFiles().Where(f => f.EndsWith(".js", StringComparison.Ordinal) && !seen.Contains(f))];
        Assert.Empty(unreachable);
        Assert.True(seen.Count >= 35, $"only {seen.Count} modules are reachable from main.js");
    }

    [Fact]
    public void WebFiles_ReferenceNoExternalHost()
    {
        int scanned = 0;
        foreach (string file in WebFiles())
        {
            string text = ReadWeb(file);
            Match absolute = AbsoluteUrl.Match(text);
            Assert.False(absolute.Success, $"{file} contains an absolute URL ('{absolute.Value}'); the UI may load nothing from outside the host");
            bool markup = file.EndsWith(".html", StringComparison.Ordinal) || file.EndsWith(".css", StringComparison.Ordinal);
            Assert.DoesNotMatch(markup ? ProtocolRelativeMarkup : ProtocolRelativeScript, text);
            scanned++;
        }

        Assert.True(scanned >= 35, $"only {scanned} files were scanned");
    }

    [Fact]
    public void WebFiles_CarryNoClientDataExtensions_AndNoForbiddenConstructs()
    {
        foreach (string file in WebFiles())
        {
            Assert.DoesNotContain(ForbiddenExtensions, ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            string text = ReadWeb(file);
            foreach ((string name, Regex pattern) in Forbidden)
            {
                Match m = pattern.Match(text);
                Assert.False(m.Success, $"{file} contains {name}: '{m.Value}' at index {m.Index}");
            }
        }
    }

    private static string StripComments(string source)
        => Regex.Replace(Regex.Replace(source, @"/\*[\s\S]*?\*/", string.Empty), @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

    /// <summary>The path (relative to Web/, forward slashes) a module specifier names from a module, or null when it leaves Web/.</summary>
    private static string? Resolve(string fromFile, string spec)
    {
        var parts = new List<string>(fromFile.Split('/').SkipLast(1));
        foreach (string segment in spec.Split('/'))
        {
            if (segment is "." or "")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(segment);
            }
        }

        return string.Join('/', parts);
    }
}
