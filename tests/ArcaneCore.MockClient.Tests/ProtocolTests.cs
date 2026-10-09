using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

// Numerical reference vectors: gtker/wow_srp @ 25ffab6433e1ee5eee629200cf42b592c1f36121.
// See PROTOCOL_VECTORS_NOTICE.md for source paths, byte orders, and the retained MIT license.
public sealed class ProtocolTests
{
    private static readonly byte[] CaptureKey =
    [
        239, 107, 150, 237, 174, 220, 162, 4, 138, 56, 166, 166, 138, 152, 188, 146, 96, 151,
        1, 201, 202, 137, 231, 87, 203, 23, 62, 17, 7, 169, 178, 1, 51, 208, 202, 223, 26, 216, 250, 9,
    ];

    [Fact]
    public void ProtocolThreeChallenge_HasIndependentGoldenEncoding()
    {
        byte[] expected = Convert.FromHexString(
            "00032400576F5700010C01F316363878006E69570042476E65000000007F00000106544553544552");
        Assert.Equal(expected, ProtocolPackets.LogonChallenge("TESTER", 5875));
    }

    [Fact]
    public void WorldClientHeader_UsesBigEndianSizeAndLittleEndianFourByteOpcode()
    {
        Assert.Equal(new byte[] { 0, 12, 0xDC, 1, 0, 0 }, ProtocolPackets.ClientHeader(0x01DC, 8));
        Assert.Equal(new byte[] { 0, 4, 0x37, 0, 0, 0 }, ProtocolPackets.ClientHeader(0x0037, 0));
    }

    [Fact]
    public void QuestChooseReward_EncodesExactGuidQuestAndChoice()
    {
        byte[] expected = Convert.FromHexString("0807060504030201A3BB0D0001000000");
        byte[] actual = ScenarioWire.GuidQuestChoice(0x0102030405060708, 900003, 1);
        Assert.Equal(16, actual.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void QuestOfferReward_DecodesIndependentBuild5875Literal()
    {
        MockQuestOfferReward result = ScenarioWire.QuestOfferReward(QuestOfferRewardVector());
        Assert.Equal(0x0102030405060708ul, result.Guid);
        Assert.Equal(900003u, result.QuestId);
        Assert.Equal("Reward", result.Title);
        Assert.Equal("Done", result.Text);
        Assert.Equal(1u, result.EnableNext);
        // Delay precedes emote ID on the wire, unlike SMSG_QUESTGIVER_QUEST_DETAILS.
        Assert.Equal(new MockQuestEmote(5, 1000), Assert.Single(result.Emotes));
        Assert.Collection(result.Choices,
            item => Assert.Equal(new MockQuestReward(900041, 1, 11), item),
            item => Assert.Equal(new MockQuestReward(900042, 3, 12), item));
        Assert.Equal(new MockQuestReward(900040, 2, 13), Assert.Single(result.Rewards));
        Assert.Equal(1234, result.Money);
        Assert.Equal(0u, result.Flags);
        Assert.Equal(0u, result.Spell);
    }

    [Fact]
    public void QuestOfferReward_PreservesSignedMoneyAndOrderedFlagsSpellSuffix()
    {
        byte[] body = QuestOfferRewardVector();
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(84), -7);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(88), 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(92), 54321);
        MockQuestOfferReward result = ScenarioWire.QuestOfferReward(body);
        Assert.Equal(-7, result.Money);
        Assert.Equal(0x100u, result.Flags);
        Assert.Equal(54321u, result.Spell);
    }

