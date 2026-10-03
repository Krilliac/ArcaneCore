using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Collision;

/// <summary>
/// The world daemon's collision feature (docs/integration/vmap-los.md): at startup it binds
/// <c>World:Collision</c> and installs the world's <see cref="ILineOfSight"/> and
/// <see cref="IPathfinder"/> into <see cref="WorldCollision"/>. Without vmap/mmap data the open
/// defaults stay (everything in sight, straight-line paths); that is logged once, never fatal.
/// <para>Handlers reach the services with <c>session.Services.GetRequiredService&lt;CollisionFeature&gt;()</c>;
/// world code uses <c>map.Collision</c>.</para>
/// </summary>
public sealed class CollisionFeature(IServiceProvider services, ILogger<CollisionFeature> logger) : IWorldFeature
{
    private WorldCollision? _collision;

    /// <summary>The bound settings (filled by <see cref="Attach"/>).</summary>
    public CollisionOptions Options { get; } = new();

    /// <summary>The world's collision services (available after <see cref="Attach"/>).</summary>
    public WorldCollision Collision => _collision ?? throw new InvalidOperationException("the collision feature is not attached");

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        services.GetService<IConfiguration>()?.GetSection(CollisionOptions.SectionName).Bind(Options);
        _collision = WorldCollision.Of(world);
        CollisionServices.Install(_collision, Options, world.Options.Maps.DataDirectory, logger);
    }
}
