using System.Text;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests.Replay;

/// <summary>
/// Byte-exact replay of the build-5875 wire conversation (multi-version design S0, docs/design/multi-version.md §5):
/// logon challenge/proof, realm list, world auth, char create/enum, player login with its update-object sequence,
/// movement and logout. The golden transcript was recorded from the code before the protocol adapter was extracted;
/// every later change must reproduce it exactly. Random or clock-derived bytes are masked by <see cref="ReplayTranscript"/>
/// with a documented reason each.
/// Re-record (only for an intended protocol change): ARCANECORE_REPLAY_RECORD=1 dotnet test --filter ReplayTranscript.
/// </summary>
public sealed class ReplayTranscriptTests
{
    private const string GoldenName = "vanilla-5875.transcript";

    [Fact]
    public async Task Vanilla5875_LoginToLogout_MatchesTheRecordedTranscriptByteForByte()
    {
        string actual = await RecordAsync();
        string goldenPath = Path.Combine(AppContext.BaseDirectory, "Replay", GoldenName);
        if (Environment.GetEnvironmentVariable("ARCANECORE_REPLAY_RECORD") == "1")
        {
            string source = Path.Combine(FindRepoRoot(), "tests", "ArcaneCore.MockClient.Tests", "Replay", GoldenName);
            await File.WriteAllTextAsync(source, actual);
            return;
        }

        string expected = await File.ReadAllTextAsync(goldenPath);
        AssertSameTranscript(expected, actual);
    }

    [Fact]
    public async Task Vanilla5875_Transcript_IsDeterministicAcrossRuns()
    {
        AssertSameTranscript(await RecordAsync(), await RecordAsync());
    }

    private static void AssertSameTranscript(string expected, string actual)
    {
        string[] e = expected.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        string[] a = actual.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        for (int i = 0; i < Math.Min(e.Length, a.Length); i++)
        {
            Assert.True(e[i] == a[i], $"transcript line {i + 1} differs\nexpected: {e[i]}\nactual:   {a[i]}");
        }

        Assert.True(e.Length == a.Length, $"transcript has {a.Length} lines, expected {e.Length}");
    }

    internal static async Task<string> RecordAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = deadline.Token;
        var transcript = new ReplayTranscript();
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(ct);
        await server.AddAccountAsync("REPLAY", "PASSWORD", ct);

        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, "REPLAY", "PASSWORD", ct, tap: transcript.Logon);
        await using WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), ct);
        client.Tap = transcript.World;
        Assert.Equal((byte)0x0C, await client.AuthenticateAsync("REPLAY", logon.SessionKey, ct));

        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Replayhero", ct);
        ulong guid = Assert.Single(await session.EnumerateAsync(ct)).Guid;
        MockLogin login = await session.LoginAsync(guid, ct);

        transcript.Mark("movement and logout", unordered: true);
        MockLocation at = login.Location;
        uint time = 1000;
        await session.SendAsync(WorldOpcode.MsgMoveStartForward, Movement(1, at.X, at.Y, at.Z, at.Orientation, time), ct);
        await session.SendAsync(WorldOpcode.MsgMoveHeartbeat, Movement(1, at.X + 1, at.Y, at.Z, at.Orientation, time + 500), ct);
        await session.SendAsync(WorldOpcode.MsgMoveStop, Movement(0, at.X + 2, at.Y, at.Z, at.Orientation, time + 1000), ct);
        await session.LogoutAsync(ct);
        return transcript.ToString();
    }

    private static byte[] Movement(uint flags, float x, float y, float z, float orientation, uint clientTime)
    {
        byte[] body = StartingZoneProbe.Movement(x, y, z, orientation, clientTime);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, flags);
        return body;
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ArcaneCore.slnx")) || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }
}

/// <summary>Collects the conversation as text lines: direction, stream, opcode and hex payload, with volatile bytes masked as "..".</summary>
internal sealed class ReplayTranscript
{
    private readonly StringBuilder _text = new();
    private readonly object _gate = new();
    private readonly List<byte> _logonClient = [];
    private readonly List<byte> _logonServer = [];

    private readonly List<(int Start, int End)> _unorderedStages = [];

    /// <summary>
    /// Starts a stage. In an <paramref name="unordered"/> stage the map tick's update flush and the session's own replies race,
    /// so the server lines of that stage are sorted (their bytes stay exact; only their interleaving is normalized).
    /// </summary>
    internal void Mark(string stage, bool unordered = false)
    {
        lock (_gate)
        {
            CloseStage();
            _text.Append("# ").Append(stage).Append(unordered ? " (server order normalized)" : "").Append('\n');
            if (unordered)
            {
                _unorderedStages.Add((_text.Length, -1));
            }
        }
    }

