using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>An in-memory character directory.</summary>
internal sealed class FakeCharacters : ICharacterLookup
{
    private readonly Dictionary<uint, CharacterInfo> _byId = [];

    public void Add(CharacterInfo info) => _byId[info.Id] = info;

    public void Remove(uint id) => _byId.Remove(id);

    public CharacterInfo? Find(uint characterId) => _byId.GetValueOrDefault(characterId);

    public CharacterInfo? FindByName(string name)
        => _byId.Values.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Records social writes.</summary>
internal sealed class FakePersistence : ISocialPersistence
{
    public List<(int CharacterId, int OtherId, SocialFlags Flags)> Social { get; } = [];

    public Dictionary<int, GuildData> Guilds { get; } = [];

    public List<int> Deleted { get; } = [];

    /// <summary>When set, <see cref="TrySetSocial"/> refuses (the write queue reached a bound) and records nothing.</summary>
    public bool RefuseSocial { get; set; }

    public void SetSocial(int characterId, int otherId, SocialFlags flags) => Social.Add((characterId, otherId, flags));

    public bool TrySetSocial(int characterId, int otherId, SocialFlags flags)
    {
        if (RefuseSocial)
        {
            return false;
        }

        SetSocial(characterId, otherId, flags);
        return true;
    }

    public void SaveGuild(GuildData guild) => Guilds[guild.Id] = guild;

    public void DeleteGuild(int guildId)
    {
        Guilds.Remove(guildId);
        Deleted.Add(guildId);
    }
}

/// <summary>A world with the social systems attached and helpers to add players and read what they were sent.</summary>
internal sealed class SocialFixture : IDisposable
{
    private readonly Dictionary<ObjectGuid, FakeSession> _sessions = [];

    public SocialFixture(SocialOptions? options = null)
    {
        World = TestWorld.CreateRuntime();
        Context = new SocialContext(World, Characters, Persistence, options);
    }

    public WorldRuntime World { get; }

    public FakeCharacters Characters { get; } = new();

    public FakePersistence Persistence { get; } = new();

    public SocialContext Context { get; }

    /// <summary>A known character that is not online.</summary>
    public CharacterInfo AddOffline(uint guid, Race race = Race.Human)
    {
        var info = new CharacterInfo(guid, (int)guid, $"P{guid}", race, Class.Warrior);
        Characters.Add(info);
        return info;
    }

    /// <summary>An online player named P{guid}; far-apart coordinates keep players out of each other's sight.</summary>
    public Player AddPlayer(uint guid, Race race = Race.Human, AccountSecurity security = AccountSecurity.Player, float x = 0, float y = 0)
    {
        var session = new FakeSession((int)guid, security);
        Player player = TestWorld.CreatePlayer(guid, x, y, session, race: race);
        World.AddPlayer(player);
        Characters.Add(new CharacterInfo(guid, (int)guid, player.Name, race, player.Class));
        _sessions[player.Guid] = session;
        Context.Friends.Load(player, []);
        return player;
    }

    public FakeSession Session(Player player) => _sessions[player.Guid];

    /// <summary>Forget everything sent so far.</summary>
    public void ClearAll()
    {
        foreach (FakeSession session in _sessions.Values)
        {
            session.Clear();
        }
    }

    /// <summary>The payloads of every packet with <paramref name="opcode"/> sent to <paramref name="player"/>.</summary>
    public List<byte[]> Sent(Player player, WorldOpcode opcode)
        => [.. Session(player).Sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];

    public byte[] Single(Player player, WorldOpcode opcode) => Assert.Single(Sent(player, opcode));

    public void Dispose() => World.Dispose();
}
