using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// What the local filter found in a line: the first category hit (null: clean), or that a pattern ran out of time
/// (<paramref name="TimedOutOn"/> names the first such pattern, e.g. "Harassment pattern #2" or "personal data (phone)"; never the line).
/// </summary>
internal readonly record struct SafetyVerdict(PlayerbotChatSafetyCategories? Category, bool TimedOut, string? TimedOutOn = null)
{
    public static readonly SafetyVerdict Clean = new(null, false);

    public bool Flagged => Category is not null;
}

/// <summary>
/// The local filter of bot chat (docs/areas/playbots.md, Safety and provider policies): per category, terms matched as whole words and
/// regular expressions, from the shipped data file (<c>PlayerbotChatSafetyTerms.json</c>, an embedded resource) and an operator's file.
/// <para>
/// A line is compared in three forms: <b>plain</b> (lower case, accents off, everything but letters and digits a single space),
/// <b>leet</b> (as plain, but with the common substitutions first: 0→o, 1→i, 3→e, 4→a, 5→s, 7→t, 8→b, @→a, $→s, !→i, and runs of
/// three or more single letters joined, so "k y s" is "kys") and <b>terms</b> (leet with every run of a repeated letter cut to
/// one, so "kiiiill" is "kil"). Terms are normalized the same way as the terms form and matched as whole words or phrases; a term
/// ending in <c>*</c> also matches longer words. Patterns run on the plain and the leet forms. Personal data (emails, phone
/// numbers, street addresses) is matched on the line as typed by fixed patterns in this class.
/// </para>
/// Every pattern is compiled with <see cref="RegexOptions.NonBacktracking"/> (matching time linear in the input: no catastrophic
/// backtracking whatever the pattern or the line) and a timeout; a pattern that construct cannot run (a back-reference, a lookaround)
/// is skipped with a warning. A timeout flags nothing but keeps the line from the model providers.
/// </summary>
internal sealed class PlayerbotChatSafetyFilter
{
    /// <summary>The shipped data file (an embedded resource of this assembly).</summary>
    internal const string ResourceName = "ArcaneCore.World.Playerbots.Chat.PlayerbotChatSafetyTerms.json";

    /// <summary>
    /// A pattern's time limit per match (the default of <see cref="PlayerbotChatSafetyOptions.PatternTimeout"/>). It is wall-clock
    /// time: a pause of the whole process (a garbage collection, a starved CPU) during a match counts against it, so it can trip on a
    /// harmless line, most likely on a pattern's first, slower match after start.
    /// </summary>
    internal static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest text screened (longer text is screened in full by the fixed-cost term pass, and its first part by the patterns).</summary>
    internal const int MaxScreenedLength = 4096;

    /// <summary>The order categories are reported in when a line hits more than one.</summary>
    internal static readonly PlayerbotChatSafetyCategories[] Order =
    [
        PlayerbotChatSafetyCategories.SexualMinors, PlayerbotChatSafetyCategories.Threats, PlayerbotChatSafetyCategories.SelfHarm,
        PlayerbotChatSafetyCategories.Hate, PlayerbotChatSafetyCategories.Harassment, PlayerbotChatSafetyCategories.IllegalGoods,
        PlayerbotChatSafetyCategories.PersonalData,
    ];

    private readonly Dictionary<PlayerbotChatSafetyCategories, CategoryRules> _rules;

    private readonly Regex _email;
    private readonly Regex _phone;
    private readonly Regex _streetAddress;

    private PlayerbotChatSafetyFilter(Dictionary<PlayerbotChatSafetyCategories, CategoryRules> rules, IReadOnlyList<string> problems, TimeSpan matchTimeout)
    {
        _rules = rules;
        Problems = problems;
        MatchTimeout = matchTimeout;
        _email = new Regex(EmailPattern, Linear, matchTimeout);
        _phone = new Regex(PhonePattern, Linear, matchTimeout);
        _streetAddress = new Regex(StreetAddressPattern, Linear, matchTimeout);
    }

    /// <summary>What could not be loaded (unknown categories, patterns that cannot run, an unreadable file): logged by the caller.</summary>
    internal IReadOnlyList<string> Problems { get; }

    /// <summary>The time limit of every pattern of this filter, the personal-data ones included.</summary>
    internal TimeSpan MatchTimeout { get; }

    /// <summary>Every compiled pattern, the personal-data ones included (the ReDoS test).</summary>
    internal IEnumerable<Regex> AllPatterns => _rules.Values.SelectMany(rule => rule.Patterns).Concat([_email, _phone, _streetAddress]);

