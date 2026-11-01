using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Gm;
using ArcaneCore.Kernel.Accounts;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>The GM spellbook: custom registry, teaching, the security and known checks, the candidate filter and the honeypot.</summary>
public sealed class GmSpellbookTests
{
    /// <summary>
    /// The real Spell.dbc attributes of every candidate (stances, equippedItemClass, spellVisual1, requiresSpellFocus and the reagent slots), read from
    /// the vmangos spell_template rows (reference database, read-only). The reagent slots are all empty for these ids.
    /// </summary>
    private static readonly Dictionary<uint, (uint Stances, int EquippedItemClass, uint SpellVisual, uint Focus)> s_dbc = new()
    {
        [47] = (0, -1, 6, 0),
        [6716] = (0, -1, 388, 0),
        [13954] = (0, -1, 681, 0),
        [18209] = (0, -1, 4487, 0),
        [18210] = (0, -1, 4487, 0),
        [18800] = (0, -1, 5041, 0),
        [21074] = (0, -1, 41, 0),
        [22690] = (0, -1, 6632, 0),
        // Candidates the registry left out because the filter rejects their real rows.
        [5] = (0, -1, 0, 0),
        [1979] = (0, 2, 0, 0),
        [5238] = (65536, -1, 347, 0),
        [29607] = (0, -1, 0, 0),
    };

    private static SpellInfo Staff(uint id)
    {
        (uint stances, int itemClass, uint visual, uint focus) = s_dbc[id];
        return Spell(id, Effect(SpellEffectName.Dummy, 0)) with
        {
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            SpellVisual = visual,
            Stances = stances,
            EquippedItemClass = itemClass,
            RequiresSpellFocus = focus,
        };
    }

    private static SpellTestKit Kit()
    {
        var kit = new SpellTestKit([.. CustomSpellRegistry.All.Select(entry => Staff(entry.SpellId))]);
        return kit;
    }

    public static TheoryData<uint> RegistryIds()
    {
        var data = new TheoryData<uint>();
        foreach (CustomSpell entry in CustomSpellRegistry.All)
        {
            data.Add(entry.SpellId);
        }

        return data;
    }

    [Fact]
    public void Registry_HoldsTheBriefedIds_AndNoneIsStillInert()
    {
        uint[] expected = [6716, 21074, 18209, 18210, 47, 13954, 22690, 18800];
        Assert.Equal(expected.Order(), CustomSpellRegistry.All.Select(entry => entry.SpellId).Order());
        foreach (uint id in expected)
        {
            Assert.False(DeliberatelyInertSpells.TryGet(id, out _), $"spell {id} is custom and must not be listed as inert");
        }
    }

