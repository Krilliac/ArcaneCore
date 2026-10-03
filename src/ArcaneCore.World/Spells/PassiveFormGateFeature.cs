using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Installs <see cref="PassiveFormCastCheck"/> (discovered <see cref="IWorldFeature"/>): form-bound passives learned or
/// restored at login apply only in their form (vmangos Player::IsNeedCastPassiveLikeSpellAtLearn). The form table is the
/// one the stance feature linked to the combat environment, else the built-in retail rows.
/// </summary>
public sealed class PassiveFormGateFeature(IServiceProvider services) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        services.GetRequiredService<SpellFeature>().System.RegisterCastCheck(
            new PassiveFormCastCheck(() => CombatEnvironment.For(world).ShapeshiftForms));
    }
}
