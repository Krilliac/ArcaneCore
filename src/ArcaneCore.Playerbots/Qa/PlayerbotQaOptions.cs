using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Bot QA (<c>World:Playerbots:Qa</c>, docs/areas/playbots-qa.md): the warnings the bots and the creature scripts log are
/// aggregated into ranked findings (<c>.playerbot qa</c>, <c>qa/report.md</c>), and optionally reported as GitHub issues.
/// Read once at start; a change needs a restart.
/// </summary>
public sealed class PlayerbotQaOptions
{
    public const string SectionName = "World:Playerbots:Qa";

    /// <summary>Aggregate the bot stall, death-loop, fault and missing-data warnings into findings (on by default; it is cheap and never touches the world thread).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The folder of the findings file, the exports (<c>report.md</c>, <c>report.json</c>) and the dry-run issues (<c>pending-issues/</c>): relative to the profile folder, or absolute.</summary>
    public string Directory { get; set; } = "qa";

    /// <summary>Seconds between two passes that save the findings, write the exports and publish (10..86400).</summary>
    public int ExportIntervalSeconds { get; set; } = 300;

    /// <summary>The most findings kept (10..100000); beyond it an unpublished finding is dropped, a one-off of a single bot first, then the lowest severity, then the oldest. A published or known-closed finding is never dropped.</summary>
    public int MaxFindings { get; set; } = 2000;

    /// <summary>Hours after which a finding not seen again and not published or known-closed is dropped, on load and on every pass (0..8760; 0 keeps them for ever).</summary>
    public int StaleHours { get; set; } = 24;

    /// <summary>Hours after which a finding not seen again is forgotten on every pass, counted from the later of its last sighting and the server start (so a restart prunes nothing at once). A finding that holds a GitHub issue is never forgotten, and GitHub is never touched. 0 (default) is off; the same as <c>.playerbot qa prune #hours</c> (0..8760).</summary>
    public int AutoPruneHours { get; set; }

    /// <summary>Minutes after the world starts ticking during which a tick hang is not reported to QA (start-up: cold grids, JIT, loading). 0 reports from the first tick (0..1440, default 5).</summary>
    public int TickHangWarmupMinutes { get; set; } = 5;

    /// <summary>The newest evidence samples kept per finding (1..20).</summary>
    public int MaxSamples { get; set; } = 5;

    /// <summary>Warnings waiting for the aggregator at once (64..1000000); a full queue drops the oldest and counts it.</summary>
    public int SignalQueueCapacity { get; set; } = 4096;

    /// <summary>The GitHub issue reporter (<c>World:Playerbots:Qa:GitHub</c>).</summary>
    public PlayerbotQaGitHubOptions GitHub { get; set; } = new();

    internal static void ApplyConfiguration(PlayerbotQaOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        // ConfigurationBinder appends array elements to a non-empty initialised array: explicit labels replace the default.
        string githubKey = nameof(GitHub);
        if (section.GetSection(githubKey).GetChildren().Any(child => string.Equals(child.Key, nameof(PlayerbotQaGitHubOptions.Labels), StringComparison.OrdinalIgnoreCase)))
        {
            options.GitHub.Labels = [];
        }

        section.Bind(options);
    }

    /// <summary>The problems of these values (empty when valid); the GitHub repository is only checked while the reporter is enabled.</summary>
    internal IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Directory) || Directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            problems.Add("Directory must be a non-empty folder path");
        }
        else if (!Path.IsPathRooted(Directory) && Directory.Split('/', '\\').Contains(".."))
        {
            problems.Add("a relative Directory must not contain '..'");
        }

        if (ExportIntervalSeconds is < 10 or > 86_400) problems.Add("ExportIntervalSeconds must be 10..86400");
        if (MaxFindings is < 10 or > 100_000) problems.Add("MaxFindings must be 10..100000");
        if (StaleHours is < 0 or > 8760) problems.Add("StaleHours must be 0..8760");
        if (AutoPruneHours is < 0 or > 8760) problems.Add("AutoPruneHours must be 0..8760");
        if (TickHangWarmupMinutes is < 0 or > 1440) problems.Add("TickHangWarmupMinutes must be 0..1440");
        if (MaxSamples is < 1 or > 20) problems.Add("MaxSamples must be 1..20");
        if (SignalQueueCapacity is < 64 or > 1_000_000) problems.Add("SignalQueueCapacity must be 64..1000000");
        if (GitHub is null) problems.Add("GitHub is missing");
        else problems.AddRange(GitHub.Validate());
        return problems;
    }
}

