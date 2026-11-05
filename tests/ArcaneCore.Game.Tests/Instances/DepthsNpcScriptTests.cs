using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackrockDepths;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// npc_theldren_trigger (16079) and boss_doomrel (9039) of Blackrock Depths (map 230), specs 230-npc_theldren_trigger and 230-boss_doomrel:
/// registration through <see cref="CreatureAiFactory"/> and <see cref="ScriptCatalog"/>, the inert trigger, and the Doom'rel EventAI host with
/// its voidwalker hook. These run by default (no world fixture needed). The rig builds each AI through the real factory registrations.
/// </summary>
public sealed class DepthsNpcScriptTests
{
    private const uint Theldren = 16079, Doomrel = 9039, Voidwalker = 8996, OtherSummon = 9999;
    private const uint SummonCastSpell = 77_001, AggroCastSpell = 15092;
    private const uint BrdMap = 230;

    private sealed class Plain(Map map) : InstanceData(map);

    private static CreatureTemplate TheldrenTemplate() => Template(Theldren, t =>
    {
        t.Name = "Theldren Trigger";
        t.Faction = 114;
        t.MinLevel = t.MaxLevel = 60;
        t.UnitFlags = 33554432u;
    }) with { ScriptName = TheldrenTriggerAI.ScriptName };

    private static CreatureTemplate DoomrelTemplate() => Template(Doomrel, t =>
    {
        t.Name = "Doomrel";
        t.MinLevel = t.MaxLevel = 54;
        t.MinLevelHealth = t.MaxLevelHealth = 100_000;
    }) with { ScriptName = DoomrelAI.ScriptName };

    private static CreatureTemplate AddTemplate(uint entry) => Template(entry, t =>
    {
        t.Name = $"add {entry}";
        t.MinLevel = t.MaxLevel = 54;
        t.MinLevelHealth = t.MaxLevelHealth = 100_000;
    });

    private static CreatureAiEvent Row(uint id, byte type, int p1, uint spell)
        => new()
        {
            Id = id, CreatureId = Doomrel, EventType = type, Chance = 100, Flags = 0, Param1 = p1,
            Action1 = new CreatureAiAction((byte)EventAiActionType.Cast, (int)spell, 0, 0),
        };

    /// <summary>EVENT_T_AGGRO casts 15092 on self; EVENT_T_SUMMONED_UNIT (17) of the voidwalker casts a marker spell on self.</summary>
    private static CreatureAiContent DoomrelRows()
        => new([Row(1, (byte)EventAiEventType.Aggro, 0, AggroCastSpell), Row(2, 17, (int)Voidwalker, SummonCastSpell)], []);

    private static DungeonNpcRig DoomrelRig()
        => new(map => new Plain(map), [DoomrelTemplate(), AddTemplate(Voidwalker), AddTemplate(OtherSummon)],
            [Spawn(100, Doomrel, DungeonNpcRig.EntranceX + 2, DungeonNpcRig.EntranceY, DungeonNpcRig.EntranceZ, InstanceFixture.Dungeon)],
            DoomrelRows());

    [Fact]
    public void BothEntries_ResolveAsScriptNameAis()
    {
        var content = new CreatureContent([TheldrenTemplate(), DoomrelTemplate()], [], [], [], []);
        var catalog = new ScriptCatalog(new CreatureAiFactory(), content);
        foreach (uint entry in new[] { Theldren, Doomrel })
        {
            ScriptResolution r = catalog.Resolve(entry, BrdMap);
            Assert.True(r.ScriptNameResolved);
            Assert.Equal(ScriptKind.ScriptName, r.Kind);
        }
    }

    [Fact]
    public void AnUnregisteredScriptNameOnTheSameMap_IsNotResolved()
    {
        CreatureTemplate unknown = Template(99_999_003, t => t.Name = "unknown") with { ScriptName = "npc_unported_depths_unit" };
        var catalog = new ScriptCatalog(new CreatureAiFactory(), new CreatureContent([unknown], [], [], [], []));
        Assert.False(catalog.Resolve(99_999_003, BrdMap).ScriptNameResolved);
    }

