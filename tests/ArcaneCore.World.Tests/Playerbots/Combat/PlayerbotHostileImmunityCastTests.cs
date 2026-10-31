using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;
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

    private sealed class FixedMechanicImmunity(uint mechanicMask) : ICreatureImmunityProvider
    {
        public uint MechanicImmuneMask(Unit unit) => mechanicMask;

        public uint SchoolImmuneMask(Unit unit) => 0;
    }

    private static SpellInfo Spell(uint id, string name, params SpellEffectInfo[] effects) => new()
    {
        Id = id,
        Name = name,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        Effects = effects,
    };

    private static async Task RunAgainstAsync(bool asTotem, Action<SpellFeature, Player, RotationState> body, uint? mechanicMask = null)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = null!;
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                target = Spawn(host, player, 8, 900041);
                if (asTotem) TotemQuery.Add(new TotemInfo(target, player, TotemSlot.Earth, 0, 0, -1));
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(target.Guid), "target visible");
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                if (mechanicMask is { } mask) feature.System.CreatureImmunities = new FixedMechanicImmunity(mask);
                RotationState state = PlayerbotCombatView.Build(session, feature, player, target, PlayerbotAbilities.Empty, PlayerbotRole.MeleeDps, static _ => false);
                body(feature, player, state);
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task ABot_SkipsRend_OnACreatureImmuneToBleed_ButNotAnUnrelatedMechanic()
    {
        SpellInfo rend = DamageOverTime(900_821, "Rend") with { Mechanic = (uint)SpellMechanic.Bleed };
        SpellInfo sting = DamageOverTime(900_822, "Serpent Sting");
        await RunAgainstAsync(false, (_, _, state) =>
        {
            Assert.False(state.CanCast(rend, state.Victim!));
            Assert.True(state.CanCast(sting, state.Victim!));
        }, SpellMechanics.Mask(SpellMechanic.Bleed));
    }

    [Fact]
    public async Task ABot_CastsASpellOnATotem_WhenOneEffectLands()
    {
        SpellInfo strike = Spell(900_831, "Damage and curse",
            new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, BasePoints = 10, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitEnemy },
            new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.PeriodicDamage, TargetA = SpellImplicitTarget.UnitEnemy, Amplitude = 3000 });
        await RunAgainstAsync(true, (_, _, state) => Assert.True(state.CanCast(strike, state.Victim!)));
    }

    [Fact]
    public async Task ABot_CastsASpellOnATotem_WhenAnotherEffectIsAimedAtTheCaster()
    {
        SpellInfo drawback = Spell(900_841, "Aura with a self drawback",
            new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.PeriodicDamage, TargetA = SpellImplicitTarget.UnitEnemy, Amplitude = 3000 },
            new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.PeriodicDamage, TargetA = SpellImplicitTarget.UnitCaster, Amplitude = 3000 });
        await RunAgainstAsync(true, (_, _, state) => Assert.True(state.CanCast(drawback, state.Victim!)));
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
