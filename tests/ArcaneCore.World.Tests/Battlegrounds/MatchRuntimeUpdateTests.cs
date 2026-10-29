using ArcaneCore.Game.Combat;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.World.Tests.Battlegrounds.MatchRuntimeRig;

namespace ArcaneCore.World.Tests.Battlegrounds;

/// <summary>
/// What a match's own update (<c>MatchRuntime.Update</c>: due pending objects and events, the battleground buff traps, the spirit guides) does,
/// and that a quiet update costs nothing: the match runs on every world tick, with hundreds of creatures and objects on its map.
/// </summary>
public sealed class MatchRuntimeUpdateTests
{
    // ------------------------------------------------------------------ allocation

    [Fact]
    public void Update_SteadyState_WithFiveHundredCreaturesAndHundredObjects_AllocatesNothing()
    {
        using var rig = new MatchRuntimeRig();
        rig.BuildSteadyState(creatures: 500, objects: 100);

        AllocationProbe.AssertEveryCleanWindowAtMost(() => { for (int i = 0; i < 100; i++) rig.Match.Update(50); },
            0, "100 MatchRuntime.Update(50) with 500 creatures, 100 objects, a channelling spirit guide, a trap out of reach and pending objects not due");

        Assert.True(rig.Creatures.Creatures.Count > 500);
    }

    // ------------------------------------------------------------------ spirit guides (vmangos npc_spirit_guideAI::UpdateAI)

    [Fact]
    public void SpiritGuide_LivingAndNotChannelling_CastsSpiritHealChannelOnUpdate()
    {
        using var rig = new MatchRuntimeRig();
        Creature guide = rig.AddCreature(SpiritGuideCreature, 3f, 3f);
        Assert.False(rig.IsChanneling(guide));

        rig.Match.Update(50);

        Assert.True(rig.IsChanneling(guide));
        Assert.True(rig.Spells.System.HasAura(guide, SpiritHealChannel));
    }

    [Fact]
    public void SpiritGuide_AlreadyChannelling_IsNotCastAgain()
    {
        using var rig = new MatchRuntimeRig();
        Creature guide = rig.AddCreature(SpiritGuideCreature, 3f, 3f);
        int casts = 0;
        rig.Spells.System.HolderAdded += holder => casts += holder.Spell.Id == SpiritHealChannel ? 1 : 0;

        for (int i = 0; i < 5; i++)
        {
            rig.Match.Update(50);
        }

        Assert.Equal(1, casts);
        Assert.True(rig.IsChanneling(guide));
    }

    [Fact]
    public void SpiritGuide_Dead_IsNotCast_AndNeitherIsAnOrdinaryCreature()
    {
        using var rig = new MatchRuntimeRig();
        Creature dead = rig.AddCreature(SpiritGuideCreature, 3f, 3f);
        Creature ordinary = rig.AddCreature(PlainCreature, 4f, 4f);
        rig.Map.Combat.Kill(null, dead);
        Assert.False(dead.IsAlive);

        rig.Match.Update(50);

        Assert.False(rig.IsChanneling(dead));
        Assert.False(rig.IsChanneling(ordinary));
        Assert.False(rig.Spells.System.HasAura(ordinary, SpiritHealChannel));
    }

    // ------------------------------------------------------------------ buff traps (vmangos GameObject::Update, GameObject.cpp:476-551)

    [Fact]
    public void BuffTrap_FiresOnALivingPlayerInRange_AndLeavesTheObjectWhenThePlayerIsNotInTheMatch()
    {
        using var rig = new MatchRuntimeRig();
        GameObject trap = rig.AddObject(BuffTrap, 40f, 40f);
        Player player = rig.AddPlayer(41f, 40f);

        rig.Match.Update(100);

        Assert.True(rig.Spells.System.HasAura(player, BuffSpell));
        Assert.True(trap.IsSpawned);   // only a participant of the match uses the object up (GameObject.cpp:546-552)
    }

    [Fact]
    public void BuffTrap_Reach_IsThreeYardsCenterToCenter()
    {
        using var rig = new MatchRuntimeRig();
        rig.AddObject(BuffTrap, 40f, 40f);
        Player outside = rig.AddPlayer(43.1f, 40f);
        Player inside = rig.AddPlayer(37.1f, 40f);   // 2.9 yd

        rig.Match.Update(100);

        Assert.False(rig.Spells.System.HasAura(outside, BuffSpell));
        Assert.True(rig.Spells.System.HasAura(inside, BuffSpell));
    }

