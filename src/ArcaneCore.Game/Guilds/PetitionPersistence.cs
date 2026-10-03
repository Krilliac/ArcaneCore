using ArcaneCore.Kernel.Social;

namespace ArcaneCore.Game.Guilds;

/// <summary>
/// Accepts petition writes made on the world thread for ordered, asynchronous persistence. The
/// social write queue implements it next to <see cref="Social.ISocialPersistence"/>, so a petition
/// write is ordered with the guild writes that precede it (a turn-in follows the saves of the
/// guild it replaces). Zero I/O in the managers.
/// </summary>
public interface IPetitionPersistence
{
    /// <summary>Insert or replace the petition and exactly these signatures.</summary>
    void SavePetition(PetitionData petition);

    /// <summary>Delete the petition and its signatures.</summary>
    void DeletePetition(int petitionId);

    /// <summary>Store the founded guild and delete the petition in one transaction.</summary>
    void CompletePetition(GuildData guild, int petitionId);
}
