using ArcaneCore.Data;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Microsoft.Extensions.Configuration;

// arcane-db: see DbUpgradeCli.Usage (arcane-db --help). The command logic lives in the data library so the
// tests can drive it in-process; this is only the process host: configuration from appsettings.json, an
// optional --config <file> and the environment (Database__Characters__ConnectionString keeps the password
// off the command line), and Ctrl+C cancelling the run.
var arguments = new List<string>(args);
string? extraConfig = null;
int configAt = arguments.IndexOf("--config");
if (configAt >= 0)
{
    if (configAt + 1 >= arguments.Count)
    {
        Console.Error.WriteLine("error: option --config needs a file name");
        return DbUpgradeExitCodes.Usage;
    }

    extraConfig = arguments[configAt + 1];
    arguments.RemoveRange(configAt, 2);
}

IConfigurationBuilder configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true);
if (extraConfig is not null)
{
    configuration.AddJsonFile(Path.GetFullPath(extraConfig), optional: false);
}

IConfigurationRoot settings = configuration.AddEnvironmentVariables().Build();
DatabaseOptions database = settings.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
string? dbcDirectory = settings["ClientData:DbcDirectory"]; // the default of 'arcane-db dbc --dbc-dir'

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await DbUpgradeCli.RunAsync([.. arguments], database, Console.Out, Console.Error, dbcDirectory, cancellation.Token).ConfigureAwait(false);
