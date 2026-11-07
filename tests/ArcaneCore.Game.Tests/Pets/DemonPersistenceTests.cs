using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Pets.PetAuras;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// A warlock's demon is saved as the current pet and comes back after a relog with its number, name and spell; a dismissal, replacement or reagent
/// refund removes only the current row and keeps the demon under its entry, which the next summon of that entry loads (vmangos Pet::SavePetToDB / LoadPetFromDB,
/// Pet.cpp:130-420, 520-600; Unit::EffectSummonPet, SpellEffects.cpp:3221); a dead owner or one on a taxi gets no pet at login (Pet.cpp:217, Player.cpp:20941);
/// and a demon is named from the pet name parts (ObjectMgr::GeneratePetName, ObjectMgr.cpp:8607). Imp 416 and Summon Imp 688 are the retail ids; the name parts are invented.
/// </summary>
public sealed class DemonPersistenceTests : IDisposable
{
    private const uint Imp = 416;
    private const uint Voidwalker = 1860;
    private const uint SummonImp = 688;
    private const uint SummonVoidwalker = 697;
    private const uint Beast = 5002;
    private const uint SoulShard = 6265;
    private const uint DemonType = 3;
    private const uint Gryphon = 3837;
    private const uint GryphonDisplay = 1147;

    private readonly SpellTestKit _spells;
    private readonly Map _map;
    private readonly CreatureMapSystem _creatures;
    private readonly SummonService _service;
    private readonly ItemTemplateStore _items;

