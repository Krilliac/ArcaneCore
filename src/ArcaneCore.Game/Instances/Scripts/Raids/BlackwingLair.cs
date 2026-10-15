using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts.BlackwingLair;

namespace ArcaneCore.Game.Instances.Scripts.Raids;

/// <summary>
/// Blackwing Lair drake encounter descriptors and helpers for Firemaw (11983) and Flamegor (11981)
/// (mangos-classic blackwing_lair/boss_firemaw.cpp, boss_flamegor.cpp).
/// </summary>
public static class BlackwingLairDrakes
{
    public const uint FiremawEntry = 11983;
    public const uint FlamegorEntry = 11981;

    public const uint ShadowFlameSpell = 22539;
    public const uint WingBuffetSpell = 23339;
    public const uint FlameBuffetSpell = 23341;
    public const uint FrenzySpell = 23342;

    public static FiremawAI CreateFiremaw(Creature creature) => new(creature);

    public static FlamegorAI CreateFlamegor(Creature creature) => new(creature);
}
