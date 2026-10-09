using System.Globalization;
using System.Text;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

public sealed class OpcodeReferenceTests
{
    [Fact]
    public void OpcodePage_MatchesRegistryAndWireHistogram() =>
        DocsGolden.Verify("docs/reference/opcodes.md", Render());

    [Fact]
    public void RegistryStatus_DistinguishesSpecialSessionPacketsAndMissingHandlers()
    {
        OpcodeTable table = WorldServiceCollectionExtensions.BuildOpcodeTable();
        Assert.Equal("handled", Status(WorldOpcode.CmsgPing, table));
        Assert.Equal("handled", Status(WorldOpcode.CmsgAuthSession, table));
        Assert.Equal("unhandled", Status(WorldOpcode.CmsgBootme, table));
        Assert.Equal("handled", Status(WorldOpcode.MsgMoveWorldportAck, table));
    }

    [Fact]
    public void BidirectionalMsgOpcodes_ShowClientHandlerStatus()
    {
        string row = Render().Split('\n').Single(l => l.StartsWith("| `MSG_MOVE_WORLDPORT_ACK`", StringComparison.Ordinal));
        Assert.Contains("| handled |", row);
    }

    internal static string Render()
    {
        OpcodeTable table = WorldServiceCollectionExtensions.BuildOpcodeTable();
        string histogram = RepoRoot.ReadText("docs/integration/wire-oracle-histogram-20261008.tsv");
        var emitted = new Dictionary<ushort, (long Count, string Decode)>();
        foreach (string line in histogram.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 4 || !ushort.TryParse(parts[1].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort opcode)
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long count))
                throw new InvalidDataException("invalid wire-oracle histogram row: " + line);
            emitted[opcode] = (count, parts[3]);
        }

        var builder = new StringBuilder();
        builder.AppendLine("# World opcode coverage (build 5875)").AppendLine();
        builder.AppendLine("Generated from `WorldOpcode.g.cs`, the live `OpcodeTable`, and [the wave 8 wire-oracle histogram](../integration/wire-oracle-histogram-20261008.tsv). Regenerate with `ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs`.").AppendLine();
        builder.AppendLine("`handled` means a client-sent CMSG or MSG (MSG opcodes travel both ways and get both columns) has a registered handler, or is handled directly by the session read loop. `unhandled` means it has no handler and is logged/dropped. The registry has no explicit ignore registration, so this snapshot has no `ignored` rows. SMSG counts are observed sends in the wave 8 test run, not a promise of live-client coverage.").AppendLine();
        builder.AppendLine("| Opcode | ID | CMSG status | SMSG emitted | Oracle |").AppendLine("| --- | ---: | --- | ---: | --- |");
        foreach (WorldOpcode opcode in Enum.GetValues<WorldOpcode>().OrderBy(o => (ushort)o))
        {
            string name = WorldOpcodeNames.GetName(opcode);
            // MSG_ opcodes travel both ways (vmangos src/game/Server/Protocol/Opcodes.cpp registers MSG_MOVE_* with a client handler and the server relays them).
            bool both = name.StartsWith("MSG_", StringComparison.Ordinal);
            bool client = both || name.StartsWith("CMSG_", StringComparison.Ordinal);
            bool server = both || name.StartsWith("SMSG_", StringComparison.Ordinal);
            if (!client && !server) continue;
            emitted.TryGetValue((ushort)opcode, out var sample);
            builder.Append("| `").Append(name).Append("` | `0x").Append(((ushort)opcode).ToString("X4", CultureInfo.InvariantCulture))
                .Append("` | ").Append(client ? Status(opcode, table) : "—").Append(" | ")
                .Append(server ? sample.Count.ToString(CultureInfo.InvariantCulture) : "—").Append(" | ")
                .Append(server && sample.Count > 0 ? sample.Decode : "—").AppendLine(" |");
        }
        return builder.ToString();
    }

    private static string Status(WorldOpcode opcode, OpcodeTable table) =>
        opcode is WorldOpcode.CmsgPing or WorldOpcode.CmsgAuthSession || table.TryGet(opcode, out _) ? "handled" : "unhandled";
}
