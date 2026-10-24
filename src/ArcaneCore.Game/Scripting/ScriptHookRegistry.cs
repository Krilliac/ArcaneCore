using System.Reflection;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Scripting;

/// <summary>
/// The global script hooks of one world (AzerothCore <c>ScriptMgr</c> with its per-hook enabled lists, ScriptMgr.h / ScriptDefines/*).
/// <see cref="Register"/> adds a hook object to each hook whose method its class overrides; every hook keeps its own array, so a dispatch
/// with nothing registered is a field load and a length check and allocates nothing. Registration happens before the world thread starts
/// (<see cref="Maps.WorldRuntime.Start"/> freezes it); dispatch is world thread only. A throwing hook is logged and the others still run.
/// </summary>
public sealed partial class ScriptHookRegistry
{
    private readonly List<IScriptHooks> _registered = [];
    private bool _frozen;

    public ScriptHookRegistry()
    {
        Player = new PlayerHookDispatch(this);
        World = new WorldHookDispatch(this);
        Unit = new UnitHookDispatch(this);
        Item = new ItemHookDispatch(this);
        Spell = new SpellHookDispatch(this);
        Group = new GroupHookDispatch(this);
        Guild = new GuildHookDispatch(this);
        Battleground = new BattlegroundHookDispatch(this);
    }

    /// <summary>Where a throwing hook is reported. The world sets its own logger.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    public PlayerHookDispatch Player { get; }

    public WorldHookDispatch World { get; }

    public UnitHookDispatch Unit { get; }

    public ItemHookDispatch Item { get; }

    public SpellHookDispatch Spell { get; }

    public GroupHookDispatch Group { get; }

    public GuildHookDispatch Guild { get; }

    public BattlegroundHookDispatch Battleground { get; }

    /// <summary>Every registered hook object, in registration order.</summary>
    public IReadOnlyList<IScriptHooks> Registered => _registered;

    /// <summary>Whether <see cref="Freeze"/> ran: registration is closed.</summary>
    public bool IsFrozen => _frozen;

    /// <summary>
    /// Add <paramref name="hooks"/> to every hook its class overrides. A hook object that overrides nothing is refused (it would never run,
    /// which is almost always a signature typo); registering the same object twice is refused too. Before the world thread starts only.
    /// </summary>
    public void Register(IScriptHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        if (_frozen)
        {
            throw new InvalidOperationException("script hooks are registered before the world starts");
        }

        if (_registered.Any(h => ReferenceEquals(h, hooks)))
        {
            throw new InvalidOperationException($"{hooks.GetType().FullName} is already registered");
        }

        int added = AddToDispatch(hooks);
        if (added == 0)
        {
            throw new ArgumentException($"{hooks.GetType().FullName} overrides no script hook", nameof(hooks));
        }

        _registered.Add(hooks);
    }

    private int AddToDispatch(IScriptHooks hooks)
    {
        Type type = hooks.GetType();
        int added = 0;
        if (hooks is IPlayerHooks p) added += Player.Add(p, Overrides(type, typeof(IPlayerHooks)));
        if (hooks is IWorldHooks w) added += World.Add(w, Overrides(type, typeof(IWorldHooks)));
        if (hooks is IUnitHooks u) added += Unit.Add(u, Overrides(type, typeof(IUnitHooks)));
        if (hooks is IItemHooks i) added += Item.Add(i, Overrides(type, typeof(IItemHooks)));
        if (hooks is ISpellHooks s) added += Spell.Add(s, Overrides(type, typeof(ISpellHooks)));
        if (hooks is IGroupHooks g) added += Group.Add(g, Overrides(type, typeof(IGroupHooks)));
        if (hooks is IGuildHooks gu) added += Guild.Add(gu, Overrides(type, typeof(IGuildHooks)));
        if (hooks is IBattlegroundHooks b) added += Battleground.Add(b, Overrides(type, typeof(IBattlegroundHooks)));
        return added;
    }

