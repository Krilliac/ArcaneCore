using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.PetAuras;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Pets;

/// <summary>
/// The talent pet hooks in the world daemon (discovered <see cref="IWorldFeature"/>, <see cref="TalentPetHooks"/>): a respec removes the owner's pet
/// (vmangos Player.cpp:4144-4146) and a learned talent re-casts the owner's talent auras on the pet. The talent feature attaches after this one
/// (features attach in full-name order and <c>ArcaneCore.World.Talents</c> sorts after <c>ArcaneCore.World.Pets</c>), so the hooks are attached on the
/// world thread once every feature is attached. Without a talent service (no Talent.dbc configured) there is nothing to hook.
/// </summary>
public sealed class TalentPetFeature(IServiceProvider services, ILogger<TalentPetFeature> logger) : IWorldFeature
{
    /// <summary>The attached hooks (null until the world's first tick, and without a talent service).</summary>
    public TalentPetHooks? Hooks { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.Post(Install);
    }

    private void Install()
    {
        if (services.GetService<TalentFeature>()?.Service is not { } talents
            || services.GetService<PetsFeature>() is not { } pets
            || services.GetService<SpellFeature>() is not { } spells)
        {
            logger.LogInformation("Talent pet hooks: no talent service, a respec leaves the pet alone");
            return;
        }

        Hooks = TalentPetHooks.Attach(talents, pets.Service, spells.System);
    }

    public Task StopAsync()
    {
        Hooks?.Dispose();
        Hooks = null;
        return Task.CompletedTask;
    }
}
