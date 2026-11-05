using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The open-world, holiday and quest-event spells of <c>Spells/Generic/HolidayAndWorldScripts.cs</c>, <c>WorldEventDummyScripts.cs</c> and
/// <c>DeliberatelyInertSpells.WorldEvents.cs</c>: the Lunar Teleport Caps, Mistletoe, Valentine, Muglash's Brazier Trap and the inert markers.
/// </summary>
public sealed class WorldEventSpellTests
{
    private static readonly uint[] s_lunarCaps = [26406, 26408, 26409, 26410, 26412, 26414];

    /// <summary>Every id of the lane: it has a script, or it is deliberately inert with its citation.</summary>
    private static readonly uint[] s_laneIds =
    [
        26406, 26408, 26409, 26410, 26412, 26414, 26924, 26005, 24755, 23770, 26636, 24999, 17327, 31309, 31951, 31954, 20358, 20465, 20863,
        21795, 21830, 21867, 21896, 23356, 27766, 27771, 6602, 7077, 7141, 7143, 24748, 24782, 11048, 11067, 22518,
        22581, 22805, 20370, 11013, 7104, 7939, 24803, 10848, 27978, 20783,
    ];

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo Trigger(uint id, SpellEffectName effect) => Instant(Spell(id, Effect(effect, 0, SpellImplicitTarget.UnitCaster)));

