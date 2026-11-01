using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.PetAuras;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// vmangos AreaAura::Update, AREA_AURA_PET (SpellAuras.cpp:702-708): the owner keeps a pet area aura copy only while it is within the effect radius of
/// the pet, checked every update, so a pet 100+ yards away (a possessed pet is not leashed) takes it and its return gives it back.
/// Spirit Bond 19578 (passive DUMMY aura) puts 19579 (APPLY_AREA_AURA_PET, 100 yards) on the hunter's pet.
/// </summary>
public sealed class PetAreaAuraRangeTests : IDisposable
{
    private const uint Wolf = 5002;
    private const uint SpiritBond = PetAuraTable.SpiritBond1;
    private const uint SpiritBondHeal = 19579;

    private readonly SpellTestKit _spells;
    private readonly SummonService _service;

    public PetAreaAuraRangeTests()
    {
        _spells = new SpellTestKit(
            Spell(SpiritBond, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            {
                StartRecoveryCategory = 0, StartRecoveryTime = 0, Attributes = SpellAttributes.Passive, Duration = new SpellDuration(-1, 0, -1),
            },
            Spell(SpiritBondHeal, Effect(SpellEffectName.ApplyAreaAuraPet, 1, aura: AuraType.ObsModHealth) with { Radius = 100f, Amplitude = 10_000 }) with
            {
                StartRecoveryCategory = 0, StartRecoveryTime = 0, Duration = new SpellDuration(-1, 0, -1),
            });
        Map map = _spells.World.GetMap(0);
        CreatureContent content = CreatureTestKit.Content(
            [CreatureTestKit.Template(Wolf, b =>
            {
                b.Name = "Wolf";
                b.Faction = 14;
                b.MinLevel = 5;
                b.MaxLevel = 5;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
            })],
            []);
        var creatures = new CreatureMapSystem(map, content, random: new Random(1));
        map.AddUpdater(creatures);
        _service = new SummonService(systems: m => ReferenceEquals(m, map) ? creatures : null, random: new Random(3));
        _spells.System.Units = new MapObjectResolver();
        _service.Install(_spells.System);
        _spells.System.Summons = _service;
        PetTargets.Install(_spells.System);
        SpellScriptDispatcher.Install(_spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
    }

    public void Dispose() => _spells.Dispose();

    private (Player Hunter, Creature Wolf) HunterWithPet()
    {
        (Player hunter, _) = _spells.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 40;
        _spells.System.CastSpell(hunter, SpiritBond, SpellCastTargets.ForSelf(), triggered: true);
        var wolf = Assert.IsType<Creature>(_service.RestoreCurrentPet(hunter, new PersistentPetSnapshot((int)hunter.Guid.Low, 7701, Wolf, 5, 0, 100, 0, 0, 1, [], [])));
        return (hunter, wolf);
    }

    [Fact]
    public void OwnerCopy_IsDroppedBeyondTheRadius_AndComesBackWhenThePetReturns()
    {
        (Player hunter, Creature wolf) = HunterWithPet();
        Assert.True(_spells.System.HasAura(hunter, SpiritBondHeal));

        wolf.Relocate(hunter.X + 110f, hunter.Y, hunter.Z);
        _spells.Advance(500);
        Assert.True(_spells.System.HasAura(wolf, SpiritBondHeal));
        Assert.False(_spells.System.HasAura(hunter, SpiritBondHeal), "the owner kept the pet area aura 110 yd from the pet");

        wolf.Relocate(hunter.X + 10f, hunter.Y, hunter.Z);
        _spells.Advance(500);
        Assert.True(_spells.System.HasAura(hunter, SpiritBondHeal), "the owner copy did not come back");
    }
}
