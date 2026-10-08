using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Paladin;
using ArcaneCore.Game.Spells.PersistentAreaAuras;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// SPELL_EFFECT_PERSISTENT_AREA_AURA and Consecration (vmangos Spell::EffectPersistentAA, DynamicObject::Update, DynamicObjectUpdater::VisitHelper,
/// PersistentAreaAura::Update, Aura::PeriodicTick's Consecration case). Consecration rank 1 follows the build 5875 row 26573: effect 27, PERIODIC_DAMAGE
/// 8 every 1000 ms, TARGET_LOCATION_CASTER_DEST / TARGET_ENUM_UNITS_ENEMY_AOE_AT_DEST_LOC, radius 8, 8 s, paladin family 0x20.
/// </summary>
public sealed class PersistentAreaAuraTests : IDisposable
{
    private const uint Consecration = 26573;
    private const uint Snapshot = 990_501;   // the same ground aura without the Consecration recalculation
    private const uint Channel = 990_502;    // a channelled ground aura (Blizzard-like)
    private const uint Heal = 990_503;       // a positive ground aura

    /// <summary>The caster side adds <see cref="SpellPower"/> to a damage-over-time snapshot; the target side changes nothing.</summary>
    private sealed class PowerModifier : ISpellAmountModifier
    {
        public float SpellPower { get; set; }

        public float Modify(SpellAmountStage stage, Unit caster, Unit target, SpellInfo spell, int effectIndex, float amount, uint stack)
            => stage is SpellAmountStage.DamageOverTimeSnapshot or SpellAmountStage.HealOverTimeSnapshot ? amount + SpellPower : amount;
    }

    private readonly SpellTestKit _kit;
    private readonly Player _paladin;
    private readonly Player _enemy;
    private readonly Player _friend;
    private readonly PowerModifier _power = new();

