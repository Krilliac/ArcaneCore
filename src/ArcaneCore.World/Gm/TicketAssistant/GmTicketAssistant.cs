using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Teleport;
using ArcaneCore.World.Graveyards;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.TicketAssistant;

/// <summary>How one ticket ended up (<see cref="GmTicketAssistant.ProcessTicketAsync"/>).</summary>
public sealed record TicketOutcome(int TicketId, TicketCategory Category, TicketAction Action, bool Performed, bool Closed, string Detail);

/// <summary>The assistant's state for <c>.ticket assistant status</c>.</summary>
public sealed record TicketAssistantStatus(bool Enabled, int Handled, int Fixed, int Answered, int Escalated, int NeedsHuman, bool ModelAvailable);

/// <summary>
/// The GM ticket assistant (ArcaneCore only; off by default, <see cref="GmTicketAssistantOptions"/>). It polls the open tickets and
/// handles one at a time, never one it has already handled or a human has answered:
/// <list type="number">
/// <item>facts about the author are read on the world thread (position, alive or ghost, time dead, time without moving, recent
/// movement anomalies from <see cref="TicketEvidenceLog"/>, the quest the text names);</item>
/// <item>a category is chosen off the world thread: by the model of <see cref="ITicketAssistantModel"/> (strictly parsed, anything
/// else is <see cref="TicketCategory.Other"/>) or, with no model or on failure, by keywords;</item>
/// <item>the code-side allowlist <see cref="TicketPolicy"/> decides from the category and the facts. Only the ticket's author is ever
/// touched, only with evidence, at most <see cref="GmTicketAssistantOptions.MaxActionsPerAccountPerDay"/> times a day per account and
/// once per <see cref="GmTicketAssistantOptions.ActionCooldownSeconds"/> for fixes; an action never gives an item, gold, experience,
/// reputation or level and never bans, kicks, mutes or touches an account;</item>
/// <item>the author is answered under <see cref="GmTicketAssistantOptions.AssistantName"/>; a fixed or answered ticket is closed, any
/// other stays open with an internal note (shown by <c>.ticket show</c>) and every outcome goes to the GM audit log with the assistant as actor.</item>
/// </list>
/// Everything that reads or changes a player runs through <see cref="WorldRuntime.InvokeAsync{T}"/> (the world thread, outside the
/// maps' parallel phase); model calls run off it with a timeout. Thread-affinity: public members are safe from any thread.
/// </summary>
public sealed partial class GmTicketAssistant(IServiceProvider services, IConfiguration? configuration = null, TimeProvider? clock = null) : IWorldFeature
{
    /// <summary>Milliseconds between samples of every online player's position and state (the idle, dead and stale-combat clocks).</summary>
    public const int SampleIntervalMs = 5000;

    private const int MaxTicketsPerPass = 10;
    private const int MaxNotesPerTicket = 8;
    private const int MaxTracked = 4096;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly HashSet<int> _handled = [];
    private readonly HashSet<int> _needsHuman = [];
    private readonly Dictionary<int, List<string>> _notes = [];
    private readonly Dictionary<int, (long Day, int Count)> _actionsToday = [];
    private readonly Dictionary<int, long> _lastFixAt = [];
    private readonly Dictionary<ObjectGuid, Watch> _watch = [];
    private TicketEvidenceLog? _evidence;
    private WorldRuntime? _world;
    private GmTicketAssistantOptions _options = new();
    private string? _faq;
    private volatile bool _enabled;
    private int _running;
    private int _sinceSampleMs;
    private int _sincePollMs;
    private int _fixed;
    private int _answered;
    private int _escalated;

    private sealed class Watch(float x, float y, float z, uint map, long now)
    {
        public float X = x, Y = y, Z = z;
        public uint Map = map;
        public long MovedAt = now;
        public long DeadSince;
        public long StaleSince;
    }

    /// <summary>The options in force.</summary>
    public GmTicketAssistantOptions Options => _options;

