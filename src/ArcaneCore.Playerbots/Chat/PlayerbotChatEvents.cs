using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>The kinds of event a bot remembers for its chat (<see cref="PlayerbotChatEvents"/>).</summary>
internal enum BotChatEventKind : byte
{
    /// <summary>It gained a level (<see cref="BotChatEvent.Value"/>: the new level).</summary>
    Ding,

    Died,
    Revived,

    /// <summary>It picked up money (<see cref="BotChatEvent.Value"/>: the amount in words).</summary>
    Money,

    /// <summary>It handed in a quest (<see cref="BotChatEvent.Value"/>: the title).</summary>
    QuestDone,

    /// <summary>It looted a notable item, uncommon gear or better or anything rare (<see cref="BotChatEvent.Value"/>: the item's name).</summary>
    Loot,

    /// <summary>Its group killed a dungeon or raid boss (<see cref="BotChatEvent.Value"/>: the boss, <see cref="BotChatEvent.Where"/>: the instance).</summary>
    BossKill,

    /// <summary>Its health fell below a third.</summary>
    LowHealth,

    /// <summary>It entered a dungeon or raid (<see cref="BotChatEvent.Value"/>: its name).</summary>
    EnteredDungeon,

    /// <summary>It entered a battleground (<see cref="BotChatEvent.Value"/>: its name).</summary>
    EnteredBattleground,
}

