using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// The pure pieces of the bot voice (<see cref="PlayerbotNeedsVoice"/>): how players named dungeons and battleground bases, what a group
/// still lacks, how players priced and what a bot may offer, where a flag carrier is, and how a whisper reads ("inv", a price question,
/// "ding"). No world access; every input is real state handed in by the caller.
/// </summary>
internal static partial class PlayerbotVoiceFacts
{
    /// <summary>The names 1.12 players used for the instances (map id: the abbreviations of the time, then the full name).</summary>
    private static readonly Dictionary<uint, string[]> s_dungeons = new()
    {
        [33] = ["SFK", "Shadowfang Keep"],
        [34] = ["Stockades", "Stocks", "the Stockade"],
        [36] = ["DM", "VC", "Deadmines"],
        [43] = ["WC", "Wailing Caverns"],
        [47] = ["RFK", "Razorfen Kraul"],
        [48] = ["BFD", "Blackfathom Deeps"],
        [70] = ["Uldaman", "Ulda"],
        [90] = ["Gnomer", "Gnomeregan"],
        [109] = ["ST", "Sunken Temple"],
        [129] = ["RFD", "Razorfen Downs"],
        [189] = ["SM", "Scarlet Monastery"],
        [209] = ["ZF", "Zul'Farrak"],
        [229] = ["BRS", "Blackrock Spire", "LBRS"],
        [230] = ["BRD", "Blackrock Depths"],
        [249] = ["Ony", "Onyxia"],
        [289] = ["Scholo", "Scholomance"],
        [309] = ["ZG", "Zul'Gurub"],
        [329] = ["Strat", "Stratholme"],
        [349] = ["Mara", "Maraudon"],
        [389] = ["RFC", "Ragefire"],
        [409] = ["MC", "Molten Core"],
        [429] = ["Dire Maul", "DM"],
        [469] = ["BWL", "Blackwing Lair"],
        [509] = ["AQ20", "Ruins of Ahn'Qiraj"],
        [531] = ["AQ40", "Temple of Ahn'Qiraj"],
        [533] = ["Naxx", "Naxxramas"],
    };

    /// <summary>
    /// How a player would name instance <paramref name="mapId"/>: one of the names of the time, or <paramref name="mapName"/> (the
    /// map_template name) for an instance not in the table. Null without either.
    /// </summary>
    internal static string? DungeonName(uint mapId, string? mapName, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (s_dungeons.TryGetValue(mapId, out string[]? names)) return names[random.Next(names.Length)];
        return string.IsNullOrWhiteSpace(mapName) ? null : mapName.Trim();
    }

    /// <summary>
    /// The tanks and healers a group still lacks: <paramref name="needs"/> (the group's quota) less what the classes already waiting can
    /// cover, tanks first (a class that can do both is taken for the scarcer role first), the way <see cref="Groups.PlayerbotGroupContent"/> fills.
    /// </summary>
    internal static (int Tanks, int Healers) MissingRoles(IReadOnlyList<Class> waiting, (int Tanks, int Healers) needs)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        var left = new List<Class>(waiting);
        int tanks = Take(left, needs.Tanks, Groups.PlayerbotGroupContent.CanTank, Groups.PlayerbotGroupContent.CanHeal);
        int healers = Take(left, needs.Healers, Groups.PlayerbotGroupContent.CanHeal, Groups.PlayerbotGroupContent.CanTank);
        return (Math.Max(0, needs.Tanks - tanks), Math.Max(0, needs.Healers - healers));