    private static SpellInfo Marker(uint id) => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
    {
        Duration = new SpellDuration(30_000, 0, 30_000),
    });

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Theory]
    [InlineData(26406u, Team.Alliance, 26454u)]
    [InlineData(26408u, Team.Alliance, 26452u)]
    [InlineData(26409u, Team.Alliance, 26450u)]
    [InlineData(26410u, Team.Horde, 26456u)]
    [InlineData(26412u, Team.Horde, 26455u)]
    [InlineData(26414u, Team.Horde, 26453u)]
    [InlineData(26406u, Team.Horde, 26451u)]
    [InlineData(26414u, Team.Alliance, 26451u)]
    [InlineData(26924u, Team.Alliance, 0u)]
    public void LunarCap_PicksTheElderTeleportByTeam(uint cap, Team team, uint expected)
        => Assert.Equal(expected, LunarTeleportCapScript.TeleportFor(cap, team));

    [Fact]
    public void LunarCap_TeleportsAnAlliancePlayerToTheElder()
    {
        using SpellTestKit kit = Kit(Trigger(26406, SpellEffectName.ScriptEffect), Marker(26454), Marker(26451));
        (Player player, _) = kit.AddPlayer(1, race: Race.Human);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 26406, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, 26454));
        Assert.False(kit.System.HasAura(player, 26451));
    }

    [Fact]
    public void LunarCap_SendsAHordePlayerToMoonglade()
    {
        using SpellTestKit kit = Kit(Trigger(26406, SpellEffectName.ScriptEffect), Marker(26454), Marker(26451));
        (Player player, _) = kit.AddPlayer(1, race: Race.Orc);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 26406, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, 26451));
        Assert.False(kit.System.HasAura(player, 26454));
    }

    [Fact]
    public void LunarCap_OfAHordeCity_TeleportsAHordePlayerToItsElder()
    {
        using SpellTestKit kit = Kit(Trigger(26414, SpellEffectName.ScriptEffect), Marker(26453), Marker(26451));
        (Player player, _) = kit.AddPlayer(1, race: Race.Orc);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 26414, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, 26453));
    }

    [Fact]
    public void Mistletoe_MarksTheTargetWithTheMistletoeAura()
    {
        using SpellTestKit kit = Kit(Trigger(26004, SpellEffectName.Dummy), Marker(26005));
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 26004, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, 26005));
    }

    [Fact]
    public void Valentine_AnswerScriptsStayRegisteredAndKeepTheirGiftTables()
    {
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        foreach (uint id in new uint[] { 26923, 26924, 26663, 27541, 27547, 27548, 27549, 27550 })
        {
            Assert.IsType<ValentineAnswerScript>(registry.Find(id));
        }

        Assert.Equal((27525u, 27509u), ValentineAnswerScript.GiftsFor(26663, 12));
    }

    private const uint Undercity = 68, Orgrimmar = 85, Heartbroken = 26898, Adored = 26680, AdorationUndercity = 27515, FriendshipUndercity = 27246,
        AdorationOrgrimmar = 27505, RemoveAmorous = 26879, LoveInTheAir = 27741, LoveToken = 21815;

    /// <summary>A random whose <c>Next(int)</c> always answers <c>value</c>: the Valentine handler's one-in-six roll.</summary>
    private sealed class FixedRandom(int value) : Random(1)
    {
        public override int Next(int maxValue) => value;
    }

    private static CraftingTestKit ValentineRig(int roll, uint spell)
    {
        var rig = new CraftingTestKit(
            [
                Instant(Spell(spell, Effect(SpellEffectName.ScriptEffect, 0, SpellImplicitTarget.Unit))),
                Marker(Heartbroken), Marker(Adored), Marker(AdorationUndercity), Marker(FriendshipUndercity), Marker(AdorationOrgrimmar),
                Marker(RemoveAmorous), Marker(LoveInTheAir),
            ],
            [new ItemTemplate { Entry = LoveToken, Class = 12, Name = "Love Token", DisplayId = 1, Stackable = 20, Quality = 1 }]);
        SpellScriptDispatcher.Install(rig.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        rig.System.Random = new FixedRandom(roll);
        return rig;
    }

    /// <summary>A city guard or citizen of <paramref name="faction"/> next to the player, wearing Love is in the Air.</summary>
    private static Creature AddValentineNpc(CraftingTestKit rig, uint faction)
    {
        Player near = rig.Player;
        var template = new CreatureTemplate { Entry = 921001, Name = "Valentine NPC", MinLevel = 10, MaxLevel = 10,
            MinLevelHealth = 500, MaxLevelHealth = 500, DisplayIds = [1], Faction = faction };
        var npc = new Creature(921001, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        npc.Relocate(near.X + 2, near.Y, near.Z, 0, 0);
        npc.MapId = near.MapId;
        near.Map!.AddObject(npc);
        npc.FactionTemplate = faction;
        Assert.Equal(SpellCastResult.CastOk, rig.System.CastSpell(npc, LoveInTheAir, SpellCastTargets.ForSelf(), triggered: true));
        return npc;
    }

    private static SpellCastResult Answer(CraftingTestKit rig, Creature npc, uint spell)
        => rig.System.CastSpell(npc, spell, SpellCastTargets.ForUnit(rig.Player.Guid), triggered: true);

    [Fact]
    public void ValentineGuard26924_OnAGoodRoll_GivesAdoredThenFriendship_ConsumesATokenAndTheGuardsAura()
    {
        // mangos-classic SpellEffects.cpp:4003-4042: Adored plus the faction pledge, Love is in the Air off the guard, one Love Token destroyed.
        using CraftingTestKit rig = ValentineRig(roll: 3, 26924);
        Creature guard = AddValentineNpc(rig, Undercity);
        rig.Give(LoveToken, 2);

        Assert.Equal(SpellCastResult.CastOk, Answer(rig, guard, 26924));
        Assert.True(rig.System.HasAura(rig.Player, Adored));
        Assert.True(rig.System.HasAura(rig.Player, AdorationUndercity));
        Assert.False(rig.System.HasAura(rig.Player, FriendshipUndercity));
        Assert.False(rig.System.HasAura(rig.Player, Heartbroken));
        Assert.False(rig.System.HasAura(guard, LoveInTheAir));
        Assert.Equal(1u, rig.Inventory.GetItemCount(LoveToken));

        Assert.Equal(SpellCastResult.CastOk, rig.System.CastSpell(guard, LoveInTheAir, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, Answer(rig, guard, 26924));
        Assert.True(rig.System.HasAura(rig.Player, FriendshipUndercity));
        Assert.Equal(0u, rig.Inventory.GetItemCount(LoveToken));
    }

    [Fact]
    public void ValentineGuard26924_OnTheOneInSixRoll_Heartbreaks_AndStillTakesTheToken()
    {
        using CraftingTestKit rig = ValentineRig(roll: 0, 26924);
        Creature guard = AddValentineNpc(rig, Undercity);
        rig.Give(LoveToken);

        Assert.Equal(SpellCastResult.CastOk, Answer(rig, guard, 26924));
        Assert.True(rig.System.HasAura(rig.Player, Heartbroken));
        Assert.False(rig.System.HasAura(rig.Player, Adored));
        Assert.False(rig.System.HasAura(rig.Player, AdorationUndercity));
        Assert.False(rig.System.HasAura(guard, LoveInTheAir));
        Assert.Equal(0u, rig.Inventory.GetItemCount(LoveToken));
    }

    [Theory]
    [InlineData(26923u)]
    [InlineData(26924u)]
    public void ValentineGuard_WithoutALoveToken_DoesNothing(uint spell)
    {
        using CraftingTestKit rig = ValentineRig(roll: 3, spell);
        Creature guard = AddValentineNpc(rig, Undercity);

        Assert.Equal(SpellCastResult.CastOk, Answer(rig, guard, spell));
        Assert.False(rig.System.HasAura(rig.Player, Adored));
        Assert.False(rig.System.HasAura(rig.Player, Heartbroken));
        Assert.True(rig.System.HasAura(guard, LoveInTheAir));
    }

    [Fact]
    public void ValentineGuard_IgnoresAHeartbrokenPlayer_AndKeepsTheirToken()
    {
        using CraftingTestKit rig = ValentineRig(roll: 3, 26924);
        Creature guard = AddValentineNpc(rig, Undercity);
        rig.Give(LoveToken);
        Assert.Equal(SpellCastResult.CastOk, rig.System.CastSpell(rig.Player, Heartbroken, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(SpellCastResult.CastOk, Answer(rig, guard, 26924));
        Assert.False(rig.System.HasAura(rig.Player, Adored));
        Assert.Equal(1u, rig.Inventory.GetItemCount(LoveToken));
    }

    [Fact]
    public void ValentineCitizen26663_NeedsNoToken_AndLeavesTheCitizensAuraAlone()
    {
        // vmangos SpellEffects.cpp:4078-4193 citizens: no Love Token gate (Orgrimmar faction 85 pledges 27505).
        using CraftingTestKit rig = ValentineRig(roll: 3, 26663);
        Creature citizen = AddValentineNpc(rig, Orgrimmar);

        Assert.Equal(SpellCastResult.CastOk, Answer(rig, citizen, 26663));
        Assert.True(rig.System.HasAura(rig.Player, Adored));
        Assert.True(rig.System.HasAura(rig.Player, AdorationOrgrimmar));
        Assert.True(rig.System.HasAura(citizen, LoveInTheAir));
    }

    [Fact]
    public void MuglashBrazierTrap_HasItsScript()
        => Assert.IsType<MuglashBrazierTrapScript>(SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly).Find(20863));

    [Fact]
    public void EveryLaneSpell_HasAScriptOrIsDeliberatelyInert_NeverBoth()
    {
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        foreach (uint id in s_laneIds)
        {
            bool scripted = registry.Find(id) is not null;
            bool inert = DeliberatelyInertSpells.TryGet(id, out InertSpell? entry);
            Assert.True(scripted || inert, $"spell {id} has neither a script nor an inert entry");
            Assert.False(scripted && inert, $"spell {id} is both scripted and inert");
            if (entry is not null)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Citation), $"spell {id}: no citation");
            }
        }

        foreach (uint cap in s_lunarCaps)
        {
            Assert.IsType<LunarTeleportCapScript>(registry.Find(cap));
        }
    }

    /// <summary>Ids that a reference DB row casts or applies: the entry names that row, and is never NeverCast (the review of this lane found the earlier boilerplate false).</summary>
    [Theory]
    [InlineData(6602u, InertSpellReason.ScriptCarrier, "20220107235502_world.sql:37")]
    [InlineData(7143u, InertSpellReason.ScriptCarrier, "20240206152221_world.sql:104")]
    [InlineData(7939u, InertSpellReason.SpawnAura, "20210717172913_world.sql:266")]
    [InlineData(11013u, InertSpellReason.ScriptCarrier, "20230214215859_world.sql:95")]
    [InlineData(11067u, InertSpellReason.ScriptCarrier, "20230214215859_world.sql:93")]
    [InlineData(11048u, InertSpellReason.ScriptCarrier, "20230214215859_world.sql:24")]
    [InlineData(21795u, InertSpellReason.Marker, "20220801022620_world.sql:72")]
    [InlineData(21867u, InertSpellReason.Marker, "20220801022620_world.sql:39")]
    [InlineData(24748u, InertSpellReason.Marker, "20220422044303_world.sql:204")]
    [InlineData(24782u, InertSpellReason.Marker, "item_template 20451")]
    [InlineData(24803u, InertSpellReason.Marker, "gameobject_template 180502")]
    [InlineData(10848u, InertSpellReason.SpawnAura, "20210717172913_world.sql:221")]
    [InlineData(27978u, InertSpellReason.SpawnAura, "20230723234305_world.sql:14")]
    [InlineData(20783u, InertSpellReason.Marker, "ashenvale.cpp:447 and :663")]
    public void SpellsWithAKnownCaster_AreNotNeverCastAndNameTheirRow(uint id, InertSpellReason reason, string rowCitation)
    {
        Assert.True(DeliberatelyInertSpells.TryGet(id, out InertSpell? entry), $"spell {id} is not listed");
        Assert.NotNull(entry);
        Assert.Equal(reason, entry.Reason);
        Assert.Contains(rowCitation, entry.Citation, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverCastEntries_OfTheLane_CarryTheScanCitationAndNoCasterIsAmongThem()
    {
        uint[] neverCast = [21830, 22518, 22581, 22805];
        foreach (uint id in s_laneIds)
        {
            if (DeliberatelyInertSpells.TryGet(id, out InertSpell? entry) && entry.Reason == InertSpellReason.NeverCast)
            {
                Assert.Contains(id, neverCast);
                Assert.Contains("Full_DB", entry.Citation, StringComparison.Ordinal);
                Assert.Contains("sql/migrations", entry.Citation, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void SpellsWithAnImportedDbScriptOrAnAura_AreNotListed()
    {
        foreach (uint id in new uint[] { 21914, 21917, 24746 })
        {
            Assert.False(DeliberatelyInertSpells.TryGet(id, out _), $"spell {id} has a DB script or a known applier and is not inert");
        }
    }

    [Fact]
    public void TheEvidenceRulingSpells_AreNotListedYet()
    {
        foreach (uint id in new uint[] { 30012, 17619, 23852 })
        {
            Assert.False(DeliberatelyInertSpells.TryGet(id, out _), $"spell {id} waits for the evidence ruling");
        }
    }
}
