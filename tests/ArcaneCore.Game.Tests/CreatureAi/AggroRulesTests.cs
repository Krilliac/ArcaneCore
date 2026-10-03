using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// vmangos proximity aggro: the detection-range formula, plain distances, the vertical limit, the react states, the
/// post-respawn pacify, the AI relocation notify that drives it, and SMSG_AI_REACTION. Expected values come from the
/// vmangos code cited on each test (D:\refs\vmangos\src\game), not from the previous implementation.
/// </summary>
public sealed class AggroRulesTests
{
    private sealed record Setup(WorldRuntime World, Map Map, CreatureMapSystem System, Player Player, FakeSession Session, Creature Wolf) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Setup Start(CreatureTemplate? template = null, float wolfX = 8, float wolfY = 0, float wolfZ = 83.5f, CreatureOptions? options = null,
        float playerX = 0, byte playerLevel = 0)
    {
        CreatureContent content = Content([template ?? Template()], [Spawn(1, WolfEntry, wolfX, wolfY, wolfZ)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() }, options);
        (Player player, FakeSession session) = AddPlayer(runtime, 1, playerX, 0);
        if (playerLevel != 0)
        {
            player.Level = playerLevel;
        }

        return new Setup(runtime, map, system, player, session, Assert.Single(system.Creatures));
    }

    // --- the formula -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(18f, 10, 10, 1f, 18f)]
    [InlineData(18f, 10, 13, 1f, 15f)]    // target above: one yard per level less
    [InlineData(18f, 10, 7, 1f, 21f)]     // target below: one yard per level more
    [InlineData(18f, 60, 1, 1f, 43f)]     // at most 25 levels counted below
    [InlineData(18f, 10, 40, 1f, 5f)]     // floor min(detection, 5)
    [InlineData(10f, 10, 10, 1f, 10f)]    // the template's own detection range
    [InlineData(10f, 10, 30, 1f, 5f)]
    [InlineData(3f, 10, 10, 1f, 3f)]      // a detection range below 5 is its own floor
    [InlineData(3f, 10, 40, 1f, 3f)]
    [InlineData(18f, 10, 10, 0.5f, 9f)]   // Rate.Creature.Aggro scales the result
    [InlineData(18f, 10, 10, 2f, 36f)]
    [InlineData(0.5f, 10, 10, 1f, 0f)]    // detection under 1: no proximity aggro (Creature.cpp:2214-2216)
    [InlineData(18f, 10, 10, 0f, 0f)]     // rate 0: none (Creature.cpp:2195-2197)
    public void AttackDistance_FollowsTheVmangosFormula(float detection, int creatureLevel, int targetLevel, float rate, float expected)
        => Assert.Equal(expected, CreatureAggro.GetAttackDistance(detection, creatureLevel, targetLevel, rate), 3);

    [Fact]
    public void TheTemplatesDetectionRange_IsTheBaseOfTheRadius()
    {
        using Setup s = Start(Template() with { Detection = 10f }, playerLevel: 2);

        Assert.Equal(10f, s.System.GetAttackDistance(s.Wolf, s.Player), 3); // wolf level 2, player level 2
    }

    // --- distances -------------------------------------------------------------------------------------

    [Fact]
    public void TheRadiusIsMeasuredWithoutBoundingRadii_ButTheDevSwitchRestoresThem()
    {
        // Level 2 wolf, level 2 player: radius 18. 18.4 yd is outside the plain range (vmangos IsWithinDistInMap with
        // SizeFactor::None, AI/BasicAI.cpp:61-67) but inside range + both bounding radii.
        using Setup retail = Start(wolfX: 18.4f, playerLevel: 2);
        Assert.False(retail.System.CanAggroOnSight(retail.Wolf, retail.Player));

        var dev = new CreatureOptions { AggroUsesBoundingRadius = true };
        using Setup withRadii = Start(wolfX: 18.4f, options: dev, playerLevel: 2);
        Assert.True(withRadii.System.CanAggroOnSight(withRadii.Wolf, withRadii.Player));

        using Setup inside = Start(wolfX: 17.9f, playerLevel: 2);
        Assert.True(inside.System.CanAggroOnSight(inside.Wolf, inside.Player));
    }

    [Fact]
    public void TheVerticalLimit_TakesTheBoundingRadiiOff_AndFlyersAreExempt()
    {
        // GetDistanceZ defaults to SizeFactor::BoundingRadius (Objects/Object.h:589): |dz| - radii must not exceed 3 yd.
        using Setup near = Start(wolfX: 5, wolfZ: 83.5f + 3.5f);
        Assert.True(near.System.CanAggroOnSight(near.Wolf, near.Player));

        using Setup far = Start(wolfX: 5, wolfZ: 83.5f + 4.5f);
        Assert.False(far.System.CanAggroOnSight(far.Wolf, far.Player));

        using Setup flyer = Start(Template() with { InhabitType = 4 }, wolfX: 5, wolfZ: 83.5f + 10f);
        Assert.True(flyer.System.CanAggroOnSight(flyer.Wolf, flyer.Player));
    }

