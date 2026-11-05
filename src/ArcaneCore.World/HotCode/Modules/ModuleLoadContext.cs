using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.HotCode.Modules;

/// <summary>
/// The collectible load context one module lives in. Everything the module shares with the server
/// (ArcaneCore.*, the framework, any assembly the default context already has) is resolved to the
/// server's copy, so a module's <c>IOpcodeHandlerGroup</c> is the server's interface; only the
/// module's own private dependencies are read from its folder. Assemblies are loaded from bytes,
/// never from the path, so no file stays locked and a module can be replaced on disk while the
/// previous version is still loaded. When <paramref name="allowlist"/> names an allowlist file, every
/// private dependency dll is hashed from the loaded bytes and checked against it (fail closed) before
/// it is loaded; a refusal throws, is kept in <see cref="Refusal"/> and is reported once to the anomaly
/// ledger. Without an allowlist nothing is checked.
/// </summary>
internal sealed class ModuleLoadContext(string moduleName, string directory, string? allowlist = null)
    : AssemblyLoadContext("hotmodule:" + moduleName, isCollectible: true)
{
    private static readonly string[] SharedPrefixes = ["ArcaneCore.", "System.", "Microsoft.", "netstandard", "mscorlib", "WindowsBase"];

    private string? _refusal;

    /// <summary>Why a private dependency was refused by the allowlist, or null. Set once (the first refusal wins); safe to read from any thread.</summary>
    public string? Refusal => Volatile.Read(ref _refusal);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? simple = assemblyName.Name;
        if (string.IsNullOrEmpty(simple) || IsShared(simple) || Path.GetFileName(simple) != simple)
        {
            return null; // the server's copy, via the default context (or a name that is not a plain file name)
        }

        if (Refusal is { } earlier)
        {
            // The module is already condemned: no further dependency is read or hashed, so a retry on the world thread costs nothing.
            throw new FileLoadException(earlier);
        }

        var file = new FileInfo(Path.Combine(directory, simple + ".dll"));
        if (!file.Exists || file.LinkTarget is not null)
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(file.FullName);
        AllowlistVerdict verdict = ModuleAllowlist.Check(allowlist, Convert.ToHexString(SHA256.HashData(bytes)));
        if (!verdict.Allowed)
        {
            string reason = $"dependency '{simple}.dll' refused by the module allowlist: {verdict.Detail}";
            if (Interlocked.CompareExchange(ref _refusal, reason, null) is null)
            {
                AnomalyLedger.Shared.Report("hotcode", "module-dependency-refused", moduleName, reason, AnomalySeverity.Warning);
            }

            throw new FileLoadException(reason, file.FullName);
        }

        return LoadFromStream(new MemoryStream(bytes));
    }

    private static bool IsShared(string simpleName)
        => SharedPrefixes.Any(prefix => simpleName.StartsWith(prefix, StringComparison.Ordinal))
            || Default.Assemblies.Any(a => string.Equals(a.GetName().Name, simpleName, StringComparison.Ordinal));
}