    /// <summary>
    /// Whether <see cref="CommitSwap"/> would work, checked without changing anything: every hook object to remove is registered, every one to add
    /// overrides at least one hook, is not registered yet (unless it is also being removed) and is not listed twice. Null when it would; else why not.
    /// </summary>
    public string? ValidateSwap(IReadOnlyCollection<IScriptHooks> remove, IReadOnlyCollection<IScriptHooks> add)
    {
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(add);
        foreach (IScriptHooks hooks in remove)
        {
            if (!_registered.Any(h => ReferenceEquals(h, hooks)))
            {
                return $"script hook object {hooks.GetType().FullName} to remove is not registered";
            }
        }

        var seen = new HashSet<IScriptHooks>(ReferenceEqualityComparer.Instance);
        foreach (IScriptHooks hooks in add)
        {
            if (!seen.Add(hooks) || _registered.Any(h => ReferenceEquals(h, hooks)))
            {
                return $"{hooks.GetType().FullName} is already registered";
            }

            if (OverriddenHookCount(hooks) == 0)
            {
                return $"{hooks.GetType().FullName} overrides no script hook";
            }

            if (hooks is IWorldHooks && Overrides(hooks.GetType(), typeof(IWorldHooks)).Contains(nameof(IWorldHooks.OnStartup)))
            {
                return $"{hooks.GetType().FullName} overrides IWorldHooks.OnStartup, which only runs once while the world starts and so can never run for a hot module";
            }
        }

        return null;
    }

    private static int OverriddenHookCount(IScriptHooks hooks)
    {
        Type type = hooks.GetType();
        int count = 0;
        foreach (Type iface in HookInterfaces.Where(i => i.IsAssignableFrom(type)))
        {
            count += Overrides(type, iface).Count;
        }

        return count;
    }

    private static readonly Type[] HookInterfaces =
        [typeof(IPlayerHooks), typeof(IWorldHooks), typeof(IUnitHooks), typeof(IItemHooks), typeof(ISpellHooks), typeof(IGroupHooks), typeof(IGuildHooks), typeof(IBattlegroundHooks)];

    /// <summary>
    /// Remove <paramref name="remove"/> and add <paramref name="add"/> in one step, even after <see cref="Freeze"/> (the hot module lane; world thread
    /// at a tick boundary, so no dispatch is running). Every dispatch array is rebuilt from the hook objects that stay, in their registration order,
    /// followed by the new ones; after it the removed objects are referenced by nothing here. Call <see cref="ValidateSwap"/> first: this does not throw
    /// for a bad argument.
    /// </summary>
    public void CommitSwap(IReadOnlyCollection<IScriptHooks> remove, IReadOnlyCollection<IScriptHooks> add)
    {
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(add);
        List<IScriptHooks> keep = [.. _registered.Where(h => !remove.Any(r => ReferenceEquals(r, h))), .. add];

        // Build the complete new state on scratch dispatches first: anything that throws does so before the live arrays are touched.
        var scratch = new ScriptHookRegistry();
        foreach (IScriptHooks hooks in keep)
        {
            _ = scratch.AddToDispatch(hooks);
        }

        (object Live, object Fresh)[] pairs =
            [(Player, scratch.Player), (World, scratch.World), (Unit, scratch.Unit), (Item, scratch.Item), (Spell, scratch.Spell), (Group, scratch.Group), (Guild, scratch.Guild), (Battleground, scratch.Battleground)];
        foreach ((object live, object fresh) in pairs)
        {
            foreach (FieldInfo field in live.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Where(f => f.FieldType.IsArray))
            {
                field.SetValue(live, field.GetValue(fresh)); // publishes a finished array: never an emptied one
            }
        }

        _registered.Clear();
        _registered.AddRange(keep);
        RaiseHooksChanged();
    }

    /// <summary>
    /// Raised at the end of <see cref="CommitSwap"/> (world thread, tick boundary) so a bridge that subscribes to game events only while some hook
    /// needs it can subscribe now. A handler that throws is logged and does not undo the swap.
    /// </summary>
    public event Action? HooksChanged;

