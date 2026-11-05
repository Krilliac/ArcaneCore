using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Guilds;

/// <summary>
/// Guild charters: the petition list, buying a charter, signing, offering, renaming, declining and
/// turning in (vmangos PetitionsHandler.cpp, GuildMgr.cpp Petition, Guild.cpp Create(petition)).
/// Every rule cites the vmangos line it follows; where this port is deliberately stricter or
/// different it says so. World thread, no I/O: writes go to <see cref="IPetitionPersistence"/>.
/// <para>
/// The petition id is the charter item's enchantment slot 0. vmangos trusts that id alone
/// (PetitionsHandler.cpp:147,204,374-409); ids here are reused after the highest petition is
/// deleted, so every use also binds the charter item's guid (no client-visible difference).
/// </para>
/// </summary>
public sealed class PetitionManager
{
    private readonly SocialContext _context;
    private readonly Dictionary<int, Petition> _petitions = [];
    private readonly Dictionary<Player, Action<uint, int>> _subscriptions = new(ReferenceEqualityComparer.Instance);
    private int _nextId = 1;

    public PetitionManager(SocialContext context)
    {
        _context = context;
        context.Guilds.MemberJoined += OnMemberJoined;
    }

    /// <summary>
    /// Durable block allocator for the <c>"petition"</c> scope. Set by the host before the world starts when several workers share one
    /// characters database; null keeps the process counter (Realm mode).
    /// </summary>
    public DurableIdBlockAllocator? IdBlockAllocator { get; set; }

    /// <summary>When true (Worker mode) a petition id is never taken from the process counter; an unprimed allocator throws instead.</summary>
    public bool RequireDurableIds { get; set; }

    internal int AllocatePetitionId()
    {
        if (IdBlockAllocator is { } allocator)
        {
            int allocated = allocator.NextInt32();
            if (allocated >= _nextId && allocated < int.MaxValue)
            {
                _nextId = allocated + 1;
            }

            return allocated;
        }

        if (RequireDurableIds)
        {
            throw new IdRangeExhaustedException(
                IdRangeScopes.Petition,
                $"Petition id allocator for scope '{IdRangeScopes.Petition}' is not primed with a durable block (fail-closed in Worker mode).");
        }

        if (_nextId <= 0)
        {
            throw new IdRangeExhaustedException(IdRangeScopes.Petition, "petition ID space exhausted");
        }

        return _nextId++;
    }

    /// <summary>The NPC and money services (set by the world daemon); without them nothing NPC-bound works.</summary>
    public QuestNpcServices? Npc { get; set; }

    /// <summary>Reserved charter names (vmangos IsReservedName); none by default.</summary>
    public ICharterNameBlacklist? Blacklist { get; set; }

    /// <summary>A charter purchase was refused as spam (vmangos LogChat "Attempt to create guild petition with spam name").</summary>
    public event Action<Player, string>? SpamRefused;

    /// <summary>Whether <see cref="Load"/> has run.</summary>
    public bool IsLoaded { get; private set; }

    public IReadOnlyCollection<Petition> All => _petitions.Values;

    private GuildOptions Options => _context.Guilds.Options;

    private IPetitionPersistence? Persistence => _context.Persistence as IPetitionPersistence;

    public Petition? Get(int id) => _petitions.GetValueOrDefault(id);

    /// <summary>vmangos GuildMgr::GetPetitionByOwnerGuid.</summary>
    public Petition? GetByOwner(uint ownerId) => _petitions.Values.FirstOrDefault(p => p.OwnerId == ownerId);

    /// <summary>vmangos GuildMgr::GetPetitionByCharterGuid.</summary>
    public Petition? GetByCharter(ObjectGuid charter) => _petitions.Values.FirstOrDefault(p => p.CharterGuid == charter);

    /// <summary>vmangos Petition::IsComplete: exactly the configured number of signatures (GuildMgr.h:124).</summary>
    public bool IsComplete(Petition petition) => petition.Signatures.Count == Options.EffectiveMinPetitionSigns;

    // --- load ----------------------------------------------------------------------------------------