/// <summary>How bot QA talks to GitHub.</summary>
public enum PlayerbotQaGitHubTransport
{
    /// <summary>The GitHub REST API over HTTPS with a token read from the environment (default).</summary>
    Rest = 0,

    /// <summary>The <c>gh</c> command-line tool, which uses its own login (no token is read or passed unless <c>GhPassToken</c> is set).</summary>
    GhCli = 1,
}

/// <summary>
/// The GitHub reporter of bot QA (<c>World:Playerbots:Qa:GitHub</c>). Off by default; when enabled it only writes the issues it would
/// open (<c>qa/pending-issues/</c>) until <see cref="Publish"/> is set. The token is read from the environment variable named by
/// <see cref="TokenEnvironmentVariable"/> and nowhere else: there is no token key.
/// </summary>
public sealed class PlayerbotQaGitHubOptions
{
    /// <summary>Report findings to GitHub (off by default). Enabled without <see cref="Publish"/> is a dry run: no network call is made.</summary>
    public bool Enabled { get; set; }

    /// <summary>The repository, <c>owner/repo</c> (required while enabled).</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>
    /// How issues are read and written: <c>Rest</c> (default; the GitHub REST API with the token from <see cref="TokenEnvironmentVariable"/>) or <c>GhCli</c> (runs the
    /// <c>gh</c> tool with an argument list and no shell, using the login of the user the server runs as; the token variable is not needed).
    /// </summary>
    public PlayerbotQaGitHubTransport Transport { get; set; } = PlayerbotQaGitHubTransport.Rest;

    /// <summary>The <c>gh</c> executable for <c>Transport</c> = <c>GhCli</c>: a file name looked up on PATH (<c>gh</c>) or a full path (1..260 characters).</summary>
    public string GhPath { get; set; } = "gh";

    /// <summary>For <c>GhCli</c> only: hand the token from <see cref="TokenEnvironmentVariable"/> to <c>gh</c> as <c>GH_TOKEN</c> (off by default: <c>GITHUB_TOKEN</c> and <c>GH_TOKEN</c> are removed from the child so <c>gh</c> uses its own login).</summary>
    public bool GhPassToken { get; set; }

    /// <summary>True when publishing reads the token variable: the REST transport always, <c>gh</c> only with <see cref="GhPassToken"/> (it otherwise uses its own login). The publisher and the start-up check share this rule.</summary>
    internal bool RequiresToken() => Transport != PlayerbotQaGitHubTransport.GhCli || GhPassToken;

    /// <summary>The name of the environment variable that holds a fine-grained token with Issues read and write on the repository (the value is never read from configuration and never logged).</summary>
    public string TokenEnvironmentVariable { get; set; } = "ARCANECORE_GITHUB_TOKEN";

    /// <summary>Really create issues and comments (off by default = dry run: the would-be issues are written to <c>qa/pending-issues/</c> and logged, nothing is sent).</summary>
    public bool Publish { get; set; }

    /// <summary>A finding is reported once it was seen this many times (1..1000000).</summary>
    public int MinOccurrences { get; set; } = 3;

    /// <summary>A finding caused by bots is reported once this many different bots met it (1..10000); data findings of creature scripts are exempt.</summary>
    public int MinDistinctBots { get; set; } = 2;

    /// <summary>The most issues created per rolling hour (0..1000; 0 creates none); the rest stay pending.</summary>
    public int MaxIssuesPerHour { get; set; } = 5;

    /// <summary>
    /// A new issue is opened only for a finding met on the running build, or last seen within this many hours (0..8760; 0 = the running build only). A finding known only from
    /// history (loaded at start-up, last met on an older build earlier than that) never opens one, and is not looked up; an issue it already holds (by number) is still updated.
    /// </summary>
    public int CreateWithinHours { get; set; } = 24;

