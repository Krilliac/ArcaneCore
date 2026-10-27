using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.Combat;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>What the trouble scan reads about one unit near an idle bot (world thread; filled from the live player).</summary>
internal readonly record struct PlayerbotTroubleFacts(bool Alive, bool SameFaction, bool InCombat, int HealthPercent, int? ManaPercent, bool Moving, bool InDuel);

/// <summary>
/// The pure rules of helping a stranger (docs/areas/playbots-social.md, Helping): who is in trouble, whether a bot helps (a hash, never a
/// <see cref="Random"/>, so the same bot, victim and fight always decide the same way), which attacker a helper takes, which goals leave a bot
/// free, and which class buffs a bot offers a stranger.
/// </summary>
internal static class PlayerbotHelpRules
{
    /// <summary>A victim below this health, in percent, is in trouble.</summary>
    internal const int TroubleHealthPercent = 40;

    /// <summary>A mana user below this mana, in percent, that is on the move in a fight is in trouble (it is running for it).</summary>
    internal const int TroubleManaPercent = 20;

    /// <summary>A helper must have at least this health, in percent, to step in.</summary>
    internal const int HelperMinHealthPercent = 70;

    /// <summary>A helper that heals must have at least this mana, in percent, to step in.</summary>
    internal const int HealerMinManaPercent = 30;

    /// <summary>The most levels an attacker may stand above the helper for the fight to be one it takes on.</summary>
    internal const int MaxLevelsAbove = 3;

    /// <summary>The group size a helper needs to take on an elite.</summary>
    internal const int EliteGroupSize = 3;

    /// <summary>Whether the helper's health, and (a healer's) mana, are enough to step in.</summary>
    internal static bool HelperFit(int healthPercent, int? manaPercent, Class cls)
        => healthPercent >= HelperMinHealthPercent && (!IsHealer(cls) || manaPercent is not { } mana || mana >= HealerMinManaPercent);

    /// <summary>
    /// Whether a bot of <paramref name="botLevel"/> in a group of <paramref name="groupSize"/> takes on the attacker: it stands at most
    /// <see cref="MaxLevelsAbove"/> levels above, and an elite needs a group of <see cref="EliteGroupSize"/> or more.
    /// </summary>
    internal static bool Winnable(int botLevel, int attackerLevel, bool elite, int groupSize)
        => attackerLevel <= botLevel + MaxLevelsAbove && (!elite || groupSize >= EliteGroupSize);

    /// <summary>How far (yards) a bot casts a resurrection or a buff on a stranger (the spells reach 30; a margin is left).</summary>
    internal const float CastYards = 25f;

    /// <summary>
    /// Whether the unit is in trouble now: alive, of the bot's faction, in a fight (not a duel) and either below <see cref="TroubleHealthPercent"/>
    /// health or a mana user below <see cref="TroubleManaPercent"/> mana that is moving.
    /// </summary>
    internal static bool InTrouble(in PlayerbotTroubleFacts unit)
        => unit.Alive && unit.SameFaction && unit.InCombat && !unit.InDuel
            && (unit.HealthPercent < TroubleHealthPercent || (unit.ManaPercent is { } mana && mana < TroubleManaPercent && unit.Moving));

    /// <summary>
    /// The chance a bot helps a victim: its helpfulness scaled by how it feels about them (<c>Helpfulness x (1 + affinity/100)</c>, clamped to
    /// 0..1, the same figure as answering a call for help).
    /// </summary>
    internal static double HelpProbability(float helpfulness, int affinity) => PlayerbotCallForHelp.AnswerProbability(helpfulness, affinity);

    /// <summary>
    /// Whether this bot helps this victim in the trouble episode <paramref name="episode"/> (a number that is the same for the whole fight): a
    /// hash of the three, below <see cref="HelpProbability"/>. Deterministic.
    /// </summary>
    internal static bool Helps(ulong botGuid, ulong victimGuid, uint episode, float helpfulness, int affinity)
        => Roll(PlayerbotContention.Mix(botGuid, victimGuid), 0x7B0B1E5UL ^ episode) < HelpProbability(helpfulness, affinity);

    /// <summary>Whether a bot offers a buff to a stranger this time (a hash of the bot, the stranger and the half hour; below its helpfulness times <paramref name="rate"/>).</summary>
    internal static bool Offers(ulong botGuid, ulong strangerGuid, long nowMs, float helpfulness, double rate)
        => Roll(PlayerbotContention.Mix(botGuid, strangerGuid), 0xB0FF5EEDUL ^ (ulong)(nowMs / 60_000L)) < helpfulness * rate;

