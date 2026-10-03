using System.Runtime.CompilerServices;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// The spell system in the world daemon (discovered <see cref="IWorldFeature"/>). On attach it
/// loads the spell tables and the spellbooks, builds the world-thread <see cref="SpellSystem"/>
/// and starts its tick; handlers reach it through
/// <c>session.Services.GetRequiredService&lt;SpellFeature&gt;()</c>.
/// <para>
/// The tick is a timer that posts <see cref="SpellSystem.Update"/> to the world thread every
/// world tick interval. This system serves all maps, so attaching the same update to each map
/// would advance its auras multiple times per world tick. At most one update is queued at a time.
/// </para>
/// <para>
/// Cooldowns and auras persist across logout (<see cref="SpellStatePersistence"/>,
/// docs/integration/spells-persistence.md): captured on the world thread when the player logs
/// out, loaded on the session task while the character loads, cooldowns restored before
/// SMSG_INITIAL_SPELLS and auras once the player is in the world.
/// </para>
/// </summary>
public sealed class SpellFeature : IWorldFeature, ICharacterHooks, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SpellFeature> _logger;
    private readonly TimeProvider _clock;
    private readonly object _stagedLock = new();
    private readonly ConditionalWeakTable<Player, SpellStateSnapshot> _staged = [];
    private readonly HashSet<MapCombat> _combatSubscriptions = new(ReferenceEqualityComparer.Instance);
    private WorldRuntime? _world;
    private Timer? _timer;
    private int _updateQueued;
    private uint _lastUpdateMs;

    public SpellFeature(IServiceScopeFactory scopes, ILogger<SpellFeature> logger, TimeProvider? timeProvider = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = timeProvider ?? TimeProvider.System;
        Spellbook = new SpellbookCache(scopes, logger);
        State = new SpellStatePersistence(scopes, logger);
        System = new SpellSystem(SpellStore.Empty, () => _world?.NowMs ?? 0, spellbook: Spellbook, logger: logger);
    }

    /// <summary>The world-thread spell system.</summary>
    public SpellSystem System { get; }

    /// <summary>Known spells of every character.</summary>
    public SpellbookCache Spellbook { get; }

    /// <summary>Cooldowns and auras saved across logout.</summary>
    public SpellStatePersistence State { get; }

    /// <summary>Wall clock (Unix ms) used for persisted cooldown ends and offline aura time.</summary>
    public long UnixNowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        System.Units = new WorldSpellUnitResolver();
        System.Damage = new WorldSpellDamageSink();

        // Attach runs once before the world thread starts, so blocking on the startup loads is
        // safe (WorldHost loads the character name cache the same way, just before this).
        using (IServiceScope scope = _scopes.CreateScope())
        {
            // Features attach alphabetically: TeleportFeature attaches after this feature.
            // Resolve its singleton now, but defer accessing its service until a spell lands.
            TeleportFeature teleports = scope.ServiceProvider.GetRequiredService<TeleportFeature>();

            // Seams owned by other areas: combat rules and groups (social area, attached later, so
            // its manager is resolved per query). Line of sight comes from map.Collision (vmap-los).
            System.CombatRules = scope.ServiceProvider.GetService<ISpellCombatRules>() ?? new VanillaSpellCombatRules();
            System.Summons = scope.ServiceProvider.GetService<ISpellSummonSink>();
            SocialFeature? social = scope.ServiceProvider.GetService<SocialFeature>();
            System.Groups = social is null ? NoGroupResolver.Instance : new WorldSpellGroups(() => social.Context.Groups);
            System.Teleports = new WorldSpellTeleportSink(() => teleports.Teleports);
            System.IsInTransit = unit => unit is Player player && world.IsOnline(player.Guid)
                && teleports.Teleports.IsBeingTeleportedFar(player);
            ISpellContentStore? content = scope.ServiceProvider.GetService<ISpellContentStore>();
            System.Store = content is null
                ? SpellStore.Empty
                : SpellStoreFactory.Build(content.LoadAsync().GetAwaiter().GetResult(), _logger);

            ICharacterSpellStore? spellbooks = scope.ServiceProvider.GetService<ICharacterSpellStore>();
            if (spellbooks is not null)
            {
                Spellbook.Load(spellbooks.GetAllAsync().GetAwaiter().GetResult());
            }
        }

        _logger.LogInformation("Loaded {Spells} spells and {Books} spellbooks", System.Store.Count, Spellbook.CharacterCount);
        System.MapUpdateIntervalMs = (uint)Math.Max(0, world.Options.TickIntervalMs);
        Spellbook.Start();

        world.PlayerLoggedIn += OnPlayerLoggedIn;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        world.MapCreated += SubscribeCombat;
        foreach (Map map in world.Maps)
        {
            SubscribeCombat(map);
        }

        int interval = Math.Max(1, world.Options.TickIntervalMs);
        _lastUpdateMs = world.NowMs;
        _timer = new Timer(_ => QueueUpdate(), null, interval, interval);
    }

    /// <summary>
    /// SMSG_INITIAL_SPELLS for a character entering the world (vmangos Player::SendInitialSpells:
    /// the known spells and the cooldowns still running). Character hooks load the spellbook;
    /// the fallback grants defaults for hosts without a character spell store. World thread.
    /// </summary>
    public byte[] BuildInitialSpells(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Spellbook.EnsureDefaults(player, System.Store.GetCreateSpells((byte)player.Race, (byte)player.Class));
        SpellStateSnapshot? staged;
        lock (_stagedLock)
        {
            _staged.TryGetValue(player, out staged);
        }

        if (staged is { Cooldowns.Count: > 0 })
        {
            System.RestoreCooldowns(player, staged.Cooldowns, UnixNowMs);
        }

        return SpellPackets.BuildInitialSpells([.. Spellbook.GetSpells(player)], [.. System.GetActiveCooldowns(player)]);
    }

    /// <summary>Player::Create: starting spells must be saved before creation succeeds.</summary>
    public async Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
    {
        IReadOnlyList<uint> spells = System.Store.GetCreateSpells(character.Race, character.Class);
        if (session.Services.GetService<ICharacterSpellStore>() is { } store && spells.Count > 0)
        {
            await store.AddAsync(character.Id, spells.ToArray()).ConfigureAwait(false);
        }

        Spellbook.LoadCharacter(character.Id, spells);
    }

    /// <summary>Player::_LoadSpells: loading and default writes fail the login on storage errors.</summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        if (session.Services.GetService<ICharacterSpellStore>() is not { } store)
        {
            Spellbook.EnsureDefaults(player, System.Store.GetCreateSpells(character.Race, character.Class));
            await StageStateAsync(session, character, player).ConfigureAwait(false);
            return;
        }

        // A quick relog must not load before an earlier learn/unlearn write reaches storage.
        await Spellbook.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        IReadOnlyList<uint> spells = await store.GetAsync(character.Id).ConfigureAwait(false);
        if (spells.Count == 0 && !Spellbook.ContainsCharacter(character.Id))
        {
            spells = System.Store.GetCreateSpells(character.Race, character.Class);
            if (spells.Count > 0)
            {
                await store.AddAsync(character.Id, spells.ToArray()).ConfigureAwait(false);
            }
        }

        Spellbook.LoadCharacter(character.Id, spells);
        await StageStateAsync(session, character, player).ConfigureAwait(false);
    }

    /// <summary>Run one spell update now (world thread; tests).</summary>
    public void Update()
    {
        uint now = System.NowMs;
        uint diff = unchecked(now - _lastUpdateMs);
        _lastUpdateMs = now;
        System.Update(diff);
    }

    public Task StopAsync() => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        if (_world is not null)
        {
            _world.PlayerLoggedIn -= OnPlayerLoggedIn;
            _world.PlayerLoggingOut -= OnPlayerLoggingOut;
            _world.MapCreated -= SubscribeCombat;
        }

        lock (_combatSubscriptions)
        {
            foreach (MapCombat combat in _combatSubscriptions)
            {
                combat.DamageDealt -= OnDamageDealt;
                combat.UnitKilled -= OnUnitKilled;
            }

            _combatSubscriptions.Clear();
        }

        await State.FlushAsync().ConfigureAwait(false);
        await Spellbook.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Player::_LoadAuras / _LoadSpellCooldowns on the session task: wait for this character's
    /// logout save, then stage what is restored on the world thread. A storage error fails the
    /// login (fail closed).
    /// </summary>
    private async Task StageStateAsync(WorldSession session, CharacterRecord character, Player player)
    {
        SpellStateSnapshot snapshot = await State.LoadAsync(character.Id, session.Services.GetService<ICharacterSpellStateStore>()).ConfigureAwait(false);
        lock (_stagedLock)
        {
            _staged.AddOrUpdate(player, snapshot);
        }
    }

    /// <summary>Weapon (melee/ranged) damage reaches casts and auras of the victim; spell damage already did in the spell system.</summary>
    private void SubscribeCombat(Map map)
    {
        if (map.FindUpdater<MapCombat>() is not { } combat)
        {
            return;
        }

        lock (_combatSubscriptions)
        {
            if (_combatSubscriptions.Add(combat))
            {
                combat.DamageDealt += OnDamageDealt;
                combat.UnitKilled += OnUnitKilled;
            }
        }
    }

    /// <summary>A unit died in combat: its non-passive auras go (<see cref="SpellSystem.OnUnitDied"/>).</summary>
    private void OnUnitKilled(Unit? killer, Unit victim) => System.OnUnitDied(victim);

    private void OnDamageDealt(Unit attacker, Unit victim, uint damage, bool direct, bool meleeDamage)
    {
        if (meleeDamage)
        {
            System.OnDamageTaken(victim, attacker, damage, periodic: !direct);
        }
    }

    private void QueueUpdate()
    {
        WorldRuntime? world = _world;
        if (world is null || Interlocked.Exchange(ref _updateQueued, 1) == 1)
        {
            return;
        }

        world.Post(() =>
        {
            Volatile.Write(ref _updateQueued, 0);
            try
            {
                Update();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "spell update failed");
            }
        });
    }

    /// <summary>
    /// Passive spells apply on login (cmangos-classic Player::_LoadSpells → addSpell casts
    /// passives with TRIGGERED_OLD_TRIGGERED once the player is in the world).
    /// </summary>
    private void OnPlayerLoggedIn(Player player)
    {
        SpellStateSnapshot? staged;
        lock (_stagedLock)
        {
            _staged.TryGetValue(player, out staged);
            _staged.Remove(player);
        }

        if (player.Map is { } map)
        {
            SubscribeCombat(map);
        }

        if (staged is { Auras.Count: > 0 })
        {
            System.RestoreAuras(player, staged.Auras, UnixNowMs);
        }

        foreach (uint spellId in Spellbook.GetSpells(player))
        {
            SpellInfo? spell = System.Store.Get(spellId);
            if (spell is { IsPassive: true } && !System.HasAura(player, spellId))
            {
                System.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true);
            }
        }
    }

    /// <summary>Player::SaveToDB at logout: capture cooldowns and auras before the unit leaves the spell system.</summary>
    private void OnPlayerLoggingOut(Player player)
    {
        lock (_stagedLock)
        {
            _staged.Remove(player);
        }

        try
        {
            State.Save(SpellbookCache.CharacterId(player), System.CaptureState(player, UnixNowMs));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capturing the spell state of {Player} failed", player.Guid);
        }

        System.RemoveUnit(player);
    }
}