    private void RaiseHooksChanged()
    {
        if (HooksChanged is not { } handlers)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                string subscriber = handler.Target?.GetType().FullName ?? handler.Method.DeclaringType?.FullName ?? "Action";
                if (Logger is not null)
                {
                    Logger.LogFault("script.hook", ex, "script hook {Subscriber}.{Method} failed (subject={Subject})", subscriber, nameof(HooksChanged), "HooksChanged");
                }
                else
                {
                    FaultLedger.Shared.Record("script.hook", ex);
                }
            }
        }
    }

    /// <summary>Close registration (the world thread is starting).</summary>
    public void Freeze() => _frozen = true;

    /// <summary>The names of the interface methods <paramref name="type"/> implements itself rather than through the empty default.</summary>
    internal static HashSet<string> Overrides(Type type, Type hookInterface)
    {
        InterfaceMapping map = type.GetInterfaceMap(hookInterface);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int n = 0; n < map.InterfaceMethods.Length; n++)
        {
            if (map.TargetMethods[n].DeclaringType != hookInterface)
            {
                names.Add(map.InterfaceMethods[n].Name);
            }
        }

        return names;
    }

    internal void Failed(Exception ex, object hook, string name, string subject = "world")
    {
        string subscriber = hook.GetType().FullName ?? hook.GetType().Name;
        if (Logger is not null)
        {
            Logger.LogFault("script.hook", ex, "script hook {Hook}.{Method} failed (subscriber={Subscriber}, subject={Subject})", hook.GetType().Name, name, subscriber, subject);
        }
        else
        {
            FaultLedger.Shared.Record("script.hook", ex);
        }
    }

    internal static string DescribePlayer(Player? player)
        => player is null ? "player=null" : $"player={player.Guid} (name={player.Name}, level={player.Level}, map={player.MapId})";

    internal static string DescribeUnit(Unit? unit)
        => unit switch
        {
            null => "unit=null",
            Player p => DescribePlayer(p),
            Creature c => $"creature={c.Guid} (entry={c.Entry}, name={c.Template.Name}, map={c.MapId})",
            _ => $"unit={unit.Guid} (entry={unit.Guid.Entry}, map={unit.MapId})",
        };

    internal static string DescribeItem(Item? item)
        => item is null ? "item=null" : $"item={item.Guid} (entry={item.Entry}, name={item.Template.Name})";

    internal static string DescribeCast(SpellCast? cast, bool completed)
        => cast is null
            ? $"cast=null (completed={completed})"
            : $"spell={cast.Spell.Id} ({cast.Spell.Name}), caster=({DescribeUnit(cast.Caster)}), target={cast.Targets.Unit}, completed={completed}";

    internal static string DescribeGroup(Group? group, ObjectGuid? member = null)
        => group is null
            ? "group=null"
            : member is { } m
                ? $"group={group.Id} (leader={group.LeaderGuid}, member={m})"
                : $"group={group.Id} (leader={group.LeaderGuid}, members={group.MemberCount})";

    internal static string DescribeGuild(Guild? guild, uint characterId)
        => guild is null ? $"guild=null (characterId={characterId})" : $"guild={guild.Id} (name={guild.Name}, characterId={characterId})";

    internal static string DescribeBattleground(Battleground? bg, Team? winner = null)
        => bg is null
            ? "battleground=null"
            : winner is { } w
                ? $"battleground={bg.InstanceId} (type={bg.Type}, map={bg.MapId}, winner={w})"
                : $"battleground={bg.InstanceId} (type={bg.Type}, map={bg.MapId})";

    /// <summary>Append <paramref name="hook"/> to <paramref name="slot"/> when its class overrides <paramref name="name"/>; 1 when it did.</summary>
    internal static int AddIf<T>(ref T[] slot, T hook, HashSet<string> overrides, string name)
    {
        if (!overrides.Contains(name))
        {
            return 0;
        }

        slot = [.. slot, hook];
        return 1;
    }
}

/// <summary>Dispatch of <see cref="IPlayerHooks"/>.</summary>
public sealed class PlayerHookDispatch(ScriptHookRegistry owner)
{
    private IPlayerHooks[] _login = [], _logout = [], _level = [], _kill = [], _killed = [], _chat = [], _addon = [], _duelStart = [], _duelEnd = [];