    // --- react states ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0x02u, 0u, 7u, CreatureReactState.Defensive)]       // NO_AGGRO extra flag
    [InlineData(0u, 0x02000000u, 7u, CreatureReactState.Passive)]    // IGNORE_COMBAT static flag
    [InlineData(0u, 0u, 11u, CreatureReactState.Passive)]            // totem
    [InlineData(0x80u, 0u, 7u, CreatureReactState.Passive)]          // trigger (invisible)
    [InlineData(0u, 0u, 7u, CreatureReactState.Aggressive)]
    public void TheInitialReactState_FollowsInitializeReactState(uint extraFlags, uint staticFlags, uint creatureType, CreatureReactState expected)
    {
        // vmangos Creature::InitializeReactState (Objects/Creature.cpp:714-722).
        using Setup s = Start(Template() with { ExtraFlags = extraFlags, StaticFlags1 = staticFlags, CreatureType = creatureType });

        Assert.Equal(expected, s.Wolf.ReactState);
    }

    [Fact]
    public void ADefensiveCreature_DoesNotAggroOnSight_ButFightsBack_AndAPassiveOneDoesNeither()
    {
        using Setup defensive = Start(Template() with { ExtraFlags = 0x02 });
        Run(defensive.World, 1500);
        Assert.Null(defensive.Wolf.Combat.Victim);
        defensive.Map.Combat.DealDamage(defensive.Player, defensive.Wolf, 1, direct: false);
        Assert.Same(defensive.Player, defensive.Wolf.Combat.Victim);

        using Setup passive = Start(Template() with { StaticFlags1 = 0x02000000 });
        Run(passive.World, 1500);
        Assert.Null(passive.Wolf.Combat.Victim);
        passive.Map.Combat.DealDamage(passive.Player, passive.Wolf, 1, direct: false);
        Assert.Null(passive.Wolf.Combat.Victim); // CreatureAI::AttackStart refuses while passive (AI/CreatureAI.cpp:67-70)
    }

    // --- the relocation notify -----------------------------------------------------------------------------

    [Fact]
    public void AggroComesWithTheRelocationNotify_AfterTheDelay_NotBefore()
    {
        using Setup s = Start();

        Run(s.World, 900);
        Assert.Null(s.Wolf.Combat.Victim);

        Run(s.World, 200);
        Assert.Same(s.Player, s.Wolf.Combat.Victim);
    }

    [Fact]
    public void PollMode_ChecksEveryTick_LikeTheOriginalImplementation()
    {
        using Setup s = Start(options: new CreatureOptions { AggroScanMode = AggroScanMode.Poll });

        s.World.RunTick(50);

        Assert.Same(s.Player, s.Wolf.Combat.Victim);
    }

    [Fact]
    public void AStationaryPlayerAndMob_TriggerNoFurtherScan_UntilSomeoneMoves()
    {
        // After a respawn the creature is pacified for 5 s. Nobody moves while it counts down, so no notify runs after the
        // pacify ends: the player is not attacked until it moves (vmangos OnRelocated only schedules on movement).
        using Setup s = Start();
        Run(s.World, 1500);
        Assert.Same(s.Player, s.Wolf.Combat.Victim);
        s.System.EnterEvadeMode(s.Wolf);
        s.Map.Combat.Kill(null, s.Wolf);
        Run(s.World, 400);
        s.System.ForceRespawn(s.Wolf);
        Assert.True(s.Wolf.IsTempPacified);

        Run(s.World, 8000);
        Assert.False(s.Wolf.IsTempPacified);
        Assert.Null(s.Wolf.Combat.Victim); // nobody moved since the respawn notify (it ran while pacified)

        s.Player.Relocate(0.5f, 0, 83.5f, 0, 0);
        Run(s.World, 1200);
        Assert.Same(s.Player, s.Wolf.Combat.Victim);
    }

    [Fact]
    public void AMovingCreature_NotifiesThePlayersAroundIt()
    {
        // Wolf 30 yd away walks to 12 yd from a stationary player. The player never moves; the creature's own movement drives
        // the notify (CreatureRelocationNotifier, Maps/GridNotifiersImpl.h:90-102).
        using Setup s = Start(wolfX: 30);
        Run(s.World, 1500); // the join-time notify found the player out of range (30 > 18)
        Assert.Null(s.Wolf.Combat.Victim);

        s.System.MoveTo(s.Wolf, 12, 0, 83.5f, run: false, null);
        Run(s.World, 6000);

        Assert.Same(s.Player, s.Wolf.Combat.Victim);
    }

