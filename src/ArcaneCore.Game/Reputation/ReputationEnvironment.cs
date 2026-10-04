using System.Runtime.CompilerServices;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// Reaches the live <see cref="ReputationService"/> from spell handlers, which only see their <see cref="SpellSystem"/>
/// (the same registry pattern as the locomotion and combat hooks). The world feature registers the service once per spell
/// system; a system without one (a test, a world with no reputation) makes the reputation handlers do nothing.
/// </summary>
public static class ReputationEnvironment
{
    private static readonly ConditionalWeakTable<SpellSystem, ReputationService> s_services = new();

    public static void Register(SpellSystem system, ReputationService service)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(service);
        s_services.AddOrUpdate(system, service);
    }

    public static ReputationService? For(SpellSystem system) => s_services.TryGetValue(system, out ReputationService? service) ? service : null;
}
