using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary><see cref="ClientOutputPath.CheckDirectory"/>, the check every extract command makes on its <c>out</c> folder.</summary>
public sealed class ClientOutputPathDirectoryTests : IDisposable
{
    private const string Command = "extract.test";

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CommandException Refused(string requested, params string[] protectedRoots)
    {
        CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory(Command, requested, protectedRoots));
        Assert.Equal(CommandException.BadArguments, e.Code);
        Assert.StartsWith(Command + ":", e.Message, StringComparison.Ordinal);
        return e;
    }

    /// <summary>A folder under the scratch root that holds a <c>.git</c> directory (or a <c>.git</c> file, as a worktree has) and an empty subfolder.</summary>
    private string WorkingTree(bool gitFile, out string inside)
    {
        string tree = Path.Combine(_dir.Path, gitFile ? "worktree" : "tree");
        Directory.CreateDirectory(tree);
        if (gitFile)
        {
            File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: elsewhere");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(tree, ".git"));
        }

        inside = Path.Combine(tree, "target");
        Directory.CreateDirectory(inside);
        return tree;
    }

    [Fact]
    public void AcceptsAnOrdinaryFolder_ExistingOrNot_AndReturnsItsFullPathWithoutATrailingSeparator()
    {
        string existing = Path.Combine(_dir.Path, "existing");
        Directory.CreateDirectory(existing);

        Assert.Equal(existing, ClientOutputPath.CheckDirectory(Command, existing + Path.DirectorySeparatorChar, []));
        string fresh = Path.Combine(_dir.Path, "a", "b", "fresh");
        Assert.Equal(fresh, ClientOutputPath.CheckDirectory(Command, fresh, []));
        Assert.False(Directory.Exists(fresh));
    }

    [Fact]
    public void RefusesARelativePath()
    {
        Assert.Contains("absolute", Refused("relative/folder").Message, StringComparison.Ordinal);
        Assert.Contains("absolute", Refused(" ").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAFile()
    {
        string file = _dir.Write("a-file", [1]);

        Assert.Contains("names a file", Refused(file).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusesAFolderInsideAGitWorkingTree_WhetherTheMarkerIsADirectoryOrAFile(bool gitFile)
    {
        string tree = WorkingTree(gitFile, out string inside);

        string message = Refused(inside).Message;
        Assert.Contains("git working tree", message, StringComparison.Ordinal);
        Assert.Contains(tree, message, StringComparison.Ordinal);
        Assert.Contains("git working tree", Refused(Path.Combine(inside, "not", "yet")).Message, StringComparison.Ordinal);
        Assert.Contains("git working tree", Refused(tree).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAProtectedFolder_AndAFolderInsideIt_ButNotASibling()
    {
        string client = Path.Combine(_dir.Path, "client");
        Directory.CreateDirectory(Path.Combine(client, "Data"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "client-extra"));

        Assert.Contains("read-only", Refused(client, client).Message, StringComparison.Ordinal);
        Assert.Contains("read-only", Refused(client + Path.DirectorySeparatorChar, client).Message, StringComparison.Ordinal);
        Assert.Contains("read-only", Refused(Path.Combine(client, "Data"), client).Message, StringComparison.Ordinal);
        Assert.Contains("read-only", Refused(Path.Combine(client, "new", "folder"), client).Message, StringComparison.Ordinal);
        string sibling = Path.Combine(_dir.Path, "client-extra");
        Assert.Equal(sibling, ClientOutputPath.CheckDirectory(Command, sibling, [client]));
    }

    [Fact]
    public void RefusesALinkAtOut_ThatPointsIntoAWorkingTree()
    {
        WorkingTree(gitFile: false, out string inside);
        string link = Path.Combine(_dir.Path, "link");
        DirectoryLinks.Make(link, inside);
        try
        {
            Assert.Contains("git working tree", Refused(link).Message, StringComparison.Ordinal);
            Assert.Contains("git working tree", Refused(Path.Combine(link, "maps")).Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(inside));
        }
        finally
        {
            DirectoryLinks.Delete(link);
        }
    }

    [Fact]
    public void RefusesALinkAtOut_ThatPointsIntoAProtectedFolder()
    {
        string client = Path.Combine(_dir.Path, "client");
        Directory.CreateDirectory(Path.Combine(client, "Data"));
        string link = Path.Combine(_dir.Path, "link");
        DirectoryLinks.Make(link, Path.Combine(client, "Data"));
        try
        {
            Assert.Contains("read-only", Refused(link, client).Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryLinks.Delete(link);
        }
    }

    [WindowsTheory]
    [InlineData(@"\\.\PHYSICALDRIVE0")]
    [InlineData(@"\\?\C:\temp\out")]
    [InlineData(@"\\server\share\out")]
    [InlineData("//server/share/out")]
    [InlineData(@"\??\C:\temp\out")]
    [InlineData(@"\??\Volume{01234567-89ab-cdef-0123-456789abcdef}\temp\out")]
    [InlineData(@"\??\UNC\server\share\out")]
    [InlineData(@"\??\GLOBALROOT\Device\HarddiskVolume1\temp\out")]
    public void RefusesDeviceAndNetworkPaths_NamingThatRule(string requested)
    {
        Assert.Contains(ClientOutputPathTests.DeviceOrNetwork, Refused(requested).Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void RefusesAnObjectManagerSpellingOfTheClientFolder()
    {
        string client = Path.Combine(_dir.Path, "client");
        Directory.CreateDirectory(client);

        Assert.Contains(ClientOutputPathTests.DeviceOrNetwork, Refused(@"\??\" + client, client).Message, StringComparison.Ordinal);
        Assert.Contains(ClientOutputPathTests.DeviceOrNetwork, Refused(@"\??\" + Path.Combine(client, "Data"), client).Message, StringComparison.Ordinal);
    }

    [WindowsTheory]
    [InlineData("repo ")]
    [InlineData("repo.")]
    public void RefusesAComponentThatEndsInASpaceOrDot_ForTheFolderAndForAnyParent(string component)
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, "repo"));
        string asked = Path.Combine(_dir.Path, component);

        Assert.Contains("ends in a space or a dot", Refused(asked).Message, StringComparison.Ordinal);
        Assert.Contains("ends in a space or a dot", Refused(asked + Path.DirectorySeparatorChar).Message, StringComparison.Ordinal);
        Assert.Contains("ends in a space or a dot", Refused(Path.Combine(asked, "data")).Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "repo" }, Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName));
    }

    [WindowsFact]
    public void RefusesAWorkingTreeSpelledWithATrailingSpace_InsteadOfWritingToADifferentSibling()
    {
        string tree = WorkingTree(gitFile: false, out _);

        // Windows resolves "tree " to "tree"; the old code returned the spelling with the space and created a sibling named that.
        Assert.Contains("ends in a space or a dot", Refused(tree + " ").Message, StringComparison.Ordinal);
        Assert.Contains("ends in a space or a dot", Refused(tree + @" \out").Message, StringComparison.Ordinal);
    }
}
