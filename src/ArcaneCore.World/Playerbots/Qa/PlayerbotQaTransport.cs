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
    private readonly HashSet<int> _marked = [];

    /// <summary>Whether the whole labelled list was read (it did not stop at the page or item limit); an issue missing from a complete index is not labelled.</summary>
    public bool Complete { get; } = complete;

    public int Count => _byNumber.Count;

    public QaIssueRef? ByMarker(string id) => _byId.GetValueOrDefault(id);

    public QaIssueRef? ByNumber(int number) => _byNumber.GetValueOrDefault(number);

    /// <summary>Whether issue <paramref name="number"/> carries a Bot QA marker (of any finding): it is a bot issue, not one whose marker a person edited away.</summary>
    public bool HasMarker(int number) => _marked.Contains(number);

    /// <summary>Index <paramref name="issue"/> under every exact <c>&lt;!-- arcanecore-bot-qa:id --&gt;</c> marker in <paramref name="body"/>.</summary>
    public void Add(QaIssueRef issue, string body)
    {
        _byNumber.TryAdd(issue.Number, issue);
        if (string.IsNullOrEmpty(body)) return;
        foreach (Match match in MarkerPattern().Matches(body))
        {
            _byId.TryAdd(match.Groups["id"].Value, issue);
            _marked.Add(issue.Number);
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
    /// <summary>Whether <see cref="SearchByMarkerAsync"/> can find an issue outside the label-filtered list (the REST search API; <c>gh</c> has no such call here).</summary>
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
}
