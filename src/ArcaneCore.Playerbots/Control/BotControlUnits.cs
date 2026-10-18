using ArcaneCore.Game.Combat;
using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Playerbots.Control;

/// <summary>
/// The unit (or game object) a <c>.botctl</c> argument names, seen from one bot (docs/ops/botctl.md, "Naming units"):
/// <c>self</c>, <c>target</c>, <c>victim</c>, <c>attacker</c>, <c>0xGUID</c>, <c>npc:ENTRY</c>, <c>go:ENTRY</c>,
/// <c>nearest[:hostile|:any|:dead|:friendly]</c>, <c>corpse</c> (nearest dead creature), <c>player:NAME</c>, or a name (an online player, else
/// the nearest visible creature or object whose name contains it). Searches cover what the bot sees (its visible objects). World thread.
/// </summary>
internal static class BotControlUnits
{
    internal static WorldObject? Resolve(WorldRuntime world, Player bot, string spec, out string? error)
    {
        error = null;
        string text = spec.Trim();
        Map? map = bot.Map;
        if (map is null)
        {
            error = "the bot is on no map";
            return null;
        }

        string lower = text.ToLowerInvariant();
        switch (lower)
        {
            case "self" or "me":
                return bot;
            case "target" or "selection":
                return Found(map.FindObject(bot.Selection), "the bot has no target", out error);
            case "victim":
                return Found(bot.Combat.Victim, "the bot is attacking nothing", out error);
            case "attacker":
                return Found(bot.Combat.Attackers.Where(a => ReferenceEquals(a.Map, map)).OrderBy(a => Distance(bot, a)).FirstOrDefault(),
                    "nothing attacks the bot", out error);
            case "corpse":
                return Found(Nearest(bot, map, o => o is Creature { IsAlive: false }), "no dead creature in sight", out error);
        }

        if (lower.StartsWith("0x", StringComparison.Ordinal))
        {
            if (!ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong raw))
            {
                error = $"'{text}' is not a hex guid";
                return null;
            }

            var guid = new ObjectGuid(raw);
            return Found((WorldObject?)map.FindObject(guid) ?? world.FindOnlinePlayer(guid), $"no object {text} on the bot's map", out error);
        }

        if (lower.StartsWith("guid:", StringComparison.Ordinal))
        {
            if (!ulong.TryParse(text[5..], NumberStyles.None, CultureInfo.InvariantCulture, out ulong raw))
            {
                error = $"'{text}' is not a guid";
                return null;
            }

            return Found((WorldObject?)map.FindObject(new ObjectGuid(raw)) ?? world.FindOnlinePlayer(new ObjectGuid(raw)), $"no object {text} on the bot's map", out error);
        }

        if (TryEntry(lower, "npc:", out uint npc) || TryEntry(lower, "creature:", out npc))
        {
            return Found(Nearest(bot, map, o => o is Creature c && c.Entry == npc, preferAlive: true), $"no creature {npc} in sight", out error);
        }

        if (TryEntry(lower, "go:", out uint go) || TryEntry(lower, "object:", out go))
        {
            return Found(Nearest(bot, map, o => o is GameObject g && g.Entry == go), $"no game object {go} in sight", out error);
        }

        if (lower.StartsWith("nearest", StringComparison.Ordinal))
        {
            string kind = lower.Length > 8 ? lower[8..] : "hostile";
            Func<WorldObject, bool> match = kind switch
            {
                "any" => o => o is Creature,
                "dead" => o => o is Creature { IsAlive: false },
                "friendly" => o => o is Creature { IsAlive: true } c && !map.Combat.Hooks.IsHostileTo(c, bot),
                "player" => o => o is Player p && !ReferenceEquals(p, bot),
                _ => o => o is Creature { IsAlive: true } c && map.Combat.Hooks.CanAttack(bot, c) && map.Combat.Hooks.IsHostileTo(c, bot),
            };
            return Found(Nearest(bot, map, match), $"no {kind} creature in sight", out error);
        }

        string name = lower.StartsWith("player:", StringComparison.Ordinal) ? text[7..] : text;
        if (world.FindOnlinePlayer(name) is { } player) return player;
        if (lower.StartsWith("player:", StringComparison.Ordinal))
        {
            error = $"no online player '{name}'";
            return null;
        }

        WorldObject? named = Nearest(bot, map, o => NameOf(o)?.Contains(name, StringComparison.OrdinalIgnoreCase) == true, preferAlive: true);
        return Found(named, $"no player, creature or object named '{name}' in sight", out error);
    }

    internal static string? NameOf(WorldObject obj) => obj switch
    {
        Player p => p.Name,
        Creature c => c.Template.Name,
        GameObject g => g.Template.Name,
        _ => null,
    };

    internal static float Distance(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static bool TryEntry(string text, string prefix, out uint entry)
    {
        entry = 0;
        return text.StartsWith(prefix, StringComparison.Ordinal)
            && uint.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out entry);
    }

    private static WorldObject? Found(WorldObject? obj, string missing, out string? error)
    {
        error = obj is null ? missing : null;
        return obj;
    }

    /// <summary>The nearest object the bot sees that matches (the living first when <paramref name="preferAlive"/>).</summary>
    internal static WorldObject? Nearest(Player bot, Map map, Func<WorldObject, bool> match, bool preferAlive = false)
    {
        WorldObject? best = null;
        float bestDistance = float.MaxValue;
        bool bestAlive = false;
        foreach (ObjectGuid guid in bot.VisibleObjects)
        {
            if (map.FindObject(guid) is not { } obj || ReferenceEquals(obj, bot) || !match(obj)) continue;
            bool alive = obj is not Unit unit || unit.IsAlive;
            float distance = Distance(bot, obj);
            if (preferAlive && bestAlive && !alive) continue;
            if ((preferAlive && alive && !bestAlive) || distance < bestDistance)
            {
                best = obj;
                bestDistance = distance;
                bestAlive = alive;
            }
        }

        return best;
    }
}