using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.ClassSpells.Totems;

/// <summary>
/// SUMMON_TOTEM / SLOT1-4 / DESTROY_ALL_TOTEMS after vmangos Spells/SpellEffects.cpp:4923-5003 and :5566-5573,
/// Objects/Totem.cpp:94-112 (Summon), Objects/Unit.cpp:5069-5115 (slots). Spell and creature ids are synthetic;
/// shapes follow classic-db (value 5 = totem health, target 41-44, MiscValue = creature entry).
/// </summary>
public sealed class TotemSummonTests
{
    private const float Pi = MathF.PI;

    [Fact]
    public void SummonSlot2_SpawnsTotemAt2yd_Angle7pi4_WithOwnerFactionLevelHealthAndOwnerFields()
    {
        using var kit = new TotemKit();
        (Player shaman, _) = kit.AddPlayer(1, 100, 200);
        shaman.Orientation = 0.5f;
        shaman.Level = 20;
        shaman.FactionTemplate = 1;

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(shaman, TotemKit.EarthTotemSummon));

        Creature totem = Assert.IsType<Creature>(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
        float angle = 0.5f + (Pi / 4) - (Pi / 2);
        // Object.cpp:2748 + 2728: distance2d + the searcher's (totem's) radius, plus the close object's (caster's) radius.
        float reach = 2 + shaman.BoundingRadius + totem.BoundingRadius;
        Assert.True(totem.BoundingRadius > 0 && shaman.BoundingRadius > 0);
        Assert.Equal(100 + (reach * MathF.Cos(angle)), totem.X, 3);
        Assert.Equal(200 + (reach * MathF.Sin(angle)), totem.Y, 3);
        Assert.Equal(totem.X, totem.Home.X, 3);
        Assert.Equal(shaman.Z, totem.Z, 3);
        Assert.Equal(TotemKit.EarthEntry, totem.Entry);
        Assert.Equal(1u, totem.FactionTemplate);
        Assert.Equal((byte)20, totem.Level);
        Assert.Equal((5u, 5u), (totem.Health, totem.MaxHealth));
        Assert.Equal(shaman.Guid.Value, totem.GetUInt64(UpdateFields.UnitFieldSummonedby));
        Assert.Equal(shaman.Guid.Value, totem.GetUInt64(UpdateFields.UnitFieldCreatedby));
        Assert.Equal(TotemKit.EarthTotemSummon, totem.GetUInt32(UpdateFields.UnitCreatedBySpell));
        Assert.NotEqual(0u, (uint)(totem.UnitFlags & UnitFlags.PlayerControlled));
        Assert.True(TotemQuery.IsTotem(totem));
        Assert.Equal(shaman.Guid, TotemQuery.GetOwnerGuid(totem));
        Assert.False(TotemQuery.IsTotem(shaman));
        Assert.False(TotemQuery.IsTotem(null));

        // Driven by this system, not by creature AI: the TotemAI name falls to NullAI instead of AggressorAI.
        Assert.IsType<NullCreatureAI>(totem.AI);
        Assert.Equal(CreatureMovementTypeIdle, (byte)totem.MovementType);
    }

    private const byte CreatureMovementTypeIdle = 0;

    [Theory]
    [InlineData(TotemKit.FireTotemSummon, TotemSlot.Fire, 0.25f)]
    [InlineData(TotemKit.EarthTotemSummon, TotemSlot.Earth, 1.75f)]
    [InlineData(TotemKit.WaterTotemSummon, TotemSlot.Water, 1.25f)]
    [InlineData(TotemKit.AirTotemSummon, TotemSlot.Air, 0.75f)]
    public void EachSlotHasItsOwnAngle_FireEarthWaterAir(uint spell, TotemSlot slot, float piMultiple)
    {
        using var kit = new TotemKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        shaman.Orientation = 0;

        kit.Cast(shaman, spell);

        Creature totem = Assert.IsType<Creature>(kit.Totems.GetTotem(shaman, slot));
        float reach = 2 + shaman.BoundingRadius + totem.BoundingRadius;
        Assert.Equal(reach * MathF.Cos(Pi * piMultiple), totem.X, 3);
        Assert.Equal(reach * MathF.Sin(Pi * piMultiple), totem.Y, 3);
    }

    [Fact]
    public void SecondTotemInTheSameSlot_UnsummonsTheFirst_OtherElementsCoexist()
    {
        using var kit = new TotemKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);

        kit.Cast(shaman, TotemKit.EarthTotemSummon);
        Creature first = kit.Totems.GetTotem(shaman, TotemSlot.Earth)!;
        kit.Cast(shaman, TotemKit.FireTotemSummon);
        Creature fire = kit.Totems.GetTotem(shaman, TotemSlot.Fire)!;
        kit.Cast(shaman, TotemKit.EarthTotemSummon);

