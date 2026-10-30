using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// The view's CanCast asks the immunity rules for hostile spells too (vmangos CanTryToCastSpell -> IsImmuneToSpell): a summoned totem is
/// immune to every negative aura, so a bot does not spend a Serpent Sting or a Rend on it.
/// </summary>
public sealed class PlayerbotHostileImmunityCastTests
{
    private static SpellInfo DamageOverTime(uint id, string name) => new()
    {
        Id = id,
        Name = name,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        Effects =
        [
            new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.PeriodicDamage, TargetA = SpellImplicitTarget.UnitEnemy, Amplitude = 3000 },
        ],
    };

    [Fact]
    public async Task ABot_DoesNotCastAHostileAuraOnATotem_ButDoesOnAnOrdinaryCreature()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature ordinary = null!, totem = null!;
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                (ordinary, totem) = (Spawn(host, player, 8, 900031), Spawn(host, player, 10, 900032));
                TotemQuery.Add(new TotemInfo(totem, player, TotemSlot.Earth, 0, 0, -1));
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(ordinary.Guid) && session.Player.VisibleObjects.Contains(totem.Guid), "targets visible");
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellInfo sting = DamageOverTime(900_811, "Serpent Sting"), rend = DamageOverTime(900_812, "Rend");

                RotationState onOrdinary = PlayerbotCombatView.Build(session, feature, player, ordinary, PlayerbotAbilities.Empty, PlayerbotRole.MeleeDps, static _ => false);
                Assert.True(onOrdinary.CanCast(sting, onOrdinary.Victim!));
                Assert.True(onOrdinary.CanCast(rend, onOrdinary.Victim!));

                RotationState onTotem = PlayerbotCombatView.Build(session, feature, player, totem, PlayerbotAbilities.Empty, PlayerbotRole.MeleeDps, static _ => false);
                Assert.False(onTotem.CanCast(sting, onTotem.Victim!));
                Assert.False(onTotem.CanCast(rend, onTotem.Victim!));
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private static Creature Spawn(WorldTestHost host, Player player, float xOffset, uint low)
    {
        var creature = new Creature(low, new CreatureTemplate
        {
            Entry = 900_000 + low % 1000,
            Name = "immunity target",
            CreatureType = 1,
            MinLevelHealth = 20,
            MaxLevelHealth = 20,
        }, null, new CreatureContent([], [], [], [], []), new Random(1));
        creature.Relocate(player.X + xOffset, player.Y, player.Z, 0, host.World.NowMs);
        player.Map!.AddObject(creature);
        return creature;
    }
}
