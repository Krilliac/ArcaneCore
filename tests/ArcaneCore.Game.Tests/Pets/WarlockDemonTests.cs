using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// The warlock demons: SPELL_EFFECT_SUMMON_PET (vmangos Spell::EffectSummonPet / Unit::EffectSummonPet, SpellEffects.cpp:3171-3327), the old pet
/// check (Unit::UnsummonOldPetBeforeNewSummon, Unit.cpp:5116-5145), the Demonic Sacrifice script (scripts/spells/spell_warlock.cpp:19-56).
/// Creature entries and spell ids are the retail ones (Imp 416, Felhunter 417, Voidwalker 1860, Succubus 1863; Summon Imp 688, Summon Voidwalker 697,
/// Demonic Sacrifice 18788 with Burning Wish 18789, Fel Stamina 18790, Touch of Shadow 18791, Fel Energy 18792); the spell shapes follow the classic-db
/// z2815 rows (effect 56, target 32, misc = the creature entry; Demonic Sacrifice is an instant kill of the caster's pet).
/// </summary>
public sealed class WarlockDemonTests : IDisposable
{
    private const uint Imp = 416;
    private const uint Felhunter = 417;
    private const uint Voidwalker = 1860;
    private const uint Succubus = 1863;
    private const uint Other = 5002;
    private const uint NpcWarlock = 5006;
    private const uint SummonImp = 688;
    private const uint SummonVoidwalker = 697;
    private const uint SummonSuccubus = 712;
    private const uint SummonFelhunter = 691;
    private const uint SummonOther = 971_101;
    private const uint SummonNpcRelative = 971_102;
    private const uint SummonNothing = 971_103;
    private const uint DemonicSacrifice = 18788;
    private const uint BurningWish = 18789;
    private const uint FelStamina = 18790;
    private const uint TouchOfShadow = 18791;
    private const uint FelEnergy = 18792;

    private readonly SpellTestKit _spells;
    private readonly Map _map;
    private readonly CreatureMapSystem _creatures;
    private readonly SummonService _service;
    private readonly CreatureContent _content;

