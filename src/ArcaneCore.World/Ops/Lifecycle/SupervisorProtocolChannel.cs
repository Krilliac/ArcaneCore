using ArcaneCore.Kernel.Ops.ServerConsole;

namespace ArcaneCore.World.Ops.Lifecycle;

/// <summary>
/// This world's line to the cluster host supervisor that launched it: whether there is one, which realm id the supervisor's configuration gave this
/// process, and the one place a protocol line is written (standard output, which the supervisor reads). Without a supervisor
/// (<see cref="IsSupervised"/> false) nothing is ever written.
/// <para>Thread affinity: <see cref="Send"/> may be called from the world thread; it writes one line to a synchronized writer and returns.</para>
/// </summary>
public sealed class SupervisorProtocolChannel
{
    private static readonly Lazy<SupervisorProtocolChannel> FromProcess = new(
        () => FromEnvironment(Environment.GetEnvironmentVariable, Console.Out), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly TextWriter _output;

    /// <param name="output">Where protocol lines go (must be safe to write from several threads).</param>
    /// <param name="token">The supervisor's per-run secret; null or empty means no supervisor.</param>
    /// <param name="ownRealmId">The realm id the supervisor's configuration gave this process; 0 when it gave none.</param>
    public SupervisorProtocolChannel(TextWriter output, string? token, int ownRealmId)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        Token = string.IsNullOrEmpty(token) ? null : token;
        OwnRealmId = ownRealmId;
    }

    /// <summary>The channel of this process, built once from its environment.</summary>
    public static SupervisorProtocolChannel Process => FromProcess.Value;

    /// <summary>Whether a supervisor launched this process with its line protocol on.</summary>
    public bool IsSupervised => Token is not null;

    /// <summary>The supervisor's secret; every request line carries it.</summary>
    public string? Token { get; }

    /// <summary>This process's realm id from the supervisor's configuration, or 0.</summary>
    public int OwnRealmId { get; }

    /// <summary>The channel an environment describes: supervised only when the protocol variable is <c>1</c> and the token is present.</summary>
    public static SupervisorProtocolChannel FromEnvironment(Func<string, string?> environment, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!SupervisorProtocol.IsEnabled(environment))
        {
            return new SupervisorProtocolChannel(output, null, 0);
        }

        _ = int.TryParse(environment(SupervisorProtocol.RealmIdEnvironmentVariable), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int realm);
        return new SupervisorProtocolChannel(output, environment(SupervisorProtocol.TokenEnvironmentVariable), realm);
    }

    /// <summary>Writes one protocol line and flushes it. Not supervised: does nothing.</summary>
    public void Send(string line)
    {
        if (!IsSupervised)
        {
            return;
        }

        try
        {
            _output.WriteLine(line);
            _output.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The supervisor's pipe is gone: it is exiting, and so is this process.
        }
    }
}
