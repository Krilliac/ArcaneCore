namespace ArcaneCore.Game.Spells.Druid;

/// <summary>Display id and model scale applied for a form.</summary>
public readonly record struct FormDisplay(uint DisplayId, float Scale);

/// <summary>
/// vmangos GetShapeshiftDisplayInfo (D:\refs\vmangos\src\game\Spells\SpellAuras.cpp:2317-2411), the druid forms
/// and Ghost Wolf (the shaman form, display 4613, scale 0.8, :2387-2390). The scale (0.8 for cat/travel/aquatic) is the vmangos value; mangos-classic does not scale. The
/// player Team picks the Alliance/Horde model (Player::TeamForRace); a non-player target uses the Alliance one.
/// </summary>
public static class FormDisplayTable
{
    public static FormDisplay? Get(byte form, bool alliance) => form switch
    {
        DruidForms.Cat => new FormDisplay(alliance ? 892u : 8571u, 0.80f),
        DruidForms.Travel => new FormDisplay(632, 0.80f),
        DruidForms.Aquatic => new FormDisplay(2428, 0.80f),
        DruidForms.Bear or DruidForms.DireBear => new FormDisplay(alliance ? 2281u : 2289u, 1.0f),
        DruidForms.Moonkin => new FormDisplay(alliance ? 15374u : 15375u, 1.0f),
        DruidForms.Tree => new FormDisplay(864, 1.0f),
        DruidForms.GhostWolf => new FormDisplay(4613, 0.80f),
        _ => null,
    };
}