    [Theory]
    [InlineData(28, 5u)]
    [InlineData(40, 7u)]
    [InlineData(68, 5u)]
    [InlineData(28, uint.MaxValue)]
    [InlineData(40, uint.MaxValue)]
    [InlineData(68, uint.MaxValue)]
    public void QuestOfferReward_RejectsCountsBeyondVanillaLimits(int offset, uint count)
    {
        byte[] body = QuestOfferRewardVector();
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), count);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestOfferReward(body));
    }

    [Fact]
    public void QuestOfferReward_RejectsEveryTruncationAndTrailingByte()
    {
        byte[] body = QuestOfferRewardVector();
        for (int length = 0; length < body.Length; length++)
        {
            Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestOfferReward(body[..length]));
        }

        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestOfferReward([.. body, 0]));
    }

    [Fact]
    public void QuestComplete_DecodesOnlyFixedItemPairsWithoutGuidOrChoice()
    {
        byte[] body = QuestCompleteVector();
        Assert.Equal(28, body.Length);
        MockQuestComplete result = ScenarioWire.QuestComplete(body);
        Assert.Equal(900003u, result.QuestId);
        Assert.Equal(3u, result.Type);
        Assert.Equal(0u, result.Experience);
        Assert.Equal(1234u, result.Money);
        Assert.Equal(new MockQuestCompletedReward(900040, 2), Assert.Single(result.Rewards));
    }

    [Fact]
    public void QuestComplete_RejectsEveryTruncationAndAdditionalRewardFields()
    {
        byte[] body = QuestCompleteVector();
        for (int length = 0; length < body.Length; length++)
        {
            Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestComplete(body[..length]));
        }

        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestComplete([.. body, 0]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestComplete(
            [0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01, .. body]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestComplete(
            [.. body, 0xCA, 0xBB, 0x0D, 0, 3, 0, 0, 0]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestComplete([.. body, 13, 0, 0, 0]));
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(uint.MaxValue)]
    public void QuestComplete_RejectsCountsBeyondFourFixedRewards(uint count)
    {
        byte[] body = QuestCompleteVector();
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), count);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestComplete(body));
    }

    [Theory]
    [InlineData("A3BB0D00BEBB0D0001000000020000000807060504030201", 1u)]
    [InlineData("A3BB0D00BEBB0D0002000000020000000807060504030201", 2u)]
    public void QuestKill_DecodesIndependentProgressLiterals(string hex, uint count)
    {
        byte[] body = Convert.FromHexString(hex);
        MockQuestKill result = ScenarioWire.QuestKill(body);
        Assert.Equal(new MockQuestKill(900003, 900030, count, 2, 0x0102030405060708), result);
        Assert.Equal(24, body.Length);
        for (int length = 0; length < body.Length; length++)
        {
            Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestKill(body[..length]));
        }

        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestKill([.. body, 0]));
    }

    [Fact]
    public void InitialSelfCreate_DecodesOwnedItemCreatesBeforePlayer()
    {
        byte[] body = [3, 0, 0, 0, 0, .. FirstItemCreateVector(), .. SecondItemCreateVector(), .. SelfCreateBlockVector()];
        MockSelfCreate self = ScenarioWire.SelfCreate(body);
        Assert.Equal(0x1234ul, self.Guid);
        Assert.Equal(1f, self.X);
        Assert.Equal(2f, self.Y);
        Assert.Equal(3f, self.Z);
        Assert.Equal(4f, self.Orientation);
        Assert.Equal(0x1234u, self.Fields[0]);
        Assert.Equal(0u, self.Fields[1]);
        Assert.Equal(0x19u, self.Fields[2]);
        Assert.Collection(ScenarioWire.FieldUpdates(body),
            item =>
            {
                Assert.Equal(0x4000000000000001ul, item.Guid);
                Assert.Equal(900040u, item.Fields[3]);
            },
            item =>
            {
                Assert.Equal(0x4000000000000002ul, item.Guid);
                Assert.Equal(900042u, item.Fields[3]);
            },
            player => Assert.Equal(self.Guid, player.Guid));
        for (int length = 0; length < body.Length; length++)
        {
            Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate(body[..length]));
        }

        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate([.. body, 0]));
    }

    [Fact]
    public void InitialSelfCreate_RequiresExactlyOneSelfPlayer()
    {
        byte[] self = SelfCreateBlockVector();
        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate([2, 0, 0, 0, 0, .. self, .. self]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate(
            [2, 0, 0, 0, 0, .. FirstItemCreateVector(), .. SecondItemCreateVector()]));
        Assert.Equal(0x1234ul, ScenarioWire.SelfCreate([1, 0, 0, 0, 0, .. self]).Guid);
    }

    [Fact]
    public void FieldUpdates_EstablishOmittedUnitZerosOnlyForFullCreates()
    {
        byte[] create = [1, 0, 0, 0, 0, .. SelfCreateBlockVector()];
        var fields = Assert.Single(ScenarioWire.FieldUpdates(create)).Fields;
        Assert.Equal(0u, fields[ArcaneCore.Game.UpdateFields.UnitFieldTarget]);
        Assert.Equal(0u, fields[ArcaneCore.Game.UpdateFields.UnitFieldTarget + 1]);
        Assert.True(fields.ContainsKey(ArcaneCore.Game.UpdateFields.UnitFieldFlags));

        byte[] values = [1, 0, 0, 0, 0, 0, 1, 18, 1, 1, 0, 0, 0, 18, 0, 0, 0];
        var incremental = Assert.Single(ScenarioWire.FieldUpdates(values)).Fields;
        Assert.False(incremental.ContainsKey(ArcaneCore.Game.UpdateFields.UnitFieldTarget));
        Assert.False(incremental.ContainsKey(ArcaneCore.Game.UpdateFields.UnitFieldTarget + 1));
        Assert.False(incremental.ContainsKey(ArcaneCore.Game.UpdateFields.UnitFieldFlags));
    }

    /// <summary>
    /// The server sends its ships in packets of their own with the has-transport byte set (vmangos Map::SendInitTransports,
    /// GenericTransport::SendCreateUpdateToMap / SendOutOfRangeUpdateToMap; ArcaneCore TransportPackets): u32 count, u8 1, then ordinary
    /// blocks. With World:Transports:Enabled every player on a continent gets them at login, so a live session (the deploy's operator)
    /// must decode them rather than end with "Synthetic update cannot contain a transport header".
    /// </summary>
    [Fact]
    public void ShipPackets_WithTheHasTransportByte_DecodeAsOrdinaryUpdates()
    {
        byte[] create =
        [
            1, 0, 0, 0, 1, // one block, has transport
            2, 0x01, 0x51, 5, // CREATE_OBJECT, packed GUID 0x51, game object
            0x52, // HAS_POSITION | ALL | TRANSPORT
            .. Convert.FromHexString("0000803F000000400000404000008040"), // x/y/z/orientation: 1/2/3/4
            1, 0, 0, 0, // ALL
            0x10, 0x27, 0, 0, // path progress
            1, 1, 0, 0, 0, 0x51, 0, 0, 0, // one mask word, field 0 = 0x51
        ];
        MockFieldUpdate ship = Assert.Single(ScenarioWire.FieldUpdates(create));
        Assert.Equal(0x51ul, ship.Guid);
        Assert.Equal(0x51u, ship.Fields[0]);
        Assert.Equal(new MockPosition(1f, 2f, 3f), ship.Position);
        Assert.Empty(ScenarioWire.RemovedGuids(create));

        byte[] outOfRange = [1, 0, 0, 0, 1, 4, 1, 0, 0, 0, 0x01, 0x51]; // the out-of-range list naming the ship
        Assert.Equal([0x51ul], ScenarioWire.RemovedGuids(outOfRange));
        Assert.Empty(ScenarioWire.FieldUpdates(outOfRange));

        // At login the ships come first (vmangos Map::Add: SendInitTransports before SendInitSelf); the login skips that packet and
        // only that packet: not a self create, not an ordinary update, not the out-of-range list.
        Assert.True(ScenarioWire.IsMapTransportsPacket(create));
        Assert.False(ScenarioWire.IsMapTransportsPacket([1, 0, 0, 0, 0, .. SelfCreateBlockVector()]));
        Assert.False(ScenarioWire.IsMapTransportsPacket([1, 0, 0, 0, 1, .. SelfCreateBlockVector()]));
        Assert.False(ScenarioWire.IsMapTransportsPacket([.. create[..4], 0, .. create[5..]]));
        Assert.False(ScenarioWire.IsMapTransportsPacket(outOfRange));
        Assert.False(ScenarioWire.IsMapTransportsPacket(create[..^1]));

        // The byte is a flag: any other value is still refused.
        create[4] = 2;
        Assert.Throws<MockProtocolException>(() => ScenarioWire.FieldUpdates(create));
        outOfRange[4] = 2;
        Assert.Throws<MockProtocolException>(() => ScenarioWire.RemovedGuids(outOfRange));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(129u)]
    [InlineData(uint.MaxValue)]
    public void InitialSelfCreate_RejectsCountsBeyondItsBlockLimit(uint count)
    {
        byte[] body = [1, 0, 0, 0, 0, .. SelfCreateBlockVector()];
        BinaryPrimitives.WriteUInt32LittleEndian(body, count);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate(body));
    }

    [Theory]
    [InlineData(4, 1)] // transport header
    [InlineData(5, 3)] // self must use CREATE_OBJECT
    [InlineData(10, 0x70)] // SELF update flag is required
    [InlineData(11, 1)] // optional movement flags are forbidden
    [InlineData(35, 1)] // nonzero fall time
    [InlineData(63, 0)] // ALL trailer must be one
    [InlineData(72, 0x35)] // object GUID fields must agree with packed GUID
    [InlineData(80, 3)] // self fields must describe a player
    public void InitialSelfCreate_PreservesStrictPlayerChecks(int offset, int value)
    {
        byte[] body = [1, 0, 0, 0, 0, .. SelfCreateBlockVector()];
        body[offset] = (byte)value;
        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate(body));
    }

    [Fact]
    public void InitialSelfCreate_RequiresPositiveSpeedAndBothGuidFields()
    {
        byte[] self = SelfCreateBlockVector();
        byte[] zeroSpeed = [1, 0, 0, 0, 0, .. self];
        BinaryPrimitives.WriteUInt32LittleEndian(zeroSpeed.AsSpan(39), 0);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate(zeroSpeed));
        // The high GUID word is zero, but its omission still makes the create incomplete.
        byte[] missingHighWord = [1, 0, 0, 0, 0, .. self[..62],
            1, 5, 0, 0, 0, 0x34, 0x12, 0, 0, 0x19, 0, 0, 0];
        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate(missingHighWord));
    }

    [Theory]
    [InlineData("A47DD4CD70DA1B0EF7E1FA8C02DE68AF0CEFCC77ACA287FBC3ADCDE0E7B78FE7", "7186DF27C1A309B5B26E293CD00ADD01E7037E09116089F26E810FD2D962BC42")]
    [InlineData("FEF24F6DBCE6FBC39666B928574B862EABD39DE8ABA94BA1CEB701EBEB4BB511", "62741F2045A1934CFCCDD2C4DD465C19002BFD11BDE5C0996E102AB682A69743")]
    public void ClientSrpPublicKey_MatchesPinnedVector(string privateKey, string expected)
        => Assert.Equal(LittleEndian(expected, 32), ClientSrp6.PublicKey(LittleEndian(privateKey, 32)));

    [Theory]
    [InlineData(
        "E232D2C71AD1BF58DB9F7DBE51FFE271B6BDC61524F2E6B32ABFFFCAB09D09AB",
        "FC3D610C4E2CEC5ECC7E47344D0ED81D2ACB938AB198EC7E2ED474AEFCC3ABD1",
        "A4A7CB7DFBE00D26EE06F6B3DACC51E5779D7E8B", "FDAFAEF0E77F0FE1BD2956CF1820D4BC964E5283",
        "3898DF5193EA6AA8111524A253DB480A51EA6160D1E41BC4B662420299B4A435")]
    [InlineData(
        "8CBDF6ADB7AB7C440ADF2A6EF35504A16D0CFC1D6BDB2B9D490A9FE0DBFC2ED4",
        "32B7EF3E95B0F0B8DDCDAAEDFA8763B50CA388D37CE6DC9EECDB621A5C844B25",
        "DE80EB1158911D56B8FDC761DC0AEA0C7C9D7EFD", "BAFAEBD30DC342C57F28C0CDB68DFD238AB2CACF",
        "879E097E9C97C6F5AB5970FBAD87C038405E0CBF401F8864FD0DADD5FAF2CB76")]
    public void ClientSrpSharedSecret_MatchesPinnedVector(string server, string secret, string x, string u, string expected)
        => Assert.Equal(LittleEndian(expected, 32), ClientSrp6.SharedSecret(
            LittleEndian(server, 32), LittleEndian(secret, 32), LittleEndian(x, 20), LittleEndian(u, 20)));

    [Theory]
    [InlineData("FCCC2DFFED3E8D0D77EAD3E230D9C12BFFA4F3D41BE507C0E534FC0DD6EB4C8F", "6C558DD116725AE527707A08C3E4BE3181B5E622343403807389BF42BC3AB61A89AC8DE01A4E14EE")]
    [InlineData("094FDE5CD68EEEF5F6FB8B0DAB05D68AF7886A03ABEADD8231FAC47FE0BDC1CC", "6D9EADFB4A0C13733697EE3CFDE55401D75973221ACC20E267F544492D3E29A41735FCC30637A22A")]
    public void ClientSrpInterleave_MatchesPinnedVector(string shared, string expected)
        => Assert.Equal(LittleEndian(expected, 40), ClientSrp6.Interleave(LittleEndian(shared, 32)));

    [Fact]
    public void ClientSrpProof_PreservesFixedWidthLeadingZeros()
    {
        byte[] actual = ClientSrp6.Proof("7WG6SHZL33JMGPO4",
            LittleEndian("00A4A09E0B5ACA438B8CD837D0816CA26043DBD1EAEF138EEF72DCF3F696D03D", 32),
            LittleEndian("0095FE039AFE5E1BADE9AC0CAEC3CB73D2D08BBF4CA8ADDBCDF0CE709ED5103F", 32),
            LittleEndian("00B0C41F58CCE894CFB816FA72CA344C9FE2ED7CE799452ADBA7ABDCD26EAE75", 32),
            LittleEndian("2F409C9AEC0FE203D3673202D57BEA19C931AACBD1FD75C539C34129BD70F83E37BFC0F99CD3A477", 40));
        Assert.Equal(LittleEndian("7D07022B4064CCE633D679F61C6B212B6F8BC5C3", 20), actual);
    }

    [Fact]
    public void ServerSrpProof_MatchesPinnedVector()
    {
        byte[] actual = ClientSrp6.Hash(
            LittleEndian("BFD1AC65C8DAAAD88BF9DFF9AF8D1DCDF11DFD0C7E398EDCDF5DBBD08EFB39D3", 32),
            LittleEndian("7EBBC190D9AB2DC0CD891372CB30DF1ED35CDA1E", 20),
            LittleEndian("4876E68F9FCCB6CA9BC9C9BCEBDB36F2358B6EAD0F17881D811891A9888E8E5B10E1162CE8B58293", 40));
        Assert.Equal(LittleEndian("269E3A3EF5DCD15944F043513BDA20D20FEBA2E0", 20), actual);
    }

    [Fact]
    public void WorldAuthProof_MatchesPinnedVector()
    {
        byte[] key = LittleEndian("914D6219A99109D6BD946F6E6AF12BB611C59A22531C6F1A3F3CF58624D528DC163BE43813112C3D", 40);
        // Upstream hex_decode_be reverses byte pairs; this digest is printed as a big-endian integer.
        Assert.Equal(LittleEndian("6095EB678CD195253F66F32BADA785CA6D9376B2", 20),
            ProtocolPackets.WorldProof("TNDQWSHEBWHPABV2", key, 1454143186, 309086257));
    }

    [Fact]
    public void WorldAuthPayload_HasIndependentFixedFieldsAndCapturedProof()
    {
        byte[] body = ProtocolPackets.WorldAuth("A", CaptureKey, 12589856, 0xDEADBEEF, 5875);
        Assert.Equal(new byte[] { 0xF3, 0x16, 0, 0, 0, 0, 0, 0, 0x41, 0 }, body[..10]);
        Assert.Equal(12589856u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10)));
        Assert.Equal(new byte[] { 26, 97, 90, 187, 176, 134, 53, 49, 75, 160, 129, 47, 67, 207, 231, 42, 234, 184, 227, 124 }, body[14..34]);
        Assert.Equal(new byte[4], body[34..]);
    }

    [Fact]
    public void ClientHeaderEncryption_MatchesConsecutiveCapturedHeaders()
    {
        byte[] key = [9, 83, 75, 103, 5, 182, 16, 162, 170, 134, 230, 117, 11, 100, 136, 74, 88, 145, 175, 126, 216, 48, 38, 40, 234, 116, 174, 149, 133, 20, 193, 51, 103, 223, 194, 141, 4, 191, 161, 96];
        var cipher = new ClientHeaderCipher(key);
        byte[][] expected = [[9, 96, 220, 67, 72, 254], [14, 188, 50, 185, 159, 20], [31, 135, 219, 38, 126, 15], [190, 48, 52, 101, 139, 179]];
        for (int index = 0; index < expected.Length; index++)
        {
            byte[] header = index == 0 ? [0, 4, 55, 0, 0, 0] : [0, 12, 0xDC, 1, 0, 0];
            cipher.Encrypt(header);
            Assert.Equal(expected[index], header);
        }
    }

    [Fact]
    public void ServerHeaderDecryption_MatchesConsecutiveCapturedHeaders()
    {
        var cipher = new ClientHeaderCipher(CaptureKey);
        byte[][] encrypted = [[239, 86, 206, 186], [104, 222, 119, 123], [5, 67, 190, 101], [239, 141, 238, 129]];
        byte[][] expected = [[0, 12, 0xEE, 1], [0, 170, 59, 0], [0, 6, 0xDD, 1], [0, 6, 0xDD, 1]];
        for (int index = 0; index < encrypted.Length; index++)
        {
            cipher.Decrypt(encrypted[index]);
            Assert.Equal(expected[index], encrypted[index]);
        }
    }

    [Theory]
    [InlineData("192.0.2.1:8085")]
    [InlineData("[2001:db8::1]:8085")]
    [InlineData("example.com:8085")]
    [InlineData("127.0.0.1:0")]
    public void RealmEndpoint_RejectsRemoteDnsAndZeroPort(string address)
        => Assert.ThrowsAny<Exception>(() => new MockRealm("Test", address).GetLoopbackEndpoint());

    [Theory]
    [InlineData("127.0.0.1:8085")]
    [InlineData("[::1]:8085")]
    [InlineData("[::ffff:127.0.0.1]:8085")]
    public void RealmEndpoint_AcceptsNumericLoopback(string address)
        => Assert.Equal(8085, new MockRealm("Test", address).GetLoopbackEndpoint().Port);

    [Fact]
    public async Task BothClients_RejectRemoteEndpointsBeforeConnecting()
    {
        var remote = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 8085);
        await Assert.ThrowsAsync<ArgumentException>(() => WorldClient.ConnectAsync(remote));
        await Assert.ThrowsAsync<ArgumentException>(() => LogonClient.AuthenticateAsync(remote, "TEST", "TEST"));
    }

    [Fact]
    public void RealmList_ParsesIndependentLiteralAndRejectsTruncation()
    {
        byte[] body = Convert.FromHexString("00000000010000000000417263616E65003132372E302E302E313A38303835000000803F0203000200");
        MockRealm realm = Assert.Single(ProtocolPackets.RealmList(body));
        Assert.Equal("Arcane", realm.Name);
        Assert.Equal("127.0.0.1:8085", realm.Address);
        Assert.Throws<MockProtocolException>(() => ProtocolPackets.RealmList(body[..^1]));
    }

    [Fact]
    public async Task ExactReader_HandlesFragmentedReadsAndReportsPartialEof()
    {
        await using var stream = new OneByteStream([1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, await ProtocolIO.ReadExactAsync(stream, 3, "fixture", CancellationToken.None));
        await using var partial = new OneByteStream([1, 2]);
        EndOfStreamException exception = await Assert.ThrowsAsync<EndOfStreamException>(() => ProtocolIO.ReadExactAsync(partial, 3, "fixture", CancellationToken.None));
        Assert.Contains("2 of 3", exception.Message);
    }

    [Fact]
    public async Task DefaultDeadline_ReportsUsefulTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ProtocolIO.DefaultOperationTimeout);
        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() => ProtocolIO.BoundedAsync("fixture stall", ProtocolIO.DefaultOperationTimeout, CancellationToken.None, async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }));
        Assert.Contains("fixture stall", exception.Message);
        Assert.Contains("5-second", exception.Message);
    }

    [Theory]
    [InlineData("0001EC01", typeof(MockProtocolException))]
    [InlineData("FFFFEC01", typeof(MockProtocolException))]
    [InlineData("0006EC", typeof(EndOfStreamException))]
    [InlineData("0006EC010102", typeof(EndOfStreamException))]
    public async Task WorldReader_RejectsMalformedAndPartialFrames(string bytes, Type exceptionType)
    {
        await WithWorldPeerAsync(async stream =>
        {
            await stream.WriteAsync(Convert.FromHexString(bytes));
            stream.Close();
        }, async client =>
        {
            Exception? exception = await Record.ExceptionAsync(() => client.ReadAsync());
            Assert.NotNull(exception);
            Assert.IsType(exceptionType, exception);
        });
    }

    [Fact]
    public async Task WorldReader_RejectsOverlappingReadersAndHonorsCancellation()
    {
        await WithWorldPeerAsync(_ => Task.CompletedTask, async client =>
        {
            using var cancellation = new CancellationTokenSource();
            Task<WorldFrame> first = client.ReadAsync(cancellation.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ReadAsync());
        });
    }

    [Fact]
    public async Task WorldPacketSearch_StopsAtItsPacketLimit()
    {
        await WithWorldPeerAsync(async stream => await stream.WriteAsync(new byte[] { 0, 2, 0x37, 0 }), async client =>
        {
            MockProtocolException exception = await Assert.ThrowsAsync<MockProtocolException>(() => client.ReadUntilAsync(0x01DD, maxPackets: 1));
            Assert.Contains("within 1 packets", exception.Message);
        });
    }

    [Theory]
    [InlineData((byte)0x0D)]
    [InlineData((byte)0x14)]
    [InlineData((byte)0x15)]
    public async Task WorldAuth_ReturnsPlaintextFailureWithoutWaitingForAddon(byte result)
    {
        await WithWorldPeerAsync(async stream =>
        {
            await stream.WriteAsync(new byte[] { 0, 6, 0xEC, 1, 1, 2, 3, 4 });
            await ReadClientPacketAsync(stream);
            await stream.WriteAsync(new byte[] { 0, 3, 0xEE, 1, result });
        }, async client => Assert.Equal(result, await client.AuthenticateAsync("TEST", CaptureKey)));
    }

    [Fact]
    public async Task WorldAuth_ConsumesEncryptedAddonAndRetainsReceiveCipherState()
    {
        await WithWorldPeerAsync(async stream =>
        {
            await stream.WriteAsync(new byte[] { 0, 6, 0xEC, 1, 1, 2, 3, 4 });
            byte[] request = await ReadClientPacketAsync(stream);
            Assert.Equal(0x01EDu, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(2)));
            Assert.Equal(5875u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(6)));
            // Independently computed literal continuation of the pinned CaptureKey cipher stream.
            await stream.WriteAsync(new byte[] { 239, 86, 206, 186, 0x0C, 0, 0, 0, 0, 0, 0, 0, 0, 0, 104, 70, 147, 153, 35, 97, 220, 131, 0x78, 0x56, 0x34, 0x12 });
        }, async client =>
        {
            Assert.Equal((byte)0x0C, await client.AuthenticateAsync("TEST", CaptureKey));
            WorldFrame pong = await client.ReadAsync();
            Assert.Equal((ushort)0x01DD, pong.Opcode);
            Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, pong.Payload);
        });
    }

    [Fact]
    public async Task LogonAuth_ReturnsFailureResult()
    {
        await WithLogonPeerAsync(async stream =>
        {
            await ReadLogonChallengeAsync(stream);
            await stream.WriteAsync(new byte[] { 0, 0, 4 });
        }, async endpoint =>
        {
            MockAuthException exception = await Assert.ThrowsAsync<MockAuthException>(() => LogonClient.AuthenticateAsync(endpoint, "TEST", "TEST"));
            Assert.Equal((byte)4, exception.Result);
        });
    }

    [Fact]
    public async Task LogonAuth_VerifiesM2BeforeReturningSessionKey()
    {
        await WithLogonPeerAsync(async stream =>
        {
            await ReadLogonChallengeAsync(stream);
            byte[] challenge = new byte[119];
            challenge[3] = 1; // nonzero server public key, deliberately unrelated to an account
            challenge[35] = 1;
            challenge[36] = 7;
            challenge[37] = 32;
            Convert.FromHexString("B79B3E2A87823CAB8F5EBFBF8EB10108535006298B5BADBD5B53E1895E644B89").CopyTo(challenge, 38);
            await stream.WriteAsync(challenge);
            byte[] proof = new byte[75];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await stream.ReadExactlyAsync(proof, timeout.Token);
            Assert.Equal((byte)1, proof[0]);
            byte[] badM2 = new byte[26];
            badM2[0] = 1;
            await stream.WriteAsync(badM2);
        }, async endpoint =>
        {
            MockProtocolException exception = await Assert.ThrowsAsync<MockProtocolException>(() => LogonClient.AuthenticateAsync(endpoint, "TEST", "TEST"));
            Assert.Contains("M2 did not verify", exception.Message);
        });
    }

    // Independently assembled protocol fixtures, using synthetic IDs. Layout reference:
    // vmangos/core @ 4b3d241cffe245a1f68da11380bce96c23db48c0,
    // src/game/Server/Packets/Quest.cpp: QuestGiverOfferReward, QuestGiverQuestComplete,
    // QuestUpdateAddKill and QuestgiverChooseReward. No upstream source code or captures copied.
    private static byte[] QuestOfferRewardVector() => Convert.FromHexString(
        "0807060504030201" + // full GUID
        "A3BB0D00" + // quest 900003
        "52657761726400" + // title: Reward
        "446F6E6500" + // offer text: Done
        "0100000001000000" + // enable next and one emote
        "E803000005000000" + // delay 1000, emote 5
        "02000000" + // two choices
        "C9BB0D00010000000B000000" + // item 900041, count 1, display 11
        "CABB0D00030000000C000000" + // item 900042, count 3, display 12
        "01000000" + // one fixed reward
        "C8BB0D00020000000D000000" + // item 900040, count 2, display 13
        "D20400000000000000000000"); // money 1234, flags 0, reward spell 0

    private static byte[] QuestCompleteVector() => Convert.FromHexString(
        "A3BB0D000300000000000000D204000001000000C8BB0D0002000000");

    // Independent update fixtures: item CREATE blocks carry ALL, then a field mask;
    // the self player carries SELF/ALL/LIVING/HAS_POSITION and six positive speeds.
    private static byte[] FirstItemCreateVector() => Convert.FromHexString(
        "02810140011001000000010F000000010000000000004003000000C8BB0D00");

    private static byte[] SecondItemCreateVector() => Convert.FromHexString(
        "02810240011001000000010F000000020000000000004003000000CABB0D00");

    private static byte[] SelfCreateBlockVector() => Convert.FromHexString(
        "020334120471" + // CREATE_OBJECT, packed GUID 0x1234, player, self update flags
        "0000000001000000" + // no movement flags, clock one
        "0000803F000000400000404000008040" + // x/y/z/orientation: 1/2/3/4
        "00000000" + // zero fall time
        "0000803F0000803F0000803F0000803F0000803F0000803F" + // six speeds of one
        "01000000" + // ALL trailer
        "0107000000341200000000000019000000"); // fields: full GUID and player type

    private static byte[] LittleEndian(string hex, int width)
    {
        byte[] bytes = Convert.FromHexString(hex.PadLeft(width * 2, '0'));
        Array.Reverse(bytes);
        return bytes;
    }

    private static async Task WithWorldPeerAsync(Func<NetworkStream, Task> server, Func<WorldClient, Task> action)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using TcpClient peer = await accept;
        Task handler = server(peer.GetStream());
        await action(client);
        await handler.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task WithLogonPeerAsync(Func<NetworkStream, Task> server, Func<IPEndPoint, Task> action)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<TcpClient> accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        Task clientTask = action((IPEndPoint)listener.LocalEndpoint);
        using TcpClient peer = await accept;
        Task handler = server(peer.GetStream());
        await clientTask.WaitAsync(timeout.Token);
        await handler.WaitAsync(timeout.Token);
    }

    private static async Task<byte[]> ReadClientPacketAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[6];
        await stream.ReadExactlyAsync(header, timeout.Token);
        byte[] body = new byte[BinaryPrimitives.ReadUInt16BigEndian(header) - 4];
        await stream.ReadExactlyAsync(body, timeout.Token);
        return [.. header, .. body];
    }

    private static async Task ReadLogonChallengeAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, timeout.Token);
        byte[] body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2))];
        await stream.ReadExactlyAsync(body, timeout.Token);
        Assert.Equal((byte)3, header[1]);
    }

    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
