using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Items;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// In-process robustness fuzz of every client-to-server opcode handler in the real opcode table, driven through real world sessions
/// (a logged-in character on a map, a second one on another map, a character-select session). Malformed input may only end in the
/// controlled malformed-packet outcome (<see cref="ArgumentOutOfRangeException"/> with ParamName "packet"), must not corrupt the player
/// and must not stall the world tick. Layouts are learnt from the handler's own reads (<see cref="PacketReadTrace"/>), mutations are
/// deterministic per opcode. Environment: ARCANECORE_HANDLER_FUZZ_ITERATIONS (random cases per opcode, default 8),
/// ARCANECORE_HANDLER_FUZZ_SEED (base seed), ARCANECORE_HANDLER_FUZZ_OPCODE (replay one opcode by wire or enum name). See docs/ops/netguard.md.
/// </summary>
public sealed class OpcodeHandlerFuzzTests(ITestOutputHelper output)
{
    /// <summary>The number of handlers in the real opcode table at the time of writing; lower only when a handler is deliberately removed.</summary>
    private const int KnownHandlerCount = 416;

    [Fact]
    public async Task OpcodeHandlers_RejectMalformedPackets_WithoutCorruptingStateOrStallingTheWorld()
    {
        var run = new HandlerFuzzRun(output, HandlerFuzzSettings.FromEnvironment());
        List<HandlerFuzzFinding> findings = await run.ExecuteAsync(KnownHandlerCount);
        Assert.True(findings.Count == 0, run.FormatFindings());
    }
}

/// <summary>One fuzz run: the host, the three sessions, the case loop, the findings and the report.</summary>
internal sealed class HandlerFuzzRun
{
    private const uint PotionEntry = 910001;
    private const uint SwordEntry = 910002;
    private const uint BagEntry = 910003;
    private const int WorldBatchCases = 128;
    private const int MaxRecoveriesPerOpcode = 3;
    private static readonly TimeSpan WorldBatchBudget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WorldCallDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SessionHandlerDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TickDeadline = TimeSpan.FromSeconds(10);

    private enum Stop { None, Kicked, LeftWorld, Transfer, Dead }

    private readonly ITestOutputHelper _output;
    private readonly HandlerFuzzSettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, HandlerFuzzFinding> _findings = [];
    private readonly List<HandlerFuzzFinding> _order = [];
    private readonly List<HandlerFuzzStats> _stats = [];
    private readonly List<string> _notes = [];
    private readonly Dictionary<WorldOpcode, (int Count, string First)> _unexpectedKicks = [];
    private WorldTestHost _host = null!;
    private Slot _primary = null!;
    private Slot _other = null!;
    private Slot _select = null!;
    private int _counter;
    private long _ticks;
    private bool _hung;
    private int _carryOverRecoveries;
    private int _rawOccurrences;

    public HandlerFuzzRun(ITestOutputHelper output, HandlerFuzzSettings settings)
    {
        _output = output;
        _settings = settings;
    }

    /// <summary>A client connection with its session and, for a character in the world, the player; the drain loop reads and discards its output.</summary>
    private sealed class Slot(WorldTestClient client, WorldSession session, Player? player, ulong characterGuid, int accountId) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _drain = Task.CompletedTask;

        public WorldTestClient Client { get; } = client;

        public WorldSession Session { get; } = session;

        public Player? Player { get; } = player;

        public ulong CharacterGuid { get; } = characterGuid;

        public int AccountId { get; } = accountId;

        public void StartDrain()
        {
            CancellationToken stop = _stop.Token;
            WorldTestClient c = Client;
            _drain = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (c.Available == 0)
                        {
                            await Task.Delay(5, stop);
                            continue;
                        }

                        await c.ReadAsync(TimeSpan.FromSeconds(10));
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or SocketException)
                {
                    // the client was disposed or the server closed the connection: nothing left to drain
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _drain.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // a drain stuck in a read is abandoned: the socket close below ends it
            }

            try
            {
                await Client.DisposeAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException)
            {
                // already closed
            }

