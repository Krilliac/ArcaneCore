using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// The label-filtered issue list of one publish pass, indexed by dedup marker and by number. Built once per pass by the transport and then only read
/// (plus <see cref="Add(QaIssueRef, string)"/> for an issue the pass itself created), so no finding needs its own list call. The first issue listed
/// for a marker wins (GitHub lists newest first). Not thread-safe: one pass owns it.
/// </summary>
internal sealed partial class QaIssueIndex(bool complete)
{
    private readonly Dictionary<string, QaIssueRef> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<int, QaIssueRef> _byNumber = [];
    private readonly Dictionary<int, string> _bodies = [];

    /// <summary>Whether the whole labelled list was read (it did not stop at the page or item limit); an issue missing from a complete index is not labelled.</summary>
    public bool Complete { get; } = complete;

    public int Count => _byNumber.Count;

    public QaIssueRef? ByMarker(string id) => _byId.GetValueOrDefault(id);

    public QaIssueRef? ByNumber(int number) => _byNumber.GetValueOrDefault(number);

    /// <summary>The body the list returned for issue <paramref name="number"/> (or the pass last wrote to it); null when the issue is not in the index.</summary>
    public string? BodyOf(int number) => _bodies.GetValueOrDefault(number);

    /// <summary>The pass rewrote the body of an indexed issue (<see cref="IQaIssueTransport.EditBodyAsync"/>).</summary>
    public void SetBody(int number, string body)
    {
        if (_byNumber.ContainsKey(number)) _bodies[number] = body;
    }

    /// <summary>Whether <paramref name="body"/> carries a Bot QA marker of ANY finding (an issue handed over by a merged finding carries that finding's, not the holder's).</summary>
    public static bool HasAnyMarker(string body) => !string.IsNullOrEmpty(body) && MarkerPattern().IsMatch(body);

    /// <summary>Index <paramref name="issue"/> under every exact <c>&lt;!-- arcanecore-bot-qa:id --&gt;</c> marker in <paramref name="body"/>.</summary>
    public void Add(QaIssueRef issue, string body)
    {
        if (!_byNumber.TryAdd(issue.Number, issue)) return;
        _bodies[issue.Number] = body ?? string.Empty;
        if (string.IsNullOrEmpty(body)) return;
        foreach (Match match in MarkerPattern().Matches(body))
        {
            _byId.TryAdd(match.Groups["id"].Value, issue);
        }
    }

    /// <summary>Index an issue the pass created for finding <paramref name="id"/>.</summary>
    public void AddCreated(QaIssueRef issue, string id)
    {
        _byNumber.TryAdd(issue.Number, issue);
        _byId.TryAdd(id, issue);
    }

    [GeneratedRegex(@"<!-- arcanecore-bot-qa:(?<id>[A-Za-z0-9]+) -->", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();
}

/// <summary>
/// What the publisher needs from GitHub, behind the REST client and the <c>gh</c> adapter. One instance serves one publish pass and its calls are
/// sequential. A failure after the retries throws <see cref="PlayerbotQaPublishException"/>; a create is never resent after an ambiguous failure.
/// </summary>
internal interface IQaIssueTransport : IDisposable
{
    /// <summary>Whether <see cref="SearchByMarkerAsync"/> can find an issue outside the label-filtered list (the REST search API, directly or through <c>gh api search/issues</c>).</summary>
    bool SupportsSearch { get; }

    /// <summary>List the labelled issues (open and closed, no pull requests) once and index them.</summary>
    Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken);

    /// <summary>One issue by number with its body; null when it does not exist.</summary>
    Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken);

    /// <summary>The issue holding the marker of <paramref name="id"/> found by search (an issue whose label was removed by hand); null when none. Only when <see cref="SupportsSearch"/>.</summary>
    Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken);

    Task<QaIssueRef> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken);

    Task CommentAsync(int number, string body, CancellationToken cancellationToken);

    Task ReopenAsync(int number, CancellationToken cancellationToken);

    /// <summary>Close an issue as not planned (a duplicate of another issue the caller has just linked in a comment).</summary>
    Task CloseAsDuplicateAsync(int number, CancellationToken cancellationToken);

    /// <summary>Close an issue as completed (its finding stopped on a newer build; the caller has just said so in a comment).</summary>
    Task CloseAsCompletedAsync(int number, CancellationToken cancellationToken);

    /// <summary>Replace an issue's body (the live status block: last seen, occurrences). A body edit sends no notification.</summary>
    Task EditBodyAsync(int number, string body, CancellationToken cancellationToken);
}
