using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneCore.Kernel.Ops.ServerConsole;

/// <summary>The states of a plain <c>heartbeat</c> line (<see cref="Watchdog.Heartbeat.TextWriterHeartbeatSink"/>).</summary>
public enum SupervisorHeartbeatState
{
    /// <summary>The process finished starting.</summary>
    Ready,

    /// <summary>One liveness beat: the world tick is progressing.</summary>
    Alive,

    /// <summary>The process is stopping.</summary>
    Stopping,
}

/// <summary>What a child says it is stopping for, from the exit code it will return (<see cref="SupervisorProtocol.IntentFor"/>).</summary>
public enum SupervisorStopIntent
{
    /// <summary>Exit code 2: the supervisor relaunches it at once.</summary>
    Restart,

    /// <summary>Exit code 0: the supervisor leaves it down.</summary>
    Shutdown,

    /// <summary>Any other exit code: a failure the supervisor backs off from.</summary>
    Failure,
}

/// <summary>The two lifecycle commands a GM can aim at another world server.</summary>
public enum SupervisorLifecycleAction
{
    Shutdown,
    Restart,
}

/// <summary>What the supervisor did with a cross-world lifecycle request.</summary>
public enum SupervisorLifecycleStatus
{
    /// <summary>Sent to the target child.</summary>
    Forwarded,

    /// <summary>No supervised child has the realm id.</summary>
    UnknownRealm,

    /// <summary>The line was refused (bad token, the target is not running, the target cannot be written to).</summary>
    Rejected,
}

/// <summary>
/// A <c>.server shutdown|restart</c> aimed at another world server. <paramref name="Token"/> is the per-supervisor secret that proves the
/// line came from the process's own code and not from log text; <paramref name="RealmId"/> is the target; <paramref name="By"/> is the issuing
/// account (one token, see <see cref="SupervisorProtocol.Sanitize"/>).
/// </summary>
public readonly record struct SupervisorLifecycleRequest(string Token, int RealmId, SupervisorLifecycleAction Action, uint DelaySeconds, int ExitCode, string By);

/// <summary>The supervisor's answer to a request, sent back to the child that issued it.</summary>
public readonly record struct SupervisorLifecycleResult(int RealmId, SupervisorLifecycleStatus Status, string By);

/// <summary>
/// The child side of the cross-world lifecycle commands: the daemon's lifecycle feature implements it and <see cref="SupervisedChildDrain"/>
/// calls it from the stdin reader thread, so an implementation hands the work to its own owner thread (the world thread) and returns.
/// </summary>
public interface ISupervisedLifecycleRequestHandler
{
    /// <summary>The supervisor forwarded a lifecycle command for this process.</summary>
    void Request(SupervisorLifecycleRequest request);

    /// <summary>The supervisor answered a request this process issued.</summary>
    void Result(SupervisorLifecycleResult result);
}

/// <summary>
/// The line protocol between the cluster host supervisor and its children (docs/CLUSTERING_DESIGN.md, M15.4), opt-in by
/// <see cref="EnvironmentVariable"/>=1 in the child's environment. Child to supervisor, on stdout: <c>heartbeat &lt;UTC&gt; pid=&lt;pid&gt;
/// ready|alive|stopping</c>, <c>lifecycle stopping intent=&lt;restart|shutdown|failure&gt; code=&lt;n&gt;</c> and <c>lifecycle request token=&lt;t&gt;
/// realm=&lt;id&gt; action=&lt;restart|shutdown&gt; delay=&lt;s&gt; code=&lt;c&gt; by=&lt;account&gt;</c>. Supervisor to child, on stdin: <c>drain</c>,
/// <c>lifecycle &lt;restart|shutdown&gt; delay=&lt;s&gt; code=&lt;c&gt; by=&lt;account&gt;</c> and <c>lifecycle result realm=&lt;id&gt;
/// status=&lt;forwarded|unknown-realm|rejected&gt; by=&lt;account&gt;</c>. Every parser is strict: one space between fields, no extra fields.
/// <para>Pure and thread-safe; nothing here touches a stream.</para>
/// </summary>
public static partial class SupervisorProtocol
{
    /// <summary>Set to <c>1</c> by the supervisor: the child beats on stdout, announces its stop and may issue lifecycle requests.</summary>
    public const string EnvironmentVariable = "ARCANECORE_SUPERVISOR_PROTOCOL";

