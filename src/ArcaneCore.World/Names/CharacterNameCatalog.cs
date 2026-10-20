using ArcaneCore.Data.Content.Names;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Names;

/// <summary>
/// The name-catalog refusal of a player name, shared by character creation and rename. vmangos ObjectMgr::CheckPlayerName ends in
/// ValidateName over NamesProfanity.dbc and NamesReserved.dbc (ObjectMgr.cpp:9597, DBCStores.cpp:617-628) for every caller, and both
/// HandleCharCreateOpcode and HandleCharRenameOpcode then refuse the <c>reserved_name</c> list to SEC_PLAYER only
/// (CharacterHandler.cpp:264 and :784).
/// </summary>
public static class CharacterNameCatalog
{
    /// <summary>
    /// The CHAR_NAME_* code that refuses <paramref name="name"/> (already normalized and past the name rules), or null when the
    /// catalog allows it: <see cref="CharResult.CharNameProfane"/> or <see cref="CharResult.CharNameReserved"/>. Staff above
    /// <see cref="AccountSecurity.Player"/> may take a name whose only reserved reason is the SQL list.
    /// </summary>
    public static CharResult? Check(NameCatalog catalog, string name, AccountSecurity security)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(name);
        NameCatalogResult result = catalog.Check(name);
        if (result == NameCatalogResult.Reserved && security > AccountSecurity.Player && catalog.IsSqlReservedOnly(name))
        {
            return null;
        }

        return result switch
        {
            NameCatalogResult.Profane => CharResult.CharNameProfane,
            NameCatalogResult.Reserved => CharResult.CharNameReserved,
            _ => null,
        };
    }
}
