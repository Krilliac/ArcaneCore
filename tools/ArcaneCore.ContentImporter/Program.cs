using ArcaneCore.Data.Content.Import;

// arcane-content-importer: see ContentImporterCli.Usage (arcane-content-importer --help).
// The command logic lives in the data library so the tests can drive it in-process; this is
// only the process host: Ctrl+C cancels the run and the importers roll back.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await ContentImporterCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