    /// <summary>
    /// Install the stored petitions (vmangos GuildMgr::LoadPetitions, GuildMgr.cpp:168-246). A petition whose
    /// owner is gone or already in a guild is deleted (joining a guild removes the owner's petition, so it is
    /// stale), and signatures of characters that no longer exist or are already guilded are dropped. Run after the
    /// guilds are loaded. Only the authority writes these repairs: a replica reads.
    /// </summary>
    public void Load(IEnumerable<PetitionData> petitions)
    {
        foreach (PetitionData data in petitions.OrderBy(p => p.Id))
        {
            _nextId = Math.Max(_nextId, data.Id + 1);
            if (_context.Characters.Find((uint)data.OwnerId) is not { } owner
                || _context.Guilds.GetGuildOf((uint)data.OwnerId) is not null
                || _petitions.Values.Any(p => p.OwnerId == (uint)data.OwnerId))
            {
                if (_context.Guilds.IsAuthority)
                {
                    Persistence?.DeletePetition(data.Id);
                }

                continue;
            }

            var petition = new Petition(data.Id, (uint)data.OwnerId, (uint)data.CharterItemId, data.Name, owner.Team);
            bool changed = false;
            foreach (PetitionSignatureData signature in data.Signatures)
            {
                // Hardening: the sign path stops at ClientMaxSignatures, so a longer persisted list is malformed data (the packet
                // count byte and its allocation follow the list length). Drop the excess and persist the trim.
                // A signer already in a guild is stale too (Guild::AddMember → Player::RemovePetitionsAndSigns drops the
                // signature on join): kept, it would count towards IsComplete and then be skipped by the founding.
                if (petition.Signatures.Count >= PetitionConstants.ClientMaxSignatures
                    || _context.Characters.Find((uint)signature.PlayerId) is null || petition.ForPlayer((uint)signature.PlayerId) is not null
                    || _context.Guilds.GetGuildOf((uint)signature.PlayerId) is not null)
                {
                    changed = true;
                    continue;
                }

                petition.Add(new PetitionSignature((uint)signature.PlayerId, signature.AccountId));
            }

            _petitions[petition.Id] = petition;
            if (changed && _context.Guilds.IsAuthority)
            {
                Persistence?.SavePetition(petition.ToData());
            }
        }

        IsLoaded = true;
    }

    // --- NPC requests --------------------------------------------------------------------------------

    /// <summary>CMSG_PETITION_SHOWLIST and the Petitioner gossip option (vmangos SendPetitionShowList, PetitionsHandler.cpp:481-507).</summary>
    public void ShowList(Player player, ObjectGuid npc)
    {
        if (Npc?.InteractableNpc(player, npc, NpcFlags.Petitioner) is null)
        {
            return;
        }

        player.Session.Send(WorldOpcode.SmsgPetitionShowlist, PetitionPackets.BuildShowList(npc));
    }

    /// <summary>
    /// CMSG_PETITION_BUY (vmangos HandlePetitionBuyOpcode, PetitionsHandler.cpp:41-135), in its order: an
    /// interactable petitioner that is also a tabard designer; silent when guilded or already owning a
    /// petition; a taken name answers NAME_EXISTS_S, a reserved or invalid one NAME_INVALID; no charter
    /// template, no money and no bag space answer their own errors; then the 1000 copper charge, the charter
    /// (petition id in enchantment slot 0), SMSG_ITEM_PUSH_RESULT and the petition.
    /// </summary>
    /// <remarks>
    /// vmangos charges before it stores the item; this port stores first and charges second (the vendor
    /// order, Vendor.cs) so the character save the charge triggers already holds the charter. Both orders
    /// end with the same state and the same packets.
    /// </remarks>
    public void Buy(Player player, ObjectGuid npcGuid, string name)
    {
        if (Npc?.InteractableNpc(player, npcGuid, NpcFlags.Petitioner) is not { } creature || (creature.NpcFlags & NpcFlags.TabardDesigner) == 0)
        {
            return;
        }

        if (!IsLoaded || _context.Guilds.GetGuildOf(player) is not null || GetByOwner(player.Guid.Low) is not null)
        {
            return;
        }

        if (_context.Guilds.GetByName(name) is not null)
        {
            SendResult(player, GuildCommand.Create, name, GuildCommandError.NameExistsS);
            return;
        }

        if (!CharterNameRules.IsValid(name, Options, Blacklist))
        {
            SendResult(player, GuildCommand.Create, name, GuildCommandError.NameInvalid);
            return;
        }

        // vmangos HandlePetitionBuyOpcode (PetitionsHandler.cpp:87-95): the antispam check, after the name rules.
        if (_context.Guilds.Antispam?.IsSpam(name) == true)
        {
            SpamRefused?.Invoke(player, name);
            SendResult(player, GuildCommand.Create, name, GuildCommandError.NameInvalid);
            return;
        }

        if (player.Inventory.Templates.Find(PetitionConstants.CharterEntry) is not { } template)
        {
            player.Session.Send(WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(ObjectGuid.Empty, PetitionConstants.CharterEntry, BuyResult.CantFindItem).AsSpan());
            return;
        }

        if (player.Money < PetitionConstants.CharterCost)
        {
            player.Session.Send(WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npcGuid, PetitionConstants.CharterEntry, BuyResult.NotEnoughMoney).AsSpan());
            return;
        }

