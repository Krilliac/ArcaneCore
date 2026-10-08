using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using S = ArcaneCore.World.Playerbots.Scenarios.ClassCombatSpells;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Test-only content that makes every playable race and class creatable in the scenario world and gives the class combat
/// scenarios (<see cref="ClassCombatScenario"/>) their spells. Registered by <see cref="ScenarioTestContent"/>.
/// <list type="bullet">
/// <item>A world data store with a start position for every classic race/class pair (all at the human start, so every bot
/// meets the same wolf), a class row per class (its power type; base health and mana are round test values), and race rows
/// that give the alliance races the human faction template and the horde races the orc one.</item>
/// <item>Synthetic spells under their real names and classic ids for rank 1 (and rank 2 of a few) of each rotation's core
/// spells. The shapes follow the classic spell (effect, aura, target, power type, form requirement); amounts, costs, cast
/// times, durations and the two extra range rows (Charge 8-25, Auto Shot 8-35) are test values sized for the 14-health
/// scenario wolf, NOT client data.</item>
/// <item>The Imp (creature 416) for Summon Imp, and a bow and arrows (items 2504 and 2512) for the hunter.</item>
/// </list>
/// </summary>
internal static class ScenarioClassContent
{
    public const uint ImpEntry = 416;

    private const uint ThirtyMinutes = 9901;
    private const uint Permanent = 9902;
    private const uint NineSeconds = 9903;
    private const uint FifteenSeconds = 9904;
    private const uint CastOneAndHalf = 9901;
    private const uint CastTwo = 9902;
    private const uint CastTwoAndHalf = 9903;
    private const uint CastThree = 9904;
    private const uint RangeSelf = 1;
    private const uint RangeCombat = 2;
    private const uint RangeThirty = 4;
    private const uint RangeCharge = 9910;
    private const uint RangeHunter = 9911;

    private const uint Mana = 0;
    private const uint Rage = 1;
    private const uint Energy = 3;

    private const uint FormBattleStance = 17;
    private const uint BattleStanceMask = 1u << ((int)FormBattleStance - 1);

    public static void Register(IServiceCollection services, InMemoryItemTemplateSource items)
    {
        services.AddSingleton<IWorldDataStore>(new ClassWorldDataStore());
        items.Templates.Add(new ItemTemplate
        {
            Entry = CombatHunterScenario.Bow, Name = "Worn Shortbow", Class = 2, SubClass = 2, Quality = 1, InventoryType = 15,
            DisplayId = 8106, Delay = 2300, MaxDurability = 20, AmmoType = 2, Damages = [new ItemDamage(3, 5, 0)],
        });
        items.Templates.Add(new ItemTemplate
        {
            Entry = CombatHunterScenario.Arrow, Name = "Rough Arrow", Class = 6, SubClass = 2, Quality = 1, InventoryType = 24,
            DisplayId = 5996, Stackable = 200, Damages = [new ItemDamage(1, 2, 0)],
        });
    }

    /// <summary>The Imp a warlock summons (creature 416).</summary>
    public static CreatureTemplate Imp => new()
    {
        Entry = ImpEntry, Name = "Imp", Faction = 14, CreatureType = 3, MinLevel = 1, MaxLevel = 1, DisplayIds = [4449],
        MinLevelHealth = 40, MaxLevelHealth = 40, MinMeleeDamage = 1, MaxMeleeDamage = 2, MeleeBaseAttackTime = 2000,
    };

    public static SpellContent Extend(SpellContent content) => content with
    {
        Spells = [.. content.Spells, .. Spells],
        CastTimes = [.. content.CastTimes,
            new SpellCastTimeRow { Id = CastOneAndHalf, CastTime = 1500, MinCastTime = 1500 },
            new SpellCastTimeRow { Id = CastTwo, CastTime = 2000, MinCastTime = 2000 },
            new SpellCastTimeRow { Id = CastTwoAndHalf, CastTime = 2500, MinCastTime = 2500 },
            new SpellCastTimeRow { Id = CastThree, CastTime = 3000, MinCastTime = 3000 }],
        Durations = [.. content.Durations,
            new SpellDurationRow { Id = ThirtyMinutes, Duration = 1_800_000, MaxDuration = 1_800_000 },
            new SpellDurationRow { Id = Permanent, Duration = -1, MaxDuration = -1 },
            new SpellDurationRow { Id = NineSeconds, Duration = 9_000, MaxDuration = 9_000 },
            new SpellDurationRow { Id = FifteenSeconds, Duration = 15_000, MaxDuration = 15_000 }],
        Ranges = [.. content.Ranges,
            new SpellRangeRow { Id = RangeCombat, MaxRange = 5 },
            new SpellRangeRow { Id = RangeCharge, MinRange = 8, MaxRange = 25 },
            new SpellRangeRow { Id = RangeHunter, MinRange = 8, MaxRange = 35 }],
    };

