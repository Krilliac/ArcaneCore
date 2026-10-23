using ArcaneCore.Data.ClientData;

namespace ArcaneCore.Data.Content.ClientEffects;

/// <summary>Shared checks of the client-effect DBC readers.</summary>
internal static class ClientEffectDbc
{
    /// <summary>Refuse a file whose field count is not the build-5875 layout.</summary>
    public static void RequireFields(DbcFile file, string fileName, int fields)
    {
        file.RequireFields(fields, table => $"{fileName} of build 5875 has {fields} fields, this file has {table.FieldCount}");
    }
}
