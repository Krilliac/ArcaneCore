using System.Reflection;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Scripting;

/// <summary>
/// The global script hooks of one world (AzerothCore <c>ScriptMgr</c> with its per-hook enabled lists, ScriptMgr.h / ScriptDefines/*).
/// <see cref="Register"/> adds a hook object to each hook whose method its class overrides; every hook keeps its own array, so a dispatch
/// with nothing registered is a field load and a length check and allocates nothing. Registration happens before the world thread starts
/// (<see cref="Maps.WorldRuntime.Start"/> freezes it); dispatch is world thread only. A throwing hook is logged and the others still run.
/// </summary>
public sealed class ScriptHookRegistry
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
        if (added == 0)
        {
            throw new ArgumentException($"{type.FullName} overrides no script hook", nameof(hooks));
        }

        _registered.Add(hooks);
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

    internal void Failed(Exception ex, object hook, string name)
        => Logger.LogError(ex, "script hook {Hook}.{Method} failed", hook.GetType().Name, name);

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
            try { h.OnLogin(player); } catch (Exception ex) { owner.Failed(ex, h, nameof(IPlayerHooks.OnLogin)); }
        }
    }

    private void Logout(Player player)
    {
        foreach (IPlayerHooks h in _logout)
        {
            try { h.OnLogout(player); } catch (Exception ex) { owner.Failed(ex, h, nameof(IPlayerHooks.OnLogout)); }
        }
    }

    private void Level(Player player, byte oldLevel)
    {
        foreach (IPlayerHooks h in _level)
        {
            try { h.OnLevelChanged(player, oldLevel); } catch (Exception ex) { owner.Failed(ex, h, nameof(IPlayerHooks.OnLevelChanged)); }
        }
    }

    private void Kill(Player killer, Unit victim)
    {
        foreach (IPlayerHooks h in _kill)
        {
            try { h.OnKill(killer, victim); } catch (Exception ex) { owner.Failed(ex, h, nameof(IPlayerHooks.OnKill)); }
        }
    }

    private void Killed(Player victim, Unit? killer)
    {
        foreach (IPlayerHooks h in _killed)
        {
            try { h.OnKilled(victim, killer); } catch (Exception ex) { owner.Failed(ex, h, nameof(IPlayerHooks.OnKilled)); }
        }
    }

    private bool Chat(Player player, ScriptChatMessage message)
    {
        // Every hook sees the line (AzerothCore asks each CanUseChat script); any false drops it.
        bool allowed = true;
        foreach (IPlayerHooks h in _chat)
        {
            try { allowed &= h.OnChat(player, message); } catch (Exception ex) { owner.Failed(ex, h, nameof(IPlayerHooks.OnChat)); }
        }

        return allowed;
    }

    private bool AddonMessage(Player player, ScriptAddonMessage message)
    {
        // Every hook sees the line; any false drops it.
        bool allowed = true;
        foreach (IPlayerHooks h in _addon)
        {
            try { allowed &= h.OnAddonMessage(player, message); } catch (Exception ex) { owner.Failed(ex, h, nameof(IPlayerHooks.OnAddonMessage)); }
        }

        return allowed;
    }

    private void DuelStart(Player first, Player second)
    {
        foreach (IPlayerHooks h in _duelStart)
        {
            try { h.OnDuelStart(first, second); } catch (Exception ex) { owner.Failed(ex, h, nameof(IPlayerHooks.OnDuelStart)); }
        }
    }

    private void DuelEnd(Player winner, Player loser, DuelCompleteType type)
    {
        foreach (IPlayerHooks h in _duelEnd)
        {
            try { h.OnDuelEnd(winner, loser, type); } catch (Exception ex) { owner.Failed(ex, h, nameof(IPlayerHooks.OnDuelEnd)); }
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
            try { h.OnStartup(); } catch (Exception ex) { owner.Failed(ex, h, nameof(IWorldHooks.OnStartup)); }
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
            try { h.OnConfigReload(); } catch (Exception ex) { owner.Failed(ex, h, nameof(IWorldHooks.OnConfigReload)); }
        }
    }

    private void Update(uint diffMs)
    {
        foreach (IWorldHooks h in _update)
        {
            try { h.OnUpdate(diffMs); } catch (Exception ex) { owner.Failed(ex, h, nameof(IWorldHooks.OnUpdate)); }
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
            try { h.OnDamage(attacker, victim, ref damage); } catch (Exception ex) { damage = before; owner.Failed(ex, h, nameof(IUnitHooks.OnDamage)); }
        }

        return damage;
    }

    private void Death(Unit victim, Unit? killer)
    {
        foreach (IUnitHooks h in _death)
        {
            try { h.OnDeath(victim, killer); } catch (Exception ex) { owner.Failed(ex, h, nameof(IUnitHooks.OnDeath)); }
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
            try { h.OnEquip(player, item, slot); } catch (Exception ex) { owner.Failed(ex, h, nameof(IItemHooks.OnEquip)); }
        }
    }

    public void OnUnequip(Player player, Item item, byte slot)
    {
        foreach (IItemHooks h in _unequip)
        {
            try { h.OnUnequip(player, item, slot); } catch (Exception ex) { owner.Failed(ex, h, nameof(IItemHooks.OnUnequip)); }
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
            catch (Exception ex)
            {
                owner.Failed(ex, h, nameof(IItemHooks.OnUse));
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
            try { h.OnCastFinished(cast, completed); } catch (Exception ex) { owner.Failed(ex, h, nameof(ISpellHooks.OnCastFinished)); }
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
            try { h.OnMemberAdded(group, member); } catch (Exception ex) { owner.Failed(ex, h, nameof(IGroupHooks.OnMemberAdded)); }
        }
    }

    public void OnMemberRemoved(Group group, ObjectGuid member)
    {
        foreach (IGroupHooks h in _removed)
        {
            try { h.OnMemberRemoved(group, member); } catch (Exception ex) { owner.Failed(ex, h, nameof(IGroupHooks.OnMemberRemoved)); }
        }
    }

    public void OnDisband(Group group)
    {
        foreach (IGroupHooks h in _disband)
        {
            try { h.OnDisband(group); } catch (Exception ex) { owner.Failed(ex, h, nameof(IGroupHooks.OnDisband)); }
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
            try { h.OnMemberAdded(guild, characterId); } catch (Exception ex) { owner.Failed(ex, h, nameof(IGuildHooks.OnMemberAdded)); }
        }
    }

    public void OnMemberRemoved(Guild guild, uint characterId)
    {
        foreach (IGuildHooks h in _removed)
        {
            try { h.OnMemberRemoved(guild, characterId); } catch (Exception ex) { owner.Failed(ex, h, nameof(IGuildHooks.OnMemberRemoved)); }
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
            try { h.OnStart(battleground); } catch (Exception ex) { owner.Failed(ex, h, nameof(IBattlegroundHooks.OnStart)); }
        }
    }

    private void End(Battleground battleground, Team? winner)
    {
        foreach (IBattlegroundHooks h in _end)
        {
            try { h.OnEnd(battleground, winner); } catch (Exception ex) { owner.Failed(ex, h, nameof(IBattlegroundHooks.OnEnd)); }
        }
    }
}
