using ArcaneCore.Realm.Protocol;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// Deterministic fuzz of the two logon body readers: seeded mutations (truncation, bit flips,
/// extreme length bytes, insertions, junk tails) of the real challenge and proof shapes the
/// handshake tests send. Neither reader may throw, and a parse that succeeds must describe bytes
/// that exist in the body.
/// </summary>
public sealed class LogonPacketFuzzTests
{
    private const int Iterations = 4_000;

    private static byte[] ChallengeBody() => CodexNetAuthRealmTests.Challenge("TESTER")[4..];

    private static byte[] ProofBody()
    {
        byte[] body = new byte[LogonProofRequest.BodyLength];
        new Random(7).NextBytes(body);
        body[^1] = 0;
        return body;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20251004)]
    public void ChallengeReader_NeverThrows_AndNeverDescribesBytesOutsideTheBody(int seed)
    {
        var random = new Random(seed);
        byte[] shape = ChallengeBody();
        for (int i = 0; i < Iterations; i++)
        {
            byte[] body = Mutate(shape, random);
            bool ok;
            LogonChallengeRequest? request;
            try
            {
                ok = LogonChallengeRequest.TryParse(body, out request);
            }
            catch (Exception ex)
            {
                Assert.Fail($"seed {seed} iteration {i}: {ex.GetType().Name}: {ex.Message} (body {Convert.ToHexString(body)})");
                return;
            }

            if (ok)
            {
                Assert.NotNull(request);
                Assert.True(body.Length >= 30);
                Assert.Equal(body[29], request!.Username.Length);
                Assert.True(30 + request.Username.Length <= body.Length);
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(20251004)]
    public void ProofReader_NeverThrows_AndAcceptsOnlyFullBodies(int seed)
    {
        var random = new Random(seed);
        byte[] shape = ProofBody();
        for (int i = 0; i < Iterations; i++)
        {
            byte[] body = Mutate(shape, random);
            bool ok;
            try
            {
                ok = LogonProofRequest.TryParse(body, out LogonProofRequest? request);
                if (ok)
                {
                    Assert.NotNull(request);
                    Assert.Equal(LogonProofRequest.PublicKeyLength, request!.ClientPublicKey.Length);
                    Assert.Equal(LogonProofRequest.ProofLength, request.ClientProof.Length);
                }
            }
            catch (Exception ex)
            {
                Assert.Fail($"seed {seed} iteration {i}: {ex.GetType().Name}: {ex.Message} (body {Convert.ToHexString(body)})");
                return;
            }

            Assert.Equal(body.Length >= LogonProofRequest.BodyLength, ok);
        }
    }

    /// <summary>One random mutation of a shape (the same menu the world-side fuzz uses).</summary>
    internal static byte[] Mutate(byte[] shape, Random random)
    {
        byte[] data = (byte[])shape.Clone();
        switch (random.Next(8))
        {
            case 0: // truncate
                return data[..random.Next(data.Length + 1)];
            case 1: // flip bits in a few bytes
                for (int n = random.Next(1, 4); n > 0 && data.Length > 0; n--)
                {
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                }

                return data;
            case 2: // an extreme length or count byte
                if (data.Length > 0)
                {
                    data[random.Next(data.Length)] = random.Next(2) == 0 ? (byte)0xFF : (byte)0;
                }

                return data;
            case 3: // append junk
                {
                    byte[] junk = new byte[random.Next(1, 64)];
                    random.NextBytes(junk);
                    return [.. data, .. junk];
                }

            case 4: // delete a run
                if (data.Length > 1)
                {
                    int at = random.Next(data.Length);
                    int count = random.Next(1, Math.Min(8, data.Length - at) + 1);
                    return [.. data[..at], .. data[(at + count)..]];
                }

                return data;
            case 5: // insert a run
                {
                    int at = random.Next(data.Length + 1);
                    byte[] run = new byte[random.Next(1, 8)];
                    random.NextBytes(run);
                    return [.. data[..at], .. run, .. data[at..]];
                }

            case 6: // remove every null terminator
                for (int k = 0; k < data.Length; k++)
                {
                    if (data[k] == 0)
                    {
                        data[k] = 0x41;
                    }
                }

                return data;
            default: // random bytes of a random length around the shape's
                {
                    byte[] noise = new byte[random.Next(0, data.Length * 2 + 1)];
                    random.NextBytes(noise);
                    return noise;
                }
        }
    }
}