            _stop.Dispose();
        }
    }

    // --- run -------------------------------------------------------------------------------

    public async Task<List<HandlerFuzzFinding>> ExecuteAsync(int knownHandlerCount)
    {
        var total = Stopwatch.StartNew();
        var setupWatch = Stopwatch.StartNew();
        var itemContent = new ItemTestContent();
        itemContent.Templates.Templates.Add(new ItemTemplate { Entry = PotionEntry, Class = 0, Name = "Fuzz Potion", DisplayId = 1, Quality = 1, Stackable = 20, BuyPrice = 100, SellPrice = 25 });
        itemContent.Templates.Templates.Add(new ItemTemplate { Entry = SwordEntry, Class = 2, SubClass = 7, Name = "Fuzz Sword", DisplayId = 2, Quality = 1, InventoryType = 13, BuyPrice = 1000, SellPrice = 250, MaxDurability = 40 });
        itemContent.Templates.Templates.Add(new ItemTemplate { Entry = BagEntry, Class = 1, Name = "Fuzz Bag", DisplayId = 3, Quality = 1, InventoryType = 18, ContainerSlots = 6 });
        using (itemContent.Use())
        {
            _host = WorldTestHost.Start();
        }

        _host.World.WorldTick += _ => Interlocked.Increment(ref _ticks);
        OpcodeTable table = _host.Opcodes;
        List<WorldOpcode> opcodes = [.. table.Opcodes.OrderBy(o => (ushort)o)];
        Assert.True(opcodes.Count > 0, "the opcode table is empty");
        Assert.True(opcodes.Count >= knownHandlerCount, $"the opcode table has {opcodes.Count} handlers, fewer than the known {knownHandlerCount}: lower the floor only when a handler is deliberately removed");

        await ProvisionAllAsync();
        setupWatch.Stop();
        (int items, int creatures, string guidNames) = await _host.OnWorldAsync(() =>
        {
            List<(string Name, ulong Value)> g = HandlerFuzzKit.BuildGuidSet(_primary.Player!, _other.Player, _select.CharacterGuid);
            return (_primary.Player!.Inventory.AllItems.Count(), HandlerFuzzKit.NearestCreature(_primary.Player!) is null ? 0 : 1, string.Join(", ", g.Select(x => x.Name)));
        });
        _output.WriteLine($"fixture: primary on map {_primary.Player!.MapId} with {items} item(s), nearest creature {(creatures == 1 ? "found" : "none")}; guid set: {guidNames}");
        _output.WriteLine($"setup: {setupWatch.Elapsed.TotalSeconds:F1} s ({opcodes.Count} handlers in the table; seed {_settings.BaseSeed}, {_settings.RandomIterations} random case(s) per opcode)");

        var worldWatch = new Stopwatch();
        var sessionWatch = new Stopwatch();
        foreach (WorldOpcode opcode in opcodes)
        {
            if (_hung)
            {
                break;
            }

            if (!_settings.Selects(opcode))
            {
                continue;
            }

            Assert.True(table.TryGet(opcode, out OpcodeHandler? handler));
            var stats = new HandlerFuzzStats(opcode, handler.Stripped ? "stripped" : handler.World is not null ? "world" : "session");
            _stats.Add(stats);
            if (handler.Stripped)
            {
                continue; // no handler delegate: the session drops it unread
            }

            var watch = Stopwatch.StartNew();
            await EnsureFleetAsync();
            List<(string Name, ulong Value)> guids = await _host.OnWorldAsync(() => HandlerFuzzKit.BuildGuidSet(_primary.Player!, _other.Player, _select.CharacterGuid));
            var source = new HandlerFuzzCaseSource(opcode, _settings.BaseSeed, _settings.RandomIterations, guids);
            if (handler.World is not null)
            {
                worldWatch.Start();
                await RunWorldOpcodeAsync(handler, source, stats);
                worldWatch.Stop();
            }
            else
            {
                sessionWatch.Start();
                await RunSessionOpcodeAsync(handler, source, stats);
                sessionWatch.Stop();
            }

            stats.Traced = source.Traced;
            stats.Fields = source.Fields.Count;
            stats.Millis = watch.Elapsed.TotalMilliseconds;
            if (stats.Cases == 0 && !_hung)
            {
                AddFinding(opcode, "handler-executed-no-cases", "-", "no mutation ran for this handler (no usable session, or recovery failed before the first case)", "-", 0, []);
            }
        }

        total.Stop();
        PrintSummary(total.Elapsed, setupWatch.Elapsed, worldWatch.Elapsed, sessionWatch.Elapsed, opcodes.Count);
        if (!_hung)
        {
            await DisposeAllAsync();
        }

        // A hung world thread cannot be disposed (World.Stop joins it): the host is abandoned and the process end tears it down.
        return [.. _order];
    }

    private async Task DisposeAllAsync()
    {
        await _primary.DisposeAsync();
        await _other.DisposeAsync();
        await _select.DisposeAsync();
        await _host.DisposeAsync();
    }

    // --- provisioning ----------------------------------------------------------------------

    private async Task ProvisionAllAsync()
    {
        _primary = await EnterWorldAsync(race: 1, map: "primary");
        _other = await EnterWorldAsync(race: 2, map: "other-map");
        _select = await CreateSelectAsync();
    }

    private async Task<Slot> EnterWorldAsync(byte race, string map)
    {
        int n = _counter++;
        string account = $"fuzzacc{n}";
        string character = "Fz" + HandlerFuzzKit.Letters(n);
        WorldTestClient client = await _host.EnterWorldAsync(account, character, AccountSecurity.Player, race);
        Account stored = (await _host.Accounts.FindByUsernameAsync(account))!;
        WorldSession session = _host.Registry.Find(stored.Id)!;
        await _host.WaitForWorldAsync(() => session.Player is { Map: not null }, $"{map} player in a map");
        Player player = session.Player!;
        await _host.OnWorldAsync(() =>
        {
            player.Money = 1_000_000; // 100 gold: economy handlers reach their checks
            if (race == 1)
            {
                // Something to move, sell, split and destroy: a stack, a weapon and a bag.
                player.Inventory.AddItem(PotionEntry, 7, out _);
                player.Inventory.AddItem(SwordEntry, 1, out _);
                player.Inventory.AddItem(BagEntry, 1, out _);
            }

            return 0;
        });
        var slot = new Slot(client, session, player, player.Guid.Value, stored.Id);
        slot.StartDrain();
        return slot;
    }

    private async Task<Slot> CreateSelectAsync()
    {
        int n = _counter++;
        string account = $"fuzzacc{n}";
        string character = "Fz" + HandlerFuzzKit.Letters(n);
        byte[] key = await _host.AddAccountAsync(account, AccountSecurity.Player);
        WorldTestClient client = await _host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(character);
        Account stored = (await _host.Accounts.FindByUsernameAsync(account))!;
        ulong guid = (ulong)(await _host.Characters.GetByAccountAsync(stored.Id)).Single().Id;
        WorldSession session = _host.Registry.Find(stored.Id)!;
        var slot = new Slot(client, session, null, guid, stored.Id);
        slot.StartDrain();
        return slot;
    }

    private async Task ReplacePrimaryAsync()
    {
        Slot old = _primary;
        _primary = await EnterWorldAsync(race: 1, map: "primary");
        await old.DisposeAsync();
    }

    private async Task ReplaceOtherAsync()
    {
        Slot old = _other;
        _other = await EnterWorldAsync(race: 2, map: "other-map");
        await old.DisposeAsync();
    }

    private async Task ReplaceSelectAsync()
    {
        Slot old = _select;
        _select = await CreateSelectAsync();
        await old.DisposeAsync();
    }

    private static bool WorldSlotHealthy(Slot s)
        => s.Player is { } player && s.Session.State == SessionState.InWorld && !HandlerFuzzKit.IsKicked(s.Session)
            && ReferenceEquals(s.Session.Player, player) && player.Map is not null && player.Combat.DeathState == DeathState.Alive && player.Health > 0;

    /// <summary>Before each opcode: replace any session an earlier opcode consumed (counted as carry-over, not against the next opcode's cap).</summary>
    private async Task EnsureFleetAsync()
    {
        bool primaryOk = await _host.OnWorldAsync(() => WorldSlotHealthy(_primary));
        if (!primaryOk)
        {
            _carryOverRecoveries++;
            await ReplacePrimaryAsync();
        }

        bool otherOk = await _host.OnWorldAsync(() => WorldSlotHealthy(_other));
        if (!otherOk)
        {
            _carryOverRecoveries++;
            await ReplaceOtherAsync();
        }

        if (!await SelectHealthyAsync())
        {
            _carryOverRecoveries++;
            await ReplaceSelectAsync();
        }
    }

    private async Task<bool> SelectHealthyAsync()
        => _select.Session.State == SessionState.CharacterSelect && !HandlerFuzzKit.IsKicked(_select.Session)
            && (await _host.Characters.GetByAccountAsync(_select.AccountId)).Count > 0;

    // --- world handlers --------------------------------------------------------------------

    private async Task RunWorldOpcodeAsync(OpcodeHandler handler, HandlerFuzzCaseSource source, HandlerFuzzStats stats)
    {
        bool mayChange = HandlerFuzzKit.MayChangeInventoryOrMoney.Contains(handler.Opcode);
        while (!source.Finished && !stats.RecoveryCapped && !_hung)
        {
            long ticksBefore = Interlocked.Read(ref _ticks);
            Slot slot = _primary;
            Stop stop;
            try
            {
                stop = await _host.OnWorldAsync(() => RunWorldBatch(handler, source, slot, stats, mayChange)).WaitAsync(WorldCallDeadline);
            }
            catch (TimeoutException)
            {
                _hung = true;
                (HandlerFuzzCase? current, int index) = _current;
                AddFinding(handler.Opcode, "handler-hung", "-", $"the world thread did not return within {WorldCallDeadline.TotalSeconds:0} s of a batch", current?.Description ?? "-", index, current?.Payload ?? []);
                return;
            }

            if (!await TickAdvancedAsync(ticksBefore))
            {
                StallFinding(handler.Opcode, _current.Case, _current.Index);
                return;
            }

            if (stop != Stop.None)
            {
                stats.Recoveries++;
                if (stats.Recoveries > MaxRecoveriesPerOpcode)
                {
                    stats.RecoveryCapped = true;
                    return;
                }

                await ReplacePrimaryAsync();
            }
        }
    }

    private (HandlerFuzzCase? Case, int Index) _current;

    private Stop RunWorldBatch(OpcodeHandler handler, HandlerFuzzCaseSource source, Slot slot, HandlerFuzzStats stats, bool mayChange)
    {
        WorldSession session = slot.Session;
        Player player = slot.Player!;
        var sink = new List<PacketReadEvent>(32);
        var breaks = new List<HandlerFuzzBreak>();
        long started = Stopwatch.GetTimestamp();
        for (int ran = 0; ran < WorldBatchCases && Stopwatch.GetElapsedTime(started) < WorldBatchBudget; ran++)
        {
            if (!source.TryNext(out HandlerFuzzCase fuzzCase))
            {
                break;
            }

            int index = source.CaseCount - 1;
            _current = (fuzzCase, index);
            HandlerFuzzInventory before = HandlerFuzzInventory.Take(player);
            sink.Clear();
            Exception? thrown = null;
            using (PacketReadTrace.Begin(sink))
            {
                try
                {
                    handler.World!(session, player, fuzzCase.Payload);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    thrown = ex;
                }
            }

            stats.Cases++;
            if (thrown is not null)
            {
                RecordException(handler.Opcode, fuzzCase, index, thrown);
            }

            source.Report(sink, thrown);

            Stop stop = Stop.None;
            if (HandlerFuzzKit.IsKicked(session))
            {
                NoteKick(stats, fuzzCase, session);
                stop = Stop.Kicked;
            }
            else if (session.State != SessionState.InWorld || !ReferenceEquals(session.Player, player))
            {
                stop = Stop.LeftWorld;
            }
            else if (player.Map is null)
            {
                stop = CompleteTransfer(session, player, stats, index);
            }

            breaks.Clear();
            HandlerFuzzKit.CheckPlayer(player, before, mayChange, mapChecks: stop == Stop.None, breaks);
            foreach (HandlerFuzzBreak broken in breaks)
            {
                AddFinding(handler.Opcode, broken.Name, "-", broken.Detail, fuzzCase.Description, index, fuzzCase.Payload);
            }

            if (stop == Stop.None && player.Combat.DeathState != DeathState.Alive)
            {
                stop = Stop.Dead;
            }

            if (stop != Stop.None)
            {
                return stop;
            }
        }

        return Stop.None;
    }

    /// <summary>The player is between SMSG_NEW_WORLD and the client's ack: send the empty MSG_MOVE_WORLDPORT_ACK as the client does.</summary>
    private Stop CompleteTransfer(WorldSession session, Player player, HandlerFuzzStats stats, int index)
    {
        if (_host.Opcodes.TryGet(WorldOpcode.MsgMoveWorldportAck, out OpcodeHandler? ack))
        {
            try
            {
                ack.World!(session, player, []);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecordException(WorldOpcode.MsgMoveWorldportAck, new HandlerFuzzCase("transfer completion after " + WorldOpcodeNames.GetName(stats.Opcode), []), index, ex);
            }
        }

        if (player.Map is null)
        {
            stats.TransfersStuck++;
            lock (_gate)
            {
                _notes.Add($"{WorldOpcodeNames.GetName(stats.Opcode)}: player stuck in a transfer after case {index}; session replaced");
            }

            return Stop.Transfer;
        }

        stats.TransfersCompleted++;
        return Stop.None;
    }

    private void NoteKick(HandlerFuzzStats stats, HandlerFuzzCase fuzzCase, WorldSession session)
    {
        stats.Kicks++;
        string first = $"{fuzzCase.Description} (reason: {session.KickReason ?? "none recorded"}, state {session.State})";
        stats.FirstKick ??= first;
        if (!HandlerFuzzKit.KickIsExpected(stats.Opcode))
        {
            lock (_gate)
            {
                _unexpectedKicks[stats.Opcode] = _unexpectedKicks.TryGetValue(stats.Opcode, out var seen) ? (seen.Count + 1, seen.First) : (1, first);
            }
        }
    }

    private async Task<bool> TickAdvancedAsync(long before)
    {
        long started = Stopwatch.GetTimestamp();
        while (Interlocked.Read(ref _ticks) <= before)
        {
            if (Stopwatch.GetElapsedTime(started) > TickDeadline)
            {
                return false;
            }

            await Task.Delay(2);
        }

        return true;
    }

    // --- session handlers ------------------------------------------------------------------

    private async Task RunSessionOpcodeAsync(OpcodeHandler handler, HandlerFuzzCaseSource source, HandlerFuzzStats stats)
    {
        bool inWorld = handler.AllowsState(SessionState.InWorld);
        if (!inWorld && !handler.AllowsState(SessionState.CharacterSelect))
        {
            AddFinding(handler.Opcode, "no-session-for-allowed-states", "-", $"allowed states {handler.AllowedStates} include neither InWorld nor CharacterSelect: the harness has no session to run it on", "-", 0, []);
            return;
        }

        bool mayChange = HandlerFuzzKit.MayChangeInventoryOrMoney.Contains(handler.Opcode);
        var sink = new List<PacketReadEvent>(32);
        var breaks = new List<HandlerFuzzBreak>();
        HandlerFuzzInventory? known = null; // the in-world player's inventory as of the last world check: the next case's "before"
        while (!_hung && !stats.RecoveryCapped && source.TryNext(out HandlerFuzzCase fuzzCase))
        {
            int index = source.CaseCount - 1;
            Slot slot = inWorld ? _primary : _select;
            if (inWorld && known is null)
            {
                (bool ok, HandlerFuzzInventory? first) = await WorldCallAsync(handler.Opcode, fuzzCase, index, () => HandlerFuzzInventory.Take(slot.Player!));
                if (!ok)
                {
                    return;
                }

                known = first;
            }

            sink.Clear();
            Exception? thrown = null;
            Task task = Task.CompletedTask;
            using (PacketReadTrace.Begin(sink))
            {
                try
                {
                    task = handler.Session!(slot.Session, fuzzCase.Payload);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    thrown = ex;
                }
            }

            bool hung = false;
            if (thrown is null)
            {
                try
                {
                    await task.WaitAsync(SessionHandlerDeadline);
                }
                catch (TimeoutException)
                {
                    hung = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    thrown = ex;
                }
            }

            stats.Cases++;
            if (hung)
            {
                AddFinding(handler.Opcode, "handler-hung", "-", $"the session handler did not complete within {SessionHandlerDeadline.TotalSeconds:0} s", fuzzCase.Description, index, fuzzCase.Payload);
            }
            else if (thrown is not null)
            {
                RecordException(handler.Opcode, fuzzCase, index, thrown);
            }

            source.Report(sink, thrown);

            bool consumed;
            if (inWorld)
            {
                // One world round trip per case: it checks the invariants and proves the tick is alive (it only returns from a tick).
                HandlerFuzzInventory before = known!;
                breaks.Clear();
                (bool ok, (bool gone, bool kicked, HandlerFuzzInventory after) check) = await WorldCallAsync(handler.Opcode, fuzzCase, index, () =>
                {
                    bool k = HandlerFuzzKit.IsKicked(slot.Session);
                    bool g = !WorldSlotHealthy(slot);
                    HandlerFuzzInventory a = HandlerFuzzKit.CheckPlayer(slot.Player!, before, mayChange, mapChecks: !k && slot.Player!.Map is not null, breaks);
                    return (g, k, a);
                });
                if (!ok)
                {
                    return;
                }

                foreach (HandlerFuzzBreak broken in breaks)
                {
                    AddFinding(handler.Opcode, broken.Name, "-", broken.Detail, fuzzCase.Description, index, fuzzCase.Payload);
                }

                if (check.kicked)
                {
                    NoteKick(stats, fuzzCase, slot.Session);
                }

                consumed = check.gone;
                known = check.after;
            }
            else
            {
                bool kicked = HandlerFuzzKit.IsKicked(slot.Session);
                if (kicked)
                {
                    NoteKick(stats, fuzzCase, slot.Session);
                }

                consumed = kicked || !await SelectHealthyAsync();
                if (!consumed && stats.Cases % 32 == 0 && !await TickAdvancedAsync(Interlocked.Read(ref _ticks)))
                {
                    StallFinding(handler.Opcode, fuzzCase, index);
                    return;
                }
            }

            if (consumed || hung)
            {
                stats.Recoveries++;
                if (stats.Recoveries > MaxRecoveriesPerOpcode)
                {
                    stats.RecoveryCapped = true;
                    return;
                }

                if (inWorld)
                {
                    await ReplacePrimaryAsync();
                    known = null;
                }
                else
                {
                    await ReplaceSelectAsync();
                }
            }
        }

        if (!_hung && !await TickAdvancedAsync(Interlocked.Read(ref _ticks)))
        {
            StallFinding(handler.Opcode, null, source.CaseCount - 1);
        }
    }

    /// <summary>A bounded call onto the world thread from the session path; a world that does not answer within the tick deadline is a finding.</summary>
    private async Task<(bool Ok, T Value)> WorldCallAsync<T>(WorldOpcode opcode, HandlerFuzzCase fuzzCase, int index, Func<T> call)
    {
        try
        {
            return (true, await _host.OnWorldAsync(call).WaitAsync(TickDeadline));
        }
        catch (TimeoutException)
        {
            StallFinding(opcode, fuzzCase, index);
            return (false, default!);
        }
    }

    private void StallFinding(WorldOpcode opcode, HandlerFuzzCase? fuzzCase, int index)
    {
        _hung = true;
        AddFinding(opcode, "world-tick-stalled", "-", $"world tick stalled after {WorldOpcodeNames.GetName(opcode)}: no tick within {TickDeadline.TotalSeconds:0} s", fuzzCase?.Description ?? "-", index, fuzzCase?.Payload ?? []);
    }

    // --- findings --------------------------------------------------------------------------

    private void RecordException(WorldOpcode opcode, HandlerFuzzCase fuzzCase, int index, Exception exception)
    {
        if (HandlerFuzzKit.IsControlledMalformed(exception))
        {
            return;
        }

        string kind = exception is ArgumentOutOfRangeException a
            ? $"ArgumentOutOfRangeException (ParamName={a.ParamName ?? "<null>"}, not the packet reader's)"
            : exception.GetType().FullName ?? exception.GetType().Name;
        AddFinding(opcode, kind, HandlerFuzzKit.FirstSourceFrame(exception), HandlerFuzzKit.TopFrames(exception, 5), fuzzCase.Description, index, fuzzCase.Payload);
    }

    private void AddFinding(WorldOpcode opcode, string kind, string frame, string detail, string mutation, int index, byte[] payload)
    {
        lock (_gate)
        {
            _rawOccurrences++;
            var finding = new HandlerFuzzFinding
            {
                Opcode = opcode, Kind = kind, Frame = frame, Detail = detail, Mutation = mutation, CaseIndex = index, Payload = payload,
            };
            if (_findings.TryGetValue(finding.Key, out HandlerFuzzFinding? existing))
            {
                existing.Occurrences++;
                return;
            }

            _findings.Add(finding.Key, finding);
            _order.Add(finding);
        }
    }

    public string FormatFindings()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{_order.Count} distinct finding(s) ({_rawOccurrences} occurrence(s)); base seed {_settings.BaseSeed}; replay one with ARCANECORE_HANDLER_FUZZ_OPCODE=<name> ARCANECORE_HANDLER_FUZZ_SEED={_settings.BaseSeed}");
        int n = 0;
        foreach (HandlerFuzzFinding f in _order)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"[{++n}] {WorldOpcodeNames.GetName(f.Opcode)} ({(ushort)f.Opcode}) :: {f.Kind}  (x{f.Occurrences})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    mutation: {f.Mutation}; seed {_settings.BaseSeed}; case {f.CaseIndex} of this opcode");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    first src frame: {f.Frame}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    payload ({f.Payload.Length} bytes): {HandlerFuzzKit.PayloadLiteral(f.Payload)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {f.Detail}");
        }

        return sb.ToString();
    }

    private void PrintSummary(TimeSpan total, TimeSpan setup, TimeSpan world, TimeSpan session, int tableCount)
    {
        int stripped = _stats.Count(s => s.Kind == "stripped");
        int invoked = _stats.Count(s => s.Kind != "stripped" && s.Cases > 0);
        int traced = _stats.Count(s => s.Kind != "stripped" && s.Cases > 0 && s.Traced);
        int byteLevel = invoked - traced;
        _output.WriteLine($"coverage: {tableCount} handlers in table, {_stats.Count} selected, {invoked} invoked ({_stats.Count(s => s.Kind == "world" && s.Cases > 0)} world, {_stats.Count(s => s.Kind == "session" && s.Cases > 0)} session), {stripped} stripped (counted, not invoked), {traced} traced, {byteLevel} byte-level");
        _output.WriteLine($"cases executed: {_stats.Sum(s => s.Cases)}; kicks: {_stats.Sum(s => s.Kicks)}; recoveries: {_stats.Sum(s => s.Recoveries)} (+{_carryOverRecoveries} carry-over); transfers completed/stuck: {_stats.Sum(s => s.TransfersCompleted)}/{_stats.Sum(s => s.TransfersStuck)}; ticks seen: {Interlocked.Read(ref _ticks)}");
        _output.WriteLine($"findings: {_order.Count} distinct ({_rawOccurrences} occurrences); wall {total.TotalSeconds:F1} s (setup {setup.TotalSeconds:F1} s, world handlers {world.TotalSeconds:F1} s, session handlers {session.TotalSeconds:F1} s)");
        foreach (HandlerFuzzStats s in _stats)
        {
            string flags = (s.Kicks > 0 ? $" kicks={s.Kicks}" : string.Empty) + (s.Recoveries > 0 ? $" recoveries={s.Recoveries}" : string.Empty)
                + (s.RecoveryCapped ? " RECOVERY-CAPPED" : string.Empty) + (s.TransfersCompleted > 0 ? $" transfers={s.TransfersCompleted}" : string.Empty)
                + (s.TransfersStuck > 0 ? $" transfers-stuck={s.TransfersStuck}" : string.Empty);
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {WorldOpcodeNames.GetName(s.Opcode),-40} {(ushort)s.Opcode,4} {s.Kind,-8} {(s.Kind == "stripped" ? "-" : s.Traced ? $"traced/{s.Fields}f" : "byte-level"),-12} cases={s.Cases,5} {s.Millis,7:F0} ms{flags}"));
        }

        if (_unexpectedKicks.Count > 0)
        {
            _output.WriteLine("kicks on opcodes that are not movement / anti-cheat / logout (a kick is the defined outcome for cheat-like input; listed, not findings):");
            foreach ((WorldOpcode opcode, (int count, string first)) in _unexpectedKicks.OrderBy(k => (ushort)k.Key))
            {
                _output.WriteLine($"  {WorldOpcodeNames.GetName(opcode)}: {count} kick(s), first on '{first}'");
            }
        }

        foreach (string note in _notes)
        {
            _output.WriteLine("note: " + note);
        }

        _output.WriteLine(FormatFindings());
    }
}
