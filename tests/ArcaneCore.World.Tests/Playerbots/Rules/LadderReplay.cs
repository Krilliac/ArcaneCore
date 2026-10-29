using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.World.Playerbots;

namespace ArcaneCore.World.Tests.Playerbots.Rules;

/// <summary>
/// A scripted population run of the bot goal ladder. Each bot thinks <see cref="Thinks"/> times; at every think the ladder tries its rungs
/// in the order under test, and each rung acts, pulls or falls through by a seeded draw whose odds depend on the bot and the rung. Every
/// attempt consumes one draw from a single stream, so any difference in the order rungs are tried in (or in which rungs are tried at all)
/// changes every later draw: the digest of the whole run is the replay's identity. The drivers themselves are scripted here; the brain's
/// real drivers are covered by the brain tests.
/// </summary>
internal static class LadderReplay
{
    public const int Bots = 240;
    public const int Thinks = 60;
    private const ulong Seed = 0x2026_1029_B07A_D0E5UL;

    internal sealed record Result(string Digest, string Histogram, int FallThroughs);

    internal delegate IReadOnlyList<PlayerbotLadderRung> OrderOf(int bot, int think);

    public static Result Run(OrderOf order)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var ended = new int[Enum.GetValues<PlayerbotLadderRung>().Length];
        int fallThroughs = 0;
        ulong state = Seed;
        for (int bot = 0; bot < Bots; bot++)
        {
            for (int think = 0; think < Thinks; think++)
            {
                var runner = new Scripted(bot, state);
                IReadOnlyList<PlayerbotLadderRung> rungs = order(bot, think);
                LadderStep step = PlayerbotLadder.Dispatch(runner, [.. rungs]);
                state = runner.State;
                hash.AppendData(Encoding.ASCII.GetBytes($"{bot}:{think}:{string.Join(',', runner.Attempts.Select(a => (int)a))}:{step}:{(runner.Acted is { } r ? (int)r : -1)};"));
                if (runner.Acted is { } acted) ended[(int)acted]++;
                else fallThroughs++;
            }
        }

        string histogram = string.Join(',', Enum.GetValues<PlayerbotLadderRung>().Where(r => r != PlayerbotLadderRung.None).Select(r => $"{(int)r}={ended[(int)r]}")) + $",fall={fallThroughs}";
        return new Result(Convert.ToHexString(hash.GetHashAndReset()), histogram, fallThroughs);
    }

    private static ulong Next(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private sealed class Scripted(int bot, ulong state) : IPlayerbotRungRunner
    {
        private ulong _state = state;

        public ulong State => _state;

        public List<PlayerbotLadderRung> Attempts { get; } = [];

        public PlayerbotLadderRung? Acted { get; private set; }

        public LadderStep Run(PlayerbotLadderRung rung)
        {
            Attempts.Add(rung);
            ulong profile = (ulong)bot * 31 + (ulong)rung;
            ulong profileState = profile ^ Seed;
            double rate = 0.003 + (Next(ref profileState) % 100) / 100.0 * 0.04;
            double draw = (Next(ref _state) % 10_000) / 10_000.0;
            if (draw >= rate) return LadderStep.Continue;
            Acted = rung;
            bool canPull = rung is PlayerbotLadderRung.HelpErrand or PlayerbotLadderRung.Policy or PlayerbotLadderRung.Opportunities;
            return canPull && draw < rate * 0.25 ? LadderStep.Pull : LadderStep.Return;
        }
    }
}
