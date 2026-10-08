using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// A pet goes with its owner through teleports (vmangos Player::UnsummonPetTemporaryIfAny / ResummonPetTemporaryUnSummonedIfAny,
/// Player.cpp:20911-20940): a far teleport puts the pet away before the owner leaves its map (Player.cpp:2045-2048) and brings it
/// back once the owner is in the new map (MovementHandler.cpp:197-198); a same-map teleport does so only when the pet is beyond the
/// grid activation distance of the destination (Player.cpp:1911-1921). Totems, guardians and mini pets are unsummoned and stay gone
/// (Player::RemoveFromWorld: UnsummonAllTotems, RemoveMiniPet; Unit::RemoveFromWorld: RemoveGuardians).
/// </summary>
public sealed class PetTeleportTests : IDisposable
{
    private const uint Imp = 416;
    private const uint SummonImp = 688;
    private const uint HunterPet = 5002;
    private const uint TotemEntry = 5001;
    private const uint GuardianEntry = 5003;
    private const uint MiniPetEntry = 5004;
    private const uint TimedPetEntry = 5008;
    private const uint FireTotem = 912001;
    private const uint Guardian = 912002;
    private const uint Critter = 912003;
    private const uint TimedPet = 912004;

    private readonly SpellTestKit _spells;
    private readonly Dictionary<Map, CreatureMapSystem> _creatures = [];
    private readonly SummonService _service;
    private readonly TeleportService _teleports;
    private readonly TotemSystem _totems;
    private readonly CreatureContent _content;