    public DemonPersistenceTests()
    {
        static SpellInfo Instant(uint id, uint creature) => Spell(id, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)creature))
            with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

        _spells = new SpellTestKit(Instant(SummonImp, Imp), Instant(SummonVoidwalker, Voidwalker) with { Reagents = [new SpellReagent(SoulShard, 1)] });
        _items = new ItemTemplateStore(
            [.. ItemTestData.Templates, new ItemTemplate { Entry = SoulShard, Class = 15, Name = "Soul Shard", DisplayId = 6009, Quality = 1, MaxCount = 0 }],
            []);
        _map = _spells.World.GetMap(0);
        CreatureContent content = CreatureTestKit.Content(
            [.. new[] { Imp, Voidwalker, Beast }.Select(entry => CreatureTestKit.Template(entry, b =>
            {
                b.Name = $"Demon {entry}";
                b.Faction = 14;
                b.MinLevel = 5;
                b.MaxLevel = 5;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
            }) with { CreatureType = entry == Beast ? 1u : DemonType })],
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
        player.MaxHealth = 400;
        player.Health = 400;
        player.Inventory.Templates = _items;
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        return player;
    }

    private static uint Shards(Player player) => player.Inventory.GetItemCount(SoulShard);

    /// <summary>A real flight of node 1 to node 2 whose end calls the resummon, as NpcServicesFeature.OnFlightEnded does (NpcServicesFeatureTaxiTests proves that wiring).</summary>
    private TaxiFlightSystem Flights()
    {
        var flights = new TaxiFlightSystem(
            new NpcStore(NpcContent.Empty with
            {
                TaxiNodes =
                [
                    new TaxiNode { Id = 1, MapId = 0, X = 0, Y = 0, Z = 83.5f, MountAlliance = Gryphon, MountHorde = Gryphon },
                    new TaxiNode { Id = 2, MapId = 0, X = 64, Y = 0, Z = 83.5f, MountAlliance = Gryphon, MountHorde = Gryphon },
                ],
                TaxiPaths = [new TaxiPath { Id = 10, FromNode = 1, ToNode = 2, Price = 100 }],
            }),
            new TaxiPathNodeCatalog([new TaxiPathNodeRecord(1, 10, 0, 0, 0, 0, 83.5f, 0, 0), new TaxiPathNodeRecord(2, 10, 1, 0, 64, 0, 83.5f, 0, 0)]),
            e => e == Gryphon ? GryphonDisplay : 0u, () => 0u);
        flights.FlightEnded += owner => _service.ResummonTemporarilyUnsummonedPet(owner);
        return flights;
    }

    private static void ResumeFlight(TaxiFlightSystem flights, Player owner)
        => Assert.True(flights.ResumeFlight(owner, new TaxiFlightRoute([1, 2], [10], [0]), Gryphon, (_, _) => true));

    /// <summary>An in-memory character_pet current row (the store seams the world wires to the database), so a relog reads what the saves and deletes left.</summary>
    private sealed class CurrentRow
    {
        public PersistentPetSnapshot? Row;
        public readonly List<PersistentPetSnapshot> Demons = [];

        public void Wire(SummonService service)
        {
            service.SavePersistence = (snapshot, _) =>
            {
                Row = snapshot;
                if (snapshot.CreatedBySpell != 0) // EfPersistentPetStore.WriteDemonRowAsync: the demon's per-entry row is written with the current row
                {
                    Demons.RemoveAll(d => d.Entry == snapshot.Entry);
                    Demons.Add(snapshot with { IsCurrent = false });
                }

                return Task.CompletedTask;
            };
            service.DeletePersistence = (_, _) => { Row = null; return Task.CompletedTask; };
            service.LoadPersistence = (_, _) => Task.FromResult(Row);
            service.LoadDemonsPersistence = (_, _) => Task.FromResult<IReadOnlyList<PersistentPetSnapshot>>([.. Demons]);
        }
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
    public void ADeadDemonAForeignSnapshotAndANonDemonEntry_AreNotRestored()
    {
        Player warlock = Warlock();
        PersistentPetSnapshot alive = new(1, 77, Imp, 10, 0, 50, 10, 0, 1, [], [], CreatedBySpell: SummonImp);

        Assert.Null(_service.RestoreCurrentPet(warlock, alive with { Entry = Beast })); // the entry is a creature, not a demon
        Assert.Null(_service.RestoreCurrentPet(warlock, alive with { Health = 0 }));
        Assert.Null(_service.RestoreCurrentPet(warlock, alive with { CharacterId = 2 }));
        Assert.Null(_service.RestoreCurrentPet(warlock, alive with { IsCurrent = false }));
        Assert.NotNull(_service.RestoreCurrentPet(warlock, alive));
    }

    [Fact]
    public async Task ADismissedOrReplacedDemon_LeavesTheCurrentSlot_ButKeepsItsEntryRow()
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
        Assert.Equal(imp.Summon!.Charm!.PetNumber, second.Summon!.Charm!.PetNumber); // the saved imp, not a new pet
        Creature voidwalker = SummonDemon(warlock, SummonVoidwalker); // replaces the imp
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, deletes);
        Assert.Null(_creatures.FindCreature(second.Guid));
        Assert.NotEqual(second.Summon.Charm.PetNumber, voidwalker.Summon!.Charm!.PetNumber);
    }

    [Fact]
    public void ADismissedDemon_SummonedAgain_KeepsItsNumberNameAndSpell_AfterAVoidwalkerSwap()
    {
        _service.Content = new PetContent([], [], [new PetNamePart(Imp, false, "Zor"), new PetNamePart(Imp, true, "ax")]);
        Player warlock = Warlock();
        Creature imp = SummonDemon(warlock, SummonImp);
        uint number = imp.Summon!.Charm!.PetNumber;
        imp.Summon.Charm.Name = "Kept";

        SummonDemon(warlock, SummonVoidwalker); // Unit::UnsummonOldPetBeforeNewSummon dismisses the imp (PET_SAVE_NOT_IN_SLOT)
        Assert.Null(_creatures.FindCreature(imp.Guid));

        Creature again = SummonDemon(warlock, SummonImp);

        Assert.NotEqual(imp.Guid, again.Guid);
        Assert.Equal((number, Imp, "Kept", SummonImp), (again.Summon!.Charm!.PetNumber, again.Entry, again.Summon.Charm.Name, again.Summon.SpellId));
        Assert.Equal(again.Guid, warlock.PetGuid);
    }

    [Fact]
    public async Task ASavedDemonOfTheLoginRead_IsSummonedByItsEntry_WithItsSavedNameAndNumber()
    {
        CurrentRow store = new();
        store.Wire(_service);
        store.Demons.Add(new PersistentPetSnapshot(1, 91, Imp, 10, 0, 60, 10, 0, 1, [], [], IsCurrent: false, Name: "Saved", CreatedBySpell: SummonImp));
        Player warlock = Warlock();

        Assert.Null(await _service.ReadCurrentPetAsync(warlock)); // nothing is current: the demon was dismissed before the logout
        Creature imp = SummonDemon(warlock, SummonImp);

        Assert.Equal((91u, "Saved"), (imp.Summon!.Charm!.PetNumber, imp.Summon.Charm.Name));
    }

    [Fact]
    public async Task ADeadOwnersLogin_DoesNotRestoreTheDemon_OrRefundItsShard_AndKeepsItForTheResurrection()
    {
        CurrentRow store = new();
        store.Wire(_service);
        Player warlock = Warlock();
        Creature voidwalker = SummonDemon(warlock, SummonVoidwalker);
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        PersistentPetSnapshot saved = Assert.IsType<PersistentPetSnapshot>(store.Row);
        _service.Unsummon(voidwalker, PetUnsummonReason.OwnerLeft); // the logout keeps the current row

        _map.Combat.KillPlayer(warlock); // the login of a ghost: DeathState.Dead (PlayerLife)
        _spells.World.RunTick(100);
        for (int relog = 0; relog < 3; relog++)
        {
            PersistentPetSnapshot current = Assert.IsType<PersistentPetSnapshot>(await _service.ReadCurrentPetAsync(warlock));
            Assert.Null(_service.RestoreCurrentPet(warlock, current)); // IsPetNeedBeTemporaryUnsummoned
            Assert.True(warlock.PetGuid.IsEmpty);
            _spells.World.RunTick(100);
            Assert.DoesNotContain(_creatures.Creatures, c => c.IsPet);
            Assert.Equal(0u, Shards(warlock));
        }

        Assert.Equal(saved.PetNumber, Assert.IsType<PersistentPetSnapshot>(store.Row).PetNumber); // the row stays the current one
        Assert.True(_service.HasTemporarilyUnsummonedPet(warlock));
        Assert.Null(_service.ResummonTemporarilyUnsummonedPet(warlock)); // still dead
    }

    [Fact]
    public void ATaxiLogin_OnlyRemembersTheDemon_AndTheEndOfTheFlightBringsItBack()
    {
        Player warlock = Warlock();
        PersistentPetSnapshot current = new(1, 77, Imp, 10, 0, 50, 10, 0, 1, [], [], CreatedBySpell: SummonImp);
        TaxiFlightSystem flights = Flights();
        ResumeFlight(flights, warlock);

        Assert.Null(_service.RestoreCurrentPet(warlock, current));
        Assert.True(_service.HasTemporarilyUnsummonedPet(warlock));
        Assert.Null(_service.ResummonTemporarilyUnsummonedPet(warlock)); // still in the air: IsPetNeedBeTemporaryUnsummoned
        Assert.True(warlock.PetGuid.IsEmpty);

        flights.LandNow(warlock);

        Creature back = Assert.IsType<Creature>(_map.FindObject(warlock.PetGuid));
        Assert.Equal(77u, back.Summon!.Charm!.PetNumber);
        Assert.False(_service.HasTemporarilyUnsummonedPet(warlock));
    }

    [Fact]
    public async Task AnOwnersDeath_RefundsTheShardOnce_AndTheRelogDoesNotBringTheDemonBackForAnotherRefund()
    {
        CurrentRow store = new();
        store.Wire(_service);
        Player warlock = Warlock();
        SummonDemon(warlock, SummonVoidwalker);
        Assert.Equal(0u, Shards(warlock));

        _map.Combat.KillPlayer(warlock);
        _spells.World.RunTick(100);
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(_creatures.Creatures, c => c.IsPet);
        Assert.Equal(1u, Shards(warlock)); // given back at the death
        Assert.Null(store.Row);            // and the demon no longer holds the current slot

        for (int relog = 0; relog < 2; relog++)
        {
            Assert.Null(await _service.ReadCurrentPetAsync(warlock)); // nothing for the login to restore
            _spells.World.RunTick(100);
            Assert.Equal(1u, Shards(warlock)); // no second refund
        }
    }

    [Fact]
    public async Task ALeashBreak_RefundsTheShardOnce_AndTheRelogDoesNotRestoreTheDemon()
    {
        CurrentRow store = new();
        store.Wire(_service);
        Player warlock = Warlock();
        Creature voidwalker = SummonDemon(warlock, SummonVoidwalker);
        warlock.SetPetGuid(ObjectGuid.Empty); // the owner has no pet any more (Pet::Update: IsControlled() && !owner->GetPetGuid())

        _spells.World.RunTick(100);
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(_creatures.FindCreature(voidwalker.Guid));
        Assert.Equal(1u, Shards(warlock));
        Assert.Null(store.Row);
        Assert.Null(await _service.ReadCurrentPetAsync(warlock));
        Assert.Equal(1u, Shards(warlock));
    }

    [Fact]
    public async Task AWoundedDemon_DismissedAndSummonedAgain_ComesBackAtFullHealthAndMana()
    {
        // vmangos Pet::LoadPetFromDB: a SUMMON_PET that is not the current load starts full (Pet.cpp:360-364); only the login's current load keeps saved values
        Player warlock = Warlock();
        Creature imp = SummonDemon(warlock, SummonImp);
        uint number = imp.Summon!.Charm!.PetNumber;
        imp.Health = 10;
        imp.SetUInt32(UpdateFields.UnitFieldPower1, 0);

        _service.AbandonSavedDemon(warlock, imp);
        _service.Unsummon(imp, PetUnsummonReason.Dismissed);
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Creature again = SummonDemon(warlock, SummonImp);

        Assert.Equal(number, again.Summon!.Charm!.PetNumber);
        Assert.True(again.MaxHealth > 10u);
        Assert.Equal(again.MaxHealth, again.Health);
        Assert.Equal(again.GetUInt32(UpdateFields.UnitFieldMaxpower1), again.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void AWoundedDemon_ThatLogsInAsTheCurrentPet_KeepsItsSavedHealth()
    {
        Player warlock = Warlock();
        PersistentPetSnapshot current = new(1, 77, Imp, 10, 0, 33, 10, 0, 1, [], [], CreatedBySpell: SummonImp);

        Creature back = Assert.IsType<Creature>(_service.RestoreCurrentPet(warlock, current));

        Assert.Equal(33u, back.Health);
    }

    [Fact]
    public async Task ADemonThatDiedAndDecayed_KeepsItsNumberAndName_WhenItsEntryIsSummonedAgain()
    {
        // vmangos loads a saved SUMMON_PET by owner and entry only (Pet.cpp:146-153) and applies the dead state only to a current load (Pet.cpp:415)
        _service.Content = new PetContent([], [], [new PetNamePart(Imp, false, "Zor"), new PetNamePart(Imp, true, "ax")]);
        CurrentRow store = new();
        store.Wire(_service);
        Player warlock = Warlock();
        Creature imp = SummonDemon(warlock, SummonImp);
        uint number = imp.Summon!.Charm!.PetNumber;
        string name = imp.Summon.Charm.Name;

        _creatures.KillCreature(imp);
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0u, Assert.IsType<PersistentPetSnapshot>(store.Row).Health);

        _creatures.Despawn(imp); // the corpse decayed before the pet system's tick
        _map.Pets!.Update(_map, 1);
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(warlock.PetGuid.IsEmpty);
        PersistentPetSnapshot row = Assert.IsType<PersistentPetSnapshot>(store.Row);
        Assert.Equal(0u, row.Health); // the row keeps the death: a relog does not restore it alive
        Assert.Null(_service.RestoreCurrentPet(warlock, row));

        Creature again = SummonDemon(warlock, SummonImp);

        Assert.Equal((number, name), (again.Summon!.Charm!.PetNumber, again.Summon.Charm.Name));
        Assert.Equal(again.MaxHealth, again.Health);
    }

    [Fact]
    public async Task ADemonThatDiedBeforeALogout_IsNotRestoredAtLogin_ButSummonedByItsEntryWithItsNumber()
    {
        CurrentRow store = new();
        store.Wire(_service);
        Player warlock = Warlock();
        Creature imp = SummonDemon(warlock, SummonImp);
        uint number = imp.Summon!.Charm!.PetNumber;
        _creatures.KillCreature(imp);
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        _service.Unsummon(imp, PetUnsummonReason.OwnerLeft);

        PersistentPetSnapshot current = Assert.IsType<PersistentPetSnapshot>(await _service.ReadCurrentPetAsync(warlock)); // the login read
        Assert.Null(_service.RestoreCurrentPet(warlock, current));

        Assert.Equal(number, SummonDemon(warlock, SummonImp).Summon!.Charm!.PetNumber);
    }

    [Fact]
    public async Task ADemonDismissedBeforeALogout_IsSummonedByItsEntryAfterTheRelog_WithItsNumberAndName()
    {
        // the login read loads the saved demons from the store (the production wiring is pinned in World.Tests), so a dismissed demon is not lost at the relog
        CurrentRow store = new();
        store.Wire(_service);
        Player warlock = Warlock();
        Creature imp = SummonDemon(warlock, SummonImp);
        uint number = imp.Summon!.Charm!.PetNumber;
        imp.Summon.Charm.Name = "Kept";
        _service.AbandonSavedDemon(warlock, imp);
        _service.Unsummon(imp, PetUnsummonReason.Dismissed);
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(store.Row);
        Assert.Single(store.Demons);

        Assert.Null(await _service.ReadCurrentPetAsync(warlock)); // the relog
        Creature again = SummonDemon(warlock, SummonImp);

        Assert.Equal((number, "Kept"), (again.Summon!.Charm!.PetNumber, again.Summon.Charm.Name));
    }

    [Fact]
    public async Task TheDismissCommandAndAbandon_LeaveTheCurrentSlot_AndTheDemonIsKeptByItsEntry()
    {
        CurrentRow store = new();
        store.Wire(_service);
        PetController controller = new(_service, () => _spells.System, new Random(5));
        Player warlock = Warlock();
        Creature imp = SummonDemon(warlock, SummonImp);
        uint number = imp.Summon!.Charm!.PetNumber;

        controller.HandleCommand(imp, CommandState.Dismiss, null); // the pet bar's dismiss
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(_creatures.FindCreature(imp.Guid));
        Assert.Null(store.Row);
        Assert.Null(await _service.ReadCurrentPetAsync(warlock));

        Creature second = SummonDemon(warlock, SummonImp);
        Assert.Equal(number, second.Summon!.Charm!.PetNumber);
        controller.HandleAbandon(warlock, second.Guid); // CMSG_PET_ABANDON on a demon dismisses it
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(_creatures.FindCreature(second.Guid));
        Assert.Null(store.Row);
        Assert.Null(await _service.ReadCurrentPetAsync(warlock));
        Assert.Equal(number, SummonDemon(warlock, SummonImp).Summon!.Charm!.PetNumber);
    }

    [Fact]
    public async Task ATalentReset_SendsTheDemonOutOfTheCurrentSlot_SoARelogDoesNotRestoreIt()
    {
        // vmangos Player::ResetTalents ends with RemovePet(PET_SAVE_REAGENTS) (Player.cpp:4146), stored as PET_SAVE_NOT_IN_SLOT (Pet.cpp:1050)
        CurrentRow store = new();
        store.Wire(_service);
        Player warlock = Warlock();
        Creature imp = SummonDemon(warlock, SummonImp);
        uint number = imp.Summon!.Charm!.PetNumber;
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(store.Row);
        using TalentService talents = new(new TalentCatalog([], []), _spells.System, new TalentOptions(), () => 1_800_000_000);
        using TalentPetHooks hooks = TalentPetHooks.Attach(talents, _service, _spells.System);

        hooks.OnTalentsReset(warlock);
        await _service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(warlock.PetGuid.IsEmpty);
        Assert.Null(_creatures.FindCreature(imp.Guid));
        Assert.Null(store.Row);
        Assert.Null(await _service.ReadCurrentPetAsync(warlock)); // the relog: nothing to restore
        Assert.Equal(number, SummonDemon(warlock, SummonImp).Summon!.Charm!.PetNumber); // and the demon is still its entry's saved one
    }

    [Fact]
    public void ADemonRememberedAtATaxiLogin_ComesBackWithItsSavedCooldowns()
    {
        Player warlock = Warlock();
        long ends = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;
        PersistentPetSnapshot current = new(1, 77, Imp, 10, 0, 50, 10, 0, 1, [], [], CreatedBySpell: SummonImp,
            Cooldowns: [new PersistentPetCooldown(0, SummonImp, 0, ends)]);
        TaxiFlightSystem flights = Flights();
        ResumeFlight(flights, warlock);
        Assert.Null(_service.RestoreCurrentPet(warlock, current));

        flights.LandNow(warlock);

        Creature back = Assert.IsType<Creature>(_map.FindObject(warlock.PetGuid));
        Assert.NotEmpty(_spells.System.GetActiveCooldowns(back));
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