    public PersistentAreaAuraTests()
    {
        SpellInfo ground = Spell(Consecration,
            Effect(SpellEffectName.PersistentAreaAura, 8, SpellImplicitTarget.LocationCasterDest, AuraType.PeriodicDamage, amplitude: 1000,
                targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc) with { Radius = 8 }) with
        {
            School = SpellSchool.Holy,
            SpellFamilyName = 10,
            SpellFamilyFlags = 0x20,
            Duration = new SpellDuration(8000, 0, 8000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        _kit = new SpellTestKit(
            ground,
            ground with { Id = Snapshot, SpellFamilyFlags = 0 },
            ground with
            {
                Id = Channel,
                SpellFamilyFlags = 0,
                AttributesEx = SpellAttributesEx.IsChanneled,
                Effects = [ground.Effects[0] with { TargetA = SpellImplicitTarget.LocationCasterDest }],
            },
            Spell(Heal, Effect(SpellEffectName.PersistentAreaAura, 5, SpellImplicitTarget.LocationCasterDest, AuraType.PeriodicHeal, amplitude: 1000,
                targetB: SpellImplicitTarget.EnumUnitsFriendAoeAtDestLoc) with { Radius = 8 }) with
            {
                Duration = new SpellDuration(8000, 0, 8000),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (_paladin, _) = _kit.AddPlayer(1, 0, 0);
        (_enemy, _) = _kit.AddPlayer(2, 3, 0);
        (_friend, _) = _kit.AddPlayer(3, -3, 0);
        _kit.System.Relations = new FakeRelations { Hostile = { _enemy.Guid } };
        _kit.System.AmountModifier = _power;
        _paladin.UnitFlags |= UnitFlags.Pvp; // patch 1.7: a non-flagged player's ground spell does not hit players
        foreach (Player player in new[] { _paladin, _enemy, _friend })
        {
            player.MaxHealth = 10_000;
            player.Health = 5_000;
        }
    }

    public void Dispose() => _kit.Dispose();

    private SpellAuraHolder? Holder(Unit unit, uint spell) => _kit.System.GetAuras(unit).FirstOrDefault(h => !h.IsRemoved && h.Spell.Id == spell);

    private void Cast(uint spell) => Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_paladin, spell, SpellCastTargets.ForSelf(), triggered: true));

    [Fact]
    public void TheCast_PutsAGroundObjectDown_WithTheVisualFields()
    {
        Cast(Consecration);

        DynamicObject ground = Assert.Single(_kit.System.DynamicObjects);
        Assert.Same(ground, _paladin.Map!.FindObject(ground.Guid));
        Assert.Equal(Game.TypeId.DynamicObject, ground.TypeId);
        Assert.Equal(HighGuid.DynamicObject, ground.Guid.High);
        Assert.Equal(_paladin.Guid.Value, ground.GetUInt64(UpdateFields.DynamicobjectCaster));
        Assert.Equal(Consecration, ground.GetUInt32(UpdateFields.DynamicobjectSpellid));
        Assert.Equal(DynamicObject.AreaSpell, ground.GetUInt32(UpdateFields.DynamicobjectBytes));
        Assert.Equal(8f, ground.GetFloat(UpdateFields.DynamicobjectRadius));
        Assert.Equal((0f, 0f), (ground.GetFloat(UpdateFields.DynamicobjectPosX), ground.GetFloat(UpdateFields.DynamicobjectPosY)));
        Assert.Equal(8000, ground.RemainingMs);
    }

    [Fact]
    public void TheEffect_NeverRunsOnTheListedUnits_OnlyTheGroundGivesTheAura()
    {
        Cast(Consecration);
        Assert.Null(Holder(_enemy, Consecration)); // the cast itself applies nothing to the unit targets

        _kit.Advance(100);

        Assert.NotNull(Holder(_enemy, Consecration));
        Assert.Same(_kit.System.DynamicObjects.Single(), _kit.System.FindDynamicObject(Holder(_enemy, Consecration)!));
        Assert.Null(Holder(_friend, Consecration));   // not hostile
        Assert.Null(Holder(_paladin, Consecration));
    }

    [Fact]
    public void Consecration_Ticks_UseTheCastersCurrentSpellPower()
    {
        _power.SpellPower = 10;
        Cast(Consecration);
        _kit.Advance(100);
        Assert.Equal(18, Holder(_enemy, Consecration)!.Auras[0]!.Amount); // stored at application: 8 + 10

        _power.SpellPower = 100;
        uint before = _enemy.Health;
        _kit.Advance(1000);

        Assert.Equal(before - 108u, _enemy.Health); // recalculated from the base points: 8 + 100
    }

    [Fact]
    public void OtherGroundAuras_TickTheirSnapshot()
    {
        _power.SpellPower = 10;
        Cast(Snapshot);
        _kit.Advance(100);

        _power.SpellPower = 100;
        uint before = _enemy.Health;
        _kit.Advance(1000);

        Assert.Equal(before - 18u, _enemy.Health);
    }

    [Fact]
    public void AUnitThatLeavesTheRadius_LosesTheAura_AndGetsItBackWhenItReturns()
    {
        Cast(Consecration);
        _kit.Advance(100);
        Assert.NotNull(Holder(_enemy, Consecration));

        _enemy.SetPosition(30, 0, _enemy.Z, 0);
        _kit.Advance(100);
        Assert.Null(Holder(_enemy, Consecration));

        _enemy.SetPosition(2, 0, _enemy.Z, 0);
        _kit.Advance(100);
        Assert.NotNull(Holder(_enemy, Consecration));
    }

    [Fact]
    public void TheObject_GoesWhenItsTimeRunsOut_AndTakesItsAurasAlong()
    {
        Cast(Consecration);
        _kit.Advance(7900);
        Assert.Single(_kit.System.DynamicObjects);
        Assert.NotNull(Holder(_enemy, Consecration));

        _kit.Advance(200);

        Assert.Empty(_kit.System.DynamicObjects);
        Assert.Null(Holder(_enemy, Consecration));
    }

    [Fact]
    public void ANonFlaggedPlayersGroundSpell_DoesNotHitPlayers()
    {
        _paladin.UnitFlags &= ~UnitFlags.Pvp;
        Cast(Consecration);
        _kit.Advance(100);

        Assert.Null(Holder(_enemy, Consecration));
    }

    [Fact]
    public void ANonFlaggedPlayersGroundSpell_HitsAPlayer_WhenBothAreInAFreeForAllArea()
    {
        // vmangos GridNotifiersImpl.h:170: the patch 1.7 rule spares "!(attackerPlayer->IsFFAPvP() && attackedPlayer->IsFFAPvP())"
        // (the Gurubashi arena, a free-for-all realm).
        _paladin.UnitFlags &= ~UnitFlags.Pvp;
        _paladin.Flags |= PlayerFlags.FfaPvp;
        _enemy.Flags |= PlayerFlags.FfaPvp;
        Cast(Consecration);
        _kit.Advance(100);

        Assert.NotNull(Holder(_enemy, Consecration));
    }

    [Fact]
    public void ANonFlaggedPlayersGroundSpell_DoesNotHitAPlayer_WhenOnlyTheCasterIsFreeForAll()
    {
        _paladin.UnitFlags &= ~UnitFlags.Pvp;
        _paladin.Flags |= PlayerFlags.FfaPvp;
        Cast(Consecration);
        _kit.Advance(100);

        Assert.Null(Holder(_enemy, Consecration));
    }

    [Fact]
    public void APositiveGroundAura_GoesToFriends()
    {
        Cast(Heal);
        _kit.Advance(100);

        Assert.NotNull(Holder(_friend, Heal));
        Assert.NotNull(Holder(_paladin, Heal));
        Assert.Null(Holder(_enemy, Heal));
    }

    [Fact]
    public void AChannelsObject_IsItsChannelObject_AndGoesWhenTheChannelIsCancelled()
    {
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_paladin, Channel, SpellCastTargets.ForSelf(), triggered: false));
        DynamicObject ground = Assert.Single(_kit.System.DynamicObjects);
        Assert.Equal(ground.Guid.Value, _paladin.GetUInt64(UpdateFields.UnitFieldChannelObject));
        _kit.Advance(100);
        Assert.NotNull(Holder(_enemy, Channel));

        _kit.System.CancelChannel(_paladin);
        _kit.Advance(100);

        Assert.Empty(_kit.System.DynamicObjects);
        Assert.Null(Holder(_enemy, Channel));
    }

    [Fact]
    public void Clients_SeeTheObject_AndItsDespawnAnimation()
    {
        Cast(Consecration);
        DynamicObject ground = _kit.System.DynamicObjects.Single();

        _kit.World.RunTick(0); // the map's visibility pass sends the create block

        Assert.Contains(ground.Guid, _enemy.VisibleObjects);
        Assert.Contains(ground.Guid, _paladin.VisibleObjects);

        _kit.Advance(8100);
        _kit.World.RunTick(0);

        Assert.DoesNotContain(ground.Guid, _enemy.VisibleObjects);
        Assert.Null(_paladin.Map!.FindObject(ground.Guid));
    }

    [Fact]
    public void ACasterThatLeavesTheWorld_TakesItsObjectAlong()
    {
        Cast(Consecration);
        _kit.Advance(100);

        _kit.World.GetMap(0).RemovePlayer(_paladin);
        _kit.Advance(100);

        Assert.Empty(_kit.System.DynamicObjects);
    }
}
