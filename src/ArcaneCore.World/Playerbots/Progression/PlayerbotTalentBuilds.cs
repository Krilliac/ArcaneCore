using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Progression;

/// <summary>
/// One step of a premade build: <see cref="Ranks"/> ranks of the talent at (<see cref="Page"/>, <see cref="Row"/>,
/// <see cref="Column"/>). The page is the tab's index among the class's tabs in <c>TalentCatalog.TabsForClassMask</c> order
/// (TalentTab.dbc order, then id), so a pick resolves against any catalog, synthetic or real, and never names a DBC id.
/// With the build-5875 TalentTab.dbc the pages are the client's tabs, except that the mage's Fire and Arcane tabs share order 0:
/// page 0 is Fire (tab 41) and page 1 Arcane (tab 81).
/// </summary>
internal readonly record struct PlayerbotTalentPick(byte Page, byte Row, byte Column, byte Ranks);

/// <summary>
/// A premade talent build: the picks in learning order (each tier gate met by the picks before it in the same page: row
/// <c>r</c> needs <c>5r</c> points there, vmangos Player::LearnTalent), the item weights that go with it, and the talent
/// <see cref="RoleSpell"/> vmangos CombatBotBaseAI::AutoAssignRole looks for to give the bot its role (0 when that class has
/// no such check or the build is the class's default role).
/// </summary>
internal sealed record PlayerbotTalentBuild(Class Class, string Name, PlayerbotStatWeights Weights, uint RoleSpell,
    IReadOnlyList<PlayerbotTalentPick> Picks)
{
    /// <summary>The points the whole build spends (51 at level 60).</summary>
    public int Points => Picks.GroupBy(pick => (pick.Page, pick.Row, pick.Column)).Sum(group => group.Max(pick => pick.Ranks));
}

/// <summary>
/// Per-class premade builds. vmangos CombatBotBaseAI::LearnPremadeSpecForClass (CombatBotBaseAI.cpp:2407) applies
/// <c>player_premade_spec</c> rows from the world database, which the references do not carry; these builds are authored
/// instead, one to three per class, and each specialised one reaches the role spell AutoAssignRole checks
/// (CombatBotBaseAI.cpp:58-122: Shield Slam, Holy Shield, Sanctity Aura, Shadowform, Elemental Mastery, Stormstrike,
/// Moonkin Form, Leader of the Pack). A bot's build is a pure function of its id (<see cref="Choose"/>).
/// </summary>
internal static class PlayerbotTalentBuilds
{
    /// <summary>vmangos CombatBotBaseAI.cpp:22-29.</summary>
    public const uint ShieldSlam = 23922, HolyShield = 20925, SanctityAura = 20218, Shadowform = 15473,
        ElementalMastery = 16166, Stormstrike = 17364, MoonkinForm = 24858, LeaderOfThePack = 17007;

    private static readonly IReadOnlyDictionary<Class, IReadOnlyList<PlayerbotTalentBuild>> s_builds = Build();

    /// <summary>Every build of every class.</summary>
    public static IEnumerable<PlayerbotTalentBuild> All => s_builds.Values.SelectMany(builds => builds);

    /// <summary>The builds of <paramref name="playerClass"/> (empty for a class without talents).</summary>
    public static IReadOnlyList<PlayerbotTalentBuild> For(Class playerClass)
        => s_builds.TryGetValue(playerClass, out IReadOnlyList<PlayerbotTalentBuild>? builds) ? builds : [];

    /// <summary>The build of the bot with id <paramref name="botId"/> (its character guid): the same bot always gets the same one.</summary>
    public static PlayerbotTalentBuild Choose(Class playerClass, uint botId)
    {
        IReadOnlyList<PlayerbotTalentBuild> builds = For(playerClass);
        return builds.Count == 0
            ? new PlayerbotTalentBuild(playerClass, "none", PlayerbotStatWeights.TwoHandStrength, 0, [])
            : builds[(int)(botId % (uint)builds.Count)];
    }

    private static PlayerbotTalentPick P(byte page, byte row, byte column, byte ranks) => new(page, row, column, ranks);

