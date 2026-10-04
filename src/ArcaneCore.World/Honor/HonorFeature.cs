using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Honor;

/// <summary>
/// Honor in the world daemon (docs/areas/honor.md): per-character load and save (characters schema, honor tables) through
/// <see cref="HonorWriteQueue"/>, honor for kills, the honor tab fields, the honor spell effect, the equip rank gate and the
/// PvP flag persistence. Other features reach it with <c>GetService&lt;HonorFeature&gt;()</c> and read
/// <see cref="ActiveService"/>, which is null while <c>World:Honor:Enabled</c> is false (every consumer then treats all players
/// as unranked, today's behaviour).
/// </summary>
public sealed class HonorFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers)
    : IWorldFeature, ICharacterHooks, IAsyncDisposable
{
    private readonly ILogger _logger = loggers.CreateLogger<HonorFeature>();
    private readonly Lock _loadLock = new();
    private HonorWriteQueue? _writes;
    private HonorService? _service;
    private HonorKillRewards? _rewards;
    private WorldRuntime? _world;
    private HonorOptions _options = new();
    private uint _weekBegin;

    /// <summary>The clock honor is dated with: the registered <see cref="HonorClock"/> (tests substitute a fixed one), else the real clock.</summary>
    private HonorClock Clock => services.GetService<HonorClock>() ?? HonorClock.System;

    /// <summary>
    /// Held by a login from before its honor read until its state is tracked, and by the weekly job from before its store
    /// transaction until the week begin day moves. A login therefore sees the week wholly before (old row, old week: the result is
    /// applied to it in memory later) or wholly after (new row, new week: nothing to add).
    /// </summary>
    internal SemaphoreSlim WeekGate { get; } = new(1, 1);

    /// <summary>The bound <c>World:Honor</c> options (after the first access to <see cref="Service"/> or <see cref="Attach"/>).</summary>
    public HonorOptions Options
    {
        get
        {
            _ = Service;
            return _options;
        }
    }

    /// <summary>The world-thread honor owner. Built on first use, so a feature attached earlier can take it.</summary>
    public HonorService Service => _service ?? Load();

    /// <summary>The honor owner when honor is enabled, otherwise null.</summary>
    public HonorService? ActiveService => Options.Enabled ? Service : null;

    /// <summary>The kill-credit component (null until attached, or when disabled).</summary>
    public HonorKillRewards? Rewards => _rewards;

    /// <summary>The first game day of the current honor week (the last maintenance day).</summary>
    public uint WeekBeginDay => Volatile.Read(ref _weekBegin);

    /// <summary>Writes queued or in progress.</summary>
    public int PendingWrites => _writes?.Pending ?? 0;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the honor feature is already attached");
        }

        HonorService service = Service;
        _world = world;
        if (!_options.Enabled)
        {
            _logger.LogInformation("Honor is disabled ({Section}:Enabled = false); every player stays unranked", HonorSettings.SectionName);
            return;
        }

        var hooks = new HonorHooks(_options, Clock) { InternalRank = player => service.For(player)?.Rank.Rank ?? 0 };
        if (!HonorHooks.TryRegister(world, hooks))
        {
            _logger.LogWarning("Honor hooks were already registered for this world; the channel rank source is not installed");
        }

        _writes!.Start();
        if (services.GetService<SpellFeature>() is { } spells)
        {
            service.InstallForSpells(spells.System);
        }
        else
        {
            _logger.LogWarning("No spell feature: the honor spell effect is not available");
        }

        _rewards = new HonorKillRewards(
            service,
            RewardGroups.Resolver(services),
            noPvpCredit: unit => CombatEnvironment.For(world).Auras?.HasAuraType(unit, AuraType.NoPvpCredit) ?? false);
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        world.PlayerLoggingOut += OnPlayerLoggingOut;
        _logger.LogInformation("Honor enabled: week begins on game day {Day}", WeekBeginDay);
    }

    /// <summary>Move the week begin day forward (the weekly maintenance finished a period).</summary>
    public void SetWeekBegin(uint day) => Volatile.Write(ref _weekBegin, day);

    /// <summary>Clear rows left by a deleted character whose id was reused (after older writes drain).</summary>
    public async Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (_writes is not null)
        {
            await _writes.FlushAsync().ConfigureAwait(false);
            _writes.ForgetCharacter(character.Id);
        }

        if (session.Services.GetService<IHonorStore>() is { } store)
        {
            await store.DeleteCharacterAsync(character.Id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// HonorMgr::Load on the session task, after earlier writes have drained; faults (refusing the login) while this
    /// character has honor writes that are retained and still cannot be persisted, so stale rows never become live state.
    /// Applies the honor fields, the PvP flags and the equip rank requirement before the player is visible.
    /// </summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);
        if (ActiveService is not { } service)
        {
            return;
        }

        await WeekGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writes is not null)
            {
                await _writes.FlushCharacterAsync(character.Id).ConfigureAwait(false);
            }

            CharacterHonorData stored = session.Services.GetService<IHonorStore>() is { } store
                ? await store.LoadAsync(character.Id).ConfigureAwait(false)
                : CharacterHonorData.Empty;
            service.Track(player, service.Create(player, stored));
        }
        finally
        {
            WeekGate.Release();
        }

        service.RestorePvpFlags(player);
        HonorItemRequirements.Install(player.Inventory, service);
    }

    /// <summary>Queue the removal of a deleted character's honor (run by <see cref="HonorCharacterDeleteHook"/>).</summary>
    public void DeleteCharacter(int characterId) => _writes?.DeleteCharacter(characterId);

    /// <summary>Wait until every queued write has been attempted. Never retries and never throws.</summary>
    public Task FlushAsync() => _writes?.FlushAsync() ?? Task.CompletedTask;

    /// <summary><see cref="FlushAsync"/> plus one more attempt at this character's retained writes; faults while they are not durable.</summary>
    public Task FlushCharacterAsync(int characterId) => _writes?.FlushCharacterAsync(characterId) ?? Task.CompletedTask;

    /// <summary>True while the character has honor writes that failed all attempts and are retained.</summary>
    public bool HasRetainedFailure(int characterId) => _writes?.HasRetainedFailure(characterId) ?? false;

    public async Task StopAsync()
    {
        if (_writes is not null)
        {
            await _writes.StopAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_world is { } world)
        {
            world.MapCreated -= OnMapCreated;
            world.MapUnloading -= OnMapUnloading;
            world.PlayerLoggingOut -= OnPlayerLoggingOut;
            foreach (Map map in world.Maps)
            {
                _rewards?.Detach(map.Combat);
            }
        }

        await StopAsync().ConfigureAwait(false);
    }

    private void OnMapCreated(Map map) => _rewards?.Attach(map.Combat);

    private void OnMapUnloading(Map map) => _rewards?.Detach(map.Combat);

    private void OnPlayerLoggingOut(Player player)
    {
        if (_service is null)
        {
            return;
        }

        _service.CapturePvpFlags(player);
        _service.Untrack(player);
        int characterId = (int)player.Guid.Low;
        if (_writes is { } writes && writes.HasRetainedFailure(characterId))
        {
            writes.RequestRetry(characterId); // an early retry; the login barrier and shutdown still retry
        }
    }

    /// <summary>Startup content failures stop attachment. Also fixes the week begin day (HonorMaintenancer::Initialize).</summary>
    private HonorService Load()
    {
        lock (_loadLock)
        {
            if (_service is { } loaded)
            {
                return loaded;
            }

            var settings = new HonorSettings();
            services.GetService<IConfiguration>()?.GetSection(HonorSettings.SectionName).Bind(settings);
            _options = settings.ToOptions();
            _writes = new HonorWriteQueue(scopes, loggers.CreateLogger<HonorWriteQueue>());

            // HonorMaintenancer::Initialize (HonorMgr.cpp:654-669): read the stored days; the very first start takes the most
            // recent maintenance weekday. It must happen before any player's honor is computed, or every row would count as
            // "this week" and the lifetime totals would double count.
            uint today = Clock.GameDay(_options.TimeZoneOffsetHours * 3600);
            uint last = HonorMaintenancePlanner.LastMaintenanceDay(today, _options.MaintenanceDay);
            using (IServiceScope scope = scopes.CreateScope())
            {
                if (scope.ServiceProvider.GetService<IHonorStore>() is { } store)
                {
                    HonorMaintenanceState? state = store.GetMaintenanceAsync().GetAwaiter().GetResult();
                    if (state is null || state.LastDay == 0)
                    {
                        store.SaveMaintenanceAsync(new HonorMaintenanceState(last, last + 7, false)).GetAwaiter().GetResult();
                    }
                    else
                    {
                        last = state.LastDay;
                    }
                }
            }

            Volatile.Write(ref _weekBegin, last);
            return _service = new HonorService(_options, Clock, () => WeekBeginDay, new Sink(_writes));
        }
    }

    private sealed class Sink(HonorWriteQueue writes) : IHonorSink
    {
        public void CpAdded(Player player, HonorCpRecord row) => writes.AppendCp((int)player.Guid.Low, row);

        public void StateChanged(Player player, CharacterHonorState state) => writes.SaveState((int)player.Guid.Low, state);

        public void Reset(Player player) => writes.Reset((int)player.Guid.Low);
    }
}
