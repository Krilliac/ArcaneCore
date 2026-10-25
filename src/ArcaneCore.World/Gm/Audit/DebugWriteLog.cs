using System.Security.Cryptography;
using System.Text;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>How long a debug write lasts (debug-commands plan 1.2/1.5).</summary>
public enum DebugWriteScope
{
    /// <summary>Live state only; gone with the entity or at restart ("(live, not saved)").</summary>
    Live,

    /// <summary>An option override that holds until the next reload of the configuration ("(until .reload config)").</summary>
    UntilReload,

    /// <summary>Written to storage (a persisted field).</summary>
    Persisted,

    /// <summary>A one-off action rather than a changed field (kick, resend, start, stop): there is no value that lasts.</summary>
    Action,
}

/// <summary>
/// One structured debug write: who changed what, from which value to which, how long it lasts and whether a confirm code was echoed.
/// Text is capped at <see cref="DebugWriteLog.MaxValueLength"/>; sensitive data arrives already hashed (<see cref="DebugWrite.Hash"/>).
/// </summary>
public sealed record DebugWriteEntry(
    long Unix, int AccountId, string Character, string Command, string Target, string Field,
    string OldValue, string NewValue, DebugWriteScope Scope, string Reason, bool Confirmed);

/// <summary>
/// The bounded in-memory ring of <see cref="DebugWriteEntry"/> (plan 1.5, I3), lost on restart like the GM audit tail; every entry is
/// also logged by <see cref="DebugWrite.Record"/>. Thread affinity: any thread (one lock). Allocation: one entry per write, the oldest
/// dropped at capacity.
/// </summary>
public sealed class DebugWriteLog(int capacity = DebugWriteLog.DefaultCapacity, TimeProvider? clock = null)
{
    /// <summary>The entries kept when no capacity is given.</summary>
    public const int DefaultCapacity = 1000;

    /// <summary>The longest text field kept; longer text is cut and ends in "...".</summary>
    public const int MaxValueLength = 256;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly int _capacity = Math.Max(0, capacity);
    private readonly Lock _gate = new();
    private readonly Queue<DebugWriteEntry> _entries = new();
    private readonly HashSet<string> _unflagged = new(StringComparer.Ordinal);
    private long _total;

    /// <summary>
    /// Strict mode (the test host sets it): <see cref="DebugWrite.Record"/> throws when the running command is not marked
    /// <see cref="ChatCommand.IsWrite"/> instead of logging an error. Set once before the world runs; not a per-call switch.
    /// </summary>
    public bool Strict { get; set; }

    /// <summary>True the first time <paramref name="path"/> is reported (so a missing flag is logged once per command, not per write).</summary>
    internal bool NoteUnflagged(string path)
    {
        lock (_gate) return _unflagged.Add(path);
    }

    /// <summary>How many writes were recorded since start (including those the ring has since dropped).</summary>
    public long Total
    {
        get
        {
            lock (_gate) return _total;
        }
    }

    /// <summary>Store one write. Text fields are cut to <see cref="MaxValueLength"/>; null becomes empty.</summary>
    public DebugWriteEntry Record(
        int accountId, string character, string command, string target, string field, string? oldValue, string? newValue,
        DebugWriteScope scope, string? reason = null, bool confirmed = false)
    {
        var entry = new DebugWriteEntry(
            _clock.GetUtcNow().ToUnixTimeSeconds(), accountId, Cut(character), Cut(command), Cut(target), Cut(field),
            Cut(oldValue), Cut(newValue), scope, Cut(reason), confirmed);
        lock (_gate)
        {
            _total++;
            if (_capacity == 0)
            {
                return entry;
            }

            while (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }

        return entry;
    }

    /// <summary>The newest <paramref name="count"/> entries, oldest first.</summary>
    public IReadOnlyList<DebugWriteEntry> Tail(int count)
    {
        lock (_gate)
        {
            return [.. _entries.Skip(Math.Max(0, _entries.Count - Math.Max(0, count)))];
        }
    }

    private static string Cut(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= MaxValueLength ? text : string.Concat(text.AsSpan(0, MaxValueLength - 3), "...");
}

/// <summary>
/// The one call every debug write makes (plan 1.5): <see cref="Record"/> files the structured entry on
/// <see cref="GmAuditFeature.DebugWrites"/> and logs it. The command that calls it must be marked <see cref="ChatCommand.IsWrite"/>;
/// <c>DebugWriteEnforcementTests</c> fails a flagged command whose handler never reaches <see cref="Record"/>.
/// </summary>
public static class DebugWrite
{
    /// <summary>Logger category of the debug write lines.</summary>
    public const string Category = "ArcaneCore.Gm.Write";

    /// <summary>
    /// Record one write by <paramref name="context"/>'s invoker, once per changed target, for the command the table is running
    /// (<see cref="CommandContext.CommandPath"/>). <paramref name="target"/> is a short description of what was changed (never a chat
    /// body). <paramref name="sensitive"/> stores and logs both values as a hash (addresses, endpoints, account data: plan 1.4).
    /// <paramref name="confirmed"/> says a confirm code was echoed (<see cref="GmConfirm"/>). When the running command is not marked
    /// <see cref="ChatCommand.IsWrite"/> this throws in strict mode (<see cref="DebugWriteLog.Strict"/>, tests) and otherwise logs an
    /// error once per command and records the write anyway. Returns false when the host has no <see cref="GmAuditFeature"/> (nothing
    /// recorded). World thread (command handlers).
    /// </summary>
    public static bool Record(
        CommandContext context, string target, string field, string? oldValue, string? newValue,
        DebugWriteScope scope, string? reason = null, bool confirmed = false, bool sensitive = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        IServiceProvider services = context.Session.Services;
        if (services.GetService<GmAuditFeature>() is not { } audit)
        {
            return false;
        }

        string path = context.CommandPath.Length == 0 ? "(no command)" : context.CommandPath;
        ILogger log = services.GetService<ILoggerFactory>()?.CreateLogger(Category) ?? context.Session.Logger;
        if (context.Command?.IsWrite != true)
        {
            if (audit.DebugWrites.Strict)
            {
                throw new InvalidOperationException($"DebugWrite.Record was called by '{path}', which is not marked IsWrite (plan 1.5): set IsWrite on the ChatCommand.");
            }

            if (audit.DebugWrites.NoteUnflagged(path))
            {
                log.LogError("Command '{Command}' records a debug write but is not marked IsWrite", path);
            }
        }

        if (sensitive)
        {
            oldValue = Hash(oldValue);
            newValue = Hash(newValue);
        }

        DebugWriteEntry entry = audit.DebugWrites.Record(
            context.IsConsole ? 0 : context.Session.AccountId, context.InvokerName, path, target, field, oldValue, newValue, scope, reason, confirmed);
        log.LogInformation(
            "DebugWrite {Command} by {Character} (account {Account}) target {Target} field {Field}: {Old} -> {New} [{Scope}] reason {Reason} confirmed {Confirmed}",
            entry.Command, entry.Character, entry.AccountId, entry.Target, entry.Field, entry.OldValue, entry.NewValue, entry.Scope, entry.Reason, entry.Confirmed);
        return true;
    }

    /// <summary>
    /// A stable stand-in for sensitive data: "sha256:" and the first 8 hex digits of the value's SHA-256 (empty stays empty), so two
    /// entries about the same address still correlate while the address is not in the log.
    /// </summary>
    public static string Hash(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
}
