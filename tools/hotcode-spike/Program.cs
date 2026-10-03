using System.Reflection.Metadata;

// One line every 200 ms: process id, the value from the method run-spike.ps1 edits, what the
// runtime and the launcher say about hot reload, how many IMarker classes the assembly has right
// now (a type added by an edit shows up here), and what the MetadataUpdateHandler saw.
Console.WriteLine("SPIKE-READY");
while (true)
{
    int markers = typeof(IMarker).Assembly.GetTypes().Count(t => t.IsClass && typeof(IMarker).IsAssignableFrom(t));
    Console.WriteLine(
        $"TICK pid={Environment.ProcessId} value={Probe.Value()} supported={MetadataUpdater.IsSupported}"
        + $" watch={Environment.GetEnvironmentVariable("DOTNET_WATCH") ?? "-"}"
        + $" modifiable={Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES") ?? "-"}"
        + $" hooks={(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS")) ? "empty" : "set")}"
        + $" markers={markers} clears={UpdateProbe.Clears} updates={UpdateProbe.Updates}"
        + $" updateTypes={UpdateProbe.LastTypeCount} updateThread={UpdateProbe.LastThread}");
    Thread.Sleep(200);
}