    /// <summary>The terms and patterns loaded (status and logs).</summary>
    internal (int Terms, int Patterns) Size => (_rules.Values.Sum(r => r.Terms.Count + r.Prefixes.Count), _rules.Values.Sum(r => r.Patterns.Count));

    /// <summary>The filter of the shipped list alone.</summary>
    internal static PlayerbotChatSafetyFilter Default => DefaultFilter.Value;

    private static readonly Lazy<PlayerbotChatSafetyFilter> DefaultFilter = new(() => Load(null, replaceDefaults: false));

    /// <summary>
    /// The shipped list plus <paramref name="operatorFile"/>'s (or that file's alone with <paramref name="replaceDefaults"/>). A file
    /// that cannot be read is a problem, and the shipped list stays. Every pattern gets <paramref name="matchTimeout"/> (default
    /// <see cref="PatternTimeout"/>).
    /// </summary>
    internal static PlayerbotChatSafetyFilter Load(string? operatorFile, bool replaceDefaults, TimeSpan? matchTimeout = null)
    {
        TimeSpan timeout = matchTimeout ?? PatternTimeout;
        var problems = new List<string>();
        var rules = new Dictionary<PlayerbotChatSafetyCategories, CategoryRules>();
        string? operatorJson = null;
        if (!string.IsNullOrWhiteSpace(operatorFile))
        {
            try
            {
                var info = new FileInfo(operatorFile);
                if (!info.Exists) problems.Add($"terms file {operatorFile} not found; the shipped list is used");
                else if (info.Length > 1024 * 1024) problems.Add($"terms file {operatorFile} is larger than 1 MiB; the shipped list is used");
                else operatorJson = File.ReadAllText(operatorFile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                problems.Add($"terms file {operatorFile} could not be read ({error.GetType().Name}); the shipped list is used");
            }
        }

        if (operatorJson is null || !replaceDefaults) Add(rules, ReadResource(), "shipped list", problems, timeout);
        if (operatorJson is not null) Add(rules, operatorJson, "terms file " + operatorFile, problems, timeout);
        return new PlayerbotChatSafetyFilter(rules, problems, timeout);
    }

    private static string ReadResource()
    {
        using Stream stream = typeof(PlayerbotChatSafetyFilter).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Read one data file (<c>{"categories": {"Hate": {"terms": [...], "patterns": [...]}, ...}}</c>) into <paramref name="rules"/>.</summary>
    private static void Add(Dictionary<PlayerbotChatSafetyCategories, CategoryRules> rules, string json, string source, List<string> problems,
        TimeSpan matchTimeout)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("categories", out JsonElement categories) || categories.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{source}: no \"categories\" object");
                return;
            }