    /// <summary>The per-supervisor secret every <c>lifecycle request</c> line must carry.</summary>
    public const string TokenEnvironmentVariable = "ARCANECORE_SUPERVISOR_TOKEN";

    /// <summary>The realm id the supervisor's configuration gave this child (absent when none): what <c>.server restart 0 2 &lt;realm&gt;</c> compares against.</summary>
    public const string RealmIdEnvironmentVariable = "ARCANECORE_SUPERVISED_REALM_ID";

    /// <summary>The longest issuer name kept on a line.</summary>
    public const int MaxNameLength = 32;

    private const string Lifecycle = "lifecycle";

    [GeneratedRegex(@"^heartbeat \S+ pid=\d+ (ready|alive|stopping)\z", RegexOptions.CultureInvariant)]
    private static partial Regex HeartbeatPattern();

    [GeneratedRegex(@"^heartbeat \S+ pid=\d+ dumping max=(\d{1,9}) token=(\S+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DumpingPattern();

    /// <summary>Whether <paramref name="environment"/> turns the protocol on (<see cref="EnvironmentVariable"/> is exactly <c>1</c>).</summary>
    public static bool IsEnabled(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return string.Equals(environment(EnvironmentVariable), "1", StringComparison.Ordinal);
    }

    /// <summary>Whether this process was started with the protocol on.</summary>
    public static bool IsEnabled() => IsEnabled(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Parses <c>heartbeat &lt;UTC&gt; pid=&lt;pid&gt; ready|alive|stopping</c> (no leading or trailing text; any control character rejects the line).
    /// <para>
    /// Known limit: a heartbeat carries no per-run token, unlike the <c>lifecycle</c> lines. Child output also holds logged text a player or GM can
    /// influence, so a logged text that ends up on a line of its own and matches this shape (a bare LF in the text starts a new line) is accepted. The
    /// effect is bounded: it can only make a hung child look alive and so delay hang detection. It cannot drain, stop or restart anything, because
    /// those paths need the token. The control-character check keeps a forged line from carrying a second one (CR, LF, escapes) inside it.
    /// </para>
    /// </summary>
    public static bool TryParseHeartbeat(string line, out SupervisorHeartbeatState state)
    {
        state = default;
        if (line is null || HasControl(line) || !HeartbeatPattern().IsMatch(line))
        {
            return false;
        }

        state = line.EndsWith(" ready", StringComparison.Ordinal) ? SupervisorHeartbeatState.Ready
            : line.EndsWith(" alive", StringComparison.Ordinal) ? SupervisorHeartbeatState.Alive
            : SupervisorHeartbeatState.Stopping;
        return true;
    }

    /// <summary>
    /// The state text of the heartbeat line a process writes just before its in-process crash dump: <c>dumping max=&lt;seconds&gt; token=&lt;token&gt;</c>
    /// (<paramref name="token"/> is the supervisor's per-run secret, the same one the <c>lifecycle stopping</c> line carries; without one, no token is written and
    /// no supervisor accepts the line). <paramref name="maxSeconds"/> is the longest the dump can take.
    /// </summary>
    public static string FormatDumpingState(int maxSeconds, string? token)
    {
        string state = string.Create(CultureInfo.InvariantCulture, $"dumping max={Math.Max(1, maxSeconds)}");
        return string.IsNullOrEmpty(token) ? state : state + " token=" + Sanitize(token);
    }

    /// <summary>
    /// Parses a <c>heartbeat &lt;UTC&gt; pid=&lt;pid&gt; dumping max=&lt;seconds&gt; token=&lt;token&gt;</c> line; the caller must still compare <paramref name="token"/>
    /// with the supervisor's. A dumping announcement that is not authenticated like the stopping line could be forged from logged text to keep a hung child alive
    /// for the whole dump grace. A line holding any control character is rejected.
    /// </summary>
    public static bool TryParseDumping(string line, out int maxSeconds, out string token)
    {
        maxSeconds = 0;
        token = string.Empty;
        if (line is null || HasControl(line) || DumpingPattern().Match(line) is not { Success: true } match
            || !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out maxSeconds))
        {
            return false;
        }

        token = match.Groups[2].Value;
        return true;
    }

    /// <summary>The intent an exit code stands for: 0 shutdown, 2 restart (ExitCodes.Restart), anything else a failure.</summary>
    public static SupervisorStopIntent IntentFor(int exitCode) => exitCode switch
    {
        ExitCodes.Success => SupervisorStopIntent.Shutdown,
        ExitCodes.Restart => SupervisorStopIntent.Restart,
        _ => SupervisorStopIntent.Failure,
    };

    /// <summary>
    /// The line a child writes when its host starts stopping. It carries the supervisor's per-run token, like a lifecycle request: child output also
    /// holds logged text a player or GM influences, so an unauthenticated stopping line could be forged to get a world killed or left down.
    /// </summary>
    public static string FormatStopping(string token, SupervisorStopIntent intent, int code)
        => string.Create(CultureInfo.InvariantCulture, $"{Lifecycle} stopping token={Sanitize(token)} intent={IntentText(intent)} code={code}");

    /// <summary>Parses a stopping line; the caller must still compare <paramref name="token"/> with the supervisor's. A line holding any control character (a lone CR included) is rejected.</summary>
    public static bool TryParseStopping(string line, out string token, out SupervisorStopIntent intent, out int code)
    {
        token = string.Empty;
        intent = default;
        code = 0;
        string[]? parts = Split(line, 5);
        if (parts is null || parts[0] != Lifecycle || parts[1] != "stopping"
            || Value(parts[2], "token") is not { Length: > 0 } tokenText
            || Value(parts[3], "intent") is not { } intentText || !TryParseIntent(intentText, out intent)
            || Value(parts[4], "code") is not { } codeText || !int.TryParse(codeText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out code))
        {
            return false;
        }

        token = tokenText;
        return true;
    }

    /// <summary>The line a child writes to ask the supervisor to forward a lifecycle command to another child.</summary>
    public static string FormatLifecycleRequest(SupervisorLifecycleRequest request)
        => string.Create(CultureInfo.InvariantCulture,
            $"{Lifecycle} request token={Sanitize(request.Token)} realm={request.RealmId} action={ActionText(request.Action)} delay={request.DelaySeconds} code={request.ExitCode} by={Sanitize(request.By)}");

    public static bool TryParseLifecycleRequest(string line, out SupervisorLifecycleRequest request)
    {
        request = default;
        string[]? parts = Split(line, 8);
        if (parts is null || parts[0] != Lifecycle || parts[1] != "request"
            || Value(parts[2], "token") is not { Length: > 0 } token
            || Value(parts[3], "realm") is not { } realmText || !TryParsePositive(realmText, out int realm)
            || Value(parts[4], "action") is not { } actionText || !TryParseAction(actionText, out SupervisorLifecycleAction action)
            || Value(parts[5], "delay") is not { } delayText || !uint.TryParse(delayText, NumberStyles.None, CultureInfo.InvariantCulture, out uint delay)
            || Value(parts[6], "code") is not { } codeText || !TryParseCode(codeText, out int code)
            || Value(parts[7], "by") is not { Length: > 0 } by)
        {
            return false;
        }

        request = new SupervisorLifecycleRequest(token, realm, action, delay, code, by);
        return true;
    }

    /// <summary>The line the supervisor writes to the target child (no realm and no token: it is the target's own stdin).</summary>
    public static string FormatForwardedLifecycle(SupervisorLifecycleRequest request)
        => string.Create(CultureInfo.InvariantCulture, $"{Lifecycle} {ActionText(request.Action)} delay={request.DelaySeconds} code={request.ExitCode} by={Sanitize(request.By)}");

    public static bool TryParseForwardedLifecycle(string line, out SupervisorLifecycleRequest request)
    {
        request = default;
        string[]? parts = Split(line, 5);
        if (parts is null || parts[0] != Lifecycle || !TryParseAction(parts[1], out SupervisorLifecycleAction action)
            || Value(parts[2], "delay") is not { } delayText || !uint.TryParse(delayText, NumberStyles.None, CultureInfo.InvariantCulture, out uint delay)
            || Value(parts[3], "code") is not { } codeText || !TryParseCode(codeText, out int code)
            || Value(parts[4], "by") is not { Length: > 0 } by)
        {
            return false;
        }

        request = new SupervisorLifecycleRequest(string.Empty, 0, action, delay, code, by);
        return true;
    }

    /// <summary>The line the supervisor writes back to the issuing child.</summary>
    public static string FormatLifecycleResult(SupervisorLifecycleResult result)
        => string.Create(CultureInfo.InvariantCulture, $"{Lifecycle} result realm={result.RealmId} status={StatusText(result.Status)} by={Sanitize(result.By)}");

    public static bool TryParseLifecycleResult(string line, out SupervisorLifecycleResult result)
    {
        result = default;
        string[]? parts = Split(line, 5);
        if (parts is null || parts[0] != Lifecycle || parts[1] != "result"
            || Value(parts[2], "realm") is not { } realmText || !int.TryParse(realmText, NumberStyles.None, CultureInfo.InvariantCulture, out int realm)
            || Value(parts[3], "status") is not { } statusText || !TryParseStatus(statusText, out SupervisorLifecycleStatus status)
            || Value(parts[4], "by") is not { Length: > 0 } by)
        {
            return false;
        }

        result = new SupervisorLifecycleResult(realm, status, by);
        return true;
    }

    /// <summary>
    /// One protocol token from free text: whitespace and control characters become <c>_</c> (a name can never end a field or a line),
    /// the length is capped at <see cref="MaxNameLength"/>, and an empty value reads <c>unknown</c>.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        int length = Math.Min(value.Length, MaxNameLength);
        var builder = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            char c = value[i];
            builder.Append(char.IsWhiteSpace(c) || char.IsControl(c) ? '_' : c);
        }

        return builder.ToString();
    }