    /// <summary>The most comments added per rolling hour (0..1000; 0 adds none).</summary>
    public int MaxCommentsPerHour { get; set; } = 10;

    /// <summary>At most one comment per finding in this many hours (1..720).</summary>
    public int CommentIntervalHours { get; set; } = 24;

    /// <summary>Reopen a finding's issue when it was closed and the finding keeps happening (off by default: a closed issue is recorded as known and left alone).</summary>
    public bool ReopenClosed { get; set; }

    /// <summary>
    /// Reopen a closed issue, with one comment (the new build, the occurrences on it, the distinct bots and a sample), when its finding recurs on a build other than the one it was
    /// observed closed on (a regression; a recurrence on the same build is a person's won't-fix and is left alone). Acts only while publishing is on, under the comment cap; never closes anything.
    /// </summary>
    public bool ReopenOnRegression { get; set; } = true;

    /// <summary>
    /// When findings are merged into one (the same problem met in several places) and each had its own issue, comment on the absorbed issue with a link to the surviving one and
    /// close it as not planned (a duplicate). Off (the default, so the first start after an upgrade can be checked: the start-up log lists every issue it would close, each with the surviving issue) keeps the absorbed issue open and untouched; the merge is recorded in the store either way.
    /// </summary>
    public bool CloseAbsorbedDuplicates { get; set; }

    /// <summary>
    /// Keep a live status block at the top of each open issue's body (last seen UTC, occurrences in all and on the running build, distinct bots), rewritten when the finding was
    /// seen again at least this many minutes after the time the block shows (0..10080; 0 is off). Comments come at most every <see cref="CommentIntervalHours"/>, so without it
    /// GitHub's view of when a finding last happened lags by up to a day; with it the lag is at most this long. A body edit sends no notification; edits are capped by
    /// <see cref="MaxStatusEditsPerHour"/>.
    /// </summary>
    public int StatusUpdateMinutes { get; set; } = 60;

    /// <summary>The most live-status body edits per rolling hour (0..1000; 0 makes none), saved with the hourly caps.</summary>
    public int MaxStatusEditsPerHour { get; set; } = 30;

    /// <summary>
    /// Close an open issue as completed when its finding has stopped: it was last seen on an older build, the running build is newer and has not met it, and it has been quiet
    /// (since the later of its last sighting and the reporter's start) for this many times its usual gap between occurrences ((last seen - first seen) / (count - 1)) and at least
    /// <see cref="AutoCloseMinQuietHours"/>. One comment says why; a recurrence on any later build counts as a regression of the build it was last seen on and reopens it
    /// (<see cref="ReopenOnRegression"/>). 0 (default) is off (0..1000). Quiet is not proof: a finding the bots simply stopped visiting looks the same.
    /// </summary>
    public int AutoCloseQuietMultiple { get; set; }

    /// <summary>The least quiet time, in hours, before <see cref="AutoCloseQuietMultiple"/> may close an issue (1..8760, default 72), whatever the finding's usual gap.</summary>
    public int AutoCloseMinQuietHours { get; set; } = 72;

    /// <summary>The labels put on created issues; the first one is also how existing issues are listed (1..10 labels; a configured list replaces the default).</summary>
    public string[] Labels { get; set; } = ["bot-qa"];

    /// <summary>
    /// The most spell issues (spell unsupported, refused, no effect) created in one publish pass (0..100; 0 creates none). Spell findings can be
    /// numerous on a first run; the hourly <see cref="MaxIssuesPerHour"/> still applies on top.
    /// </summary>
    public int MaxSpellIssuesPerPass { get; set; } = 2;

    /// <summary>The most spell issues created per rolling 24 hours (0..1000; 0 creates none), saved with the hourly caps.</summary>
    public int MaxSpellIssuesPerDay { get; set; } = 10;

    /// <summary>
    /// Add a per-kind label (<c>bot-qa:spell-unsupported</c>, <c>bot-qa:death-loop</c>, ...) to created issues besides <see cref="Labels"/> (off by
    /// default: the <c>gh</c> transport refuses a label that does not exist yet, so create the labels in the repository first).
    /// </summary>
    public bool KindLabels { get; set; }