        var destination = new List<ItemPosCount>();
        Items.InventoryResult result = player.Inventory.CanStoreNewItem(PetitionConstants.CharterEntry, template.BuyCount, destination, out _);
        if (result != Items.InventoryResult.Ok)
        {
            player.Inventory.SendEquipError(result, null, null, 0, PetitionConstants.CharterEntry);
            return;
        }

        // vmangos charges, then stores (PetitionsHandler.cpp:116-125). The charge can fail with money in hand (the
        // quest-NPC state is not Loaded yet), and then nothing is handed out.
        // The id comes first: AllocatePetitionId can throw (unprimed Worker-mode allocator, drained block), and the copper
        // taken by TryCharge is flushed, so a throw after the charge would cost the player money for nothing.
        int id = AllocatePetitionId();
        if (!Npc.TryCharge(player, PetitionConstants.CharterCost))
        {
            return;
        }

        Item charter = player.Inventory.StoreNewItem(destination, template, template.BuyCount);
        charter.SetUInt32(UpdateFields.ItemFieldEnchantment + (PetitionConstants.EnchantmentSlot * 3), (uint)id);

        var petition = new Petition(id, player.Guid.Low, charter.Guid.Low, name, player.Team);
        _petitions[id] = petition;
        Persist(petition);
        // The push result last: the send can reach the client before this handler returns, and a client (or a test) that saw the
        // charter arrive must find its petition already queued for the store.
        player.Session.Send(WorldOpcode.SmsgItemPushResult, ItemPackets.ItemPushResult(player.Guid, charter, 1, received: true, created: false, showInChat: true));
    }

    // --- charter requests ----------------------------------------------------------------------------

    /// <summary>CMSG_PETITION_SHOW_SIGNATURES (vmangos HandlePetitionShowSignOpcode, PetitionsHandler.cpp:137-169).</summary>
    public void ShowSignatures(Player player, ObjectGuid itemGuid)
    {
        if (_context.Guilds.GetGuildOf(player) is not null || PetitionOfHeldCharter(player, itemGuid) is not { } petition)
        {
            return;
        }

        player.Session.Send(WorldOpcode.SmsgPetitionShowSignatures,
            PetitionPackets.BuildShowSignatures(itemGuid, player.Guid, petition.Id, petition.Signatures));
    }

    /// <summary>CMSG_PETITION_QUERY (vmangos HandlePetitionQueryOpcode, PetitionsHandler.cpp:171-185): by petition id alone, like vmangos.</summary>
    public void Query(Player player, uint petitionId)
    {
        if (Get((int)petitionId) is { } petition)
        {
            player.Session.Send(WorldOpcode.SmsgPetitionQueryResponse, PetitionPackets.BuildQueryResponse(petition));
        }
    }

    /// <summary>MSG_PETITION_RENAME (vmangos HandlePetitionRenameOpcode, PetitionsHandler.cpp:187-216): name checks first, then the charter.</summary>
    public void Rename(Player player, ObjectGuid itemGuid, string newName)
    {
        if (player.Inventory.GetItemByGuid(itemGuid) is null)
        {
            return;
        }

        if (_context.Guilds.GetByName(newName) is not null)
        {
            SendResult(player, GuildCommand.Create, newName, GuildCommandError.NameExistsS);
            return;
        }

        if (!CharterNameRules.IsValid(newName, Options, Blacklist))
        {
            SendResult(player, GuildCommand.Create, newName, GuildCommandError.NameInvalid);
            return;
        }

        if (PetitionOfHeldCharter(player, itemGuid) is not { } petition)
        {
            return;
        }

        petition.Name = newName;
        Persist(petition);
        player.Session.Send(WorldOpcode.MsgPetitionRename, PetitionPackets.BuildRenameResult(itemGuid, newName));
    }

    /// <summary>
    /// CMSG_PETITION_SIGN (vmangos HandlePetitionSignOpcode, PetitionsHandler.cpp:218-319), in its order. The
    /// error codes follow vmangos even where they look odd (a duplicate signature tells the owner
    /// ALREADY_INVITED_TO_GUILD_S, :281-286) so the client sees what it sees on vmangos.
    /// </summary>
    public void Sign(Player player, ObjectGuid itemGuid)
    {
        if (_context.Guilds.Link is { IsHolder: false } link)
        {
            // The petition and the signer's guild state are the holder's: it checks, signs and answers the signer and the owner wherever they are online.
            if (!link.Forward(new GuildRequest(GuildRequestKind.PetitionSign, player.Guid, Item: itemGuid.Value), null))
            {
                SendHolderUnreachable(player);
            }

            return;
        }

        Sign(GuildActor.Of(player), itemGuid);
    }

    private void Sign(GuildActor player, ObjectGuid itemGuid)
    {
        if (GetByCharter(itemGuid) is not { } petition || IsComplete(petition))
        {
            return;
        }

        if (petition.OwnerId == player.Guid.Low)
        {
            player.Send(WorldOpcode.SmsgPetitionSignResults, PetitionPackets.BuildSignResults(itemGuid, player.Guid, PetitionResult.CantSignOwn));
            return;
        }

        if (!_context.Options.AllowTwoSideGuild && player.Team != petition.Team)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.NotAllied);
            return;
        }

        // CheckGuildState for the signer itself (guilded, then invited), reported to the signer by its own name.
        if (_context.Guilds.GetGuildOf(player.Guid.Low) is not null)
        {
            SendResult(player, GuildCommand.Invite, player.Name, GuildCommandError.AlreadyInGuildS);
            return;
        }

        if (_context.Guilds.InvitedTo(player.Guid) != 0)
        {
            SendResult(player, GuildCommand.Invite, player.Name, GuildCommandError.AlreadyInvitedToGuildS);
            return;
        }

        if (petition.Signatures.Count >= PetitionConstants.ClientMaxSignatures)
        {
            return;
        }

        int account = player.AccountId;
        if (petition.ForAccount(account) is not null || petition.ForPlayer(player.Guid.Low) is not null)
        {
            player.Send(WorldOpcode.SmsgPetitionSignResults, PetitionPackets.BuildSignResults(itemGuid, player.Guid, PetitionResult.AlreadySigned));
            _context.Guilds.SendTo(petition.OwnerGuid, WorldOpcode.SmsgGuildCommandResult,
                GuildPackets.BuildCommandResult(GuildCommand.Invite, player.Name, GuildCommandError.AlreadyInvitedToGuildS));
            return;
        }

        petition.Add(new PetitionSignature(player.Guid.Low, account));
        Persist(petition);

        byte[] ok = PetitionPackets.BuildSignResults(itemGuid, player.Guid, PetitionResult.Ok);
        player.Send(WorldOpcode.SmsgPetitionSignResults, ok);
        _context.Guilds.SendTo(petition.OwnerGuid, WorldOpcode.SmsgPetitionSignResults, ok);
    }

    /// <summary>MSG_PETITION_DECLINE (vmangos HandlePetitionDeclineOpcode, PetitionsHandler.cpp:321-338): an online owner is told who declined.</summary>
    public void Decline(Player player, ObjectGuid itemGuid)
    {
        if (GetByCharter(itemGuid) is not { } petition)
        {
            return;
        }

        _context.Guilds.SendTo(petition.OwnerGuid, WorldOpcode.MsgPetitionDecline, PetitionPackets.BuildDeclineResult(player.Guid));
    }

    /// <summary>
    /// CMSG_OFFER_PETITION (vmangos HandleOfferPetitionOpcode, PetitionsHandler.cpp:340-398): the target is
    /// checked first, and its errors are reported to the offerer by the OFFERER's name; then the signature
    /// list of the offerer's charter goes to the target.
    /// </summary>
    public void Offer(Player player, ObjectGuid itemGuid, ObjectGuid target)
    {
        if (_context.World.FindOnlinePlayer(target) is not { } other)
        {
            OfferToOtherWorker(player, itemGuid, target);
            return;
        }

        if (!_context.Options.AllowTwoSideGuild && player.Team != other.Team)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.NotAllied);
            return;
        }

        if (!CheckGuildState(player, other, signer: player))
        {
            return;
        }

        if (PetitionOfHeldCharter(player, itemGuid) is not { } petition)
        {
            return;
        }

        other.Session.Send(WorldOpcode.SmsgPetitionShowSignatures,
            PetitionPackets.BuildShowSignatures(itemGuid, player.Guid, petition.Id, petition.Signatures));
    }

    /// <summary>
    /// CMSG_TURN_IN_PETITION (vmangos HandleTurnInPetitionOpcode, PetitionsHandler.cpp:400-475): guilded answers
    /// ALREADY_IN_GUILD, a non-owner is ignored, an incomplete petition answers NEED_MORE, a taken name
    /// NAME_EXISTS_S; otherwise the guild is created (owner guild master, still-unguilded signers lowest rank,
    /// no GE_JOINED), the petition deleted, the charter destroyed and OK sent. The guild and the petition
    /// deletion are stored in one transaction.
    /// </summary>
    public void TurnIn(Player player, ObjectGuid itemGuid)
    {
        if (PetitionOfHeldCharter(player, itemGuid) is not { } petition)
        {
            return;
        }

        if (_context.Guilds.GetGuildOf(player) is not null)
        {
            player.Session.Send(WorldOpcode.SmsgTurnInPetitionResults, PetitionPackets.BuildTurnInResult(PetitionResult.AlreadyInGuild));
            return;
        }

        if (petition.OwnerId != player.Guid.Low)
        {
            return;
        }

        if (!IsComplete(petition))
        {
            player.Session.Send(WorldOpcode.SmsgTurnInPetitionResults, PetitionPackets.BuildTurnInResult(PetitionResult.NeedMore));
            return;
        }

        if (_context.Guilds.GetByName(petition.Name) is not null)
        {
            SendResult(player, GuildCommand.Create, petition.Name, GuildCommandError.NameExistsS);
            return;
        }

        if (_context.Guilds.Link is { IsHolder: false } link)
        {
            // The holder founds the guild and drops the petition; the charter is destroyed and the answer sent here once it did.
            var request = new GuildRequest(GuildRequestKind.PetitionTurnIn, player.Guid, Item: itemGuid.Value, Id: petition.Id);
            if (!link.Forward(request, founded =>
            {
                if (founded)
                {
                    FinishTurnIn(player, itemGuid);
                }
            }))
            {
                SendHolderUnreachable(player);
            }

            return;
        }

        if (!Complete(petition, player.Guid.Low))
        {
            return;
        }

        FinishTurnIn(player, itemGuid);
    }

    /// <summary>
    /// The founding itself (authority only): the guild from the petition, the petition gone and both stored in one transaction, then published to the other workers.
    /// False when the guild could not be created.
    /// </summary>
    private bool Complete(Petition petition, uint ownerId)
    {
        if (_context.Guilds.CreateFromPetition(ownerId, petition.Name, petition.Signatures.Select(s => s.PlayerId), petition.Id) is not { } guild)
        {
            return false;
        }

        _petitions.Remove(petition.Id);
        Persistence?.CompletePetition(guild.ToData(), petition.Id);
        _context.Guilds.Publish(guild);
        Publish(petition.Id, null);
        return true;
    }

    /// <summary>
    /// "Deleting the charter deletes the petition if it still exists, so we do not want to do it before
    /// the guild is created" (PetitionsHandler.cpp:462-464): the petition is already gone here.
    /// </summary>
    private static void FinishTurnIn(Player player, ObjectGuid itemGuid)
    {
        if (player.Inventory.GetItemByGuid(itemGuid) is { } charter)
        {
            player.Inventory.DestroyItem(charter.BagSlot, charter.Slot);
        }

        player.Session.Send(WorldOpcode.SmsgTurnInPetitionResults, PetitionPackets.BuildTurnInResult(PetitionResult.Ok));
    }

    // --- lifecycle -----------------------------------------------------------------------------------

    /// <summary>
    /// A player entered the world: watch its charter being destroyed and heal a petition whose charter
    /// is gone (a crash between the item save and the petition write would otherwise block buying a new
    /// one forever, PetitionsHandler.cpp:69-71).
    /// </summary>
    public void OnPlayerLoggedIn(Player player)
    {
        if (_subscriptions.ContainsKey(player))
        {
            return;
        }

        Action<uint, int> handler = (entry, delta) =>
        {
            if (entry == PetitionConstants.CharterEntry && delta < 0)
            {
                DeleteOwnPetitionIfCharterGone(player);
            }
        };
        _subscriptions[player] = handler;
        player.Inventory.ItemCountChanged += handler;
        DeleteOwnPetitionIfCharterGone(player);
    }

    /// <summary>A player is leaving the world.</summary>
    public void OnPlayerLoggingOut(Player player)
    {
        if (_subscriptions.Remove(player, out Action<uint, int>? handler))
        {
            player.Inventory.ItemCountChanged -= handler;
        }
    }

    /// <summary>
    /// A character was deleted (vmangos Player::DeleteFromDB → RemovePetitionsAndSigns, Player.cpp:4353-4354):
    /// its petition and its signatures go from memory; the rows are removed by the deletion cleanup and the
    /// queued purge, so nothing is written here.
    /// </summary>
    public void OnCharacterDeleted(uint characterId)
    {
        foreach (Petition petition in _petitions.Values.ToList())
        {
            if (petition.OwnerId == characterId)
            {
                _petitions.Remove(petition.Id);
                Publish(petition.Id, null);
            }
            else if (petition.RemoveSigner(characterId))
            {
                Publish(petition.Id, petition.ToData());
            }
        }

        if (_context.Guilds.Link is { IsHolder: false } link)
        {
            link.Forward(new GuildRequest(GuildRequestKind.PetitionCharacterDeleted, ObjectGuid.Player(characterId)), null);
        }
    }

    /// <summary>
    /// A player joined a guild (vmangos Guild::AddMember → Player::RemovePetitionsAndSigns, Guild.cpp:213,
    /// Player.cpp:17796-17806): its signatures on other petitions and the petition it owns go. vmangos forgets
    /// the owned petition in memory (only the rows go); this port removes both so memory and storage agree.
    /// </summary>
    private void OnMemberJoined(Guild guild, uint characterId, int exceptPetitionId)
    {
        foreach (Petition petition in _petitions.Values.ToList())
        {
            if (petition.Id == exceptPetitionId)
            {
                continue;
            }

            if (petition.OwnerId == characterId)
            {
                Delete(petition);
            }
            else if (petition.RemoveSigner(characterId))
            {
                Persist(petition);
            }
        }
    }

    // --- cluster: the holder is authoritative, the other workers keep a replica ---------------------

    /// <summary>
    /// Run a petition request forwarded by another worker, on the holder (called by <see cref="GuildManager.Serve"/>). True when a turn-in founded the guild.
    /// </summary>
    public bool Serve(GuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Kind)
        {
            case GuildRequestKind.PetitionPut:
                if (request.Petition is { } data)
                {
                    Store(data);
                }

                return true;
            case GuildRequestKind.PetitionDelete:
                if (Get(request.Id) is { } gone)
                {
                    Delete(gone);
                }

                return true;
            case GuildRequestKind.PetitionCharacterDeleted:
                OnCharacterDeleted(request.Actor.Low);
                return true;
        }

        if (_context.Characters.Find(request.Actor.Low) is not { } info)
        {
            return false;
        }

        ObjectGuid guid = request.Actor;
        var actor = new GuildActor(guid, info.Name, info.Team, info.AccountId, (opcode, payload) => _context.Guilds.SendTo(guid, opcode, payload));
        switch (request.Kind)
        {
            case GuildRequestKind.PetitionSign:
                Sign(actor, new ObjectGuid(request.Item));
                return true;
            case GuildRequestKind.PetitionTurnIn:
                return ServeTurnIn(actor, request);
            case GuildRequestKind.PetitionOffer:
                return ServeOffer(actor, new ObjectGuid(request.Item), ObjectGuid.Player(request.A), request.B != 0);
            default:
                return false;
        }
    }

    /// <summary>
    /// An offer whose target is not on this worker: the holder checks the target against the authoritative guild state and sends it the signature list wherever it is online;
    /// any other worker forwards the offer. Without a link the target is simply not online.
    /// </summary>
    private void OfferToOtherWorker(Player player, ObjectGuid itemGuid, ObjectGuid target)
    {
        if (_context.Guilds.Link is not { } link || !target.IsPlayer)
        {
            return;
        }

        bool held = PetitionOfHeldCharter(player, itemGuid) is not null;
        if (link.IsHolder)
        {
            ServeOffer(GuildActor.Of(player), itemGuid, target, held);
            return;
        }

        if (!link.Forward(new GuildRequest(GuildRequestKind.PetitionOffer, player.Guid, Item: itemGuid.Value, A: target.Low, B: held ? 1u : 0u), null))
        {
            SendHolderUnreachable(player);
        }
    }

    /// <summary>
    /// The holder could not be reached, so a forwarded sign, offer or turn-in did not happen: answer the player with the guild "internal error" result, the same answer
    /// <see cref="GuildManager"/> gives a forwarded guild request that cannot be sent. ERR_GUILD_INTERNAL = 0x01 is in vmangos Guild.h:99 (CommandErrors); no vmangos handler sends it,
    /// so this is the nearest existing result rather than a retail behaviour, and the client shows its generic guild error text.
    /// </summary>
    private static void SendHolderUnreachable(Player player)
        => SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.Internal);

    /// <summary>
    /// The holder's half of <see cref="Offer"/> for a target on another worker: the same checks in the same order (team, guilded, invited, then the charter), against the
    /// authoritative state. <paramref name="held"/> is what the offerer's own worker saw in its bags. True when the signature list was sent.
    /// </summary>
    private bool ServeOffer(GuildActor offerer, ObjectGuid itemGuid, ObjectGuid target, bool held)
    {
        if (_context.Guilds.FindOnlineCharacter(target) is not { } other)
        {
            return false;
        }

        if (!_context.Options.AllowTwoSideGuild && offerer.Team != other.Team)
        {
            SendResult(offerer, GuildCommand.Create, string.Empty, GuildCommandError.NotAllied);
            return false;
        }

        if (_context.Guilds.GetGuildOf(other.Guid.Low) is not null)
        {
            SendResult(offerer, GuildCommand.Invite, offerer.Name, GuildCommandError.AlreadyInGuildS);
            return false;
        }

        if (_context.Guilds.InvitedTo(other.Guid) != 0)
        {
            SendResult(offerer, GuildCommand.Invite, offerer.Name, GuildCommandError.AlreadyInvitedToGuildS);
            return false;
        }

        if (!held || GetByCharter(itemGuid) is not { } petition)
        {
            return false;
        }

        _context.Guilds.SendTo(other.Guid, WorldOpcode.SmsgPetitionShowSignatures,
            PetitionPackets.BuildShowSignatures(itemGuid, offerer.Guid, petition.Id, petition.Signatures));
        return true;
    }

    /// <summary>The holder's half of <see cref="TurnIn"/> for an owner on another worker: the same checks against the authoritative state, then the founding.</summary>
    private bool ServeTurnIn(GuildActor owner, GuildRequest request)
    {
        if (Get(request.Id) is not { } petition || petition.CharterGuid.Value != request.Item || petition.OwnerId != owner.Guid.Low)
        {
            return false;
        }

        if (_context.Guilds.GetGuildOf(owner.Guid.Low) is not null)
        {
            owner.Send(WorldOpcode.SmsgTurnInPetitionResults, PetitionPackets.BuildTurnInResult(PetitionResult.AlreadyInGuild));
            return false;
        }

        if (!IsComplete(petition))
        {
            owner.Send(WorldOpcode.SmsgTurnInPetitionResults, PetitionPackets.BuildTurnInResult(PetitionResult.NeedMore));
            return false;
        }

        if (_context.Guilds.GetByName(petition.Name) is not null)
        {
            SendResult(owner, GuildCommand.Create, petition.Name, GuildCommandError.NameExistsS);
            return false;
        }

        return Complete(petition, owner.Guid.Low);
    }

    /// <summary>
    /// Install a petition bought or renamed on another worker (holder only): a known petition takes the new name, which is all a rename changes (the signatures stay
    /// the holder's); an unknown one is installed whole. Stored and published.
    /// </summary>
    private void Store(PetitionData data)
    {
        if (Get(data.Id) is { } existing)
        {
            existing.Name = data.Name;
            Persist(existing);
            return;
        }

        if (Install(data) is { } installed)
        {
            Persist(installed);
        }
    }

    private Petition? Install(PetitionData data)
    {
        if (_context.Characters.Find((uint)data.OwnerId) is not { } owner)
        {
            return null;
        }

        var petition = new Petition(data.Id, (uint)data.OwnerId, (uint)data.CharterItemId, data.Name, owner.Team);
        foreach (PetitionSignatureData signature in data.Signatures)
        {
            petition.Add(new PetitionSignature((uint)signature.PlayerId, signature.AccountId));
        }

        _petitions[petition.Id] = petition;
        return petition;
    }

    /// <summary>
    /// Install the holder's published state of a petition on a worker that is not the holder (<paramref name="data"/> null: gone). Ignored on the holder.
    /// World thread.
    /// </summary>
    public void ApplyReplica(int petitionId, PetitionData? data)
    {
        if (_context.Guilds.IsAuthority)
        {
            return;
        }

        _petitions.Remove(petitionId);
        if (data is not null)
        {
            Install(data);
        }
    }

    /// <summary>
    /// This worker became the holder at run time: what storage holds replaces the replica, as <see cref="GuildManager.ReloadFromStorage"/>, which runs first (a petition's owner must not be guilded). A save stores a whole
    /// petition, so a signature the previous holder stored but never published would otherwise be lost. The result is published to the other workers. World thread.
    /// </summary>
    public void ReloadFromStorage(IReadOnlyCollection<PetitionData> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        int[] known = [.. _petitions.Keys, .. stored.Select(p => p.Id)];
        _petitions.Clear();
        Load(stored);
        foreach (Petition petition in _petitions.Values)
        {
            Publish(petition.Id, petition.ToData());
        }

        foreach (int petitionId in known.Distinct().Where(id => !_petitions.ContainsKey(id)))
        {
            Publish(petitionId, null);
        }
    }

    /// <summary>The petition changed: the holder (or a single process) stores it and publishes it; any other worker sends the whole petition to the holder.</summary>
    private void Persist(Petition petition)
    {
        PetitionData data = petition.ToData();
        if (_context.Guilds.Link is { IsHolder: false } link)
        {
            link.Forward(new GuildRequest(GuildRequestKind.PetitionPut, ObjectGuid.Player(petition.OwnerId), Petition: data), null);
            return;
        }

        Persistence?.SavePetition(data);
        _context.Guilds.Link?.ReplicatePetition(petition.Id, data);
    }

    /// <summary>The holder publishes a petition change to the other workers; a single process and a replica publish nothing.</summary>
    private void Publish(int petitionId, PetitionData? data)
    {
        if (_context.Guilds.Link is { IsHolder: true } link)
        {
            link.ReplicatePetition(petitionId, data);
        }
    }

    // --- internals -----------------------------------------------------------------------------------

    private void DeleteOwnPetitionIfCharterGone(Player player)
    {
        if (GetByOwner(player.Guid.Low) is not { } petition)
        {
            return;
        }

        bool held = player.Inventory.AllItems.Any(i => i.Entry == PetitionConstants.CharterEntry
            && i.Guid.Low == petition.CharterItemId && i.EnchantmentId(PetitionConstants.EnchantmentSlot) == (uint)petition.Id);
        if (!held)
        {
            Delete(petition);
        }
    }

    private void Delete(Petition petition)
    {
        if (_context.Guilds.Link is { IsHolder: false } link)
        {
            if (!link.CanForward)
            {
                return; // the holder cannot be told: keep the petition so this replica and the holder do not diverge (the charter check runs again at the next login)
            }

            _petitions.Remove(petition.Id);
            link.Forward(new GuildRequest(GuildRequestKind.PetitionDelete, ObjectGuid.Player(petition.OwnerId), Id: petition.Id), null);
            return;
        }

        _petitions.Remove(petition.Id);
        Persistence?.DeletePetition(petition.Id);
        Publish(petition.Id, null);
    }

    /// <summary>The petition of a charter in the player's own bags, bound by item guid AND petition id.</summary>
    private Petition? PetitionOfHeldCharter(Player player, ObjectGuid itemGuid)
    {
        if (player.Inventory.GetItemByGuid(itemGuid) is not { } item || item.Entry != PetitionConstants.CharterEntry)
        {
            return null;
        }

        Petition? petition = Get((int)item.EnchantmentId(PetitionConstants.EnchantmentSlot));
        return petition is not null && petition.CharterGuid == item.Guid ? petition : null;
    }

    /// <summary>
    /// The guild-state checks shared by sign and offer: <paramref name="subject"/> must not be guilded
    /// (ALREADY_IN_GUILD_S) nor invited (ALREADY_INVITED_TO_GUILD_S). The error goes to <paramref name="recipient"/>
    /// and names the <paramref name="signer"/> (vmangos reports the offerer's name for an offer target,
    /// PetitionsHandler.cpp:352-362).
    /// </summary>
    private bool CheckGuildState(Player recipient, Player subject, Player signer)
    {
        if (_context.Guilds.GetGuildOf(subject) is not null)
        {
            SendResult(recipient, GuildCommand.Invite, signer.Name, GuildCommandError.AlreadyInGuildS);
            return false;
        }

        if (_context.Guilds.InvitedTo(subject) != 0)
        {
            SendResult(recipient, GuildCommand.Invite, signer.Name, GuildCommandError.AlreadyInvitedToGuildS);
            return false;
        }

        return true;
    }

    private static void SendResult(Player player, GuildCommand command, string text, GuildCommandError error)
        => player.Session.Send(WorldOpcode.SmsgGuildCommandResult, GuildPackets.BuildCommandResult(command, text, error));

    private static void SendResult(GuildActor player, GuildCommand command, string text, GuildCommandError error)
        => player.Send(WorldOpcode.SmsgGuildCommandResult, GuildPackets.BuildCommandResult(command, text, error));
}