    /// <summary>Whether tickets are handled now (the configuration, or <see cref="SetEnabled"/>).</summary>
    public bool Enabled => _enabled;

    /// <summary>The recent position anomalies per character (the evidence of the movement fixes).</summary>
    public TicketEvidenceLog Evidence => _evidence ??= new TicketEvidenceLog(_clock);

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    private ILogger Log => services.GetRequiredService<ILoggerFactory>().CreateLogger<GmTicketAssistant>();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _options = GmTicketAssistantOptions.Bind(configuration);
        _enabled = _options.Enabled;
        Evidence.Start();
        world.Updated += OnUpdated;
    }

    public Task StopAsync()
    {
        if (_world is { } world)
        {
            world.Updated -= OnUpdated;
        }

        Evidence.Stop();
        return Task.CompletedTask;
    }

    /// <summary>Turn the assistant on or off until the daemon restarts.</summary>
    public void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>The counters and switches for <c>.ticket assistant status</c>.</summary>
    public TicketAssistantStatus Status()
    {
        lock (_gate)
        {
            return new TicketAssistantStatus(_enabled, _handled.Count, _fixed, _answered, _escalated, _needsHuman.Count,
                _options.UseModel && services.GetService<ITicketAssistantModel>() is { Available: true });
        }
    }

    /// <summary>The internal GM notes of an open ticket (oldest first), for <c>.ticket show</c>; empty when the assistant left none.</summary>
    public IReadOnlyList<string> NotesOf(int ticketId)
    {
        lock (_gate)
        {
            return _notes.TryGetValue(ticketId, out List<string>? notes) ? [.. notes] : [];
        }
    }

    /// <summary>Whether the assistant left this ticket for a human.</summary>
    public bool NeedsHuman(int ticketId)
    {
        lock (_gate)
        {
            return _needsHuman.Contains(ticketId);
        }
    }

    // ---- world-thread tick: sampling and polling ----------------------------------------------------

    private void OnUpdated(uint diffMs)
    {
        if (!_enabled || _world is not { } world)
        {
            return;
        }

        _sinceSampleMs += (int)Math.Min(diffMs, int.MaxValue / 2);
        _sincePollMs += (int)Math.Min(diffMs, int.MaxValue / 2);
        if (_sinceSampleMs >= SampleIntervalMs)
        {
            _sinceSampleMs = 0;
            Sample(world);
        }

        if (_sincePollMs >= _options.PollSeconds * 1000)
        {
            _sincePollMs = 0;
            if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            {
                _ = Task.Run(RunPassAsync);
            }
        }
    }

    private void Sample(WorldRuntime world)
    {
        long now = Now;
        foreach (Player player in world.OnlinePlayers)
        {
            Observe(player, now);
        }

        if (_watch.Count > MaxTracked / 2)
        {
            foreach (ObjectGuid gone in _watch.Keys.Where(guid => !world.IsOnline(guid)).ToArray())
            {
                _watch.Remove(gone);
            }
        }
    }

    /// <summary>World thread: update the player's idle, dead and stale-combat clocks.</summary>
    private Watch Observe(Player player, long now)
    {
        if (!_watch.TryGetValue(player.Guid, out Watch? watch))
        {
            if (_watch.Count >= MaxTracked)
            {
                _watch.Clear();
            }

            _watch[player.Guid] = watch = new Watch(player.X, player.Y, player.Z, player.MapId, now);
        }

        float dx = player.X - watch.X, dy = player.Y - watch.Y, dz = player.Z - watch.Z;
        if (player.MapId != watch.Map || (dx * dx) + (dy * dy) + (dz * dz) > 0.25f)
        {
            watch.X = player.X;
            watch.Y = player.Y;
            watch.Z = player.Z;
            watch.Map = player.MapId;
            watch.MovedAt = now;
        }

        watch.DeadSince = player.IsAlive ? 0 : watch.DeadSince == 0 ? now : watch.DeadSince;
        bool stale = player.Combat.IsInCombat && player.Combat.Attackers.Count == 0 && player.Combat.Victim is null && !player.Combat.HasThreatList;
        watch.StaleSince = stale ? (watch.StaleSince == 0 ? now : watch.StaleSince) : 0;
        return watch;
    }

    // ---- passes ---------------------------------------------------------------------------------

    /// <summary>Handle every open ticket the assistant has not handled and no human has answered, one at a time (at most 10 per pass).</summary>
    public async Task RunPassAsync()
    {
        try
        {
            if (!_enabled)
            {
                return;
            }

            GmAuditFeature audit = services.GetRequiredService<GmAuditFeature>();
            foreach (GmTicketRecord ticket in audit.OpenTickets().Where(IsPending).Take(MaxTicketsPerPass).ToArray())
            {
                if (!_enabled)
                {
                    break;
                }

                try
                {
                    await ProcessTicketAsync(ticket.Id).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
                {
                    // Never retried every pass: a human sees the ticket as it is.
                    FaultLedger.Shared.Record("gm.ticket-assistant", error);
                    Log.LogWarning(error, "GM ticket assistant: ticket {Ticket} failed", ticket.Id);
                    lock (_gate)
                    {
                        _handled.Add(ticket.Id);
                    }
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("gm.ticket-assistant", error);
            Log.LogWarning(error, "GM ticket assistant: a pass failed");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private bool IsPending(GmTicketRecord ticket)
    {
        lock (_gate)
        {
            return ticket.Response.Length == 0 && !_handled.Contains(ticket.Id);
        }
    }

    /// <summary>
    /// Handle one open ticket now. Null when the assistant is off, the ticket is not open, or (unless <paramref name="force"/>) it
    /// was already handled or answered. Serialized: one ticket at a time.
    /// </summary>
    public async Task<TicketOutcome?> ProcessTicketAsync(int ticketId, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_enabled || _world is not { } world)
        {
            return null;
        }

        GmAuditFeature audit = services.GetRequiredService<GmAuditFeature>();
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (audit.OpenTicket(ticketId) is not { } ticket || (!force && !IsPending(ticket)))
            {
                return null;
            }

            TicketClassification classification = await ClassifyAsync(ticket.Text, cancellationToken).ConfigureAwait(false);
            Step step = await world.InvokeAsync(() => Decide(ticket, classification), cancellationToken).ConfigureAwait(false);
            string? answer = null;
            if (step.Decision.Action == TicketAction.Answer)
            {
                answer = await DraftAsync(ticket.Text, cancellationToken).ConfigureAwait(false);
            }

            return await world.InvokeAsync(() => Finish(ticket, classification, step, answer), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    // ---- classification and drafting (off the world thread) -----------------------------------------

    private async Task<TicketClassification> ClassifyAsync(string ticketText, CancellationToken cancellationToken)
    {
        if (_options.UseModel && services.GetService<ITicketAssistantModel>() is { Available: true } model)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.ModelTimeoutSeconds));
            try
            {
                if (await model.ClassifyAsync(ticketText, timeout.Token).ConfigureAwait(false) is { } raw)
                {
                    return TicketClassifier.Parse(raw);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log.LogInformation("GM ticket assistant: the model timed out; classifying by keywords");
            }
            catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
            {
                FaultLedger.Shared.Record("gm.ticket-assistant", error);
                Log.LogWarning("GM ticket assistant: the model failed ({Error}); classifying by keywords", error.GetType().Name);
            }
        }

        return TicketClassifier.ByKeywords(ticketText);
    }

    private async Task<string?> DraftAsync(string ticketText, CancellationToken cancellationToken)
    {
        if (!_options.UseModel || services.GetService<ITicketAssistantModel>() is not { Available: true } model)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ModelTimeoutSeconds));
        try
        {
            string? text = await model.DraftAnswerAsync(ticketText, Faq(), timeout.Token).ConfigureAwait(false);
            // Screened before the 255-character cut of CleanReason, so an overlong draft is refused rather than silently truncated.
            return string.IsNullOrWhiteSpace(text) || ScreenDraft(text.Trim(), Faq()) is not { } screened ? null : AuditCommands.CleanReason(screened);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
        {
            FaultLedger.Shared.Record("gm.ticket-assistant", error);
            return null;
        }
    }

    /// <summary>The longest drafted answer sent to a player (the model is told 200; this is the code-side bound).</summary>
    internal const int MaxDraftLength = 300;

    // A command prefix of the command table ('.' or '!') at the start of a word.
    private static readonly Regex DraftCommandPattern = new(@"(?<![\w.!])[.!][A-Za-z]{2,}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    // A bare domain ("name.example"), which the "://" and "www." checks miss.
    private static readonly Regex DraftDomainPattern = new(@"\b[A-Za-z0-9-]+\.(?:com|net|org|info|biz|io|gg|tv|me|co|cc|ly|to|xyz|top|club|online|site|ru|cn|de|uk|us|eu|fr|nl|pl|tk|ml|ga|cf|gq|app|dev|link|live|shop|store|tech)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// The code-side check of a model-drafted answer, which is the one model-written text a player sees and which closes the ticket
    /// (prompt injection through the ticket text can steer it). Null, so the ticket is left to a GM, when the draft is longer than
    /// <see cref="MaxDraftLength"/>, carries a client escape (<c>|</c>: colour, item or quest link), a link (<c>://</c>, <c>www.</c>), or
    /// a bare domain, or names a dot or bang command the <paramref name="faq"/> does not contain. Pure.
    /// </summary>
    internal static string? ScreenDraft(string draft, string faq)
    {
        if (draft.Length > MaxDraftLength
            || draft.Contains('|', StringComparison.Ordinal)
            || draft.Contains("://", StringComparison.Ordinal)
            || draft.Contains("www.", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            if (DraftDomainPattern.IsMatch(draft))
            {
                return null;
            }

            foreach (Match command in DraftCommandPattern.Matches(draft))
            {
                if (!faq.Contains(command.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return draft;
    }

    private string Faq()
    {
        if (_faq is { } cached)
        {
            return cached;
        }

        string? text = null;
        if (_options.FaqPath.Length > 0)
        {
            try
            {
                text = File.ReadAllText(_options.FaqPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.LogWarning("GM ticket assistant: the FAQ file {Path} cannot be read ({Error}); using the built-in FAQ", _options.FaqPath, error.GetType().Name);
            }
        }

        if (text is null)
        {
            using Stream? stream = typeof(GmTicketAssistant).Assembly.GetManifestResourceStream(GmTicketAssistantOptions.EmbeddedFaq);
            text = stream is null ? string.Empty : new StreamReader(stream).ReadToEnd();
        }

        _faq = text;
        return text;
    }

    // ---- world thread: facts, decision, action --------------------------------------------------------

    private sealed record Step(TicketFacts Facts, TicketDecision Decision, bool Performed, string Detail, string? LimitNote);

    private CharacterIdentity? IdentityOf(GmTicketRecord ticket) => services.GetRequiredService<CharacterDirectory>().Find(ticket.CharacterId);

    private TicketFacts Gather(GmTicketRecord ticket, out Player? player)
    {
        CharacterIdentity? identity = IdentityOf(ticket);
        string name = identity?.Name ?? "#" + ticket.CharacterId.ToString(CultureInfo.InvariantCulture);
        player = identity is null ? null : _world?.FindOnlinePlayer(identity.Name);
        string? quest = QuestNote(ticket.Text, player);
        if (player is null)
        {
            return new TicketFacts(false, identity?.AccountId ?? 0, name, ticket.MapId, ticket.X, ticket.Y, ticket.Z, false, false, identity?.Level ?? 0,
                0, 0, false, false, true, null, quest);
        }

        long now = Now;
        Watch watch = Observe(player, now);
        bool blocked = player.IsGameMaster
            || player.IsQuestSettlementPending
            || player.MapId > 1
            || (player.UnitFlags & UnitFlags.TaxiFlight) != 0
            || BattlegroundManager.TypeOfMap(player.MapId) != BattlegroundType.None;
        bool ghost = (player.Flags & PlayerFlags.Ghost) != 0;
        bool stale = watch.StaleSince != 0 && now - watch.StaleSince >= _options.StaleCombatSeconds;
        TicketSignal? signal = Evidence.Recent(player.Guid.Low, _options.SignalWindowSeconds);
        return new TicketFacts(
            true, player.AccountId, player.Name, player.MapId, player.X, player.Y, player.Z, player.IsAlive, ghost, player.Level,
            watch.DeadSince == 0 ? 0 : now - watch.DeadSince, now - watch.MovedAt, player.Combat.IsInCombat || player.Duel is not null, stale, blocked,
            signal, quest);
    }

    private static readonly Regex QuestIdPattern = new(@"\b\d{1,6}\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>The quest the ticket names (by title or id) and, for an online author, its state in their quest log.</summary>
    private string? QuestNote(string text, Player? player)
    {
        QuestNpcServices? npc = services.GetService<QuestNpcFeature>()?.Services;
        QuestStore? store = npc?.Quests ?? services.GetService<QuestStore>();
        if (store is null || text.Length == 0)
        {
            return null;
        }

        ArcaneCore.Game.Quests.Quest? found = null;
        try
        {
            foreach (Match match in QuestIdPattern.Matches(text))
            {
                if (uint.TryParse(match.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && store.Get(id) is { } byId)
                {
                    found = byId;
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            found = null;
        }

        found ??= store.All
            .Where(q => q.Template.Title.Length >= 6 && text.Contains(q.Template.Title, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(q => q.Template.Title.Length)
            .FirstOrDefault();
        if (found is null)
        {
            return null;
        }

        string label = FormattableString.Invariant($"quest \"{TicketClassifier.Clean(found.Template.Title)}\" (id {found.Id})");
        if (player is null || npc?.StateOf(player) is not { Loaded: true } state)
        {
            return label + " named in the ticket";
        }

        return label + " is " + state.Quests.GetStatus(found.Id) + " in the author's log";
    }

    /// <summary>World thread: facts, the allowlist, the account limits, and the fix itself.</summary>
    private Step Decide(GmTicketRecord ticket, TicketClassification classification)
    {
        TicketFacts facts = Gather(ticket, out Player? player);
        TicketDecision decision = TicketPolicy.Decide(classification.Category, facts, _options);
        string? limit = null;
        if (decision.Action != TicketAction.Escalate)
        {
            limit = LimitOf(facts.AccountId, decision.Action);
            if (limit is not null)
            {
                decision = new TicketDecision(TicketAction.Escalate, string.Empty);
            }
        }

        if (!TicketPolicy.ChangesWorld(decision.Action))
        {
            return new Step(facts, decision, false, string.Empty, limit);
        }

        (bool done, string detail) = player is null ? (false, "author offline") : Perform(decision.Action, player);
        if (done)
        {
            lock (_gate)
            {
                _lastFixAt[facts.AccountId] = Now;
            }
        }

        return new Step(facts, done ? decision : new TicketDecision(TicketAction.Escalate, string.Empty), done, detail, limit);
    }

    /// <summary>Why the account may not get <paramref name="action"/> now (the daily limit, the fix cooldown), or null.</summary>
    private string? LimitOf(int accountId, TicketAction action)
    {
        long now = Now;
        lock (_gate)
        {
            if (_actionsToday.TryGetValue(accountId, out (long Day, int Count) today) && today.Day == now / 86400 && today.Count >= _options.MaxActionsPerAccountPerDay)
            {
                return "daily limit of automatic actions reached";
            }

            if (_options.MaxActionsPerAccountPerDay == 0)
            {
                return "automatic actions are off";
            }

            if (TicketPolicy.ChangesWorld(action) && _lastFixAt.TryGetValue(accountId, out long last) && now - last < _options.ActionCooldownSeconds)
            {
                return "fix cooldown running";
            }
        }

        return null;
    }

    private void CountAction(int accountId)
    {
        long day = Now / 86400;
        lock (_gate)
        {
            (long Day, int Count) today = _actionsToday.TryGetValue(accountId, out (long Day, int Count) known) && known.Day == day ? known : (day, 0);
            _actionsToday[accountId] = (day, today.Count + 1);
        }
    }

    /// <summary>World thread: carry out one allowlisted action on the ticket's author. The result is whether the world was changed.</summary>
    private (bool Done, string Detail) Perform(TicketAction action, Player player)
    {
        if (player.Map is not { } map)
        {
            return (false, "author is not in a map");
        }

        switch (action)
        {
            case TicketAction.Unstuck:
                return UnstuckCommands.TryApply(services, player, out string reply)
                    ? (true, reply.StartsWith(UnstuckCommands.Dead, StringComparison.Ordinal) ? "unstuck: ghost sent to the graveyard" : "unstuck: Undying Soul cast")
                    : (false, "unstuck refused: " + reply);

            case TicketAction.Revive:
                return Revive(player);

            case TicketAction.ClearCombat:
                map.Combat.CombatStop(player);
                return (true, "combat state cleared");

            default:
                return (false, "not a world action");
        }
    }

    /// <summary>
    /// A ghost is resurrected by the spirit healer's rules (<see cref="SpiritHealerResurrection"/> with the real item service, so the 25% durability
    /// loss and the sickness apply), and is then at a graveyard: the corpse's graveyard when it differs from the ghost's (the resurrection's own
    /// step), otherwise the ghost's nearest one, so a ghost that walked back to its body does not come back on the spot.
    /// </summary>
    private (bool Done, string Detail) Revive(Player player)
    {
        if ((player.Flags & PlayerFlags.Ghost) == 0)
        {
            return (false, "not a ghost; release the spirit first");
        }

        var graveyards = services.GetService<GraveyardFeature>()?.Service;
        IItemService? items = services.GetService<QuestNpcFeature>()?.Services.Deps.Items;
        if (graveyards is null || items is null)
        {
            return (false, "no graveyard or item service");
        }

        var spells = services.GetService<SpellFeature>()?.System;
        var spiritHealer = new SpiritHealerResurrection(() => spells, items, p => _world?.SavePlayer(p));
        if (!spiritHealer.ResurrectAtSpiritHealer(player))
        {
            return (false, "resurrection refused");
        }

        // The corpse-graveyard step starts a teleport only when it differs from the ghost's; otherwise start the move here.
        if (services.GetService<TeleportFeature>()?.TeleportsIfAttached is not { } teleports)
        {
            return (true, "resurrected with sickness and durability loss; no teleport service, not moved");
        }

        if (teleports.IsBeingTeleported(player))
        {
            return (true, "resurrected with sickness and durability loss, sent to the corpse's graveyard");
        }

        return graveyards.RepopAtGraveyard(player)
            ? (true, "resurrected with sickness and durability loss, sent to the graveyard")
            : (true, "resurrected with sickness and durability loss; no graveyard could take the player");
    }

    // ---- world thread: answer, close, note, audit ---------------------------------------------------

    private TicketOutcome Finish(GmTicketRecord ticket, TicketClassification classification, Step step, string? answer)
    {
        GmAuditFeature audit = services.GetRequiredService<GmAuditFeature>();
        TicketAction action = step.Decision.Action;
        string name = _options.AssistantName;
        string text;
        bool close;
        if (action == TicketAction.Answer && answer is null)
        {
            action = TicketAction.Escalate;
        }

        switch (action)
        {
            case TicketAction.Answer:
                text = answer!;
                close = true;
                break;
            case TicketAction.Unstuck:
                text = "I saw you have not moved for a while and used the unstuck function for you. " + (step.Facts.Alive ? "You will die when the buff ends (do not enter combat or cast), and can then release to a graveyard." : "You were sent to the nearest graveyard.");
                close = true;
                break;
            case TicketAction.Revive:
                text = (step.Detail.Contains("sent to", StringComparison.Ordinal) ? "I resurrected you and sent you to the graveyard. " : "I resurrected you. ")
                    + "You have resurrection sickness for a while and your gear took damage, as at a spirit healer.";
                close = true;
                break;
            case TicketAction.ClearCombat:
                text = "Your character was stuck in combat with nobody fighting you. I cleared it.";
                close = true;
                break;
            default:
                text = classification.Category == TicketCategory.Harassment
                    ? "Thank you for the report. A GM has been asked to review it; this ticket stays open."
                    : "Your ticket is logged for a GM, who will look at it. This ticket stays open.";
                close = false;
                break;
        }

        bool performed = action == TicketAction.Answer || step.Performed;
        string evidence = action == step.Decision.Action ? step.Decision.Evidence : string.Empty;
        string detail = performed ? step.Detail : step.LimitNote ?? (step.Decision.Action == TicketAction.Answer ? "no answer drafted" : "no evidence or no action allowed");

        // The player's view first, then the record.
        GmTicketRecord? updated = close ? audit.CloseTicket(ticket.Id, name, text) : audit.RespondToTicket(ticket.Id, text);
        if (updated is not null)
        {
            Tell(audit, ticket, name, text, close);
        }

        string summary = classification.Summary.Length == 0 ? string.Empty : $" \"{classification.Summary}\"";
        string line = $"ticket {ticket.Id} from {step.Facts.Name}: {classification.Category} ({classification.Source}){summary} -> {action}"
            + (evidence.Length == 0 ? string.Empty : $" [{evidence}]") + $": {detail}";
        audit.RecordCommand(0, name, $"ticket assistant {action.ToString().ToLowerInvariant()}", line);
        Log.LogInformation("GM ticket assistant: {Line}", line);

        lock (_gate)
        {
            _handled.Add(ticket.Id);
            if (performed)
            {
                if (action == TicketAction.Answer)
                {
                    _answered++;
                }
                else
                {
                    _fixed++;
                }
            }
            else
            {
                _escalated++;
            }

            if (!close)
            {
                _needsHuman.Add(ticket.Id);
                if (!_notes.TryGetValue(ticket.Id, out List<string>? notes))
                {
                    _notes[ticket.Id] = notes = [];
                }

                if (notes.Count < MaxNotesPerTicket)
                {
                    notes.Add($"{name}: {classification.Category}{summary}. Facts: {step.Facts.Describe()}. {detail}.");
                }
            }
            else
            {
                _needsHuman.Remove(ticket.Id);
                _notes.Remove(ticket.Id);
            }
        }

        if (performed)
        {
            CountAction(step.Facts.AccountId);
        }

        return new TicketOutcome(ticket.Id, classification.Category, action, performed, close && updated is not null, detail);
    }

    /// <summary>Tell the author, if online, what <c>.ticket respond</c> / <c>.ticket close</c> tell them.</summary>
    private void Tell(GmAuditFeature audit, GmTicketRecord ticket, string name, string text, bool closed)
    {
        CharacterIdentity? identity = IdentityOf(ticket);
        if (identity is null || _world?.FindOnlinePlayer(identity.Name) is not { } owner)
        {
            return;
        }

        if (closed)
        {
            owner.SendSystemMessage(GmAuditStrings.YourTicketClosed(name));
        }

        owner.SendSystemMessage(GmAuditStrings.TicketAnswer(name, text));
        if (closed)
        {
            owner.Session.Send(WorldOpcode.SmsgGmticketGetticket, MiscPackets.BuildNoGmTicket());
        }
    }
}
