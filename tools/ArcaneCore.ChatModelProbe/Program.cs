using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Chat.Inference;

// The in-process chat model probe: the same engine, tokenizer, prompt and screen the world server's InProcess chat provider uses.
// Usage: ArcaneCore.ChatModelProbe <model.gguf> [threads] [context]
if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: ArcaneCore.ChatModelProbe <model.gguf> [threads=2] [context=1024]");
    return 1;
}

CultureInfo c = CultureInfo.InvariantCulture;
string path = Path.GetFullPath(args[0]);
int threads = args.Length > 1 ? int.Parse(args[1], c) : 2;
int context = args.Length > 2 ? int.Parse(args[2], c) : 1024;

var load = Stopwatch.StartNew();
InProcessChatEngine engine;
try
{
    engine = InProcessChatEngine.Load(path, context, threads, slots: 1);
}
catch (Exception error) when (error is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
{
    Console.WriteLine($"The model did not load: {error.GetType().Name}: {error.Message}");
    return 2;
}

using (engine)
{
    ChatModel model = engine.Model;
    ChatModelConfig cfg = model.Config;
    BpeTokenizer tokenizer = model.Tokenizer;
    Console.WriteLine(string.Create(c, $"Loaded {path} in {load.ElapsedMilliseconds} ms"));
    Console.WriteLine($"  model       {model.Description}");
    Console.WriteLine(string.Create(c, $"  shape       {cfg.Layers} layers, dim {cfg.Dim}, {cfg.Heads} heads / {cfg.KvHeads} kv heads x {cfg.HeadDim}, ffn {cfg.FfnDim}, vocab {cfg.Vocabulary}, rope base {cfg.RopeBase} ({(cfg.RopeNeox ? "neox" : "norm")}), ctx {cfg.ContextLength}"));
    Console.WriteLine(string.Create(c, $"  memory      {model.File.FileLength / (1024 * 1024)} MB mapped, {engine.AllocatedBytes / (1024 * 1024)} MB of cache and buffers ({engine.ContextTokens} tokens), {engine.Threads} threads"));
    Console.WriteLine($"  tokenizer   pre={tokenizer.Pre}{(tokenizer.PreExact ? string.Empty : " (unknown, gpt2 split)")} bos={tokenizer.Bos} eos={tokenizer.Eos} im_start={tokenizer.ImStart} im_end={tokenizer.ImEnd} add_bos={tokenizer.AddBos}");
    var types = model.File.Tensors.GroupBy(t => t.Type).Select(g => $"{g.Key} x{g.Count()}");
    Console.WriteLine($"  tensors     {string.Join(", ", types)}");

    const string sample = "Hello there, traveller! Have you seen the Defias in Westfall? I'll pay 1234 gold.\nThanks :)";
    List<int> ids = tokenizer.Encode(sample);
    string back = tokenizer.Decode(ids);
    Console.WriteLine();
    Console.WriteLine(string.Create(c, $"Tokenizer round trip: {ids.Count} tokens, {(back == sample ? "OK" : "MISMATCH")}"));
    Console.WriteLine("  ids   " + string.Join(" ", ids.Take(48)) + (ids.Count > 48 ? " ..." : string.Empty));
    Console.WriteLine("  split " + string.Join("|", ids.Take(24).Select(id => tokenizer.Decode([id]).Replace("\n", "\\n", StringComparison.Ordinal))));
    if (back != sample) Console.WriteLine("  back  " + back);

    var persona = new BotChatPersona("Thorgar", Race.Dwarf, Class.Paladin, Gender.Male, 12, "Dun Morogh", "Kharanos", PlayerbotGoalKind.Quest,
        "The Troll Cave", null, false, ["gained a level"]);
    var ask = new BotChatAsk(Guid.NewGuid(), new ObjectGuid(7), persona, new ObjectGuid(42), "Testplayer", ChatType.Whisper,
        "hey, what are you up to?", FromMaster: false, InviteAllowed: false);
    BotChatPrompt prompt = PlayerbotChatPrompts.Build(ask, []);
    var greedy = new ChatGenerationSettings(MaxNewTokens: 48, BudgetMs: 600_000, Temperature: 0f, TopK: 1, TopP: 1f, RepetitionPenalty: 1.1f, Seed: 1);
    List<int> tokens = InProcessChatPrompt.Build(prompt, tokenizer, engine.ContextTokens - greedy.MaxNewTokens)
        ?? throw new InvalidOperationException("the sample prompt does not fit the context");
    Console.WriteLine();
    var warm = Stopwatch.StartNew();
    int warmed = await engine.WarmPrefixAsync(InProcessChatPrompt.SharedPrefix(tokenizer));
    Console.WriteLine(string.Create(c, $"Shared prefix (system rules): {warmed} tokens prefilled once into the prefix cache in {warm.ElapsedMilliseconds} ms"));
    Console.WriteLine(string.Create(c, $"Sample prompt: {tokens.Count} tokens"));
    // The server's own sampling: the ported chain (penalties, top-k, top-p, min-p, temperature, seeded draw) behind the one-line constraint.
    var sampled = new ChatGenerationSettings(MaxNewTokens: 48, BudgetMs: 600_000, Temperature: 0.7f, TopK: 40, TopP: 0.9f, RepetitionPenalty: 1.15f, Seed: 7,
        MinP: 0.05f);
    (string Label, ChatGenerationSettings Settings)[] runs =
    [
        ("greedy, first reply", greedy),
        ("greedy, same prompt again", greedy),
        ("sampled t=0.7 k=40 p=0.9 min_p=0.05 seed=7", sampled),
    ];
    foreach ((string label, ChatGenerationSettings settings) in runs)
    {
        ChatGenerationResult result = await engine.GenerateAsync(tokens, settings, CancellationToken.None);
        int computed = result.PromptTokens - result.ReusedTokens;
        double promptRate = result.PrefillMs > 0 ? computed / (result.PrefillMs / 1000) : 0;
        double rate = result.DecodeMs > 0 ? result.GeneratedTokens / (result.DecodeMs / 1000) : 0;
        Console.WriteLine(string.Create(c,
            $"Run ({label}): {result.Status}, stop={result.StopReason}, prompt {computed} computed + {result.ReusedTokens} reused in {result.PrefillMs:0} ms ({promptRate:0.0} tok/s), {result.GeneratedTokens} generated in {result.DecodeMs:0} ms ({rate:0.0} tok/s), TTFT {result.TtftMs:0} ms, ITL {result.InterTokenMs:0.0} ms, queue {result.QueueMs:0} ms"));
        Console.WriteLine("  raw      " + result.Text);
        string? said = InProcessChatPrompt.Screen(result.Text, persona.Name, 160, truncated: result.StopReason == "length");
        Console.WriteLine("  screened " + (said ?? "(dropped: the templates would answer)"));
    }

    Console.WriteLine("Scheduler: " + engine.ServingStats.Describe());

    Console.WriteLine();
    Console.WriteLine("A reply must finish inside World:Playerbots:Chat:InProcess:BudgetMs (default 6000) on the live server's CPU, with the live load.");
}

return 0;
