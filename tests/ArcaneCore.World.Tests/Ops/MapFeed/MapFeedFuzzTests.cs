using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// Deterministic, seeded fuzzers for <see cref="MapFeedProtocol"/>, <see cref="MapFeedLineReader"/>,
/// and live <see cref="WorldMapFeed"/> named-pipe sessions.
/// </summary>
public sealed class MapFeedFuzzTests
{
    public static TheoryData<int> Seeds => [1, 42, 20251004];

    private static readonly string[] ValidLines =
    [
        """{"op":"hello","protocol":1,"client":"fuzz"}""",
        """{"op":"subscribe","map":0,"instance":0,"hz":5,"kinds":["player","bot","creature","gameobject"],"paths":true}""",
        """{"op":"subscribe","map":1,"kinds":[],"paths":false}""",
        """{"op":"unsubscribe"}""",
        """{"op":"query","id":10,"kind":"los","map":0,"instance":0,"a":[-8949.95,-132.49,83.53],"b":[-8940.0,-130.0,84.0]}""",
        """{"op":"query","id":11,"kind":"height","map":0,"a":[-8949.95,-132.49,83.53]}""",
        """{"op":"query","id":12,"kind":"path","map":0,"a":[-8949.95,-132.49,83.53],"b":[-8935.0,-125.0,83.53]}""",
        """{"op":"query","id":13,"kind":"undermap","map":0,"a":[-8949.95,-132.49,83.53]}""",
    ];

