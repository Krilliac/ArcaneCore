namespace ArcaneCore.Kernel.Items;

/// <summary>One page of readable text (vmangos <c>page_text</c>: entry, text, next_page), shown by items (<c>item_template.page_text</c>) and text objects.</summary>
public sealed record PageTextRecord(uint Entry, string Text, uint NextPage);

/// <summary>
/// The page texts, immutable once built. Building applies vmangos ObjectMgr::LoadPageTexts (ObjectMgr.cpp:6647-6687): a page whose next page
/// does not exist is reported and kept as it is (the query then answers that page as missing), and a chain that loops back is cut by setting
/// the next page of the page that closes the loop to 0 ("can cause the server to freeze").
/// </summary>
public sealed class PageTextCatalog
{
    private readonly Dictionary<uint, PageTextRecord> _pages;

    public PageTextCatalog(IEnumerable<PageTextRecord> pages, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _pages = [];
        foreach (PageTextRecord page in pages)
        {
            _pages[page.Entry] = page;
        }

        foreach (uint entry in _pages.Keys.Order().ToArray())
        {
            PageTextRecord page = _pages[entry];
            if (page.NextPage != 0 && !_pages.ContainsKey(page.NextPage))
            {
                report?.Invoke($"Page text (Id: {entry}) has not existing next page (Id:{page.NextPage})");
                continue;
            }

            var seen = new HashSet<uint>();
            for (PageTextRecord? at = page; at is not null && at.NextPage != 0; at = _pages.GetValueOrDefault(at.NextPage))
            {
                seen.Add(at.Entry);
                if (seen.Contains(at.NextPage))
                {
                    report?.Invoke($"The text page(s) {string.Join(' ', seen.Order())} create(s) a circular reference; next_page of page {at.Entry} set to 0");
                    _pages[at.Entry] = at with { NextPage = 0 };
                    break;
                }
            }
        }
    }

    public static PageTextCatalog Empty { get; } = new([]);

    public int Count => _pages.Count;

    public PageTextRecord? Find(uint entry) => _pages.GetValueOrDefault(entry);
}
