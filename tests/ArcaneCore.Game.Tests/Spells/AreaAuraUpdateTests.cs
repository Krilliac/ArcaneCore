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
        // The member is itself a caster of the same spell (its own area source, cast before the paladin's), while sharing the paladin's party.
        // So the paladin's pass finds the member already holding the spell and skips it.
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
    public void PartyAreaAura_TwoAreaEffectsWithDifferentRadii_UsesTheMaxRadius_InEitherEffectOrder()
    {
        foreach (bool smallFirst in new[] { true, false })
        {
            const uint Dual = 946021;
            SpellEffectInfo small = Effect(SpellEffectName.ApplyAreaAuraParty, 5, aura: AuraType.ModResistance, misc: 1) with { Radius = 10 };
            SpellEffectInfo large = Effect(SpellEffectName.ApplyAreaAuraParty, 5, aura: AuraType.ModResistance, misc: 2) with { Radius = 40 };
            SpellInfo dual = Spell(Dual, smallFirst ? [small, large] : [large, small]) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            };
            using var kit = new SpellTestKit(dual);
            var groups = new FakeGroups();
            kit.System.Groups = groups;
            (Player paladin, _) = kit.AddPlayer(1);
            (Player near, _) = kit.AddPlayer(2, 5, 0);        // inside both radii
            (Player between, _) = kit.AddPlayer(3, 25, 0);    // beyond the 10 radius, inside the 40 radius
            (Player beyond, _) = kit.AddPlayer(4, 45, 0);     // beyond both
            groups.Parties.Add([paladin.Guid, near.Guid, between.Guid, beyond.Guid]);

            kit.System.CastSpell(paladin, Dual, SpellCastTargets.ForSelf(), triggered: true);
            kit.Advance(100);

            Assert.Single(kit.System.GetAuras(near));
            Assert.True(Assert.Single(kit.System.GetAuras(between)).AreaParent is not null, $"smallFirst={smallFirst}: the member between the radii must get the child");
            Assert.Empty(kit.System.GetAuras(beyond));
        }
    }

    // ---- Nested area-aura updates -------------------------------------------------------------------------------------------------
    // SpellSystem.UpdateAreaAuras keeps shared scratch collections with an in-use flag. HolderAdded/HolderRemoved fire inside AddAuraHolder/
    // RemoveHolder, i.e. while the outer pass is mid-iteration; a subscriber that runs another area-aura update is the only way to re-enter.
    // The nested pass must then use fresh locals (the fallback) and leave the outer pass's scratch alone.

    private static readonly string[] ScratchFlags =
        ["_areaSourceScratchInUse", "_areaMemberScratchInUse", "_areaInRangeScratchInUse", "_areaChildScratchInUse"];

    private static bool[] Flags(SpellSystem system) => [.. ScratchFlags.Select(name =>
        (bool)typeof(SpellSystem).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(system)!)];

    /// <summary>Two paladins in two parties; A's party has B and C, D's party has E and F. A toggle moves B, C, E and F in or out of range.</summary>
    private sealed class NestedScenario : IDisposable
    {
        public SpellTestKit Kit;
        public Player A, B, C, D, E, F;
        public SpellAuraHolder SourceA, SourceD;
        private readonly bool _entering;

        public NestedScenario(bool entering)
        {
            _entering = entering;
            Kit = AreaAuraUpdateTests.Kit(out FakeGroups groups);
            (A, _) = Kit.AddPlayer(1, 0, 0);
            (B, _) = Kit.AddPlayer(2, 10, 0);
            (C, _) = Kit.AddPlayer(3, 12, 0);
            (D, _) = Kit.AddPlayer(4, 0, 200);
            (E, _) = Kit.AddPlayer(5, 10, 200);
            (F, _) = Kit.AddPlayer(6, 12, 200);
            groups.Parties.Add([A.Guid, B.Guid, C.Guid]);
            groups.Parties.Add([D.Guid, E.Guid, F.Guid]);
            if (entering)
            {
                MoveMembers(inRange: false);
            }

            Kit.System.CastSpell(A, Devotion, SpellCastTargets.ForSelf(), triggered: true);
            Kit.System.CastSpell(D, Devotion, SpellCastTargets.ForSelf(), triggered: true);
            Kit.Advance(100);
            SourceA = Assert.Single(Kit.System.GetAuras(A));
            SourceD = Assert.Single(Kit.System.GetAuras(D));
            // Precondition: the steady state before the transition is the opposite of the one under test.
            Assert.Equal(entering ? 0 : 2, SourceA.AreaChildren.Count);
            Assert.Equal(entering ? 0 : 2, SourceD.AreaChildren.Count);
        }

        /// <summary>Moves the four members across the radius boundary.</summary>
        public void MoveMembers(bool inRange)
        {
            float x = inRange ? 10 : 100;
            B.Relocate(x, 0, B.Z, 0, Kit.Now);
            C.Relocate(x + 2, 0, C.Z, 0, Kit.Now);
            E.Relocate(x, 200, E.Z, 0, Kit.Now);
            F.Relocate(x + 2, 200, F.Z, 0, Kit.Now);
        }

        public void Transition() => MoveMembers(inRange: _entering);

        public void Reverse() => MoveMembers(inRange: !_entering);

        /// <summary>Per player, the sorted (spell, caster, is-child) of every live aura, plus each source's child guid set.</summary>
        public string[] Snapshot()
        {
            string Of(string name, Player p) => $"{name}=" + string.Join(";", Kit.System.GetAuras(p).Where(h => !h.IsRemoved)
                .Select(h => $"{h.Spell.Id}/{h.CasterGuid}/{(h.AreaParent is null ? "src" : "child")}").Order());
            string Kids(string name, SpellAuraHolder s) => $"{name}.children=" + string.Join(";", s.AreaChildren.Keys.Select(g => g.ToString()).Order());
            return
            [
                Of("A", A), Of("B", B), Of("C", C), Of("D", D), Of("E", E), Of("F", F),
                Kids("SrcA", SourceA), Kids("SrcD", SourceD),
            ];
        }

        public void Dispose() => Kit.Dispose();
    }

    /// <summary>
    /// Runs A's pass after the transition. Nested: the handler (raised for A's own child, from inside the outer pass) runs
    /// the target's pass once (the same caster, or the other source). Baseline: no handler; D's pass is run after A's, so both give the same end state.
    /// </summary>
    private static (string[] Snapshot, int NestedCalls, bool[] FlagsInNested) RunTransition(bool entering, bool nested, bool sameCaster, bool onAdd)
    {
        using var s = new NestedScenario(entering);
        int calls = 0;
        bool[] flagsInNested = [];
        SpellSystem system = s.Kit.System;
        void Handler(SpellAuraHolder holder)
        {
            if (!nested || holder.AreaParent is null || holder.CasterGuid != s.A.Guid || calls >= 1)
            {
                return;
            }

            calls++;
            flagsInNested = Flags(system);
            system.UpdateAreaAurasOf(sameCaster ? s.A : s.D);
            // The nested pass must not release scratch the outer pass still holds.
            Assert.Equal(flagsInNested, Flags(system));
        }

        if (onAdd)
        {
            system.HolderAdded += Handler;
        }
        else
        {
            system.HolderRemoved += Handler;
        }

        s.Transition();
        system.UpdateAreaAurasOf(s.A);
        if (!nested && !sameCaster)
        {
            system.UpdateAreaAurasOf(s.D);
        }

        if (nested)
        {
            Assert.Equal(1, calls); // the nested pass really ran, inside the outer one
        }

        // The flags are all released once the outer pass returns.
        Assert.All(Flags(system), f => Assert.False(f));

        if (onAdd)
        {
            system.HolderAdded -= Handler;
        }
        else
        {
            system.HolderRemoved -= Handler;
        }

        string[] snapshot = s.Snapshot();

        // A later non-nested pass still works: everything moves back and the children are undone / regranted by plain passes.
        s.Reverse();
        system.UpdateAreaAurasOf(s.A);
        system.UpdateAreaAurasOf(s.D);
        Assert.Equal(entering ? 0 : 2, s.SourceA.AreaChildren.Count);
        Assert.Equal(entering ? 0 : 2, s.SourceD.AreaChildren.Count);
        Assert.All(Flags(system), f => Assert.False(f));
        foreach (Player member in new[] { s.B, s.C, s.E, s.F })
        {
            Assert.Equal(entering ? 0 : 1, system.GetAuras(member).Count(h => !h.IsRemoved));
        }

        return (snapshot, calls, flagsInNested);
    }

    [Fact]
    public void NestedAreaAuraUpdate_OtherSourceOnHolderAdded_MatchesTheNonNestedChildren()
    {
        var baseline = RunTransition(entering: true, nested: false, sameCaster: false, onAdd: true);
        var nested = RunTransition(entering: true, nested: true, sameCaster: false, onAdd: true);

        Assert.Equal(baseline.Snapshot, nested.Snapshot);
        Assert.Contains("SrcA.children=", nested.Snapshot[6]);
        Assert.Equal(2, nested.Snapshot[6].Split(';').Length); // both of A's members got a child
        Assert.Equal(2, nested.Snapshot[7].Split(';').Length); // both of D's members, added by the nested pass
        // The outer pass was in its member loop: source, in-range and member scratch were held, so the nested pass took the fallbacks.
        Assert.Equal([true, true, true, false], nested.FlagsInNested);
    }

    [Fact]
    public void NestedAreaAuraUpdate_OtherSourceOnHolderRemoved_MatchesTheNonNestedRemovals()
    {
        var baseline = RunTransition(entering: false, nested: false, sameCaster: false, onAdd: false);
        var nested = RunTransition(entering: false, nested: true, sameCaster: false, onAdd: false);

        Assert.Equal(baseline.Snapshot, nested.Snapshot);
        Assert.Equal("SrcA.children=", nested.Snapshot[6]);
        Assert.Equal("SrcD.children=", nested.Snapshot[7]);
        // The outer pass was in its child-removal loop: source, in-range and child scratch were held, the member scratch was released.
        Assert.Equal([true, false, true, true], nested.FlagsInNested);
    }

    [Fact]
    public void NestedAreaAuraUpdate_SameCasterOnHolderAdded_MatchesTheNonNestedChildren()
    {
        var baseline = RunTransition(entering: true, nested: false, sameCaster: true, onAdd: true);
        var nested = RunTransition(entering: true, nested: true, sameCaster: true, onAdd: true);

        Assert.Equal(baseline.Snapshot, nested.Snapshot);
        Assert.Equal(2, nested.Snapshot[6].Split(';').Length);
        Assert.Equal([true, true, true, false], nested.FlagsInNested);
    }

    [Fact]
    public void NestedAreaAuraUpdate_SameCasterOnHolderRemoved_MatchesTheNonNestedRemovals()
    {
        var baseline = RunTransition(entering: false, nested: false, sameCaster: true, onAdd: false);
        var nested = RunTransition(entering: false, nested: true, sameCaster: true, onAdd: false);

        Assert.Equal(baseline.Snapshot, nested.Snapshot);
        Assert.Equal("SrcA.children=", nested.Snapshot[6]);
        Assert.Equal([true, false, true, true], nested.FlagsInNested);
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
