using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackrockDepths;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
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
    private const uint Theldren = TheldrenTriggerAI.Entry, Doomrel = DoomrelAI.Entry, Voidwalker = DoomrelAI.VoidwalkerMinion, OtherSummon = 9999;
    private const uint VoidwalkerGuardianSpell = 77_010;
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

    [Fact]
    public void AVoidwalkerSummonedThroughTheGuardianSpellEffect_IsSentAtAThreatListPlayer_BeforePetAiAttaches()
    {
        // SummonService.EffectSummonGuardian spawns the guardian (SpawnSummoned runs OnJustSummoned) and only then attaches PetAI: the hook
        // reaches the guardian while it still has the creature AI of its entry, and the engagement must survive the AI swap.
        SpellInfo summon = SpellTestKit.Spell(VoidwalkerGuardianSpell, SpellTestKit.Effect(SpellEffectName.SummonGuardian, 1, misc: (int)Voidwalker));
        using var kit = new PetTestKit([summon], extraTemplates: [DoomrelTemplate(), AddTemplate(Voidwalker)], creatureAi: new CreatureAiContent([], []));
        (Player player, _) = kit.AddPlayer(1);
        Creature doomrel = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(Doomrel)!, 0, 0, 83.5f, 0);
        Assert.IsType<DoomrelAI>(doomrel.AI);
        Assert.True(kit.Creatures.AttackStart(doomrel, player));

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(doomrel, VoidwalkerGuardianSpell));

        Creature guardian = Assert.Single(kit.Map.Pets!.GuardiansOf(doomrel));
        Assert.Equal(Voidwalker, guardian.Template.Entry);
        Assert.IsType<PetAI>(guardian.AI); // the summon service attached it after the hook ran
        Assert.Same(player, guardian.Combat.Victim);
    }

    [Fact]
    public void AThreatListHoldingOnlyACreature_SendsNoVoidwalkerAnywhere()
    {
        using DungeonNpcRig rig = DoomrelRig();
        Creature doomrel = rig.Creature(Doomrel);
        Creature? ally = rig.Creatures.Summon(doomrel, OtherSummon, null, 60_000);
        Assert.NotNull(ally);
        Assert.True(rig.Creatures.AttackStart(doomrel, ally));
        Assert.True(doomrel.Combat.HasThreatList);
        Assert.DoesNotContain(doomrel.Combat.Threat.Entries, e => e.Target is Player); // only the non-player is listed: playerOnly finds nothing

        Creature? voidwalker = rig.Creatures.Summon(doomrel, Voidwalker, null, 60_000);

        Assert.NotNull(voidwalker);
        Assert.Null(voidwalker.Combat.Victim);
    }

    [Fact]
    public void AThreatListHoldingACreatureAndAPlayer_AlwaysSendsEveryVoidwalkerAtThePlayer()
    {
        using DungeonNpcRig rig = DoomrelRig();
        Creature doomrel = rig.Creature(Doomrel);
        Creature? ally = rig.Creatures.Summon(doomrel, OtherSummon, null, 60_000);
        Assert.NotNull(ally);
        Assert.True(rig.Creatures.AttackStart(doomrel, ally));
        Assert.True(rig.Creatures.AttackStart(doomrel, rig.Player));
        Assert.Equal(2, doomrel.Combat.Threat.Entries.Count);

        for (int i = 0; i < 8; i++)
        {
            Creature? voidwalker = rig.Creatures.Summon(doomrel, Voidwalker, null, 60_000);
            Assert.NotNull(voidwalker);
            Assert.Same(rig.Player, voidwalker.Combat.Victim); // never the creature on the list
        }
    }

    [Fact]
    public void TheThelrinBannerSpell_FindsTheNotSelectableTriggerByTargetSearch_AndMovesTheRingOfLawToTheBannerState()
    {
        // ClassicDB z2815 spell_template 27517: effect 1 SUMMON_OBJECT_WILD (game object 181058, not modelled here), effect 2 DUMMY with target 38
        // (TARGET_UNIT_SCRIPT_NEAR_CASTER); spell_script_target (27517, 1, 16079, 0). The spell's cast range (index 7) is not decoded: 100 yd is
        // used, and the triggered cast skips its spell-focus requirement (1360).
        using var spells = new SpellTestKit(
            SpellTestKit.Spell(TheldrenTriggerAI.BannerSpell, new SpellEffectInfo(),
                SpellTestKit.Effect(SpellEffectName.Dummy, 1, SpellImplicitTarget.UnitScriptNearCaster))
                with { StartRecoveryCategory = 0, StartRecoveryTime = 0, Range = new SpellRange(0, 100) });
        spells.System.Store = new SpellStore(spells.Store.All, [], [],
            [new SpellStore.ScriptTarget(TheldrenTriggerAI.BannerSpell, 1, Theldren, 0)]);
        SpellScriptDispatcher.Install(spells.System, SpellScriptRegistry.Discover(typeof(ThelrinBannerSpellScript).Assembly));
        using DungeonScriptHarness run = new(map => new BlackrockDepthsInstance(map), [Doomrel, Theldren], [Doomrel, Theldren]);
        var data = Assert.IsType<BlackrockDepthsInstance>(run.Data);
        Creature caster = run.Creature(Doomrel);
        Creature trigger = run.Creature(Theldren);
        trigger.UnitFlags |= UnitFlags.NotSelectable; // the template's UNIT_FLAG_NOT_SELECTABLE (33554432)
        Assert.Equal(EncounterState.NotStarted, data.GetData(BlackrockDepthsInstance.TypeRingOfLaw));

        // out of range of the search: nothing is found and the state stays
        run.Creatures.NearTeleport(trigger, caster.X + 300f, caster.Y, caster.Z, 0);
        spells.System.CastSpell(caster, TheldrenTriggerAI.BannerSpell, SpellCastTargets.ForSelf(), triggered: true);
        ResolveSpellBatch(spells);
        Assert.Equal(EncounterState.NotStarted, data.GetData(BlackrockDepthsInstance.TypeRingOfLaw));

        run.Creatures.NearTeleport(trigger, caster.X + 5f, caster.Y, caster.Z, 0);
        Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(caster, TheldrenTriggerAI.BannerSpell, SpellCastTargets.ForSelf(), triggered: true));
        ResolveSpellBatch(spells);

        Assert.Equal(5u, data.GetData(BlackrockDepthsInstance.TypeRingOfLaw)); // DATA_BANNER_BEFORE_EVENT
    }

    /// <summary>With a spell batch window (ARCANECORE_TEST_SPELL_BATCH_MS) a cast that goes off is held: advance past the boundary so it resolves.</summary>
    private static void ResolveSpellBatch(SpellTestKit kit)
    {
        if (kit.System.SpellBatchWindowMs != 0)
        {
            kit.Advance(kit.System.SpellBatchWindowMs);
        }
    }
}