    internal int Add(IPlayerHooks h, HashSet<string> o)
        => ScriptHookRegistry.AddIf(ref _login, h, o, nameof(IPlayerHooks.OnLogin))
            + ScriptHookRegistry.AddIf(ref _logout, h, o, nameof(IPlayerHooks.OnLogout))
            + ScriptHookRegistry.AddIf(ref _level, h, o, nameof(IPlayerHooks.OnLevelChanged))
            + ScriptHookRegistry.AddIf(ref _kill, h, o, nameof(IPlayerHooks.OnKill))
            + ScriptHookRegistry.AddIf(ref _killed, h, o, nameof(IPlayerHooks.OnKilled))
            + ScriptHookRegistry.AddIf(ref _chat, h, o, nameof(IPlayerHooks.OnChat))
            + ScriptHookRegistry.AddIf(ref _addon, h, o, nameof(IPlayerHooks.OnAddonMessage))
            + ScriptHookRegistry.AddIf(ref _duelStart, h, o, nameof(IPlayerHooks.OnDuelStart))
            + ScriptHookRegistry.AddIf(ref _duelEnd, h, o, nameof(IPlayerHooks.OnDuelEnd));

    public bool HasLogin => _login.Length != 0;

    public bool HasLogout => _logout.Length != 0;

    public bool HasChat => _chat.Length != 0;

    public bool HasAddonMessage => _addon.Length != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnLogin(Player player)
    {
        if (_login.Length != 0) Login(player);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnLogout(Player player)
    {
        if (_logout.Length != 0) Logout(player);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnLevelChanged(Player player, byte oldLevel)
    {
        if (_level.Length != 0) Level(player, oldLevel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnKill(Player killer, Unit victim)
    {
        if (_kill.Length != 0) Kill(killer, victim);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnKilled(Player victim, Unit? killer)
    {
        if (_killed.Length != 0) Killed(victim, killer);
    }

    /// <summary>False when a hook dropped the line.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool OnChat(Player player, ScriptChatMessage message) => _chat.Length == 0 || Chat(player, message);

    /// <summary>False when a hook dropped the addon line.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool OnAddonMessage(Player player, ScriptAddonMessage message) => _addon.Length == 0 || AddonMessage(player, message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnDuelStart(Player first, Player second)
    {
        if (_duelStart.Length != 0) DuelStart(first, second);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnDuelEnd(Player winner, Player loser, DuelCompleteType type)
    {
        if (_duelEnd.Length != 0) DuelEnd(winner, loser, type);
    }

    private void Login(Player player)
    {
        foreach (IPlayerHooks h in _login)
        {
            try { h.OnLogin(player); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IPlayerHooks.OnLogin), ScriptHookRegistry.DescribePlayer(player)); }
        }
    }

    private void Logout(Player player)
    {
        foreach (IPlayerHooks h in _logout)
        {
            try { h.OnLogout(player); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IPlayerHooks.OnLogout), ScriptHookRegistry.DescribePlayer(player)); }
        }
    }

    private void Level(Player player, byte oldLevel)
    {
        foreach (IPlayerHooks h in _level)
        {
            try { h.OnLevelChanged(player, oldLevel); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IPlayerHooks.OnLevelChanged), $"{ScriptHookRegistry.DescribePlayer(player)}, oldLevel={oldLevel}"); }
        }
    }

