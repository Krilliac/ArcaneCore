using System.Buffers.Binary;
using System.Diagnostics;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>One completed assertion in the owned synthetic client lifecycle.</summary>
public sealed record MockScenarioCheck(string Name, bool Passed, string Detail);

/// <summary>A credential-free result suitable for the CLI's JSON output.</summary>
public sealed record MockScenarioReport(string Outcome, ushort ClientBuild, ulong CharacterGuid,
    int FramesReceived, long DurationMilliseconds, IReadOnlyList<MockScenarioCheck> Checks)
{
    public int CheckCount => Checks.Count;
}

/// <summary>
/// Exercises the production realm, world sessions, database stores and features using independently
/// encoded client packets. It owns both listeners and every synthetic account and character.
/// </summary>
public static partial class MockScenarios
{
    private const string AccountName = "MOCKACCOUNT";
    private const string Password = "MOCKPASSWORD";
    private const string CharacterName = "Mockhero";
    private const ushort Build = 5875;
    private const int QuestIdField = 0x00C6;
    private const int QuestCountField = 0x00C7;
    private const int NpcQuestIdField = 0x00C9;
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromSeconds(45);

    public static async Task<MockScenarioReport> RunAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ScenarioTimeout);
        CancellationToken token = deadline.Token;
        var elapsed = Stopwatch.StartNew();
        var checks = new List<MockScenarioCheck>();
        ulong characterGuid;
        int framesReceived = 0;

        await using (SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token).ConfigureAwait(false))
        {
            int accountId = await server.AddAccountAsync(AccountName, Password, token).ConfigureAwait(false);
            Check(checks, "fixture.started", "Real SQLite stores and realm/world sessions listen on owned loopback sockets.");

            LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, AccountName, Password, token).ConfigureAwait(false);
            await AssertStoredSessionAsync(server, accountId, logon, token).ConfigureAwait(false);
            Check(checks, "realm.srp", "SRP and the server proof succeeded; the real account store received the session key.");
            var endpoint = RealmEndpoint(server, logon);
            Check(checks, "realm.address", "The decoded realm list directs the client to this fixture's world listener.");

            MockCharacter character;
            await using (WorldClient client = await WorldClient.ConnectAsync(endpoint, token).ConfigureAwait(false))
            {
                Require(await client.AuthenticateAsync(AccountName, logon.SessionKey, token).ConfigureAwait(false) == 0x0C,
                    "World authentication rejected the real realm-derived session key.");
                framesReceived += 3; // challenge, auth response, addon response consumed by AuthenticateAsync
                var connection = new ScenarioConnection(client);
                Check(checks, "world.auth", "World session proof and encrypted authentication succeeded with the realm-derived key.");
                Require((await connection.EnumerateAsync(token).ConfigureAwait(false)).Count == 0,
                    "A fresh synthetic account must have no characters.");
                Check(checks, "character.initial-empty", "Character enumeration is exactly empty before creation.");
                await connection.CreateCharacterAsync(CharacterName, token).ConfigureAwait(false);
                Check(checks, "character.create", "The wire client created a human warrior through the real session handler.");
                character = SingleCharacter(await connection.EnumerateAsync(token).ConfigureAwait(false));
                ValidateCharacter(character);
                characterGuid = character.Guid;
                Check(checks, "character.enumerate", "Decoded the GUID, appearance, level, map, position, pet and all 20 equipment slots without trailing bytes.");

                await SeedJournalAsync(server, characterGuid, token).ConfigureAwait(false);
                Check(checks, "journal.persist", "Saved an ordinary incomplete quest row with one of two synthetic objectives through ICharacterQuestStore.");
                MockLogin login = await connection.LoginAsync(characterGuid, token).ConfigureAwait(false);
                ValidateLogin(character, login);
                ValidateJournal(login.Self);
                Check(checks, "login.sequence", "The account, pre-map, self-create and world-state packet stages arrived in order.");
                Check(checks, "login.self-create", "Decoded the full self GUID, finite movement position, field mask and player type independently.");
                Check(checks, "journal.initial-fields", "The self create contains quest 900001 at field 0xC6 and objective progress one at 0xC7.");

                await connection.SendAsync(WorldOpcode.CmsgQuestQuery, ScenarioWire.UInt32(SyntheticArcaneServer.JournalQuestId), token).ConfigureAwait(false);
                MockQuestQuery quest = ScenarioWire.QuestQuery(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestQueryResponse, token).ConfigureAwait(false));
                ValidateQuestQuery(quest);
                Check(checks, "quest.query", "Decoded exact vanilla quest fields, synthetic title/details/objectives, four requirements and four texts.");

                ValidateNpcSlot(login.Self.Fields, accepted: false);
                await connection.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, ScenarioWire.Guid(SyntheticArcaneServer.NpcGuid), token).ConfigureAwait(false);
                MockQuestgiverStatus status = ScenarioWire.QuestgiverStatus(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverStatus, token).ConfigureAwait(false));
                Require(status is { Guid: SyntheticArcaneServer.NpcGuid, Status: 5 },
                    "Synthetic NPC must return its exact full GUID and AVAILABLE status.");
                Check(checks, "npc.status", "The real nearby creature returned its full GUID and exact u32 available status.");
                await connection.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest,
                    ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.NpcQuestId), token).ConfigureAwait(false);
                MockQuestDetails details = ScenarioWire.QuestDetails(await connection.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestDetails, token).ConfigureAwait(false));
                ValidateNpcDetails(details);
                Check(checks, "npc.details", "Decoded the complete vanilla quest details body, including strings, empty reward arrays, spell and four emotes.");
                await connection.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest,
                    ScenarioWire.GuidQuest(SyntheticArcaneServer.NpcGuid, SyntheticArcaneServer.NpcQuestId), token).ConfigureAwait(false);
                Require((await connection.ReadUntilAsync(WorldOpcode.SmsgGossipComplete, token).ConfigureAwait(false)).Length == 0,
                    "Quest acceptance must close gossip with an empty body.");
                MockFieldUpdate accepted = await connection.ReadUntilFieldAsync(characterGuid, NpcQuestIdField, SyntheticArcaneServer.NpcQuestId, token).ConfigureAwait(false);
                ValidateNpcSlot(accepted.Fields, accepted: true);
                ValidateOriginalJournalDelta(accepted.Fields);
                Check(checks, "npc.accept-fields", "The exact twelve-byte accept request produced empty gossip completion and slot-one quest id 900002 in a real update mask.");

                const uint pingSequence = 0x12345678;
                await connection.SendAsync(WorldOpcode.CmsgPing, ScenarioWire.Ping(pingSequence, 42), token).ConfigureAwait(false);
                byte[] pong = await connection.ReadUntilAsync(WorldOpcode.SmsgPong, token).ConfigureAwait(false);
                Require(pong.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(pong) == pingSequence,
                    "World ping did not return an exact four-byte sequence echo.");
                Check(checks, "session.ping", "An eight-byte sequence/latency ping received the exact four-byte sequence echo.");

                await connection.LogoutAsync(token).ConfigureAwait(false);
                Require(server.World.OnlinePlayerCount == 0, "Instant logout left a player online.");
                Check(checks, "session.logout", "Received success/instant logout response and empty completion; the real world is empty.");
                Require(SingleCharacter(await connection.EnumerateAsync(token).ConfigureAwait(false)).Guid == characterGuid,
                    "Logout did not retain the character at character select.");
                Check(checks, "character.after-logout", "Character select enumerates the same character after logout.");
                await ValidatePersistedJournalAsync(server, characterGuid, token).ConfigureAwait(false);
                Check(checks, "journal.after-logout", "The real quest store retains the synthetic incomplete status and objective count.");
                await ValidatePersistedNpcQuestAsync(server, characterGuid, expectedStatus: 3, token).ConfigureAwait(false);
                Check(checks, "npc.accept-persisted", "The real quest save queue stored the accepted ordinary quest with incomplete status and zero counters.");
                framesReceived += connection.FramesReceived;
            }

            LogonResult reconnect = await LogonClient.AuthenticateAsync(server.RealmEndpoint, AccountName, Password, token).ConfigureAwait(false);
            await AssertStoredSessionAsync(server, accountId, reconnect, token).ConfigureAwait(false);
            Check(checks, "realm.reconnect", "A fresh realm SRP exchange supplied the key for a new world connection.");
            await using (WorldClient client = await WorldClient.ConnectAsync(RealmEndpoint(server, reconnect), token).ConfigureAwait(false))
            {
                Require(await client.AuthenticateAsync(AccountName, reconnect.SessionKey, token).ConfigureAwait(false) == 0x0C,
                    "Reconnected world rejected the newly derived realm key.");
                framesReceived += 3;
                var connection = new ScenarioConnection(client);
                MockCharacter saved = SingleCharacter(await connection.EnumerateAsync(token).ConfigureAwait(false));
                ValidateCharacter(saved);
                Require(saved.Guid == characterGuid, "Reconnect did not enumerate the original character GUID.");
                MockLogin relogin = await connection.LoginAsync(characterGuid, token).ConfigureAwait(false);
                ValidateLogin(saved, relogin);
                ValidateJournal(relogin.Self);
                ValidateNpcSlot(relogin.Self.Fields, accepted: true);
                Check(checks, "character.relogin", "The new world connection logged in the same persisted character.");
                Check(checks, "journal.relogin-fields", "Reloaded quest id and objective progress survived logout and a fresh realm/world handshake.");
                Check(checks, "npc.relogin-fields", "Accepted quest 900002 reloaded into slot one after a fresh realm/world handshake.");
                await connection.SendAsync(WorldOpcode.CmsgQuestlogRemoveQuest, [1], token).ConfigureAwait(false);
                MockFieldUpdate abandoned = await connection.ReadUntilFieldAsync(characterGuid, NpcQuestIdField, 0, token).ConfigureAwait(false);
                ValidateNpcSlot(abandoned.Fields, accepted: false);
                ValidateOriginalJournalDelta(abandoned.Fields);
                Check(checks, "npc.abandon-fields", "The one-byte slot-one abandon request cleared quest id 900002 in an independently decoded update mask.");
                await connection.LogoutAsync(token).ConfigureAwait(false);
                Require(server.World.OnlinePlayerCount == 0, "Logout after quest abandonment left the character online.");
                await ValidatePersistedNpcQuestAsync(server, characterGuid, expectedStatus: 0, token).ConfigureAwait(false);
                await ValidatePersistedJournalAsync(server, characterGuid, token).ConfigureAwait(false);
                Check(checks, "npc.abandon-persisted", "The real quest store saved status None while retaining the separate seeded journal's progress.");
                framesReceived += connection.FramesReceived;
            }

            LogonResult finalLogon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, AccountName, Password, token).ConfigureAwait(false);
            await AssertStoredSessionAsync(server, accountId, finalLogon, token).ConfigureAwait(false);
            await using (WorldClient client = await WorldClient.ConnectAsync(RealmEndpoint(server, finalLogon), token).ConfigureAwait(false))
            {
                Require(await client.AuthenticateAsync(AccountName, finalLogon.SessionKey, token).ConfigureAwait(false) == 0x0C,
                    "World authentication after abandonment rejected the real realm-derived key.");
                framesReceived += 3;
                var connection = new ScenarioConnection(client);
                MockCharacter saved = SingleCharacter(await connection.EnumerateAsync(token).ConfigureAwait(false));
                Require(saved.Guid == characterGuid, "Final reconnect enumerated a different character.");
                MockLogin finalLogin = await connection.LoginAsync(characterGuid, token).ConfigureAwait(false);
                ValidateLogin(saved, finalLogin);
                ValidateJournal(finalLogin.Self);
                ValidateNpcSlot(finalLogin.Self.Fields, accepted: false);
                await ValidatePersistedNpcQuestAsync(server, characterGuid, expectedStatus: 0, token).ConfigureAwait(false);
                Check(checks, "npc.abandon-relogin", "A second fresh realm/world reconnect left the abandoned slot empty and the separate journal unchanged.");
                await RunRewardFlowAsync(server, connection, characterGuid, checks, token).ConfigureAwait(false);
                await WaitForCombatExitAsync(server, connection, characterGuid, token).ConfigureAwait(false);
                await connection.LogoutAsync(token).ConfigureAwait(false);
                Require(server.World.OnlinePlayerCount == 0, "Final logout left the character online.");
                await ValidatePersistedRewardAsync(server, characterGuid, rewarded: true, count: 2, token).ConfigureAwait(false);
                framesReceived += connection.FramesReceived;
            }

            LogonResult rewardRelog = await LogonClient.AuthenticateAsync(server.RealmEndpoint, AccountName, Password, token).ConfigureAwait(false);
            await AssertStoredSessionAsync(server, accountId, rewardRelog, token).ConfigureAwait(false);
            await using (WorldClient client = await WorldClient.ConnectAsync(RealmEndpoint(server, rewardRelog), token).ConfigureAwait(false))
            {
                Require(await client.AuthenticateAsync(AccountName, rewardRelog.SessionKey, token).ConfigureAwait(false) == 0x0C,
                    "Reward relog rejected the fresh realm-derived session key.");
                framesReceived += 3;
                var connection = new ScenarioConnection(client);
                MockCharacter saved = SingleCharacter(await connection.EnumerateAsync(token).ConfigureAwait(false));
                Require(saved.Guid == characterGuid, "Reward reconnect enumerated a different character.");
                MockLogin rewardedLogin = await connection.LoginAsync(characterGuid, token).ConfigureAwait(false);
                ValidateLogin(saved, rewardedLogin);
                ValidateJournal(rewardedLogin.Self);
                ValidateNpcSlot(rewardedLogin.Self.Fields, accepted: false);
                RewardObservation reward = await ObserveRewardAsync(server, characterGuid, token).ConfigureAwait(false);
                ValidateRewardObservation(reward, rewarded: true);
                ValidateRewardWireFields(connection, characterGuid, reward);
                await ValidatePersistedRewardAsync(server, characterGuid, rewarded: true, count: 2, token).ConfigureAwait(false);
                await ValidatePersistedJournalAsync(server, characterGuid, token).ConfigureAwait(false);
                Check(checks, "reward.relogin-fields", "Fresh realm/world authentication restored 1234 copper and fixed/chosen item creates while keeping the rewarded quest slot clear.");
                Check(checks, "reward.relogin-history", "The real quest store retained complete rewarded history, two kills and chosen item entry 900042; the original journal still has one kill.");
                await connection.LogoutAsync(token).ConfigureAwait(false);
                Require(server.World.OnlinePlayerCount == 0, "Reward relog logout left the character online.");
                await connection.SendAsync(WorldOpcode.CmsgCharDelete, ScenarioWire.Guid(characterGuid), token).ConfigureAwait(false);
                byte[] deletion = await connection.ExpectAsync(WorldOpcode.SmsgCharDelete, token).ConfigureAwait(false);
                Require(deletion.AsSpan().SequenceEqual(new byte[] { 0x39 }), "Final character deletion did not succeed.");
                Require((await connection.EnumerateAsync(token).ConfigureAwait(false)).Count == 0,
                    "Deleted character remains in the character enumeration.");
                Check(checks, "character.delete", "The wire client deleted the offline character and decoded the final empty enumeration.");
                framesReceived += connection.FramesReceived;
            }
        }

        Check(checks, "fixture.disposed", "Owned connections, real world lifecycle and temporary database directory drained and disposed.");
        return new MockScenarioReport("passed", Build, characterGuid, framesReceived, elapsed.ElapsedMilliseconds, checks.AsReadOnly());
    }

    private static System.Net.IPEndPoint RealmEndpoint(SyntheticArcaneServer server, LogonResult result)
    {
        Require(result.Realms.Count == 1, "Synthetic realm list must contain one entry.");
        System.Net.IPEndPoint endpoint = result.Realms[0].GetLoopbackEndpoint();
        Require(endpoint.Equals(server.WorldEndpoint), "Realm redirected outside this fixture's owned world listener.");
        return endpoint;
    }

    private static async Task AssertStoredSessionAsync(SyntheticArcaneServer server, int accountId, LogonResult result, CancellationToken token)
    {
        Require(result.SessionKey.Length == 40, "Realm authentication returned a session key with the wrong size.");
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        Account? stored = await scope.ServiceProvider.GetRequiredService<IAccountStore>().FindByUsernameAsync(AccountName, token).ConfigureAwait(false);
        Require(stored is not null && stored.Id == accountId && stored.SessionKey is { Length: 40 }
            && stored.SessionKey.AsSpan().SequenceEqual(result.SessionKey), "The real realm store did not persist the client-derived session key.");
    }

    internal static async Task SeedJournalAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
    {
        int characterId = checked((int)guid);
        var status = new CharacterQuestStatus(characterId, SyntheticArcaneServer.JournalQuestId, Status: 3,
            Rewarded: false, Explored: false, Timer: 0, MobCount1: 1, MobCount2: 0, MobCount3: 0, MobCount4: 0,
            ItemCount1: 0, ItemCount2: 0, ItemCount3: 0, ItemCount4: 0, RewardChoice: 0);
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>().SaveQuestsAsync(characterId, [status], token).ConfigureAwait(false);
        await ValidatePersistedJournalAsync(server, guid, token).ConfigureAwait(false);
    }

    private static async Task ValidatePersistedJournalAsync(SyntheticArcaneServer server, ulong guid, CancellationToken token)
    {
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterQuestData persisted = await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>().LoadAsync(checked((int)guid), token).ConfigureAwait(false);
        CharacterQuestStatus? row = persisted.Quests.SingleOrDefault(value => value.Quest == SyntheticArcaneServer.JournalQuestId);
        Require(row is { Status: 3, Rewarded: false, Explored: false, Timer: 0, MobCount1: 1, MobCount2: 0,
            MobCount3: 0, MobCount4: 0, ItemCount1: 0, ItemCount2: 0, ItemCount3: 0, ItemCount4: 0 },
            "The real quest store returned different journal progress.");
    }

    private static async Task ValidatePersistedNpcQuestAsync(SyntheticArcaneServer server, ulong guid, byte expectedStatus, CancellationToken token)
    {
        int characterId = checked((int)guid);
        // A barrier observes writes already produced by the real packet handlers. It never
        // creates, injects or changes a quest row and is bounded by the scenario deadline.
        await server.Services.GetRequiredService<QuestNpcFeature>().Persistence.FlushCharacterAsync(characterId).WaitAsync(token).ConfigureAwait(false);
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterQuestData data = await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>().LoadAsync(characterId, token).ConfigureAwait(false);
        CharacterQuestStatus? row = data.Quests.SingleOrDefault(value => value.Quest == SyntheticArcaneServer.NpcQuestId);
        Require(row is not null && row.Status == expectedStatus && row is { Rewarded: false, Explored: false, Timer: 0,
            MobCount1: 0, MobCount2: 0, MobCount3: 0, MobCount4: 0, ItemCount1: 0, ItemCount2: 0, ItemCount3: 0, ItemCount4: 0 },
            "The real store did not retain the expected NPC quest status and counters.");
    }

    private static MockCharacter SingleCharacter(IReadOnlyList<MockCharacter> characters)
    {
        Require(characters.Count == 1, "Synthetic account must enumerate exactly one character.");
        return characters[0];
    }

    private static void ValidateCharacter(MockCharacter character)
    {
        Require(character.Guid is > 0 and <= int.MaxValue && character.Name == CharacterName,
            "Enumerated character GUID or name differs from the created fixture.");
        Require(character is { Race: 1, Class: 1, Gender: 0, Level: 1, Map: 0, Zone: 12 }
            && character.Appearance.All(value => value == 0), "Enumerated human warrior fields differ from the fixture.");
        Require(float.IsFinite(character.X) && float.IsFinite(character.Y) && float.IsFinite(character.Z),
            "Enumerated character position must be finite.");
        Require(character is { Guild: 0, Flags: 0, PetDisplay: 0, PetLevel: 0, PetFamily: 0 }
            && character.FirstLogin <= 1, "Enumerated synthetic character metadata is invalid.");
        Require(character.Equipment.Count == 20 && character.Equipment.All(item => item is { DisplayId: 0, InventoryType: 0 }),
            "Synthetic character must enumerate all twenty empty equipment slots.");
    }

    private static void ValidateLogin(MockCharacter character, MockLogin login)
    {
        Require(login.Location.Map == character.Map && login.Location.X == character.X
            && login.Location.Y == character.Y && login.Location.Z == character.Z && float.IsFinite(login.Location.Orientation),
            "Login location differs from the saved character's enumerated position.");
        Require(login.Self.X == login.Location.X && login.Self.Y == login.Location.Y && login.Self.Z == login.Location.Z
            && login.Self.Orientation == login.Location.Orientation, "Self create location differs from login verify world.");
        Require(login.MotdLines == 1, "Synthetic login must receive the configured single MOTD line.");
    }

    private static void ValidateJournal(MockSelfCreate self)
    {
        Require(self.Fields.GetValueOrDefault(QuestIdField) == SyntheticArcaneServer.JournalQuestId
            && self.Fields.GetValueOrDefault(QuestCountField) == 1 && self.Fields.GetValueOrDefault(QuestCountField + 1) == 0,
            "Initial self create did not restore the ordinary saved quest journal fields.");
    }

    private static void ValidateNpcSlot(IReadOnlyDictionary<int, uint> fields, bool accepted)
    {
        Require(fields.GetValueOrDefault(NpcQuestIdField) == (accepted ? SyntheticArcaneServer.NpcQuestId : 0)
            && fields.GetValueOrDefault(NpcQuestIdField + 1) == 0 && fields.GetValueOrDefault(NpcQuestIdField + 2) == 0,
            "NPC quest slot one contains an unexpected id, count/state word or timer.");
    }

    private static void ValidateOriginalJournalDelta(IReadOnlyDictionary<int, uint> fields)
    {
        Require((!fields.TryGetValue(QuestIdField, out uint quest) || quest == SyntheticArcaneServer.JournalQuestId)
            && (!fields.TryGetValue(QuestCountField, out uint progress) || progress == 1),
            "NPC quest mutation changed the separate seeded journal slot.");
    }

    private static void ValidateNpcDetails(MockQuestDetails details)
    {
        Require(details is { Guid: SyntheticArcaneServer.NpcGuid, QuestId: SyntheticArcaneServer.NpcQuestId,
            Title: "Mock NPC quest", Details: "synthetic NPC acceptance", Objectives: "defeat two synthetic targets",
            ActivateAccept: 1, Money: 0, Spell: 0 } && details.Choices.Length == 0 && details.Rewards.Length == 0
            && details.Emotes.Length == 4 && details.Emotes.All(value => value is { Id: 0, Delay: 0 }),
            "NPC quest details differ from the synthetic ordinary quest.");
    }

    private static void ValidateQuestQuery(MockQuestQuery quest)
    {
        Require(quest is { Id: SyntheticArcaneServer.JournalQuestId, Method: 2, Level: 1,
            Title: "Mock journal", Details: "synthetic lifecycle query", Objectives: "synthetic progress", EndText: "" },
            "Quest query differs from the synthetic immutable content.");
        Require(quest.Header.All(value => value == 0) && quest.Rewards.All(value => value == 0)
            && quest is { PointMap: 0, PointX: 0, PointY: 0, PointOption: 0 }, "Synthetic quest query fixed header/reward/point fields differ.");
        Require(quest.Requirements[0] is { CreatureOrGameObject: 900101, CreatureCount: 2, ItemId: 0, ItemCount: 0 }
            && quest.Requirements.Skip(1).All(value => value is { CreatureOrGameObject: 0, CreatureCount: 0, ItemId: 0, ItemCount: 0 })
            && quest.ObjectiveTexts.All(string.IsNullOrEmpty), "Synthetic quest query objective fields differ.");
    }

    private static void Check(List<MockScenarioCheck> checks, string name, string detail)
        => checks.Add(new MockScenarioCheck(name, Passed: true, detail));

    private static void Require(bool condition, string message) => ScenarioWire.Require(condition, message);
}
