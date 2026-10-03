using ArcaneCore.Game;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;

namespace ArcaneCore.World.Social;

/// <summary><see cref="ICharacterLookup"/> over the daemon's <see cref="CharacterDirectory"/> (offline characters included).</summary>
public sealed class CharacterLookup(CharacterDirectory directory) : ICharacterLookup
{
    public CharacterInfo? Find(uint characterId)
        => characterId is 0 or > int.MaxValue ? null : ToInfo(directory.Find((int)characterId));

    public CharacterInfo? FindByName(string name) => name.Length == 0 ? null : ToInfo(directory.FindByName(name));

    private static CharacterInfo? ToInfo(CharacterIdentity? identity) => identity is null
        ? null
        : new CharacterInfo((uint)identity.Id, identity.AccountId, identity.Name, (Race)identity.Race, (Class)identity.Class);
}