    public PetTeleportTests()
    {
        static SpellInfo Instant(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        _spells = new SpellTestKit(
            Instant(SummonImp, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)Imp)),
            Instant(FireTotem, Effect(SpellEffectName.SummonTotemSlot1, 5, misc: (int)TotemEntry)) with { Duration = new SpellDuration(30_000, 0, 30_000) },
            Instant(Guardian, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)GuardianEntry)),
            Instant(Critter, Effect(SpellEffectName.SummonCritter, 1, misc: (int)MiniPetEntry)),
            Instant(TimedPet, Effect(SpellEffectName.Summon, 0, misc: (int)TimedPetEntry)) with { Duration = new SpellDuration(60_000, 0, 60_000) });
        _content = CreatureTestSupport.Content(
            [.. new[] { Imp, HunterPet, TotemEntry, GuardianEntry, MiniPetEntry, TimedPetEntry }.Select(entry => CreatureTestSupport.Template(entry, b =>
            {
                b.Name = $"Summon {entry}";
                b.Faction = 14;
                b.MinLevel = 5;
                b.MaxLevel = 5;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
            }))],
            []);
        foreach (uint mapId in new uint[] { 0, 1 })
        {
            Map map = _spells.World.GetMap(mapId);
            var system = new CreatureMapSystem(map, _content, random: new Random(1));
            map.AddUpdater(system);
            _creatures[map] = system;
        }

        _service = new SummonService(systems: map => _creatures.GetValueOrDefault(map), random: new Random(3));
        _spells.System.Units = new MapObjectResolver();
        _service.Install(_spells.System);
        _service.InstallDemons(_spells.System);
        _spells.System.Summons = _service;
        _totems = new TotemSystem(_spells.System, map => _creatures.GetValueOrDefault(map), entry => _content.FindTemplate(entry), _ => null);
        _totems.Register();
        foreach (Map map in _creatures.Keys)
        {
            _totems.EnsureUpdater(map);
        }

        _teleports = new TeleportService(_spells.World, _ => { }, _ => { });
        _spells.World.PlayerLoggingOut += _teleports.Forget;
        _ = new PetTeleportFollow(_service, _teleports, _spells.World);
    }

    public void Dispose() => _spells.Dispose();

    private Map Kalimdor => _spells.World.GetMap(1);

    private Map EasternKingdoms => _spells.World.GetMap(0);

    // The content has no spawns, so every creature of a map is a summon (a totem of the shaman TotemSystem has no SummonLinks).
    private IEnumerable<Creature> Summons(Map map) => _creatures[map].Creatures;

    private (Player Player, FakeSession Session) Owner(Class cls = Class.Warlock)
    {
        (Player player, FakeSession session) = _spells.AddPlayer(1);
        player.Level = 10;
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)cls);
        return (player, session);
    }

    private SpellCastResult Cast(Unit caster, uint spell) => _spells.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true);

    /// <summary>The whole far teleport: scheduled, carried out after the map update (SMSG_NEW_WORLD), acknowledged, arrived.</summary>
    private void FarTeleport(Player player, uint mapId = 1, float x = 100f, float y = 200f)
    {
        Assert.True(_teleports.TeleportTo(player, mapId, x, y, 83.5f, 0f));
        _spells.World.RunTick(50);
        Assert.Equal(TeleportStage.Far, _teleports.StageOf(player));
        Assert.True(_teleports.HandleWorldportAck(player));
        _spells.World.RunTick(50);
        Assert.Same(_spells.World.GetMap(mapId), player.Map);
    }

    [Fact]
    public void FarTeleport_BringsTheDemonAlong_WithItsNumberHealthReactStateAndBar()
    {
        (Player warlock, FakeSession session) = Owner();
        Assert.Equal(SpellCastResult.CastOk, Cast(warlock, SummonImp));
        Creature imp = Assert.Single(Summons(EasternKingdoms));
        uint petNumber = imp.Summon!.Charm!.PetNumber;
        imp.Health = 37;
        imp.Summon.Charm.ReactState = ReactState.Passive;
        imp.Summon.Charm.SetActionBar(5, 4242, (byte)ActionType.Disabled);

        FarTeleport(warlock);

        Assert.Empty(Summons(EasternKingdoms));
        Creature back = Assert.Single(Summons(Kalimdor));
        Assert.NotSame(imp, back); // a new pet object in the new map (vmangos new Pet + LoadPetFromDB)
        Assert.Equal(back.Guid, warlock.PetGuid);
        Assert.Equal((Imp, petNumber, SummonImp), (back.Entry, back.Summon!.Charm!.PetNumber, back.GetUInt32(UpdateFields.UnitCreatedBySpell)));
        Assert.Equal((37u, ReactState.Passive), (back.Health, back.Summon.Charm.ReactState));
        Assert.Equal(ActionButton.Make(4242, ActionType.Disabled), back.Summon.Charm.GetButton(5));
        Assert.Equal(warlock.Guid, back.OwnerGuid);
        Assert.True(Math.Abs(back.X - warlock.X) < 5f && Math.Abs(back.Y - warlock.Y) < 5f, "the pet comes back at its owner's side");
        Assert.False(_service.HasTemporarilyUnsummonedPet(warlock));
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetSpells && p.Payload.Length > 8);
    }

    [Fact]
    public void WhileTheFarTeleportIsUnderWay_ThePlayerHasNoPet()
    {
        (Player warlock, _) = Owner();
        Cast(warlock, SummonImp);

        Assert.True(_teleports.TeleportTo(warlock, 1, 100f, 200f, 83.5f, 0f));
        _spells.World.RunTick(50);

        Assert.Equal(TeleportStage.Far, _teleports.StageOf(warlock));
        Assert.True(warlock.PetGuid.IsEmpty);
        Assert.Empty(Summons(EasternKingdoms));
        Assert.Empty(Summons(Kalimdor));
        Assert.True(_service.HasTemporarilyUnsummonedPet(warlock));
    }

    [Fact]
    public void FarTeleport_BringsTheHunterPetAlong_FromItsCurrentPetSnapshot()
    {
        (Player hunter, _) = Owner(Class.Hunter);
        Assert.NotNull(_service.RestoreCurrentPet(hunter, new PersistentPetSnapshot(1, 701, HunterPet, 5, 0, 60, 0, 0, 1, [], [])));
        Creature pet = Assert.Single(Summons(EasternKingdoms));

        FarTeleport(hunter);

        Creature back = Assert.Single(Summons(Kalimdor));
        Assert.Equal((HunterPet, 701u, 60u), (back.Entry, back.Summon!.Charm!.PetNumber, back.Health));
        Assert.Equal(back.Guid, hunter.PetGuid);
        Assert.Empty(Summons(EasternKingdoms));
        Assert.NotSame(pet, back);
    }

    [Fact]
    public void FarTeleport_LeavesTotemsGuardiansAndMiniPetsBehind_Unsummoned()
    {
        (Player shaman, _) = Owner(Class.Shaman);
        Cast(shaman, FireTotem);
        Cast(shaman, Guardian);
        Cast(shaman, Critter);
        Assert.Equal(3, Summons(EasternKingdoms).Count());

        FarTeleport(shaman);

        Assert.Empty(Summons(EasternKingdoms));
        Assert.Empty(Summons(Kalimdor));
        Assert.True(shaman.PetGuid.IsEmpty);
    }

    [Fact]
    public void FarTeleport_DoesNotBringBackATemporarySummon()
    {
        (Player owner, _) = Owner();
        Cast(owner, TimedPet);
        Assert.Single(Summons(EasternKingdoms));

        FarTeleport(owner);

        Assert.Empty(Summons(Kalimdor));
        Assert.True(owner.PetGuid.IsEmpty);
    }

    [Fact]
    public void NearTeleport_WithinTheActivationDistance_LeavesThePetInPlace()
    {
        (Player warlock, _) = Owner();
        Cast(warlock, SummonImp);
        Creature imp = Assert.Single(Summons(EasternKingdoms));

        Assert.True(_teleports.TeleportTo(warlock, 0, 50f, 0f, 83.5f, 0f));
        Assert.True(_teleports.HandleTeleportAck(warlock, warlock.Guid.Value));

        Assert.Same(imp, Assert.Single(Summons(EasternKingdoms)));
        Assert.Equal(imp.Guid, warlock.PetGuid);
    }

    [Fact]
    public void NearTeleport_BeyondTheActivationDistance_PutsThePetAway_UntilTheAck()
    {
        (Player warlock, _) = Owner();
        Cast(warlock, SummonImp);
        Creature imp = Assert.Single(Summons(EasternKingdoms));
        float far = EasternKingdoms.Grids.Options.GridActivationDistance + 50f;

        Assert.True(_teleports.TeleportTo(warlock, 0, far, 0f, 83.5f, 0f));
        Assert.Empty(Summons(EasternKingdoms));
        Assert.True(warlock.PetGuid.IsEmpty);

        Assert.True(_teleports.HandleTeleportAck(warlock, warlock.Guid.Value));

        Creature back = Assert.Single(Summons(EasternKingdoms));
        Assert.Equal(imp.Summon!.Charm!.PetNumber, back.Summon!.Charm!.PetNumber);
        Assert.Equal(back.Guid, warlock.PetGuid);
        Assert.True(Math.Abs(back.X - far) < 5f, "the pet comes back next to the owner's new position");
    }

    [Fact]
    public void LoggingOutDuringTheTransfer_ForgetsThePet()
    {
        (Player warlock, _) = Owner();
        Cast(warlock, SummonImp);
        Assert.True(_teleports.TeleportTo(warlock, 1, 100f, 200f, 83.5f, 0f));
        _spells.World.RunTick(50);
        Assert.True(_service.HasTemporarilyUnsummonedPet(warlock));

        _spells.World.RemovePlayer(warlock);

        Assert.False(_service.HasTemporarilyUnsummonedPet(warlock));
    }
}
