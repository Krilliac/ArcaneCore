using System.Reflection;
using System.Text.RegularExpressions;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using Xunit;

namespace ArcaneCore.World.Tests.PacketHeuristics;

/// <summary>
/// Pins <see cref="PacketShapeTable"/> to the vendored wire oracle (tests/ThirdParty/WowWorldMessages, vanilla): the table is the fixed-length subset
/// of what the oracle declares, so it can neither drift from it nor list a message whose layout is not fixed.
/// </summary>
public sealed partial class PacketShapeTableTests
{
    [GeneratedRegex(@"WriteClientHeaderAsync\(w, (?<size>\d+), (?<opcode>\d+),", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralHeader();

    private static IReadOnlyDictionary<int, (string Message, int BodyLength)> OracleFixed()
    {
        string dir = Path.Combine(RepositorySource.RequireRoot(), "tests", "ThirdParty", "WowWorldMessages", "src", "vanilla");
        var result = new Dictionary<int, (string, int)>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith("CMSG_", StringComparison.Ordinal) && !name.StartsWith("MSG_", StringComparison.Ordinal))
            {
                continue;
            }

            Match match = LiteralHeader().Match(File.ReadAllText(file));
            if (match.Success)
            {
                result[int.Parse(match.Groups["opcode"].Value, System.Globalization.CultureInfo.InvariantCulture)] =
                    (name, int.Parse(match.Groups["size"].Value, System.Globalization.CultureInfo.InvariantCulture) - 4);
            }
        }

        return result;
    }

    /// <summary>Rows where the server deliberately differs from the oracle: the table holds the server length (the oracle length is recorded for the pin).</summary>
    private static readonly Dictionary<WorldOpcode, (int Oracle, int Server, string Why)> ServerOverrides = new()
    {
        // The server reads a u32 reputation-list index like vmangos; the vendored wowm oracle types it u16 (ReputationPackets.cs:6-13, docs/integration/reputation.md).
        [WorldOpcode.CmsgSetFactionAtwar] = (3, 5, "u32 list slot + u8 flag"),
        [WorldOpcode.CmsgSetFactionInactive] = (3, 5, "u32 list slot + u8 flag"),
        [WorldOpcode.CmsgSetWatchedFaction] = (2, 4, "i32 list slot"),
    };

    /// <summary>Fixed oracle messages left out of the table because the server handler does not agree with (or does not use) the oracle length.</summary>
    private static readonly Dictionary<WorldOpcode, string> Removed = new()
    {
        [WorldOpcode.CmsgBootme] = "no handler: the server drops it as unknown",
        [WorldOpcode.MsgLookingForGroup] = "the handler never reads the body and answers regardless; an empty request is UNVERIFIED against a client",
        [WorldOpcode.CmsgMailCreateTextItem] = "the handler reads 12 bytes (mailbox, mail id), the oracle declares 16",
        [WorldOpcode.CmsgAcceptTrade] = "the handler ignores the body entirely",
        [WorldOpcode.CmsgSetTargetObsolete] = "a no-op handler that ignores the body",
        [WorldOpcode.CmsgCancelChannelling] = "the handler ignores the body (the spell id is not read)",
        [WorldOpcode.CmsgItemTextQuery] = "the handler reads 4 bytes (the item text id), the oracle declares 12, and nothing documents the rest",
    };

    [Fact]
    public void EveryRow_EqualsTheOraclesLiteralSize_ExceptTheCommentedOverridesAndRemovals()
    {
        IReadOnlyDictionary<int, (string Message, int BodyLength)> oracle = OracleFixed();
        Assert.True(oracle.Count > 150, $"the oracle scan found only {oracle.Count} fixed-size messages");

        IReadOnlyList<(WorldOpcode Opcode, int Length)> rows = PacketShapeTable.Rows();
        Assert.Equal(oracle.Count - Removed.Count, rows.Count);
        foreach ((WorldOpcode opcode, int length) in rows)
        {
            Assert.True(oracle.TryGetValue((int)opcode, out var entry), $"{WorldOpcodeNames.GetName((uint)opcode)} is in the table but not a fixed message of the oracle");
            Assert.DoesNotContain(opcode, Removed.Keys);
            if (ServerOverrides.TryGetValue(opcode, out var over))
            {
                Assert.Equal(over.Oracle, entry.BodyLength);
                Assert.Equal(over.Server, length);
            }
            else
            {
                Assert.True(entry.BodyLength == length, $"{entry.Message}: table {length}, oracle {entry.BodyLength}");
            }

            Assert.Equal(length, PacketShapeTable.FixedLength(opcode));
        }

        foreach ((int code, (string message, _)) in oracle)
        {
            var opcode = (WorldOpcode)code;
            if (Removed.ContainsKey(opcode))
            {
                Assert.Equal(PacketShapeTable.NotFixed, PacketShapeTable.FixedLength(opcode));
            }
            else
            {
                Assert.True(PacketShapeTable.FixedLength(opcode) != PacketShapeTable.NotFixed, $"{message} is a fixed oracle message the server agrees with but is missing from the table");
            }
        }

        foreach (WorldOpcode removed in Removed.Keys)
        {
            Assert.True(oracle.ContainsKey((int)removed), $"{removed} is listed as removed but is not a fixed oracle message any more");
        }
    }