            foreach (JsonProperty category in categories.EnumerateObject())
            {
                if (!Enum.TryParse(category.Name, ignoreCase: false, out PlayerbotChatSafetyCategories which) || which is PlayerbotChatSafetyCategories.None
                    or PlayerbotChatSafetyCategories.All or PlayerbotChatSafetyCategories.PersonalData || !Enum.IsDefined(which))
                {
                    problems.Add($"{source}: unknown category {category.Name} (PersonalData is built in)");
                    continue;
                }

                if (!rules.TryGetValue(which, out CategoryRules? rule)) rules[which] = rule = new CategoryRules();
                foreach (string term in Strings(category.Value, "terms"))
                {
                    string normalized = Normalize(term.TrimEnd('*')).Terms.Trim();
                    if (normalized.Length == 0) continue;
                    (term.EndsWith('*') ? rule.Prefixes : rule.Terms).Add(normalized);
                }

                foreach (string pattern in Strings(category.Value, "patterns"))
                {
                    try
                    {
                        rule.Patterns.Add(new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeout));
                    }
                    catch (Exception error) when (error is ArgumentException or NotSupportedException)
                    {
                        problems.Add($"{source}: {category.Name} pattern skipped ({error.GetType().Name}: back-references and lookarounds are not supported)");
                    }
                }
            }
        }
        catch (JsonException error)
        {
            problems.Add($"{source}: not valid JSON (line {error.LineNumber + 1})");
        }
    }

    private static IEnumerable<string> Strings(JsonElement category, string name)
    {
        if (category.ValueKind != JsonValueKind.Object || !category.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 512 } text) yield return text;
    }

    /// <summary>Screen <paramref name="text"/> for the <paramref name="categories"/>: the first hit in <see cref="Order"/>.</summary>
    internal SafetyVerdict Screen(string? text, PlayerbotChatSafetyCategories categories)
    {
        if (string.IsNullOrWhiteSpace(text) || categories == PlayerbotChatSafetyCategories.None) return SafetyVerdict.Clean;
        string line = text.Length > MaxScreenedLength ? text[..MaxScreenedLength] : text;
        SafetyForms forms = Normalize(line);
        string? timedOutOn = null;
        foreach (PlayerbotChatSafetyCategories category in Order)
        {
            if ((categories & category) == 0) continue;
            if (category == PlayerbotChatSafetyCategories.PersonalData)
            {
                if (HasPersonalData(line, out string? personal)) return new SafetyVerdict(category, personal is not null, personal ?? timedOutOn);
                continue;
            }

            if (!_rules.TryGetValue(category, out CategoryRules? rule)) continue;
            if (rule.MatchesTerm(forms)) return new SafetyVerdict(category, timedOutOn is not null, timedOutOn);
            for (int index = 0; index < rule.Patterns.Count; index++)
            {
                Regex pattern = rule.Patterns[index];
                try
                {
                    if (IsMatch(pattern, forms.Plain) || IsMatch(pattern, forms.Leet)) return new SafetyVerdict(category, timedOutOn is not null, timedOutOn);
                }
                catch (RegexMatchTimeoutException)
                {
                    timedOutOn ??= $"{category} pattern #{index + 1}";
                }
            }
        }

        return new SafetyVerdict(null, timedOutOn is not null, timedOutOn);
    }

    /// <summary>Test seam: a pattern for which this returns true times out on every match (a real timeout cannot be forced).</summary>
    internal Func<Regex, bool>? TimeoutForTest { get; set; }

    private bool IsMatch(Regex pattern, string text)
    {
        if (TimeoutForTest?.Invoke(pattern) == true) throw new RegexMatchTimeoutException(string.Empty, pattern.ToString(), pattern.MatchTimeout);
        return pattern.IsMatch(text);
    }

    // --- warm-up ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A line for the warm-up: letters of both cases, digits and the punctuation the patterns read (personal data included), so
    /// the lazily built matcher states a real line needs exist before one arrives.
    /// </summary>
    internal const string WarmUpLine = "Warm up: the quick brown fox jumps over the lazy dog, THE QUICK BROWN FOX! 0123456789 k y s "
        + "kiiill k1ll y0urs3lf mail a.b-c_d+e@example.co.uk (at) [dot] call +1 (555) 123-4567 or 555.123.4567, 221 Baker Street ?#$&*";

    private Task? _warmed;
    private int _warmedPatterns;

    /// <summary>
    /// Every pattern of this filter (the personal-data ones included) run once over <see cref="WarmUpLine"/>, on the thread pool;
    /// started at the first call, the same task after. A pattern's first match builds its matcher (the one-time cost behind a
    /// "not judged in time" on a harmless first line); the warm-up pays it instead of a player's line.
    /// </summary>
    internal Task Warmed
    {
        get
        {
            if (Volatile.Read(ref _warmed) is { } started) return started;
            var task = new Task(WarmUp);
            if (Interlocked.CompareExchange(ref _warmed, task, null) is { } other) return other;
            task.Start(TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>Whether the warm-up has been started (reading this does not start it, unlike <see cref="Warmed"/>).</summary>
    internal bool WarmUpStarted => Volatile.Read(ref _warmed) is not null;

    /// <summary>How many patterns the warm-up has run (each pattern once; the count of <see cref="AllPatterns"/> when done).</summary>
    internal int WarmedPatterns => Volatile.Read(ref _warmedPatterns);

    private void WarmUp()
    {
        SafetyForms forms = Normalize(WarmUpLine);
        foreach (Regex pattern in AllPatterns)
        {
            try
            {
                pattern.IsMatch(WarmUpLine);
                pattern.IsMatch(forms.Plain);
                pattern.IsMatch(forms.Leet);
            }
            catch (RegexMatchTimeoutException)
            {
                // A cold match ran out of time here instead of on a player's line: the next one is warm.
            }

            Interlocked.Increment(ref _warmedPatterns);
        }
    }

    // --- normalization ----------------------------------------------------------------------------------------------------

    /// <summary>The three forms of a line (each padded with one space on both ends).</summary>
    internal readonly record struct SafetyForms(string Plain, string Leet, string Terms, string? JoinedTerms);

    internal static SafetyForms Normalize(string text)
    {
        string lower = StripAccents(text).ToLowerInvariant();
        var plain = new StringBuilder(lower.Length + 2);
        var leet = new StringBuilder(lower.Length + 2);
        foreach (char ch in lower)
        {
            plain.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            char mapped = Leet(ch);
            leet.Append(char.IsLetterOrDigit(mapped) ? mapped : ' ');
        }

        string plainForm = Pad(plain.ToString());
        string spaced = Pad(leet.ToString());
        string joined = JoinSingles(spaced);
        string? joinedTerms = joined == spaced ? null : Squeeze(joined);
        return new SafetyForms(plainForm, joined, Squeeze(spaced), joinedTerms);
    }

    private static char Leet(char ch) => ch switch
    {
        '0' => 'o', '1' => 'i', '3' => 'e', '4' => 'a', '5' => 's', '7' => 't', '8' => 'b', '@' => 'a', '$' => 's', '!' => 'i',
        _ => ch,
    };

    private static string StripAccents(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(ch);
        return builder.ToString();
    }

    /// <summary>Single spaces, one leading and one trailing.</summary>
    private static string Pad(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append(' ');
        foreach (char ch in text)
            if (ch != ' ' || builder[^1] != ' ') builder.Append(ch);
        if (builder[^1] != ' ') builder.Append(' ');
        return builder.ToString();
    }

    /// <summary>Runs of three or more one-letter words joined ("k y s" is "kys").</summary>
    private static string JoinSingles(string padded)
    {
        string[] words = padded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var output = new List<string>(words.Length);
        for (int i = 0; i < words.Length;)
        {
            int run = i;
            while (run < words.Length && words[run].Length == 1 && char.IsLetter(words[run][0])) run++;
            if (run - i >= 3)
            {
                output.Add(string.Concat(words[i..run]));
                i = run;
            }
            else
            {
                output.Add(words[i]);
                i++;
            }
        }

        return " " + string.Join(' ', output) + " ";
    }

    /// <summary>Every run of a repeated letter cut to one ("kiiiill" is "kil").</summary>
    private static string Squeeze(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char ch in text)
            if (builder.Length == 0 || ch != builder[^1] || !char.IsLetter(ch)) builder.Append(ch);
        return builder.ToString();
    }

    // --- personal data ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="text"/> holds an email address, a phone number or a street address. A pattern out of time counts as a
    /// hit (unreadable in time: treated as personal data), named in <paramref name="timedOutOn"/>.
    /// </summary>
    internal bool HasPersonalData(string text, out string? timedOutOn)
    {
        timedOutOn = null;
        foreach ((Regex pattern, string name) in (ReadOnlySpan<(Regex, string)>)[(_email, "email"), (_phone, "phone"), (_streetAddress, "street address")])
        {
            try
            {
                if (IsMatch(pattern, text)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                timedOutOn = $"personal data ({name})";
                return true;
            }
        }

        return false;
    }

    /// <summary>Emails and phone numbers replaced by placeholders (<c>[email]</c>, <c>[phone]</c>), street addresses by <c>[address]</c>.</summary>
    internal string StripPersonalData(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        try
        {
            string result = _email.Replace(text, "[email]");
            result = _phone.Replace(result, "[phone]");
            return _streetAddress.Replace(result, "[address]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[removed]";
        }
    }

    private const RegexOptions Linear = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private const string EmailPattern = @"[A-Za-z0-9._%+\-]+\s*(@|\(at\)|\[at\])\s*[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\s*(\.|\(dot\)|\[dot\])\s*[A-Za-z]{2,}";

    /// <summary>
    /// A North American number (with or without +1, brackets, dots, dashes or spaces) or an international one (+ and 8 to 15 digits).
    /// </summary>
    private const string PhonePattern = @"(\+?1[\s.\-]?)?(\(\d{3}\)|\b\d{3})[\s.\-]?\d{3}[\s.\-]?\d{4}\b|\+\d{1,3}([\s.\-]?\d){7,14}\b";

    /// <summary>A house number, one or two capitalised words and a street suffix ("221 Baker Street", "12 Elm St").</summary>
    private const string StreetAddressPattern = @"\b\d{1,5}\s+([A-Z][a-z]+\s+){1,2}(Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Lane|Ln|Drive|Dr|Court|Ct|Way|Place|Pl)\b";

    private sealed class CategoryRules
    {
        public HashSet<string> Terms { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Prefixes { get; } = new(StringComparer.Ordinal);
        public List<Regex> Patterns { get; } = [];

        public bool MatchesTerm(SafetyForms forms) => Matches(forms.Terms) || (forms.JoinedTerms is { } joined && Matches(joined));

        private bool Matches(string text)
        {
            foreach (string term in Terms)
                if (text.Contains(" " + term + " ", StringComparison.Ordinal)) return true;
            foreach (string prefix in Prefixes)
                if (text.Contains(" " + prefix, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
