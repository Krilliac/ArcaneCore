using System.Globalization;
using System.Text.Json;
using ArcaneCore.MockClient.Scenarios;

namespace ArcaneCore.MockClient.Playbots;

internal static class PlaybotCommand
{
    internal const string Usage = "arcane-mock playbot --account NAME --character NAME --password-env VAR"
        + " [--realm 127.0.0.1:3724] [--duration-s 120] [--steps 120] [--attack-entry 6]"
        + " [--movement true] [--llm false] [--model qwen3.5:4b] [--model-endpoint http://127.0.0.1:11435/]"
        + " [--report NEW_FILE.json]";

    internal sealed record Options(PlaybotRunOptions Run, bool Llm, string Model, Uri Endpoint, string? Report);

    internal static Options Parse(string[] args)
    {
        List<string> login = [];
        int seconds = 120, steps = 120;
        uint entry = 0;
        bool movement = true, llm = false;
        string model = "qwen3.5:4b", endpoint = "http://127.0.0.1:11435/";
        string? report = null;
        for (int i = 0; i < args.Length; i++)
        {
            string flag = args[i];
            if (++i >= args.Length) throw new ArgumentException($"{flag} needs a value.");
            string value = args[i];
            switch (flag)
            {
                case "--account": case "--character": case "--password-env": case "--realm":
                    login.Add(flag); login.Add(value); break;
                case "--duration-s": seconds = Number(value, 1, 600); break;
                case "--steps": steps = Number(value, 1, 500); break;
                case "--attack-entry":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out entry))
                        throw new ArgumentException("--attack-entry must be an unsigned entry ID (0 disables combat).");
                    break;
                case "--movement": movement = Boolean(value); break;
                case "--llm": llm = Boolean(value); break;
                case "--model": model = value; break;
                case "--model-endpoint": endpoint = value; break;
                case "--report": report = Path.GetFullPath(value); break;
                default: throw new ArgumentException("Unknown playbot option.");
            }
        }
        if (!login.Contains("--character")) throw new ArgumentException("An explicit disposable --character is required.");
        LiveSession.Options auth = LiveSession.Parse(login.ToArray());
        var run = new PlaybotRunOptions(auth.Realm, auth.Account, auth.Password, auth.Character, seconds, steps, entry, movement);
        AutonomousPlaybot.Validate(run);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)) throw new ArgumentException("Invalid model endpoint.");
        OllamaPlaybotSelector.ValidateEndpoint(uri);
        if (report is not null && File.Exists(report)) throw new ArgumentException("Report must be a new file.");
        return new(run, llm, model, uri, report);
    }

    internal static async Task<int> MainAsync(string[] args, TextWriter output, TextWriter error)
    {
        Options options;
        try { options = Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            error.WriteLine(ex.Message);
            error.WriteLine(Usage);
            return 2;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            using OllamaPlaybotSelector? local = options.Llm ? new(options.Endpoint, options.Model) : null;
            bool warm = local is not null && await local.WarmAsync(cancellation.Token).ConfigureAwait(false);
            IPlaybotSelector selector = warm
                ? local! : new DeterministicPlaybotSelector();
            PlaybotRunReport report = (await AutonomousPlaybot.RunAsync(options.Run, selector, cancellation.Token).ConfigureAwait(false))
                with { ModelRequested = options.Llm, ModelWarm = warm };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (options.Report is not null)
            {
                await using var file = new FileStream(options.Report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await using var writer = new StreamWriter(file);
                await writer.WriteAsync(json).ConfigureAwait(false);
            }
            output.WriteLine(json);
            return report.Outcome == "failed" ? 1 : 0;
        }
        catch (OperationCanceledException) { return 1; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            // Exception types suffice here; transport/model bodies and credential values are never printed.
            error.WriteLine($"Playbot command failed: {ex.GetType().Name}");
            return 1;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static int Number(string value, int minimum, int maximum)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= minimum && number <= maximum
            ? number : throw new ArgumentException($"Run budget must be {minimum}..{maximum}.");
    private static bool Boolean(string value)
        => bool.TryParse(value, out bool result) ? result : throw new ArgumentException("Boolean option must be true or false.");
}
