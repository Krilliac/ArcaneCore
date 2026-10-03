using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Social;

namespace ArcaneCore.Game.Guilds;

/// <summary>SMSG_PETITION_SIGN_RESULTS / SMSG_TURN_IN_PETITION_RESULTS result for 1.12 (vmangos Guild.h:146-153; gtker petitionresult).</summary>
public enum PetitionResult : uint
{
    Ok = 0,
    AlreadySigned = 1,
    AlreadyInGuild = 2,
    CantSignOwn = 3,
    NeedMore = 4,
    NotServer = 5,
}

/// <summary>The fixed charter facts of vmangos PetitionsHandler.cpp:36-39 (hard-coded there, not data).</summary>
public static class PetitionConstants
{
    /// <summary>GUILD_CHARTER: the charter item entry (also vmangos Item::IsCharter).</summary>
    public const uint CharterEntry = 5863;

    /// <summary>GUILD_CHARTER_COST: 1000 copper (10 silver).</summary>
    public const uint CharterCost = 1000;

    /// <summary>CHARTER_DISPLAY_ID.</summary>
    public const uint CharterDisplayId = 16161;

    /// <summary>The client's hard limit of signatures on one charter (PetitionsHandler.cpp:270).</summary>
    public const int ClientMaxSignatures = 9;

    /// <summary>Enchantment slot 0 of the charter item holds the petition id (ITEM_FIELD_ENCHANTMENT, PetitionsHandler.cpp:126).</summary>
    public const int EnchantmentSlot = 0;
}

/// <summary>One signature (vmangos PetitionSignature: signer guid and account).</summary>
public readonly record struct PetitionSignature(uint PlayerId, int AccountId)
{
    public ObjectGuid Guid => ObjectGuid.Player(PlayerId);
}

/// <summary>
/// A guild charter petition (vmangos Petition, GuildMgr.h:96-125): its id, owner, charter item,
/// proposed guild name and signatures in the order they were given. World thread.
/// </summary>
public sealed class Petition
{
    private readonly List<PetitionSignature> _signatures = [];

    internal Petition(int id, uint ownerId, uint charterItemId, string name, Team team)
    {
        Id = id;
        OwnerId = ownerId;
        CharterItemId = charterItemId;
        Name = name;
        Team = team;
    }

    public int Id { get; }

    public uint OwnerId { get; }

    public ObjectGuid OwnerGuid => ObjectGuid.Player(OwnerId);

    /// <summary>The low part of the charter item's guid.</summary>
    public uint CharterItemId { get; }

    public ObjectGuid CharterGuid => ObjectGuid.Item(CharterItemId);

    public string Name { get; internal set; }

    /// <summary>The owner's team (vmangos Petition::LoadFromDB / CreatePetition).</summary>
    public Team Team { get; }

    public IReadOnlyList<PetitionSignature> Signatures => _signatures;

    /// <summary>vmangos Petition::GetSignatureForPlayerGuid.</summary>
    public PetitionSignature? ForPlayer(uint playerId)
    {
        foreach (PetitionSignature signature in _signatures)
        {
            if (signature.PlayerId == playerId)
            {
                return signature;
            }
        }

        return null;
    }

    /// <summary>vmangos Petition::GetSignatureForAccount.</summary>
    public PetitionSignature? ForAccount(int accountId)
    {
        foreach (PetitionSignature signature in _signatures)
        {
            if (signature.AccountId == accountId)
            {
                return signature;
            }
        }

        return null;
    }

    internal void Add(PetitionSignature signature) => _signatures.Add(signature);

    internal bool RemoveSigner(uint playerId) => _signatures.RemoveAll(s => s.PlayerId == playerId) > 0;

    /// <summary>A copy for persistence.</summary>
    public PetitionData ToData()
        => new(Id, (int)OwnerId, (int)CharterItemId, Name, [.. _signatures.Select(s => new PetitionSignatureData((int)s.PlayerId, s.AccountId))]);
}
