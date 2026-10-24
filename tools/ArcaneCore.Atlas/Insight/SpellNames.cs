using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Insight;

/// <summary>Spell id to name from the client's Spell.dbc. Stub.</summary>
internal sealed class SpellNames
{
    public const string TablePath = "DBFilesClient\Spell.dbc";

    public int Count => throw new NotImplementedException();

    public static SpellNames FromBytes(byte[] dbc) => throw new NotImplementedException();

    public static SpellNames? TryLoad(IClientFileSource files) => throw new NotImplementedException();

    public string? Name(uint spellId) => throw new NotImplementedException();
}
