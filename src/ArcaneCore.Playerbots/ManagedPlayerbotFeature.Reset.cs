using System.Collections.Concurrent;
using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots;

/// <summary>One bot of a fleet reset census: its registry identity and every character its account holds.</summary>
public sealed record FleetResetBot(Guid BotId, int AccountId, string AccountName, IReadOnlyList<int> CharacterIds, bool Running);

/// <summary>
/// What a fleet reset would do, read from the authoritative bot registry (<c>managed_playerbot</c>): the bot accounts, the characters
/// on them, how many bots run, and how many are grouped or trading with a real player. <see cref="Refusal"/> is set when the
/// registry and the account or character stores disagree in a way that could make the reset touch a real player; nothing runs then.
/// </summary>
public sealed record FleetResetCensus(IReadOnlyList<FleetResetBot> Bots, int Characters, int Running, int GroupedWithPlayers,
    int TradingWithPlayers, string? Refusal)
{
    /// <summary>The bot accounts (one per registry row).</summary>
    public int Accounts => Bots.Count;

    /// <summary>Every character id the reset would delete (what a confirm code is bound to).</summary>
    public IReadOnlyList<int> CharacterIds => [.. Bots.SelectMany(b => b.CharacterIds)];
}

/// <summary>What a confirmed fleet reset did. Counts are of this run only; <see cref="Code"/> is "reset" or the refusal.</summary>
public sealed record FleetResetResult(bool Success, string Code, string Message, int Stopped = 0, int StopFailed = 0,
    int CharactersDeleted = 0, int CharactersFailed = 0, int RegistryRemoved = 0, int MembersRemoved = 0, int GuildsReleased = 0,
    int TradesCancelled = 0, int GroupedWithPlayers = 0, int AccountsKept = 0);

public sealed partial class ManagedPlayerbotFeature
{
    private int _resetting;
    private TimeProvider _resetClock = TimeProvider.System;
    private FleetResetConfirmation? _resetConfirmation;

    // Emptied bot accounts of earlier resets, handed to CreateAsync (single consumer under the operation lock). Memory only: after
    // a restart the empty accounts are simply left alone. CreateAsync re-checks every one before use.
    private readonly ConcurrentQueue<SpareAccount> _spareAccounts = new();

    private sealed record SpareAccount(int AccountId, string Name);

    /// <summary>
    /// A warning for the reset preview when the population would fill the emptied fleet again at once with boosted bots (the population
    /// is on and FreshLaunch is off), otherwise null.
    /// </summary>
    internal string? RefillWarning => _options.Population is { Enabled: true, FreshLaunch: false }
        ? "WARNING: the population manager is on and World:Playerbots:Population:FreshLaunch is off, so it would refill the fleet at once with boosted bots at their usual levels, not level 1. Turn FreshLaunch on (.reload config) before you confirm."
        : null;

    /// <summary>True while <c>.playerbot fleet reset</c> runs: bots cannot be created or started, and the population stands still.</summary>
    internal bool ResetInProgress => Volatile.Read(ref _resetting) != 0;

    /// <summary>The clock of the confirm code (a test replaces it).</summary>
    internal TimeProvider ResetClock
    {
        get => _resetClock;
        set => _resetClock = value ?? TimeProvider.System;
    }

    /// <summary>The pending preview of <c>.playerbot fleet reset</c>.</summary>
    internal FleetResetConfirmation ResetConfirmation => Volatile.Read(ref _resetConfirmation) ?? CreateResetConfirmation();

    // Two commands racing on the first use must end up with one confirmation object, or a code could be issued to one and redeemed on the other.
    private FleetResetConfirmation CreateResetConfirmation()
    {
        var created = new FleetResetConfirmation(() => _resetClock);
        return Interlocked.CompareExchange(ref _resetConfirmation, created, null) ?? created;
    }

