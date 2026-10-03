using System.Reflection.Metadata;
using ArcaneCore.World.HotCode;

[assembly: MetadataUpdateHandler(typeof(HotCodeMetadataHandler))]

namespace ArcaneCore.World.HotCode;

/// <summary>
/// The runtime calls <c>UpdateApplication</c> on this type after it applies a code edit
/// (<see cref="MetadataUpdateHandlerAttribute"/>; measured by tools/hotcode-spike: once per applied
/// edit, on a thread-pool thread). It forwards to the active <see cref="HotCodeRefresh"/>, if any.
/// With code hot reload disabled nothing is active, no edit can be applied (the launch guard
/// refuses such a process), and this type does nothing.
/// </summary>
internal static class HotCodeMetadataHandler
{
    private static HotCodeRefresh? s_active;

    internal static void Activate(HotCodeRefresh refresh) => Volatile.Write(ref s_active, refresh);

    /// <summary>Deactivate <paramref name="refresh"/> unless something else has been activated since.</summary>
    internal static void Deactivate(HotCodeRefresh refresh)
        => Interlocked.CompareExchange(ref s_active, null, refresh);

    /// <summary>Called by the runtime with the types the edit touched (null: any type may have changed).</summary>
    internal static void UpdateApplication(Type[]? updatedTypes) => Volatile.Read(ref s_active)?.OnMetadataUpdate(updatedTypes);
}
