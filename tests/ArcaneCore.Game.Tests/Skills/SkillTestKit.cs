using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Skills;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Tests.Skills;

/// <summary>A scripted <see cref="ICombatRandom"/>: integers and floats come from queues, the default is the maximum (no roll succeeds early).</summary>
internal sealed class ScriptedSkillRandom : ICombatRandom
{
    public Queue<int> Ints { get; } = new();

    public Queue<float> Floats { get; } = new();

    public int Next(int minInclusive, int maxInclusive) => Ints.Count > 0 ? Ints.Dequeue() : maxInclusive;

    public float NextFloat(float min, float max) => Floats.Count > 0 ? Floats.Dequeue() : max;
}

/// <summary>An in-memory spellbook that records what the skill rules asked of it.</summary>
internal sealed class FakeSkillSpellHost : ISkillSpellHost
{
    public HashSet<uint> Known { get; } = [];

    public List<string> Calls { get; } = [];

    /// <summary>The player whose skills are told about learned and forgotten spells, like the real spellbook owner does.</summary>
    public Player? Cascade { get; set; }

    public int WeaponSkillRemovedCount { get; private set; }

    public bool HasSpell(uint spellId) => Known.Contains(spellId);

    public void LearnSpell(uint spellId)
    {
        Calls.Add($"learn {spellId}");
        if (Known.Add(spellId))
        {
            Cascade?.Skills?.OnSpellLearned(spellId);
        }
    }

    public void RemoveSpell(uint spellId)
    {
        Calls.Add($"remove {spellId}");
        if (Known.Remove(spellId))
        {
            Cascade?.Skills?.OnSpellForgotten(spellId);
        }
    }

    public void WeaponSkillRemoved() => WeaponSkillRemovedCount++;
}

/// <summary>
/// A small build-5875-shaped skill catalog: ids and categories follow SharedDefines.h; the ability and spell
/// rows are fixtures (ids from vanilla: 201 One-Handed Swords, 668 Language Common, 750 Plate Mail, 2575/2576
/// Mining Apprentice/Journeyman, 2580 Find Minerals, 2581/2582 Mining ranks). No real data is copied.
/// </summary>
internal static class SkillTestKit
{
    public const uint SwordsSpell = 201;
    public const uint CommonSpell = 668;
    public const uint PlateSpell = 750;
    public const uint MiningApprentice = 2575;
    public const uint MiningJourneyman = 2576;
    public const uint FindMinerals = 2580;
    public const uint SmeltCopper = 2657;
    public const uint DualWieldSpell = 674;
    public const uint PoisonsSpell = 2842;
    public const uint CookingSpell = 2550;