    /// <summary>
    /// Read the fleet: every registry row is a bot account. Bot ownership comes only from the registry; the account store and the
    /// character store are then asked to agree with it, and any disagreement is a <see cref="FleetResetCensus.Refusal"/> rather than a guess:
    /// a registry row whose account is not the named one or is not a plain player account, a bot account somebody has logged in to
    /// (<see cref="Account.SessionKey"/>), a character on a bot account that the registry does not name for that account (a human's
    /// character on a bot account), a character on a bot account that is online in a real (non-managed) session, or a registry
    /// character that belongs to another account.
    /// </summary>
    internal async Task<FleetResetCensus> TakeFleetCensusAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        IReadOnlyList<ManagedPlayerbot> registered = await provider.GetRequiredService<IManagedPlayerbotStore>()
            .LoadAllAsync(cancellationToken).ConfigureAwait(false);
        IAccountStore accounts = provider.GetRequiredService<IAccountStore>();
        ICharacterStore characters = provider.GetRequiredService<ICharacterStore>();
        // The only characters a bot account may hold are the registry's own: a bot has one account and one character, and anything else
        // on the account (a character somebody made there, a second one the registry does not name) is not ours to delete.
        Dictionary<int, HashSet<int>> registryCharacters = [];
        foreach (ManagedPlayerbot row in registered)
        {
            if (!registryCharacters.TryGetValue(row.AccountId, out HashSet<int>? own)) registryCharacters[row.AccountId] = own = [];
            own.Add(row.CharacterId);
        }

        var bots = new List<FleetResetBot>(registered.Count);
        string? refusal = null;
        int characterCount = 0;
        foreach (ManagedPlayerbot bot in registered)
        {
            Account? owner = await accounts.FindByUsernameAsync(bot.AccountName, cancellationToken).ConfigureAwait(false);
            if (owner is null || owner.Id != bot.AccountId)
            {
                refusal ??= $"the registry row of bot {bot.BotId} names account '{bot.AccountName}' (id {bot.AccountId}), which the account store does not confirm";
                continue;
            }

            if (owner.Security != AccountSecurity.Player || owner.SessionKey is not null)
            {
                refusal ??= $"bot account '{bot.AccountName}' is not a plain player account without a login ({(owner.Security != AccountSecurity.Player ? "staff account" : "a session key is issued")})";
                continue;
            }

            IReadOnlyList<CharacterRecord> held = await characters.GetByAccountAsync(bot.AccountId, cancellationToken).ConfigureAwait(false);
            if (await characters.GetByIdAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false) is { } registryCharacter
                && registryCharacter.AccountId != bot.AccountId)
            {
                refusal ??= $"the registry of bot {bot.BotId} names character {registryCharacter.Id} of account {registryCharacter.AccountId}, not of its own bot account {bot.AccountId}";
                continue;
            }

            if (held.FirstOrDefault(c => !registryCharacters[bot.AccountId].Contains(c.Id)) is { } stray)
            {
                refusal ??= $"character {stray.Id} ('{stray.Name}') is on bot account '{bot.AccountName}' but is not that bot's registry character";
                continue;
            }

