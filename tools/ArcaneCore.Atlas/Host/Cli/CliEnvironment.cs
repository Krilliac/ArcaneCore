using System.Diagnostics;
using System.Text;
using ArcaneCore.Atlas.Core;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host.Cli;

/// <summary>
/// Everything a verb reads from the process: standard streams, environment variables, the state folder and the browser
/// launcher. <see cref="AtlasCli"/> touches no static process state, so tests run verbs side by side with their own instances.
/// Immutable after construction; the stream members are used by one verb run at a time.
/// </summary>
public sealed class CliEnvironment
{
    /// <summary>Standard input (the MCP request stream, <c>--json -</c> and <c>script -</c>).</summary>
    public TextReader In { get; init; } = TextReader.Null;

    /// <summary>Standard output: only command results, MCP messages and the <c>serve</c> address line are written here.</summary>
    public TextWriter Out { get; init; } = TextWriter.Null;

    /// <summary>Standard error: diagnostics, usage errors and logs.</summary>
    public TextWriter Error { get; init; } = TextWriter.Null;

    /// <summary>Where discovery files and bookmarks live.</summary>
    public AtlasState State { get; init; } = AtlasState.FromEnvironment();

    /// <summary>Reads an environment variable (for the data and client directory defaults).</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Every environment variable (name to value), for settings read by prefix (<c>ARCANECORE_ATLAS_PUBLIC__*</c>); null means none.</summary>
    public System.Collections.IDictionary? EnvironmentVariables { get; init; }

    /// <summary>The modules to compose; null discovers every module of the Atlas assembly.</summary>
    public IReadOnlyList<IAtlasModule>? Modules { get; init; }

    /// <summary>The logger factory every context uses; null logs to standard error through the console logger.</summary>
    public ILoggerFactory? Loggers { get; init; }

    /// <summary>Opens the UI window for <c>serve --open</c>.</summary>
    public IBrowserLauncher Browser { get; init; } = new SystemBrowserLauncher();

    /// <summary>The UI files <c>serve</c> hosts; null uses the files embedded in this assembly.</summary>
    public IWebAssets? Assets { get; init; }

    /// <summary>Liveness test for discovery files; null checks the operating system's process table.</summary>
    public Func<DiscoveryRecord, bool>? IsAlive { get; init; }

    /// <summary>The real process: UTF-8 without a byte-order mark on all three streams and '\n' line endings.</summary>
    public static CliEnvironment ForProcess()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new CliEnvironment
        {
            In = new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: false),
            Out = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" },
            Error = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true, NewLine = "\n" },
            EnvironmentVariables = Environment.GetEnvironmentVariables(),
        };
    }
}

/// <summary>Opens the Atlas UI in a browser window of its own.</summary>
public interface IBrowserLauncher
{
    /// <summary>Starts an app-mode window on <paramref name="url"/> and returns the browser's name, or null when none is installed.</summary>
    string? TryOpenApp(Uri url);
}

/// <summary>Which operating system's install locations <see cref="SystemBrowserLauncher.Find"/> searches.</summary>
public enum BrowserPlatform
{
    Windows,
    MacOs,
    Linux,
}

/// <summary>
/// Starts Microsoft Edge, else Google Chrome, with <c>--app=&lt;url&gt;</c> (a chrome-less window). The token travels in the URL
/// fragment, which a browser never sends in a request line.
/// </summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    /// <summary>The installed browser to use: Edge first, then Chrome; null when neither is found.</summary>
    /// <param name="platform">Which install locations to look at.</param>
    /// <param name="getEnvironmentVariable">Reads <c>ProgramFiles</c>, <c>ProgramFiles(x86)</c>, <c>LocalAppData</c> and <c>PATH</c>.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    public static (string Name, string Path)? Find(BrowserPlatform platform, Func<string, string?> getEnvironmentVariable, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(fileExists);
        foreach ((string name, IEnumerable<string> candidates) in Candidates(platform, getEnvironmentVariable))
        {
            foreach (string candidate in candidates)
            {
                if (fileExists(candidate))
                {
                    return (name, candidate);
                }
            }
        }

        return null;
    }

    public string? TryOpenApp(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        BrowserPlatform platform = OperatingSystem.IsWindows() ? BrowserPlatform.Windows
            : OperatingSystem.IsMacOS() ? BrowserPlatform.MacOs
            : BrowserPlatform.Linux;
        if (Find(platform, Environment.GetEnvironmentVariable, File.Exists) is not { } browser)
        {
            return null;
        }

        var start = new ProcessStartInfo(browser.Path) { UseShellExecute = false };
        start.ArgumentList.Add("--app=" + url.AbsoluteUri);
        try
        {
            using Process? process = Process.Start(start);
            return process is null ? null : browser.Name;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<(string Name, IEnumerable<string> Paths)> Candidates(BrowserPlatform platform, Func<string, string?> env)
    {
        switch (platform)
        {
            case BrowserPlatform.Windows:
                string?[] programFiles = [env("ProgramFiles(x86)"), env("ProgramFiles")];
                string? localAppData = env("LocalAppData");
                yield return ("Microsoft Edge", Under(programFiles, @"Microsoft\Edge\Application\msedge.exe"));
                yield return ("Google Chrome", Under([.. programFiles, localAppData], @"Google\Chrome\Application\chrome.exe"));
                break;
            case BrowserPlatform.MacOs:
                yield return ("Microsoft Edge", ["/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"]);
                yield return ("Google Chrome", ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"]);
                break;
            default:
                string[] directories = (env("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                yield return ("Microsoft Edge", OnPath(directories, "microsoft-edge-stable", "microsoft-edge"));
                yield return ("Google Chrome", OnPath(directories, "google-chrome-stable", "google-chrome"));
                break;
        }
    }

    private static IEnumerable<string> Under(IEnumerable<string?> roots, string relative)
    {
        foreach (string? root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, relative);
            }
        }
    }

    private static IEnumerable<string> OnPath(string[] directories, params string[] names)
    {
        foreach (string name in names)
        {
            foreach (string directory in directories)
            {
                yield return Path.Combine(directory, name);
            }
        }
    }
}
