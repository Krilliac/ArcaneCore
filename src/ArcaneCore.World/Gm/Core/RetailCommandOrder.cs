namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// The order of the root commands in the vmangos command table (D:\refs\vmangos\src\game\Chat\Chat.cpp:1185-1366).
/// Abbreviations resolve to the first entry whose name starts with the typed word (hasStringAbbr,
/// Chat.cpp:1566-1600), so the table order decides what ".g" or ".s" means. Roots this server adds
/// that are not in the retail table keep their registration order after the retail ones.
/// </summary>
public static class RetailCommandOrder
{
    private static readonly string[] Roots =
    [
        "account", "auction", "cast", "character", "charge", "cheat", "debug", "deplenish",
        "replenish", "event", "gm", "honor", "go", "gobject", "guild", "instance",
        "learn", "list", "lookup", "modify", "npc", "unit", "pool", "pdump",
        "quest", "reload", "reset", "server", "tele", "trigger", "wp", "service",
        "bot", "ahbot", "partybot", "battlebot", "world", "possess", "cinematic", "escort",
        "bg", "spell", "pvp", "variable", "aura", "nameaura", "unaura", "announce",
        "notify", "goname", "namego", "group", "groupgo", "gocorpse", "commands", "demorph",
        "namedie", "die", "fear", "knockback", "revive", "mount", "dismount", "gps",
        "guid", "help", "itemmove", "cooldown", "unlearn", "removeriding", "distance", "angle",
        "recall", "save", "saveall", "kick", "ban", "unban", "baninfo", "banlist",
        "start", "unstuck", "taxicheat", "linkgrave", "neargrave", "explorecheat", "hover", "levelup",
        "showarea", "hidearea", "additem", "deleteitem", "additemset", "bank", "wchange", "ticket",
        "maxskill", "setskill", "whispers", "wr", "pinfo", "groupinfo", "pbcast", "respawn",
        "send", "mute", "unmute", "movegens", "cometome", "aoedamage", "damage", "combatstop",
        "repairitems", "stable", "quit", "mmap", "video", "freeze", "unfreeze", "anticheat",
        "groupspell", "pet", "channel", "log", "sniff", "spamer", "antispam", "gold",
        "wareffort",
    ];

    private static readonly Dictionary<string, int> Index = Roots
        .Select((name, i) => (name, i))
        .ToDictionary(t => t.name, t => t.i, StringComparer.OrdinalIgnoreCase);

    /// <summary>The position of a root in the retail table, or -1 when the retail table has no such root.</summary>
    public static int IndexOf(string root) => Index.GetValueOrDefault(root, -1);

    /// <summary>Roots in retail order first (stable), then unknown roots in their given order.</summary>
    public static List<T> Sort<T>(IEnumerable<T> roots, Func<T, string> name)
        => [.. roots.Select((r, i) => (r, i)).OrderBy(t =>
            {
                int index = IndexOf(name(t.r));
                return index < 0 ? int.MaxValue : index;
            }).ThenBy(t => t.i).Select(t => t.r)];
}
