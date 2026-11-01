using ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Tests.CreatureAi;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.AvScriptRig;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>The Alterac Valley tower bowmen (vmangos npc_AlteracBowman, battleground_alterac.cpp:1151-1230) on the manual world clock.</summary>
public sealed class AvBowmanTests
{
    private const float BowmanX = 100f;
    private const float BowmanY = 100f;

    private static AvScriptRig Rig(uint entry, float playerDistance)
        => Create([AvNpc(entry, b => b.Faction = 14)], [Spawn(1, entry, BowmanX, BowmanY, Z)], BowmanX + playerDistance, BowmanY,
            hostility: new AlwaysHostile());

    [Theory]
    [InlineData(AvBowmanAI.EntryStormpikeBowman)]
    [InlineData(AvBowmanAI.EntryFrostwolfBowman)]
    public void Bowman_RootsItself_AndShootsATargetAt70YardsInSight(uint entry)
    {
        using AvScriptRig rig = Rig(entry, 70f);
        Creature bowman = rig.Single(entry);
        Assert.IsType<AvBowmanAI>(bowman.AI);
        rig.Run(200);
        Assert.Contains(rig.Caster.AddedAuras, a => ReferenceEquals(a.Unit, bowman) && a.Spell == AvBowmanAI.SpellRootSelf && a.Permanent);

        bowman.AI!.MoveInLineOfSight(rig.Player);
        Assert.True(bowman.Combat.IsInCombat);
        Assert.Same(rig.Player, bowman.Combat.Victim);

        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == AvBowmanAI.SpellShoot), "Shoot", 1000);
        Assert.Contains(rig.Caster.Casts, c => c.Spell == AvBowmanAI.SpellShoot && ReferenceEquals(c.Target, rig.Player));
        Assert.Equal(BowmanX, bowman.X);
        Assert.Equal(BowmanY, bowman.Y);
    }

    [Fact]
    public void Bowman_ShootsEvery1700Ms()
    {
        using AvScriptRig rig = Rig(AvBowmanAI.EntryStormpikeBowman, 70f);
        Creature bowman = rig.Single(AvBowmanAI.EntryStormpikeBowman);
        rig.Run(200);
        bowman.AI!.MoveInLineOfSight(rig.Player);

        rig.Run(3000);
        int shots = rig.Caster.Casts.Count(c => c.Spell == AvBowmanAI.SpellShoot);
        Assert.InRange(shots, 2, 3);
    }

    [Fact]
    public void Bowman_IgnoresATargetBeyond80Yards()
    {
        using AvScriptRig rig = Rig(AvBowmanAI.EntryStormpikeBowman, 90f);
        Creature bowman = rig.Single(AvBowmanAI.EntryStormpikeBowman);
        rig.Run(200);

        bowman.AI!.MoveInLineOfSight(rig.Player);

        Assert.False(bowman.Combat.IsInCombat);
    }

    [Fact]
    public void Bowman_ResetsThreeSecondsAfterLosingTheTarget()
    {
        using AvScriptRig rig = Rig(AvBowmanAI.EntryFrostwolfBowman, 70f);
        Creature bowman = rig.Single(AvBowmanAI.EntryFrostwolfBowman);
        rig.Run(200);
        bowman.AI!.MoveInLineOfSight(rig.Player);
        Assert.True(bowman.Combat.IsInCombat);

        rig.Player.Relocate(BowmanX + 95f, BowmanY, Z, 0, 0);
        rig.Run(2000);
        Assert.True(bowman.Combat.IsInCombat);

        rig.Run(1500);
        Assert.False(bowman.Combat.IsInCombat);
    }
}