/// <summary>One remembered event: when, what, and the names it is about (never a GUID).</summary>
internal readonly record struct BotChatEvent(long AtMs, BotChatEventKind Kind, string? Value = null, string? Where = null)
{
    /// <summary>The event as a clause for the model prompt ("you reached level 12").</summary>
    internal string Clause => Kind switch
    {
        BotChatEventKind.Ding => $"you reached level {Value}",
        BotChatEventKind.Died => "you died",
        BotChatEventKind.Revived => "you came back to life",
        BotChatEventKind.Money => $"you picked up {Value}",
        BotChatEventKind.QuestDone => $"you turned in the quest {Value}",
        BotChatEventKind.Loot => $"you looted {Value}",
        BotChatEventKind.BossKill => Where is null ? $"your group killed {Value}" : $"your group killed {Value} in {Where}",
        BotChatEventKind.LowHealth => "you nearly died in a fight",
        BotChatEventKind.EnteredDungeon => $"you entered {Value}",
        BotChatEventKind.EnteredBattleground => $"you joined the battle in {Value}",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// A bot's recent events for its chat persona and the conversation director. Most are seen by comparing its state between looks
/// (<see cref="Observe"/>, world thread, a handful of field reads): a level gained, a death, a return to life, money picked up, health
/// falling low, a dungeon or battleground entered. Others are told (<see cref="Note"/>): a quest handed in, a boss kill. Notable loot is
/// heard from the bot's inventory (<see cref="ArcaneCore.Game.Items.PlayerInventory.ItemCountChanged"/>, subscribed once per player object). At most
/// <see cref="MaxEvents"/>, each forgotten after <see cref="MaxAgeMs"/>.
/// </summary>
internal sealed class PlayerbotChatEvents
{
    internal const int MaxEvents = 8;
    internal const long MaxAgeMs = 30 * 60_000;

    /// <summary>The same kind of event is not noted again within this time (a low-health event per fight, not per tick).</summary>
    internal const long RepeatGapMs = 60_000;

    private readonly List<BotChatEvent> _events = [];
    private bool _seen;
    private byte _level;
    private bool _alive;
    private uint _money;
    private bool _low;
    private uint _mapId;
    private Player? _tracked;
    private long _lastNowMs;

    /// <summary>Item templates by entry, for loot events (set by the owner; null: no loot events).</summary>
    internal Func<uint, ItemTemplate?>? Items { get; set; }

    /// <summary>Whether a map id is a dungeon/raid (1), a battleground (2) or neither (0), and its name; null: no instance events.</summary>
    internal Func<uint, (int Kind, string? Name)>? MapKind { get; set; }

    internal void Observe(Player player, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(player);
        _lastNowMs = nowMs;
        if (!ReferenceEquals(_tracked, player)) Track(player);
        byte level = player.Level;
        bool alive = player.IsAlive;
        uint money = player.Money;
        bool low = alive && player.MaxHealth > 0 && player.Health * 3UL < player.MaxHealth;
        uint map = player.MapId;
        if (_seen)
        {
            if (level > _level) Add(new BotChatEvent(nowMs, BotChatEventKind.Ding, level.ToString(CultureInfo.InvariantCulture)));
            if (_alive && !alive) Add(new BotChatEvent(nowMs, BotChatEventKind.Died));
            if (!_alive && alive) Add(new BotChatEvent(nowMs, BotChatEventKind.Revived));
            if (money > _money) AddMoney(nowMs, money - _money);
            if (low && !_low && player.Combat.IsInCombat) Add(new BotChatEvent(nowMs, BotChatEventKind.LowHealth));
            if (map != _mapId && MapKind?.Invoke(map) is { Kind: > 0, Name: { Length: > 0 } name } entered)
                Add(new BotChatEvent(nowMs, entered.Kind == 2 ? BotChatEventKind.EnteredBattleground : BotChatEventKind.EnteredDungeon, name));
        }

        _seen = true;
        _level = level;
        _alive = alive;
        _money = money;
        _low = low;
        _mapId = map;
    }

    /// <summary>Tell the bot of an event it cannot see from its own state (a quest handed in, a boss its group killed).</summary>
    internal void Note(BotChatEvent ev) => Add(ev);

    /// <summary>The clock of the last <see cref="Observe"/> (the owner's milliseconds; events are stamped and aged by it).</summary>
    internal long LastNowMs => _lastNowMs;

    /// <summary><see cref="Note"/> stamped with <see cref="LastNowMs"/>, for a caller on another clock.</summary>
    internal void NoteNow(BotChatEventKind kind, string? value = null, string? where = null) => Add(new BotChatEvent(_lastNowMs, kind, value, where));

    /// <summary><see cref="Latest"/> on the owner's clock (<see cref="LastNowMs"/>).</summary>
    internal BotChatEvent? LatestNow(BotChatEventKind kind, long withinMs) => Latest(kind, _lastNowMs, withinMs);

    /// <summary>The remembered events, oldest first, as clauses for the prompt.</summary>
    internal IReadOnlyList<string> Recent(long nowMs)
    {
        _events.RemoveAll(e => nowMs - e.AtMs > MaxAgeMs);
        return [.. _events.Select(e => e.Clause)];
    }

    /// <summary>The remembered events, oldest first.</summary>
    internal IReadOnlyList<BotChatEvent> Events(long nowMs)
    {
        _events.RemoveAll(e => nowMs - e.AtMs > MaxAgeMs);
        return [.. _events];
    }

    /// <summary>The newest event of <paramref name="kind"/> within <paramref name="withinMs"/>, or null.</summary>
    internal BotChatEvent? Latest(BotChatEventKind kind, long nowMs, long withinMs)
    {
        for (int i = _events.Count - 1; i >= 0; i--)
            if (_events[i].Kind == kind && nowMs - _events[i].AtMs <= withinMs) return _events[i];
        return null;
    }

    private void Track(Player player)
    {
        if (_tracked is { } old) old.Inventory.ItemCountChanged -= OnItemCount;
        _tracked = player;
        player.Inventory.ItemCountChanged += OnItemCount;
        _seen = false; // a new player object (a login): its state is the baseline, not news
    }

    /// <summary>The inventory gained or lost items (world thread): uncommon gear or anything rare or better is notable loot.</summary>
    private void OnItemCount(uint entry, int delta)
    {
        if (delta <= 0 || Items?.Invoke(entry) is not { Name.Length: > 0 } template) return;
        bool gear = template.Class is 2 or 4 && template.Quality >= 2;
        if (!gear && template.Quality < 3) return;
        Add(new BotChatEvent(_lastNowMs, BotChatEventKind.Loot, template.Name));
    }

    private void AddMoney(long nowMs, uint copper)
    {
        // Coins from one kill after another are one event: the newest money event within the repeat gap is raised.
        for (int i = _events.Count - 1; i >= 0; i--)
        {
            if (_events[i].Kind != BotChatEventKind.Money || nowMs - _events[i].AtMs > RepeatGapMs) continue;
            _events.RemoveAt(i);
            break;
        }

        Add(new BotChatEvent(nowMs, BotChatEventKind.Money, Coins(copper)));
    }

    private void Add(BotChatEvent ev)
    {
        if (ev.Kind is BotChatEventKind.LowHealth or BotChatEventKind.EnteredDungeon or BotChatEventKind.EnteredBattleground or BotChatEventKind.BossKill)
            for (int i = _events.Count - 1; i >= 0; i--)
                if (_events[i].Kind == ev.Kind && _events[i].Value == ev.Value && ev.AtMs - _events[i].AtMs < RepeatGapMs) return;
        _events.Add(ev);
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