        Creature second = kit.Totems.GetTotem(shaman, TotemSlot.Earth)!;
        Assert.NotSame(first, second);
        Assert.Null(kit.Map.FindObject(first.Guid));
        Assert.False(TotemQuery.IsTotem(first));
        Assert.Same(fire, kit.Totems.GetTotem(shaman, TotemSlot.Fire));
        Assert.Equal(2, kit.Totems.Totems.Count);
    }

    [Fact]
    public void SummonTotemWithoutSlot_DoesNotReplaceAnything()
    {
        using var kit = new TotemKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        kit.Cast(shaman, TotemKit.EarthTotemSummon);

        kit.Cast(shaman, TotemKit.NoSlotSummon);
        kit.Cast(shaman, TotemKit.NoSlotSummon);

        Assert.NotNull(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
        Assert.Equal(3, kit.Totems.Totems.Count);
    }

    [Fact]
    public void DestroyAllTotems_UnsummonsAllFourSlots()
    {
        using var kit = new TotemKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        (Player other, _) = kit.AddPlayer(2, 5, 0);
        kit.Cast(shaman, TotemKit.FireTotemSummon);
        kit.Cast(shaman, TotemKit.EarthTotemSummon);
        kit.Cast(shaman, TotemKit.WaterTotemSummon);
        kit.Cast(shaman, TotemKit.AirTotemSummon);
        kit.Cast(other, TotemKit.EarthTotemSummon);
        Assert.Equal(5, kit.Totems.Totems.Count);

        kit.Cast(shaman, TotemKit.DestroyAll);

        foreach (TotemSlot slot in new[] { TotemSlot.Fire, TotemSlot.Earth, TotemSlot.Water, TotemSlot.Air })
        {
            Assert.Null(kit.Totems.GetTotem(shaman, slot));
        }

        Assert.NotNull(kit.Totems.GetTotem(other, TotemSlot.Earth));
        Assert.Single(kit.Totems.Totems);
    }

    [Fact]
    public void PvPOwner_GivesAPvPTotem_AndAnUnflaggedOwnerDoesNot()
    {
        using var kit = new TotemKit();
        (Player flagged, _) = kit.AddPlayer(1, 0, 0);
        (Player plain, _) = kit.AddPlayer(2, 20, 0);
        flagged.UnitFlags |= UnitFlags.Pvp;

        kit.Cast(flagged, TotemKit.EarthTotemSummon);
        kit.Cast(plain, TotemKit.EarthTotemSummon);

        Assert.NotEqual(0u, (uint)(kit.Totems.GetTotem(flagged, TotemSlot.Earth)!.UnitFlags & UnitFlags.Pvp));
        Assert.Equal(0u, (uint)(kit.Totems.GetTotem(plain, TotemSlot.Earth)!.UnitFlags & UnitFlags.Pvp));
    }

    [Fact]
    public void UnknownCreatureEntry_SummonsNothing_AndKeepsTheOldTotem()
    {
        using var kit = new TotemKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        kit.Cast(shaman, TotemKit.EarthTotemSummon);
        var broken = new SpellStore(
            [.. kit.Store.All, TotemKit.BrokenSummonSpell()],
            [],
            []);
        kit.Spells.Store = broken;

        kit.Cast(shaman, TotemKit.BrokenSummonId);

        // vmangos unsummons the old totem before it looks the template up, so the slot is empty and nothing replaced it.
        Assert.Null(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
        Assert.Empty(kit.Totems.Totems);
    }

    [Fact]
    public void TotemsDisabled_EffectsStayUnregistered()
    {
        using var kit = new TotemKit(new TotemOptions { Enabled = false });

        Assert.False(kit.Registered);
        Assert.False(kit.Spells.HasEffectHandler(SpellEffectName.SummonTotemSlot2));
        Assert.False(kit.Spells.HasEffectHandler(SpellEffectName.DestroyAllTotems));
    }

    [Fact]
    public void RegisteringOverAnotherOwnersHandler_Throws_InsteadOfClobberingIt()
    {
        using var kit = new TotemKit();
        var second = new TotemSystem(kit.Spells, _ => kit.Creatures, e => kit.Content.FindTemplate(e), _ => null);

        Assert.Throws<InvalidOperationException>(() => second.Register());
    }

    [Fact]
    public void PlacementDistanceOption_MovesTheTotem()
    {
        using var kit = new TotemKit(new TotemOptions { PlacementDistance = 5f });
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);

        kit.Cast(shaman, TotemKit.FireTotemSummon);

        Creature totem = kit.Totems.GetTotem(shaman, TotemSlot.Fire)!;
        Assert.Equal(5f + shaman.BoundingRadius + totem.BoundingRadius, MathF.Sqrt((totem.X * totem.X) + (totem.Y * totem.Y)), 3);
    }

    [Fact]
    public void KillingATotem_GrantsNoExperience_WhileTheSameCreatureOutsideTheTotemSystemDoes()
    {
        // Player::IsHonorOrXPTarget (Player.cpp:19943-19954) and MaNGOS::XP::Gain (Formulas.h:102-107) refuse totems.
        using var kit = new TotemKit();
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        (Player enemy, _) = kit.AddPlayer(2, 5, 0);
        shaman.Level = 20;
        enemy.Level = 20;
        var progression = new ArcaneCore.Game.Progression.PlayerProgression(new ArcaneCore.Game.Progression.ProgressionOptions());
        progression.InitializeLoadedPlayer(enemy);

        kit.Cast(shaman, TotemKit.EarthTotemSummon);
        Creature totem = kit.Totems.GetTotem(shaman, TotemSlot.Earth)!;
        Creature plain = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(TotemKit.EarthEntry)! with { AIName = "NullAI" }, 3, 3, 0, 0);
        plain.Level = 20;

        Assert.Equal([0u], ArcaneCore.Game.Progression.KillRewards.AwardExperience(progression, [enemy], totem, nonRaidDungeon: false));
        Assert.True(ArcaneCore.Game.Progression.KillRewards.AwardExperience(progression, [enemy], plain, nonRaidDungeon: false)[0] > 0);
    }
}