    [Theory]
    [MemberData(nameof(RegistryIds))]
    public void AGm_LearnsAndCasts_EachSpell(uint id)
    {
        using SpellTestKit kit = Kit();
        AccountSecurity needed = CustomSpellRegistry.All.Single(entry => entry.SpellId == id).MinimumSecurity;
        (Player staff, _) = kit.AddPlayer(1, security: needed);

        Assert.Equal(SpellCastResult.NotKnown, kit.System.HandleCastRequest(staff, id, SpellCastTargets.ForSelf()));
        Assert.Contains(id, GmSpellbook.Teach(kit.System, staff));
        Assert.True(kit.Spellbook.HasSpell(staff, id));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(staff, id, SpellCastTargets.ForSelf()));
    }

    [Theory]
    [MemberData(nameof(RegistryIds))]
    public void APlayer_IsRefused_EvenWithTheSpellInTheBook(uint id)
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, id);

        Assert.Empty(GmSpellbook.Teach(kit.System, player));
        Assert.Equal(SpellCastResult.NotKnown, kit.System.CastSpell(player, id, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Equal(SpellCastResult.NotKnown, kit.System.CastSpell(player, id, SpellCastTargets.ForSelf(), triggered: true));
    }

    [Fact]
    public void AModeratorBelowTheEntrysLevel_IsRefused()
    {
        using SpellTestKit kit = Kit();
        (Player moderator, _) = kit.AddPlayer(1, security: AccountSecurity.Moderator);
        kit.Spellbook.Teach(moderator, 47); // GameMaster only

        Assert.Empty(GmSpellbook.Teach(kit.System, moderator));
        Assert.Equal(SpellCastResult.NotKnown, kit.System.CastSpell(moderator, 47, SpellCastTargets.ForSelf(), triggered: false));
    }

    [Fact]
    public void TheRegistryIds_PassTheFilterOnTheirRealDbcAttributes()
    {
        foreach (CustomSpell entry in CustomSpellRegistry.All)
        {
            Assert.True(s_dbc.ContainsKey(entry.SpellId), $"spell {entry.SpellId} has no pinned DBC row");
            Assert.Null(GmSpellbook.Unsuitable(Staff(entry.SpellId)));
            Assert.False(string.IsNullOrWhiteSpace(entry.Note));
            Assert.DoesNotContain("repurposed safe", entry.Note, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(5u, "has no visual")]
    [InlineData(1979u, "needs an equipped item")]
    [InlineData(5238u, "needs a shapeshift stance")]
    [InlineData(29607u, "has no visual")]
    public void TheLeftOutCandidates_AreRejectedOnTheirRealDbcAttributes_AndAreNotInTheRegistry(uint id, string reason)
    {
        Assert.Equal(reason, GmSpellbook.Unsuitable(Staff(id)));
        Assert.False(CustomSpellRegistry.Contains(id));
    }

    [Fact]
    public void TheRegistryList_CannotBeCastBackToAnArrayAndChanged()
    {
        Assert.False(CustomSpellRegistry.All is CustomSpell[]);
        Assert.Throws<NotSupportedException>(() => ((IList<CustomSpell>)CustomSpellRegistry.All)[0] = null!);
    }

    [Fact]
    public void ANegativeReagentItem_IsAnAbsentSlot()
    {
        Assert.Null(GmSpellbook.Unsuitable(Staff(47) with { Reagents = [new SpellReagent(-1, 1u)] }));
    }

    [Fact]
    public void AStaffCast_OfASpellNotKnown_IsRefused()
    {
        using SpellTestKit kit = Kit();
        (Player gm, _) = kit.AddPlayer(1, security: AccountSecurity.GameMaster);

        Assert.Equal(SpellCastResult.NotKnown, kit.System.CastSpell(gm, 18209, SpellCastTargets.ForSelf(), triggered: false));
    }

    [Theory]
    [MemberData(nameof(RegistryIds))]
    public void Honeypot_FlagsAPlayerCast_WhenOn_AndNeverAGm(uint id)
    {
        var options = new AntiCheatOptions();
        Assert.Null(CastSpellHoneypot.Evaluate(options.SpellHoneypot, id, callerExempt: false));
        options.SpellHoneypot.Enabled = true;

        AntiCheatFinding? finding = CastSpellHoneypot.Evaluate(options.SpellHoneypot, id, callerExempt: false);
        Assert.NotNull(finding);
        Assert.Equal(AntiCheatViolation.Spell, finding.Value.Type);
        Assert.Null(CastSpellHoneypot.Evaluate(options.SpellHoneypot, id, callerExempt: true));
    }

    [Fact]
    public void Unsuitable_RejectsReagentFocusStanceItemAndNoVisual()
    {
        SpellInfo ok = Staff(47);
        Assert.Null(GmSpellbook.Unsuitable(ok));
        Assert.Equal("needs a reagent", GmSpellbook.Unsuitable(ok with { Reagents = [new SpellReagent(5u, 1u)] }));
        Assert.Equal("needs a spell focus", GmSpellbook.Unsuitable(ok with { RequiresSpellFocus = 3 }));
        Assert.Equal("needs a shapeshift stance", GmSpellbook.Unsuitable(ok with { Stances = 1 }));
        Assert.Equal("needs an equipped item", GmSpellbook.Unsuitable(ok with { EquippedItemClass = 2 }));
        Assert.Equal("has no visual", GmSpellbook.Unsuitable(ok with { SpellVisual = 0 }));
    }

    [Fact]
    public void Teach_SkipsAnUnsuitableCandidate_AndAuditNamesIt()
    {
        SpellInfo[] spells = [.. CustomSpellRegistry.All.Select(entry => entry.SpellId == 47 ? Staff(47) with { Stances = 1 } : Staff(entry.SpellId))];
        using var kit = new SpellTestKit(spells);
        (Player admin, _) = kit.AddPlayer(1, security: AccountSecurity.Administrator);

        IReadOnlyList<uint> learned = GmSpellbook.Teach(kit.System, admin);

        Assert.DoesNotContain(47u, learned);
        Assert.Equal(CustomSpellRegistry.All.Count - 1, learned.Count);
        (uint SpellId, string Reason) problem = Assert.Single(GmSpellbook.Audit(kit.Store));
        Assert.Equal((47u, "needs a shapeshift stance"), problem);
    }
}
