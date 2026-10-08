using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Social;
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
    /// guilds are loaded.
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
                Persistence?.DeletePetition(data.Id);
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
            if (changed)
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
        if (!Npc.TryCharge(player, PetitionConstants.CharterCost))
        {
            return;
        }

        int id = _nextId++;
        Item charter = player.Inventory.StoreNewItem(destination, template, template.BuyCount);
        charter.SetUInt32(UpdateFields.ItemFieldEnchantment + (PetitionConstants.EnchantmentSlot * 3), (uint)id);
        player.Session.Send(WorldOpcode.SmsgItemPushResult, ItemPackets.ItemPushResult(player.Guid, charter, 1, received: true, created: false, showInChat: true));

        var petition = new Petition(id, player.Guid.Low, charter.Guid.Low, name, player.Team);
        _petitions[id] = petition;
        Persistence?.SavePetition(petition.ToData());
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
        Persistence?.SavePetition(petition.ToData());
        player.Session.Send(WorldOpcode.MsgPetitionRename, PetitionPackets.BuildRenameResult(itemGuid, newName));
    }

    /// <summary>
    /// CMSG_PETITION_SIGN (vmangos HandlePetitionSignOpcode, PetitionsHandler.cpp:218-319), in its order. The
    /// error codes follow vmangos even where they look odd (a duplicate signature tells the owner
    /// ALREADY_INVITED_TO_GUILD_S, :281-286) so the client sees what it sees on vmangos.
    /// </summary>
    public void Sign(Player player, ObjectGuid itemGuid)
    {
        if (GetByCharter(itemGuid) is not { } petition || IsComplete(petition))
        {
            return;
        }

        if (petition.OwnerId == player.Guid.Low)
        {
            player.Session.Send(WorldOpcode.SmsgPetitionSignResults, PetitionPackets.BuildSignResults(itemGuid, player.Guid, PetitionResult.CantSignOwn));
            return;
        }

        if (!_context.Options.AllowTwoSideGuild && player.Team != petition.Team)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.NotAllied);
            return;
        }

        if (!CheckGuildState(player, player, signer: player))
        {
            return;
        }

        if (petition.Signatures.Count >= PetitionConstants.ClientMaxSignatures)
        {
            return;
        }

        int account = player.Session.AccountId;
        if (petition.ForAccount(account) is not null || petition.ForPlayer(player.Guid.Low) is not null)
        {
            player.Session.Send(WorldOpcode.SmsgPetitionSignResults, PetitionPackets.BuildSignResults(itemGuid, player.Guid, PetitionResult.AlreadySigned));
            if (_context.World.FindOnlinePlayer(petition.OwnerGuid) is { } duplicateOwner)
            {
                SendResult(duplicateOwner, GuildCommand.Invite, player.Name, GuildCommandError.AlreadyInvitedToGuildS);
            }

            return;
        }

        petition.Add(new PetitionSignature(player.Guid.Low, account));
        Persistence?.SavePetition(petition.ToData());

        byte[] ok = PetitionPackets.BuildSignResults(itemGuid, player.Guid, PetitionResult.Ok);
        player.Session.Send(WorldOpcode.SmsgPetitionSignResults, ok);
        _context.World.FindOnlinePlayer(petition.OwnerGuid)?.Session.Send(WorldOpcode.SmsgPetitionSignResults, ok);
    }

    /// <summary>MSG_PETITION_DECLINE (vmangos HandlePetitionDeclineOpcode, PetitionsHandler.cpp:321-338): an online owner is told who declined.</summary>
    public void Decline(Player player, ObjectGuid itemGuid)
    {
        if (GetByCharter(itemGuid) is not { } petition)
        {
            return;
        }

        _context.World.FindOnlinePlayer(petition.OwnerGuid)?.Session.Send(WorldOpcode.MsgPetitionDecline, PetitionPackets.BuildDeclineResult(player.Guid));
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

        if (_context.Guilds.CreateFromPetition(player.Guid.Low, petition.Name, petition.Signatures.Select(s => s.PlayerId), petition.Id) is not { } guild)
        {
            return;
        }

        _petitions.Remove(petition.Id);
        Persistence?.CompletePetition(guild.ToData(), petition.Id);

        // "Deleting the charter deletes the petition if it still exists, so we do not want to do it before
        // the guild is created" (PetitionsHandler.cpp:462-464): the petition is already gone here.
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
            }
            else
            {
                petition.RemoveSigner(characterId);
            }
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
                Persistence?.SavePetition(petition.ToData());
            }
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
        _petitions.Remove(petition.Id);
        Persistence?.DeletePetition(petition.Id);
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
}
