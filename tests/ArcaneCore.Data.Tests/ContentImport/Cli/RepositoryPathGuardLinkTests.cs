using System.Diagnostics;
using ArcaneCore.Data.Content.Import;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>
/// <see cref="RepositoryPathGuard"/> must judge the place the bytes really land: a directory link
/// (junction on Windows) is resolved before the work tree is looked for, in both directions.
/// </summary>
public sealed class RepositoryPathGuardLinkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-repoguard-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];

    public RepositoryPathGuardLinkTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        // Remove links first so the recursive delete can never walk through one into the target.
        foreach (string link in _links)
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Git can hold .git files briefly on Windows; the temp folder is disposable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void PathUnderALinkOutsideTheCheckout_ThatTargetsTheCheckout_IsRefused()
    {
        string repo = MakeRepo("repo");
        // A subfolder has no .git of its own, so a lexical walk up from the link never finds the work tree.
        string sub = Path.Combine(repo, "sub");
        Directory.CreateDirectory(sub);
        string outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(outside, "viaLink");
        MakeDirectoryLink(link, sub);

        string? reason = RepositoryPathGuard.Check(Path.Combine(link, "world.db"));

        Assert.NotNull(reason);
    }

    [Fact]
    public void PathUnderALinkOutsideTheCheckout_ThatTargetsAnIgnoredFolder_IsAllowed()
    {
        string repo = MakeRepo("repo");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "ignored/\n");
        string ignored = Path.Combine(repo, "ignored");
        Directory.CreateDirectory(ignored);
        string link = Path.Combine(_directory, "viaLink");
        MakeDirectoryLink(link, ignored);

        string? reason = RepositoryPathGuard.Check(Path.Combine(link, "world.db"));

        Assert.Null(reason);
    }

    [Fact]
    public void PathUnderALinkInsideTheCheckout_ThatTargetsOutsideIt_IsAllowed()
    {
        string repo = MakeRepo("repo");
        string outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(repo, "viaLink");
        MakeDirectoryLink(link, outside);

        string? reason = RepositoryPathGuard.Check(Path.Combine(link, "world.db"));

        Assert.Null(reason);
    }

    [Fact]
    public void PathInsideTheCheckout_ReachedThroughALinkToTheCheckout_StillFollowsGitIgnore()
    {
        string repo = MakeRepo("repo");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "ignored/\n");
        Directory.CreateDirectory(Path.Combine(repo, "ignored"));
        string link = Path.Combine(_directory, "repoLink");
        MakeDirectoryLink(link, repo);

        Assert.NotNull(RepositoryPathGuard.Check(Path.Combine(link, "world.db")));
        Assert.Null(RepositoryPathGuard.Check(Path.Combine(link, "ignored", "world.db")));
    }

    [FileSymlinkFact]
    public void FileLinkOutsideTheCheckout_ThatTargetsAPathInIt_IsRefused()
    {
        string repo = MakeRepo("repo");
        string target = Path.Combine(repo, "world.db");
        File.WriteAllText(target, "x");
        string link = Path.Combine(_directory, "world-link.db");
        File.CreateSymbolicLink(link, target);

        Assert.NotNull(RepositoryPathGuard.Check(link));
    }

    [Fact]
    public void LinkLoop_DoesNotHangOrThrow()
    {
        string a = Path.Combine(_directory, "a");
        string b = Path.Combine(_directory, "b");
        Directory.CreateDirectory(Path.Combine(_directory, "seed"));
        MakeDirectoryLink(a, Path.Combine(_directory, "seed"));
        MakeDirectoryLink(b, a);
        Directory.Delete(a);
        _links.Remove(a);
        MakeDirectoryLink(a, b);

        // The outcome (refuse or allow) is the fix's choice; the guard must simply return.
        _ = RepositoryPathGuard.Check(Path.Combine(a, "world.db"));
    }

    /// <summary>Skips (rather than silently passes) where file symlinks need a privilege this machine does not grant.</summary>
    private sealed class FileSymlinkFactAttribute : FactAttribute
    {
        public FileSymlinkFactAttribute()
        {
            string probe = Path.Combine(Path.GetTempPath(), "arcanecore-symlink-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.CreateSymbolicLink(probe, Path.Combine(Path.GetTempPath(), "arcanecore-symlink-target"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip = "File symlinks need Developer Mode or elevation on this machine.";
                return;
            }

            File.Delete(probe);
        }
    }

    private string MakeRepo(string name)
    {
        string repo = Path.Combine(_directory, name);
        Directory.CreateDirectory(repo);
        Run(repo, "git", ["init", "-q"]);
        return repo;
    }

    private void MakeDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Run(_directory, "cmd.exe", ["/c", "mklink", "/J", link, target]);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        _links.Add(link);
    }

    private static void Run(string directory, string fileName, string[] args)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);
        Assert.True(process.ExitCode == 0, $"{fileName} {string.Join(' ', args)} failed: {stderr.Result}");
    }
}