    // --- the respawn pacify --------------------------------------------------------------------------------

    [Fact]
    public void ARespawnedCreature_CannotInitiateAnAttackForFiveSeconds()
    {
        using Setup s = Start();
        s.Map.Combat.Kill(null, s.Wolf);
        s.System.ForceRespawn(s.Wolf);

        // The respawn notify at +1000 ms finds it pacified; a moving player re-notifies it at 3 s (still pacified) and at 6 s.
        Assert.False(s.System.CanInitiateAttack(s.Wolf));
        Assert.Equal(5000u, s.Wolf.PacifiedMs);
        Run(s.World, 2000);
        s.Player.Relocate(0.2f, 0, 83.5f, 0, 0);
        Run(s.World, 1500);
        Assert.Null(s.Wolf.Combat.Victim);
        Assert.True(s.Wolf.IsTempPacified);

        Run(s.World, 2000);
        Assert.False(s.Wolf.IsTempPacified);
        s.Player.Relocate(0.4f, 0, 83.5f, 0, 0);
        Run(s.World, 1200);
        Assert.Same(s.Player, s.Wolf.Combat.Victim);
    }

    [Fact]
    public void TheRespawnPacify_IsConfigurable_AndZeroRestoresImmediateEligibility()
    {
        var options = new CreatureOptions { RespawnPacifyMs = 0 };
        using Setup s = Start(options: options);
        s.Map.Combat.Kill(null, s.Wolf);
        s.System.ForceRespawn(s.Wolf);

        Assert.True(s.System.CanInitiateAttack(s.Wolf));
    }

    // --- picking up further targets ---------------------------------------------------------------------------

    [Fact]
    public void ACreatureWithAVictim_OnlyAddsFurtherTargetsToItsThreatInInstanceableMaps()
    {
        // BasicAI::IsProximityAggroAllowedFor (AI/BasicAI.cpp:30-47) + Creature::EnterCombatWithTarget (Creature.cpp:4078-4087).
        Setup Fight(bool instanceable)
        {
            Setup s = Start();
            if (instanceable)
            {
                s.Map.Combat.Hooks = new InstanceHooks();
            }

            Run(s.World, 1500);
            Assert.Same(s.Player, s.Wolf.Combat.Victim);
            return s;
        }

        foreach (bool instanceable in new[] { false, true })
        {
            using Setup s = Fight(instanceable);
            (Player second, _) = AddPlayer(s.World, 2, 1, 0);
            Run(s.World, 1500);

            bool threatened = s.Wolf.Combat.Threat.Entries.Any(e => ReferenceEquals(e.Target, second));
            Assert.Equal(instanceable, threatened);
            Assert.Same(s.Player, s.Wolf.Combat.Victim);
        }
    }

    private sealed class InstanceHooks : CombatHooks
    {
        public override bool IsInstanceable(uint mapId) => true;
    }

    // --- SMSG_AI_REACTION ---------------------------------------------------------------------------------------

    [Fact]
    public void EveryAttackStart_SendsOneHostileAiReaction_BeforeTheFirstSwing()
    {
        // vmangos Unit::Attack sends AI_REACTION_HOSTILE for a creature (Objects/Unit.cpp:4535-4537); layout from gtker
        // wow_messages smsg_ai_reaction.wowm: guid, u32 reaction (2 = hostile, the aggro sound).
        using Setup s = Start(Template() with { MinMeleeDamage = 5, MaxMeleeDamage = 5 });
        Run(s.World, 3000);

        List<byte[]> reactions = Packets(s.Session, WorldOpcode.SmsgAiReaction);
        byte[] packet = Assert.Single(reactions);
        var reader = new PacketReader(packet);
        Assert.Equal(s.Wolf.Guid.Value, reader.ReadUInt64());
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);

        int reactionIndex = s.Session.Sent.ToList().FindIndex(p => p.Opcode == WorldOpcode.SmsgAiReaction);
        int swingIndex = s.Session.Sent.ToList().FindIndex(p => p.Opcode == WorldOpcode.SmsgAttackerstateupdate);
        Assert.True(swingIndex < 0 || reactionIndex < swingIndex);
    }

    [Fact]
    public void TheAiReaction_CanBeSwitchedOff()
    {
        using Setup s = Start(options: new CreatureOptions { SendAiReaction = false });
        Run(s.World, 2000);

        Assert.Same(s.Player, s.Wolf.Combat.Victim);
        Assert.Empty(Packets(s.Session, WorldOpcode.SmsgAiReaction));
    }
}