    private static double Roll(ulong a, ulong b) => (PlayerbotContention.Mix(a, b) >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>The goals a bot may leave to lend a hand: it is walking, grinding or on an errand, not fighting, resting, recovering, retreating or in a group's run.</summary>
    internal static bool IsFreeGoal(PlayerbotGoalKind goal) => goal is PlayerbotGoalKind.Explore or PlayerbotGoalKind.Quest or PlayerbotGoalKind.Grind
        or PlayerbotGoalKind.Vendor or PlayerbotGoalKind.Train or PlayerbotGoalKind.Home or PlayerbotGoalKind.Mount or PlayerbotGoalKind.Gather;

    /// <summary>Whether the class has a direct heal the brain's assist lane casts at the player it helps (<c>PlayerbotBrain.TryHealTagger</c>).</summary>
    internal static bool IsHealer(Class cls) => cls is Class.Priest or Class.Paladin or Class.Druid or Class.Shaman;

    /// <summary>Whether the class resurrects (<c>PlayerbotGroupContent.CanHeal</c>: priest, paladin, shaman, druid).</summary>
    internal static bool CanResurrect(Class cls) => IsHealer(cls);

    /// <summary>The class buff a bot offers a stranger: the one it casts on groupmates (<c>PlayerbotPriestRotation</c> and its kin), by spell name; null for a class without one.</summary>
    internal static string? BuffOf(Class cls) => cls switch
    {
        Class.Priest => PlayerbotPriestRotation.PowerWordFortitude,
        Class.Druid => PlayerbotDruidRotation.MarkOfTheWild,
        Class.Mage => PlayerbotMageRotation.ArcaneIntellect,
        Class.Paladin => PlayerbotPaladinRotation.BlessingOfMight,
        _ => null,
    };

    /// <summary>Whether the unit carries a PvP flag (a player flagged for PvP or free-for-all).</summary>
    internal static bool IsPvpFlagged(Player player) => (player.UnitFlags & UnitFlags.Pvp) != 0 || (player.Flags & PlayerFlags.FfaPvp) != 0;

    /// <summary>
    /// Whether attacking <paramref name="attacker"/> would flag the bot for PvP: it is a player, a creature a player controls (a pet or a totem), or a unit carrying the PvP flag itself (a guard or an enemy town NPC; <c>MapCombat.Death</c> flags on the same test).
    /// </summary>
    internal static bool WouldFlag(Unit attacker) => attacker is Player || attacker is Creature { CharmerOrOwnerGuid.IsPlayer: true } || (attacker.UnitFlags & UnitFlags.Pvp) != 0;

    /// <summary>
    /// Whether the bot may take a PvP-flagging fight: it is flagged already or War Mode is on. A creature that is no player's never asks.
    /// </summary>
    internal static bool MayFlag(Player bot, bool warMode) => warMode || IsPvpFlagged(bot);

    /// <summary>
    /// The creature among the victim's attackers a helping bot takes: the nearest to the bot that is alive, on its map, one the combat hooks let it
    /// attack, and (when it would flag the bot) only if <paramref name="mayFlag"/>. Players are never taken: the brain's targets are creatures.
    /// Null when there is none.
    /// </summary>
    internal static Creature? AttackerOf(Player bot, Player victim, bool mayFlag, int groupSize)
    {
        if (bot.Map is not { } map) return null;
        Creature? best = null;
        float bestYards = float.MaxValue;
        Consider(victim.Combat.Attackers);
        Consider(victim.Combat.ThreatenedBy);
        return best;

        void Consider(IReadOnlySet<Unit> units)
        {
            if (units.Count == 0) return;
            foreach (Unit unit in units)
            {
                if (unit is not Creature { IsAlive: true, IsInWorld: true } creature || !ReferenceEquals(creature.Map, map)) continue;
                if (WouldFlag(creature) && !mayFlag) continue;
                if (!Winnable(bot.Level, creature.Level, creature.Template.Rank is 1 or 2 or 3, groupSize)) continue;
                if (!map.Combat.Hooks.CanAttack(bot, creature)) continue;
                float yards = Distance(bot, creature);
                if (yards < bestYards)
                {
                    best = creature;
                    bestYards = yards;
                }
            }
        }
    }

    private static readonly string[] Distress = ["adds", "add", "dying", "dead", "die", "aggro", "pls", "plz", "mobs", "attacked", "ganked", "gank", "hp", "health"];
    private static readonly string[] ThanksWords = ["thanks", "thank", "thx", "ty", "tyvm"];
    private static readonly string[] QuestTalk = ["quest", "quests", "where", "how", "anyone", "looking", "find", "tips"];

    /// <summary>
    /// Whether the text is a call for help in words: it holds the whole word "help" (the bots' own call is the template "callhelp"). A thanks for help
    /// ("thanks for the help") and a question about a quest ("can someone help with this quest?") are no call unless a word of distress comes with it
    /// (adds, dying, aggro, pls).
    /// </summary>
    internal static bool LooksLikeHelpCall(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        bool help = false, thanks = false, quest = false, distress = false;
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool letter = i < text.Length && char.IsLetter(text[i]);
            if (letter)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start < 0) continue;
            ReadOnlySpan<char> word = text.AsSpan(start, i - start);
            start = -1;
            if (word.Equals("help", StringComparison.OrdinalIgnoreCase)) help = true;
            else if (Contains(ThanksWords, word)) thanks = true;
            else if (Contains(QuestTalk, word)) quest = true;
            else if (Contains(Distress, word)) distress = true;
        }

        return help && (distress || (!thanks && !quest));
    }

    private static bool Contains(string[] words, ReadOnlySpan<char> word)
    {
        foreach (string candidate in words)
            if (word.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
