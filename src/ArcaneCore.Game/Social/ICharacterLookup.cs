namespace ArcaneCore.Game.Social;

/// <summary>Finds characters that may be offline (the world daemon's character directory).</summary>
public interface ICharacterLookup
{
    CharacterInfo? Find(uint characterId);

    /// <summary>Exact (already normalized) name.</summary>
    CharacterInfo? FindByName(string name);
}
