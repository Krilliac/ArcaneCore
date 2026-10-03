namespace ArcaneCore.MockClient.Hosting;

/// <summary>A uniquely named temporary directory whose ownership is checked before deletion.</summary>
internal sealed class OwnedFixtureDirectory
{
    private const string Prefix = "ArcaneCore.MockClient-";
    private const string OwnerFile = ".fixture-owner";
    private readonly string _tempRoot;
    private readonly string _owner;

    private OwnedFixtureDirectory(string tempRoot, string path, string owner)
    {
        _tempRoot = tempRoot;
        Path = path;
        _owner = owner;
    }

    public string Path { get; }

    public static OwnedFixtureDirectory Create()
    {
        string tempRoot = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        string owner = Guid.NewGuid().ToString("N");
        string path = System.IO.Path.Combine(tempRoot, Prefix + owner);
        Directory.CreateDirectory(path);
        using (var stream = new FileStream(System.IO.Path.Combine(path, OwnerFile), FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(owner);
        }

        return new OwnedFixtureDirectory(tempRoot, path, owner);
    }

    public void Delete()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        string fullPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path));
        string? parent = System.IO.Path.GetDirectoryName(fullPath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(parent, _tempRoot, comparison)
            || !string.Equals(System.IO.Path.GetFileName(fullPath), Prefix + _owner, StringComparison.Ordinal)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The synthetic fixture directory failed its ownership check.");
        }

        string ownerPath = System.IO.Path.Combine(fullPath, OwnerFile);
        if ((File.GetAttributes(ownerPath) & FileAttributes.ReparsePoint) != 0
            || !string.Equals(File.ReadAllText(ownerPath), _owner, StringComparison.Ordinal))
        {
            throw new IOException("The synthetic fixture ownership marker does not match.");
        }

        // Inspect each directory before descending, so a junction or symbolic link cannot
        // redirect recursive deletion outside this fixture.
        var pending = new Stack<string>();
        pending.Push(fullPath);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("A symbolic link was found in the synthetic fixture directory.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }

        Directory.Delete(fullPath, recursive: true);
    }
}