    private static IReadOnlyList<SpellTemplateRow> Spells =>
    [
        // --- warrior -------------------------------------------------------------------------------------------------------
        SelfAura(S.BattleStance, "Battle Stance", aura: 36, misc: (int)FormBattleStance, value: 0, Permanent, gcd: false),
        With(Hostile(S.Charge, "Charge", school: 0, damage: 0, cost: 0, power: Rage, cast: 0, range: RangeCharge, dmgClass: 2), r =>
        {
            r.Effect1 = 96; // SPELL_EFFECT_CHARGE
            r.Effect2 = 30; r.EffectImplicitTargetA2 = 1; r.EffectMiscValue2 = (int)Rage; r.EffectBasePoints2 = 89; r.EffectBaseDice2 = 1; r.EffectDieSides2 = 1;
            r.Attributes = 0x10000000; // SPELL_ATTR_CANT_USED_IN_COMBAT
            r.Stances = BattleStanceMask;
            r.StartRecoveryCategory = 0; r.StartRecoveryTime = 0; r.RecoveryTime = 15_000;
        }),
        Rank(With(DoT(S.Rend, "Rend", tick: 2, cost: 100, power: Rage, range: RangeCombat), r => r.Stances = BattleStanceMask), 1),
        Rank(With(DoT(S.RendRank2, "Rend", tick: 3, cost: 100, power: Rage, range: RangeCombat), r => r.Stances = BattleStanceMask), 2),
        Rank(With(Hostile(S.HeroicStrike, "Heroic Strike", school: 0, damage: 4, cost: 150, power: Rage, cast: 0, range: RangeCombat, dmgClass: 2), r =>
        {
            r.Attributes = 0x00000410; // ON_NEXT_SWING | IS_ABILITY
            r.StartRecoveryCategory = 0; r.StartRecoveryTime = 0;
        }), 1),

        // --- paladin (Seal of Righteousness and Judgement come from ClassScriptScenarioContent) ------------------------------
        Rank(With(SelfAura(S.DevotionAura, "Devotion Aura", aura: 22, misc: 1, value: 25, Permanent, gcd: true), r => r.Effect1 = 35), 1),
        Rank(Heal(S.HolyLight, "Holy Light", amount: 45, cost: 35, cast: CastTwoAndHalf), 1),

        // --- hunter --------------------------------------------------------------------------------------------------------
        With(Hostile(S.AutoShot, "Auto Shot", school: 0, damage: 0, cost: 0, power: Mana, cast: 0, range: RangeHunter, dmgClass: 3), r =>
        {
            r.Effect1 = 58; // SPELL_EFFECT_WEAPON_DAMAGE
            r.Attributes = 0x50012; r.AttributesEx2 = 0x20; r.AttributesEx3 = 0x8000; r.InterruptFlags = 1;
            r.StartRecoveryCategory = 0; r.StartRecoveryTime = 0;
        }),
        Rank(With(Hostile(S.RaptorStrike, "Raptor Strike", school: 0, damage: 5, cost: 15, power: Mana, cast: 0, range: RangeCombat, dmgClass: 2), r =>
        {
            r.Attributes = 0x00000410; // ON_NEXT_SWING | IS_ABILITY
            r.StartRecoveryCategory = 0; r.StartRecoveryTime = 0; r.RecoveryTime = 6000;
        }), 1),
        Rank(With(DoT(S.SerpentSting, "Serpent Sting", tick: 2, cost: 15, power: Mana, range: RangeHunter), r =>
        {
            r.School = 3; r.DmgClass = 3; r.DurationIndex = FifteenSeconds;
        }), 1),

        // --- rogue ---------------------------------------------------------------------------------------------------------
        Rank(With(SelfAura(S.Stealth, "Stealth", aura: 16, misc: 0, value: 5, Permanent, gcd: false), r =>
        {
            r.AuraInterruptFlags = 0x0003_1C07; // broken by attacking, casting, damage
            r.RecoveryTime = 10_000;
        }), 1),
        Rank(With(Hostile(S.SinisterStrike, "Sinister Strike", school: 0, damage: 5, cost: 45, power: Energy, cast: 0, range: RangeCombat, dmgClass: 2), r =>
        {
            r.Effect2 = 80; r.EffectImplicitTargetA2 = 6; r.EffectBasePoints2 = 0; r.EffectBaseDice2 = 1; r.EffectDieSides2 = 1; // one combo point
            r.StartRecoveryTime = 1000;
        }), 1),
        Rank(With(Hostile(S.Eviscerate, "Eviscerate", school: 0, damage: 6, cost: 35, power: Energy, cast: 0, range: RangeCombat, dmgClass: 2), r =>
        {
            r.AttributesEx = 0x00080000; // FINISHING_MOVE_DAMAGE
            r.EffectPointsPerComboPoint1 = 5;
        }), 1),

        // --- priest --------------------------------------------------------------------------------------------------------
        Rank(Hostile(S.Smite, "Smite", school: 1, damage: 7, cost: 20, power: Mana, cast: CastOneAndHalf, range: RangeThirty, dmgClass: 1), 1),
        Rank(Heal(S.LesserHeal, "Lesser Heal", amount: 50, cost: 30, cast: CastOneAndHalf), 1),
        Rank(FriendAura(S.PowerWordFortitude, "Power Word: Fortitude", aura: 29, misc: 2, value: 3, cost: 15), 1),

        // --- shaman --------------------------------------------------------------------------------------------------------
        Rank(Hostile(S.LightningBolt, "Lightning Bolt", school: 3, damage: 8, cost: 15, power: Mana, cast: CastOneAndHalf, range: RangeThirty, dmgClass: 1), 1),
        Rank(Heal(S.HealingWave, "Healing Wave", amount: 40, cost: 25, cast: CastOneAndHalf), 1),
        Rank(SelfAura(S.LightningShield, "Lightning Shield", aura: 4, misc: 0, value: 13, ThirtyMinutes, gcd: true, cost: 15), 1), // a dummy: its procs are not modelled

        // --- mage ----------------------------------------------------------------------------------------------------------
        Rank(Hostile(S.Fireball, "Fireball", school: 2, damage: 8, cost: 30, power: Mana, cast: CastOneAndHalf, range: RangeThirty, dmgClass: 1), 1),
        Rank(Hostile(S.FireballRank2, "Fireball", school: 2, damage: 20, cost: 45, power: Mana, cast: CastTwo, range: RangeThirty, dmgClass: 1), 2),
        Rank(SelfAura(S.FrostArmor, "Frost Armor", aura: 22, misc: 1, value: 30, ThirtyMinutes, gcd: true, cost: 15), 1),
        Rank(FriendAura(S.ArcaneIntellect, "Arcane Intellect", aura: 29, misc: 3, value: 2, cost: 15), 1),

        // --- warlock -------------------------------------------------------------------------------------------------------
        Rank(Hostile(S.ShadowBolt, "Shadow Bolt", school: 5, damage: 8, cost: 25, power: Mana, cast: CastOneAndHalf, range: RangeThirty, dmgClass: 1), 1),
        Rank(SelfAura(S.DemonSkin, "Demon Skin", aura: 22, misc: 1, value: 30, ThirtyMinutes, gcd: true, cost: 10), 1),
        new SpellTemplateRow
        {
            Id = S.SummonImp, SpellName = "Summon Imp", School = 5, RangeIndex = RangeSelf, CastingTimeIndex = CastThree,
            PowerType = Mana, ManaCost = 30, StartRecoveryCategory = 133, StartRecoveryTime = 1500, SpellVisual = 1,
            Effect1 = 56, EffectImplicitTargetA1 = 1, EffectMiscValue1 = (int)ImpEntry, EffectBaseDice1 = 1, EffectDieSides1 = 1,
        },

        // --- druid ---------------------------------------------------------------------------------------------------------
        Rank(Hostile(S.Wrath, "Wrath", school: 3, damage: 8, cost: 20, power: Mana, cast: CastOneAndHalf, range: RangeThirty, dmgClass: 1), 1),
        Rank(Heal(S.HealingTouch, "Healing Touch", amount: 45, cost: 25, cast: CastOneAndHalf), 1),
        Rank(FriendAura(S.MarkOfTheWild, "Mark of the Wild", aura: 22, misc: 1, value: 25, cost: 20), 1),
    ];