    private void Kill(Player killer, Unit victim)
    {
        foreach (IPlayerHooks h in _kill)
        {
            try { h.OnKill(killer, victim); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IPlayerHooks.OnKill), $"killer=({ScriptHookRegistry.DescribePlayer(killer)}), victim=({ScriptHookRegistry.DescribeUnit(victim)})"); }
        }
    }

    private void Killed(Player victim, Unit? killer)
    {
        foreach (IPlayerHooks h in _killed)
        {
            try { h.OnKilled(victim, killer); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IPlayerHooks.OnKilled), $"victim=({ScriptHookRegistry.DescribePlayer(victim)}), killer=({ScriptHookRegistry.DescribeUnit(killer)})"); }
        }
    }

    private bool Chat(Player player, ScriptChatMessage message)
    {
        // Every hook sees the line (AzerothCore asks each CanUseChat script); any false drops it.
        bool allowed = true;
        foreach (IPlayerHooks h in _chat)
        {
            try { allowed &= h.OnChat(player, message); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IPlayerHooks.OnChat), $"{ScriptHookRegistry.DescribePlayer(player)}, chat={message}"); }
        }

        return allowed;
    }

    private bool AddonMessage(Player player, ScriptAddonMessage message)
    {
        // Every hook sees the line; any false drops it.
        bool allowed = true;
        foreach (IPlayerHooks h in _addon)
        {
            try { allowed &= h.OnAddonMessage(player, message); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IPlayerHooks.OnAddonMessage), $"{ScriptHookRegistry.DescribePlayer(player)}, prefix={message.Prefix}"); }
        }

        return allowed;
    }

    private void DuelStart(Player first, Player second)
    {
        foreach (IPlayerHooks h in _duelStart)
        {
            try { h.OnDuelStart(first, second); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IPlayerHooks.OnDuelStart), $"first=({ScriptHookRegistry.DescribePlayer(first)}), second=({ScriptHookRegistry.DescribePlayer(second)})"); }
        }
    }

    private void DuelEnd(Player winner, Player loser, DuelCompleteType type)
    {
        foreach (IPlayerHooks h in _duelEnd)
        {
            try { h.OnDuelEnd(winner, loser, type); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IPlayerHooks.OnDuelEnd), $"winner=({ScriptHookRegistry.DescribePlayer(winner)}), loser=({ScriptHookRegistry.DescribePlayer(loser)}), type={type}"); }
        }
    }
}

/// <summary>Dispatch of <see cref="IWorldHooks"/>.</summary>
public sealed class WorldHookDispatch(ScriptHookRegistry owner)
{
    private IWorldHooks[] _startup = [], _update = [], _reload = [];

    internal int Add(IWorldHooks h, HashSet<string> o)
        => ScriptHookRegistry.AddIf(ref _startup, h, o, nameof(IWorldHooks.OnStartup))
            + ScriptHookRegistry.AddIf(ref _update, h, o, nameof(IWorldHooks.OnUpdate))
            + ScriptHookRegistry.AddIf(ref _reload, h, o, nameof(IWorldHooks.OnConfigReload));

    public void OnStartup()
    {
        foreach (IWorldHooks h in _startup)
        {
            try { h.OnStartup(); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IWorldHooks.OnStartup), "world:startup"); }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnUpdate(uint diffMs)
    {
        if (_update.Length != 0) Update(diffMs);
    }

    public void OnConfigReload()
    {
        foreach (IWorldHooks h in _reload)
        {
            try { h.OnConfigReload(); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IWorldHooks.OnConfigReload), "world:config-reload"); }
        }
    }

    private void Update(uint diffMs)
    {
        foreach (IWorldHooks h in _update)
        {
            try { h.OnUpdate(diffMs); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IWorldHooks.OnUpdate), $"world:update(diffMs={diffMs})"); }
        }
    }
}

/// <summary>Dispatch of <see cref="IUnitHooks"/>.</summary>
public sealed class UnitHookDispatch(ScriptHookRegistry owner)
{
    private IUnitHooks[] _damage = [], _death = [];

    internal int Add(IUnitHooks h, HashSet<string> o)
        => ScriptHookRegistry.AddIf(ref _damage, h, o, nameof(IUnitHooks.OnDamage))
            + ScriptHookRegistry.AddIf(ref _death, h, o, nameof(IUnitHooks.OnDeath));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint OnDamage(Unit attacker, Unit victim, uint damage) => _damage.Length == 0 ? damage : Damage(attacker, victim, damage);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnDeath(Unit victim, Unit? killer)
    {
        if (_death.Length != 0) Death(victim, killer);
    }

