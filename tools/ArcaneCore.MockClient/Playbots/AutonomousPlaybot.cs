using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;

namespace ArcaneCore.MockClient.Playbots;

internal sealed record PlaybotRunOptions(IPEndPoint Realm, string Account, string Password, string Character,
    int Seconds = 120, int Steps = 120, uint AttackEntry = 0, bool Movement = true);
internal sealed record PlaybotStep(int Index, string Action, string Provider, string? Fallback, uint? Health,
    ulong Target, int Frames, long Bytes)
{
    public string DecisionId { get; init; } = string.Empty;
    public StartingZoneFood.FoodEvidence? Food { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PlaybotObject>? CombatObservation { get; init; }
}
internal sealed record PlaybotRunReport(string Outcome, string? Failure, bool EnteredWorld, bool LogoutComplete,
    int Frames, long Bytes, IReadOnlyDictionary<string, int> Replies, IReadOnlyList<PlaybotStep> Steps)
{
    public bool ModelRequested { get; init; }
    public bool ModelWarm { get; init; }
}

/// <summary>A single normal protocol player, with one reader and finite gameplay/traffic budgets.</summary>
internal static class AutonomousPlaybot
{
    internal static void Validate(PlaybotRunOptions options)
    {
        LoopbackOnly.Validate(options.Realm);
        _ = ProtocolPackets.NormalizeAccount(options.Account);
        _ = ProtocolPackets.NormalizePassword(options.Password);
        if (string.IsNullOrWhiteSpace(options.Account) || options.Account.Length > 32 || options.Password.Length == 0
            || options.Character.Length is < 2 or > 12 || !options.Character.All(char.IsAsciiLetter)
            || options.Seconds is < 1 or > 600 || options.Steps is < 1 or > 500)
            throw new ArgumentException("Invalid playbot account, character or finite run budget.");
    }

    internal static async Task<PlaybotRunReport> RunAsync(PlaybotRunOptions options, IPlaybotSelector selector,
        CancellationToken cancellationToken)
    {
        Validate(options);
        List<PlaybotStep> steps = [];
        Dictionary<string, int> replies = [];
        WorldClient? client = null;
        ScenarioConnection? connection = null;
        bool entered = false, loggedOut = false;
        string? failure = null;
        string outcome = "budget-complete";
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        run.CancelAfter(TimeSpan.FromSeconds(options.Seconds));
        CancellationToken ct = run.Token;
        try
        {
            LogonResult logon = await LogonClient.AuthenticateAsync(options.Realm, options.Account, options.Password, ct).ConfigureAwait(false);
            if (logon.Realms.Count == 0) throw new MockProtocolException("No realm advertised.");
            client = await WorldClient.ConnectAsync(logon.Realms[0].GetLoopbackEndpoint(), ct).ConfigureAwait(false);
            if (await client.AuthenticateAsync(options.Account, logon.SessionKey, ct).ConfigureAwait(false) != 0x0C)
                throw new MockProtocolException("World authentication rejected.");
            connection = new ScenarioConnection(client, maximumObjects: 4096, maximumFrames: 10000, maximumBytes: 8 * 1024 * 1024);
            IReadOnlyList<MockCharacter> characters = await connection.EnumerateAsync(ct).ConfigureAwait(false);
            MockCharacter? chosen = LiveSession.SelectCharacter(characters, options.Character, true, out bool create);
            if (create)
            {
                await connection.CreateCharacterAsync(options.Character, ct).ConfigureAwait(false);
                chosen = (await connection.EnumerateAsync(ct).ConfigureAwait(false)).Single(c => c.Name.Equals(options.Character, StringComparison.OrdinalIgnoreCase));
            }
            if (chosen is null || chosen.Race != 1 || chosen.Class != 1)
                throw new MockProtocolException("Initial playbot repertoire requires a Human Warrior.");
            MockLogin login = await connection.LoginAsync(chosen.Guid, ct).ConfigureAwait(false);
            entered = true;
            var origin = new MockPosition(login.Location.X, login.Location.Y, login.Location.Z);
            var position = origin;
            var queried = new HashSet<ulong>();
            var looted = new HashSet<ulong>();
            ulong attackTarget = 0;
            bool released = false, heroicQueued = false;
            int moves = 0;
            int combatDiagnostics = 0;
            uint clientTime = 1;
            StartingZoneLoot.LootWindow? loot = null;
            ulong pendingLoot = 0;
            long pendingLootAt = 0;
            PlaybotCandidate? lootAction = null;
            long lootActionAt = 0;
            bool knowsHeroic = ReadKnownSpells(connection.InitialSpellsPayload).Contains(78);
            var clock = Stopwatch.StartNew();

            PlaybotState State(int revision)
            {
                IReadOnlyDictionary<int, uint> self = connection.FieldsOf(chosen.Guid);
                uint? Field(int field) => self.TryGetValue(field, out uint value) ? value : null;
                uint? flags = Field(UpdateFields.UnitFieldFlags);
                return new(revision, Field(UpdateFields.UnitFieldHealth), Field(UpdateFields.UnitFieldMaxhealth),
                    flags is { } f ? (f & (uint)UnitFlags.InCombat) != 0 : null,
                    position, origin, connection.ObserveObjects(256).Select(o => new PlaybotObject(o.Guid,
                        Read(o.Fields, UpdateFields.ObjectFieldEntry), Read(o.Fields, UpdateFields.UnitFieldHealth),
                        Read(o.Fields, UpdateFields.UnitFieldFlags), Read(o.Fields, UpdateFields.UnitDynamicFlags), o.Position,
                        ReadGuid(o.Fields, UpdateFields.UnitFieldTarget))).ToArray(),
                    queried, looted, attackTarget, loot, pendingLoot != 0 || lootAction is not null, released, heroicQueued, knowsHeroic,
                    Field(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage), moves, chosen.Guid);
            }
            IReadOnlyList<PlaybotCandidate> Choices(PlaybotState state)
            {
                IReadOnlyList<PlaybotCandidate> choices = PlaybotPolicy.Candidates(state, options.AttackEntry, options.Movement);
                if (state.Released || state.AttackTarget != 0 || state.Loot is not null || state.LootPending) return choices;
                PlaybotCandidate? food = PlaybotFood.FindCandidate(state.Revision, chosen.Guid,
                    connection.FieldsOf(chosen.Guid), connection.FieldsOf);
                return food is null ? choices : [food];
            }
            void Observe(WorldFrame frame)
            {
                // Reports retain packet families and numerical facts, never chat or packet bodies.
                if (Enum.IsDefined(typeof(WorldOpcode), frame.Opcode))
                {
                    string name = ((WorldOpcode)frame.Opcode).ToString();
                    replies[name] = replies.GetValueOrDefault(name) + 1;
                }
                if (frame.Opcode == (ushort)WorldOpcode.SmsgLootResponse)
                {
                    StartingZoneLoot.LootWindow window = StartingZoneLoot.ParseWindow(frame.Payload);
                    if (window.Guid != pendingLoot) throw new MockProtocolException("Unexpected loot window.");
                    loot = window;
                    pendingLoot = 0;
                }
                else if (frame.Opcode == (ushort)WorldOpcode.SmsgLootRemoved && loot is { } current)
                {
                    if (frame.Payload.Length != 1) throw new MockProtocolException("Malformed loot removal.");
                    loot = current with { Items = current.Items.Where(i => i.Slot != frame.Payload[0]).ToArray() };
                    if (lootAction?.Kind == PlaybotActionKind.LootItem && lootAction.Target == current.Guid
                        && lootAction.Value == frame.Payload[0]) lootAction = null;
                }
                else if (frame.Opcode == (ushort)WorldOpcode.SmsgLootClearMoney && loot is { } money)
                {
                    if (frame.Payload.Length != 0) throw new MockProtocolException("Malformed money clearance.");
                    loot = money with { Gold = 0 };
                    if (lootAction?.Kind == PlaybotActionKind.LootMoney && lootAction.Target == money.Guid) lootAction = null;
                }
                else if (frame.Opcode == (ushort)WorldOpcode.SmsgLootReleaseResponse)
                {
                    if (frame.Payload.Length != 9 || frame.Payload[8] != 1) throw new MockProtocolException("Malformed loot release.");
                    ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload);
                    if (guid == pendingLoot) pendingLoot = 0;
                    if (loot?.Guid == guid) loot = null;
                    if (lootAction?.Kind == PlaybotActionKind.CloseLoot && lootAction.Target == guid) lootAction = null;
                }
            }
            async Task Drain(TimeSpan window)
            {
                long until = clock.ElapsedMilliseconds + (long)window.TotalMilliseconds;
                while (clock.ElapsedMilliseconds < until)
                {
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    quiet.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, until - clock.ElapsedMilliseconds)));
                    try { await connection.WaitForTrafficAsync(quiet.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
                    // Only the wait is cancelled for quiet. Never cancel a partial frame read for quiet.
                    Observe(await connection.ReadAsync(ct).ConfigureAwait(false));
                }
            }
            for (int index = 0; index < options.Steps; index++)
            {
                ct.ThrowIfCancellationRequested();
                await Drain(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
                if (pendingLoot != 0 && clock.ElapsedMilliseconds - pendingLootAt > 5000)
                    throw new MockProtocolException("Loot open reply deadline exceeded.");
                if (lootAction is not null && clock.ElapsedMilliseconds - lootActionAt > 5000)
                    throw new MockProtocolException("Loot action acknowledgement deadline exceeded.");
                PlaybotState state = State(index);
                var context = new PlaybotDecisionContext(index, state.Health, state.MaximumHealth, state.InCombat,
                    Choices(state));
                PlaybotSelection selection = await selector.SelectAsync(context, ct).ConfigureAwait(false);
                // Drain and revalidate after inference. The model never executes a stale action directly.
                await Drain(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                state = State(index);
                IReadOnlyList<PlaybotCandidate> current = Choices(state);
                PlaybotCandidate action = current.FirstOrDefault(c => c == selection.Candidate)
                    ?? DeterministicPlaybotSelector.Choose(new(index, state.Health, state.MaximumHealth, state.InCombat, current));
                if (action != selection.Candidate) selection = new(action, "deterministic", "stale-action");
                StartingZoneFood.FoodEvidence? foodEvidence = null;
                switch (action.Kind)
                {
                    case PlaybotActionKind.QueryCreature:
                        byte[] query = new byte[12];
                        BinaryPrimitives.WriteUInt32LittleEndian(query, action.Value);
                        BinaryPrimitives.WriteUInt64LittleEndian(query.AsSpan(4), action.Target);
                        await connection.SendAsync(WorldOpcode.CmsgCreatureQuery, query, ct).ConfigureAwait(false);
                        queried.Add(action.Target);
                        break;
                    case PlaybotActionKind.Move:
                        float orientation = MathF.Atan2(action.Y - position.Y, action.X - position.X);
                        clientTime = checked(clientTime + 1000);
                        await connection.SendAsync(WorldOpcode.MsgMoveStop,
                            StartingZoneProbe.Movement(action.X, action.Y, action.Z, orientation, clientTime), ct).ConfigureAwait(false);
                        position = new(action.X, action.Y, action.Z);
                        moves++;
                        break;
                    case PlaybotActionKind.Attack:
                        await connection.SendAsync(WorldOpcode.CmsgAttackswing, ScenarioWire.Guid(action.Target), ct).ConfigureAwait(false);
                        attackTarget = action.Target;
                        heroicQueued = false;
                        break;
                    case PlaybotActionKind.StopAttack:
                        await connection.SendAsync(WorldOpcode.CmsgAttackstop, [], ct).ConfigureAwait(false);
                        attackTarget = 0;
                        break;
                    case PlaybotActionKind.CastKnownSpell:
                        var cast = new PacketWriter(16);
                        cast.WriteUInt32(action.Value);
                        SpellCastTargets.ForUnit(new ObjectGuid(action.Target)).Write(cast);
                        await connection.SendAsync(WorldOpcode.CmsgCastSpell, cast.ToArray(), ct).ConfigureAwait(false);
                        heroicQueued = true;
                        break;
                    case PlaybotActionKind.OpenLoot:
                        await connection.SendAsync(WorldOpcode.CmsgLoot, ScenarioWire.Guid(action.Target), ct).ConfigureAwait(false);
                        pendingLoot = action.Target;
                        pendingLootAt = clock.ElapsedMilliseconds;
                        looted.Add(action.Target);
                        break;
                    case PlaybotActionKind.LootItem:
                        await connection.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [(byte)action.Value], ct).ConfigureAwait(false);
                        break;
                    case PlaybotActionKind.LootMoney:
                        await connection.SendAsync(WorldOpcode.CmsgLootMoney, [], ct).ConfigureAwait(false);
                        break;
                    case PlaybotActionKind.CloseLoot:
                        await connection.SendAsync(WorldOpcode.CmsgLootRelease, ScenarioWire.Guid(action.Target), ct).ConfigureAwait(false);
                        break;
                    case PlaybotActionKind.ReleaseSpirit:
                        await connection.SendAsync(WorldOpcode.CmsgRepopRequest, [], ct).ConfigureAwait(false);
                        released = true;
                        break;
                    case PlaybotActionKind.Eat:
                        // The existing normal item-use proof takes the same single reader while awaited.
                        var foodBudget = new StartingZoneCombat.CombatBudget { FrameObserved = Observe };
                        foodEvidence = await StartingZoneFood.RunAsync(connection, chosen.Guid, foodBudget, ct, emitProgress: false,
                            expectedFood: (action.Target, (byte)action.Value, action.ExpectedStack)).ConfigureAwait(false);
                        break;
                }
                if (action.Kind is PlaybotActionKind.LootItem or PlaybotActionKind.LootMoney or PlaybotActionKind.CloseLoot)
                {
                    lootAction = action;
                    lootActionAt = clock.ElapsedMilliseconds;
                }
                steps.Add(new(index, action.Kind.ToString(), selection.Provider, selection.FallbackReason, state.Health,
                    action.Target, connection.FramesReceived, connection.BytesReceived)
                {
                    DecisionId = action.Id, Food = foodEvidence,
                    CombatObservation = state.InCombat == true && state.AttackTarget == 0 && combatDiagnostics++ < 8
                        ? state.Objects.Where(o => o.Entry == options.AttackEntry).Take(4).ToArray() : null,
                });
                await Drain(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : entered ? "budget-complete" : "failed";
            if (!entered) failure = "login-deadline";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            outcome = "failed";
            failure = error.GetType().Name;
        }
        finally
        {
            if (entered && connection is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await connection.SendAsync(WorldOpcode.CmsgAttackstop, [], cleanup.Token).ConfigureAwait(false);
                    await connection.SendAsync(WorldOpcode.CmsgLogoutRequest, [], cleanup.Token).ConfigureAwait(false);
                    byte[] reply = await LiveSession.ReadLogoutUntilAsync(connection, WorldOpcode.SmsgLogoutResponse, cleanup.Token).ConfigureAwait(false);
                    if (LiveSession.IsSuccessfulLogoutResponse(reply))
                        loggedOut = (await LiveSession.ReadLogoutUntilAsync(connection, WorldOpcode.SmsgLogoutComplete, cleanup.Token).ConfigureAwait(false)).Length == 0;
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException) { }
                if (!loggedOut) { outcome = "failed"; failure ??= "logout-incomplete"; }
            }
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
        return new(outcome, failure, entered, loggedOut, connection?.FramesReceived ?? 0,
            connection?.BytesReceived ?? 0, replies, steps);
    }

    private static uint? Read(IReadOnlyDictionary<int, uint> fields, int index)
        => fields.TryGetValue(index, out uint value) ? value : null;

    internal static ulong? ReadGuid(IReadOnlyDictionary<int, uint> fields, int index)
        => fields.TryGetValue(index, out uint low) && fields.TryGetValue(index + 1, out uint high)
            ? low | ((ulong)high << 32) : null;

    internal static IReadOnlySet<uint> ReadKnownSpells(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload[0] != 0) throw new MockProtocolException("Malformed initial spells.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        int end = checked(3 + count * 4);
        if (end + 2 > payload.Length) throw new MockProtocolException("Truncated initial spell list.");
        int cooldowns = BinaryPrimitives.ReadUInt16LittleEndian(payload[end..]);
        if (end + 2 + cooldowns * 14 != payload.Length) throw new MockProtocolException("Malformed initial cooldown list.");
        HashSet<uint> result = [];
        for (int i = 0; i < count; i++) result.Add(BinaryPrimitives.ReadUInt16LittleEndian(payload[(3 + i * 4)..]));
        return result;
    }
}