    private static string[]? Split(string line, int fields)
    {
        if (string.IsNullOrEmpty(line) || HasControl(line))
        {
            return null;
        }

        string[] parts = line.Split(' ');
        return parts.Length == fields && Array.TrueForAll(parts, static p => p.Length > 0) ? parts : null;
    }

    private static bool HasControl(string line)
    {
        foreach (char c in line)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Value(string field, string key)
        => field.Length > key.Length + 1 && field.StartsWith(key, StringComparison.Ordinal) && field[key.Length] == '=' ? field[(key.Length + 1)..] : null;

    private static bool TryParsePositive(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static bool TryParseCode(string text, out int code)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out code) && code <= ExitCodes.MaxRequested;

    private static string IntentText(SupervisorStopIntent intent) => intent switch
    {
        SupervisorStopIntent.Restart => "restart",
        SupervisorStopIntent.Shutdown => "shutdown",
        _ => "failure",
    };

    private static bool TryParseIntent(string text, out SupervisorStopIntent intent)
    {
        intent = text switch
        {
            "restart" => SupervisorStopIntent.Restart,
            "shutdown" => SupervisorStopIntent.Shutdown,
            _ => SupervisorStopIntent.Failure,
        };
        return text is "restart" or "shutdown" or "failure";
    }

    private static string ActionText(SupervisorLifecycleAction action) => action == SupervisorLifecycleAction.Restart ? "restart" : "shutdown";

    private static bool TryParseAction(string text, out SupervisorLifecycleAction action)
    {
        action = text == "restart" ? SupervisorLifecycleAction.Restart : SupervisorLifecycleAction.Shutdown;
        return text is "restart" or "shutdown";
    }

    private static string StatusText(SupervisorLifecycleStatus status) => status switch
    {
        SupervisorLifecycleStatus.Forwarded => "forwarded",
        SupervisorLifecycleStatus.UnknownRealm => "unknown-realm",
        _ => "rejected",
    };

    private static bool TryParseStatus(string text, out SupervisorLifecycleStatus status)
    {
        status = text switch
        {
            "forwarded" => SupervisorLifecycleStatus.Forwarded,
            "unknown-realm" => SupervisorLifecycleStatus.UnknownRealm,
            _ => SupervisorLifecycleStatus.Rejected,
        };
        return text is "forwarded" or "unknown-realm" or "rejected";
    }
}
