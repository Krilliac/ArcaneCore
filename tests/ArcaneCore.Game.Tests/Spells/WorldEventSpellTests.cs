using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
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
        22581, 22805, 20370, 11013, 7104, 7939, 24803,
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
        foreach (uint id in new uint[] { 26923, 26663, 27541, 27547, 27548, 27549, 27550 })
        {
            Assert.IsType<ValentineAnswerScript>(registry.Find(id));
        }

        Assert.Equal((27525u, 27509u), ValentineAnswerScript.GiftsFor(26663, 12));
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
    [InlineData(26924u, InertSpellReason.ScriptCarrier, "dbscripts_on_gossip 284901")]
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
        uint[] neverCast = [21830, 22518, 22581, 22805, 24803];
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
