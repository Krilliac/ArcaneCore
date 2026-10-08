namespace ArcaneCore.Game.Guilds;

public sealed partial class GuildManager
{
    /// <summary>
    /// A guild founded by a completed petition (vmangos Guild::Create(petition, leader), Guild.cpp:104-119):
    /// default ranks, the leader at the guild master rank and every signer who is still unguilded and
    /// still exists at the lowest rank. No GE_JOINED goes out (AddMember sends none) and nothing is
    /// saved here: the petition manager stores the guild together with the petition's deletion in one
    /// transaction. Every member's other petitions and signatures are removed through
    /// <see cref="MemberJoined"/> except <paramref name="petitionId"/>. Null when the guilds are not
    /// loaded, the name is taken or the leader is gone or guilded.
    /// </summary>
    public Guild? CreateFromPetition(uint leaderId, string name, IEnumerable<uint> signerIds, int petitionId)
    {
        // Guild::Create(petition, leader) goes through Guild::Create(leader, name), antispam check included.
        if (!IsLoaded || GetByName(name) is not null || Antispam?.IsSpam(name) == true || context.Characters.Find(leaderId) is null || GetGuildOf(leaderId) is not null)
        {
            return null;
        }

        var guild = new Guild(_nextId++, name, leaderId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        guild.CreateDefaultRanks();
        _guilds[guild.Id] = guild;
        AddMember(guild, leaderId, Guild.GuildMasterRank, petitionId);
        foreach (uint signer in signerIds)
        {
            // Guild::AddMember refuses a signer who joined a guild meanwhile (ALREADY_IN_GUILD) or whose
            // character is gone (UNKNOWN_PLAYER); Guild::Create ignores the status and goes on.
            if (GetGuildOf(signer) is null && context.Characters.Find(signer) is not null)
            {
                AddMember(guild, signer, guild.LowestRank, petitionId);
            }
        }

        return guild;
    }
}
