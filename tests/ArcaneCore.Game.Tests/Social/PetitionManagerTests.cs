using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// Guild charters (vmangos PetitionsHandler.cpp). One test per rule, citing the vmangos line. The
/// guild-state of the world is a real <see cref="GuildManager"/>; the NPC services, the inventory and
/// the item/charter binding are the real ones.
/// </summary>
public sealed class PetitionManagerTests
{
    private static (uint Command, string Text, uint Error) ReadResult(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return (reader.ReadUInt32(), reader.ReadCString(), reader.ReadUInt32());
    }

    private static (ulong Item, ulong Signer, uint Result) ReadSign(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return (reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt32());
    }

    private static (uint Command, string Text, uint Error) OnlyResult(PetitionKit k, Player p)
        => ReadResult(Assert.Single(k.Sent(p, WorldOpcode.SmsgGuildCommandResult)));

    private static void Quiet(PetitionKit k, Player p)
    {
        Assert.Empty(k.F.Session(p).Sent);
    }

    private static Guild FoundGuild(PetitionKit k, Player leader, string name)
    {
        Assert.Equal(GuildAdminResult.Ok, k.Guilds.Create(leader.Guid.Low, name, out Guild? guild));
        return guild!;
    }

    // --- show list ----------------------------------------------------------------------------------

