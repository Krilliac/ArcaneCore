using System.Buffers.Binary;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using ArcaneCore.Game;
using ArcaneCore.Game.Quests;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>Finite, loopback-only starting-zone probe for a disposable private profile.</summary>
internal static class StartingZoneProbe
{
    internal const uint NpcEntry = 197;
    internal const uint NpcSpawn = 79970;
    internal const uint PrerequisiteNpcEntry = 823;
    internal const uint PrerequisiteNpcSpawn = 79942;
    internal const uint PrerequisiteQuest = 783;
    internal const uint Quest = 7;
    internal const uint LearnedSpell = 2457;
    internal const string DefaultCharacter = "Arcseed";

    internal sealed record Options(IPEndPoint Realm, string Account, string Character, string PasswordEnvironment)
    {
        internal uint? CombatSpawn { get; init; }
        internal (float X, float Y, float Z)? CombatPosition { get; init; }
        internal bool Loot { get; init; }
        internal bool Heroic { get; init; }
        internal bool CompleteQuest { get; init; }
    }

    public static async Task<int> MainAsync(string[] args, TextWriter output, TextWriter error)
    {
        int deadlineSeconds = 120;
        var elapsed = Stopwatch.StartNew();
        CancellationToken scenarioToken = default;
        try
        {
            Options options = Parse(args);
            deadlineSeconds = options.CompleteQuest ? 900 : options.CombatSpawn is not null ? 180 : 120;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(deadlineSeconds));
            scenarioToken = deadline.Token;
            await RunAsync(options, output, deadline.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            string origin = scenarioToken.IsCancellationRequested ? "scenario-deadline" : "nested-or-transport";
            error.WriteLine($"starting-zone probe cancelled: origin={origin}; elapsedMs={elapsed.ElapsedMilliseconds}; "
                + $"scenarioDeadlineSeconds={deadlineSeconds}");
            return 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error.WriteLine($"starting-zone probe failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    internal static Options Parse(string[] args)
    {
        string realm = "127.0.0.1:3724", account = "ARCPLAY", character = DefaultCharacter, passwordEnvironment = "ARCANE_ACCOUNT_PASSWORD";
        uint? combatSpawn = null;
        (float X, float Y, float Z)? combatPosition = null;
        bool loot = false;
        bool heroic = false;
        bool completeQuest = false;
        for (int i = 0; i < args.Length; i++)
        {
            string value = i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i - 1])
            {
                case "--realm": realm = value; break;
                case "--account": account = value; break;
                case "--character": character = value; break;
                case "--password-env": passwordEnvironment = value; break;
                case "--loot":
                    if (!bool.TryParse(value, out loot))
                        throw new ArgumentException("--loot must be true or false.");
                    break;
                case "--heroic":
                    if (!bool.TryParse(value, out heroic))
                        throw new ArgumentException("--heroic must be true or false.");
                    break;
                case "--complete-quest":
                    if (!bool.TryParse(value, out completeQuest))
                        throw new ArgumentException("--complete-quest must be true or false.");
                    break;
                case "--combat-spawn":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint spawn))
                        throw new ArgumentException("--combat-spawn must be an unsigned spawn id.");
                    combatSpawn = spawn;
                    break;
                case "--combat-position":
                    string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length != 3
                        || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                        || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                        || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                        throw new ArgumentException("--combat-position must be finite x,y,z coordinates.");
                    combatPosition = (x, y, z);
                    break;
                default: throw new ArgumentException($"unknown argument '{args[i - 1]}'.");
            }
        }

