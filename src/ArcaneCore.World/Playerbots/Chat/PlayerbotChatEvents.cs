using System.Globalization;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// A bot's recent events for its chat persona, seen by comparing its state between looks (world thread): a level gained, a death, a
/// return to life, money picked up. At most <see cref="MaxEvents"/>, each forgotten after <see cref="MaxAgeMs"/>.
/// </summary>
internal sealed class PlayerbotChatEvents
{
    internal const int MaxEvents = 5;
    internal const long MaxAgeMs = 30 * 60_000;
    private readonly List<(long AtMs, string Text)> _events = [];
    private bool _seen;
    private byte _level;
    private bool _alive;
    private uint _money;

    internal void Observe(Player player, long nowMs)
    {
        byte level = player.Level;
        bool alive = player.IsAlive;
        uint money = player.Money;
        if (_seen)
        {
            if (level > _level) Add(nowMs, string.Create(CultureInfo.InvariantCulture, $"you reached level {level}"));
            if (_alive && !alive) Add(nowMs, "you died");
            if (!_alive && alive) Add(nowMs, "you came back to life");
            if (money > _money) Add(nowMs, "you picked up " + Coins(money - _money));
        }

        _seen = true;
        _level = level;
        _alive = alive;
        _money = money;
    }

    /// <summary>The remembered events, oldest first, as clauses for the prompt.</summary>
    internal IReadOnlyList<string> Recent(long nowMs)
    {
        _events.RemoveAll(e => nowMs - e.AtMs > MaxAgeMs);
        return [.. _events.Select(e => e.Text)];
    }

    private void Add(long nowMs, string text)
    {
        _events.Add((nowMs, text));
        if (_events.Count > MaxEvents) _events.RemoveAt(0);
    }

    internal static string Coins(uint copper)
    {
        uint gold = copper / 10_000, silver = copper / 100 % 100, rest = copper % 100;
        var parts = new List<string>(3);
        if (gold > 0) parts.Add(gold.ToString(CultureInfo.InvariantCulture) + " gold");
        if (silver > 0) parts.Add(silver.ToString(CultureInfo.InvariantCulture) + " silver");
        if (rest > 0 || parts.Count == 0) parts.Add(rest.ToString(CultureInfo.InvariantCulture) + " copper");
        return string.Join(" ", parts);
    }
}
