using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ArcaneCore.Gateway;

/// <summary>
/// The gateway daemon's command line, run before anything is started: <c>--help</c>, <c>check-config</c> (also spelled <c>--check-config</c>) and
/// <c>write-config</c>, with the same exit codes as the world and the realm (64 usage, 78 invalid configuration). Nothing binds a port and no
/// database is touched by the verbs. Stateless; called once from the main thread, or from a test with its own writers.
/// </summary>
internal static class GatewayCli
{
    internal const string CheckConfigFlag = "--check-config";

    /// <summary>
    /// Answers the command line. Returns <c>null</c> when the daemon should run: <paramref name="builder"/> is then fully configured
    /// (appsettings, gateway.conf, gateway.json, environment, command line, lowest precedence first). Otherwise returns the process exit
    /// code and <paramref name="builder"/> is null.
    /// </summary>
    internal static int? Prepare(string[] args, TextWriter standardOutput, TextWriter standardError, out HostApplicationBuilder? builder)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        builder = null;
        DaemonCommand command = DaemonCommandLine.Parse(NormalizeVerb(args), ConfigDaemon.Gateway, ConfCatalog.Embedded);
        if (ConfStartup.TryAnswerEarly(command, ConfigDaemon.Gateway, standardOutput, standardError, out int earlyExit))
        {
            return earlyExit;
        }

        string[] hostArgs = [.. command.HostArgs];
        HostApplicationBuilder host = Host.CreateApplicationBuilder(hostArgs);
        ConfAttachment? conf = ConfStartup.TryAttach(host, ConfigDaemon.Gateway, command, standardError, out int confExit);
        if (conf is null)
        {
            return confExit;
        }

        // gateway.json next to the executable keeps its place above the .conf file; the command line stays on top.
        host.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "gateway.json"), optional: true, reloadOnChange: false);
        host.Configuration.AddEnvironmentVariables();
        host.Configuration.AddCommandLine(hostArgs);

        if (command.Kind == DaemonCommandKind.Verb)
        {
            return RunVerb(command, host.Configuration, standardOutput, standardError);
        }

        ConfigReport report = Validate(host.Configuration);
        if (report.Issues.Count > 0)
        {
            report.Write(standardError);
        }

        if (report.IsInvalid)
        {
            return ExitCodes.InvalidConfiguration;
        }

        builder = host;
        return null;
    }

    /// <summary>Every problem of the gateway's configuration: the .conf key check, then <see cref="GatewayConfigCheck"/>, then <see cref="CrashDumpConfigChecks"/> (the gateway binds <c>Diagnostics:CrashDump</c> like every daemon).</summary>
    internal static ConfigReport Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ConfigReport.Run(configuration, [new ConfKeyCheck(ConfCatalog.Embedded, ConfigDaemon.Gateway), new GatewayConfigCheck(), new CrashDumpConfigChecks()]);
    }

    /// <summary>Turns the <c>--check-config</c> flag into the <c>check-config</c> verb the shared parser knows (the verb must be the first argument).</summary>
    internal static string[] NormalizeVerb(string[] args)
        => args.Any(a => string.Equals(a, CheckConfigFlag, StringComparison.OrdinalIgnoreCase))
            ? [DaemonCommandLine.CheckConfig, .. args.Where(a => !string.Equals(a, CheckConfigFlag, StringComparison.OrdinalIgnoreCase))]
            : args;

    private static int RunVerb(DaemonCommand command, IConfigurationRoot configuration, TextWriter standardOutput, TextWriter standardError)
    {
        if (command.Verb == DaemonCommandLine.WriteConfig)
        {
            return WriteConfigVerb.Run(command, ConfigDaemon.Gateway, ConfCatalog.Embedded, configuration, standardOutput, standardError);
        }

        ConfigReport report = Validate(configuration);
        report.Write(standardOutput);
        if (report.IsInvalid)
        {
            standardError.WriteLine("The gateway configuration is invalid.");
            return ExitCodes.InvalidConfiguration;
        }

        return ExitCodes.Success;
    }
}

/// <summary>
/// Binds <c>Gateway</c> and reports every <see cref="GatewayOptions.Validate"/> problem, plus the link secret rule that
/// <see cref="GatewayServer.StartAsync"/> enforces (<see cref="GatewaySessionLinkHost.ThrowIfWeakSecret"/>), as errors. Pure; no I/O.
/// </summary>
internal sealed class GatewayConfigCheck : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        GatewayOptions options = new();
        string? bindFailure = null;
        try
        {
            options = configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
        }
        catch (InvalidOperationException ex)
        {
            bindFailure = ex.Message;
        }

        if (bindFailure is not null)
        {
            yield return new ConfigIssue(ConfigSeverity.Error, GatewayOptions.SectionName, bindFailure, "use values of the documented type (docs/reference/configuration.md)");
            yield break;
        }

        foreach (string problem in options.Validate())
        {
            int split = problem.IndexOf(": ", StringComparison.Ordinal);
            string key = split > 0 ? problem[..split] : GatewayOptions.SectionName;
            string text = split > 0 ? problem[(split + 2)..] : problem;
            yield return new ConfigIssue(ConfigSeverity.Error, key, text, "correct " + ConfCatalog.Display(key) + " (docs/reference/configuration.md)");
        }

        string? secretProblem = null;
        try
        {
            GatewaySessionLinkHost.ThrowIfWeakSecret(options.LinkSecret, GatewayOptions.SectionName + ":LinkSecret");
        }
        catch (ArgumentException ex)
        {
            secretProblem = ex.Message;
        }

        if (secretProblem is not null)
        {
            int cut = secretProblem.IndexOf(" (Parameter", StringComparison.Ordinal);
            yield return new ConfigIssue(
                ConfigSeverity.Error,
                GatewayOptions.SectionName + ":LinkSecret",
                cut > 0 ? secretProblem[..cut] : secretProblem,
                "set Gateway.LinkSecret to the same value as the worker's Cluster.Worker.LinkSecret (a long random string)");
        }
    }
}
