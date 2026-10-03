using System.Reflection.Metadata;

// One line every 200 ms: process id, the value from the method run-spike.ps1 edits, and what the
// runtime and the launcher say about hot reload.
Console.WriteLine("SPIKE-READY");
while (true)
{
    string hooks = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS") ?? string.Empty;
    Console.WriteLine(
        $"TICK pid={Environment.ProcessId} value={Probe.Value()} supported={MetadataUpdater.IsSupported}"
        + $" watch={Environment.GetEnvironmentVariable("DOTNET_WATCH") ?? "-"}"
        + $" modifiable={Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES") ?? "-"}"
        + $" applier={hooks.Contains("DotNetDeltaApplier", StringComparison.OrdinalIgnoreCase)}"
        + $" hooks={string.Join(';', hooks.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileName))}"
        + $" vars={string.Join(';', Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("HOTRELOAD", StringComparison.OrdinalIgnoreCase)).Order())}");
    Thread.Sleep(200);
}