    private uint Damage(Unit attacker, Unit victim, uint damage)
    {
        foreach (IUnitHooks h in _damage)
        {
            uint before = damage;
            try { h.OnDamage(attacker, victim, ref damage); } catch (Exception ex) when (ex is not OutOfMemoryException) { damage = before; owner.Failed(ex, h, nameof(IUnitHooks.OnDamage), $"attacker=({ScriptHookRegistry.DescribeUnit(attacker)}), victim=({ScriptHookRegistry.DescribeUnit(victim)}), damage={before}"); }
        }

        return damage;
    }

    private void Death(Unit victim, Unit? killer)
    {
        foreach (IUnitHooks h in _death)
        {
            try { h.OnDeath(victim, killer); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IUnitHooks.OnDeath), $"victim=({ScriptHookRegistry.DescribeUnit(victim)}), killer=({ScriptHookRegistry.DescribeUnit(killer)})"); }
        }
    }
}

/// <summary>Dispatch of <see cref="IItemHooks"/>.</summary>
public sealed class ItemHookDispatch(ScriptHookRegistry owner)
{
    private IItemHooks[] _use = [], _equip = [], _unequip = [];

    internal int Add(IItemHooks h, HashSet<string> o)
        => ScriptHookRegistry.AddIf(ref _use, h, o, nameof(IItemHooks.OnUse))
            + ScriptHookRegistry.AddIf(ref _equip, h, o, nameof(IItemHooks.OnEquip))
            + ScriptHookRegistry.AddIf(ref _unequip, h, o, nameof(IItemHooks.OnUnequip));

    /// <summary>Whether any equip or unequip hook exists (the world bridge subscribes to inventories only then).</summary>
    public bool HasEquipHooks => _equip.Length != 0 || _unequip.Length != 0;

    /// <summary>True when a hook handled the use: the caller stops (AzerothCore <c>OnItemUse</c> returning true).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool OnUse(Player player, Item item, SpellCastTargets targets) => _use.Length != 0 && Use(player, item, targets);

    public void OnEquip(Player player, Item item, byte slot)
    {
        foreach (IItemHooks h in _equip)
        {
            try { h.OnEquip(player, item, slot); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IItemHooks.OnEquip), $"{ScriptHookRegistry.DescribePlayer(player)}, {ScriptHookRegistry.DescribeItem(item)}, slot={slot}"); }
        }
    }

    public void OnUnequip(Player player, Item item, byte slot)
    {
        foreach (IItemHooks h in _unequip)
        {
            try { h.OnUnequip(player, item, slot); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IItemHooks.OnUnequip), $"{ScriptHookRegistry.DescribePlayer(player)}, {ScriptHookRegistry.DescribeItem(item)}, slot={slot}"); }
        }
    }

    private bool Use(Player player, Item item, SpellCastTargets targets)
    {
        foreach (IItemHooks h in _use)
        {
            try
            {
                if (h.OnUse(player, item, targets)) return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("scripting.item-hook", ex);
                owner.Failed(ex, h, nameof(IItemHooks.OnUse), $"{ScriptHookRegistry.DescribePlayer(player)}, {ScriptHookRegistry.DescribeItem(item)}");
            }
        }

        return false;
    }
}

/// <summary>Dispatch of <see cref="ISpellHooks"/>.</summary>
public sealed class SpellHookDispatch(ScriptHookRegistry owner)
{
    private ISpellHooks[] _finished = [];

    internal int Add(ISpellHooks h, HashSet<string> o) => ScriptHookRegistry.AddIf(ref _finished, h, o, nameof(ISpellHooks.OnCastFinished));

    /// <summary>Whether any spell hook exists (the world bridge observes the spell system only then).</summary>
    public bool HasHooks => _finished.Length != 0;

    public void OnCastFinished(SpellCast cast, bool completed)
    {
        foreach (ISpellHooks h in _finished)
        {
            try { h.OnCastFinished(cast, completed); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(ISpellHooks.OnCastFinished), ScriptHookRegistry.DescribeCast(cast, completed)); }
        }
    }
}

/// <summary>Dispatch of <see cref="IGroupHooks"/>.</summary>
public sealed class GroupHookDispatch(ScriptHookRegistry owner)
{
    private IGroupHooks[] _added = [], _removed = [], _disband = [];