        if (!IPEndPoint.TryParse(realm, out IPEndPoint? endpoint) || !IPAddress.IsLoopback(endpoint.Address))
            throw new ArgumentException("--realm must be a numeric loopback endpoint.");
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(passwordEnvironment))
            throw new ArgumentException("account, character and password environment name are required.");
        if (Environment.GetEnvironmentVariable(passwordEnvironment) is not { Length: > 0 })
            throw new ArgumentException($"password environment variable '{passwordEnvironment}' is empty or unset.");
        if ((combatSpawn is null) != (combatPosition is null))
            throw new ArgumentException("--combat-spawn and --combat-position must be supplied together.");
        if (loot && combatSpawn is null)
            throw new ArgumentException("--loot true requires a configured combat spawn and position.");
        if (heroic && combatSpawn is null)
            throw new ArgumentException("--heroic true requires a configured combat spawn and position.");
        if (completeQuest && combatSpawn is null)
            throw new ArgumentException("--complete-quest true requires a configured combat spawn and position.");
        if (completeQuest && loot)
            throw new ArgumentException("--loot true cannot be combined with --complete-quest true; the campaign returns to the questgiver before reward turn-in.");
        return new(endpoint, account, character, passwordEnvironment)
        {
            CombatSpawn = combatSpawn,
            CombatPosition = combatPosition,
            Loot = loot,
            Heroic = heroic,
            CompleteQuest = completeQuest,
        };
    }

    internal static byte[] Movement(float x, float y, float z, float orientation, uint clientTime)
    {
        byte[] body = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), clientTime);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8), x);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(12), y);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(16), z);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(20), orientation);
        return body;
    }

    internal static bool IsLogoutSuccess(ReadOnlySpan<byte> payload)
        => payload.Length == 5 && BinaryPrimitives.ReadUInt32LittleEndian(payload) == 0 && payload[4] <= 1;

    private static async Task RunAsync(Options options, TextWriter output, CancellationToken ct)
    {
        string password = Environment.GetEnvironmentVariable(options.PasswordEnvironment)!;
        LogonResult logon = await LogonClient.AuthenticateAsync(options.Realm, options.Account, password, ct).ConfigureAwait(false);
        await using WorldClient client = await WorldClient.ConnectAsync(logon.Realms[0].GetLoopbackEndpoint(), ct).ConfigureAwait(false);
        if (await client.AuthenticateAsync(options.Account, logon.SessionKey, ct).ConfigureAwait(false) != 0x0C)
            throw new MockProtocolException("world authentication was rejected");

        var connection = new ScenarioConnection(client);
        IReadOnlyList<MockCharacter> characters = await connection.EnumerateAsync(ct).ConfigureAwait(false);
        MockCharacter? character = characters.FirstOrDefault(c => c.Name.Equals(options.Character, StringComparison.OrdinalIgnoreCase));
        if (character is null)
        {
            await connection.CreateCharacterAsync(options.Character, ct).ConfigureAwait(false);
            character = (await connection.EnumerateAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => c.Name.Equals(options.Character, StringComparison.OrdinalIgnoreCase))
                ?? throw new MockProtocolException("created character was absent from re-enumeration");
        }

        if (character is not { Race: 1, Class: 1, Map: 0 })
            throw new MockProtocolException("selected character is not the expected Human Warrior on map 0");

        MockLogin login = await connection.LoginAsync(character.Guid, ct).ConfigureAwait(false);
        float sx = login.Self.X, sy = login.Self.Y, sz = login.Self.Z;
        uint movementTime = 0;
        if (!float.IsFinite(sx) || !float.IsFinite(sy) || !float.IsFinite(sz)
            || MathF.Abs(sx + 8949.95f) > 20 || MathF.Abs(sy + 132.493f) > 20 || MathF.Abs(sz - 83.5f) > 20)
            throw new MockProtocolException("probe character is outside the bounded Human starting area");
        if (HasQuest(connection.FieldsOf(character.Guid), PrerequisiteQuest) || HasQuest(connection.FieldsOf(character.Guid), Quest))
            throw new MockProtocolException("probe prerequisite or target quest is already in the selected character's journal");
        ulong npc = ObjectGuid.WithEntry(HighGuid.Unit, NpcEntry, NpcSpawn).Value;
        ulong prerequisiteNpc = ObjectGuid.WithEntry(HighGuid.Unit, PrerequisiteNpcEntry, PrerequisiteNpcSpawn).Value;
        await ObserveNpcAsync(connection, npc, NpcEntry, ct).ConfigureAwait(false);
        await ObserveNpcAsync(connection, prerequisiteNpc, PrerequisiteNpcEntry, ct).ConfigureAwait(false);

        await connection.SendAsync(WorldOpcode.CmsgCastSpell, CastSelf(LearnedSpell), ct).ConfigureAwait(false);
        bool spellGo = false;
        var castBudget = new PacketBudget();
        for (int i = 0; i < 128; i++)
        {
            WorldFrame frame = await castBudget.ReadAsync(connection, ct).ConfigureAwait(false);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgSpellGo)
            {
                spellGo = IsSelfSpellGo(frame.Payload, character.Guid, LearnedSpell);
                if (spellGo) break;
            }
            if (frame.Opcode == (ushort)WorldOpcode.SmsgCastResult && frame.Payload.Length >= 5
                && BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload) == LearnedSpell && frame.Payload[4] != 0)
                throw new MockProtocolException($"self cast {LearnedSpell} was rejected");
        }
        if (!spellGo) throw new MockProtocolException($"self cast {LearnedSpell} did not produce matching SMSG_SPELL_GO");
        for (int i = 0; !IsBattleStance(connection.FieldsOf(character.Guid)) && i < 128; i++)
            await castBudget.ReadAsync(connection, ct).ConfigureAwait(false);
        if (!IsBattleStance(connection.FieldsOf(character.Guid)))
            throw new MockProtocolException("accepted self cast did not establish Battle Stance");

        movementTime = await WalkToAsync(connection, sx, sy, sz, -8933.540f, -136.523f, 83.447f, movementTime, ct).ConfigureAwait(false);
        await AcceptQuestAsync(connection, character.Guid, prerequisiteNpc, PrerequisiteQuest, expectedComplete: true, ct).ConfigureAwait(false);
        movementTime = await WalkToAsync(connection, -8933.540f, -136.523f, 83.447f, -8902.590f, -162.606f, 82.022f, movementTime, ct).ConfigureAwait(false);
        await CompleteNoObjectiveQuestAsync(connection, character.Guid, npc, PrerequisiteQuest, expectedExperience: 40, ct).ConfigureAwait(false);
        if (HasQuest(connection.FieldsOf(character.Guid), PrerequisiteQuest))
            throw new MockProtocolException("prerequisite quest remained in the player journal after reward");

        await connection.SendAsync(WorldOpcode.CmsgQuestQuery, ScenarioWire.UInt32(Quest), ct).ConfigureAwait(false);
        MockQuestQuery query = ScenarioWire.QuestQuery(await ReadUntilAsync(connection, WorldOpcode.SmsgQuestQueryResponse, ct).ConfigureAwait(false));
        if (query.Id != Quest) throw new MockProtocolException($"quest query returned {query.Id}, expected {Quest}");
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, ScenarioWire.Guid(npc), ct).ConfigureAwait(false);
        MockQuestgiverStatus status = ScenarioWire.QuestgiverStatus(await ReadUntilAsync(connection, WorldOpcode.SmsgQuestgiverStatus, ct).ConfigureAwait(false));
        if (status.Guid != npc) throw new MockProtocolException("questgiver status returned a different GUID");
        await AcceptQuestAsync(connection, character.Guid, npc, Quest, expectedComplete: false, ct).ConfigureAwait(false);
        if (options.CombatSpawn is { } combatSpawn && options.CombatPosition is { } combatPosition)
        {
            StartingZoneCombat.CombatEvidence evidence;
            StartingZoneCampaign.CampaignEvidence? campaignEvidence = null;
            if (options.CompleteQuest)
            {
                campaignEvidence = await StartingZoneCampaign.RunAsync(connection, character.Guid, Quest, combatSpawn,
                    combatPosition, -8902.590f, -162.606f, 82.022f, movementTime, options.Heroic, ct).ConfigureAwait(false);
                evidence = campaignEvidence.Kills[^1];
                output.WriteLine("starting-zone campaign=" + System.Text.Json.JsonSerializer.Serialize(campaignEvidence));
            }
            else
            {
                evidence = await StartingZoneCombat.RunAsync(
                    connection, character.Guid, Quest, combatSpawn, combatPosition,
                    -8902.590f, -162.606f, 82.022f, movementTime, ct, options.Heroic).ConfigureAwait(false);
            }
            output.WriteLine("starting-zone combat=" + System.Text.Json.JsonSerializer.Serialize(evidence));
            if (options.Loot)
            {
                StartingZoneLoot.LootEvidence lootEvidence = await StartingZoneLoot.RunAsync(
                    connection, character.Guid, evidence.Target,
                    (evidence.PlayerX, evidence.PlayerY, evidence.PlayerZ), evidence.ClientTime, ct).ConfigureAwait(false);
                output.WriteLine("starting-zone loot=" + System.Text.Json.JsonSerializer.Serialize(lootEvidence));
            }
            if (options.CompleteQuest)
            {
                await LogoutAsync(connection, ct).ConfigureAwait(false);
                await VerifyQuest7RelogAsync(options, character.Name, character.Guid, output, ct).ConfigureAwait(false);
                return;
            }
        }
        output.WriteLine($"starting-zone observed npcEntry={NpcEntry} npcSpawn={NpcSpawn} quest={Quest} start=({sx.ToString(CultureInfo.InvariantCulture)},{sy.ToString(CultureInfo.InvariantCulture)},{sz.ToString(CultureInfo.InvariantCulture)}) target=(-8902.590,-162.606,82.022) outcome=incomplete-observed");
        await LogoutAsync(connection, ct).ConfigureAwait(false);
    }

    private static async Task<uint> WalkToAsync(ScenarioConnection connection, float sx, float sy, float sz,
        float tx, float ty, float tz, uint clientTime, CancellationToken ct)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(MathF.Sqrt(MathF.Pow(tx - sx, 2) + MathF.Pow(ty - sy, 2) + MathF.Pow(tz - sz, 2)) / 7f));
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            await Task.Delay(1000, ct).ConfigureAwait(false);
            clientTime = checked(clientTime + 1000);
            await connection.SendAsync(WorldOpcode.MsgMoveStop,
                Movement(sx + ((tx - sx) * t), sy + ((ty - sy) * t), sz + ((tz - sz) * t), 0, clientTime), ct).ConfigureAwait(false);
        }

        return clientTime;
    }

    private static async Task AcceptQuestAsync(ScenarioConnection connection, ulong character, ulong npc, uint quest,
        bool expectedComplete, CancellationToken ct)
    {
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest, ScenarioWire.GuidQuest(npc, quest), ct).ConfigureAwait(false);
        MockQuestDetails details = ScenarioWire.QuestDetails(await ReadUntilAsync(connection, WorldOpcode.SmsgQuestgiverQuestDetails, ct).ConfigureAwait(false));
        if (details.Guid != npc || details.QuestId != quest)
            throw new MockProtocolException($"quest details mismatch for prerequisite {quest}");
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, ScenarioWire.GuidQuest(npc, quest), ct).ConfigureAwait(false);
        await AwaitJournalStateAsync(connection, character, quest, expectedComplete, ct).ConfigureAwait(false);
    }

    private static async Task AwaitJournalStateAsync(ScenarioConnection connection, ulong character, uint quest,
        bool expectedComplete, CancellationToken ct)
    {
        var budget = new PacketBudget();
        for (int i = 0; i < 128; i++)
        {
            bool state = expectedComplete ? IsQuestComplete(connection.FieldsOf(character), quest) : IsQuestIncomplete(connection.FieldsOf(character), quest);
            if (state) return;
            WorldFrame frame = await budget.ReadAsync(connection, ct).ConfigureAwait(false);
            if (frame.Opcode is (ushort)WorldOpcode.SmsgQuestgiverQuestInvalid or (ushort)WorldOpcode.SmsgQuestgiverQuestFailed or (ushort)WorldOpcode.SmsgQuestlogFull)
                throw new MockProtocolException(QuestRefusal((WorldOpcode)frame.Opcode, frame.Payload, quest));
        }

        if (expectedComplete ? IsQuestComplete(connection.FieldsOf(character), quest) : IsQuestIncomplete(connection.FieldsOf(character), quest))
            return;

        throw new MockProtocolException($"quest {quest} did not reach expected journal state complete={expectedComplete}");
    }

    internal static async Task<MockQuestComplete> CompleteNoObjectiveQuestAsync(ScenarioConnection connection, ulong character, ulong npc,
        uint quest, uint? expectedExperience, CancellationToken ct, StartingZoneCombat.CombatBudget? sharedBudget = null)
    {
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverCompleteQuest, ScenarioWire.GuidQuest(npc, quest), ct).ConfigureAwait(false);
        async Task<WorldFrame> ReadFrameAsync() => sharedBudget is null
            ? await new PacketBudget().ReadAsync(connection, ct).ConfigureAwait(false)
            : await sharedBudget.ReadAsync(connection, ct).ConfigureAwait(false);
        MockQuestOfferReward? offer = null;
        for (int i = 0; i < 128; i++)
        {
            WorldFrame frame = await ReadFrameAsync().ConfigureAwait(false);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgQuestgiverOfferReward)
            {
                offer = ScenarioWire.QuestOfferReward(frame.Payload);
                break;
            }

            if (frame.Opcode == (ushort)WorldOpcode.SmsgQuestgiverRequestItems)
            {
                MockQuestRequestItems request = ScenarioWire.QuestRequestItems(frame.Payload);
                if (request.Guid != npc || request.QuestId != quest || request.RequiredItems.Length != 0)
                    throw new MockProtocolException($"quest {quest} reward request mismatch");
                await connection.SendAsync(WorldOpcode.CmsgQuestgiverRequestReward, ScenarioWire.GuidQuest(npc, quest), ct).ConfigureAwait(false);
            }
        }

        if (offer is not { } matchingOffer || matchingOffer.Guid != npc || matchingOffer.QuestId != quest)
            throw new MockProtocolException($"quest {quest} did not produce a matching reward offer");
        if (matchingOffer.Choices.Any(item => item.ItemId == 0 || item.Count == 0)
            || matchingOffer.Rewards.Any(item => item.ItemId == 0 || item.Count == 0))
            throw new MockProtocolException($"quest {quest} offered a malformed reward item");
        await connection.SendAsync(WorldOpcode.CmsgQuestgiverChooseReward, ScenarioWire.GuidQuestChoice(npc, quest, 0), ct).ConfigureAwait(false);
        MockQuestComplete complete = ScenarioWire.QuestComplete(await ReadUntilAsync(connection, WorldOpcode.SmsgQuestgiverQuestComplete, ct, sharedBudget).ConfigureAwait(false));
        if (complete.QuestId != quest || (expectedExperience is { } expected && complete.Experience != expected))
            throw new MockProtocolException($"quest {quest} reward mismatch: xp={complete.Experience}");
        if (!complete.Rewards.SequenceEqual(matchingOffer.Rewards
            .Select(item => new MockQuestCompletedReward(item.ItemId, item.Count))))
            throw new MockProtocolException($"quest {quest} completion items differed from its observed offer");
        await AwaitQuestClearAsync(connection, character, quest, ct, sharedBudget).ConfigureAwait(false);
        return complete;
    }

    internal static Task<MockQuestComplete> CompleteQuest7Async(ScenarioConnection connection, ulong character, CancellationToken ct,
        StartingZoneCombat.CombatBudget? sharedBudget = null)
        => CompleteNoObjectiveQuestAsync(connection, character,
            ObjectGuid.WithEntry(HighGuid.Unit, NpcEntry, NpcSpawn).Value, Quest, expectedExperience: null, ct, sharedBudget);

    private static async Task AwaitQuestClearAsync(ScenarioConnection connection, ulong character, uint quest, CancellationToken ct,
        StartingZoneCombat.CombatBudget? sharedBudget = null)
    {
        async Task<WorldFrame> ReadFrameAsync() => sharedBudget is null
            ? await new PacketBudget().ReadAsync(connection, ct).ConfigureAwait(false)
            : await sharedBudget.ReadAsync(connection, ct).ConfigureAwait(false);
        for (int i = 0; i < 128; i++)
        {
            if (!HasQuest(connection.FieldsOf(character), quest)) return;
            WorldFrame frame = await ReadFrameAsync().ConfigureAwait(false);
            if (frame.Opcode is (ushort)WorldOpcode.SmsgQuestgiverQuestInvalid or (ushort)WorldOpcode.SmsgQuestgiverQuestFailed or (ushort)WorldOpcode.SmsgQuestlogFull)
                throw new MockProtocolException(QuestRefusal((WorldOpcode)frame.Opcode, frame.Payload, quest));
        }

        if (!HasQuest(connection.FieldsOf(character), quest)) return;

        throw new MockProtocolException($"quest {quest} remained in journal after reward");
    }

    private static byte[] CastSelf(uint spell)
    {
        byte[] bytes = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, spell);
        return bytes;
    }

    private static async Task ObserveNpcAsync(ScenarioConnection connection, ulong npc, uint expectedEntry, CancellationToken ct)
    {
        bool created = connection.FieldsOf(npc).GetValueOrDefault(UpdateFields.ObjectFieldEntry) == expectedEntry;
        var budget = new PacketBudget();
        for (int i = 0; !created && i < 128; i++)
        {
            await budget.ReadAsync(connection, ct).ConfigureAwait(false);
            created = connection.FieldsOf(npc).GetValueOrDefault(UpdateFields.ObjectFieldEntry) == expectedEntry;
        }
        if (!created) throw new MockProtocolException("visible NPC create did not contain the requested GUID and entry");
        byte[] query = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(query, expectedEntry);
        BinaryPrimitives.WriteUInt64LittleEndian(query.AsSpan(4), npc);
        await connection.SendAsync(WorldOpcode.CmsgCreatureQuery, query, ct).ConfigureAwait(false);
        byte[] answer = await ReadUntilAsync(connection, WorldOpcode.SmsgCreatureQueryResponse, ct).ConfigureAwait(false);
        if (answer.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(answer) != expectedEntry)
            throw new MockProtocolException("visible NPC query returned a missing or different template");
    }

    private static async Task<byte[]> ReadUntilAsync(ScenarioConnection connection, WorldOpcode opcode, CancellationToken ct,
        StartingZoneCombat.CombatBudget? sharedBudget = null)
    {
        async Task<WorldFrame> ReadFrameAsync() => sharedBudget is null
            ? await new PacketBudget().ReadAsync(connection, ct).ConfigureAwait(false)
            : await sharedBudget.ReadAsync(connection, ct).ConfigureAwait(false);
        for (int i = 0; i < 128; i++)
        {
            WorldFrame frame = await ReadFrameAsync().ConfigureAwait(false);
            if (frame.Opcode == (ushort)opcode) return frame.Payload;
        }
        throw new MockProtocolException($"did not receive {opcode} within 128 packets");
    }

    private static async Task LogoutAsync(ScenarioConnection connection, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct); bound.CancelAfter(TimeSpan.FromSeconds(30));
        await connection.SendAsync(WorldOpcode.CmsgLogoutRequest, [], bound.Token).ConfigureAwait(false);
        if (!IsLogoutSuccess(await LiveSession.ReadLogoutUntilAsync(connection, WorldOpcode.SmsgLogoutResponse, bound.Token).ConfigureAwait(false)))
            throw new MockProtocolException("logout response was not successful");
        if ((await LiveSession.ReadLogoutUntilAsync(connection, WorldOpcode.SmsgLogoutComplete, bound.Token).ConfigureAwait(false)).Length != 0)
            throw new MockProtocolException("logout completion body was not empty");
    }

    private static async Task VerifyQuest7RelogAsync(Options options, string characterName, ulong expectedGuid,
        TextWriter output, CancellationToken ct)
    {
        string password = Environment.GetEnvironmentVariable(options.PasswordEnvironment)!;
        LogonResult logon = await LogonClient.AuthenticateAsync(options.Realm, options.Account, password, ct).ConfigureAwait(false);
        await using WorldClient client = await WorldClient.ConnectAsync(logon.Realms[0].GetLoopbackEndpoint(), ct).ConfigureAwait(false);
        if (await client.AuthenticateAsync(options.Account, logon.SessionKey, ct).ConfigureAwait(false) != 0x0C)
            throw new MockProtocolException("quest-7 relog world authentication was rejected");

        var connection = new ScenarioConnection(client);
        IReadOnlyList<MockCharacter> listed = await connection.EnumerateAsync(ct).ConfigureAwait(false);
        if (connection.FramesReceived > 512)
            throw new MockProtocolException("quest-7 relog exceeded its 512-frame setup budget");
        MockCharacter saved = listed.FirstOrDefault(c => c.Name.Equals(characterName, StringComparison.OrdinalIgnoreCase))
            ?? throw new MockProtocolException("quest-7 relog did not enumerate the requested character");
        if (saved.Guid != expectedGuid)
            throw new MockProtocolException("quest-7 relog enumerated a different character GUID");
        MockLogin login = await connection.LoginAsync(saved.Guid, ct).ConfigureAwait(false);
        if (connection.FramesReceived > 512)
            throw new MockProtocolException("quest-7 relog exceeded its 512-frame login budget");
        if (HasQuest(login.Self.Fields, Quest)
            || login.Self.Fields.GetValueOrDefault(UpdateFields.UnitFieldLevel) == 0)
            throw new MockProtocolException("quest-7 relog retained the rewarded quest or lacked a valid level field");
        output.WriteLine($"starting-zone quest7-relog guid={saved.Guid} questPresent=false level={login.Self.Fields.GetValueOrDefault(UpdateFields.UnitFieldLevel)} xp={login.Self.Fields.GetValueOrDefault(UpdateFields.PlayerXp)} budgetFrames={connection.FramesReceived}/512");
        await LogoutAsync(connection, ct).ConfigureAwait(false);
    }

    internal static bool IsSelfSpellGo(ReadOnlySpan<byte> body, ulong character, uint spell)
    {
        try
        {
            var reader = new PacketReader(body);
            if (reader.ReadPackedGuid() != character || reader.ReadPackedGuid() != character || reader.ReadUInt32() != spell)
                return false;
            reader.ReadUInt16();
            if (reader.ReadByte() != 1 || reader.ReadUInt64() != character) return false;
            // This probe sends the vanilla self target mask (zero), with one hit and no misses/ammo.
            return reader.ReadByte() == 0 && reader.ReadUInt16() == 0 && reader.Remaining == 0;
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    internal static bool HasQuest(IReadOnlyDictionary<int, uint> fields, uint quest)
        => Enumerable.Range(0, QuestConstants.MaxQuestLogSize).Any(slot =>
            fields.GetValueOrDefault(UpdateFields.PlayerQuestLog11 + slot * QuestConstants.FieldsPerSlot) == quest);

    internal static bool IsQuestIncomplete(IReadOnlyDictionary<int, uint> fields, uint quest)
    {
        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            int start = UpdateFields.PlayerQuestLog11 + slot * QuestConstants.FieldsPerSlot;
            if (fields.GetValueOrDefault(start) == quest)
                return ((fields.GetValueOrDefault(start + 1) >> 24) & (QuestConstants.SlotStateComplete | QuestConstants.SlotStateFail)) == 0;
        }
        return false;
    }

    internal static bool IsQuestComplete(IReadOnlyDictionary<int, uint> fields, uint quest)
    {
        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            int start = UpdateFields.PlayerQuestLog11 + slot * QuestConstants.FieldsPerSlot;
            if (fields.GetValueOrDefault(start) == quest)
            {
                uint state = fields.GetValueOrDefault(start + 1) >> 24;
                return (state & QuestConstants.SlotStateComplete) != 0 && (state & QuestConstants.SlotStateFail) == 0;
            }
        }
        return false;
    }

    internal static string QuestRefusal(WorldOpcode opcode, ReadOnlySpan<byte> payload, uint expectedQuest)
    {
        return opcode switch
        {
            WorldOpcode.SmsgQuestgiverQuestInvalid when payload.Length == 4
                => $"quest {expectedQuest} refused with invalid reason {BinaryPrimitives.ReadUInt32LittleEndian(payload)}",
            WorldOpcode.SmsgQuestgiverQuestFailed when payload.Length == 8
                && BinaryPrimitives.ReadUInt32LittleEndian(payload) == expectedQuest
                => $"quest {expectedQuest} failed with reason {BinaryPrimitives.ReadUInt32LittleEndian(payload[4..])}",
            WorldOpcode.SmsgQuestlogFull when payload.Length == 0
                => $"quest {expectedQuest} refused because the quest log is full",
            _ => throw new MockProtocolException($"malformed or mismatched quest refusal opcode={opcode} length={payload.Length} expectedQuest={expectedQuest}"),
        };
    }

    internal static bool IsBattleStance(IReadOnlyDictionary<int, uint> fields)
        => ((fields.GetValueOrDefault(UpdateFields.UnitFieldBytes1) >> 16) & 0xFF) == 17; // ShapeshiftService.FormOf.

    private sealed class PacketBudget
    {
        private int _packets;
        private int _bytes;
        public async Task<WorldFrame> ReadAsync(ScenarioConnection connection, CancellationToken ct)
        {
            if (++_packets > 128) throw new MockProtocolException("probe phase exceeded 128 packets");
            await connection.WaitForTrafficAsync(ct).ConfigureAwait(false);
            WorldFrame frame = await connection.ReadAsync(ct).ConfigureAwait(false);
            _bytes += frame.Payload.Length + 4;
            if (_bytes > 1024 * 1024) throw new MockProtocolException("probe phase exceeded its one-megabyte packet budget");
            return frame;
        }
    }
}
