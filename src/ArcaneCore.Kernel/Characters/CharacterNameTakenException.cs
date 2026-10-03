namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// A character could not be created because another row already holds its name (the unique index
/// on characters.name rejected the insert). The handler answers CHAR_CREATE_NAME_IN_USE for it, which
/// is what vmangos' name lookup would have said had the other creation committed first.
/// </summary>
public sealed class CharacterNameTakenException(string characterName, Exception? inner = null)
    : Exception($"The character name '{characterName}' is already taken.", inner)
{
    public string CharacterName { get; } = characterName;
}
