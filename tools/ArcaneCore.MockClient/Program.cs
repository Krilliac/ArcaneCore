using System.Text.Json;
using ArcaneCore.MockClient.Scenarios;

namespace ArcaneCore.MockClient;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("Usage: arcane-mock self-test");
            Console.WriteLine("Runs the build 5875 protocol scenario against disposable owned loopback servers.");
            return 0;
        }

        if (args.Length != 1 || args[0] != "self-test")
        {
            Console.Error.WriteLine("Usage: arcane-mock self-test");
            return 2;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            MockScenarioReport report = await MockScenarios.RunAsync(deadline.Token).ConfigureAwait(false);
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
}
