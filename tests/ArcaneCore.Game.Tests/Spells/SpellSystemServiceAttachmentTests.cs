using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Pets.PetAuras;
using ArcaneCore.Game.Quests.Adapters;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Game.Tests.Honor;
using ArcaneCore.Game.Tests.Reputation;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Characterisation of the per-<see cref="SpellSystem"/> service attachments (ConditionalWeakTable lookups): the create-on-first-use,
/// register/replace, find-or-null and remove-on-null semantics each lane has today, and that tables of different lanes do not
/// share entries. Pins the public/internal seams that survive a shared-helper refactor.
/// </summary>
public sealed class SpellSystemServiceAttachmentTests
{
    private static SpellSystem NewSystem() => new(SpellStore.Empty, () => 1000);

    private sealed class TapSource(bool tapped) : ISoulShardTapSource
    {
        public bool IsTappedBy(Creature victim, Player player) => tapped;
    }

    // ---- create on first use ----

    [Fact]
    public void ScriptEffectRegistry_IsCreatedOnFirstUse_AndIsStablePerSystem()
    {
        SpellSystem a = NewSystem();
        SpellSystem b = NewSystem();
        ScriptEffectRegistry first = ScriptEffectRegistry.For(a);
        Assert.Same(first, ScriptEffectRegistry.For(a));
        Assert.NotSame(first, ScriptEffectRegistry.For(b));
    }