        static int Take(List<Class> left, int wanted, Func<Class, bool> can, Func<Class, bool> other)
        {
            int taken = 0;
            // The single-role classes first, so a druid or paladin stays free for the other role.
            foreach (Class @class in left.Where(c => can(c)).OrderBy(c => other(c)).ToArray())
            {
                if (taken >= wanted) break;
                left.Remove(@class);
                taken++;
            }

            return taken;
        }
    }

    /// <summary>The roles in chat words ("tank and heals", "2 healers"), or null when none is missing.</summary>
    internal static string? RoleWords(int tanks, int healers)
    {
        string? tank = tanks <= 0 ? null : tanks == 1 ? "tank" : tanks.ToString(CultureInfo.InvariantCulture) + " tanks";
        string? heal = healers <= 0 ? null : healers == 1 ? "heals" : healers.ToString(CultureInfo.InvariantCulture) + " healers";
        return tank is null ? heal : heal is null ? tank : tank + " and " + heal;
    }

    /// <summary>
    /// A price the way players asked it: <paramref name="copper"/> rounded (to the 10 silver from 10 gold up, to the silver from 1 gold,
    /// to the silver from 10 silver, else to the copper) and written "2g", "1g 50s", "35s" or "80c".
    /// </summary>
    internal static string Price(long copper)
    {
        copper = Math.Max(1, copper);
        if (copper >= 100_000) copper = (long)Math.Round(copper / 1000d, MidpointRounding.AwayFromZero) * 1000;
        else if (copper >= 1000) copper = (long)Math.Round(copper / 100d, MidpointRounding.AwayFromZero) * 100;
        long gold = copper / 10_000, silver = copper / 100 % 100, rest = copper % 100;
        CultureInfo c = CultureInfo.InvariantCulture;
        if (gold > 0) return silver > 0 ? string.Create(c, $"{gold}g {silver}s") : string.Create(c, $"{gold}g");
        if (silver > 0) return rest > 0 && silver < 10 ? string.Create(c, $"{silver}s {rest}c") : string.Create(c, $"{silver}s");
        return string.Create(c, $"{rest}c");
    }

    /// <summary>The asking price of one item worth <paramref name="sellPrice"/> copper at a vendor: <paramref name="multiplier"/> times that, at least 1 silver for a green or better.</summary>
    internal static long AskingPrice(uint sellPrice, uint quality, int multiplier)
    {
        long price = (long)sellPrice * Math.Max(1, multiplier);
        return quality >= 2 ? Math.Max(100, price) : Math.Max(1, price);
    }

    /// <summary>What a bag item can be offered as (<see cref="TradeOffer.None"/>: not at all).</summary>
    internal enum TradeOffer : byte
    {
        None,

        /// <summary>One item: a green or better weapon, armour or recipe.</summary>
        Single,

        /// <summary>A stack of trade goods (cloth, ore, herbs, leather), priced each.</summary>
        Stack,
    }

    /// <summary>The item classes of the 1.12 item_template.</summary>
    private const uint ClassWeapon = 2, ClassArmor = 4, ClassTradeGoods = 7, ClassRecipe = 9, ClassQuest = 12;

    /// <summary>The binding of a 1.12 item_template: bind on pickup, quest item.</summary>
    private const uint BindOnPickup = 1, BindQuest = 4;

    /// <summary>
    /// Whether a carried item may be offered for sale, and how: never a grey, a bound or bind-on-pickup item, a quest item or one that
    /// starts a quest, an item the bot would wear (<paramref name="upgrade"/>) or one worth less than <paramref name="minCopper"/> as a
    /// stack; a stack of trade goods needs five or more and white or better, anything else a green or better weapon, armour or recipe.
    /// </summary>
    internal static TradeOffer Offer(ItemTemplate template, uint count, bool soulBound, bool upgrade, int minCopper)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Quality == 0 || soulBound || upgrade || template.Bonding is BindOnPickup or BindQuest || template.Class == ClassQuest
            || template.StartQuest != 0 || template.SellPrice == 0 || (long)template.SellPrice * Math.Max(1u, count) < minCopper) return TradeOffer.None;
        if (template.Class == ClassTradeGoods) return count >= 5 ? TradeOffer.Stack : TradeOffer.None;
        return template.Quality >= 2 && template.Class is ClassWeapon or ClassArmor or ClassRecipe ? TradeOffer.Single : TradeOffer.None;
    }

    /// <summary>Whether an item the bot lacks for a recipe is one players traded (trade goods or reagents, white or better).</summary>
    internal static bool Tradeable(ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Quality >= 1 && template.Bonding is not BindOnPickup and not BindQuest && template.Class is ClassTradeGoods or 5;
    }

    // --- battlegrounds -----------------------------------------------------------------------------------------------------

    /// <summary>The Arathi Basin bases as players called them (<see cref="ArathiBasin"/> node order).</summary>
    private static readonly string[][] s_abNodes =
    [
        ["ST", "stables"], ["BS", "blacksmith"], ["farm", "farm"], ["LM", "lumber mill"], ["GM", "mine"],
    ];

    /// <summary>The Alterac Valley nodes as players called them (<see cref="AlteracValley"/> node order).</summary>
    private static readonly string[][] s_avNodes =
    [
        ["aid station", "SP aid station"], ["SP GY", "stormpike GY"], ["SH GY", "stonehearth GY"], ["SF", "snowfall"], ["IB GY", "iceblood GY"],
        ["FW GY", "frostwolf GY"], ["relief hut", "FW hut"], ["DB south", "south bunker"], ["DB north", "north bunker"], ["IW bunker", "icewing"],
        ["SH bunker", "stonehearth bunker"], ["IB tower", "iceblood tower"], ["TP", "tower point"], ["east tower", "FW east"], ["west tower", "FW west"],
    ];

    /// <summary>An Arathi Basin base's name (null for an unknown node).</summary>
    internal static string? AbNode(int node, Random random)
        => node >= 0 && node < s_abNodes.Length ? s_abNodes[node][random.Next(s_abNodes[node].Length)] : null;

    /// <summary>An Alterac Valley node's name (null for an unknown node).</summary>
    internal static string? AvNode(int node, Random random)
        => node >= 0 && node < s_avNodes.Length ? s_avNodes[node][random.Next(s_avNodes[node].Length)] : null;

    /// <summary>Where a Warsong Gulch flag carrier is, as seen by the team whose flag it carries: near that team's base, mid, or near the carrier's own base.</summary>
    internal static string CarrierWhere(Vector3 carrier, Vector3 ourBase, Vector3 theirBase)
    {
        float ours = Vector3.Distance(carrier, ourBase), theirs = Vector3.Distance(carrier, theirBase);
        float share = ours + theirs <= 0.01f ? 0.5f : ours / (ours + theirs);
        return share < 0.33f ? "in our base" : share < 0.67f ? "mid" : "near their base";
    }

    /// <summary>The kind of Arathi Basin change one team remarks on (<see cref="ArathiBasin.NodeStatus"/> before and after; <paramref name="ours"/>: the team's own was the last owner).</summary>
    internal static string? AbCall(byte before, byte after, Team team, bool ours)
    {
        byte ourContested = team == Team.Alliance ? ArathiBasin.StatusAllianceContested : ArathiBasin.StatusHordeContested;
        byte ourOccupied = team == Team.Alliance ? ArathiBasin.StatusAllianceOccupied : ArathiBasin.StatusHordeOccupied;
        byte theirContested = team == Team.Alliance ? ArathiBasin.StatusHordeContested : ArathiBasin.StatusAllianceContested;
        byte theirOccupied = team == Team.Alliance ? ArathiBasin.StatusHordeOccupied : ArathiBasin.StatusAllianceOccupied;
        if (before == after) return null;
        if (before == ourOccupied && after == theirContested) return "voice.bg.inc";
        if (before == theirContested && after == theirOccupied && ours) return "voice.bg.lost";
        if (before == ourContested && after == ourOccupied) return "voice.bg.ours";
        if (after == ourContested && before != ourOccupied) return "voice.bg.capping";
        return null;
    }

    /// <summary>
    /// The kind of Alterac Valley change one team (<paramref name="teamIndex"/>, 0 Alliance, 1 Horde) remarks on, from a node's owner and
    /// state (<see cref="AlteracValley.NodeOwner"/>, <see cref="AlteracValley.NodeState"/>) before and after.
    /// </summary>
    internal static string? AvCall(int ownerBefore, int stateBefore, int ownerAfter, int stateAfter, bool tower, int teamIndex)
    {
        if (ownerBefore == ownerAfter && stateBefore == stateAfter) return null;
        bool oursBefore = ownerBefore == teamIndex, oursAfter = ownerAfter == teamIndex;
        bool theirsAfter = ownerAfter is 0 or 1 && ownerAfter != teamIndex;
        if (oursBefore && stateBefore == AlteracValley.PointControlled && theirsAfter && stateAfter == AlteracValley.PointAssaulted) return "voice.av.attacked";
        if (!oursBefore && stateBefore == AlteracValley.PointAssaulted && theirsAfter && stateAfter == AlteracValley.PointControlled) return tower ? "voice.av.down" : "voice.bg.lost";
        if (!oursBefore && oursAfter && stateAfter == AlteracValley.PointAssaulted) return "voice.av.assaulted";
        if (oursBefore && stateBefore == AlteracValley.PointAssaulted && oursAfter && stateAfter == AlteracValley.PointControlled) return tower ? "voice.av.burned" : "voice.av.taken";
        return null;
    }

    // --- reading a heard line ------------------------------------------------------------------------------------------------

    /// <summary>Whether a whisper to an LFG or LFM poster asks for an invitation ("inv", "invite pls", "can i come", "i can tank", "me").</summary>
    internal static bool AsksInvite(string text) => !string.IsNullOrWhiteSpace(text) && InviteWords().IsMatch(text.ToLowerInvariant());

    /// <summary>Whether a whisper to a seller asks about its offer (the item's name or a word of it, a price, "still", "wts", "how much", "?").</summary>
    internal static bool AsksAbout(string text, string item)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string line = text.ToLowerInvariant();
        if (PriceWords().IsMatch(line)) return true;
        foreach (string word in (item ?? string.Empty).ToLowerInvariant().Split([' ', '\'', '-', ','], StringSplitOptions.RemoveEmptyEntries))
            if (word.Length >= 4 && line.Contains(word, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Whether a line is a level-up shout ("ding", "ding 30", "DING!").</summary>
    internal static bool IsDing(string text) => !string.IsNullOrWhiteSpace(text) && Ding().IsMatch(text.Trim().ToLowerInvariant());

    [GeneratedRegex(@"^\s*(inv|invite|inv me|inv pls|inv plz|invite me|me|x|\+1|1)\b|\b(inv|invite)( me)?( pls| plz| please)?\b|\bcan i (come|join)\b|\bi('?m| am) (in|down|interested)\b|\bi can (tank|heal|dps)\b|\broom for\b|\bspot\b")]
    private static partial Regex InviteWords();

    [GeneratedRegex(@"\b(how much|price|cost|h/m|hm\?|still (have|selling|got)|wts|buy|cod|interested)\b|\?\s*$")]
    private static partial Regex PriceWords();

    [GeneratedRegex(@"^(ding|dinged|ding!+|ding \d{1,2}!*|\d{1,2} ding!*)$")]
    private static partial Regex Ding();
}
