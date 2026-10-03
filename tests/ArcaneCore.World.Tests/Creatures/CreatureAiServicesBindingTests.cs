using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary>
/// The creature AI services are bound from the container by reflection over the properties of
/// <see cref="CreatureAiServices"/>, so a later AI slice adds a seam without editing the creature feature.
/// </summary>
public sealed class CreatureAiServicesBindingTests
{
    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered) => CreatureCastResult.Ok;

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => false;

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    private sealed class FixedHostility : ICreatureHostility
    {
        public bool IsHostile(Creature creature, Unit target) => true;

        public bool CanAssist(Creature helper, Creature caller) => true;
    }

    private static IServiceProvider Container(Action<ServiceCollection>? configure = null)
    {
        var collection = new ServiceCollection();
        configure?.Invoke(collection);
        return collection.BuildServiceProvider();
    }

    [Fact]
    public void WithNothingRegistered_TheDefaultsApply()
    {
        CreatureAiServices services = CreatureAiServicesBinder.Build(Container(), new CreatureOptions());

        Assert.IsType<FactionCreatureHostility>(services.Hostility);
        Assert.Null(services.Spells);
        Assert.NotNull(services.Factory);
    }

    [Fact]
    public void ARegisteredSpellCaster_IsBound_EvenWithoutTheSpellFeature()
    {
        // Only a SpellFeature adapter used to be possible; any registered ICreatureSpellCaster is now bound.
        var caster = new RecordingCaster();
        CreatureAiServices services = CreatureAiServicesBinder.Build(Container(c => c.AddSingleton<ICreatureSpellCaster>(caster)), new CreatureOptions());

        Assert.Same(caster, services.Spells);
    }

    [Fact]
    public void RegisteredHostilityAndFactory_ReplaceTheDefaults()
    {
        var hostility = new FixedHostility();
        var factory = new CreatureAiFactory();
        CreatureAiServices services = CreatureAiServicesBinder.Build(
            Container(c => c.AddSingleton<ICreatureHostility>(hostility).AddSingleton(factory)), new CreatureOptions());

        Assert.Same(hostility, services.Hostility);
        Assert.Same(factory, services.Factory);
    }

    [Fact]
    public void ARegisteredServiceOfAnUnrelatedType_ChangesNothing()
    {
        CreatureAiServices services = CreatureAiServicesBinder.Build(Container(c => c.AddSingleton("unrelated")), new CreatureOptions());

        Assert.IsType<FactionCreatureHostility>(services.Hostility);
        Assert.Null(services.Spells);
    }
}