            characterCount += held.Count;
            bots.Add(new FleetResetBot(bot.BotId, bot.AccountId, bot.AccountName, [.. held.Select(c => c.Id)], _active.ContainsKey(bot.BotId)));
        }

        int grouped = 0, trading = 0;
        if (_world is { } world && bots.Count != 0)
        {
            FleetResetBot[] snapshot = [.. bots];
            (string? online, grouped, trading) = await world.InvokeAsync(() => ReadWorldFacts(world, snapshot), cancellationToken).ConfigureAwait(false);
            refusal ??= online;
        }

        return new FleetResetCensus(bots, characterCount, bots.Count(b => b.Running), grouped, trading, refusal);
    }

    // World thread: a bot-account character online in a session that is not a managed one is a real login on a bot account (refuse); the
    // running bots in a group with a real player, and those in a trade (a trade between two bots counts too: it is cancelled the same way).
    private (string? Refusal, int Grouped, int Trading) ReadWorldFacts(ArcaneCore.Game.Maps.WorldRuntime world, FleetResetBot[] bots)
    {
        string? refusal = null;
        int grouped = 0, trading = 0;
        EconomyFeature? economy = services.GetService<EconomyFeature>();
        ArcaneCore.Game.Groups.GroupManager? groups = services.GetService<SocialFeature>()?.Context.Groups;
        foreach (FleetResetBot bot in bots)
        {
            foreach (int id in bot.CharacterIds)
                if (world.FindOnlinePlayer(ObjectGuid.Player((uint)id)) is { Session: not WorldSession { IsManaged: true } } intruder)
                    refusal ??= $"character '{intruder.Name}' of bot account '{bot.AccountName}' is online in a session that is not a managed playerbot's";

            if (!_active.TryGetValue(bot.BotId, out ActiveBot? active) || active.Session.Player is not { } player) continue;
            if (economy?.TradeOf(player) is not null) trading++;
            if (groups?.GetGroup(player.Guid) is { } group
                && group.Members.Any(slot => slot.Guid != player.Guid && world.FindOnlinePlayer(slot.Guid) is { Session: WorldSession { IsManaged: false } }))
                grouped++;
        }

        return (refusal, grouped, trading);
    }

    /// <summary>
    /// <c>.playerbot fleet reset confirm</c>: stop every managed bot, then delete every character of every bot account through the
    /// ordinary character deletion (<see cref="CharacterDeletion.TryDeleteAsync"/>: the same path as <c>CMSG_CHAR_DELETE</c>, so every
    /// characters-database module cleans its rows and every <see cref="ICharacterDeleteHook"/> drops the live state), keep the accounts,
    /// and remove what is left of the registry, home, set-aside and population membership rows. The fleet is read again under the
    /// operation lock and must still match the counts the operator was shown; a census refusal stops everything before the first stop.
    /// <para>
    /// Real players are impossible to reach: the only characters ever deleted are those the census listed under a registry row's
    /// account, each one is deleted by a session of that very account (<c>TryDeleteAsync</c> refuses another account's character), and
    /// a bot account that fails any census check stops the whole reset. Trades with a bot are cancelled (both sides are told) and a
    /// group with a bot loses the bot when its character is deleted (the social delete hook), the group disbanding if it was two.
    /// </para>
    /// Thread affinity: any thread; world-thread work goes through <c>InvokeAsync</c>. Holds the operation lock for the whole run.
    /// </summary>
    internal async Task<FleetResetResult> ResetFleetAsync(FleetResetPromise promise, Action<string> progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promise);
        ArgumentNullException.ThrowIfNull(progress);
        if (!_options.Enabled || _stopping) return new(false, "playerbots-disabled", "playerbots are off or stopping");
        if (_detaching) return new(false, PlayerbotsDetaching, "the playerbot feature is being detached");
        if (SplitBrainLatched) return new(false, "playerbot-split-brain", "another world process is writing the playerbot rows; this world writes none until .playerbot splitbrain clear");
        if (Interlocked.CompareExchange(ref _resetting, 1, 0) != 0) return new(false, "reset-running", "a fleet reset is already running");
        try
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (SplitBrainLatched) return new(false, "playerbot-split-brain", "another world process is writing the playerbot rows; this world writes none until .playerbot splitbrain clear");
                FleetResetCensus census = await TakeFleetCensusAsync(cancellationToken).ConfigureAwait(false);
                if (census.Refusal is { } refusal)
                {
                    logger.LogWarning("Playerbot fleet reset REFUSED: {Reason}. Nothing was stopped or deleted", refusal);
                    return new(false, "census-refused", refusal);
                }

                if (DescribeFleetChange(promise, census) is { } changed)
                {
                    logger.LogWarning("Playerbot fleet reset REFUSED: {Reason}. Nothing was stopped or deleted", changed);
                    return new(false, "fleet-changed", changed);
                }

                logger.LogInformation(
                    "Playerbot fleet reset begins: {Characters} characters on {Accounts} bot accounts, {Running} bots running, {Grouped} grouped with real players, {Trading} trading",
                    census.Characters, census.Accounts, census.Running, census.GroupedWithPlayers, census.TradingWithPlayers);
                progress(string.Create(CultureInfo.InvariantCulture,
                    $"Fleet reset started: {census.Characters} characters on {census.Accounts} bot accounts, {census.Running} running."));

                int tradesCancelled = await CancelBotTradesAsync(cancellationToken).ConfigureAwait(false);
                if (tradesCancelled != 0)
                    logger.LogInformation("Playerbot fleet reset: cancelled {Count} trades with bots", tradesCancelled);

                (int stopped, int stopFailed) = await StopAllForResetAsync(census, progress, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Playerbot fleet reset: stopped {Stopped} bots ({Failed} failed to stop)", stopped, stopFailed);

                var counts = new ResetCounts();
                int position = 0;
                foreach (FleetResetBot bot in census.Bots)
                {
                    position++;
                    await DeleteBotForResetAsync(bot, counts, cancellationToken).ConfigureAwait(false);
                    if (position % 25 == 0 || position == census.Bots.Count)
                        progress(string.Create(CultureInfo.InvariantCulture,
                            $"Fleet reset: {position}/{census.Bots.Count} bot accounts processed, {counts.CharactersDeleted} characters deleted, {counts.CharactersFailed} failed."));
                }

                lock (_snapshotGate)
                {
                    HashSet<Guid> gone = [.. census.Bots.Where(b => counts.Removed.Contains(b.BotId)).Select(b => b.BotId)];
                    Volatile.Write(ref _snapshot, Volatile.Read(ref _snapshot).Where(s => !gone.Contains(s.BotId)).ToArray());
                    _snapshotEpoch++;
                }

                logger.LogInformation(
                    "Playerbot fleet reset finished: {Deleted} characters deleted, {Failed} failed, {Registry} registry rows removed, {Members} population member rows removed, {Guilds} guilds handed over or disbanded, {Accounts} bot accounts kept",
                    counts.CharactersDeleted, counts.CharactersFailed, counts.Removed.Count, counts.MembersRemoved, counts.GuildsReleased, census.Accounts);
                return new(counts.CharactersFailed == 0, counts.CharactersFailed == 0 ? "reset" : "reset-incomplete",
                    string.Create(CultureInfo.InvariantCulture,
                        $"{counts.CharactersDeleted} characters deleted ({counts.CharactersFailed} failed), {stopped} bots stopped ({stopFailed} failed), {counts.Removed.Count} registry rows and {counts.MembersRemoved} population member rows removed, {counts.GuildsReleased} guilds handed over or disbanded, {tradesCancelled} trades cancelled, {census.GroupedWithPlayers} bots had been grouped with real players, {census.Accounts} bot accounts kept for reuse."),
                    stopped, stopFailed, counts.CharactersDeleted, counts.CharactersFailed, counts.Removed.Count, counts.MembersRemoved,
                    counts.GuildsReleased, tradesCancelled, census.GroupedWithPlayers, census.Accounts);
            }
            finally { _operations.Release(); }
        }
        finally { Interlocked.Exchange(ref _resetting, 0); }
    }

    /// <summary>Null when the fleet is what the preview showed: the same characters when the preview named them, otherwise the same counts.</summary>
    private static string? DescribeFleetChange(FleetResetPromise promise, FleetResetCensus census)
    {
        string then = string.Create(CultureInfo.InvariantCulture, $"{promise.Characters} characters on {promise.Accounts} accounts then, {census.Characters} on {census.Accounts} now");
        if (promise.CharacterIds is not { } promised)
            return census.Characters == promise.Characters && census.Accounts == promise.Accounts ? null : $"the fleet changed since the preview ({then})";

        HashSet<int> before = [.. promised];
        HashSet<int> now = [.. census.CharacterIds];
        int gone = before.Count(id => !now.Contains(id));
        int added = now.Count(id => !before.Contains(id));
        if (gone == 0 && added == 0 && census.Accounts == promise.Accounts) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"the fleet changed since the preview ({gone} of the previewed characters are gone, {added} new ones appeared; {then})");
    }

    private sealed class ResetCounts
    {
        public int CharactersDeleted;
        public int CharactersFailed;
        public int MembersRemoved;
        public int GuildsReleased;
        public HashSet<Guid> Removed { get; } = [];
    }

    /// <summary>World thread: cancel every trade a running bot is in (both sides are told, as a logout does).</summary>
    private Task<int> CancelBotTradesAsync(CancellationToken cancellationToken)
    {
        if (_world is not { } world || services.GetService<EconomyFeature>() is not { } economy) return Task.FromResult(0);
        return world.InvokeAsync(() =>
        {
            int cancelled = 0;
            foreach (ActiveBot active in _active.Values)
            {
                if (active.Session.Player is not { } player || economy.TradeOf(player) is null) continue;
                economy.CancelTradeRequest(player);
                cancelled++;
            }

            return cancelled;
        }, cancellationToken);
    }

    private async Task<(int Stopped, int Failed)> StopAllForResetAsync(FleetResetCensus census, Action<string> progress, CancellationToken cancellationToken)
    {
        int stopped = 0, failed = 0, total = census.Running;
        foreach (ActiveBot[] batch in _active.Values.ToArray().Chunk(32))
        {
            Task[] stops = [.. batch.Select(active => StopCoreAsync(active.Record, false, cancellationToken))];
            try { await Task.WhenAll(stops).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* counted per task below */ }
            foreach ((Task stop, ActiveBot active) in stops.Zip(batch))
            {
                if (stop.IsCompletedSuccessfully) stopped++;
                else
                {
                    failed++;
                    Exception? cause = stop.Exception?.GetBaseException();
                    if (cause is not null and not OperationCanceledException)
                    {
                        FaultLedger.Shared.Record("playerbot.reset", cause);
                        AnomalyLedger.Shared.Report("bot-lifecycle", "reset-fault",
                            string.Create(CultureInfo.InvariantCulture, $"bot {active.Name} (guid {(uint)active.Record.CharacterId:X})"),
                            string.Create(CultureInfo.InvariantCulture, $"bot {active.Record.BotId:D} stop during fleet reset failed ({cause.GetType().Name}: {cause.Message})"),
                            AnomalySeverity.Warning, _world?.Identity);
                    }
                    logger.LogWarning(cause, "Playerbot fleet reset: bot {BotId} did not stop cleanly ({Type})", active.Record.BotId,
                        cause?.GetType().Name ?? "canceled");
                }
            }

            progress(string.Create(CultureInfo.InvariantCulture, $"Fleet reset: {stopped + failed}/{total} bots stopped."));
        }

        foreach (FleetResetBot bot in census.Bots) _quarantine.TryRemove(bot.BotId, out _);
        return (stopped, failed);
    }

    /// <summary>
    /// One bot of the reset: its characters through <see cref="CharacterDeletion.TryDeleteAsync"/> (a guild it leads is handed to the
    /// next member or disbanded first, because the social module refuses a leader), then the registry row, the population member row,
    /// the home and the set-asides. The owner account is read again and must still be the registry's plain player account.
    /// </summary>
    private async Task DeleteBotForResetAsync(FleetResetBot bot, ResetCounts counts, CancellationToken cancellationToken)
    {
        string? botName = null;
        try
        {
            if (_active.ContainsKey(bot.BotId))
            {
                counts.CharactersFailed += bot.CharacterIds.Count;
                logger.LogWarning("Playerbot fleet reset: bot {BotId} is still running; its characters are kept", bot.BotId);
                return;
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IServiceProvider provider = scope.ServiceProvider;
            Account? owner = await provider.GetRequiredService<IAccountStore>().FindByUsernameAsync(bot.AccountName, cancellationToken).ConfigureAwait(false);
            if (owner?.Id != bot.AccountId || owner.Status != AccountStatus.Active || owner.Security != AccountSecurity.Player || owner.SessionKey is not null)
            {
                counts.CharactersFailed += bot.CharacterIds.Count;
                logger.LogWarning("Playerbot fleet reset: owner of bot {BotId} was refused; its characters are kept", bot.BotId);
                return;
            }

            ICharacterStore characters = provider.GetRequiredService<ICharacterStore>();
            IReadOnlyList<CharacterRecord> held = await characters.GetByAccountAsync(bot.AccountId, cancellationToken).ConfigureAwait(false);
            botName = held.FirstOrDefault()?.Name;
            bool allGone = true;
            // The census vetted exactly these characters. One that was not in it has appeared on the account since (or the account
            // changed hands): that is not a bot's character, so nothing on this account is deleted.
            if (held.FirstOrDefault(c => c.AccountId != bot.AccountId || !bot.CharacterIds.Contains(c.Id)) is { } stray)
            {
                counts.CharactersFailed += held.Count;
                logger.LogWarning(
                    "Playerbot fleet reset: character {CharacterId} ('{Name}') appeared on the account of bot {BotId} after the census; nothing on that account is deleted",
                    stray.Id, stray.Name, bot.BotId);
                return;
            }

            if (held.Count != 0)
            {
                WorldSession session = await NewSessionAsync(owner, null, provider, cancellationToken).ConfigureAwait(false);
                try
                {
                    foreach (CharacterRecord character in held)
                    {
                        if (await ReleaseGuildLeadershipAsync(character.Id).ConfigureAwait(false)) counts.GuildsReleased++;
                        if (await CharacterDeletion.TryDeleteAsync(session, (ulong)(uint)character.Id).ConfigureAwait(false)) counts.CharactersDeleted++;
                        else
                        {
                            allGone = false;
                            counts.CharactersFailed++;
                            logger.LogWarning("Playerbot fleet reset: the deletion of character {CharacterId} of bot {BotId} was refused", character.Id, bot.BotId);
                        }
                    }
                }
                finally
                {
                    session.Kick("playerbot fleet reset character deleted");
                    try { await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                    catch (TimeoutException timeout)
                    {
                        FaultLedger.Shared.Record("playerbot.reset", timeout);
                        AnomalyLedger.Shared.Report("bot-lifecycle", "session-close-timeout",
                            string.Create(CultureInfo.InvariantCulture, $"bot {held[0].Name} (guid {(uint)held[0].Id:X})"),
                            string.Create(CultureInfo.InvariantCulture, $"bot {bot.BotId:D} fleet-reset deletion session did not close within 10 s"),
                            AnomalySeverity.Warning, _world?.Identity);
                        logger.LogWarning(timeout, "Playerbot {BotId}: the deletion session did not close within 10 s", bot.BotId);
                    }
                }
            }

            if (!allGone) return;

            // The delete modules already remove the registry, home, set-aside and member rows with the character in production; these
            // remove what a store without that cascade (or a row whose character was already gone) leaves.
            IManagedPlayerbotStore registry = provider.GetRequiredService<IManagedPlayerbotStore>();
            if (await registry.FindAsync(bot.BotId, cancellationToken).ConfigureAwait(false) is { } row)
                await registry.DeleteAsync(bot.BotId, row.AccountId, row.CharacterId, cancellationToken).ConfigureAwait(false);
            if (provider.GetService<IPlayerbotPopulationStore>() is { } members
                && await members.DeleteAsync(bot.BotId, cancellationToken).ConfigureAwait(false))
                counts.MembersRemoved++;
            if (provider.GetService<IManagedPlayerbotHomeStore>() is { } homes) await homes.ClearAsync(bot.BotId, cancellationToken).ConfigureAwait(false);
            if (provider.GetService<IManagedPlayerbotSetAsideStore>() is { } setAsides)
                await setAsides.ReplaceAsync(bot.BotId, [], cancellationToken).ConfigureAwait(false);
            _homes.TryRemove(bot.BotId, out _);
            _loadedSetAsides.TryRemove(bot.BotId, out _);
            foreach (int characterId in bot.CharacterIds) await ForgetRelationsAsync(provider, bot.BotId, characterId).ConfigureAwait(false);
            if (bot.CharacterIds.Count == 0) await ForgetRelationsAsync(provider, bot.BotId, 0).ConfigureAwait(false);
            counts.Removed.Add(bot.BotId);
            _spareAccounts.Enqueue(new SpareAccount(bot.AccountId, bot.AccountName));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            FaultLedger.Shared.Record("playerbot.reset", ex);
            AnomalyLedger.Shared.Report("bot-lifecycle", "reset-fault",
                string.Create(CultureInfo.InvariantCulture, $"bot {botName ?? bot.AccountName} (guid {(uint)(bot.CharacterIds.Count > 0 ? bot.CharacterIds[0] : 0):X})"),
                string.Create(CultureInfo.InvariantCulture, $"bot {bot.BotId:D} fleet reset failed ({ex.GetType().Name}: {ex.Message})"),
                AnomalySeverity.Warning, _world?.Identity);
            counts.CharactersFailed += bot.CharacterIds.Count;
            logger.LogWarning(ex, "Playerbot fleet reset: bot {BotId} failed ({Type}: {Message})", bot.BotId, ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>
    /// A character that leads a guild cannot be deleted (the social module refuses it). Remove it from the guild first: leadership
    /// passes to the next member (who may be a real player), or the guild disbands when it was the last. True when the character led one.
    /// <para>
    /// Nothing is handed over for a character that is still in the world (the deletion would be refused anyway). A deletion that is
    /// refused later (a delete hook, the database transaction) leaves the bot out of its guild with its character kept; the
    /// reset then reports it as failed and the character is deleted by the next reset.
    /// </para>
    /// </summary>
    private async Task<bool> ReleaseGuildLeadershipAsync(int characterId)
    {
        if (_world is not { } world || services.GetService<SocialFeature>() is not { } social) return false;
        await social.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        uint id = (uint)characterId;
        bool led = await world.InvokeAsync(() =>
        {
            ArcaneCore.Game.Guilds.GuildManager guilds = social.Context.Guilds;
            if (world.FindOnlinePlayer(ObjectGuid.Player(id)) is not null) return false;
            if (guilds.GetGuildOf(id) is not { } guild || guild.LeaderId != id) return false;
            guilds.AdminUninvite(id);
            return true;
        }).ConfigureAwait(false);
        // The stored guild row must name the new leader (or be gone) before the delete transaction looks at it.
        if (led) await social.Context.Persistence.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return led;
    }
}