    public WarlockDemonTests()
    {
        static SpellInfo Instant(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        static SpellInfo Sacrifice(uint id) => Instant(id,
            Effect(SpellEffectName.ApplyAura, 14, aura: AuraType.ModDamagePercentDone, misc: 4),
            Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.OverrideClassScripts, misc: 2228)) with
        {
            Duration = new SpellDuration(1_800_000, 0, 1_800_000),
            SpellVisual = 1,
        };

        _spells = new SpellTestKit(
            Instant(SummonImp, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)Imp)),
            Instant(SummonVoidwalker, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)Voidwalker)),
            Instant(SummonSuccubus, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)Succubus)),
            Instant(SummonFelhunter, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)Felhunter)),
            Instant(SummonOther, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)Other)),
            Instant(SummonNpcRelative, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)Imp) with { MultipleValue = -3f }),
            Instant(SummonNothing, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: 999_999)),
            Instant(DemonicSacrifice, Effect(SpellEffectName.Instakill, 0, (SpellImplicitTarget)5)),
            Sacrifice(BurningWish),
            Sacrifice(FelStamina),
            Sacrifice(TouchOfShadow),
            Sacrifice(FelEnergy));
        _map = _spells.World.GetMap(0);
        _content = CreatureTestSupport.Content(
            [.. new[] { Imp, Felhunter, Voidwalker, Succubus, Other }.Select(entry => CreatureTestSupport.Template(entry, b =>
            {
                b.Name = $"Demon {entry}";
                b.Faction = 14;
                b.MinLevel = 5;
                b.MaxLevel = 5;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
            })),
            CreatureTestSupport.Template(NpcWarlock, b =>
            {
                b.Name = "Warlock NPC";
                b.Faction = 14;
                b.MinLevel = 10;
                b.MaxLevel = 10;
                b.MinLevelHealth = 500;
                b.MaxLevelHealth = 500;
            })],
            []);
        _creatures = new CreatureMapSystem(_map, _content, random: new Random(1));
        _map.AddUpdater(_creatures);
        _service = new SummonService(systems: map => ReferenceEquals(map, _map) ? _creatures : null, random: new Random(3));
        _spells.System.Units = new MapObjectResolver();
        _service.Install(_spells.System);
        _spells.System.Summons = _service;
        PetTargets.Install(_spells.System);
        SpellScriptDispatcher.Install(_spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
    }

    public void Dispose() => _spells.Dispose();

    private void InstallDemons() => _service.InstallDemons(_spells.System);

    private Player Warlock(uint guid = 1, byte level = 10)
    {
        (Player player, _) = _spells.AddPlayer(guid);
        player.Level = level;
        return player;
    }

    private SpellCastResult Cast(Unit caster, uint spell) => _spells.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true);

    private IEnumerable<Creature> Pets() => _creatures.Creatures.Where(c => c.IsPet);

    [Fact]
    public void SummonImp_CreatesAPetOfTheCasterLevelAtTheFollowPoint_AndSendsThePetBar()
    {
        InstallDemons();
        (Player warlock, FakeSession session) = _spells.AddPlayer(1);
        warlock.Level = 10;
        warlock.Orientation = 0f;
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, Cast(warlock, SummonImp));

        Creature pet = Assert.Single(Pets());
        Assert.Equal(Imp, pet.Entry);
        Assert.Equal(HighGuid.Pet, pet.Guid.High);
        Assert.Equal(warlock.Guid, pet.OwnerGuid);
        Assert.Equal(warlock.Guid, pet.CreatorGuid);
        Assert.Equal(pet.Guid, warlock.PetGuid);
        Assert.Equal(warlock.FactionTemplate, pet.FactionTemplate);
        Assert.Equal(SummonImp, pet.GetUInt32(UpdateFields.UnitCreatedBySpell));
        Assert.Equal(10, pet.Level);
        Assert.Equal(ReactState.Defensive, pet.Summon!.Charm!.ReactState);
        Assert.Single(SpellTestKit.Packets(session, WorldOpcode.SmsgPetSpells), p => p.Length > 8);

        // vmangos CreatureCreatePos(this, orientation, PET_FOLLOW_DIST, PET_FOLLOW_ANGLE): the owner's close point 2 yards to the left (pi / 2).
        float range = warlock.BoundingRadius + 2.0f;
        Assert.Equal(warlock.X + (range * MathF.Cos(MathF.PI / 2)), pet.X, 2);
        Assert.Equal(warlock.Y + (range * MathF.Sin(MathF.PI / 2)), pet.Y, 2);
    }

    [Fact]
    public void WithoutTheInstall_TheEffectIsNotImplemented()
    {
        Player warlock = Warlock();

        Cast(warlock, SummonImp);

        Assert.Empty(Pets());
    }

    [Fact]
    public void SummoningAnotherDemon_DismissesTheLivingOne_AndSwapsThePetLink()
    {
        InstallDemons();
        Player warlock = Warlock();
        Cast(warlock, SummonImp);
        Creature imp = Assert.Single(Pets());

        Cast(warlock, SummonVoidwalker);

        Creature voidwalker = Assert.Single(Pets());
        Assert.Equal(Voidwalker, voidwalker.Entry);
        Assert.NotSame(imp, voidwalker);
        Assert.Null(_creatures.FindCreature(imp.Guid));
        Assert.Equal(voidwalker.Guid, warlock.PetGuid);
    }

    [Fact]
    public void ADeadOldPet_IsReplaced_ByTheNewSummon()
    {
        InstallDemons();
        Player warlock = Warlock();
        Cast(warlock, SummonImp);
        Creature imp = Assert.Single(Pets());
        _creatures.KillCreature(imp);

        Cast(warlock, SummonImp);

        Creature fresh = Assert.Single(Pets(), c => c.IsAlive);
        Assert.NotSame(imp, fresh);
        Assert.Equal(fresh.Guid, warlock.PetGuid);
    }

    [Fact]
    public void ACreatureCaster_UsesItsLevelPlusTheMultipleValue_AndIsRefusedWhileItHasALivingPet()
    {
        InstallDemons();
        Creature npc = _creatures.SpawnTemporary(_content.FindTemplate(NpcWarlock)!, 0, 0, 83.5f, 0);

        Cast(npc, SummonNpcRelative);

        Creature npcPet = Assert.Single(Pets(), c => c.OwnerGuid == npc.Guid);
        Assert.Equal(7, npcPet.Level); // 10 - 3
        Assert.Equal(ReactState.Aggressive, npcPet.Summon!.Charm!.ReactState);

        Cast(npc, SummonVoidwalker);

        Assert.Single(Pets(), c => c.OwnerGuid == npc.Guid); // an NPC keeps its living pet: the new summon is refused
        Assert.Same(npcPet, Pets().Single(c => c.OwnerGuid == npc.Guid));
    }

    [Fact]
    public void AnUnknownEntry_StillDismissesTheOldPet_ButSummonsNothing()
    {
        InstallDemons();
        Player warlock = Warlock();
        Cast(warlock, SummonImp);
        Creature imp = Assert.Single(Pets());

        Cast(warlock, SummonNothing);

        // vmangos unsummons the old pet first (UnsummonOldPetBeforeNewSummon) and only then finds the template missing.
        Assert.Empty(Pets());
        Assert.True(warlock.PetGuid.IsEmpty);
        Assert.Null(_creatures.FindCreature(imp.Guid));
    }

    [Theory]
    [InlineData(Imp, BurningWish)]
    [InlineData(Felhunter, FelEnergy)]
    [InlineData(Voidwalker, FelStamina)]
    [InlineData(Succubus, TouchOfShadow)]
    public void DemonicSacrifice_KillsThePet_AndGrantsTheBuffOfItsKind(uint entry, uint buff)
    {
        InstallDemons();
        Player warlock = Warlock();
        Creature pet = SpawnDemon(warlock, entry);

        Cast(warlock, DemonicSacrifice);

        Assert.False(pet.IsAlive);
        Assert.True(_spells.System.HasAura(warlock, buff));
        foreach (uint other in new[] { BurningWish, FelStamina, TouchOfShadow, FelEnergy }.Where(b => b != buff))
        {
            Assert.False(_spells.System.HasAura(warlock, other));
        }
    }

    [Fact]
    public void DemonicSacrifice_OfAnUnhandledPetEntry_GrantsNothing()
    {
        InstallDemons();
        Player warlock = Warlock();
        Cast(warlock, SummonOther);
        Creature pet = Assert.Single(Pets());

        Cast(warlock, DemonicSacrifice);

        Assert.False(pet.IsAlive);
        Assert.DoesNotContain(_spells.System.GetAuras(warlock), h => h.Spell.Id is BurningWish or FelStamina or TouchOfShadow or FelEnergy);
    }

    [Fact]
    public void SummoningAPet_RemovesTheDemonicSacrificeBuff()
    {
        InstallDemons();
        Player warlock = Warlock();
        Cast(warlock, BurningWish);
        Assert.True(_spells.System.HasAura(warlock, BurningWish));

        Cast(warlock, SummonVoidwalker);

        Assert.False(_spells.System.HasAura(warlock, BurningWish));
        Assert.Single(Pets());
    }

    [Fact]
    public void InstallingTheDemonsTwice_FailsClosed()
    {
        InstallDemons();

        Assert.Throws<InvalidOperationException>(InstallDemons);
    }

    private Creature SpawnDemon(Player warlock, uint entry)
    {
        uint spell = entry switch
        {
            Imp => SummonImp,
            Felhunter => SummonFelhunter,
            Voidwalker => SummonVoidwalker,
            _ => SummonSuccubus,
        };
        Cast(warlock, spell);
        return Assert.Single(Pets());
    }
}
