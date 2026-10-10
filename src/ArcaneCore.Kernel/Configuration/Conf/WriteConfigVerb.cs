using System.Text;
using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>
/// <c>write-config (--defaults | --current) &lt;file | -&gt; [--force]</c>: writes the documented defaults or the effective
/// configuration as a .conf file. Exit codes: 0 written, 64 the file exists and <c>--force</c> was not given, 1 it could not be written.
/// </summary>
public static class WriteConfigVerb
{
    public static int Run(
        DaemonCommand command,
        ConfigDaemon daemon,
        ConfCatalog catalog,
        IConfigurationRoot configuration,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        string text;
        try
        {
            text = command.WriteMode == WriteConfigMode.Defaults
                ? ConfWriter.WriteDefaults(catalog, daemon)
                : ConfWriter.WriteCurrent(catalog, daemon, configuration, command.IncludeSecrets);
        }
        catch (FormatException ex)
        {
            standardError.WriteLine($"write-config: {ex.Message}");
            return ExitCodes.Failure;
        }

        string target = command.OutputPath ?? "-";
        if (target == "-")
        {
            if (command.IncludeSecrets)
            {
                standardError.WriteLine("write-config: --include-secrets refuses standard output; name a file.");
                return ExitCodes.Usage;
            }

            standardOutput.Write(text);
            return ExitCodes.Success;
        }

        string path = Path.GetFullPath(target);
        if (File.Exists(path) && !command.Force)
        {
            standardError.WriteLine($"write-config: {path} already exists; pass --force to overwrite it.");
            return ExitCodes.Usage;
        }

        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            standardError.WriteLine($"write-config: cannot write {path}: {ex.Message}");
            return ExitCodes.Failure;
        }

        standardOutput.WriteLine($"Wrote {path}");
        return ExitCodes.Success;
    }
}