    private static SpellTemplateRow Hostile(uint id, string name, uint school, int damage, uint cost, uint power, uint cast, uint range, uint dmgClass)
        => new()
        {
            Id = id, SpellName = name, School = school, RangeIndex = range, CastingTimeIndex = cast, PowerType = power, ManaCost = cost,
            DmgClass = dmgClass, StartRecoveryCategory = 133, StartRecoveryTime = 1500, InterruptFlags = cast != 0 ? 0x1u : 0u,
            Effect1 = 2, EffectImplicitTargetA1 = 6, EffectBasePoints1 = damage - 1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
        };

    private static SpellTemplateRow DoT(uint id, string name, int tick, uint cost, uint power, uint range) => new()
    {
        Id = id, SpellName = name, School = 0, RangeIndex = range, PowerType = power, ManaCost = cost, DurationIndex = NineSeconds,
        DmgClass = 2, StartRecoveryCategory = 133, StartRecoveryTime = 1500, SpellVisual = 1,
        Effect1 = 6, EffectApplyAuraName1 = 3, EffectAmplitude1 = 3000, EffectImplicitTargetA1 = 6,
        EffectBasePoints1 = tick - 1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
    };

    private static SpellTemplateRow Heal(uint id, string name, int amount, uint cost, uint cast) => new()
    {
        Id = id, SpellName = name, School = 1, RangeIndex = RangeThirty, CastingTimeIndex = cast, PowerType = Mana, ManaCost = cost,
        DmgClass = 1, StartRecoveryCategory = 133, StartRecoveryTime = 1500, InterruptFlags = 0x1,
        Effect1 = 10, EffectImplicitTargetA1 = 21, EffectBasePoints1 = amount - 1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
    };

