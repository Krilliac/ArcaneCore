using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// A warlock's demon is saved as the current pet and comes back after a relog with its number, name and spell; a dismissal or replacement
/// deletes the saved row (vmangos Pet::SavePetToDB / LoadPetFromDB, Pet.cpp:130-420, 520-600), and a demon is named from the pet name parts
/// (ObjectMgr::GeneratePetName, ObjectMgr.cpp:8607). Imp 416 and Summon Imp 688 are the retail ids; the name parts are invented.
/// </summary>
public sealed class DemonPersistenceTests : IDisposable
{
    private const uint Imp = 416;
    private const uint Voidwalker = 1860;
    private const uint SummonImp = 688;
    private const uint SummonVoidwalker = 697;
    private const uint DemonType = 3;

    private readonly SpellTestKit _spells;
    private readonly Map _map;
    private readonly CreatureMapSystem _creatures;
    private readonly SummonService _service;

    public DemonPersistenceTests()
    {
        static SpellInfo Instant(uint id, uint creature) => Spell(id, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)creature))
            with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

        _spells = new SpellTestKit(Instant(SummonImp, Imp), Instant(SummonVoidwalker, Voidwalker));
        _map = _spells.World.GetMap(0);
        CreatureContent content = CreatureTestKit.Content(
            [.. new[] { Imp, Voidwalker }.Select(entry => CreatureTestKit.Template(entry, b =>
            {
                b.Name = $"Demon {entry}";
                b.Faction = 14;
                b.MinLevel = 5;
                b.MaxLevel = 5;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
            }) with { CreatureType = DemonType })],
            []);
        _creatures = new CreatureMapSystem(_map, content, random: new Random(1));
        _map.AddUpdater(_creatures);
        _service = new SummonService(systems: map => ReferenceEquals(map, _map) ? _creatures : null, random: new Random(3));
        _spells.System.Units = new MapObjectResolver();
        _service.Install(_spells.System);
        _spells.System.Summons = _service;
        PetTargets.Install(_spells.System);
        SpellScriptDispatcher.Install(_spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        _service.InstallDemons(_spells.System);
    }

    public void Dispose() => _spells.Dispose();

    private Player Warlock(uint guid = 1)
    {
        (Player player, _) = _spells.AddPlayer(guid);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        player.Level = 10;
        return player;
    }

    private Creature SummonDemon(Player warlock, uint spell)
    {
        Assert.Equal(SpellCastResult.CastOk, _spells.System.CastSpell(warlock, spell, SpellCastTargets.ForSelf(), triggered: true));
        return Assert.Single(_creatures.Creatures, c => c.IsPet && c.IsAlive);
    }

    [Fact]
    public void ASummonedDemon_IsNamedFromTheNameParts_AndKeepsTheCreatureNameWithoutThem()
    {
        _service.Content = new PetContent([], [], [new PetNamePart(Imp, false, "Zor"), new PetNamePart(Imp, true, "ax")]);
        Player warlock = Warlock();

        Creature imp = SummonDemon(warlock, SummonImp);
        Assert.Equal("Zorax", imp.Summon!.Charm!.Name);

        Creature voidwalker = SummonDemon(warlock, SummonVoidwalker); // no parts for 1860
        Assert.Equal($"Demon {Voidwalker}", voidwalker.Summon!.Charm!.Name);
    }

    [Fact]
    public async Task ARelog_BringsTheDemonBack_WithItsNumberNameAndSpell()
    {
        _service.Content = new PetContent([], [], [new PetNamePart(Imp, false, "Zor"), new PetNamePart(Imp, true, "ax")]);
        PersistentPetSnapshot? saved = null;
        _service.SavePersistence = (snapshot, _) => { saved = snapshot; return Task.CompletedTask; };
        Player warlock = Warlock();
        Creature imp = SummonDemon(warlock, SummonImp);
        uint number = imp.Summon!.Charm!.PetNumber;
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(saved);
        Assert.Equal((number, Imp, SummonImp, "Zorax", true), (saved.PetNumber, saved.Entry, saved.CreatedBySpell, saved.Name, saved.IsCurrent));

        // the logout: the pet goes with the owner (OwnerLeft keeps the saved row), the next login restores it from the row
        _service.Unsummon(imp, PetUnsummonReason.OwnerLeft);
        Assert.True(warlock.PetGuid.IsEmpty);
        Creature? back = _service.RestoreCurrentPet(warlock, saved);

        Assert.NotNull(back);
        Assert.NotEqual(imp.Guid, back.Guid);
        Assert.Equal(back.Guid, warlock.PetGuid);
        Assert.Equal((number, Imp, "Zorax"), (back.Summon!.Charm!.PetNumber, back.Entry, back.Summon.Charm.Name));
        Assert.Equal(SummonImp, back.Summon.SpellId);
        Assert.Equal(10, back.Level);
    }

    [Fact]
    public void ADeadDemonAnotherClassAndAForeignSnapshot_AreNotRestored()
    {
        Player warlock = Warlock();
        PersistentPetSnapshot alive = new(1, 77, Imp, 10, 0, 50, 10, 0, 1, [], [], CreatedBySpell: SummonImp);

        Assert.Null(_service.RestoreCurrentPet(warlock, alive with { Health = 0 }));
        Assert.Null(_service.RestoreCurrentPet(warlock, alive with { CharacterId = 2 }));
        Assert.Null(_service.RestoreCurrentPet(warlock, alive with { IsCurrent = false }));
        Assert.NotNull(_service.RestoreCurrentPet(warlock, alive));
    }

    [Fact]
    public async Task ADismissedOrReplacedDemon_DeletesTheSavedRow()
    {
        int deletes = 0;
        _service.DeletePersistence = (_, _) => { Interlocked.Increment(ref deletes); return Task.CompletedTask; };
        Player warlock = Warlock();
        Creature imp = SummonDemon(warlock, SummonImp);

        _service.AbandonSavedDemon(warlock, imp);
        _service.Unsummon(imp, PetUnsummonReason.Dismissed);
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, deletes);

        Creature second = SummonDemon(warlock, SummonImp);
        SummonDemon(warlock, SummonVoidwalker); // replaces the imp
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, deletes);
        Assert.Null(_creatures.FindCreature(second.Guid));
    }

    [Fact]
    public void CaptureCurrentPet_TakesAWarlocksDemon_AndNothingForAnotherClass()
    {
        Player warlock = Warlock();
        SummonDemon(warlock, SummonImp);
        Assert.NotNull(_service.CaptureCurrentPet(warlock));

        warlock.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        Assert.Null(_service.CaptureCurrentPet(warlock));
    }
}