    public static SkillCatalog Catalog(Func<IEnumerable<SkillLineRecord>, IEnumerable<SkillLineRecord>>? lines = null)
    {
        SkillLineRecord[] baseLines =
        [
            new(SkillIds.Swords, SkillCategories.Weapon, "Swords", 0),
            new(SkillIds.Axes, SkillCategories.Weapon, "Axes", 0),
            new(SkillIds.Unarmed, SkillCategories.Weapon, "Unarmed", 0),
            new(SkillIds.Defense, SkillCategories.Attributes, "Defense", 0),
            new(SkillIds.LanguageCommon, SkillCategories.Languages, "Common", 0),
            new(SkillIds.PlateMail, SkillCategories.Armor, "Plate Mail", 0),
            new(SkillIds.Poisons, SkillCategories.Class, "Poisons", 0),
            new(SkillIds.Mining, SkillCategories.Profession, "Mining", 0),
            new(SkillIds.Herbalism, SkillCategories.Profession, "Herbalism", 0),
            new(SkillIds.Skinning, SkillCategories.Profession, "Skinning", 0),
            new(SkillIds.Blacksmithing, SkillCategories.Profession, "Blacksmithing", 0),
            new(SkillIds.Fishing, SkillCategories.Secondary, "Fishing", 0),
            new(SkillIds.Cooking, SkillCategories.Secondary, "Cooking", 0),
            new(SkillIds.Lockpicking, SkillCategories.Secondary, "Lockpicking", 0),
            new(SkillIds.DualWield, SkillCategories.Class, "Dual Wield", 0),
            new(SkillIds.Riding, SkillCategories.Secondary, "Riding", 0),
        ];

        SkillRaceClassInfoRecord[] raceClass =
        [
            new(SkillIds.Swords, 0, 0, 0, 0, 0),
            new(SkillIds.Axes, 0, 0, 0, 0, 0),
            new(SkillIds.Unarmed, 0, 0, SkillRaceClassFlags.AlwaysMaxValue, 0, 0),
            new(SkillIds.Defense, 0, 0, 0, 0, 0),
            new(SkillIds.LanguageCommon, 0, 0, 0, 0, 0),
            new(SkillIds.PlateMail, 0, 0, SkillRaceClassFlags.AlwaysMaxValue | SkillRaceClassFlags.MonoValue, 0, 0),
            new(SkillIds.Poisons, 0, 0, 0, 0, 0),
            new(SkillIds.Mining, 0, 0, SkillRaceClassFlags.Unlearnable, 0, 21),
            new(SkillIds.Herbalism, 0, 0, SkillRaceClassFlags.Unlearnable, 0, 21),
            new(SkillIds.Skinning, 0, 0, SkillRaceClassFlags.Unlearnable, 0, 21),
            new(SkillIds.Blacksmithing, 0, 0, SkillRaceClassFlags.Unlearnable, 0, 21),
            new(SkillIds.Fishing, 0, 0, 0, 0, 21),
            new(SkillIds.Cooking, 0, 0, 0, 0, 21),
            new(SkillIds.Lockpicking, 0, 0, 0, 0, 0),
            new(SkillIds.DualWield, 0, 0, SkillRaceClassFlags.AlwaysMaxValue | SkillRaceClassFlags.MonoValue, 0, 0),
            new(SkillIds.Riding, 0, 0, 0, 0, 21),
        ];

        var tier = new SkillTierRecord(21, Enumerable.Repeat(0u, 16).ToArray(), Enumerable.Range(1, 16).Select(step => (uint)(step * 75)).ToArray());
        SkillLineAbilityRecord[] abilities =
        [
            // id, skill, spell, race, class, req skill value, forward, learn on get, max (trivial high), min (trivial low)
            new(1, SkillIds.Swords, SwordsSpell, 0, 0, 0, 0, 2, 0, 0),
            new(2, SkillIds.LanguageCommon, CommonSpell, 0, 0, 0, 0, 2, 0, 0),
            new(3, SkillIds.PlateMail, PlateSpell, 0, 0, 0, 0, 2, 0, 0),
            new(4, SkillIds.Mining, MiningApprentice, 0, 0, 0, MiningJourneyman, 0, 0, 0),
            new(5, SkillIds.Mining, MiningJourneyman, 0, 0, 0, 0, 0, 0, 0),
            new(6, SkillIds.Mining, FindMinerals, 0, 0, 1, 0, 1, 0, 0),
            new(7, SkillIds.Mining, 2581, 0, 0, 100, 0, 1, 0, 0),
            new(8, SkillIds.Blacksmithing, SmeltCopper, 0, 0, 1, 0, 0, 100, 25),
            new(9, SkillIds.Poisons, PoisonsSpell, 0, 1u << 3, 0, 0, 0, 0, 0),
            new(11, SkillIds.Cooking, CookingSpell, 0, 0, 0, 0, 2, 0, 0),
            new(10, SkillIds.DualWield, DualWieldSpell, 0, 0, 0, 0, 0, 0, 0),
        ];

        // Mining Apprentice/Journeyman: effect 1 is SKILL for skill 186 (step 1 and 2).
        var learn = SpellLearnSkillTable.Build(
        [
            new SpellSkillEffect(MiningApprentice, 1, (int)SkillIds.Mining, 0, 1),
            new SpellSkillEffect(MiningJourneyman, 1, (int)SkillIds.Mining, 1, 1),
        ]);

        return new SkillCatalog(lines is null ? baseLines : lines(baseLines), raceClass, [tier], abilities, learn);
    }

    public static (Player Player, PlayerSkills Skills, FakeSkillSpellHost Host, ScriptedSkillRandom Random) CreateSkills(
        byte level = 1, SkillOptions? options = null, SkillCatalog? catalog = null)
    {
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        player.Level = level;
        var host = new FakeSkillSpellHost { Cascade = player };
        var random = new ScriptedSkillRandom();
        var skills = new PlayerSkills(player, catalog ?? Catalog(), options ?? new SkillOptions(), host, random);
        player.AttachSkills(skills);
        return (player, skills, host, random);
    }

    public static int SlotIndex(int slot) => UpdateFields.PlayerSkillInfo11 + (3 * slot);

    public static void SetClass(Player player, Class playerClass) => player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)playerClass);
}