    [Fact]
    public void ScriptEffectRegistry_ForNull_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => ScriptEffectRegistry.For(null!));
    }

    [Fact]
    public void ScriptEffectRegistry_DuplicateSpellId_ThrowsWithTheExactMessage()
    {
        ScriptEffectRegistry registry = ScriptEffectRegistry.For(NewSystem());
        registry.Add(4242, _ => { });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => registry.Add(4242, _ => { }));
        Assert.Equal("spell 4242 already has a script effect", ex.Message);
    }

    [Fact]
    public void PetAuraService_IsCreatedOnFirstUse_StablePerSystem_AndKnowsItsSystem()
    {
        SpellSystem a = NewSystem();
        SpellSystem b = NewSystem();
        PetAuraService first = PetAuraService.For(a);
        Assert.Same(first, PetAuraService.For(a));
        Assert.Same(a, first.Spells);
        Assert.NotSame(first, PetAuraService.For(b));
        Assert.Throws<ArgumentNullException>(() => PetAuraService.For(null!));
    }

    [Fact]
    public void SpellMods_TheBuiltInModuleInstalledOneEnginePerSystem()
    {
        SpellSystem a = NewSystem();
        SpellSystem b = NewSystem();
        ISpellModEngine engine = a.Mods;
        Assert.Same(engine, a.Mods);
        Assert.Same(engine, SpellModExtensions.Install(a));
        Assert.NotSame(engine, b.Mods);
        Assert.Same(engine, a.SpellModifiers);
    }

    // ---- register / replace, find returns null ----

    [Fact]
    public void ReputationEnvironment_NullWhenNothingRegistered_ThenRegisterAndReplace()
    {
        SpellSystem system = NewSystem();
        Assert.Null(ReputationEnvironment.For(system));
        var first = new ReputationService(ReputationFixtures.Factions, roll: () => 0.0);
        var second = new ReputationService(ReputationFixtures.Factions, roll: () => 0.0);
        ReputationEnvironment.Register(system, first);
        Assert.Same(first, ReputationEnvironment.For(system));
        ReputationEnvironment.Register(system, second);
        Assert.Same(second, ReputationEnvironment.For(system));
        Assert.Null(ReputationEnvironment.For(NewSystem()));
    }

    [Fact]
    public void ReputationEnvironment_RegisterRejectsNulls()
    {
        var service = new ReputationService(ReputationFixtures.Factions, roll: () => 0.0);
        Assert.Throws<ArgumentNullException>(() => ReputationEnvironment.Register(null!, service));
        Assert.Throws<ArgumentNullException>(() => ReputationEnvironment.Register(NewSystem(), null!));
    }

    [Fact]
    public void HonorService_InstallForSpells_FindsAndReplaces_AndNullChecksBothSides()
    {
        SpellSystem system = NewSystem();
        var clock = new FixedHonorClock(20_000);
        var first = new HonorService(new HonorOptions(), clock, () => 19_997);
        var second = new HonorService(new HonorOptions(), clock, () => 19_997);
        Assert.Null(HonorService.ForSpells(system));
        first.InstallForSpells(system);
        Assert.Same(first, HonorService.ForSpells(system));
        second.InstallForSpells(system);
        Assert.Same(second, HonorService.ForSpells(system));
        Assert.Null(HonorService.ForSpells(NewSystem()));
        Assert.Throws<ArgumentNullException>(() => HonorService.ForSpells(null!));
        Assert.Throws<ArgumentNullException>(() => first.InstallForSpells(null!));
    }

    [Fact]
    public void DuelService_Install_SetsSpells_FindsItAndRegistersTheCastCheck()
    {
        SpellSystem system = NewSystem();
        var service = new DuelService(new DuelOptions(), () => 0L);
        Assert.Null(DuelService.ForSpells(system));
        service.Install(system);
        Assert.Same(system, service.Spells);
        Assert.Same(service, DuelService.ForSpells(system));
        Assert.Null(DuelService.ForSpells(NewSystem()));
        Assert.Throws<ArgumentNullException>(() => service.Install(null!));
    }

    [Fact]
    public void QuestSpellEvents_NullWhenNothingInstalled_ThenInstallAndReplace()
    {
        SpellSystem system = NewSystem();
        Assert.Null(QuestSpellEvents.For(system));
        Action<Player, uint> first = (_, _) => { };
        Action<Player, uint> second = (_, _) => { };
        QuestSpellEvents.Install(system, first);
        Assert.Same(first, QuestSpellEvents.For(system));
        QuestSpellEvents.Install(system, second);
        Assert.Same(second, QuestSpellEvents.For(system));
        Assert.Null(QuestSpellEvents.For(NewSystem()));
        Assert.Throws<ArgumentNullException>(() => QuestSpellEvents.Install(null!, first));
        Assert.Throws<ArgumentNullException>(() => QuestSpellEvents.Install(system, null!));
    }

    // ---- remove-on-null ----

    [Fact]
    public void SoulShardRules_UseTapSource_InstallsReplacesAndNullRemoves()
    {
        SpellSystem system = NewSystem();
        Assert.Null(SoulShardRules.TapSourceOf(system));
        var first = new TapSource(true);
        var second = new TapSource(false);
        SoulShardRules.UseTapSource(system, first);
        Assert.Same(first, SoulShardRules.TapSourceOf(system));
        SoulShardRules.UseTapSource(system, second);
        Assert.Same(second, SoulShardRules.TapSourceOf(system));
        SoulShardRules.UseTapSource(system, null);
        Assert.Null(SoulShardRules.TapSourceOf(system));
        SoulShardRules.UseTapSource(system, null);
        Assert.Null(SoulShardRules.TapSourceOf(system));
        Assert.Null(SoulShardRules.TapSourceOf(NewSystem()));
    }

    // ---- lanes are independent ----

    [Fact]
    public void LanesOnOneSystem_DoNotOverwriteEachOther()
    {
        SpellSystem system = NewSystem();
        var reputation = new ReputationService(ReputationFixtures.Factions, roll: () => 0.0);
        var honor = new HonorService(new HonorOptions(), new FixedHonorClock(20_000), () => 19_997);
        var duel = new DuelService(new DuelOptions(), () => 0L);
        Action<Player, uint> sink = (_, _) => { };
        var tap = new TapSource(true);

        ReputationEnvironment.Register(system, reputation);
        honor.InstallForSpells(system);
        duel.Install(system);
        QuestSpellEvents.Install(system, sink);
        SoulShardRules.UseTapSource(system, tap);

        Assert.Same(reputation, ReputationEnvironment.For(system));
        Assert.Same(honor, HonorService.ForSpells(system));
        Assert.Same(duel, DuelService.ForSpells(system));
        Assert.Same(sink, QuestSpellEvents.For(system));
        Assert.Same(tap, SoulShardRules.TapSourceOf(system));

        SoulShardRules.UseTapSource(system, null);
        Assert.Same(sink, QuestSpellEvents.For(system));
        Assert.Same(honor, HonorService.ForSpells(system));
    }
}
