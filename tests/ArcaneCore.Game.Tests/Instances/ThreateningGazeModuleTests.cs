using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.ZulGurub;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// mangos-classic boss_mandokir.cpp ThreateningGaze AuraScript (OnApply/OnRemove send AI_EVENT_CUSTOM_A/B to the caster): the real aura
/// holder going on and off a player reaches the casting creature's AI through the discovered <see cref="ThreateningGazeAuraModule"/>.
/// </summary>
public sealed class ThreateningGazeModuleTests
{
    private sealed class RecordingAI(Creature creature) : CreatureAI(creature)
    {
        public List<(uint Event, Unit? Invoker)> Events { get; } = [];

        public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue) => Events.Add((eventType, invoker));
    }

    private const uint OtherAura = 941501;

    private static SpellInfo Aura(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
    {
        Duration = new SpellDuration(6000, 0, 6000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    [Fact]
    public void GazeHolderAddedAndRemoved_SendsTheCasterAiBothEventsWithThePlayer_AndOtherAurasDoNot()
    {
        using var kit = new PetTestKit([Aura(ThreateningGazeAuraModule.ThreateningGaze), Aura(OtherAura)]);
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        Creature caster = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(PetTestKit.NpcCasterEntry)!, 6, 5, 0, 0);
        var ai = new RecordingAI(caster);
        caster.AI = ai;

        kit.Spells.System.CastSpell(caster, OtherAura, SpellCastTargets.ForUnit(player.Guid), triggered: true);
        kit.Spells.System.RemoveAuras(player, OtherAura);
        Assert.Empty(ai.Events);

        kit.Spells.System.CastSpell(caster, ThreateningGazeAuraModule.ThreateningGaze, SpellCastTargets.ForUnit(player.Guid), triggered: true);
        Assert.True(kit.Spells.System.HasAura(player, ThreateningGazeAuraModule.ThreateningGaze));
        Assert.Equal([(MandokirAI.AiEventGazeApplied, (Unit?)player)], ai.Events);

        kit.Spells.System.RemoveAuras(player, ThreateningGazeAuraModule.ThreateningGaze);
        Assert.Equal([(MandokirAI.AiEventGazeApplied, (Unit?)player), (MandokirAI.AiEventGazeRemoved, (Unit?)player)], ai.Events);
    }
}
