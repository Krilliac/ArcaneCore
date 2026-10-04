using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Stats;

/// <summary>
/// Wires <see cref="FormStatListener"/> into the daemon (discovered <see cref="IWorldFeature"/>): form changes of the stance
/// feature reach the player stat system, and the spell system's aura events keep Predatory Strikes current.
/// Configuration <c>Forms:ResetFistAttackTimeOnFormLoss</c> (default false = vmangos literal behaviour: SetRegularAttackTime only rewrites
/// hands that hold a weapon, so an unarmed hand keeps 1.0 / 2.5 s, Player.cpp:5158-5172); true is an opt-in deviation that gives
/// a weapon-less hand the 2.0 s base attack time when a form ends.
/// </summary>
public sealed class FormStatFeature(IServiceProvider services) : IWorldFeature
{
    public const string ResetFistKey = "Forms:ResetFistAttackTimeOnFormLoss";

    public FormStatListener? Listener { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        IConfiguration? configuration = services.GetService<IConfiguration>();
        bool resetFist = configuration is not null && bool.TryParse(configuration[ResetFistKey], out bool configured) && configured;
        Listener = new FormStatListener(resetFist);
        Listener.Attach(services.GetRequiredService<SpellFeature>().System);
        services.GetRequiredService<StanceFeature>().AddFormChangeListener(Listener);
    }
}
