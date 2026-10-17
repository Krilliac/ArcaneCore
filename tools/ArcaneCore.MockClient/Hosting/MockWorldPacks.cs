using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;

namespace ArcaneCore.MockClient.Hosting;

/// <summary>
/// The built-in packs of the mock client's in-process world: the same as the daemon's (docs/design/hot-reload-tiers.md, 4.6), so the
/// synthetic server discovers the Playerbots features, service modules and commands like production. The daemon loads the pack from
/// beside its executable; here the project is referenced, so its assembly is registered directly. Idempotent; it must run before
/// anything in the process reads <see cref="WorldCodeAssemblies.Startup"/>.
/// </summary>
public static class MockWorldPacks
{
    /// <summary>Register every built-in pack the daemon registers.</summary>
    public static void EnsureRegistered() => WorldCodeAssemblies.RegisterBuiltIn(typeof(ManagedPlayerbotFeature).Assembly);
}