    /// <summary>Seconds one GitHub request may take (1..120).</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>The most 100-issue pages read when looking for an existing issue (1..100).</summary>
    public int MaxListPages { get; set; } = 10;

    internal IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Enabled && !IsValidRepository(Repository)) problems.Add("GitHub:Repository must be owner/repo while GitHub:Enabled is true");
        if (!Enum.IsDefined(Transport)) problems.Add("GitHub:Transport must be Rest or GhCli");
        if (string.IsNullOrWhiteSpace(GhPath) || GhPath.Length > 260 || GhPath.Any(char.IsControl)) problems.Add("GitHub:GhPath must be a 1..260 character executable name or path");
        if (!IsValidVariableName(TokenEnvironmentVariable)) problems.Add("GitHub:TokenEnvironmentVariable must be an environment variable name ([A-Z_][A-Z0-9_]*)");
        if (MinOccurrences is < 1 or > 1_000_000) problems.Add("GitHub:MinOccurrences must be 1..1000000");
        if (MinDistinctBots is < 1 or > 10_000) problems.Add("GitHub:MinDistinctBots must be 1..10000");
        if (MaxIssuesPerHour is < 0 or > 1000) problems.Add("GitHub:MaxIssuesPerHour must be 0..1000");
        if (CreateWithinHours is < 0 or > 8760) problems.Add("GitHub:CreateWithinHours must be 0..8760");
        if (MaxCommentsPerHour is < 0 or > 1000) problems.Add("GitHub:MaxCommentsPerHour must be 0..1000");
        if (CommentIntervalHours is < 1 or > 720) problems.Add("GitHub:CommentIntervalHours must be 1..720");
        if (MaxSpellIssuesPerPass is < 0 or > 100) problems.Add("GitHub:MaxSpellIssuesPerPass must be 0..100");
        if (MaxSpellIssuesPerDay is < 0 or > 1000) problems.Add("GitHub:MaxSpellIssuesPerDay must be 0..1000");
        if (RequestTimeoutSeconds is < 1 or > 120) problems.Add("GitHub:RequestTimeoutSeconds must be 1..120");
        if (MaxListPages is < 1 or > 100) problems.Add("GitHub:MaxListPages must be 1..100");
        if (StatusUpdateMinutes is < 0 or > 10_080) problems.Add("GitHub:StatusUpdateMinutes must be 0..10080");
        if (MaxStatusEditsPerHour is < 0 or > 1000) problems.Add("GitHub:MaxStatusEditsPerHour must be 0..1000");
        if (AutoCloseQuietMultiple is < 0 or > 1000) problems.Add("GitHub:AutoCloseQuietMultiple must be 0..1000");
        if (AutoCloseMinQuietHours is < 1 or > 8760) problems.Add("GitHub:AutoCloseMinQuietHours must be 1..8760");
        if (Labels is null || Labels.Length is < 1 or > 10 || Labels.Any(label => string.IsNullOrWhiteSpace(label) || label.Length > 50 || label.Any(char.IsControl)))
        {
            problems.Add("GitHub:Labels must be 1..10 non-empty labels of at most 50 characters");
        }

        return problems;
    }

    internal static bool IsValidRepository(string? repository)
    {
        if (string.IsNullOrEmpty(repository)) return false;
        int slash = repository.IndexOf('/', StringComparison.Ordinal);
        if (slash < 1 || slash > 39 || slash == repository.Length - 1 || repository.Length - slash - 1 > 100) return false;
        for (int i = 0; i < repository.Length; i++)
        {
            char c = repository[i];
            if (i == slash) continue;
            bool owner = i < slash;
            bool ok = char.IsAsciiLetterOrDigit(c) || c == '-' || (!owner && (c == '.' || c == '_'));
            if (!ok) return false;
        }

        string name = repository[(slash + 1)..];
        return name is not ("." or "..") && repository.IndexOf('/', slash + 1) < 0;
    }

    internal static bool IsValidVariableName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
        if (!(char.IsAsciiLetterUpper(name[0]) || name[0] == '_')) return false;
        return name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');
    }
}