    [Fact]
    public void WhereTheServerHasAnExactParser_ItAcceptsTheRowLength_AndRefusesOneByteMoreOrLess()
    {
        // The server's own exact-length parsers, by opcode: every one must accept a body of exactly the table length and refuse length - 1 and length + 1.
        var exact = new Dictionary<WorldOpcode, Func<byte[], bool>>
        {
            [WorldOpcode.CmsgSetFactionAtwar] = b => ArcaneCore.Game.Reputation.ReputationPackets.TryReadSetAtWar(b, out _, out _),
            [WorldOpcode.CmsgSetFactionInactive] = b => ArcaneCore.Game.Reputation.ReputationPackets.TryReadSetInactive(b, out _, out _),
            [WorldOpcode.CmsgSetWatchedFaction] = b => ArcaneCore.Game.Reputation.ReputationPackets.TryReadSetWatched(b, out _),
            [WorldOpcode.CmsgBattlemasterJoin] = b => ArcaneCore.Game.Battlegrounds.BattlegroundPackets.TryParseBattlemasterJoin(b, out _),
            [WorldOpcode.CmsgBattlefieldPort] = b => ArcaneCore.Game.Battlegrounds.BattlegroundPackets.TryParseBattlefieldPort(b, out _),
            [WorldOpcode.CmsgBattlefieldList] = b => ArcaneCore.Game.Battlegrounds.BattlegroundPackets.TryParseMap(b, out _),
            [WorldOpcode.CmsgBattlefieldJoin] = b => ArcaneCore.Game.Battlegrounds.BattlegroundPackets.TryParseMap(b, out _),
            [WorldOpcode.CmsgLeaveBattlefield] = b => ArcaneCore.Game.Battlegrounds.BattlegroundPackets.TryParseMap(b, out _),
            [WorldOpcode.CmsgBattlemasterHello] = b => ArcaneCore.Game.Battlegrounds.BattlegroundPackets.TryParseGuid(b, out _),
            [WorldOpcode.CmsgAreaSpiritHealerQuery] = b => ArcaneCore.Game.Battlegrounds.BattlegroundPackets.TryParseGuid(b, out _),
            [WorldOpcode.CmsgAreaSpiritHealerQueue] = b => ArcaneCore.Game.Battlegrounds.BattlegroundPackets.TryParseGuid(b, out _),
        };

        foreach ((WorldOpcode opcode, Func<byte[], bool> accepts) in exact)
        {
            int length = PacketShapeTable.FixedLength(opcode);
            Assert.True(length > 0, $"{opcode} has no table row");
            byte[] body = new byte[length];
            Assert.True(accepts(body), $"{opcode}: the server's parser refuses its own table length {length}");
            Assert.False(accepts(new byte[length - 1]), $"{opcode}: the server's parser accepts {length - 1} bytes");
            Assert.False(accepts(new byte[length + 1]), $"{opcode}: the server's parser accepts {length + 1} bytes");
        }
    }

    [Fact]
    public void AReputationMessage_AtTheServersLength_IsNotAShapeFinding()
    {
        Assert.Equal(5, PacketShapeTable.FixedLength(WorldOpcode.CmsgSetFactionAtwar));
        Assert.Equal(5, PacketShapeTable.FixedLength(WorldOpcode.CmsgSetFactionInactive));
        Assert.Equal(4, PacketShapeTable.FixedLength(WorldOpcode.CmsgSetWatchedFaction));
    }

    [Fact]
    public async Task EveryRow_DecodesExactlyItsLengthInTheOraclesReader_AndFailsOneByteShort()
    {
        Assembly oracle = typeof(WowWorldMessages.Vanilla.CMSG_PING).Assembly;
        int decoded = 0;
        foreach ((WorldOpcode opcode, int length) in PacketShapeTable.Rows())
        {
            if (ServerOverrides.ContainsKey(opcode))
            {
                continue; // the server length, which the oracle's reader does not read
            }

            string name = WorldOpcodeNames.GetName((uint)opcode);
            Type? type = oracle.GetType("WowWorldMessages.Vanilla." + name);
            MethodInfo? read = type?.GetMethod("ReadBodyAsync", BindingFlags.Public | BindingFlags.Static, [typeof(Stream), typeof(CancellationToken)]);
            if (read is null)
            {
                continue;
            }

            using var exact = new MemoryStream(new byte[length]);
            await (Task)read.Invoke(null, [exact, CancellationToken.None])!;
            Assert.True(exact.Position == length, $"{name}: the oracle read {exact.Position} of {length} bytes");
            decoded++;

            if (length > 0)
            {
                using var shortStream = new MemoryStream(new byte[length - 1]);
                await Assert.ThrowsAnyAsync<Exception>(async () => await (Task)read.Invoke(null, [shortStream, CancellationToken.None])!);
            }
        }

        Assert.True(decoded > 150, $"only {decoded} rows could be decoded through the oracle");
    }

    [Fact]
    public void AVariableMessage_IsNeverChecked()
    {
        Assert.Equal(PacketShapeTable.NotFixed, PacketShapeTable.FixedLength(WorldOpcode.CmsgCastSpell));
        Assert.Equal(PacketShapeTable.NotFixed, PacketShapeTable.FixedLength((WorldOpcode)5000));
        Assert.Equal(0, PacketShapeTable.FixedLength(WorldOpcode.CmsgCharEnum));
        Assert.Equal(8, PacketShapeTable.FixedLength(WorldOpcode.CmsgPlayerLogin));
    }
}
