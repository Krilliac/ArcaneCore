using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Graveyards;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>
/// Battlegrounds in the world daemon (docs/areas/battlegrounds.md, the "lifecycle" slice): owns the <see cref="BattlegroundManager"/>, its
/// templates (the <c>battleground_template</c> rows, or the classic-db rows built in when the table is empty) and the per-match maps; drives
/// it from the world tick; wires the matches into the world: the battleground map resolver (one map instance per match), the teleport gate,
/// the spawn gate of the <c>creature_battleground</c>/<c>gameobject_battleground</c> events, the flag stand, dropped flag and banner game
/// objects, the battleground buff traps, the area triggers, the kills, the graveyards, Waiting to Resurrect on release, the flag drop on every
/// vmangos trigger (aura removal, positive invulnerability, death, leaving, a far teleport), the initial world states and the queue packets
/// (<see cref="BattlegroundHandlers"/>). The <c>Battleground</c> configuration section binds <see cref="BattlegroundOptions"/>.
/// <para>Thread affinity: everything below runs on the world thread except <see cref="Attach"/> and the character hooks.</para>
/// </summary>
public sealed partial class BattlegroundFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers)
    : IWorldFeature, IAreaTriggerListener, ICharacterHooks
{
    private readonly ILogger _logger = loggers.CreateLogger<BattlegroundFeature>();
    private readonly Dictionary<uint, MatchRuntime> _matches = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ObjectGuid, BattlegroundEntryPoint> _entryPoints = new();
    private BattlegroundEventIndexMap _events = BattlegroundEventIndexMap.Empty;
    private BattlegroundContent _content = BattlegroundContent.Empty;
    private readonly System.Threading.Channels.Channel<Func<IBattlegroundEntryPointStore, Task>> _writes =
        System.Threading.Channels.Channel.CreateUnbounded<Func<IBattlegroundEntryPointStore, Task>>(new() { SingleReader = true });
    private BattlegroundManager? _manager;
    private WorldRuntime? _world;
    private Task? _writer;

    /// <summary>The bound options (section <see cref="BattlegroundOptions.SectionName"/>).</summary>
    public BattlegroundOptions Options { get; } = new();

    /// <summary>The battleground manager (world thread; after <see cref="Attach"/>).</summary>
    public BattlegroundManager Manager => _manager ?? throw new InvalidOperationException("the battleground feature is not attached");

    /// <summary>The world (after <see cref="Attach"/>).</summary>
    internal WorldRuntime World => _world ?? throw new InvalidOperationException("the battleground feature is not attached");

    internal IServiceProvider Services => services;

    internal ILogger Logger => _logger;

    /// <summary>The event rows of the battleground spawns.</summary>
    internal BattlegroundEventIndexMap Events => _events;

    /// <summary>The battlemaster creature entries and their type (vmangos <c>battlemaster_entry</c>).</summary>
    internal IReadOnlyDictionary<uint, BattlegroundType> Battlemasters { get; private set; } = new Dictionary<uint, BattlegroundType>();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        services.GetService<IConfiguration>()?.GetSection(BattlegroundOptions.SectionName).Bind(Options);
        foreach (string warning in Options.Normalize())
        {
            _logger.LogWarning("{Section}: {Warning}", BattlegroundOptions.SectionName, warning);
        }

        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IBattlegroundContentStore>() is { } store)
            {
                try
                {
                    _content = store.LoadAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "battleground content could not be read; the built-in templates are used and no spawn is event-gated");
                }
            }
        }

        _events = BattlegroundEventIndexMap.Build(_content);
        Battlemasters = _content.Battlemasters
            .Where(b => b.BattlegroundTypeId is >= 1 and <= 3)
            .GroupBy(b => b.CreatureEntry)
            .ToDictionary(g => g.Key, g => (BattlegroundType)g.First().BattlegroundTypeId);

        var host = new BattlegroundWorldHost(this);
        _manager = new BattlegroundManager(Options, host, host.BasePorts());
        _writer = Task.Run(WriteLoopAsync);

        // Everything that needs the other features (they attach in type-name order, after this one): the first world command.
        world.Post(Install);
        world.WorldTick += OnWorldTick;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        world.PlayerLoggedIn += OnPlayerLoggedIn;
        _logger.LogInformation(
            "battlegrounds: {Templates} template row(s), {Creatures} creature and {Objects} game object event row(s), {Masters} battlemaster(s)",
            _content.Templates.Count, _content.CreatureEvents.Count, _content.GameObjectEvents.Count, Battlemasters.Count);
    }

    // ------------------------------------------------------------------ installation (world thread, first command)

    private void Install()
    {
        WorldRuntime world = World;
        RegisterTemplates();

        var resolver = new BattlegroundMapResolver(this);
        if (world.MapResolver is InstanceManager instances)
        {
            instances.BattlegroundMaps = resolver;
        }
        else if (world.MapResolver is null)
        {
            world.MapResolver = resolver;
        }
        else
        {
            _logger.LogWarning("the world's map resolver is {Type}; battleground maps fall back to their shared copy", world.MapResolver.GetType().Name);
        }

        if (services.GetService<TeleportFeature>() is { } teleport)
        {
            teleport.Teleports.BattlegroundEntryAllowed = CanEnterMap;
        }

        var presence = new BattlegroundDeathPresence(this);
        if (!DeathSeams.Of(world).TryRegisterBattlegrounds(presence))
        {
            _logger.LogWarning("battleground presence was already registered for this world; corpse reclaim and Waiting to Resurrect use the earlier one");
        }

        if (services.GetService<GraveyardFeature>()?.Service is { } graveyards)
        {
            graveyards.AddOverride(new BattlegroundGraveyards(this));
        }

        WorldStateHooks.For(world).WorldStates.Add(new BattlegroundWorldStates(this));

        if (services.GetService<SpellFeature>() is { } spells)
        {
            spells.System.HolderRemoved += OnAuraHolderRemoved;
            spells.System.HolderAdded += OnAuraHolderAdded;
        }

        InstallMatchHooks();
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        foreach (Map map in world.Maps.ToArray())
        {
            OnMapCreated(map);
        }
    }

    /// <summary>
    /// The templates: the <c>battleground_template</c> rows, else the classic-db z2815 rows (the same values: AV 20-40 players, levels 51-60,
    /// starts 611/610; WS 5-10, 10-60, 769/770; AB 8-15, 20-60, 890/889). The mark spells are the 1.12 marks of honor (vmangos
    /// <c>BattleGroundMarks</c>, BattleGroundDefines.h:100-112) unless a row names others. A template whose start location is not a known
    /// WorldSafeLocs id is left out, as vmangos does (BattleGroundMgr::CreateInitialBattleGrounds).
    /// </summary>
    private void RegisterTemplates()
    {
        IReadOnlyList<BattlegroundTemplateRecord> rows = _content.Templates.Count > 0
            ? _content.Templates
            :
            [
                new BattlegroundTemplateRecord(1, 20, 40, 51, 60, 611, 610, 100, 1),
                new BattlegroundTemplateRecord(2, 5, 10, 10, 60, 769, 770, 75, 0),
                new BattlegroundTemplateRecord(3, 8, 15, 20, 60, 890, 889, 75, 0),
            ];
        GraveyardCatalog catalog = WorldGraveyards.Of(World).Catalog;
        foreach (BattlegroundTemplateRecord row in rows)
        {
            var type = (BattlegroundType)row.Id;
            if (type is not (BattlegroundType.AlteracValley or BattlegroundType.WarsongGulch or BattlegroundType.ArathiBasin))
            {
                continue;
            }

            if (catalog.Find(row.AllianceStartLoc) is not { } alliance || catalog.Find(row.HordeStartLoc) is not { } horde)
            {
                _logger.LogWarning("battleground {Type}: start location {Alliance} or {Horde} is not a WorldSafeLocs id; the battleground is not available",
                    type, row.AllianceStartLoc, row.HordeStartLoc);
                continue;
            }

            (uint winMark, uint loseMark) = type switch
            {
                BattlegroundType.AlteracValley => (24955u, 24954u),
                BattlegroundType.WarsongGulch => (24951u, 24950u),
                _ => (24953u, 24952u),
            };
            Manager.RegisterTemplate(new BattlegroundTemplate
            {
                Type = type,
                MapId = BattlegroundManager.MapOfType(type),
                Name = type switch
                {
                    BattlegroundType.AlteracValley => "Alterac Valley",
                    BattlegroundType.WarsongGulch => "Warsong Gulch",
                    _ => "Arathi Basin",
                },
                MinPlayersPerTeam = row.MinPlayersPerTeam,
                MaxPlayersPerTeam = row.MaxPlayersPerTeam,
                MinLevel = row.MinLevel,
                MaxLevel = row.MaxLevel,
                AllianceWinSpell = row.AllianceWinSpell != 0 ? row.AllianceWinSpell : winMark,
                AllianceLoseSpell = row.AllianceLoseSpell != 0 ? row.AllianceLoseSpell : loseMark,
                HordeWinSpell = row.HordeWinSpell != 0 ? row.HordeWinSpell : winMark,
                HordeLoseSpell = row.HordeLoseSpell != 0 ? row.HordeLoseSpell : loseMark,
                AllianceStart = new BattlegroundStartLocation(alliance.X, alliance.Y, alliance.Z, alliance.Orientation),
                HordeStart = new BattlegroundStartLocation(horde.X, horde.Y, horde.Z, horde.Orientation),
                PlayerSkinRefLootId = row.PlayerSkinRefLootId,
            });
        }
    }

    // ------------------------------------------------------------------ the tick

    private void OnWorldTick(uint diffMs)
    {
        if (_manager is not { } manager)
        {
            return;
        }

        manager.Update(diffMs);
        foreach (MatchRuntime match in _matches.Values.ToArray())
        {
            match.Update(diffMs);
        }
    }

    // ------------------------------------------------------------------ matches and their maps

    /// <summary>The running match of an instance id, with its map, or null.</summary>
    internal MatchRuntime? FindMatch(uint instanceId) => _matches.GetValueOrDefault(instanceId);

    /// <summary>The match whose map is <paramref name="map"/>, or null.</summary>
    internal MatchRuntime? FindMatch(Map map)
        => map.InstanceId != 0 && _matches.TryGetValue(map.InstanceId, out MatchRuntime? match) && BattlegroundManager.MapOfType(match.Type) == map.MapId ? match : null;

    /// <summary>The match <paramref name="player"/> is bound to (vmangos <c>Player::GetBattleGround</c>), or null.</summary>
    public Battleground? BattlegroundOf(ObjectGuid player)
    {
        if (_manager is not { } manager)
        {
            return null;
        }

        BattlegroundPlayerState state = manager.StateOf(player);
        return state.InBattleground ? manager.GetBattleground(state.InstanceId, state.Type) : null;
    }

    internal MatchRuntime? MatchOf(ObjectGuid player) => BattlegroundOf(player) is { } bg ? FindMatch(bg.InstanceId) : null;

    /// <summary>The effects channel of a match about to be created (the manager asks before it builds the match).</summary>
    internal MatchRuntime CreateMatch(BattlegroundType type, uint instanceId)
    {
        var match = new MatchRuntime(this, type, instanceId);
        _matches[instanceId] = match;
        return match;
    }

    /// <summary>A match was created: its map instance (vmangos <c>MapManager::CreateBgMap</c>).</summary>
    internal void OnBattlegroundCreated(Battleground battleground)
    {
        if (!_matches.TryGetValue(battleground.InstanceId, out MatchRuntime? match))
        {
            match = CreateMatch(battleground.Type, battleground.InstanceId);
        }

        match.Battleground = battleground;
        Map map = World.GetMap(battleground.MapId, battleground.InstanceId);   // raises MapCreated, which installs the map side
        match.AttachMap(map);
    }

    /// <summary>A match was deleted: its map goes once it is empty (vmangos <c>~BattleGround</c>).</summary>
    internal void OnBattlegroundDeleted(Battleground battleground)
    {
        if (_matches.Remove(battleground.InstanceId, out MatchRuntime? match))
        {
            match.Detach();
            if (match.Map is { } map)
            {
                World.UnloadMap(map);
            }
        }
    }

    private void OnMapCreated(Map map)
    {
        if (FindMatch(map) is { } match)
        {
            match.AttachMap(map);
        }
    }

    private void OnMapUnloading(Map map)
    {
        if (FindMatch(map) is { } match && ReferenceEquals(match.Map, map))
        {
            match.Detach();
        }
    }

    // ------------------------------------------------------------------ entry points

    /// <summary>Where the player returns when it leaves its match (vmangos <c>m_bgData.joinPos</c>).</summary>
    public BattlegroundEntryPoint? EntryPointOf(ObjectGuid player) => _entryPoints.TryGetValue(player, out BattlegroundEntryPoint point) ? point : null;

    /// <summary>vmangos <c>SetBattleGroundEntryPoint</c>: the position of <paramref name="from"/> (the player itself or its group leader).</summary>
    internal void StoreEntryPoint(ObjectGuid member, Player from)
        => _entryPoints[member] = new BattlegroundEntryPoint(from.MapId, from.X, from.Y, from.Z, from.Orientation, from.ZoneId);

    internal void ForgetEntryPoint(ObjectGuid player) => _entryPoints.TryRemove(player, out _);

    /// <summary>Drop the in-memory entry points, as a restart does (tests).</summary>
    internal void DropEntryPointCache() => _entryPoints.Clear();

    /// <summary>
    /// The participant is in its match now: its <c>character_battleground_data</c> row is written (vmangos <c>_SaveBGData</c> once
    /// <c>bgInstanceID</c> is set), so a login after a restart still returns it to its entry point.
    /// </summary>
    private void PersistBinding(Player player, Battleground bg, Team team)
    {
        if (EntryPointOf(player.Guid) is not { } point)
        {
            return;
        }

        var record = new BattlegroundEntryPointRecord((int)player.Guid.Counter, bg.InstanceId, team == Team.Alliance ? 469u : 67u,
            point.MapId, point.X, point.Y, point.Z, point.Orientation);
        Enqueue(store => store.SaveAsync(record));
    }

    /// <summary>The participant left its match online: its row goes (vmangos <c>_SaveBGData</c> with no battleground deletes it).</summary>
    internal void DeleteBinding(ObjectGuid player)
    {
        int characterId = (int)player.Counter;
        Enqueue(store => store.DeleteAsync(characterId));
    }

    private void Enqueue(Func<IBattlegroundEntryPointStore, Task> write) => _writes.Writer.TryWrite(write);

    /// <summary>Wait until every row write queued so far was attempted.</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(_ => { done.TrySetResult(); return Task.CompletedTask; }))
        {
            done.TrySetResult();
        }

        return done.Task;
    }

    private async Task WriteLoopAsync()
    {
        await foreach (Func<IBattlegroundEntryPointStore, Task> write in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<IBattlegroundEntryPointStore>() is { } store)
                {
                    await write(store).ConfigureAwait(false);
                }
                else
                {
                    await write(NullEntryPointStore.Instance).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "a character_battleground_data write failed");
            }
        }
    }

    /// <summary>Drain the row writes at shutdown.</summary>
    public async Task StopAsync()
    {
        _writes.Writer.TryComplete();
        if (_writer is { } writer)
        {
            await writer.ConfigureAwait(false);
        }
    }

    private sealed class NullEntryPointStore : IBattlegroundEntryPointStore
    {
        public static NullEntryPointStore Instance { get; } = new();

        public Task<BattlegroundEntryPointRecord?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<BattlegroundEntryPointRecord?>(null);

        public Task SaveAsync(BattlegroundEntryPointRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // ------------------------------------------------------------------ the teleport gate and the far-teleport leave

    /// <summary>The teleport gate: a battleground map is open only to the players of its match (vmangos Player::TeleportTo, Player.cpp:1861-1864).</summary>
    private bool CanEnterMap(Player player, uint mapId) => BattlegroundOf(player.Guid) is { } bg && bg.MapId == mapId;

    /// <summary>
    /// A player arrived on a map. A player bound to a match who arrived anywhere but its map left the battleground (vmangos TeleportTo far,
    /// Player.cpp:2036-2043: <c>LeaveBattleground(false)</c>); one who arrived on its match's map enters the match (vmangos
    /// HandleMoveWorldportAckOpcode → <c>BattleGround::AddPlayer</c>).
    /// </summary>
    internal void OnPlayerEnteredMap(Player player, Map map)
    {
        if (BattlegroundOf(player.Guid) is not { } bg)
        {
            return;
        }

        if (bg.MapId != map.MapId || bg.InstanceId != map.InstanceId)
        {
            LeaveBattleground(player, teleportToEntryPoint: false);
            return;
        }

        if (bg.PlayerTeam(player.Guid) is null && Manager.EnterBattleground(player.Guid))
        {
            SendInitialWorldStates(player, map);
            PersistBinding(player, bg, bg.PlayerTeam(player.Guid) ?? player.Team);
        }
    }

    /// <summary>
    /// vmangos <c>Player::LeaveBattleground</c> (Player.cpp:18675-18702): Waiting to Resurrect goes, a deserter of a match that has not ended gets
    /// the Deserter debuff (unless a game master, or <see cref="BattlegroundOptions.CastDeserter"/> is off), then the match removes the player.
    /// </summary>
    internal void LeaveBattleground(Player player, bool teleportToEntryPoint)
    {
        if (BattlegroundOf(player.Guid) is not { } bg)
        {
            return;
        }

        SpellSystem? spells = services.GetService<SpellFeature>()?.System;
        spells?.RemoveAuras(player, BattlegroundConstants.SpellWaitingToResurrect);
        if (!player.IsGameMaster && Options.CastDeserter && bg.Status is BattlegroundStatus.InProgress or BattlegroundStatus.WaitJoin)
        {
            spells?.CastSpell(player, BattlegroundConstants.SpellDeserter, SpellCastTargets.ForSelf(), triggered: true);
        }

        bg.RemovePlayerAtLeave(player.Guid, teleportToEntryPoint, sendStatus: true);
    }

    // ------------------------------------------------------------------ logout and login

    private void OnPlayerLoggingOut(Player player)
    {
        if (_manager is not { } manager)
        {
            return;
        }

        // vmangos keeps an offline participant for MAX_OFFLINE_TIME; this removes it at once (offline: nothing of it is touched).
        if (BattlegroundOf(player.Guid) is { } bg)
        {
            bg.RemovePlayerAtLeave(player.Guid, teleportToEntryPoint: false, sendStatus: false, online: false);
            manager.ClearBinding(player.Guid);
        }

        manager.PlayerLoggedOut(player.Guid);
    }

    private void OnPlayerLoggedIn(Player player) => _manager?.PlayerLoggedIn(player.Guid, player.Level);

    /// <summary>
    /// vmangos Player::LoadFromDB (Player.cpp:14775-14788): a character saved on a battleground map is not in a match any more after a login,
    /// so it is moved to its entry point before it enters the world, or to its bind point when none is known. The loaded record is moved too:
    /// the login sends its map and position (SMSG_LOGIN_VERIFY_WORLD) and places the player there.
    /// </summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);
        if (BattlegroundManager.TypeOfMap(character.MapId) == BattlegroundType.None)
        {
            return;
        }

        // The binding row of this character, after the writes queued before the logout (Player::_LoadBGData).
        await FlushAsync().ConfigureAwait(false);
        BattlegroundEntryPointRecord? row = session.Services.GetService<IBattlegroundEntryPointStore>() is { } store
            ? await store.LoadAsync(character.Id).ConfigureAwait(false)
            : null;
        (uint map, uint zone, float x, float y, float z, float o) = EntryPointOf(player.Guid) is { } point
            ? (point.MapId, point.ZoneId, point.X, point.Y, point.Z, point.Orientation)
            : row is { } saved
                ? (saved.JoinMapId, 0u, saved.JoinX, saved.JoinY, saved.JoinZ, saved.JoinOrientation)
                : (character.HomeMapId, character.HomeZoneId, character.HomeX, character.HomeY, character.HomeZ, character.Orientation);
        character.MapId = map;
        character.ZoneId = zone;
        character.X = x;
        character.Y = y;
        character.Z = z;
        character.Orientation = o;
        player.MapId = map;
        player.Relocate(x, y, z, o, 0);

        // Not in a match any more: the binding is gone (the next save of vmangos deletes the row).
        ForgetEntryPoint(player.Guid);
        if (row is not null)
        {
            DeleteBinding(player.Guid);
        }
    }

    // ------------------------------------------------------------------ area triggers

    /// <summary>vmangos HandleAreaTriggerOpcode (MiscHandler.cpp:690-695): a participant's trigger goes to its battleground first.</summary>
    public void OnAreaTrigger(Player player, uint triggerId)
    {
        if (BattlegroundOf(player.Guid) is { } bg)
        {
            bg.HandleAreaTrigger(player.Guid, triggerId);
        }
    }

    // ------------------------------------------------------------------ auras: flag drops and invulnerability

    /// <summary>AURA_INTERRUPT_INVULNERABILITY_BUFF_CANCELS (vmangos SpellDefines.h:598): the flag auras carry it.</summary>
    internal const uint InvulnerabilityBuffCancels = 0x00200000;

    /// <summary>
    /// vmangos Aura::HandleAuraModEffectImmunity (SpellAuras.cpp:4066-4083): removing an aura that carries
    /// <see cref="InvulnerabilityBuffCancels"/> from a player who is not possessed drops what the player carries in its battleground.
    /// </summary>
    private void OnAuraHolderRemoved(SpellAuraHolder holder)
    {
        if (holder.Target is Player player && ((uint)holder.Spell.AuraInterruptFlags & InvulnerabilityBuffCancels) != 0
            && BattlegroundOf(player.Guid) is { } bg)
        {
            bg.EventPlayerDroppedFlag(player.Guid);
        }
    }

    /// <summary>
    /// vmangos Aura::HandleAuraModSchoolImmunity (SpellAuras.cpp:4112-4121) and HandleModUnattackable (SpellAuras.cpp:5689-5695): a positive
    /// school immunity (client patch 1.7.0: offensive immunities no longer drop the flag) or an unattackable aura on a player who is not charmed
    /// removes the auras that carry <see cref="InvulnerabilityBuffCancels"/> — the flag, which then drops.
    /// </summary>
    private void OnAuraHolderAdded(SpellAuraHolder holder)
    {
        if (holder.Target is not Player player || player.Map is null)
        {
            return;
        }

        bool schoolImmunity = holder.Spell.HasAura(AuraType.SchoolImmunity) && holder.Spell.IsPositive;
        if (!schoolImmunity && !holder.Spell.HasAura(AuraType.ModUnattackable))
        {
            return;
        }

        if (services.GetService<SpellFeature>()?.System is not { } spells)
        {
            return;
        }

        foreach (SpellAuraHolder other in spells.GetAuras(player).ToArray())
        {
            if (!ReferenceEquals(other, holder) && ((uint)other.Spell.AuraInterruptFlags & InvulnerabilityBuffCancels) != 0)
            {
                spells.RemoveAuras(player, other.Spell.Id);
            }
        }
    }

    // ------------------------------------------------------------------ world states

    /// <summary>SMSG_INIT_WORLD_STATES for a player who entered its match (vmangos sends them with the zone update).</summary>
    internal void SendInitialWorldStates(Player player, Map map)
    {
        uint zone = player.ZoneId;
        List<WorldStatePair> states = WorldStateHooks.For(World).WorldStates.Build(player, zone);
        player.Session.Send(Protocol.WorldOpcode.SmsgInitWorldStates, WorldStatePackets.BuildInit(map.MapId, zone, states));
    }
}

/// <summary>Where a participant came from and returns to (vmangos <c>BGData.joinPos</c>).</summary>
public readonly record struct BattlegroundEntryPoint(uint MapId, float X, float Y, float Z, float Orientation, uint ZoneId = 0);