    [Fact]
    public void BuffTrap_IgnoresADeadPlayer_AndAPlayerOutOfRange_AndDoesNothingWithoutPlayers()
    {
        using var rig = new MatchRuntimeRig();
        GameObject trap = rig.AddObject(BuffTrap, 40f, 40f);
        rig.Match.Update(100);                       // no player on the map at all
        Assert.True(trap.IsSpawned);

        Player far = rig.AddPlayer(80f, 80f);
        Player dead = rig.AddPlayer(40f, 41f);
        rig.Map.Combat.KillPlayer(dead);
        Assert.False(dead.IsAlive);

        rig.Match.Update(100);

        Assert.False(rig.Spells.System.HasAura(far, BuffSpell));
        Assert.False(rig.Spells.System.HasAura(dead, BuffSpell));
        Assert.True(trap.IsSpawned);
    }

    [Fact]
    public void BuffTrap_RespectsItsThreeSecondCooldown()
    {
        using var rig = new MatchRuntimeRig();
        rig.AddObject(BuffTrap, 40f, 40f);
        Player player = rig.AddPlayer(41f, 40f);

        rig.Match.Update(100);                       // fires at 100 ms, cooling down until 3100 ms
        Assert.True(rig.Spells.System.HasAura(player, BuffSpell));

        rig.Spells.System.RemoveAuras(player, BuffSpell, AuraRemoveMode.Default);
        rig.Match.Update(2000);                      // 2100 ms
        Assert.False(rig.Spells.System.HasAura(player, BuffSpell));
        rig.Match.Update(1000);                      // 3100 ms: the cooldown holds up to and including its end
        Assert.False(rig.Spells.System.HasAura(player, BuffSpell));

        rig.Match.Update(1);                         // 3101 ms
        Assert.True(rig.Spells.System.HasAura(player, BuffSpell));
    }

    [Fact]
    public void BuffTrap_OfAnotherKind_IsNeverTriggered()
    {
        using var rig = new MatchRuntimeRig();
        rig.AddObject(BuffObject, 40f, 40f);          // not a trap
        rig.AddObject(PlainObject, 40f, 41f);
        Player player = rig.AddPlayer(40f, 40f);

        rig.Match.Update(100);

        Assert.False(rig.Spells.System.HasAura(player, BuffSpell));
    }

    // ------------------------------------------------------------------ pending match objects

    [Fact]
    public void SpawnBattlegroundObject_WithRespawnSeconds_SummonsItAtTheDueTick_NotBefore()
    {
        using var rig = new MatchRuntimeRig();
        rig.Match.AddBattlegroundObject(7, BuffObject, 20f, 20f, 10f, 0f);
        rig.Match.SpawnBattlegroundObject(7, 10);
        int Live() => rig.Objects.GameObjects.Count(go => go.Entry == BuffObject);

        Assert.Equal(0, Live());
        rig.Match.Update(9_999);
        Assert.Equal(0, Live());

        rig.Match.Update(1);
        Assert.Equal(1, Live());

        rig.Match.Update(60_000);                     // not summoned a second time
        Assert.Equal(1, Live());
    }

    [Fact]
    public void SpawnBattlegroundObject_AgainBeforeTheDueTick_ReplacesThePendingOne()
    {
        using var rig = new MatchRuntimeRig();
        rig.Match.AddBattlegroundObject(7, BuffObject, 20f, 20f, 10f, 0f);
        int Live() => rig.Objects.GameObjects.Count(go => go.Entry == BuffObject);

        rig.Match.SpawnBattlegroundObject(7, 10);
        rig.Match.Update(5_000);
        rig.Match.SpawnBattlegroundObject(7, 10);     // due at 15 000 now, the first (10 000) is gone
        rig.Match.Update(5_000);                      // 10 000
        Assert.Equal(0, Live());
        rig.Match.Update(5_000);                      // 15 000
        Assert.Equal(1, Live());
    }

    [Fact]
    public void SpawnBattlegroundObject_TwoDueAtTheSameTick_BothAppear_AndZeroSecondsIsImmediate()
    {
        using var rig = new MatchRuntimeRig();
        rig.Match.AddBattlegroundObject(1, BuffObject, 20f, 20f, 10f, 0f);
        rig.Match.AddBattlegroundObject(2, BuffObject, 22f, 20f, 10f, 0f);
        rig.Match.AddBattlegroundObject(3, BuffObject, 24f, 20f, 10f, 0f);
        rig.Match.SpawnBattlegroundObject(1, 4);
        rig.Match.SpawnBattlegroundObject(2, 4);
        rig.Match.SpawnBattlegroundObject(3, 0);
        int Live() => rig.Objects.GameObjects.Count(go => go.Entry == BuffObject);

        Assert.Equal(1, Live());
        rig.Match.Update(3_999);
        Assert.Equal(1, Live());
        rig.Match.Update(1);
        Assert.Equal(3, Live());
    }
}
