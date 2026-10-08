using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A bot healer resurrects a dead group member with the real 1.12.1 resurrection spells. Their <c>spell_template</c> rows (classic-db
/// z2815, read only; the numbers below are copied as fixtures and mapped through the server's own <see cref="SpellStoreFactory"/>) have
/// no implicit target (EffectImplicitTargetA1 0), the corpse target flag (Targets 0x8000, TARGET_FLAG_CORPSE_ALLY) and no
/// SPELL_ATTR_EX2_ALLOW_DEAD_TARGET; the earlier group tests used a synthetic Resurrection aimed at a friendly unit. A 1.12 client aims
/// such a spell at the dead player's unit while the body is unreleased, and at the corpse object once the player released (the ghost
/// itself cannot be targeted): CMSG_CAST_SPELL with TARGET_FLAG_CORPSE and the corpse guid (vmangos Spell::ValidateExplicitTargetMask
/// accepts either, Spell::SetTargetMap Spell.cpp:3106-3117 resolves the corpse to its owner, Spell::CheckCast :5780-5788 checks the
/// corpse exists and is in line of sight).
/// </summary>
public sealed class PlayerbotGroupResurrectionTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Paladin = 2, Priest = 5;
    private const uint LesserHeal = 2050, Smite = 585, HolyLight = 635;

    /// <summary>The real ids: Resurrection rank 1 (priest), Redemption rank 1 (paladin), Ancestral Spirit rank 1 (shaman), Rebirth rank 1 (druid).</summary>
    internal const uint RealResurrection = 2006, RealRedemption = 7328, RealAncestralSpirit = 2008, RealRebirth = 20484;

    /// <summary>
    /// The four rows as classic-db z2815 has them (every non-zero column that matters to the cast), and the two index rows they name:
    /// spell_cast_times 7 (10 s) and 5 (2 s), spell_range 4 (30 yards).
    /// </summary>
    internal static SpellInfo[] RealResurrectionSpells()
    {
        SpellTemplateRow Row(uint id, string name, uint school, uint attributes, uint castIndex, int basePoints, int miscValue, uint family, uint level,
            uint reagent = 0, uint ex3 = 0, uint category = 0, uint categoryRecovery = 0) => new()
        {
            Id = id, SpellName = name, Rank = "Rank 1", School = school, Category = category, Attributes = attributes, AttributesEx = 0x20000,
            AttributesEx3 = ex3, Targets = 0x8000, CastingTimeIndex = castIndex, CategoryRecoveryTime = categoryRecovery, InterruptFlags = 15,
            BaseLevel = level, SpellLevel = level, RangeIndex = 4, EquippedItemClass = -1, Reagent1 = (int)reagent, ReagentCount1 = reagent == 0 ? 0u : 1u,
            Effect1 = 113, EffectDieSides1 = 1, EffectBaseDice1 = 1, EffectBasePoints1 = basePoints, EffectMiscValue1 = miscValue,
            EffectImplicitTargetA1 = 0, SpellFamilyName = family, ManaCostPercentage = 75, StartRecoveryCategory = 133, StartRecoveryTime = 1500,
            DmgClass = 1, PreventionType = 1, DmgMultiplier1 = 1, DmgMultiplier2 = 1, DmgMultiplier3 = 1,
        };

        SpellTemplateRow[] rows =
        [
            Row(RealResurrection, "Resurrection", school: 1, attributes: 0x10010000, castIndex: 7, basePoints: 69, miscValue: 135, family: 6, level: 10),
            Row(RealRedemption, "Redemption", school: 1, attributes: 0x10000000, castIndex: 7, basePoints: 64, miscValue: 120, family: 0, level: 12),
            Row(RealAncestralSpirit, "Ancestral Spirit", school: 3, attributes: 0x10010000, castIndex: 7, basePoints: 64, miscValue: 120, family: 11, level: 12),
            Row(RealRebirth, "Rebirth", school: 3, attributes: 0x10000, castIndex: 5, basePoints: 399, miscValue: 700, family: 7, level: 20,
                reagent: 17034, ex3: 0x10, category: 26, categoryRecovery: 1_800_000),
        ];
        var castTimes = new Dictionary<uint, SpellCastTimeRow> { [5] = new() { Id = 5, CastTime = 2000, MinCastTime = 2000 }, [7] = new() { Id = 7, CastTime = 10000, MinCastTime = 10000 } };
        var ranges = new Dictionary<uint, SpellRangeRow> { [4] = new() { Id = 4, MinRange = 0, MaxRange = 30 } };
        return [.. rows.Select(row => SpellStoreFactory.ToSpellInfo(row, castTimes, new Dictionary<uint, SpellDurationRow>(), ranges,
            new Dictionary<uint, SpellRadiusRow>()))];
    }

    private static Task AddRealSpellsAsync(GroupTestWorld world) => world.OnWorldAsync(() =>
    {
        SpellFeature spells = world.World.Services.GetRequiredService<SpellFeature>();
        SpellInfo[] real = RealResurrectionSpells();
        spells.System.Store = new SpellStore([.. spells.System.Store.All.Where(s => real.All(r => r.Id != s.Id)), .. real], [], []);
        return true;
    });

    [Fact]
    public void TheRealRows_HaveNoImplicitTarget_TheCorpseFlag_AndNoAllowDeadTarget()
    {
        foreach (SpellInfo spell in RealResurrectionSpells())
        {
            Assert.Equal(SpellImplicitTarget.None, spell.Effects[0].TargetA);
            Assert.Equal(0x8000u, spell.Targets);
            Assert.False(spell.HasAttribute(SpellAttributesEx2.AllowDeadTarget));
            Assert.True(spell.CanTargetDead); // only through the corpse flag (vmangos IsDeathOnlySpell)
        }
    }

    /// <summary>The tank dies between fights with its body unreleased; the healer casts its real rank-1 spell at the dead unit.</summary>
    [Theory]
    [InlineData(Priest, RealResurrection)]
    [InlineData(Paladin, RealRedemption)]
    public async Task AHealer_ResurrectsADeadMember_WithTheRealSpell(byte healerClass, uint spell)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        await AddRealSpellsAsync(world);
        var tank = await world.AddBotAsync("Realtank", Human, Warrior, 20, [Taunt]);
        uint[] heals = healerClass == Priest ? [LesserHeal, Smite, spell] : [HolyLight, spell];
        var healer = await world.AddBotAsync("Realheal", Human, healerClass, 20, heals);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State is PlayerbotGroupState.Gathering
            or PlayerbotGroupState.Travelling)), world.Trace());
        PlayerbotGroupAI healerAi = world.Coordinator.FindAI(healer.Id)!;
        Assert.Equal(spell, await world.OnWorldAsync(() => healerAi.ResurrectionSpell(world.Player(healer.Id))?.Id));

        await world.OnWorldAsync(() =>
        {
            Player dead = world.Player(tank.Id);
            dead.Map!.Combat.KillPlayer(dead);
            return true;
        });
        Assert.True(await world.RunUntilAsync(120_000, () => world.Player(tank.Id).IsAlive), world.Trace());
        Assert.Equal(tank.Name, healerAi.LastResurrection);
        Assert.False(healerAi.LastResurrectionAtCorpse); // an unreleased body is the dead unit itself
        Assert.Equal(0u, await world.OnWorldAsync(() => (uint)(world.Player(tank.Id).Flags & PlayerFlags.Ghost)));
    }

    /// <summary>
    /// The tank releases at once (it does not wait for the healer): its spirit is a ghost the living cannot target, so the healer walks to
    /// the body and casts at the corpse (TARGET_FLAG_CORPSE). The ghost accepts and is resurrected (by the spell, not at its body).
    /// </summary>
    [Fact]
    public async Task AHealer_ResurrectsAMemberThatReleased_ThroughItsCorpse()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        await AddRealSpellsAsync(world);
        var tank = await world.AddBotAsync("Ghosttank", Human, Warrior, 20, [Taunt]);
        var healer = await world.AddBotAsync("Ghostprie", Human, Priest, 20, [LesserHeal, Smite, RealResurrection]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State is PlayerbotGroupState.Gathering
            or PlayerbotGroupState.Travelling)), world.Trace());
        PlayerbotGroupAI healerAi = world.Coordinator.FindAI(healer.Id)!;
        world.Coordinator.FindAI(tank.Id)!.DeadWaitMs = 0;

        // The healer stands 45 yards off when the tank falls, so the tank has released before the healer is in reach of its body.
        await world.OnWorldAsync(() =>
        {
            Player dead = world.Player(tank.Id), priest = world.Player(healer.Id);
            priest.Relocate(dead.X - 45f, dead.Y, dead.Z, 0, world.World.Host.World.NowMs);
            dead.Map!.Combat.KillPlayer(dead);
            return true;
        });
        Assert.True(await world.RunUntilAsync(30_000, () => (world.Player(tank.Id).Flags & PlayerFlags.Ghost) != 0), world.Trace());
        Assert.True(await world.RunUntilAsync(120_000, () => world.Player(tank.Id).IsAlive), world.Trace());
        Assert.Equal(tank.Name, healerAi.LastResurrection);
        Assert.True(healerAi.LastResurrectionAtCorpse, world.Trace());
    }
}