    [Fact]
    public void TheTheldrenTriggerNextToAHostilePlayer_NeverFightsMovesOrCasts()
    {
        using var rig = new DungeonNpcRig(map => new Plain(map), [TheldrenTemplate()],
            [Spawn(100, Theldren, DungeonNpcRig.EntranceX + 2, DungeonNpcRig.EntranceY, DungeonNpcRig.EntranceZ, InstanceFixture.Dungeon)]);
        Creature trigger = rig.Creature(Theldren);
        Assert.IsType<TheldrenTriggerAI>(trigger.AI); // built by the real factory
        (float x, float y, float z) before = (trigger.X, trigger.Y, trigger.Z);

        rig.Run(30_000);

        Assert.Null(trigger.Combat.Victim);
        Assert.False(trigger.Combat.IsInCombat);
        Assert.Equal(before, (trigger.X, trigger.Y, trigger.Z));
        Assert.DoesNotContain(rig.Spells.Casts, c => ReferenceEquals(c.Caster, trigger));
        Assert.False(trigger.AI!.AttackStart(rig.Player));
        trigger.AI.OnAttackedBy(rig.Player);
        Assert.Null(trigger.Combat.Victim);
    }

    [Fact]
    public void DoomrelRunsTheDoomrelAi_AndOnAggroTheEventAiRowsFireUnderTheHost()
    {
        using DungeonNpcRig rig = DoomrelRig();
        Creature doomrel = rig.Creature(Doomrel);
        Assert.IsType<DoomrelAI>(doomrel.AI); // built by the real factory

        Assert.True(rig.Creatures.AttackStart(doomrel, rig.Player));
        rig.Tick();

        Assert.NotEmpty(rig.Spells.Of(doomrel, AggroCastSpell)); // the EVENT_T_AGGRO row
    }

    [Fact]
    public void ASummonedVoidwalker_EngagesAThreatListPlayerAtOnce_AndTheSummonedUnitRowStillFires()
    {
        using DungeonNpcRig rig = DoomrelRig();
        Creature doomrel = rig.Creature(Doomrel);
        Assert.True(rig.Creatures.AttackStart(doomrel, rig.Player));

        Creature? voidwalker = rig.Creatures.Summon(doomrel, Voidwalker, null, 60_000);
        Assert.NotNull(voidwalker);
        Assert.Same(rig.Player, voidwalker.Combat.Victim); // sent at the threat-list player by OnJustSummoned, before any update

        rig.Tick();
        Assert.Single(rig.Spells.Of(doomrel, SummonCastSpell)); // base.OnJustSummoned still dispatches EVENT_T_SUMMONED_UNIT
    }

    [Fact]
    public void AnotherSummon_IsNotSentAtAPlayer_AndTheVoidwalkerRowDoesNotFireForIt()
    {
        using DungeonNpcRig rig = DoomrelRig();
        Creature doomrel = rig.Creature(Doomrel);
        Assert.True(rig.Creatures.AttackStart(doomrel, rig.Player));

        Creature? other = rig.Creatures.Summon(doomrel, OtherSummon, null, 60_000);
        Assert.NotNull(other);
        Assert.Null(other.Combat.Victim); // not entry 8996: the hook leaves it alone

        rig.Tick();
        Assert.Empty(rig.Spells.Of(doomrel, SummonCastSpell)); // the row is keyed on 8996
    }

    [Fact]
    public void AVoidwalkerSummonedWithNoThreatListPlayer_IsNotSentAnywhere()
    {
        using DungeonNpcRig rig = DoomrelRig();
        Creature doomrel = rig.Creature(Doomrel);
        Assert.False(doomrel.Combat.HasThreatList);

        Creature? voidwalker = rig.Creatures.Summon(doomrel, Voidwalker, null, 60_000);
        Assert.NotNull(voidwalker);
        Assert.Null(voidwalker.Combat.Victim);
    }
}
