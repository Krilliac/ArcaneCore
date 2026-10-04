using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>SPELL_AURA_TRANSFORM (mangoszero Aura::HandleAuraTransform, SpellAuraShapeshift.cpp:502-598): the display change, its restore and the override rules.</summary>
public sealed class TransformAuraTests
{
    private const uint SheepEntry = 1001;
    private const uint CatEntry = 1002;
    private const uint SheepDisplay = 4500;
    private const uint CatDisplay = 4600;
    private const uint NativeHuman = 49;

    private const uint PolymorphSheep = 940001;   // negative: aimed at an enemy
    private const uint PolymorphCat = 940002;     // negative, a different model
    private const uint PolymorphGhost = 940003;   // negative, creature entry nobody knows
    private const uint PotionOfCat = 940004;      // positive: cast on oneself
    private const uint NoModel = 940005;          // misc 0 and not Orb of Deception
    private const uint OrbSpell = TransformAuras.OrbOfDeception;

    private sealed class FakeDisplays : ITransformDisplaySource
    {
        public Dictionary<uint, uint> Entries { get; } = new() { [SheepEntry] = SheepDisplay, [CatEntry] = CatDisplay };

        public List<uint> Lookups { get; } = [];

        public List<uint> NoModelSpells { get; } = [];

        public uint? FindDisplay(uint creatureEntry)
        {
            Lookups.Add(creatureEntry);
            return Entries.TryGetValue(creatureEntry, out uint display) ? display : null;
        }

        public void ReportNoModel(uint spellId) => NoModelSpells.Add(spellId);
    }

