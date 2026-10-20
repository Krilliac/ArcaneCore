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

    [Fact]
    public void EveryRow_EqualsTheOraclesLiteralSize_AndNoFixedOracleMessageIsMissing()
    {
        IReadOnlyDictionary<int, (string Message, int BodyLength)> oracle = OracleFixed();
        Assert.True(oracle.Count > 150, $"the oracle scan found only {oracle.Count} fixed-size messages");

        IReadOnlyList<(WorldOpcode Opcode, int Length)> rows = PacketShapeTable.Rows();
        Assert.Equal(oracle.Count, rows.Count);
        foreach ((WorldOpcode opcode, int length) in rows)
        {
            Assert.True(oracle.TryGetValue((int)opcode, out var entry), $"{WorldOpcodeNames.GetName((uint)opcode)} is in the table but not a fixed message of the oracle");
            Assert.True(entry.BodyLength == length, $"{entry.Message}: table {length}, oracle {entry.BodyLength}");
            Assert.Equal(entry.BodyLength, PacketShapeTable.FixedLength(opcode));
        }

        foreach (int code in oracle.Keys)
        {
            Assert.NotEqual(PacketShapeTable.NotFixed, PacketShapeTable.FixedLength((WorldOpcode)code));
        }
    }

    [Fact]
    public async Task EveryRow_DecodesExactlyItsLengthInTheOraclesReader_AndFailsOneByteShort()
    {
        Assembly oracle = typeof(WowWorldMessages.Vanilla.CMSG_PING).Assembly;
        int decoded = 0;
        foreach ((WorldOpcode opcode, int length) in PacketShapeTable.Rows())
        {
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