    private void CloseStage()
    {
        if (_unorderedStages.Count > 0 && _unorderedStages[^1].End < 0)
        {
            _unorderedStages[^1] = (_unorderedStages[^1].Start, _text.Length);
        }
    }

    private string Body()
    {
        CloseStage();
        string text = _text.ToString();
        var result = new StringBuilder();
        int at = 0;
        foreach ((int start, int end) in _unorderedStages)
        {
            result.Append(text, at, start - at);
            string[] lines = text[start..end].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var client = lines.Where(l => l.StartsWith("C>", StringComparison.Ordinal));
            // The map's periodic update flush may land before or after the logout completes, so a late
            // SMSG_UPDATE_OBJECT/SMSG_COMPRESSED_UPDATE_OBJECT is not part of these stages (the login sequence covers update objects).
            var server = lines.Where(l => !l.StartsWith("C>", StringComparison.Ordinal)
                    && !l.StartsWith("S< world 00A9 ", StringComparison.Ordinal) && !l.StartsWith("S< world 01F6 ", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal);
            foreach (string line in client.Concat(server))
            {
                result.Append(line).Append('\n');
            }

            at = end;
        }

        return result.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>The logon streams are kept whole (socket reads may split differently between runs) and masked by fixed offset.</summary>
    internal void Logon(bool outbound, byte[] bytes)
    {
        lock (_gate)
        {
            (outbound ? _logonClient : _logonServer).AddRange(bytes);
        }
    }

    internal void World(bool outbound, ushort opcode, byte[] payload)
    {
        lock (_gate)
        {
            _text.Append(outbound ? "C> world " : "S< world ").Append(opcode.ToString("X4")).Append(' ')
                .Append(Mask(outbound, opcode, Convert.ToHexString(payload))).Append('\n');
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return "C> logon " + MaskRanges(_logonClient, LogonClientVolatile) + "\n"
                + "S< logon " + MaskLoopbackPort(MaskRanges(_logonServer, LogonServerVolatile)) + "\n"
                + Body();
        }
    }

    // Client stream: challenge (fixed for a fixed account and build), then proof 0x01 + A[32] + M1[20] + crc[20] + 2 bytes:
    // A, M1 and the CRC derive from random SRP values, so the 72 bytes after the proof command are masked.
    private static readonly (int Start, int End)[] LogonClientVolatile = [(LogonChallengeLength("REPLAY") + 1, LogonChallengeLength("REPLAY") + 73)];

    // Server stream: challenge = cmd, 0, result, B[32] (random), g_len, g, N_len, N[32], salt[32] (random per account),
    // version challenge[16] (random), security flags; then proof = cmd, result, M2[20] (random), survey id[4]; realm list fixed.
    private static readonly (int Start, int End)[] LogonServerVolatile = [(3, 35), (70, 102), (102, 118), (121, 141)];

    // The realm list advertises the fixture's ephemeral world port ("127.0.0.1:NNNNN"): the port digits are masked.
    private static string MaskLoopbackPort(string hex)
    {
        const string Loopback = "3132372E302E302E313A"; // "127.0.0.1:"
        int at = hex.IndexOf(Loopback, StringComparison.Ordinal);
        if (at < 0 || at % 2 != 0)
        {
            return hex;
        }

        int start = at + Loopback.Length;
        int end = start;
        while (end + 2 <= hex.Length && hex.Substring(end, 2) != "00")
        {
            end += 2;
        }

        return hex[..start] + new string('.', end - start) + hex[end..];
    }

    private static int LogonChallengeLength(string account) => 34 + account.Length;

    private static string MaskRanges(List<byte> bytes, (int Start, int End)[] ranges)
    {
        char[] hex = Convert.ToHexString(bytes.ToArray()).ToCharArray();
        foreach ((int start, int end) in ranges)
        {
            for (int i = start * 2; i < Math.Min(end * 2, hex.Length); i++)
            {
                hex[i] = '.';
            }
        }

        return new string(hex);
    }

    private static string Mask(bool outbound, ushort opcode, string hex)
    {
        return (outbound, opcode) switch
        {
            (false, (ushort)WorldOpcode.SmsgAuthChallenge) => Dots(hex.Length), // random server seed
            (true, (ushort)WorldOpcode.CmsgAuthSession) => MaskAuthSession(hex),
            (false, (ushort)WorldOpcode.SmsgLoginSettimespeed) => Dots(8) + hex[8..], // packed wall-clock game time
            (false, (ushort)WorldOpcode.SmsgUpdateObject) => UpdateObjectMask.Apply(Convert.FromHexString(hex)),
            (false, (ushort)WorldOpcode.SmsgCompressedUpdateObject) => "inflated:" + UpdateObjectMask.Apply(ScenarioWire.InflateUpdate(Convert.FromHexString(hex))),
            _ => hex,
        };
    }

    // CMSG_AUTH_SESSION: build u32, server id u32, account cstring, client seed u32, digest[20]: seed and digest are random.
    private static string MaskAuthSession(string hex)
    {
        int nul = 16;
        while (nul + 2 <= hex.Length && hex.Substring(nul, 2) != "00")
        {
            nul += 2;
        }

        int seedAt = nul + 2;
        int digestEnd = Math.Min(hex.Length, seedAt + 48);
        return hex[..seedAt] + Dots(digestEnd - seedAt) + hex[digestEnd..];
    }

    private static string Dots(int length) => new('.', length);
}

/// <summary>
/// Masks the server-clock bytes of an SMSG_UPDATE_OBJECT body (UpdateBlockWriter.WriteMovementBlock): a living object's
/// movement time ("now + 1000" when it never moved) and the transport/server time. Everything else stays byte-exact.
/// If a block can't be walked (an unexpected layout), the rest of the body is left unmasked so the test fails loudly.
/// </summary>
internal static class UpdateObjectMask
{
    internal static string Apply(byte[] body)
    {
        char[] hex = Convert.ToHexString(body).ToCharArray();
        try
        {
            Walk(body, hex);
        }
        catch (IndexOutOfRangeException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        return new string(hex);
    }

    private static void Walk(byte[] b, char[] hex)
    {
        int p = 0;
        uint count = U32(b, ref p);
        p++; // has-transport
        for (uint block = 0; block < count; block++)
        {
            byte type = b[p++];
            switch (type)
            {
                case 0: // values
                    PackedGuid(b, ref p);
                    Values(b, ref p);
                    break;
                case 2 or 3: // create object / create object 2
                    PackedGuid(b, ref p);
                    p++; // type id
                    Movement(b, ref p, hex);
                    Values(b, ref p);
                    break;
                case 4 or 5: // out of range / near objects
                    uint guids = U32(b, ref p);
                    for (uint i = 0; i < guids; i++)
                    {
                        PackedGuid(b, ref p);
                    }

                    break;
                default:
                    return;
            }
        }
    }

    private static void Movement(byte[] b, ref int p, char[] hex)
    {
        byte flags = b[p++];
        if ((flags & 0x20) != 0)
        {
            uint moveFlags = U32(b, ref p);
            MaskU32(hex, p);
            p += 4 + 16;
            if ((moveFlags & 0x02000000) != 0)
            {
                p += 8 + 16;
            }

            if ((moveFlags & 0x00200000) != 0)
            {
                p += 4;
            }

            p += 4; // fall time
            if ((moveFlags & 0x00002000) != 0)
            {
                p += 16;
            }

            if ((moveFlags & 0x04000000) != 0)
            {
                p += 4;
            }

            p += 24; // six speeds
        }
        else if ((flags & 0x40) != 0)
        {
            p += 16;
        }

        if ((flags & 0x08) != 0)
        {
            p += 4;
        }

        if ((flags & 0x10) != 0)
        {
            p += 4;
        }

        if ((flags & 0x04) != 0)
        {
            PackedGuid(b, ref p);
        }

        if ((flags & 0x02) != 0)
        {
            MaskU32(hex, p);
            p += 4;
        }
    }

    private static void Values(byte[] b, ref int p)
    {
        int blocks = b[p++];
        int set = 0;
        for (int i = 0; i < blocks; i++)
        {
            set += System.Numerics.BitOperations.PopCount(U32(b, ref p));
        }

        p += set * 4;
    }

    private static void PackedGuid(byte[] b, ref int p)
    {
        byte mask = b[p++];
        p += System.Numerics.BitOperations.PopCount(mask);
    }

    private static uint U32(byte[] b, ref int p)
    {
        uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
        p += 4;
        return value;
    }

    private static void MaskU32(char[] hex, int at)
    {
        for (int i = at * 2; i < (at + 4) * 2; i++)
        {
            hex[i] = '.';
        }
    }
}