    [Fact] // PetitionsHandler.cpp:481-507
    public void ShowList_SendsTheCharterEntry_AndOnlyFromAnInteractablePetitioner()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);

        k.Petitions.ShowList(a, k.Npc.Guid);

        byte[] payload = Assert.Single(k.Sent(a, WorldOpcode.SmsgPetitionShowlist));
        var reader = new PacketReader(payload);
        Assert.Equal(k.Npc.Guid.Value, reader.ReadUInt64());
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal([1u, 5863u, 16161u, 1000u, 1u], [reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()]);
        Assert.Equal(0, reader.Remaining);

        k.ClearAll();
        k.Npc = k.Npc with { X = 50 }; // out of range
        k.Petitions.ShowList(a, k.Npc.Guid);
        Quiet(k, a);
    }

    // --- buy ----------------------------------------------------------------------------------------

    [Fact] // PetitionsHandler.cpp:41-135
    public void Buy_Charges1000Copper_StoresTheCharterWithThePetitionId_AndCreatesThePetition()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1, money: 5000);

        Item charter = k.Buy(a);

        Assert.Equal(4000u, a.Money);
        Assert.Equal(1u, a.Inventory.GetItemCount(PetitionConstants.CharterEntry));
        Petition petition = k.Petitions.GetByOwner(1)!;
        Assert.Equal("Arcane Order", petition.Name);
        Assert.Equal(Team.Alliance, petition.Team);
        Assert.Equal(charter.Guid.Low, petition.CharterItemId);
        Assert.Equal((uint)petition.Id, charter.EnchantmentId(0)); // ITEM_FIELD_ENCHANTMENT (:126)
        Assert.Contains($"save:{petition.Id}:0", k.Persistence.PetitionWrites);
        Assert.Single(k.Sent(a, WorldOpcode.SmsgItemPushResult)); // SendNewItem(charter, 1, true, false) (:129)
        Assert.Empty(k.Sent(a, WorldOpcode.SmsgGuildCommandResult));
        Assert.True(k.Sink.CharacterChanges > 0);
    }

    [Fact] // :44-52 — the NPC must be a petitioner AND a tabard designer
    public void Buy_RequiresAPetitionerThatIsAlsoATabardDesigner()
    {
        using var petitionerOnly = new PetitionKit(NpcFlags.Petitioner);
        Player a = petitionerOnly.Add(1);
        petitionerOnly.Petitions.Buy(a, petitionerOnly.Npc.Guid, "Arcane Order");
        Assert.Equal(10_000u, a.Money);
        Assert.Empty(petitionerOnly.Persistence.PetitionWrites);

        using var designerOnly = new PetitionKit(NpcFlags.TabardDesigner);
        Player b = designerOnly.Add(1);
        designerOnly.Petitions.Buy(b, designerOnly.Npc.Guid, "Arcane Order");
        Assert.Equal(10_000u, b.Money);

        using var far = new PetitionKit();
        Player c = far.Add(1);
        far.Npc = far.Npc with { X = 40 };
        far.Petitions.Buy(c, far.Npc.Guid, "Arcane Order");
        Assert.Equal(10_000u, c.Money);
    }

    [Fact] // :66 guilded, :70-71 already owning a petition: silent, nothing charged
    public void Buy_IsSilentWhenGuildedOrAlreadyOwningAPetition()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        FoundGuild(k, b, "Existing");
        k.ClearAll();
        k.Petitions.Buy(b, k.Npc.Guid, "Another Order");
        Assert.Equal(10_000u, b.Money);
        Quiet(k, b);

        k.Buy(a);
        k.ClearAll();
        k.Petitions.Buy(a, k.Npc.Guid, "Second Order");
        Assert.Equal(9_000u, a.Money);
        Assert.Equal(1u, a.Inventory.GetItemCount(PetitionConstants.CharterEntry));
        Quiet(k, a);
    }

    [Fact] // :76-80
    public void Buy_WithATakenGuildName_AnswersNameExists_CaseInsensitively()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        FoundGuild(k, b, "Arcane Order");
        k.ClearAll();

        k.Petitions.Buy(a, k.Npc.Guid, "ARCANE order");

        Assert.Equal((0u, "ARCANE order", (uint)GuildCommandError.NameExistsS), OnlyResult(k, a));
        Assert.Equal(10_000u, a.Money);
    }

    [Theory] // :81-85
    [InlineData("Guild!")]
    [InlineData("A")]
    [InlineData("This name is much too long")]
    [InlineData("")]
    public void Buy_WithAnInvalidName_AnswersNameInvalid(string name)
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);

        k.Petitions.Buy(a, k.Npc.Guid, name);

        Assert.Equal((0u, name, (uint)GuildCommandError.NameInvalid), OnlyResult(k, a));
        Assert.Equal(10_000u, a.Money);
        Assert.Empty(k.Persistence.PetitionWrites);
    }

    [Fact] // :81-85 reserved names
    public void Buy_WithAReservedName_AnswersNameInvalid()
    {
        using var k = new PetitionKit();
        k.Petitions.Blacklist = new Reserved();
        Player a = k.Add(1);

        k.Petitions.Buy(a, k.Npc.Guid, "Gamemaster");

        Assert.Equal((0u, "Gamemaster", (uint)GuildCommandError.NameInvalid), OnlyResult(k, a));
    }

    [Fact] // :105-110
    public void Buy_WithTooLittleMoney_AnswersBuyFailedNotEnoughMoney()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1, money: 999);

        k.Petitions.Buy(a, k.Npc.Guid, "Arcane Order");

        var reader = new PacketReader(Assert.Single(k.Sent(a, WorldOpcode.SmsgBuyFailed)));
        Assert.Equal((k.Npc.Guid.Value, 5863u, (byte)2), (reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadByte()));
        Assert.Equal(999u, a.Money);
        Assert.Equal(0u, a.Inventory.GetItemCount(PetitionConstants.CharterEntry));
    }

    [Fact] // vmangos charges, then stores (:116-125); an uncharged buy (NPC state not Loaded yet) must hand out nothing
    public void Buy_WhenTheChargeCannotBeTaken_GivesNoCharterAndCreatesNoPetition()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1, loadNpcState: false);

        k.Petitions.Buy(a, k.Npc.Guid, "Arcane Order");

        Assert.Equal(10_000u, a.Money);
        Assert.Equal(0u, a.Inventory.GetItemCount(PetitionConstants.CharterEntry));
        Assert.Null(k.Petitions.GetByOwner(a.Guid.Low));
        Assert.Empty(k.Persistence.PetitionWrites);
    }

    [Fact] // :98-103
    public void Buy_WithoutACharterTemplate_AnswersBuyFailedCantFindItem()
    {
        using var k = new PetitionKit(withTemplates: false);
        Player a = k.Add(1);

        k.Petitions.Buy(a, k.Npc.Guid, "Arcane Order");

        var reader = new PacketReader(Assert.Single(k.Sent(a, WorldOpcode.SmsgBuyFailed)));
        Assert.Equal((0ul, 5863u, (byte)0), (reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadByte()));
        Assert.Equal(10_000u, a.Money);
    }

    [Fact] // :112-118
    public void Buy_WithFullBags_AnswersAnEquipError_AndChargesNothing()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        for (int i = 0; i < 16; i++)
        {
            ItemTestData.Give(a.Inventory, PetitionKit.Filler, 1);
        }

        k.ClearAll();
        k.Petitions.Buy(a, k.Npc.Guid, "Arcane Order");

        Assert.Single(k.Sent(a, WorldOpcode.SmsgInventoryChangeFailure));
        Assert.Equal(10_000u, a.Money);
        Assert.Empty(k.Persistence.PetitionWrites);
        Assert.Null(k.Petitions.GetByOwner(1));
    }

    [Fact] // petition ids come from a counter that starts above the stored ones (GeneratePetitionID)
    public void PetitionIds_StartAboveTheStoredOnes()
    {
        using var k = new PetitionKit();
        Player b = k.Add(2);
        k.Petitions.Load([new PetitionData(41, 2, 9, "Old", [])]);
        Player a = k.Add(1);

        k.Buy(a);

        Assert.Equal(42, k.Petitions.GetByOwner(1)!.Id);
    }

    // --- show signatures / query / rename -----------------------------------------------------------

    [Fact] // :137-169
    public void ShowSignatures_ListsTheSignersInOrder_ToTheHolder()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Player c = k.Add(3);
        (Petition petition, Item charter) = k.Signed(a, b, c);
        k.ClearAll();

        k.Petitions.ShowSignatures(a, charter.Guid);

        var reader = new PacketReader(Assert.Single(k.Sent(a, WorldOpcode.SmsgPetitionShowSignatures)));
        Assert.Equal(charter.Guid.Value, reader.ReadUInt64());
        Assert.Equal(a.Guid.Value, reader.ReadUInt64());
        Assert.Equal((uint)petition.Id, reader.ReadUInt32());
        Assert.Equal(2, reader.ReadByte());
        Assert.Equal((b.Guid.Value, 0u, c.Guid.Value, 0u), (reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadUInt32()));
        Assert.Equal(0, reader.Remaining);
    }

    [Fact] // :141-142 guilded players get nothing; :144-146 an item that is not in the bags either
    public void ShowSignatures_IsSilentWhenGuildedOrTheItemIsNotHeld()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Item charter = k.Buy(a);
        k.ClearAll();

        k.Petitions.ShowSignatures(b, charter.Guid); // b does not hold it
        Quiet(k, b);

        FoundGuild(k, a, "Founded");
        k.Petitions.ShowSignatures(a, charter.Guid);
        Assert.Empty(k.Sent(a, WorldOpcode.SmsgPetitionShowSignatures));
    }

    [Fact] // :171-185 and wow_messages smsg_petition_query_response (1.12)
    public void Query_AnswersTheFixedFields_ByPetitionId_ForAnyone()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        k.Buy(a, "Arcane Order");
        int id = k.Petitions.GetByOwner(1)!.Id;
        k.ClearAll();

        k.Petitions.Query(b, (uint)id);

        var r = new PacketReader(Assert.Single(k.Sent(b, WorldOpcode.SmsgPetitionQueryResponse)));
        Assert.Equal((uint)id, r.ReadUInt32());
        Assert.Equal(a.Guid.Value, r.ReadUInt64());
        Assert.Equal("Arcane Order", r.ReadCString());
        Assert.Equal(string.Empty, r.ReadCString());
        Assert.Equal([1u, 9u, 9u], [r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()]);   // flags, min, max (hard-coded 9)
        Assert.Equal([0u, 0u, 0u, 0u, 0u], [r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()]);
        Assert.Equal(0, r.ReadUInt16());
        Assert.Equal([0u, 0u, 0u, 0u], [r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()]);
        Assert.Equal(0, r.Remaining);

        k.ClearAll();
        k.Petitions.Query(b, 9999);
        Quiet(k, b);
    }

    [Fact] // :187-216
    public void Rename_ChecksTheNameFirst_ThenEchoesTheNewName()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        FoundGuild(k, b, "Taken Name");
        Item charter = k.Buy(a);
        k.ClearAll();

        k.Petitions.Rename(a, charter.Guid, "Taken name");
        Assert.Equal((0u, "Taken name", (uint)GuildCommandError.NameExistsS), OnlyResult(k, a));
        k.ClearAll();
        k.Petitions.Rename(a, charter.Guid, "Bad!");
        Assert.Equal((0u, "Bad!", (uint)GuildCommandError.NameInvalid), OnlyResult(k, a));
        k.ClearAll();

        k.Petitions.Rename(a, charter.Guid, "Fresh Name");

        var reader = new PacketReader(Assert.Single(k.Sent(a, WorldOpcode.MsgPetitionRename)));
        Assert.Equal((charter.Guid.Value, "Fresh Name"), (reader.ReadUInt64(), reader.ReadCString()));
        Assert.Equal("Fresh Name", k.Petitions.GetByOwner(1)!.Name);
        Assert.Equal("Fresh Name", k.Persistence.Petitions[k.Petitions.GetByOwner(1)!.Id].Name);
    }

    // --- sign ---------------------------------------------------------------------------------------

    [Fact] // :276-286 ok: signer and an online owner get SIGN_RESULTS OK
    public void Sign_AddsTheSignature_AndTellsTheSignerAndTheOnlineOwner()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Item charter = k.Buy(a);
        k.ClearAll();

        k.Petitions.Sign(b, charter.Guid);

        Assert.Equal((charter.Guid.Value, b.Guid.Value, 0u), ReadSign(Assert.Single(k.Sent(b, WorldOpcode.SmsgPetitionSignResults))));
        Assert.Equal((charter.Guid.Value, b.Guid.Value, 0u), ReadSign(Assert.Single(k.Sent(a, WorldOpcode.SmsgPetitionSignResults))));
        Assert.Equal([new PetitionSignature(2, 2)], k.Petitions.GetByOwner(1)!.Signatures);
        Assert.Equal("save:" + k.Petitions.GetByOwner(1)!.Id + ":1", k.Persistence.PetitionWrites[^1]);
    }

    [Fact] // :232-240
    public void Sign_ByTheOwner_AnswersCantSignOwn()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Item charter = k.Buy(a);
        k.ClearAll();

        k.Petitions.Sign(a, charter.Guid);

        Assert.Equal((charter.Guid.Value, a.Guid.Value, 3u), ReadSign(Assert.Single(k.Sent(a, WorldOpcode.SmsgPetitionSignResults))));
        Assert.Empty(k.Petitions.GetByOwner(1)!.Signatures);
    }

    [Fact] // :249-254
    public void Sign_ByTheEnemyFaction_AnswersNotAllied_UnlessTwoSideGuildsAreAllowed()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player orc = k.Add(2, Race.Orc);
        Item charter = k.Buy(a);
        k.ClearAll();

        k.Petitions.Sign(orc, charter.Guid);

        Assert.Equal((0u, string.Empty, (uint)GuildCommandError.NotAllied), OnlyResult(k, orc));
        Assert.Empty(k.Petitions.GetByOwner(1)!.Signatures);

        k.F.Context.Options.AllowTwoSideGuild = true;
        k.Petitions.Sign(orc, charter.Guid);
        Assert.Single(k.Petitions.GetByOwner(1)!.Signatures);
    }

    [Fact] // :256-260 — the error carries the SIGNER's name
    public void Sign_WhileGuilded_AnswersAlreadyInGuildS()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Item charter = k.Buy(a);
        FoundGuild(k, b, "Other Guild");
        k.ClearAll();

        k.Petitions.Sign(b, charter.Guid);

        Assert.Equal((1u, "P2", (uint)GuildCommandError.AlreadyInGuildS), OnlyResult(k, b));
        Assert.Empty(k.Petitions.GetByOwner(1)!.Signatures);
    }

    [Fact] // :261-265
    public void Sign_WithAPendingGuildInvite_AnswersAlreadyInvitedToGuildS()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Player leader = k.Add(3);
        Item charter = k.Buy(a);
        FoundGuild(k, leader, "Other Guild");
        k.Guilds.Invite(leader, b.Name);
        k.ClearAll();

        k.Petitions.Sign(b, charter.Guid);

        Assert.Equal((1u, "P2", (uint)GuildCommandError.AlreadyInvitedToGuildS), OnlyResult(k, b));
    }

    [Fact] // :275-290 the same character signing twice
    public void Sign_Twice_AnswersAlreadySigned_AndTellsTheOwnerWithTheOddInviteError()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Item charter = k.Buy(a);
        k.Petitions.Sign(b, charter.Guid);
        k.ClearAll();

        k.Petitions.Sign(b, charter.Guid);

        Assert.Equal((charter.Guid.Value, b.Guid.Value, 1u), ReadSign(Assert.Single(k.Sent(b, WorldOpcode.SmsgPetitionSignResults))));
        // vmangos: "Unsure if this is the correct message" (:281-286) — COMMAND_RESULT(INVITE, signer, ALREADY_INVITED_TO_GUILD_S).
        Assert.Equal((1u, "P2", (uint)GuildCommandError.AlreadyInvitedToGuildS), OnlyResult(k, a));
        Assert.Empty(k.Sent(a, WorldOpcode.SmsgPetitionSignResults));
        Assert.Single(k.Petitions.GetByOwner(1)!.Signatures);
    }

    [Fact] // :273-274 / GuildMgr.cpp:513-540 another character of an account that already signed
    public void Sign_ByAnotherCharacterOfTheSameAccount_AnswersAlreadySigned()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2, accountId: 50);
        Player twin = k.Add(3, accountId: 50);
        Item charter = k.Buy(a);
        k.Petitions.Sign(b, charter.Guid);
        k.ClearAll();

        k.Petitions.Sign(twin, charter.Guid);

        Assert.Equal((charter.Guid.Value, twin.Guid.Value, 1u), ReadSign(Assert.Single(k.Sent(twin, WorldOpcode.SmsgPetitionSignResults))));
        Assert.Single(k.Petitions.GetByOwner(1)!.Signatures);
    }

    [Fact] // :229-230 a complete petition is ignored; the client hard limit is 9 (:270-271)
    public void Sign_OnACompletePetition_IsIgnored()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player[] signers = [.. Enumerable.Range(2, 10).Select(i => k.Add((uint)i))];
        Item charter = k.Buy(a);
        foreach (Player signer in signers.Take(9))
        {
            k.Petitions.Sign(signer, charter.Guid);
        }

        k.ClearAll();
        k.Petitions.Sign(signers[9], charter.Guid);

        Assert.Equal(9, k.Petitions.GetByOwner(1)!.Signatures.Count);
        Quiet(k, signers[9]);
    }

    [Fact] // MinPetitionSigns (World:Guild) — GuildMgr.h:124 completes at exactly the configured count
    public void MinPetitionSigns_CapsTheSignaturesAtTheConfiguredCount()
    {
        using var k = new PetitionKit(options: new GuildOptions { MinPetitionSigns = 2 });
        Player a = k.Add(1);
        Player b = k.Add(2);
        Player c = k.Add(3);
        Player d = k.Add(4);
        (Petition petition, Item charter) = k.Signed(a, b, c, d);

        Assert.Equal(2, petition.Signatures.Count);
        Assert.True(k.Petitions.IsComplete(petition));
        Assert.Equal(charter.Guid.Low, petition.CharterItemId);
    }

    [Fact] // PetitionsHandler.cpp:223-226: signing needs only the charter's guid, held by the OWNER
    public void Sign_WithAnUnknownCharterGuid_IsIgnored()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        k.Buy(a);
        k.ClearAll();

        k.Petitions.Sign(b, ObjectGuid.Item(424242));

        Quiet(k, b);
    }

    // --- decline / offer ----------------------------------------------------------------------------

    [Fact] // :321-338
    public void Decline_TellsAnOnlineOwnerWhoDeclined()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Item charter = k.Buy(a);
        k.ClearAll();

        k.Petitions.Decline(b, charter.Guid);

        var reader = new PacketReader(Assert.Single(k.Sent(a, WorldOpcode.MsgPetitionDecline)));
        Assert.Equal(b.Guid.Value, reader.ReadUInt64());
        Assert.Equal(0, reader.Remaining);
        Quiet(k, b);
    }

    [Fact] // :340-398
    public void Offer_SendsTheSignatureList_WithTheOfferersGuidAsOwner()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Player c = k.Add(3);
        Item charter = k.Buy(a);
        k.Petitions.Sign(b, charter.Guid);
        k.ClearAll();

        k.Petitions.Offer(a, charter.Guid, c.Guid);

        var reader = new PacketReader(Assert.Single(k.Sent(c, WorldOpcode.SmsgPetitionShowSignatures)));
        Assert.Equal(charter.Guid.Value, reader.ReadUInt64());
        Assert.Equal(a.Guid.Value, reader.ReadUInt64());
        Assert.Equal((uint)k.Petitions.GetByOwner(1)!.Id, reader.ReadUInt32());
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal((b.Guid.Value, 0u), (reader.ReadUInt64(), reader.ReadUInt32()));
    }

    [Fact] // :344-346 target must be online; :348-353 NOT_ALLIED
    public void Offer_ToAnOfflineOrEnemyTarget_IsRefused()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player orc = k.Add(2, Race.Orc);
        Item charter = k.Buy(a);
        k.ClearAll();

        k.Petitions.Offer(a, charter.Guid, ObjectGuid.Player(77));
        Quiet(k, a);

        k.Petitions.Offer(a, charter.Guid, orc.Guid);
        Assert.Equal((0u, string.Empty, (uint)GuildCommandError.NotAllied), OnlyResult(k, a));
        Quiet(k, orc);
    }

    [Fact] // :355-362 — the OFFERER receives the error and it names the OFFERER
    public void Offer_ToAGuildedOrInvitedTarget_ReportsTheErrorToTheOffererWithTheOfferersName()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player guilded = k.Add(2);
        Player invited = k.Add(3);
        Player leader = k.Add(4);
        Item charter = k.Buy(a);
        FoundGuild(k, guilded, "Guilded");
        FoundGuild(k, leader, "Leader Guild");
        k.Guilds.Invite(leader, invited.Name);
        k.ClearAll();

        k.Petitions.Offer(a, charter.Guid, guilded.Guid);
        Assert.Equal((1u, "P1", (uint)GuildCommandError.AlreadyInGuildS), OnlyResult(k, a));
        k.ClearAll();

        k.Petitions.Offer(a, charter.Guid, invited.Guid);
        Assert.Equal((1u, "P1", (uint)GuildCommandError.AlreadyInvitedToGuildS), OnlyResult(k, a));
        Assert.Empty(k.Sent(guilded, WorldOpcode.SmsgPetitionShowSignatures));
        Assert.Empty(k.Sent(invited, WorldOpcode.SmsgPetitionShowSignatures));
    }

    // --- turn in ------------------------------------------------------------------------------------

    private static (Player Owner, Player[] Signers) Nine(PetitionKit k)
    {
        Player owner = k.Add(1);
        Player[] signers = [.. Enumerable.Range(2, 9).Select(i => k.Add((uint)i))];
        return (owner, signers);
    }

    [Fact] // :435-440
    public void TurnIn_WithEightSignatures_AnswersNeedMore()
    {
        using var k = new PetitionKit();
        (Player owner, Player[] signers) = Nine(k);
        (_, Item charter) = k.Signed(owner, signers.Take(8).ToArray());
        k.ClearAll();

        k.Petitions.TurnIn(owner, charter.Guid);

        var reader = new PacketReader(Assert.Single(k.Sent(owner, WorldOpcode.SmsgTurnInPetitionResults)));
        Assert.Equal(4u, reader.ReadUInt32());
        Assert.Empty(k.Guilds.All);
        Assert.Equal(1u, owner.Inventory.GetItemCount(PetitionConstants.CharterEntry));
    }

    [Fact] // :400-475 and Guild.cpp:104-119
    public void TurnIn_WithNineSignatures_FoundsTheGuild_WithoutJoinEvents_AndDestroysTheCharter()
    {
        using var k = new PetitionKit();
        (Player owner, Player[] signers) = Nine(k);
        (Petition petition, Item charter) = k.Signed(owner, signers);
        k.ClearAll();

        k.Petitions.TurnIn(owner, charter.Guid);

        Guild guild = Assert.Single(k.Guilds.All);
        Assert.Equal("Arcane Order", guild.Name);
        Assert.Equal(10, guild.MemberCount);
        Assert.Equal(owner.Guid.Low, guild.LeaderId);
        Assert.Equal(Guild.GuildMasterRank, guild.Find(1)!.Rank);
        Assert.All(signers, s => Assert.Equal(guild.LowestRank, guild.Find(s.Guid.Low)!.Rank));
        Assert.Equal(["Guild Master", "Officer", "Veteran", "Member", "Initiate"], guild.Ranks.Select(r => r.Name));
        Assert.Equal((uint)guild.Id, owner.GetUInt32(UpdateFields.PlayerGuildid));
        Assert.Equal((uint)guild.Id, signers[0].GetUInt32(UpdateFields.PlayerGuildid));
        Assert.Equal(0u, owner.Inventory.GetItemCount(PetitionConstants.CharterEntry));
        Assert.Null(k.Petitions.Get(petition.Id));

        var reader = new PacketReader(Assert.Single(k.Sent(owner, WorldOpcode.SmsgTurnInPetitionResults)));
        Assert.Equal(0u, reader.ReadUInt32());
        foreach (Player p in new[] { owner }.Concat(signers))
        {
            Assert.Empty(k.Sent(p, WorldOpcode.SmsgGuildEvent)); // AddMember sends no GE_JOINED
        }

        // One transaction: the guild and the petition's deletion are a single write.
        Assert.Equal($"complete:{guild.Id}:{petition.Id}", k.Persistence.PetitionWrites[^1]);
        GuildData stored = Assert.Single(k.Persistence.Completed);
        Assert.Equal(10, stored.Members.Count);
        Assert.DoesNotContain(k.Persistence.PetitionWrites, w => w.StartsWith("delete:", StringComparison.Ordinal));
        Assert.Empty(k.Persistence.Petitions);
    }

    [Fact] // :414-416
    public void TurnIn_ByAnotherHolder_IsSilent()
    {
        using var k = new PetitionKit();
        (Player owner, Player[] signers) = Nine(k);
        (_, Item charter) = k.Signed(owner, signers);
        Player other = k.Add(20);
        // The owner hands the charter on (a GM can); the new holder is not the owner.
        owner.Inventory.RemoveItem(charter.BagSlot, charter.Slot);
        other.Inventory.StoreItem([new ItemPosCount(InventorySlots.Bag0, InventorySlots.ItemStart, 1)], charter);
        k.ClearAll();

        k.Petitions.TurnIn(other, charter.Guid);

        Quiet(k, other);
        Assert.Empty(k.Guilds.All);
    }

    [Fact] // :421-427 (ALREADY_IN_GUILD itself is only reachable with a stale petition: the join removes the owner's petition first)
    public void TurnIn_AfterTheOwnerJoinedAGuild_IsSilent_BecauseThePetitionWentWithTheJoin()
    {
        using var k = new PetitionKit();
        (Player owner, Player[] signers) = Nine(k);
        (_, Item charter) = k.Signed(owner, signers);
        Player leader = k.Add(30);
        FoundGuild(k, leader, "Leader Guild");
        k.Guilds.Invite(leader, owner.Name);
        k.Guilds.Accept(owner); // joining removes the owner's petition (Guild::AddMember → RemovePetitionsAndSigns)
        k.ClearAll();

        k.Petitions.TurnIn(owner, charter.Guid);

        // The petition is gone with the join, so the lookup fails first and nothing is answered (vmangos order).
        Quiet(k, owner);
    }

    [Fact] // :443-448
    public void TurnIn_WithAGuildNameTakenMeanwhile_AnswersNameExists()
    {
        using var k = new PetitionKit();
        (Player owner, Player[] signers) = Nine(k);
        (_, Item charter) = k.Signed(owner, signers);
        Player rival = k.Add(30);
        FoundGuild(k, rival, "ARCANE ORDER");
        k.ClearAll();

        k.Petitions.TurnIn(owner, charter.Guid);

        Assert.Equal((0u, "Arcane Order", (uint)GuildCommandError.NameExistsS), OnlyResult(k, owner));
        Assert.Equal(1u, owner.Inventory.GetItemCount(PetitionConstants.CharterEntry));
    }

    [Fact] // Guild.cpp:104-119, :202-204 — a signer who belongs to another guild is skipped
    public void TurnIn_SkipsASignerWhoIsInAnotherGuild_AndStillFoundsTheGuild()
    {
        using var k = new PetitionKit();
        (Player owner, Player[] signers) = Nine(k);
        (Petition petition, Item charter) = k.Signed(owner, signers.Take(8).ToArray());
        Player stolen = signers[8];
        FoundGuild(k, stolen, "Rival");
        // A signature that survived a guild join (a stored petition loaded before the guild existed):
        // the join cleanup cannot have removed it, so turn-in must skip the member itself.
        petition.Add(new PetitionSignature(stolen.Guid.Low, stolen.Session.AccountId));
        Assert.True(k.Petitions.IsComplete(petition));
        k.ClearAll();

        k.Petitions.TurnIn(owner, charter.Guid);

        Guild founded = k.Guilds.GetByName("Arcane Order")!;
        Assert.Equal(9, founded.MemberCount);
        Assert.Null(founded.Find(stolen.Guid.Low));
        Assert.Equal("Rival", k.Guilds.GetGuildOf(stolen)!.Name);
        Assert.Equal(0u, owner.Inventory.GetItemCount(PetitionConstants.CharterEntry));
    }

    // --- joining a guild strips petitions and signatures --------------------------------------------

    [Fact] // Guild.cpp:213 → Player.cpp:17796-17806
    public void JoiningAGuild_RemovesTheJoinersSignaturesOnOtherPetitions_AndItsOwnPetition()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Player joiner = k.Add(3);
        Player leader = k.Add(4);
        Item aCharter = k.Buy(a, "Alpha Order");
        Item bCharter = k.Buy(b, "Beta Order");
        k.Petitions.Sign(joiner, aCharter.Guid);
        k.Petitions.Sign(joiner, bCharter.Guid);
        Item joinerCharter = k.Buy(joiner, "Joiner Order");
        FoundGuild(k, leader, "Leader Guild");
        k.Persistence.PetitionWrites.Clear();

        k.Guilds.Invite(leader, joiner.Name);
        k.Guilds.Accept(joiner);

        Assert.Empty(k.Petitions.GetByOwner(1)!.Signatures);
        Assert.Empty(k.Petitions.GetByOwner(2)!.Signatures);
        Assert.Null(k.Petitions.GetByOwner(3)); // memory...
        int ownedId = k.Petitions.All.Count; // (a and b remain)
        Assert.Equal(2, ownedId);
        Assert.Contains(k.Persistence.PetitionWrites, w => w.StartsWith("delete:", StringComparison.Ordinal)); // ...and persistence
        Assert.Equal(2, k.Persistence.PetitionWrites.Count(w => w.EndsWith(":0", StringComparison.Ordinal) && w.StartsWith("save:", StringComparison.Ordinal)));
        Assert.NotNull(joinerCharter); // the orphaned charter item stays in the bag, as in vmangos
    }

    // --- charter lifecycle --------------------------------------------------------------------------

    [Fact] // Player.cpp:10656-10661 / Item.cpp:514-516
    public void DestroyingTheCharter_DeletesThePetition()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Item charter = k.Buy(a);
        k.Petitions.OnPlayerLoggedIn(a);
        int id = k.Petitions.GetByOwner(1)!.Id;

        a.Inventory.DestroyItem(charter.BagSlot, charter.Slot);

        Assert.Null(k.Petitions.GetByOwner(1));
        Assert.Equal($"delete:{id}", k.Persistence.PetitionWrites[^1]);

        // A new charter can be bought now (PetitionsHandler.cpp:70-71 no longer blocks).
        k.Buy(a, "Second Order");
        Assert.NotNull(k.Petitions.GetByOwner(1));
    }

    [Fact] // the owner's logout stops the watch
    public void AfterLogout_TheInventoryIsNoLongerWatched()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Item charter = k.Buy(a);
        k.Petitions.OnPlayerLoggedIn(a);
        k.Petitions.OnPlayerLoggingOut(a);

        a.Inventory.DestroyItem(charter.BagSlot, charter.Slot);

        Assert.NotNull(k.Petitions.GetByOwner(1));
    }

    [Fact] // login self-heal: an owner without a bound charter loses the petition
    public void Login_RemovesAPetitionWhoseCharterIsGone_AndKeepsOneWhoseCharterIsHeld()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Item aCharter = k.Buy(a);
        k.Buy(b);
        b.Inventory.DestroyItem(b.Inventory.AllItems.Single(i => i.Entry == PetitionConstants.CharterEntry).BagSlot,
            b.Inventory.AllItems.Single(i => i.Entry == PetitionConstants.CharterEntry).Slot); // before the watch starts

        k.Petitions.OnPlayerLoggedIn(a);
        k.Petitions.OnPlayerLoggedIn(b);

        Assert.NotNull(k.Petitions.GetByOwner(1));
        Assert.Null(k.Petitions.GetByOwner(2));
        Assert.Equal(aCharter.Guid.Low, k.Petitions.GetByOwner(1)!.CharterItemId);
    }

    [Fact] // design: ids can be reused, so the charter item's guid binds the petition too
    public void ACharterWhoseGuidIsNotThePetitionsCharter_IsRejectedEvenWithTheRightId()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Item aCharter = k.Buy(a);
        int id = k.Petitions.GetByOwner(1)!.Id;

        // b holds another charter item whose enchantment slot 0 names a's petition (a stale or forged id).
        Item forged = ItemTestData.Give(b.Inventory, PetitionConstants.CharterEntry, 1);
        forged.SetUInt32(UpdateFields.ItemFieldEnchantment, (uint)id);
        k.ClearAll();

        k.Petitions.ShowSignatures(b, forged.Guid);
        k.Petitions.Offer(b, forged.Guid, a.Guid);
        k.Petitions.TurnIn(b, forged.Guid);
        k.Petitions.Rename(b, forged.Guid, "Hijacked");

        Quiet(k, b);
        Quiet(k, a);
        Assert.Equal("Arcane Order", k.Petitions.GetByOwner(1)!.Name);
        Assert.NotNull(aCharter);
    }

    [Fact] // critique: the petition id survives the item's save and load
    public void TheCharterItem_RoundTripsItsPetitionId_ThroughItsPersistentState()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Item charter = k.Buy(a);
        int id = k.Petitions.GetByOwner(1)!.Id;

        ItemInstanceData data = charter.ToData();

        Assert.Equal((uint)id, data.Enchantments[0]);
        Assert.Equal(PetitionConstants.CharterEntry, data.Entry);
    }

    // --- load and character deletion ----------------------------------------------------------------

    [Fact] // GuildMgr.cpp:222-236 plus the join rule
    public void Load_DropsPetitionsOfUnknownOrGuildedOwners_AndSignaturesOfUnknownCharacters()
    {
        using var k = new PetitionKit();
        Player owner = k.Add(1);
        Player signer = k.Add(2);
        Player guilded = k.Add(3);
        FoundGuild(k, guilded, "Founded");
        k.Persistence.PetitionWrites.Clear();
        var fresh = new PetitionManager(k.F.Context); // a second manager on the same context: a clean load

        fresh.Load(
        [
            new PetitionData(1, 1, 100, "Good", [new PetitionSignatureData(2, 2), new PetitionSignatureData(99, 99), new PetitionSignatureData(2, 2)]),
            new PetitionData(2, 77, 101, "Unknown owner", []),
            new PetitionData(3, 3, 102, "Guilded owner", []),
        ]);

        Petition good = Assert.Single(fresh.All);
        Assert.Equal(1, good.Id);
        Assert.Equal([new PetitionSignature(2, 2)], good.Signatures);
        Assert.Contains("delete:2", k.Persistence.PetitionWrites);
        Assert.Contains("delete:3", k.Persistence.PetitionWrites);
        Assert.Contains("save:1:1", k.Persistence.PetitionWrites);
        Assert.NotNull(owner);
        Assert.NotNull(signer);
    }

    [Fact] // Player.cpp:4353-4354
    public void OnCharacterDeleted_DropsThePetitionAndTheSignatures_WithoutWritingRows()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        Player c = k.Add(3);
        Item aCharter = k.Buy(a);
        k.Buy(c, "Third Order");
        k.Petitions.Sign(b, aCharter.Guid);
        k.Petitions.Sign(c, aCharter.Guid);
        k.Persistence.PetitionWrites.Clear();

        k.Petitions.OnCharacterDeleted(3); // owns a petition and signed another
        Assert.Null(k.Petitions.GetByOwner(3));
        Assert.Equal([new PetitionSignature(2, 2)], k.Petitions.GetByOwner(1)!.Signatures);

        k.Petitions.OnCharacterDeleted(1);
        Assert.Empty(k.Petitions.All);
        Assert.Empty(k.Persistence.PetitionWrites); // the deletion cleanup and the purge own the rows
    }

    private sealed class Reserved : ICharterNameBlacklist
    {
        public bool IsReserved(string lowercaseName) => lowercaseName == "gamemaster";
    }
}
