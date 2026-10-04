using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Gm.Audit;

// GM tickets: one open ticket per character (vmangos GMTicketMgr keeps a map by character guid and an id map).
public sealed partial class GmAuditFeature
{
    private readonly Dictionary<int, GmTicketRecord> _tickets = [];
    private int _lastTicketId;

    /// <summary>The character's open ticket, if any.</summary>
    public GmTicketRecord? OpenTicketOf(int characterId)
    {
        lock (_gate)
        {
            return _tickets.Values.FirstOrDefault(t => t.CharacterId == characterId);
        }
    }

    /// <summary>The open ticket with this id, if any (a closed ticket is history, not listed).</summary>
    public GmTicketRecord? OpenTicket(int ticketId)
    {
        lock (_gate)
        {
            return _tickets.GetValueOrDefault(ticketId);
        }
    }

    /// <summary>Every open ticket, oldest (lowest id) first.</summary>
    public IReadOnlyList<GmTicketRecord> OpenTickets()
    {
        lock (_gate)
        {
            return [.. _tickets.Values.OrderBy(t => t.Id)];
        }
    }

    /// <summary>
    /// File a ticket for the character. Null when it already has an open ticket (vmangos HandleGMTicketCreateOpcode:
    /// GMTICKET_RESPONSE_ALREADY_EXIST). Ids count up from the highest ever stored.
    /// </summary>
    public GmTicketRecord? CreateTicket(int characterId, string text, byte category, uint mapId, float x, float y, float z)
    {
        GmTicketRecord ticket;
        lock (_gate)
        {
            if (_tickets.Values.Any(t => t.CharacterId == characterId))
            {
                return null;
            }

            long now = NowUnixSeconds;
            ticket = new GmTicketRecord(++_lastTicketId, characterId, text, category, mapId, x, y, z, now, now, GmTicketStatus.Open, string.Empty, string.Empty, 0);
            _tickets[ticket.Id] = ticket;
        }

        PersistTicket(ticket);
        return ticket;
    }

    /// <summary>Replace the text of the character's open ticket (vmangos HandleGMTicketUpdateTextOpcode). Null when it has none.</summary>
    public GmTicketRecord? UpdateTicketText(int characterId, string text)
    {
        GmTicketRecord updated;
        lock (_gate)
        {
            GmTicketRecord? ticket = _tickets.Values.FirstOrDefault(t => t.CharacterId == characterId);
            if (ticket is null)
            {
                return null;
            }

            updated = ticket with { Text = text, UpdatedAt = NowUnixSeconds };
            _tickets[updated.Id] = updated;
        }

        PersistTicket(updated);
        return updated;
    }

    /// <summary>Record the staff answer on an open ticket, which stays open. Null when there is no such ticket.</summary>
    public GmTicketRecord? RespondToTicket(int ticketId, string response)
    {
        GmTicketRecord updated;
        lock (_gate)
        {
            if (!_tickets.TryGetValue(ticketId, out GmTicketRecord? ticket))
            {
                return null;
            }

            updated = ticket with { Response = response, UpdatedAt = NowUnixSeconds };
            _tickets[ticketId] = updated;
        }

        PersistTicket(updated);
        return updated;
    }

    /// <summary>
    /// Close an open ticket: it leaves the open set and its row stays as history with who closed it (and the final
    /// <paramref name="response"/> when given). Null when there is no such ticket.
    /// </summary>
    public GmTicketRecord? CloseTicket(int ticketId, string closedBy, string? response)
    {
        GmTicketRecord closed;
        lock (_gate)
        {
            if (!_tickets.Remove(ticketId, out GmTicketRecord? ticket))
            {
                return null;
            }

            long now = NowUnixSeconds;
            closed = ticket with
            {
                Status = GmTicketStatus.Closed,
                Response = string.IsNullOrEmpty(response) ? ticket.Response : response,
                ClosedBy = closedBy,
                ClosedAt = now,
                UpdatedAt = now,
            };
        }

        PersistTicket(closed);
        return closed;
    }

    /// <summary>Delete an open ticket outright, row included (vmangos <c>.ticket delete</c>). False when there is no such ticket.</summary>
    public bool DeleteTicket(int ticketId) => RemoveTicket(ticketId) is not null;

    /// <summary>The player withdrew their own ticket (vmangos HandleGMTicketDeleteTicketOpcode). False when it had none.</summary>
    public bool DeleteTicketOf(int characterId)
    {
        GmTicketRecord? ticket = OpenTicketOf(characterId);
        return ticket is not null && DeleteTicket(ticket.Id);
    }

    private GmTicketRecord? RemoveTicket(int ticketId)
    {
        GmTicketRecord? removed;
        lock (_gate)
        {
            _tickets.Remove(ticketId, out removed);
        }

        if (removed is not null)
        {
            Writes.Save(TicketKey(ticketId), store => store.DeleteTicketAsync(ticketId));
        }

        return removed;
    }

    private void PersistTicket(GmTicketRecord ticket) => Writes.Save(TicketKey(ticket.Id), store => store.SaveTicketAsync(ticket));

    private static string TicketKey(int ticketId) => "ticket:" + ticketId;

    // ---- character deletion (docs/integration/character-delete.md) -------------------------------

    /// <summary>Queued ticket writes drain before the character's rows are removed.</summary>
    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character) => Writes.FlushAsync();

    /// <summary>
    /// The character's open ticket goes with it (the storage cleanup removes the rows). A later queued write of the
    /// ticket is ignored by the store because the character no longer exists, so nothing can bring it back.
    /// </summary>
    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (OpenTicketOf(character.Id) is { } ticket)
        {
            DeleteTicket(ticket.Id);
        }

        await Writes.FlushAsync().WaitAsync(CharacterDeletion.DrainTimeout).ConfigureAwait(false);
    }
}
