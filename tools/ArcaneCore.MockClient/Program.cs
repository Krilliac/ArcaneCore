using System.Text.Json;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.MockClient.Playbots;

namespace ArcaneCore.MockClient;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "help" or "--help" or "-h")
        {
            PrintUsage(Console.Out);
            Console.WriteLine("Runs the build 5875 protocol scenario against disposable owned loopback servers.");
            return 0;
        }

        if (args.Length > 0 && args[0] == "live")
        {
            return await LiveSession.MainAsync(args[1..], Console.Out, Console.Error).ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "playbot")
        {
            return await PlaybotCommand.MainAsync(args[1..], Console.Out, Console.Error).ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "item-feedback")
        {
            return await ItemFeedbackProbe.MainAsync(args[1..], Console.Out, Console.Error).ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "starting-zone")
        {
            return await StartingZoneProbe.MainAsync(args[1..], Console.Out, Console.Error).ConfigureAwait(false);
        }

        ClientFixtureOptions? fixture = null;
        if (args.Length == 7 && args[0] == "client-fixture" && args[1] == "--directory"
            && args[3] == "--account" && args[5] == "--password")
        {
            fixture = new ClientFixtureOptions(args[2], args[4], args[6]);
        }
        else if (args.Length != 1 || args[0] != "self-test")
        {
            PrintUsage(Console.Error);
            return 2;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            object report = fixture is not null
                ? await ClientFixture.PrepareAsync(fixture, deadline.Token).ConfigureAwait(false)
                : await MockScenarios.RunAsync(deadline.Token).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                outcome = "failed",
                error = ex.GetType().Name,
                message = ex.Message,
            }));
            return 1;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: arcane-mock self-test");
        writer.WriteLine("       " + PlaybotCommand.Usage);
        writer.WriteLine("       arcane-mock item-feedback --account NAME --character NAME --password-env VAR [--realm 127.0.0.1:3724]");
        writer.WriteLine("       arcane-mock live ...   (stay connected to a running dev server; run 'arcane-mock live' with no flags for details)");
        writer.WriteLine("       arcane-mock starting-zone ...   (bounded Human Warrior starting-zone probe; password comes from an environment variable)");
        writer.WriteLine("       arcane-mock client-fixture --directory <new absolute directory> --account <name> --password <disposable password>");
    }
}
