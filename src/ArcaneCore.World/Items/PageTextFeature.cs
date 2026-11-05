using ArcaneCore.Data.Content.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Items;

/// <summary>
/// Readable pages of items (books, letters, plaques: <c>item_template.page_text</c> with CMSG_READ_ITEM) and of text objects
/// (SMSG_GAMEOBJECT_PAGETEXT): the client asks for each page with CMSG_PAGE_TEXT_QUERY and this feature answers from the immutable
/// <see cref="PageTextCatalog"/>, read once at startup from the world database's <c>page_text</c> table (<see cref="IPageTextStore"/>; filled by
/// <c>arcane-content-importer import</c> or <c>refresh</c> from the content dump).
/// </summary>
public sealed class PageTextFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PageTextFeature> _logger;

    public PageTextFeature(IServiceProvider services, ILogger<PageTextFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The pages in force (<see cref="PageTextCatalog.Empty"/> when the world database has no <c>page_text</c> rows).</summary>
    public PageTextCatalog Pages { get; private set; } = PageTextCatalog.Empty;

    public void Attach(WorldRuntime world)
    {
        IReadOnlyList<PageTextRecord> rows = [];
        if (_services.GetService<IServiceScopeFactory>() is { } scopes)
        {
            using IServiceScope scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<IPageTextStore>() is { } store)
            {
                // Fail closed: a database that cannot be read refuses startup.
                rows = store.LoadAsync().GetAwaiter().GetResult();
            }
        }

        if (rows.Count == 0)
        {
            _logger.LogWarning("The world database has no page_text rows (import the content dump): item and object pages answer \"{Missing}\"", ItemMiscPackets.MissingPageText);
            return;
        }

        Pages = new PageTextCatalog(rows, message => _logger.LogWarning("{Message}", message));
        _logger.LogInformation("Loaded {Count} page texts from the world database", Pages.Count);
    }

    /// <summary>Replace the pages (tests and tools; the world thread or before the first query).</summary>
    public void Replace(PageTextCatalog pages) => Pages = pages ?? throw new ArgumentNullException(nameof(pages));

    /// <summary>
    /// vmangos WorldSession::HandlePageTextQueryOpcode (QueryHandler.cpp:263-299): one SMSG_PAGE_TEXT_QUERY_RESPONSE per page from
    /// <paramref name="pageId"/> along the next-page chain; an unknown page is answered "Item page missing." with no next page, which ends it.
    /// The catalog cut every loop at load, and the walk is also bounded by the page count, so a reply can never run away.
    /// </summary>
    public void Answer(WorldSession session, uint pageId)
    {
        ArgumentNullException.ThrowIfNull(session);
        PageTextCatalog pages = Pages;
        for (int sent = 0; pageId != 0 && sent <= pages.Count; sent++)
        {
            if (pages.Find(pageId) is not { } page)
            {
                session.Send(WorldOpcode.SmsgPageTextQueryResponse, ItemMiscPackets.PageTextQueryResponse(pageId, ItemMiscPackets.MissingPageText, 0));
                return;
            }

            session.Send(WorldOpcode.SmsgPageTextQueryResponse, ItemMiscPackets.PageTextQueryResponse(pageId, page.Text, page.NextPage));
            pageId = page.NextPage;
        }
    }
}

/// <summary>CMSG_PAGE_TEXT_QUERY (build 5875: u32 page id, then an optional u64 GUID the server ignores; vmangos QueryPageText::Read).</summary>
public sealed class PageTextHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnSession(WorldOpcode.CmsgPageTextQuery, SessionStates.LoggedIn, (session, payload) =>
        {
            session.Services.GetRequiredService<PageTextFeature>().Answer(session, new PacketReader(payload).ReadUInt32());
            return Task.CompletedTask;
        });
}
