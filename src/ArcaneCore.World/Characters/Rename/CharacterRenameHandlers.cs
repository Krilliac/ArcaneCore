using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;

namespace ArcaneCore.World.Characters.Rename;

/// <summary>
/// CMSG_CHAR_RENAME (mangos OpcodeTable.cpp:796, STATUS_AUTHED): accepted at the character screen, on the session task because it
/// writes the characters database. The logic is <see cref="CharacterRename"/>.
/// </summary>
public sealed class CharacterRenameHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnSession(WorldOpcode.CmsgCharRename, SessionStates.CharacterSelect, CharacterRename.HandleAsync);
}