    private static SpellTemplateRow SelfAura(uint id, string name, uint aura, int misc, int value, uint duration, bool gcd, uint cost = 0) => new()
    {
        Id = id, SpellName = name, RangeIndex = RangeSelf, DurationIndex = duration, PowerType = Mana, ManaCost = cost, SpellVisual = 1,
        StartRecoveryCategory = gcd ? 133u : 0u, StartRecoveryTime = gcd ? 1500u : 0u,
        Effect1 = 6, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = aura, EffectMiscValue1 = misc,
        EffectBasePoints1 = value - 1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
    };

    private static SpellTemplateRow FriendAura(uint id, string name, uint aura, int misc, int value, uint cost) => new()
    {
        Id = id, SpellName = name, RangeIndex = RangeThirty, DurationIndex = ThirtyMinutes, PowerType = Mana, ManaCost = cost, SpellVisual = 1,
        StartRecoveryCategory = 133, StartRecoveryTime = 1500,
        Effect1 = 6, EffectImplicitTargetA1 = 21, EffectApplyAuraName1 = aura, EffectMiscValue1 = misc,
        EffectBasePoints1 = value - 1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
    };

    private static SpellTemplateRow Rank(SpellTemplateRow row, int rank)
    {
        row.Rank = "Rank " + rank;
        return row;
    }

    private static SpellTemplateRow With(SpellTemplateRow row, Action<SpellTemplateRow> change)
    {
        change(row);
        return row;
    }

    /// <summary>
    /// Every classic race/class pair starts at the human start on map 0 (zone 12). Alliance races (human, dwarf, night elf,
    /// gnome) use faction template 1, horde races (orc, undead, tauren, troll) faction template 2 (both in the scenario faction
    /// catalog).
    /// </summary>
    private sealed class ClassWorldDataStore : IWorldDataStore
    {
        private static readonly Dictionary<byte, byte[]> Classes = new()
        {
            [1] = [1, 2, 4, 5, 8, 9],    // human: warrior, paladin, rogue, priest, mage, warlock
            [2] = [1, 3, 4, 7, 9],       // orc: warrior, hunter, rogue, shaman, warlock
            [3] = [1, 2, 3, 4, 5],       // dwarf: warrior, paladin, hunter, rogue, priest
            [4] = [1, 3, 4, 5, 11],      // night elf: warrior, hunter, rogue, priest, druid
            [5] = [1, 4, 5, 8, 9],       // undead: warrior, rogue, priest, mage, warlock
            [6] = [1, 3, 7, 11],         // tauren: warrior, hunter, shaman, druid
            [7] = [1, 4, 8, 9],          // gnome: warrior, rogue, mage, warlock
            [8] = [1, 3, 4, 5, 7, 8],    // troll: warrior, hunter, rogue, priest, shaman, mage
        };

        private static bool IsValid(byte race, byte cls) => Classes.TryGetValue(race, out byte[]? classes) && classes.Contains(cls);

        public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult<StartPosition?>(IsValid(race, cls)
                ? new StartPosition(0, 12, ScenarioTestContent.StartX, ScenarioTestContent.StartY, ScenarioTestContent.StartZ, 0f)
                : null);

        public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
        {
            if (!Classes.ContainsKey(race)) return Task.FromResult<RaceInfo?>(null);
            uint display = race switch { 1 => 49, 2 => 51, 3 => 53, 4 => 55, 5 => 57, 6 => 59, 7 => 1563, _ => 1478 };
            bool horde = race is 2 or 5 or 6 or 8;
            return Task.FromResult<RaceInfo?>(new RaceInfo(display + gender, horde ? 2u : 1u));
        }

        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default) => Task.FromResult<ClassInfo?>(cls switch
        {
            1 => new ClassInfo(60, 0, 1),     // warrior: rage (the shared test value)
            4 => new ClassInfo(55, 0, 3),     // rogue: energy
            2 => new ClassInfo(68, 150, 0),
            3 => new ClassInfo(66, 150, 0),
            5 => new ClassInfo(72, 200, 0),
            7 => new ClassInfo(75, 150, 0),
            8 => new ClassInfo(72, 200, 0),
            9 => new ClassInfo(63, 200, 0),
            11 => new ClassInfo(64, 150, 0),
            _ => null,
        });

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult(IsValid(race, cls));
    }
}
