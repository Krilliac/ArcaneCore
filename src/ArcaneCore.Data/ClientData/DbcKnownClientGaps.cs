namespace ArcaneCore.Data.ClientData;

/// <summary>
/// Missing foreign ids observed in the 154-file 1.12.1.5875 extraction on 2026-10-08.
/// An id not listed here stays an unexpected dangling reference. These are client-file facts,
/// not replacement content; see docs/integration/dbc-content-review-20261008.md.
/// </summary>
internal static class DbcKnownClientGaps
{
    private static readonly IReadOnlyDictionary<string, uint[]> Values = new Dictionary<string, uint[]>(StringComparer.Ordinal)
    {
        ["AreaTable.dbc.ContinentID"] = [17, 150],
        ["AreaTable.dbc.IntroSound"] = [65, 67, 68, 71],
        ["AreaTrigger.dbc.ContinentID"] = [24, 28],
        ["CreatureModelData.dbc.SoundID"] = [908],
        ["CreatureSoundData.dbc.SoundExertionCriticalID"] = [313, 323, 329, 350, 362, 438, 443, 457, 756],
        ["CreatureSoundData.dbc.SoundInjuryID"] = [1025, 2521],
        ["CreatureSoundData.dbc.SoundInjuryCriticalID"] = [387, 1303],
        ["CreatureSoundData.dbc.SoundStunID"] = [191, 199, 690],
        ["CreatureSoundData.dbc.SoundStandID"] = [192, 200, 317, 327, 354, 366, 401, 436, 461],
        ["CreatureSoundData.dbc.SoundFidget"] = [763, 1017, 5514, 5515, 5516, 5517, 5518, 5519, 5520, 5521, 5522, 5523, 5524, 5526, 5527, 5528, 5529],
        ["SkillLineAbility.dbc.SupercededBySpell"] = [2997],
        ["SkillRaceClassInfo.dbc.SkillID"] = [96, 120, 130, 198, 199, 227, 238, 239, 241, 242, 243, 244, 245, 246, 247, 252, 254, 255, 258, 260, 262, 263, 264, 268, 269, 272, 273, 353, 416, 418, 419, 420, 453, 493, 515],
        ["Spell.dbc.EffectTriggerSpell"] = [875, 961, 1100, 1816, 1951, 2437, 2438, 2439, 2440, 2441, 2447, 2448, 2449, 2450, 2451, 2926],
        ["SpellItemEnchantment.dbc.EffectArg"] = [2820, 2821, 2822],
        ["SpellVisual.dbc.ImpactKit"] = [210],
        ["SpellVisualKit.dbc.SoundID"] = [17, 21, 23, 24, 34, 40, 675, 676, 933, 938, 1162, 1488, 1492, 1493, 3361, 7434],
        ["TaxiNodes.dbc.ContinentID"] = [131074],
        ["TaxiPathNode.dbc.PathID"] = [248, 403],
        ["UISoundLookups.dbc.SoundID"] = [820, 848, 849, 885, 886, 887, 889, 893, 894, 904, 3356],
        ["WMOAreaTable.dbc.ZoneMusic"] = [198, 260],
        ["WMOAreaTable.dbc.IntroSound"] = [65, 66, 68, 69, 71, 75, 77],
        ["ZoneMusic.dbc.Sounds"] = [7340],
    };

    internal static bool Contains(string reference, uint id) =>
        Values.TryGetValue(reference, out uint[]? ids) && Array.IndexOf(ids, id) >= 0;
}