    private static Dictionary<Class, IReadOnlyList<PlayerbotTalentBuild>> Build() => new()
    {
        [Class.Warrior] =
        [
            // Arms 31 (Mortal Strike) / Fury 20. Pages: 0 Arms, 1 Fury, 2 Protection.
            new(Class.Warrior, "arms", PlayerbotStatWeights.TwoHandStrength, 0,
            [
                P(0, 0, 0, 3), P(0, 0, 2, 3), P(0, 1, 1, 5), P(0, 2, 1, 1), P(0, 2, 2, 3), P(0, 3, 1, 5), P(0, 3, 2, 2),
                P(0, 4, 1, 1), P(0, 4, 0, 5), P(0, 5, 2, 2), P(0, 6, 1, 1),
                P(1, 0, 2, 5), P(1, 1, 2, 5), P(1, 2, 3, 5), P(1, 3, 1, 2), P(1, 3, 2, 3),
            ]),
            // Protection 40 (Shield Slam) / Arms 11.
            new(Class.Warrior, "protection", PlayerbotStatWeights.ShieldTank, ShieldSlam,
            [
                P(2, 0, 1, 5), P(2, 1, 2, 5), P(2, 2, 3, 5), P(2, 2, 1, 3), P(2, 1, 0, 2), P(2, 2, 0, 1), P(2, 3, 0, 3),
                P(2, 3, 2, 2), P(2, 4, 1, 1), P(2, 4, 0, 2), P(2, 5, 2, 5), P(2, 6, 1, 1), P(2, 0, 2, 5),
                P(0, 0, 0, 3), P(0, 0, 1, 5), P(0, 1, 1, 3),
            ]),
            // Fury 34 (Bloodthirst) / Arms 17.
            new(Class.Warrior, "fury", PlayerbotStatWeights.DualWieldStrength, 0,
            [
                P(1, 0, 2, 5), P(1, 1, 2, 5), P(1, 2, 3, 5), P(1, 3, 0, 5), P(1, 3, 1, 2), P(1, 3, 2, 5), P(1, 4, 1, 1),
                P(1, 5, 2, 5), P(1, 6, 1, 1),
                P(0, 0, 0, 3), P(0, 0, 1, 2), P(0, 1, 1, 5), P(0, 2, 1, 1), P(0, 0, 2, 3), P(0, 2, 2, 3),
            ]),
        ],
        [Class.Paladin] =
        [
            // Holy 31 (Holy Shock) / Protection 20: the healer (no Holy Shield, no Sanctity Aura). Pages: 0 Holy, 1 Protection, 2 Retribution.
            new(Class.Paladin, "holy", PlayerbotStatWeights.Healer, 0,
            [
                P(0, 0, 2, 5), P(0, 1, 1, 5), P(0, 2, 0, 3), P(0, 2, 1, 1), P(0, 2, 2, 2), P(0, 3, 1, 5), P(0, 3, 2, 2),
                P(0, 4, 1, 1), P(0, 4, 2, 1), P(0, 5, 2, 5), P(0, 6, 1, 1),
                P(1, 0, 1, 5), P(1, 0, 2, 5), P(1, 1, 1, 2), P(1, 2, 0, 1), P(1, 1, 3, 2), P(1, 3, 1, 3), P(1, 2, 2, 2),
            ]),
            // Protection 35 (Holy Shield) / Holy 16.
            new(Class.Paladin, "protection", PlayerbotStatWeights.ShieldTank, HolyShield,
            [
                P(1, 0, 1, 5), P(1, 0, 2, 5), P(1, 1, 3, 5), P(1, 1, 0, 3), P(1, 2, 0, 1), P(1, 2, 2, 3), P(1, 2, 1, 3),
                P(1, 3, 1, 3), P(1, 4, 1, 1), P(1, 5, 2, 5), P(1, 6, 1, 1),
                P(0, 0, 1, 5), P(0, 0, 2, 5), P(0, 1, 1, 5), P(0, 1, 2, 1),
            ]),
            // Retribution 31 (Sanctity Aura, Repentance) / Holy 20.
            new(Class.Paladin, "retribution", PlayerbotStatWeights.TwoHandStrength, SanctityAura,
            [
                P(2, 0, 2, 5), P(2, 1, 0, 2), P(2, 1, 1, 3), P(2, 2, 1, 5), P(2, 2, 2, 1), P(2, 2, 3, 2), P(2, 3, 0, 2),
                P(2, 4, 0, 3), P(2, 4, 2, 1), P(2, 1, 2, 1), P(2, 5, 1, 5), P(2, 6, 1, 1),
                P(0, 0, 1, 5), P(0, 0, 2, 5), P(0, 1, 1, 5), P(0, 1, 2, 5),
            ]),
        ],
        [Class.Hunter] =
        [
            // Marksmanship 31 (Trueshot Aura) / Beast Mastery 20. Pages: 0 Beast Mastery, 1 Marksmanship, 2 Survival.
            new(Class.Hunter, "marksmanship", PlayerbotStatWeights.Hunter, 0,
            [
                P(1, 0, 2, 5), P(1, 1, 2, 5), P(1, 2, 0, 1), P(1, 2, 1, 5), P(1, 3, 2, 5), P(1, 4, 1, 3), P(1, 4, 0, 1),
                P(1, 5, 2, 5), P(1, 6, 1, 1),
                P(0, 0, 1, 5), P(0, 0, 2, 5), P(0, 1, 2, 3), P(0, 2, 2, 5), P(0, 2, 1, 1), P(0, 1, 1, 1),
            ]),
            // Beast Mastery 37 (Bestial Wrath) / Marksmanship 14.
            new(Class.Hunter, "beast-mastery", PlayerbotStatWeights.Hunter, 0,
            [
                P(0, 0, 1, 5), P(0, 0, 2, 5), P(0, 1, 2, 3), P(0, 1, 1, 2), P(0, 2, 2, 5), P(0, 2, 1, 1), P(0, 3, 2, 5),
                P(0, 3, 1, 2), P(0, 4, 1, 1), P(0, 4, 3, 2), P(0, 5, 2, 5), P(0, 6, 1, 1),
                P(1, 0, 2, 5), P(1, 1, 2, 5), P(1, 2, 0, 1), P(1, 1, 1, 3),
            ]),
        ],
        [Class.Rogue] =
        [
            // Combat 32 (swords, Adrenaline Rush) / Assassination 19. Pages: 0 Assassination, 1 Combat, 2 Subtlety.
            new(Class.Rogue, "combat-swords", PlayerbotStatWeights.DualWieldAgility, 0,
            [
                P(1, 0, 1, 2), P(1, 0, 2, 3), P(1, 1, 2, 5), P(1, 0, 0, 3), P(1, 2, 0, 2), P(1, 3, 2, 5), P(1, 4, 1, 1),
                P(1, 4, 2, 5), P(1, 5, 2, 3), P(1, 5, 1, 2), P(1, 6, 1, 1),
                P(0, 0, 0, 3), P(0, 0, 2, 5), P(0, 1, 0, 3), P(0, 1, 1, 2), P(0, 2, 0, 1), P(0, 1, 3, 3), P(0, 2, 2, 2),
            ]),
            // Assassination 31 (Seal Fate, Vigor) / Combat 20 (daggers).
            new(Class.Rogue, "assassination", PlayerbotStatWeights.DualWieldAgility, 0,
            [
                P(0, 0, 0, 3), P(0, 0, 2, 5), P(0, 1, 0, 3), P(0, 1, 1, 2), P(0, 2, 0, 1), P(0, 1, 3, 3), P(0, 2, 2, 5),
                P(0, 4, 1, 1), P(0, 2, 1, 2), P(0, 5, 1, 5), P(0, 6, 1, 1),
                P(1, 0, 1, 2), P(1, 0, 2, 3), P(1, 1, 2, 5), P(1, 0, 0, 3), P(1, 2, 0, 2), P(1, 3, 1, 5),
            ]),
        ],
        [Class.Priest] =
        [
            // Holy 32 (Spiritual Healing, Lightwell) / Discipline 19: the healer. Pages: 0 Discipline, 1 Holy, 2 Shadow.
            new(Class.Priest, "holy", PlayerbotStatWeights.Healer, 0,
            [
                P(1, 0, 1, 3), P(1, 0, 0, 2), P(1, 1, 2, 5), P(1, 2, 3, 3), P(1, 2, 0, 1), P(1, 0, 2, 1), P(1, 3, 1, 3),
                P(1, 3, 0, 2), P(1, 4, 1, 1), P(1, 4, 2, 5), P(1, 5, 2, 5), P(1, 6, 1, 1),
                P(0, 0, 2, 5), P(0, 1, 2, 3), P(0, 1, 1, 2), P(0, 2, 1, 1), P(0, 2, 2, 3), P(0, 0, 1, 1), P(0, 3, 1, 4),
            ]),
            // Shadow 31 (Shadowform) / Discipline 20.
            new(Class.Priest, "shadow", PlayerbotStatWeights.Caster, Shadowform,
            [
                P(2, 0, 1, 5), P(2, 1, 2, 5), P(2, 1, 1, 2), P(2, 2, 1, 5), P(2, 2, 2, 1), P(2, 3, 3, 5), P(2, 4, 1, 1),
                P(2, 3, 2, 1), P(2, 5, 2, 5), P(2, 6, 1, 1),
                P(0, 0, 2, 5), P(0, 1, 2, 3), P(0, 1, 1, 2), P(0, 2, 1, 1), P(0, 2, 2, 3), P(0, 0, 1, 1), P(0, 3, 1, 5),
            ]),
        ],
        [Class.Shaman] =
        [
            // Elemental 31 (Elemental Mastery) / Restoration 20. Pages: 0 Elemental, 1 Enhancement, 2 Restoration.
            new(Class.Shaman, "elemental", PlayerbotStatWeights.Caster, ElementalMastery,
            [
                P(0, 0, 1, 5), P(0, 0, 2, 5), P(0, 1, 2, 3), P(0, 2, 0, 1), P(0, 2, 2, 5), P(0, 3, 0, 2), P(0, 4, 1, 1),
                P(0, 2, 1, 3), P(0, 5, 2, 5), P(0, 6, 1, 1),
                P(2, 0, 2, 5), P(2, 1, 2, 5), P(2, 2, 0, 3), P(2, 2, 2, 1), P(2, 1, 1, 1), P(2, 3, 2, 5),
            ]),
            // Enhancement 31 (Stormstrike) / Elemental 20.
            new(Class.Shaman, "enhancement", PlayerbotStatWeights.TwoHandShaman, Stormstrike,
            [
                P(1, 0, 1, 5), P(1, 1, 1, 5), P(1, 2, 2, 1), P(1, 2, 3, 4), P(1, 3, 1, 5), P(1, 4, 1, 3), P(1, 3, 2, 2),
                P(1, 5, 2, 5), P(1, 6, 1, 1),
                P(0, 0, 1, 5), P(0, 0, 2, 5), P(0, 1, 2, 3), P(0, 2, 0, 1), P(0, 2, 1, 5), P(0, 1, 1, 1),
            ]),
            // Restoration 37 (Mana Tide Totem) / Enhancement 14: the healer.
            new(Class.Shaman, "restoration", PlayerbotStatWeights.Healer, 0,
            [
                P(2, 0, 1, 5), P(2, 0, 2, 5), P(2, 1, 1, 3), P(2, 2, 0, 3), P(2, 2, 2, 1), P(2, 2, 1, 3), P(2, 3, 2, 5),
                P(2, 4, 2, 1), P(2, 3, 1, 5), P(2, 5, 2, 5), P(2, 6, 1, 1),
                P(1, 0, 1, 5), P(1, 0, 2, 5), P(1, 1, 0, 2), P(1, 1, 3, 2),
            ]),
        ],
        [Class.Mage] =
        [
            // Frost 31 (Ice Barrier) / Arcane 20. Pages: 0 Fire, 1 Arcane, 2 Frost (Fire and Arcane share order 0; ids break the tie).
            new(Class.Mage, "frost", PlayerbotStatWeights.Caster, 0,
            [
                P(2, 0, 1, 5), P(2, 1, 0, 5), P(2, 1, 1, 1), P(2, 2, 0, 3), P(2, 2, 1, 1), P(2, 1, 2, 2), P(2, 3, 1, 3),
                P(2, 4, 1, 1), P(2, 3, 2, 5), P(2, 4, 2, 3), P(2, 5, 2, 1), P(2, 6, 1, 1),
                P(1, 0, 0, 2), P(1, 0, 1, 3), P(1, 0, 2, 5), P(1, 1, 2, 5), P(1, 3, 3, 3), P(1, 2, 1, 2),
            ]),
            // Fire 31 (Combustion) / Arcane 20.
            new(Class.Mage, "fire", PlayerbotStatWeights.Caster, 0,
            [
                P(0, 0, 1, 5), P(0, 1, 0, 5), P(0, 1, 2, 3), P(0, 2, 2, 1), P(0, 2, 0, 2), P(0, 3, 0, 3), P(0, 3, 3, 1),
                P(0, 4, 1, 3), P(0, 4, 2, 1), P(0, 3, 1, 1), P(0, 5, 2, 5), P(0, 6, 1, 1),
                P(1, 0, 0, 2), P(1, 0, 1, 3), P(1, 0, 2, 5), P(1, 1, 2, 5), P(1, 3, 3, 3), P(1, 2, 1, 2),
            ]),
        ],
        [Class.Warlock] =
        [
            // Destruction 31 (Conflagrate) / Affliction 20. Pages: 0 Affliction, 1 Demonology, 2 Destruction.
            new(Class.Warlock, "destruction", PlayerbotStatWeights.Caster, 0,
            [
                P(2, 0, 1, 5), P(2, 1, 1, 5), P(2, 2, 2, 5), P(2, 2, 3, 1), P(2, 3, 1, 2), P(2, 3, 0, 2), P(2, 4, 2, 1),
                P(2, 4, 1, 5), P(2, 5, 2, 4), P(2, 6, 1, 1),
                P(0, 0, 2, 5), P(0, 0, 1, 2), P(0, 1, 2, 2), P(0, 1, 1, 1), P(0, 2, 2, 1), P(0, 2, 0, 3), P(0, 2, 1, 1),
                P(0, 3, 1, 2), P(0, 3, 0, 2), P(0, 1, 3, 1),
            ]),
            // Demonology 31 (Soul Link) / Affliction 20.
            new(Class.Warlock, "demonology", PlayerbotStatWeights.Caster, 0,
            [
                P(1, 0, 2, 5), P(1, 1, 1, 3), P(1, 1, 2, 2), P(1, 2, 1, 1), P(1, 2, 2, 4), P(1, 3, 1, 2), P(1, 3, 2, 3),
                P(1, 4, 1, 1), P(1, 3, 2, 5), P(1, 0, 0, 2), P(1, 5, 2, 5), P(1, 6, 1, 1),
                P(0, 0, 2, 5), P(0, 0, 1, 2), P(0, 1, 2, 2), P(0, 1, 1, 1), P(0, 2, 2, 1), P(0, 2, 0, 3), P(0, 2, 1, 1),
                P(0, 3, 1, 2), P(0, 3, 0, 2), P(0, 1, 3, 1),
            ]),
        ],
        [Class.Druid] =
        [
            // Balance 31 (Moonkin Form) / Restoration 20. Pages: 0 Balance, 1 Feral Combat, 2 Restoration.
            new(Class.Druid, "balance", PlayerbotStatWeights.Caster, MoonkinForm,
            [
                P(0, 0, 0, 5), P(0, 0, 1, 1), P(0, 1, 1, 5), P(0, 2, 3, 2), P(0, 2, 0, 2), P(0, 3, 1, 5), P(0, 4, 1, 1),
                P(0, 4, 2, 3), P(0, 3, 2, 1), P(0, 5, 1, 5), P(0, 6, 1, 1),
                P(2, 0, 1, 5), P(2, 0, 2, 5), P(2, 1, 1, 5), P(2, 2, 2, 1), P(2, 2, 1, 3), P(2, 1, 0, 1),
            ]),
            // Feral 33 (Leader of the Pack) / Restoration 18.
            new(Class.Druid, "feral", PlayerbotStatWeights.Feral, LeaderOfThePack,
            [
                P(1, 0, 1, 5), P(1, 1, 2, 5), P(1, 1, 0, 5), P(1, 2, 1, 1), P(1, 2, 2, 3), P(1, 3, 1, 3), P(1, 3, 3, 2),
                P(1, 3, 2, 2), P(1, 4, 2, 1), P(1, 5, 1, 5), P(1, 6, 1, 1),
                P(2, 0, 1, 5), P(2, 0, 2, 5), P(2, 1, 1, 5), P(2, 2, 2, 1), P(2, 1, 0, 2),
            ]),
            // Restoration 37 (Swiftmend) / Balance 14: the healer.
            new(Class.Druid, "restoration", PlayerbotStatWeights.Healer, 0,
            [
                P(2, 0, 1, 5), P(2, 0, 2, 5), P(2, 1, 0, 5), P(2, 1, 1, 3), P(2, 2, 2, 1), P(2, 2, 1, 3), P(2, 3, 1, 5),
                P(2, 4, 0, 1), P(2, 3, 3, 3), P(2, 5, 2, 5), P(2, 6, 1, 1),
                P(0, 0, 0, 5), P(0, 0, 1, 1), P(0, 1, 1, 5), P(0, 1, 3, 3),
            ]),
        ],
    };
}
