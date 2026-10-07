using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>One audited GM command line (what <see cref="GmCommandLog"/> writes), kept for <c>.arcane gmlog</c>.</summary>
public sealed record GmAuditEntry(long Unix, int AccountId, string Player, string Command, string Line);

/// <summary>
/// The state of the GM audit lane (docs/integration/gm-audit-lane.md): account chat mutes, GM tickets and the tail of
/// the GM command audit log. Mutes and tickets live in memory (loaded from <see cref="IGmAuditStore"/> at startup)
/// and are written through <see cref="GmAuditWriteQueue"/>, so staff always see the latest state even when storage is
/// failing and the write is retained. A mute is offered to <see cref="ChatFeature"/> as an <see cref="IChatMuteSource"/>
/// (the seam the chat lane left for <c>.mute</c>, see <see cref="IChatMuteSource"/>); the gates of
/// vmangos HandleChatMessageOpcode stay in <see cref="ChatFeature"/>. With no <see cref="IGmAuditStore"/> registered
/// everything lives in memory only. State is guarded by one lock: commands run on the world thread, the character
/// deletion hook does not.
/// </summary>
public sealed partial class GmAuditFeature(
    IServiceScopeFactory scopes, ILoggerFactory loggers, IConfiguration? configuration = null, TimeProvider? clock = null)
    : IWorldFeature, IChatMuteSource, ICharacterDeleteHook
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, AccountMuteRecord> _mutes = [];
    private readonly Queue<GmAuditEntry> _tail = new();
    private int _tailSize = new GmOptions().AuditTailSize;
    private long _lastMuteSweepUnixSeconds = long.MinValue;

    /// <summary>The write-through queue (exposed for health reporting and tests).</summary>
    public GmAuditWriteQueue Writes { get; } = new(scopes, loggers.CreateLogger<GmAuditWriteQueue>());

    /// <summary>Whole seconds since the Unix epoch.</summary>
    public long NowUnixSeconds => _clock.GetUtcNow().ToUnixTimeSeconds();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (configuration is not null)
        {
            GmOptions options = GmOptions.Bind(configuration);
            _tailSize = Math.Max(0, options.AuditTailSize);
            // Fail-closed: a negative value cannot mean "unlimited", it falls back to the default; 0 is literally none.
            TicketMutationsPerMinute = options.TicketMutationsPerMinute < 0 ? new GmOptions().TicketMutationsPerMinute : options.TicketMutationsPerMinute;
        }

        // Fail closed: a database that cannot be read must not start the world with every mute forgotten.
        using IServiceScope scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<IGmAuditStore>() is { } store)
        {
            Load(store.LoadActiveMutesAsync(NowUnixSeconds).GetAwaiter().GetResult(),
                store.LoadOpenTicketsAsync().GetAwaiter().GetResult(),
                store.GetMaxTicketIdAsync().GetAwaiter().GetResult());
        }

        Writes.Start();

        // Rows of mutes that ran out while the world was down go now; the world tick ends the rest as they expire.
        long now = NowUnixSeconds;
        Writes.Save(ExpiredMutesKey, store => store.DeleteExpiredMutesAsync(now));
        world.Updated += ExpireMutes;
    }

    public Task StopAsync() => Writes.StopAsync();

    // ---- mutes ----------------------------------------------------------------------------------

    /// <summary>The account's mute when it is still in force, else null.</summary>
    public AccountMuteRecord? MuteOf(int accountId)
    {
        lock (_gate)
        {
            return _mutes.TryGetValue(accountId, out AccountMuteRecord? mute) && mute.MutedUntil > NowUnixSeconds ? mute : null;
        }
    }

    /// <summary>Every mute still in force, by account id.</summary>
    public IReadOnlyList<AccountMuteRecord> ActiveMutes()
    {
        long now = NowUnixSeconds;
        lock (_gate)
        {
            return [.. _mutes.Values.Where(m => m.MutedUntil > now).OrderBy(m => m.AccountId)];
        }
    }

    /// <summary>Mute the account for <paramref name="seconds"/> from now (replacing any mute it has) and persist it.</summary>
    public AccountMuteRecord Mute(int accountId, long seconds, string by, AccountSecurity bySecurity, string reason)
    {
        long now = NowUnixSeconds;
        var mute = new AccountMuteRecord(accountId, now + seconds, now, by, (byte)bySecurity, reason);
        lock (_gate)
        {
            _mutes[accountId] = mute;
        }

        Writes.Save(MuteKey(accountId), store => store.SaveMuteAsync(mute));
        return mute;
    }

    /// <summary>End the account's mute and persist that. False when it had none in force.</summary>
    public bool Unmute(int accountId)
    {
        bool had;
        lock (_gate)
        {
            had = _mutes.TryGetValue(accountId, out AccountMuteRecord? mute) && mute.MutedUntil > NowUnixSeconds;
            _mutes.Remove(accountId);
        }

        if (had)
        {
            Writes.Save(MuteKey(accountId), store => store.DeleteMuteAsync(accountId));
        }

        return had;
    }

    public long MutedUntilUnixSeconds(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return MuteOf(player.AccountId)?.MutedUntil ?? 0;
    }

    /// <summary>How many account mutes are held in memory, expired ones not yet swept included (tests and diagnostics).</summary>
    public int RememberedMutes
    {
        get
        {
            lock (_gate)
            {
                return _mutes.Count;
            }
        }
    }

    private static string MuteKey(int accountId) => "mute:" + accountId;

    private const string ExpiredMutesKey = "mute:expired";

    /// <summary>
    /// End the mutes that ran out, once per clock second on the world thread: drop them from memory and delete their rows
    /// (only rows that really ended, so a mute set again meanwhile is safe). vmangos keeps <c>account.mutetime</c> and
    /// only compares it with the clock when someone speaks (WorldSession::m_muteTime, ChatHandler.cpp:221-247); the
    /// outcome for the player is the same, this only stops expired mutes from piling up in memory and in
    /// <c>account_mute</c>. Nothing is said to the player when a mute ends on its own, as in vmangos.
    /// </summary>
    private void ExpireMutes(uint diffMs)
    {
        long now = NowUnixSeconds;
        if (now == _lastMuteSweepUnixSeconds)
        {
            return;
        }

        _lastMuteSweepUnixSeconds = now;
        bool any = false;
        lock (_gate)
        {
            foreach (int accountId in _mutes.Where(pair => pair.Value.MutedUntil <= now).Select(pair => pair.Key).ToArray())
            {
                _mutes.Remove(accountId);
                any = true;
            }
        }

        if (any)
        {
            Writes.Save(ExpiredMutesKey, store => store.DeleteExpiredMutesAsync(now));
        }
    }

    // ---- audit tail -----------------------------------------------------------------------------

    /// <summary>Remember an audit line (the newest <see cref="GmOptions.AuditTailSize"/> are kept).</summary>
    public void RecordCommand(int accountId, string player, string commandText, string line)
    {
        if (_tailSize <= 0)
        {
            return;
        }

        lock (_gate)
        {
            while (_tail.Count >= _tailSize)
            {
                _tail.Dequeue();
            }

            _tail.Enqueue(new GmAuditEntry(NowUnixSeconds, accountId, player, commandText, line));
        }
    }

    /// <summary>The newest <paramref name="count"/> audited lines, oldest first.</summary>
    public IReadOnlyList<GmAuditEntry> Tail(int count)
    {
        lock (_gate)
        {
            return [.. _tail.Skip(Math.Max(0, _tail.Count - Math.Max(0, count)))];
        }
    }

    /// <summary>How many lines the tail can hold (0 when the audit tail is off).</summary>
    public int TailCapacity => _tailSize;

    private void Load(IReadOnlyList<AccountMuteRecord> mutes, IReadOnlyList<GmTicketRecord> tickets, int maxTicketId)
    {
        lock (_gate)
        {
            foreach (AccountMuteRecord mute in mutes)
            {
                _mutes[mute.AccountId] = mute;
            }

            foreach (GmTicketRecord ticket in tickets)
            {
                _tickets[ticket.Id] = ticket;
            }

            _lastTicketId = Math.Max(_lastTicketId, Math.Max(maxTicketId, tickets.Count == 0 ? 0 : tickets.Max(t => t.Id)));
        }
    }
}
