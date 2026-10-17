using System.Globalization;
using System.Text;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>A scenario expectation that did not hold (or a step that could not be performed).</summary>
public sealed class ScenarioAssertionException(string message) : Exception(message);

/// <summary>A bounded wait that ran out.</summary>
public sealed class ScenarioTimeoutException(string message) : Exception(message);

/// <summary>One step of a run: wall time, game time the manual clock advanced inside it, and its error when it failed.</summary>
public sealed record ScenarioStepResult(int Index, string Name, bool Passed, TimeSpan Wall, TimeSpan Game, string? Error);

/// <summary>The outcome of one scenario run: steps with timings, and on failure the last relevant packets of every bot.</summary>
public sealed class ScenarioReport
{
    private readonly List<ScenarioStepResult> _steps = [];
    private readonly Dictionary<string, IReadOnlyList<ScenarioPacket>> _packets = new(StringComparer.Ordinal);

    public ScenarioReport(string name) => Name = name;

    public string Name { get; }

    public bool Passed => Failure is null && _steps.All(step => step.Passed);

    public IReadOnlyList<ScenarioStepResult> Steps => _steps;

    /// <summary>The first failure (assertion, timeout or exception) that ended the run.</summary>
    public string? Failure { get; private set; }

    public string? FailedStep { get; private set; }

    public TimeSpan Wall { get; internal set; }

    public TimeSpan Game { get; internal set; }

    /// <summary>On failure: each bot's last relevant packets (noise filtered), oldest first.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ScenarioPacket>> LastPackets => _packets;

    internal void Add(ScenarioStepResult step) => _steps.Add(step);

    internal void Fail(string? step, string message)
    {
        if (Failure is not null) return;
        FailedStep = step;
        Failure = message;
    }

    internal void AttachPackets(string bot, IReadOnlyList<ScenarioPacket> packets) => _packets[bot] = packets;

    /// <summary>Plain-text lines (the GM command prints these; tests put them in the failure message).</summary>
    public IReadOnlyList<string> ToLines()
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"SCENARIO {Name} {(Passed ? "PASS" : "FAIL")} steps={_steps.Count} wall={Wall.TotalMilliseconds:F0}ms game={Game.TotalMilliseconds:F0}ms"),
        };
        foreach (ScenarioStepResult step in _steps)
        {
            string error = step.Error is null ? string.Empty : " :: " + step.Error;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  [{step.Index:D2}] {(step.Passed ? "ok  " : "FAIL")} {step.Name} wall={step.Wall.TotalMilliseconds:F0}ms game={step.Game.TotalMilliseconds:F0}ms{error}"));
        }

        if (Failure is not null)
        {
            lines.Add($"  failure{(FailedStep is null ? string.Empty : " in '" + FailedStep + "'")}: {Failure}");
            foreach ((string bot, IReadOnlyList<ScenarioPacket> packets) in _packets)
            {
                lines.Add($"  last packets of {bot}:");
                lines.AddRange(packets.Select(packet => "    " + packet));
            }
        }

        return lines;
    }

    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (string line in ToLines()) text.AppendLine(line);
        return text.ToString();
    }
}
