using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Party area aura update (<c>SpellSystem.UpdateAreaAuras</c>, vmangos AreaAura::Update) behaviour that the allocation trim must keep:
/// child per member in range, rank by member level, the player-only flag, a member holding the spell from another source, a dead caster
/// or member, and the updated child set across ticks. Range/party/logout transitions are in SpellEffectCombatTests, the level-10 lower rank
/// in AuraRankSelectionTests, the settlement hold in HeldAreaAuraTests.
/// </summary>
public sealed class AreaAuraUpdateTests
{
    private const uint Devotion = 946001, PlayerOnly = 946002, Rank1 = 946011, Rank2 = 946012;

    private const uint PlayerOnlyFlag = 0x100; // SPELL_ATTR_EX3_ONLY_ON_PLAYER

    private static SpellInfo Permanent(uint id, uint spellLevel = 0, uint ex3 = 0, int value = 0, float radius = 30) => Spell(id,
        Effect(SpellEffectName.ApplyAreaAuraParty, value, aura: AuraType.ModResistance, misc: 1) with { Radius = radius }) with
    {
        SpellLevel = spellLevel,
        AttributesEx3 = ex3,
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    /// <summary>The world daemon's resolver (WorldSpellUnitResolver): players and creatures through the map's object registry.</summary>
    private sealed class MapObjectResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => guid.IsEmpty ? null : reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    private static SpellTestKit Kit(out FakeGroups groups)
    {
        var kit = new SpellTestKit(
            Permanent(Devotion, value: 5),
            Permanent(PlayerOnly, ex3: PlayerOnlyFlag, value: 5),
            Permanent(Rank1, spellLevel: 20, value: 5),
            Permanent(Rank2, spellLevel: 40, value: 50));
        kit.System.RankChains = new SpellRankChains(
        [
            new SkillLineAbilityRecord(1, 8, Rank1, 0, 0, 0, Rank2, 0, 0, 0),
            new SkillLineAbilityRecord(2, 8, Rank2, 0, 0, 0, 0, 0, 0, 0),
        ]);
        groups = new FakeGroups();
        kit.System.Groups = groups;
        return kit;
    }

    [Fact]
    public void PartyAreaAura_GivesEveryMemberInRangeOneChild_AndNothingToTheCasterOrOutsiders()
    {
        using SpellTestKit kit = Kit(out FakeGroups groups);
        (Player paladin, _) = kit.AddPlayer(1);
        var members = new List<Player>();
        for (uint i = 2; i <= 5; i++)
        {
            members.Add(kit.AddPlayer(i, i * 2, 0).Player);
        }

        (Player outsider, _) = kit.AddPlayer(6, 3, 0);
        groups.Parties.Add([paladin.Guid, .. members.Select(m => m.Guid)]);

        kit.System.CastSpell(paladin, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);
        kit.Advance(100); // a second tick changes nothing: children are not re-created

        SpellAuraHolder source = Assert.Single(kit.System.GetAuras(paladin));
        Assert.True(source.IsAreaSource);
        Assert.Equal(members.Count, source.AreaChildren.Count);
        foreach (Player member in members)
        {
            SpellAuraHolder child = Assert.Single(kit.System.GetAuras(member));
            Assert.Same(source, child.AreaParent);
            Assert.Same(child, source.AreaChildren[member.Guid]);
            Assert.Equal(Devotion, child.Spell.Id);
        }

        Assert.Empty(kit.System.GetAuras(outsider));
    }

    [Fact]
    public void PartyAreaAura_ChildrenSurviveManyTicks_AsTheSameHolders()
    {
        using SpellTestKit kit = Kit(out FakeGroups groups);
        (Player paladin, _) = kit.AddPlayer(1);
        (Player a, _) = kit.AddPlayer(2, 4, 0);
        (Player b, _) = kit.AddPlayer(3, 8, 0);
        groups.Parties.Add([paladin.Guid, a.Guid, b.Guid]);
        kit.System.CastSpell(paladin, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);
        SpellAuraHolder childA = Assert.Single(kit.System.GetAuras(a));
        SpellAuraHolder childB = Assert.Single(kit.System.GetAuras(b));

        kit.Advance(2000);

        Assert.Same(childA, Assert.Single(kit.System.GetAuras(a)));
        Assert.Same(childB, Assert.Single(kit.System.GetAuras(b)));
        Assert.False(childA.IsRemoved);
    }

    [Fact]
    public void PartyAreaAura_MemberAtExactlyTheRadius_GetsIt_AndJustBeyondDoesNot()
    {
        using SpellTestKit kit = Kit(out FakeGroups groups);
        (Player paladin, _) = kit.AddPlayer(1);
        (Player edge, _) = kit.AddPlayer(2, 30, 0);
        (Player beyond, _) = kit.AddPlayer(3, 30.5f, 0);
        groups.Parties.Add([paladin.Guid, edge.Guid, beyond.Guid]);

        kit.System.CastSpell(paladin, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);

        Assert.Single(kit.System.GetAuras(edge)); // dist^2 == radius^2 is not "greater than"
        Assert.Empty(kit.System.GetAuras(beyond));
    }

    [Fact]
    public void PartyAreaAura_MemberBelowTheFirstRanksLevel_GetsNothing_LowerLevelGetsTheLowerRank_HigherGetsTheCastRank()
    {
        using SpellTestKit kit = Kit(out FakeGroups groups);
        (Player paladin, _) = kit.AddPlayer(1);
        (Player tooLow, _) = kit.AddPlayer(2, 4, 0);   // level 5: 5 + 10 < 20, no rank fits
        (Player rank1, _) = kit.AddPlayer(3, 6, 0);    // level 10: 20 <= 10 + 10, rank 1 only
        (Player rank2, _) = kit.AddPlayer(4, 8, 0);    // level 30: 40 <= 30 + 10, the cast rank
        paladin.Level = 60;
        tooLow.Level = 5;
        rank1.Level = 10;
        rank2.Level = 30;
        groups.Parties.Add([paladin.Guid, tooLow.Guid, rank1.Guid, rank2.Guid]);

        kit.System.CastSpell(paladin, Rank2, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);

        Assert.Empty(kit.System.GetAuras(tooLow));
        SpellAuraHolder lower = Assert.Single(kit.System.GetAuras(rank1));
        Assert.Equal(Rank1, lower.Spell.Id);
        Assert.Equal(5, lower.AuraSpan.ToArray().First(a => a is not null)!.Amount);
        SpellAuraHolder full = Assert.Single(kit.System.GetAuras(rank2));
        Assert.Equal(Rank2, full.Spell.Id);
        Assert.Equal(50, full.AuraSpan.ToArray().First(a => a is not null)!.Amount);

        // The too-low member levels up past the threshold and gets the aura at the next tick.
        tooLow.Level = 10;
        kit.Advance(100);
        Assert.Equal(Rank1, Assert.Single(kit.System.GetAuras(tooLow)).Spell.Id);
    }

    [Fact]
    public void PartyAreaAura_OnlyOnPlayerFlag_SkipsACreatureMember_ButAPlainAuraDoesNot()
    {
        using SpellTestKit kit = Kit(out FakeGroups groups);
        var creatures = new CreatureMapSystem(kit.World.GetMap(0), Content([Template(WolfEntry)], []), random: new Random(1));
        kit.World.GetMap(0).AddUpdater(creatures);
        kit.System.Units = new MapObjectResolver(); // the default resolver only finds players
        (Player paladin, _) = kit.AddPlayer(1);
        (Player player, _) = kit.AddPlayer(2, 4, 0);
        Creature wolf = creatures.SpawnTemporary(Template(WolfEntry), 6, 0, 83.5f, 0);
        groups.Parties.Add([paladin.Guid, player.Guid, wolf.Guid]);

        kit.System.CastSpell(paladin, PlayerOnly, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);

        Assert.Single(kit.System.GetAuras(player));
        Assert.Empty(kit.System.GetAuras(wolf));

        // Control: the same party without the flag does reach the creature, so the skip above is the flag and not the setup.
        kit.System.RemoveAuras(paladin, PlayerOnly);
        kit.System.CastSpell(paladin, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);
        Assert.Single(kit.System.GetAuras(wolf));
        Assert.Single(kit.System.GetAuras(player));
    }

    [Fact]
    public void PartyAreaAura_MemberHoldingTheSpellFromAnotherSource_IsSkipped()
    {
        using SpellTestKit kit = Kit(out FakeGroups groups);
        (Player first, _) = kit.AddPlayer(1);
        (Player second, _) = kit.AddPlayer(2, 5, 0);
        (Player member, _) = kit.AddPlayer(3, 10, 0);
        groups.Parties.Add([first.Guid, second.Guid, member.Guid]);

        kit.System.CastSpell(first, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(second, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);
        kit.Advance(100);

        // Each paladin already holds Devotion (its own source), so neither gets the other's child; the member gets exactly one.
        Assert.True(Assert.Single(kit.System.GetAuras(first)).IsAreaSource);
        Assert.True(Assert.Single(kit.System.GetAuras(second)).IsAreaSource);
        SpellAuraHolder child = Assert.Single(kit.System.GetAuras(member));
        Assert.NotNull(child.AreaParent);
        Assert.Contains(child.CasterGuid, new[] { first.Guid, second.Guid });
    }

    [Fact]
    public void PartyAreaAura_MemberAlreadyHoldingTheSpellDirectly_KeepsItAndGetsNoChild()
    {
        using SpellTestKit kit = Kit(out FakeGroups groups);
        (Player paladin, _) = kit.AddPlayer(1);
        (Player member, _) = kit.AddPlayer(2, 4, 0);
        groups.Parties.Add([paladin.Guid, member.Guid]);
        // The member holds the same spell as its own area source (a second paladin in another group state).
        kit.System.CastSpell(member, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(paladin, Devotion, SpellCastTargets.ForSelf(), triggered: true);

        kit.Advance(100);

        SpellAuraHolder own = Assert.Single(kit.System.GetAuras(member));
        Assert.Null(own.AreaParent);
        Assert.Equal(member.Guid, own.CasterGuid);
        Assert.Empty(kit.System.GetAuras(paladin).Single(h => h.IsAreaSource).AreaChildren);
    }

    [Fact]
    public void PartyAreaAura_DeadCaster_LosesEveryChild_AndGetsThemBackWhenAlive()
    {
        using SpellTestKit kit = Kit(out FakeGroups groups);
        (Player paladin, _) = kit.AddPlayer(1);
        (Player a, _) = kit.AddPlayer(2, 4, 0);
        (Player b, _) = kit.AddPlayer(3, 8, 0);
        groups.Parties.Add([paladin.Guid, a.Guid, b.Guid]);
        kit.System.CastSpell(paladin, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);
        SpellAuraHolder source = Assert.Single(kit.System.GetAuras(paladin));
        SpellAuraHolder childA = Assert.Single(kit.System.GetAuras(a));
        Assert.Single(kit.System.GetAuras(b));

        uint health = paladin.Health;
        paladin.Health = 0; // dead, source holder still present (OnUnitDied is not raised)
        kit.Advance(100);

        Assert.True(childA.IsRemoved);
        Assert.Empty(kit.System.GetAuras(a));
        Assert.Empty(kit.System.GetAuras(b));
        Assert.Empty(source.AreaChildren);

        paladin.Health = health;
        kit.Advance(100);
        Assert.Single(kit.System.GetAuras(a));
        Assert.Single(kit.System.GetAuras(b));
    }

    [Fact]
    public void PartyAreaAura_DeadMember_LosesItsChild_AndOthersKeepTheirs()
    {
        using SpellTestKit kit = Kit(out FakeGroups groups);
        (Player paladin, _) = kit.AddPlayer(1);
        (Player dying, _) = kit.AddPlayer(2, 4, 0);
        (Player healthy, _) = kit.AddPlayer(3, 8, 0);
        groups.Parties.Add([paladin.Guid, dying.Guid, healthy.Guid]);
        kit.System.CastSpell(paladin, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);
        Assert.Single(kit.System.GetAuras(dying));

        dying.Health = 0;
        kit.Advance(100);

        Assert.Empty(kit.System.GetAuras(dying));
        Assert.Single(kit.System.GetAuras(healthy));
    }

    [Fact]
    public void PartyAreaAura_RangeFlap_RemovesAndRegrantsAsADifferentHolder()
    {
        using SpellTestKit kit = Kit(out FakeGroups groups);
        (Player paladin, _) = kit.AddPlayer(1);
        (Player member, _) = kit.AddPlayer(2, 10, 0);
        groups.Parties.Add([paladin.Guid, member.Guid]);
        kit.System.CastSpell(paladin, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);
        SpellAuraHolder first = Assert.Single(kit.System.GetAuras(member));

        member.Relocate(60, 0, member.Z, 0, kit.Now);
        kit.Advance(100);
        Assert.True(first.IsRemoved);
        Assert.Empty(kit.System.GetAuras(member));
        Assert.Empty(kit.System.GetAuras(paladin).Single(h => h.IsAreaSource).AreaChildren);

        member.Relocate(10, 0, member.Z, 0, kit.Now);
        kit.Advance(100);
        SpellAuraHolder second = Assert.Single(kit.System.GetAuras(member));
        Assert.NotSame(first, second);
    }

    [Fact]
    public void PartyAreaAura_SoloCaster_UpdatesWithoutChildren()
    {
        using SpellTestKit kit = Kit(out _);
        (Player paladin, _) = kit.AddPlayer(1);

        kit.System.CastSpell(paladin, Devotion, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(300);

        SpellAuraHolder source = Assert.Single(kit.System.GetAuras(paladin));
        Assert.Empty(source.AreaChildren);
    }
}
