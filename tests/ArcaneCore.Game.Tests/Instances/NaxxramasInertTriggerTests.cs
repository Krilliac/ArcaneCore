using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>spell_anchor (16137) and npc_invible_man (17286): inert trigger scripts (specs 533-spell_anchor, 533-npc_invible_man).</summary>
public sealed class NaxxramasInertTriggerTests
{
    private const uint Anchor = 16137, InvisibleMan = 17286;

    private static CreatureTemplate TriggerTemplate(uint entry) => Template(entry, t =>
    {
        t.Name = entry == Anchor ? "Naxxramas Military Sub-Boss Trigger" : "Invisible Man";
        t.Faction = 114;
        t.MinLevel = t.MaxLevel = 60;
        t.UnitFlags = entry == Anchor ? 33554432u : 33816576u;
    }) with { ScriptName = entry == Anchor ? GothikAnchorAI.ScriptName : InvisibleManAI.ScriptName };

    private static DungeonNpcRig Rig(params uint[] entries)
        => new(map => new NaxxramasInstance(map), entries.Distinct().Select(TriggerTemplate),
            entries.Select((e, i) => Spawn((uint)(100 + i), e, DungeonNpcRig.EntranceX + 2 + i, DungeonNpcRig.EntranceY, DungeonNpcRig.EntranceZ, InstanceFixture.Dungeon)));

    [Fact]
    public void BothEntries_ResolveAsScriptNameAis()
    {
        var content = new CreatureContent([TriggerTemplate(Anchor), TriggerTemplate(InvisibleMan)], [], [], [], []);
        var catalog = new ScriptCatalog(new CreatureAiFactory(), content);
        Assert.True(catalog.Resolve(Anchor, 533).ScriptNameResolved);
        Assert.True(catalog.Resolve(InvisibleMan, 533).ScriptNameResolved);
        Assert.Equal(ScriptKind.ScriptName, catalog.Resolve(Anchor, 533).Kind);
    }

    [Fact]
    public void ATriggerNextToAHostilePlayer_NeverFightsMovesOrCasts_AndStaysFindableByEntry()
    {
        using DungeonNpcRig rig = Rig(Anchor, InvisibleMan);
        Creature anchor = rig.Creature(Anchor), man = rig.Creature(InvisibleMan);
        Assert.IsType<GothikAnchorAI>(anchor.AI);
        Assert.IsType<InvisibleManAI>(man.AI);
        (float x, float y, float z)[] before = [(anchor.X, anchor.Y, anchor.Z), (man.X, man.Y, man.Z)];

        rig.Run(30_000);

        foreach ((Creature c, int i) in new[] { (anchor, 0), (man, 1) })
        {
            Assert.Null(c.Combat.Victim);
            Assert.False(c.Combat.IsInCombat);
            Assert.Equal(before[i], (c.X, c.Y, c.Z));
            Assert.NotEqual(UnitFlags.None, c.UnitFlags & UnitFlags.NotSelectable);
            Assert.True(c.AiImmobilized);
            Assert.DoesNotContain(rig.Spells.Casts, x => ReferenceEquals(x.Caster, c));
            Assert.False(c.AI!.AttackStart(rig.Player));
            c.AI.OnAttackedBy(rig.Player);
            Assert.Null(c.Combat.Victim);
        }

        Assert.Single(rig.Creatures.Creatures, c => c.Entry == Anchor); // the instance queries creatures by entry
        Assert.Single(rig.Creatures.Creatures, c => c.Entry == InvisibleMan);
    }

    [Fact]
    public void ATrigger_StillTakesPartInTheMapAfterARespawn()
    {
        using DungeonNpcRig rig = Rig(Anchor);
        Creature anchor = rig.Creature(Anchor);
        rig.Map.Combat.Kill(rig.Player, anchor);
        rig.Creatures.ForceRespawn(anchor);
        rig.Run(200);
        Assert.True(anchor.IsAlive);
        Assert.True(anchor.AiImmobilized);
        Assert.NotEqual(UnitFlags.None, anchor.UnitFlags & UnitFlags.NotSelectable);
    }
}