    internal int Add(IGroupHooks h, HashSet<string> o)
        => ScriptHookRegistry.AddIf(ref _added, h, o, nameof(IGroupHooks.OnMemberAdded))
            + ScriptHookRegistry.AddIf(ref _removed, h, o, nameof(IGroupHooks.OnMemberRemoved))
            + ScriptHookRegistry.AddIf(ref _disband, h, o, nameof(IGroupHooks.OnDisband));

    public bool HasHooks => _added.Length != 0 || _removed.Length != 0 || _disband.Length != 0;

    public void OnMemberAdded(Group group, ObjectGuid member)
    {
        foreach (IGroupHooks h in _added)
        {
            try { h.OnMemberAdded(group, member); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IGroupHooks.OnMemberAdded), ScriptHookRegistry.DescribeGroup(group, member)); }
        }
    }

    public void OnMemberRemoved(Group group, ObjectGuid member)
    {
        foreach (IGroupHooks h in _removed)
        {
            try { h.OnMemberRemoved(group, member); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IGroupHooks.OnMemberRemoved), ScriptHookRegistry.DescribeGroup(group, member)); }
        }
    }

    public void OnDisband(Group group)
    {
        foreach (IGroupHooks h in _disband)
        {
            try { h.OnDisband(group); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IGroupHooks.OnDisband), ScriptHookRegistry.DescribeGroup(group)); }
        }
    }
}

/// <summary>Dispatch of <see cref="IGuildHooks"/>.</summary>
public sealed class GuildHookDispatch(ScriptHookRegistry owner)
{
    private IGuildHooks[] _added = [], _removed = [];

    internal int Add(IGuildHooks h, HashSet<string> o)
        => ScriptHookRegistry.AddIf(ref _added, h, o, nameof(IGuildHooks.OnMemberAdded))
            + ScriptHookRegistry.AddIf(ref _removed, h, o, nameof(IGuildHooks.OnMemberRemoved));

    public bool HasHooks => _added.Length != 0 || _removed.Length != 0;

    public void OnMemberAdded(Guild guild, uint characterId)
    {
        foreach (IGuildHooks h in _added)
        {
            try { h.OnMemberAdded(guild, characterId); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IGuildHooks.OnMemberAdded), ScriptHookRegistry.DescribeGuild(guild, characterId)); }
        }
    }

    public void OnMemberRemoved(Guild guild, uint characterId)
    {
        foreach (IGuildHooks h in _removed)
        {
            try { h.OnMemberRemoved(guild, characterId); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IGuildHooks.OnMemberRemoved), ScriptHookRegistry.DescribeGuild(guild, characterId)); }
        }
    }
}

/// <summary>Dispatch of <see cref="IBattlegroundHooks"/>.</summary>
public sealed class BattlegroundHookDispatch(ScriptHookRegistry owner)
{
    private IBattlegroundHooks[] _start = [], _end = [];

    internal int Add(IBattlegroundHooks h, HashSet<string> o)
        => ScriptHookRegistry.AddIf(ref _start, h, o, nameof(IBattlegroundHooks.OnStart))
            + ScriptHookRegistry.AddIf(ref _end, h, o, nameof(IBattlegroundHooks.OnEnd));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnStart(Battleground battleground)
    {
        if (_start.Length != 0) Start(battleground);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnEnd(Battleground battleground, Team? winner)
    {
        if (_end.Length != 0) End(battleground, winner);
    }

    private void Start(Battleground battleground)
    {
        foreach (IBattlegroundHooks h in _start)
        {
            try { h.OnStart(battleground); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IBattlegroundHooks.OnStart), ScriptHookRegistry.DescribeBattleground(battleground)); }
        }
    }

    private void End(Battleground battleground, Team? winner)
    {
        foreach (IBattlegroundHooks h in _end)
        {
            try { h.OnEnd(battleground, winner); } catch (Exception ex) when (ex is not OutOfMemoryException) { owner.Failed(ex, h, nameof(IBattlegroundHooks.OnEnd), ScriptHookRegistry.DescribeBattleground(battleground, winner)); }
        }
    }
}
