using System.Reflection;
using System.Runtime.Loader;

namespace ArcaneCore.World.HotCode.Modules;

/// <summary>
/// The collectible load context one module lives in. Everything the module shares with the server
/// (ArcaneCore.*, the framework, any assembly the default context already has) is resolved to the
/// server's copy, so a module's <c>IOpcodeHandlerGroup</c> is the server's interface; only the
/// module's own private dependencies are read from its folder. Assemblies are loaded from bytes,
/// never from the path, so no file stays locked and a module can be replaced on disk while the
/// previous version is still loaded.
/// </summary>
internal sealed class ModuleLoadContext(string moduleName, string directory)
    : AssemblyLoadContext("hotmodule:" + moduleName, isCollectible: true)
{
    private static readonly string[] SharedPrefixes = ["ArcaneCore.", "System.", "Microsoft.", "netstandard", "mscorlib", "WindowsBase"];

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? simple = assemblyName.Name;
        if (string.IsNullOrEmpty(simple) || IsShared(simple) || Path.GetFileName(simple) != simple)
        {
            return null; // the server's copy, via the default context (or a name that is not a plain file name)
        }

        var file = new FileInfo(Path.Combine(directory, simple + ".dll"));
        if (!file.Exists || file.LinkTarget is not null)
        {
            return null;
        }

        return LoadFromStream(new MemoryStream(File.ReadAllBytes(file.FullName)));
    }

    private static bool IsShared(string simpleName)
        => SharedPrefixes.Any(prefix => simpleName.StartsWith(prefix, StringComparison.Ordinal))
            || Default.Assemblies.Any(a => string.Equals(a.GetName().Name, simpleName, StringComparison.Ordinal));
}
