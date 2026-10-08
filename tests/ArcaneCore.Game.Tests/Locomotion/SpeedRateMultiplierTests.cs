using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// The configured player speed rates (<c>Locomotion:Player*SpeedRate</c>; the MaNGOS Zero fork's Movement.PlayerSpeedRate and per-type rates,
/// UnitSpeed.cpp:162-184): applied where the speed is set, so the aura recomputation and its force orders carry them, creatures do not.
/// </summary>
public sealed class SpeedRateMultiplierTests
{
    private const uint GhostWolf = 951001; // aura 31, +40

    private static SpellTestKit Kit() => new(Spell(GhostWolf, Effect(SpellEffectName.ApplyAura, 40, aura: AuraType.ModIncreaseSpeed)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
    });

    private static float Speed(byte[] order) => BitConverter.ToSingle(order, order.Length - 4);

    [Fact]
    public void ASpeedAura_IsMultipliedByTheConfiguredRate_InTheForceOrder()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.Locomotion.ConfiguredSpeedRates = PlayerSpeedRates.Retail with { Run = 2.0f };

        kit.System.CastSpell(player, GhostWolf, SpellCastTargets.ForSelf(), triggered: true);

        byte[] order = Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgForceRunSpeedChange).Payload;
        Assert.Equal(7.0f * 1.4f * 2.0f, Speed(order), 4);
    }

    [Fact]
    public void Apply_ReSendsOnlyTheTypesThatChange_IncludingSwimBackAndTheTurnRate()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        var options = new LocomotionOptions { PlayerSwimBackSpeedRate = 2.0f, PlayerTurnRate = 3.0f };

        SpeedRates.Apply(player, options.GetSpeedRates(), options);

        Assert.Equal(2.5f * 2.0f, Speed(Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgForceSwimBackSpeedChange).Payload));
        Assert.Equal(Unit.BaseTurnRate * 3.0f, Speed(Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgForceTurnRateChange).Payload));
        Assert.Equal(Unit.BaseTurnRate * 3.0f, player.TurnRate);
        Assert.DoesNotContain(session.Sent, p => p.Opcode is WorldOpcode.SmsgForceRunSpeedChange or WorldOpcode.SmsgForceWalkSpeedChange
            or WorldOpcode.SmsgForceSwimSpeedChange or WorldOpcode.SmsgForceRunBackSpeedChange);
        Assert.Equal(2.5f, player.SwimBackSpeed); // the speeds still wait for the ack

        session.Clear();
        SpeedRates.Apply(player, options.GetSpeedRates(), options);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgForceTurnRateChange); // nothing new for the turn rate
    }

    [Fact]
    public void ThePlayerSpeedRate_MultipliesEveryPerTypeRate_ButNotTheTurnRate()
    {
        var options = new LocomotionOptions { PlayerSpeedRate = 2.0f, PlayerRunSpeedRate = 1.5f, PlayerWalkSpeedRate = 0.5f };

        PlayerSpeedRates rates = options.GetSpeedRates();

        Assert.Equal(new PlayerSpeedRates(Walk: 1.0f, Run: 3.0f, RunBack: 2.0f, Swim: 2.0f, SwimBack: 2.0f, Turn: 1.0f), rates);
        Assert.Equal(PlayerSpeedRates.Retail, new LocomotionOptions().GetSpeedRates());
    }

    [Fact]
    public void Normalize_ClampsTheRatesToTheForksRange()
    {
        var options = new LocomotionOptions { PlayerSpeedRate = 0.0f, PlayerRunSpeedRate = 50.0f, PlayerTurnRate = float.NaN };

        IReadOnlyList<string> corrected = options.Normalize();

        Assert.Equal(0.1f, options.PlayerSpeedRate);
        Assert.Equal(10.0f, options.PlayerRunSpeedRate);
        Assert.Equal(1.0f, options.PlayerTurnRate);
        Assert.Equal(["PlayerSpeedRate", "PlayerRunSpeedRate", "PlayerTurnRate"], corrected);
    }

    [Fact]
    public void ACreature_KeepsItsSpeeds_WhateverThePlayerRates()
    {
        CreatureTemplate template = Template();
        CreatureContent content = Content([template], [Spawn(1, template.Entry, 30, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.Locomotion.ConfiguredSpeedRates = PlayerSpeedRates.Retail with { Run = 5.0f }; // never set on a creature; ignored if it were

        wolf.Locomotion.Auras.Add(new SpellAura(0, AuraType.ModDecreaseSpeed, -50, 0, 0));
        UnitSpeed.UpdateSpeed(wolf, MoveType.Run);

        Assert.Equal(7.0f * 0.5f * Creature.DefaultRunSpeedRate, wolf.RunSpeed, 4);
    }
}