    private static readonly string[] CorruptFragments =
    [
        "1e39",
        "-1e39",
        "1e309",
        "999999999999999999999999999",
        "-999999999999999999999999999",
        "17067",
        "-17067",
        "400001",
        "-400001",
        "null",
        "true",
        "false",
        "[]",
        "{}",
        "\"\\uD800\"",
        "\"\\u0000\"",
        new string('[', MapFeedProtocol.MaxJsonDepth + 4) + new string(']', MapFeedProtocol.MaxJsonDepth + 4),
    ];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void MapFeedProtocol_TryParse_UnderStructuredAndRandomMutations_NeverThrows(int seed)
    {
        var rng = new Random(seed);
        int accepted = 0;
        int refused = 0;

        for (int iteration = 0; iteration < 2000; iteration++)
        {
            byte[] payload = BuildFuzzedLine(rng, iteration);
            bool ok;
            FeedRequest request;
            FeedError error;
            try
            {
                ok = MapFeedProtocol.TryParse(payload, out request, out error);
            }
            catch (Exception ex)
            {
                Assert.Fail($"MapFeedProtocol.TryParse threw on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (ok)
            {
                accepted++;
                Assert.True(Enum.IsDefined(request.Op), $"Invalid FeedOp on seed={seed} iteration={iteration}");
                if (request.Op == FeedOp.Query)
                {
                    Assert.True(request.HasId);
                    Assert.True(float.IsFinite(request.A.X) && float.IsFinite(request.A.Y) && float.IsFinite(request.A.Z));
                    if (request.Kind is QueryKind.LineOfSight or QueryKind.Path)
                    {
                        Assert.True(float.IsFinite(request.B.X) && float.IsFinite(request.B.Y) && float.IsFinite(request.B.Z));
                    }
                }
            }
            else
            {
                refused++;
                Assert.True(
                    error.Code is MapFeedProtocol.BadJson or MapFeedProtocol.UnknownOp or MapFeedProtocol.BadArgs,
                    $"Unexpected error code '{error.Code}' on seed={seed} iteration={iteration}");
                Assert.False(string.IsNullOrWhiteSpace(error.Message));
            }
        }

        Assert.True(accepted >= 150, $"Expected benign/valid lines to be accepted (got {accepted})");
        Assert.True(refused >= 1000, $"Expected corrupt lines to be refused (got {refused})");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task MapFeedLineReader_UnderRandomChunkSplitsAndOverlongLines_MatchesOracle(int seed)
    {
        var rng = new Random(seed);
        const int maxLineBytes = 128;

        for (int iteration = 0; iteration < 250; iteration++)
        {
            int lineCount = rng.Next(1, 12);
            var expectedLines = new List<string>();
            var rawBytes = new List<byte>();
            bool expectTooLong = false;

            for (int i = 0; i < lineCount; i++)
            {
                bool overlong = rng.Next(8) == 0;
                int length = overlong ? rng.Next(maxLineBytes + 1, maxLineBytes * 3) : rng.Next(0, maxLineBytes + 1);
                byte[] lineBytes = new byte[length];
                for (int b = 0; b < length; b++)
                {
                    // Printable ASCII excluding '\r' and '\n'.
                    lineBytes[b] = (byte)rng.Next(32, 127);
                }

                rawBytes.AddRange(lineBytes);
                bool isLast = i == lineCount - 1;
                int ending = isLast ? rng.Next(3) : rng.Next(2); // 0: \n, 1: \r\n, 2: EOF without newline
                if (ending == 0)
                {
                    rawBytes.Add((byte)'\n');
                }
                else if (ending == 1)
                {
                    rawBytes.Add((byte)'\r');
                    rawBytes.Add((byte)'\n');
                }

                if (!expectTooLong)
                {
                    if (length > maxLineBytes)
                    {
                        expectTooLong = true;
                    }
                    else if (length > 0 || ending != 2)
                    {
                        expectedLines.Add(Encoding.ASCII.GetString(lineBytes));
                    }
                }
            }

            var stream = new RandomChunkStream(rawBytes.ToArray(), rng);
            var reader = new MapFeedLineReader(stream, maxLineBytes);
            var actualLines = new List<string>();
            bool actualTooLong = false;

            while (true)
            {
                LineRead read = await reader.ReadLineAsync(CancellationToken.None);
                if (read.Status == LineStatus.Line)
                {
                    actualLines.Add(Encoding.ASCII.GetString(read.Line.Span));
                }
                else if (read.Status == LineStatus.TooLong)
                {
                    actualTooLong = true;
                    break;
                }
                else
                {
                    break;
                }
            }

            Assert.Equal(expectTooLong, actualTooLong);
            Assert.Equal(expectedLines, actualLines);
        }
    }

    [Fact]
    public void MapFeedProtocol_TryParse_UnpairedUtf16SurrogateEscape_RefusedWithoutThrowing()
    {
        ReadOnlySpan<string> lines =
        [
            """{"op":"\uD800"}""",
            """{"op\uD800":"unsubscribe"}""",
            """{"op":"query","id":1,"kind":"\uD800","map":0,"a":[0,0,0]}""",
            """{"op":"subscribe","map":0,"kinds":["\uDFFF"]}""",
        ];

        foreach (string line in lines)
        {
            bool ok = MapFeedProtocol.TryParse(Encoding.UTF8.GetBytes(line), out _, out FeedError error);
            Assert.False(ok);
            Assert.False(string.IsNullOrEmpty(error.Code));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task WorldMapFeed_UnderInterleavedValidAndHostileTraffic_StaysHealthy(int seed)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rng = new Random(seed);
        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true });
        await world.AddPlayerAsync("Fuzzalice");
        FeedClient goodClient = await world.ConnectAsync();

        // 1. A hostile peer sending fuzzed garbage is cut off after MaxStrikes without affecting the world or goodClient.
        await using (FeedClient hostile = await world.ConnectAsync())
        {
            for (int strike = 0; strike < MapFeedProtocol.MaxStrikes; strike++)
            {
                string badLine = strike switch
                {
                    0 => "{not valid json",
                    1 => """{"op":"unknown_fuzz_op"}""",
                    2 => """{"op":"query","id":99,"kind":"height","map":0,"a":[17067,0,0]}""",
                    3 => """{"op":"subscribe","map":-5}""",
                    _ => """{"op":"\uD800"}""",
                };
                await hostile.SendAsync(badLine);
                JsonElement reply = await hostile.ReadAsync();
                if (strike == 2)
                {
                    // A refused query with a readable id answers op=result with ok=false and still counts as a strike.
                    Assert.Equal("result", reply.Op());
                    Assert.False(reply.GetProperty("ok").GetBoolean());
                    Assert.Equal(MapFeedProtocol.BadArgs, reply.GetProperty("code").GetString());
                }
                else
                {
                    Assert.Equal("error", reply.Op());
                }
            }

            JsonElement cutoff = await hostile.ReadAsync();
            Assert.Equal("error", cutoff.Op());
            Assert.Equal(MapFeedProtocol.TooManyErrors, cutoff.GetProperty("code").GetString());
            Assert.Null(await hostile.TryReadAsync());
        }

        // 2. Interleave valid queries, subscriptions, and a few recoverable bad lines (< MaxStrikes) on goodClient.
        await goodClient.SendAsync(FeedClient.Subscribe(hz: 5));
        JsonElement firstFrame = await goodClient.ReadOpAsync("frame");
        Assert.Equal(1L, firstFrame.GetProperty("seq").GetInt64());

        for (int i = 0; i < 20; i++)
        {
            float ax = FeedWorld.StartX + ((float)(rng.NextDouble() - 0.5) * 40f);
            float ay = FeedWorld.StartY + ((float)(rng.NextDouble() - 0.5) * 40f);
            float az = FeedWorld.StartZ + ((float)(rng.NextDouble() - 0.5) * 10f);
            float bx = ax + ((float)(rng.NextDouble() - 0.5) * 20f);
            float by = ay + ((float)(rng.NextDouble() - 0.5) * 20f);
            float bz = az + ((float)(rng.NextDouble() - 0.5) * 5f);
            string kind = (i % 4) switch
            {
                0 => "height",
                1 => "undermap",
                2 => "los",
                _ => "path",
            };

            long queryId = 1000 + i;
            string queryJson = string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"op":"query","id":{{queryId}},"kind":"{{kind}}","map":0,"a":[{{ax:R}},{{ay:R}},{{az:R}}],"b":[{{bx:R}},{{by:R}},{{bz:R}}]}""");
            await goodClient.SendAsync(queryJson);
            JsonElement answer = await goodClient.ReadOpAsync("result");
            Assert.Equal(queryId, answer.GetProperty("id").GetInt64());
            Assert.Equal(kind, answer.GetProperty("kind").GetString());
        }

        List<JsonElement> frames = await world.StepFramesAsync(goodClient, 4);
        Assert.NotEmpty(frames);
        Assert.Equal(0, world.Feed.CaptureFaults);
        Assert.DoesNotContain(world.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    private static byte[] BuildFuzzedLine(Random rng, int iteration)
    {
        // Every 8th iteration, construct a valid or benignly-extended request.
        if (iteration % 8 == 0)
        {
            string baseLine = ValidLines[(iteration / 8) % ValidLines.Length];
            if (rng.Next(2) == 0)
            {
                return Encoding.UTF8.GetBytes(baseLine);
            }

            // Inject an unknown extra property into a valid JSON object (protocol must ignore unknown properties).
            return Encoding.UTF8.GetBytes(baseLine[..^1] + $",\"extra_{rng.Next(1000)}\":{rng.Next(-500, 500)}}}");
        }

        string template = ValidLines[rng.Next(ValidLines.Length)];
        byte[] bytes = Encoding.UTF8.GetBytes(template);

        switch (rng.Next(7))
        {
            case 0: // Truncate at random point
                return bytes[..rng.Next(0, bytes.Length)];

            case 1: // Flip random bits/bytes (including invalid UTF-8)
            {
                byte[] copy = (byte[])bytes.Clone();
                int count = rng.Next(1, 6);
                for (int i = 0; i < count && copy.Length > 0; i++)
                {
                    copy[rng.Next(copy.Length)] ^= (byte)rng.Next(1, 256);
                }

                return copy;
            }

            case 2: // Splice a corrupt JSON value or extreme number into the template
            {
                string fragment = CorruptFragments[rng.Next(CorruptFragments.Length)];
                int colon = template.IndexOf(':', rng.Next(0, template.Length));
                if (colon < 0)
                {
                    return Encoding.UTF8.GetBytes(fragment);
                }

                string mutated = template[..(colon + 1)] + fragment + "}";
                return Encoding.UTF8.GetBytes(mutated);
            }

            case 3: // Query with random finite/out-of-bounds/extreme coordinates
            {
                double x = (rng.NextDouble() - 0.5) * 60000.0;
                double y = (rng.NextDouble() - 0.5) * 60000.0;
                double z = (rng.NextDouble() - 0.5) * 1000000.0;
                string kind = (rng.Next(5)) switch
                {
                    0 => "los",
                    1 => "height",
                    2 => "path",
                    3 => "undermap",
                    _ => "bogus",
                };
                string json = string.Create(
                    CultureInfo.InvariantCulture,
                    $$"""{"op":"query","id":{{rng.Next(-10, 1000)}},"kind":"{{kind}}","map":{{rng.Next(-2, 1000)}},"a":[{{x:R}},{{y:R}},{{z:R}}],"b":[0,0,0]}""");
                return Encoding.UTF8.GetBytes(json);
            }

            case 4: // Delete a slice from the middle
            {
                int start = rng.Next(0, bytes.Length);
                int len = rng.Next(1, bytes.Length - start + 1);
                byte[] cut = new byte[bytes.Length - len];
                Buffer.BlockCopy(bytes, 0, cut, 0, start);
                Buffer.BlockCopy(bytes, start + len, cut, start, bytes.Length - start - len);
                return cut;
            }

            case 5: // Append trailing tokens after the object
                return Encoding.UTF8.GetBytes(template + " " + CorruptFragments[rng.Next(CorruptFragments.Length)]);

            default: // Pure random bytes
            {
                byte[] noise = new byte[rng.Next(0, 128)];
                rng.NextBytes(noise);
                return noise;
            }
        }
    }

    private sealed class RandomChunkStream(byte[] data, Random rng) : Stream
    {
        private int _pos;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos >= data.Length || buffer.Length == 0)
            {
                return ValueTask.FromResult(0);
            }

            int maxChunk = rng.Next(1, 33);
            int take = Math.Min(Math.Min(maxChunk, buffer.Length), data.Length - _pos);
            data.AsMemory(_pos, take).CopyTo(buffer);
            _pos += take;
            return ValueTask.FromResult(take);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
