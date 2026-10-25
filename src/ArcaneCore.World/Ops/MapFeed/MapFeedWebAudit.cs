using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// One line of the durable audit file. <paramref name="Event"/> is <c>action</c>, <c>refusals</c> (the channel: a run of identical pre-actor refusals, <paramref name="Targets"/> = how many repeats), or
/// <c>login</c>, <c>logout</c>, <c>kick</c> and <c>revoke</c> (the ticket service, <see cref="IFeedActorTickets"/>). The ticket itself is never
/// written: only <c>ticketRef</c>, the first four bytes of its SHA-256 in hex.
/// </summary>
/// <param name="Event">The kind of record.</param>
/// <param name="Actor">The actor's invoker name (<c>atlas-web:ACCOUNT</c>, <c>atlas-local:user#pid</c>), or null when none was resolved.</param>
/// <param name="AccountId">The actor's account id, or null.</param>
/// <param name="Ticket">The ticket text the actor presented (hashed into <c>ticketRef</c>, never stored), or null.</param>
/// <param name="ClientIp">The address the web listener reports for the end user, or null.</param>
/// <param name="Line">The command line as run, with secrets masked, or null.</param>
/// <param name="Targets">How many targets the action named.</param>
/// <param name="Result"><c>ok</c> or the refusal / failure code.</param>
/// <param name="Code">The refusal / failure code, or null on success.</param>
internal sealed record WebAuditRecord(
    string Event, string? Actor, int? AccountId, string? Ticket, string? ClientIp, string? Line, int Targets, string Result, string? Code);

/// <summary>
/// The durable audit of the action channel (docs/areas/atlas-actions.md, "Audit"): <c>atlas-web-audit.jsonl</c>, one JSON object per line
/// <c>{time, event, actor, accountId, ticketRef, clientIp, line, targets, result, code}</c>, written and flushed to disk before an action's answer
/// is sent. Web actions are always recorded, local ones while <see cref="MapFeedActionsOptions.AuditLocalDurable"/> is on. An action that cannot be
/// recorded is refused (<see cref="Preflight"/>) instead of run. The file is opened per record in append mode (a record is one write, so
/// parallel hosts and a reader on the file see whole lines), and a file over <see cref="RotateBytes"/> is renamed with its time and kept
/// <see cref="MapFeedActionsOptions.WebAuditDays"/> days. Thread-safe: one process-wide gate serializes the writes.
/// </summary>
internal sealed class MapFeedWebAudit(IOptions<MapFeedActionsOptions> options, ILoggerFactory loggers, TimeProvider? time = null)
{
    /// <summary>The audit file's name.</summary>
    public const string FileName = "atlas-web-audit.jsonl";

    /// <summary>A file this long is rotated before the next record.</summary>
    public const long RotateBytes = 16L * 1024 * 1024;

    private static readonly Lock FileGate = new();

    private readonly MapFeedActionsOptions _options = options.Value;
    private readonly ILogger _logger = loggers.CreateLogger(MapFeedActions.LogCategory);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _purged;

    /// <summary>The first four bytes of SHA-256 of the ticket text, as 8 upper-case hex digits.</summary>
    public static string TicketRef(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)))[..8];

    /// <summary>The audit file's full path.</summary>
    public string FilePath => Path.Combine(Path.GetFullPath(string.IsNullOrWhiteSpace(_options.AuditDirectory) ? "logs" : _options.AuditDirectory), FileName);

    /// <summary>Whether a record can be appended now: the directory exists (or is created) and the file opens for append. Nothing is written.</summary>
    public bool Preflight()
    {
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger.LogError(ex, "atlas audit: {Path} cannot be opened for append; actions that need the durable audit are refused", FilePath);
            return false;
        }
    }

    /// <summary>Append <paramref name="record"/> and flush it to disk. False (and an error in the log) when it could not be written.</summary>
    public ValueTask<bool> AppendAsync(WebAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("time"u8, _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("event"u8, record.Event);
            WriteOptional(writer, "actor"u8, record.Actor);
            if (record.AccountId is { } account)
            {
                writer.WriteNumber("accountId"u8, account);
            }
            else
            {
                writer.WriteNull("accountId"u8);
            }

            WriteOptional(writer, "ticketRef"u8, record.Ticket is { Length: > 0 } ticket ? TicketRef(ticket) : null);
            WriteOptional(writer, "clientIp"u8, record.ClientIp);
            WriteOptional(writer, "line"u8, record.Line);
            writer.WriteNumber("targets"u8, record.Targets);
            writer.WriteString("result"u8, record.Result);
            WriteOptional(writer, "code"u8, record.Code);
            writer.WriteEndObject();
        }

        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RotateIfNeeded();
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.WriteThrough);
                stream.Write(buffer.WrittenSpan);
                stream.Flush(flushToDisk: true);
            }

            return ValueTask.FromResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            FaultLedger.Shared.Record("ops.map-feed-audit", ex);
            _logger.LogError(ex, "atlas audit: a {Event} record could not be written to {Path}", record.Event, FilePath);
            return ValueTask.FromResult(false);
        }
    }

    private static void WriteOptional(Utf8JsonWriter writer, ReadOnlySpan<byte> name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>Under <see cref="FileGate"/>: rename a file past <see cref="RotateBytes"/>, and drop rotated copies older than the retention (once per process start, and on each rotation).</summary>
    private void RotateIfNeeded()
    {
        string path = FilePath;
        bool rotate = File.Exists(path) && new FileInfo(path).Length >= RotateBytes;
        if (rotate)
        {
            string stamp = _time.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string target = Path.Combine(Path.GetDirectoryName(path)!, $"atlas-web-audit-{stamp}.jsonl");
            for (int n = 1; File.Exists(target); n++)
            {
                target = Path.Combine(Path.GetDirectoryName(path)!, $"atlas-web-audit-{stamp}-{n}.jsonl");
            }

            File.Move(path, target);
        }

        if (rotate || !_purged)
        {
            _purged = true;
            PurgeRotated(Path.GetDirectoryName(path)!);
        }
    }

    private void PurgeRotated(string directory)
    {
        DateTimeOffset cutoff = _time.GetUtcNow().AddDays(-Math.Max(1, _options.WebAuditDays));
        foreach (string file in Directory.EnumerateFiles(directory, "atlas-web-audit-*.jsonl"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length >= "atlas-web-audit-".Length + 14
                && DateTimeOffset.TryParseExact(name.AsSpan("atlas-web-audit-".Length, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset rotatedAt)
                && rotatedAt < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "atlas audit: could not delete the rotated file {File}", file);
                }
            }
        }
    }
}
