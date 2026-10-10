
namespace ArcaneCore.Game.Creatures.Scripts.WorldBosses;

/// <summary>
/// The open-world bosses that ClassicDB z2815 hands to core scripts (creature_template ScriptName boss_ysondre, boss_lethon, boss_emeriss,
/// boss_taerar, npc_spirit_shade, boss_azuregos, boss_kazzak, all with an empty AIName and no creature_ai_scripts rows). Their adds that
/// ClassicDB scripts with EventAI (Dream Fog 15224, Demented Druid Spirit 15260, Shade of Taerar 15302) are left to EventAI.
/// </summary>
public static class WorldBossScripts
{
    /// <summary>Every entry served here.</summary>
    public static readonly uint[] Entries =
        [YsondreAI.Entry, LethonAI.Entry, EmerissAI.Entry, TaerarAI.Entry, SpiritShadeAI.Entry, AzuregosAI.Entry, KazzakAI.Entry];

    public static CreatureAI? Create(Creature creature) => creature.Template.Entry switch
    {
        YsondreAI.Entry => new YsondreAI(creature),
        LethonAI.Entry => new LethonAI(creature),
        EmerissAI.Entry => new EmerissAI(creature),
        TaerarAI.Entry => new TaerarAI(creature),
        SpiritShadeAI.Entry => new SpiritShadeAI(creature),
        AzuregosAI.Entry => new AzuregosAI(creature),
        KazzakAI.Entry => new KazzakAI(creature),
        _ => null,
    };
}