    private static SpellInfo Negative(uint id, int misc, int durationMs = 50_000) => Spell(id,
        Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Transform, misc: misc)) with
    {
        Duration = new SpellDuration(durationMs, 0, durationMs),
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        SpellVisual = 1,
    };

    private static SpellInfo Positive(uint id, int misc) => Spell(id,
        Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Transform, misc: misc)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
    };

    private static SpellTestKit Kit(out FakeDisplays displays)
    {
        var kit = new SpellTestKit(
            Negative(PolymorphSheep, (int)SheepEntry),
            Negative(PolymorphCat, (int)CatEntry),
            Negative(PolymorphGhost, 99999),
            Positive(PotionOfCat, (int)CatEntry),
            Negative(NoModel, 0),
            Negative(OrbSpell, 0));
        displays = new FakeDisplays();
        TransformDisplays.Register(kit.World, displays);
        return kit;
    }

    private static (Player Caster, Player Victim) Pair(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);
        victim.NativeDisplayId = NativeHuman;
        victim.DisplayId = NativeHuman;
        return (caster, victim);
    }

    private static void Cast(SpellTestKit kit, Unit caster, uint spell, Unit target)
        => kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    [Fact]
    public void ModuleIsDiscovered_AndTheAuraHasAHandler()
    {
        using SpellTestKit kit = Kit(out _);

        Assert.Contains(typeof(TransformAuras), kit.System.Modules);
        Assert.True(kit.System.HasAuraHandler(AuraType.Transform));
        Assert.Equal(AuraSupportLevel.Handler, AuraSupport.Get(AuraType.Transform).Level);
    }

    [Fact]
    public void Polymorph_SetsTheCreatureDisplay_AndRemovalRestoresTheNativeOne()
    {
        using SpellTestKit kit = Kit(out FakeDisplays displays);
        (Player caster, Player victim) = Pair(kit);

        Cast(kit, caster, PolymorphSheep, victim);

        Assert.Equal(SheepDisplay, victim.DisplayId);
        Assert.Equal(NativeHuman, victim.NativeDisplayId); // the native display is never touched
        Assert.Equal([SheepEntry], displays.Lookups);

        kit.System.RemoveAuras(victim, PolymorphSheep);

        Assert.Equal(NativeHuman, victim.DisplayId);
    }

    [Fact]
    public void AnExpiredPolymorph_RestoresTheNativeDisplay()
    {
        using SpellTestKit kit = Kit(out _);
        (Player caster, Player victim) = Pair(kit);

        Cast(kit, caster, PolymorphSheep, victim);
        Assert.Equal(SheepDisplay, victim.DisplayId);

        kit.Advance(51_000);

        Assert.False(kit.System.HasAura(victim, PolymorphSheep));
        Assert.Equal(NativeHuman, victim.DisplayId);
    }

    [Fact]
    public void ASecondTransform_Overrides_AndTheFirstComesBackWhenTheSecondEnds()
    {
        using SpellTestKit kit = Kit(out _);
        (Player caster, Player victim) = Pair(kit);

        Cast(kit, caster, PolymorphSheep, victim);
        Cast(kit, caster, PolymorphCat, victim);

        Assert.Equal(CatDisplay, victim.DisplayId);
        Assert.True(kit.System.HasAura(victim, PolymorphSheep)); // both auras live: polymorph spells of different ids do not replace each other here

        kit.System.RemoveAuras(victim, PolymorphCat);

        // mangoszero: SetDisplayId(native) then ApplyModifier(true) of the transform that is still on the unit.
        Assert.Equal(SheepDisplay, victim.DisplayId);

        kit.System.RemoveAuras(victim, PolymorphSheep);
        Assert.Equal(NativeHuman, victim.DisplayId);
    }

    [Fact]
    public void RemovingTheOlderTransform_WhileANewerOneIsOn_KeepsTheNewerModel()
    {
        using SpellTestKit kit = Kit(out _);
        (Player caster, Player victim) = Pair(kit);

        Cast(kit, caster, PolymorphSheep, victim);
        Cast(kit, caster, PolymorphCat, victim);
        kit.System.RemoveAuras(victim, PolymorphSheep);

        // The removal resets to native and re-applies what is left: the cat.
        Assert.Equal(CatDisplay, victim.DisplayId);
        kit.System.RemoveAuras(victim, PolymorphCat);
        Assert.Equal(NativeHuman, victim.DisplayId);
    }

    [Fact]
    public void ARecastOfTheSamePolymorph_KeepsTheModel_AndOneRemovalRestores()
    {
        using SpellTestKit kit = Kit(out _);
        (Player caster, Player victim) = Pair(kit);

        Cast(kit, caster, PolymorphSheep, victim);
        Cast(kit, caster, PolymorphSheep, victim);

        Assert.Equal(SheepDisplay, victim.DisplayId);
        Assert.Single(kit.System.GetAuras(victim), h => h.Spell.Id == PolymorphSheep);
        kit.System.RemoveAuras(victim, PolymorphSheep);
        Assert.Equal(NativeHuman, victim.DisplayId);
    }

    [Fact]
    public void APositiveTransformOverANegativeOne_ChangesTheModel_ButTheNegativeStaysTheActiveRecord()
    {
        using SpellTestKit kit = Kit(out _);
        (Player _, Player victim) = Pair(kit);
        (Player caster, _) = kit.AddPlayer(3, 1, 0);

        Cast(kit, caster, PolymorphSheep, victim);
        SpellAuraHolder sheep = Assert.Single(kit.System.GetAuras(victim));
        Assert.Same(sheep, TransformAuras.ActiveHolder(victim));

        kit.System.CastSpell(victim, PotionOfCat, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(CatDisplay, victim.DisplayId);                  // the display always follows the newest transform
        Assert.Same(sheep, TransformAuras.ActiveHolder(victim));             // "not overwriting negative by positive"

        // The positive one ends: native, then the preferred remaining transform (the negative sheep) is applied again.
        kit.System.RemoveAuras(victim, PotionOfCat);
        Assert.Equal(SheepDisplay, victim.DisplayId);
        Assert.Same(sheep, TransformAuras.ActiveHolder(victim));
    }

    [Fact]
    public void ANegativeTransformIsPreferredOverAPositiveOneOnRestore()
    {
        using SpellTestKit kit = Kit(out _);
        (Player caster, Player victim) = Pair(kit);

        kit.System.CastSpell(victim, PotionOfCat, SpellCastTargets.ForSelf(), triggered: true);   // positive first (cat)
        Cast(kit, caster, PolymorphSheep, victim);                                                 // negative second
        Cast(kit, caster, PolymorphCat, victim);                                                   // another negative on top
        Assert.Equal(CatDisplay, victim.DisplayId);

        kit.System.RemoveAuras(victim, PolymorphCat);

        // Of the remaining two the negative sheep wins although the positive potion was applied first.
        Assert.Equal(SheepDisplay, victim.DisplayId);
        Assert.Equal(PolymorphSheep, TransformAuras.ActiveHolder(victim)?.Spell.Id);
    }

    [Fact]
    public void AnUnknownCreatureEntry_TurnsTheTargetIntoThePig()
    {
        using SpellTestKit kit = Kit(out FakeDisplays displays);
        (Player caster, Player victim) = Pair(kit);

        Cast(kit, caster, PolymorphGhost, victim);

        Assert.Equal(TransformAuras.PigDisplay, victim.DisplayId);
        Assert.Equal(16358u, TransformAuras.PigDisplay);
        Assert.Equal([99999u], displays.Lookups);
        kit.System.RemoveAuras(victim, PolymorphGhost);
        Assert.Equal(NativeHuman, victim.DisplayId);
    }

    [Fact]
    public void WithoutADisplaySource_EveryEntryIsUnknown()
    {
        using var kit = new SpellTestKit(Negative(PolymorphSheep, (int)SheepEntry));
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);

        Cast(kit, caster, PolymorphSheep, victim);

        Assert.Equal(TransformAuras.PigDisplay, victim.DisplayId);
    }

    [Theory]
    [InlineData(1479u, 10134u)]
    [InlineData(1478u, 10135u)]
    [InlineData(59u, 10136u)]
    [InlineData(49u, 10137u)]
    [InlineData(50u, 10138u)]
    [InlineData(51u, 10139u)]
    [InlineData(52u, 10140u)]
    [InlineData(53u, 10141u)]
    [InlineData(54u, 10142u)]
    [InlineData(55u, 10143u)]
    [InlineData(56u, 10144u)]
    [InlineData(58u, 10145u)]
    [InlineData(57u, 10146u)]
    [InlineData(60u, 10147u)]
    [InlineData(1563u, 10148u)]
    [InlineData(1564u, 10149u)]
    public void OrbOfDeception_MapsTheWearersNativeDisplay_ToTheOrbModel(uint native, uint orb)
    {
        using SpellTestKit kit = Kit(out FakeDisplays displays);
        (Player caster, Player victim) = Pair(kit);
        victim.NativeDisplayId = native;
        victim.DisplayId = native;

        Cast(kit, caster, OrbSpell, victim);

        Assert.Equal(orb, victim.DisplayId);
        Assert.Empty(displays.Lookups);
        Assert.Empty(displays.NoModelSpells);
        kit.System.RemoveAuras(victim, OrbSpell);
        Assert.Equal(native, victim.DisplayId);
    }

    [Fact]
    public void OrbOfDeception_OnAModelWithNoOrbVariant_LeavesTheDisplayAlone()
    {
        using SpellTestKit kit = Kit(out FakeDisplays displays);
        (Player caster, Player victim) = Pair(kit);
        victim.NativeDisplayId = 7777;
        victim.DisplayId = 7777;

        Cast(kit, caster, OrbSpell, victim);

        Assert.Equal(7777u, victim.DisplayId);
        Assert.Empty(displays.NoModelSpells); // Orb of Deception is the spell the table is for; no error
    }

    [Fact]
    public void AMiscValueZeroTransform_OfAnotherSpell_IsReported_AndChangesNothing()
    {
        using SpellTestKit kit = Kit(out FakeDisplays displays);
        (Player caster, Player victim) = Pair(kit);

        Cast(kit, caster, NoModel, victim);

        Assert.Equal(NativeHuman, victim.DisplayId);
        Assert.Equal([NoModel], displays.NoModelSpells);
        Assert.True(kit.System.HasAura(victim, NoModel));
        kit.System.RemoveAuras(victim, NoModel);
        Assert.Equal(NativeHuman, victim.DisplayId);
    }

    [Fact]
    public void TheNativeDisplay_IsNeverTouchedByATransform()
    {
        using SpellTestKit kit = Kit(out _);
        (Player caster, Player victim) = Pair(kit);
        victim.NativeDisplayId = 1234;
        victim.DisplayId = 1234;

        Cast(kit, caster, PolymorphSheep, victim);
        Assert.Equal(1234u, victim.NativeDisplayId);
        kit.System.RemoveAuras(victim, PolymorphSheep);

        Assert.Equal(1234u, victim.DisplayId);
        Assert.Equal(1234u, victim.NativeDisplayId);
    }
}
