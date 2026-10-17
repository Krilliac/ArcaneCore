using System.Runtime.CompilerServices;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;

namespace ArcaneCore.World.Tests;

/// <summary>
/// The test process registers the daemon's built-in packs (the Playerbots pack, docs/design/hot-reload-tiers.md 4.6) before any test
/// runs, so every discovery over the world's startup code set (features, service modules, commands, config checks, the wiring audit)
/// sees what production sees. The daemon loads the pack from beside its executable; this project references it.
/// </summary>
internal static class BuiltInPacks
{
#pragma warning disable CA2255 // A test assembly's one-time setup, before xUnit runs any test; it is not a library initializer.
    [ModuleInitializer]
    internal static void Initialize() => WorldCodeAssemblies.RegisterBuiltIn(typeof(ManagedPlayerbotFeature).Assembly);
#pragma warning restore CA2255
}
