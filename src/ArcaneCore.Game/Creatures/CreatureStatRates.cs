namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureOptions
{
    /// <summary>The creature health, melee damage and spell damage rates by rank (<c>Creatures:Rates:*</c>, vmangos Rate.Creature.*).</summary>
    public CreatureStatRates Rates { get; set; } = new();
}

/// <summary>
/// vmangos Rate.Creature.{Normal,Elite.Elite,Elite.RAREELITE,Elite.WORLDBOSS,Elite.RARE}.{HP,Damage,SpellDamage} (World.cpp:514-528, setConfigPos,
/// every default 1), read by Creature::_GetHealthMod / _GetDamageMod / _GetSpellDamageMod (Creature.cpp:1856-1911): rank 0 normal, 1 elite,
/// 2 rare elite, 3 world boss, 4 rare, anything else elite. The health rate scales the health a creature spawns with (Creature::SelectLevel,
/// :1802-1806, a pet counting as normal), the damage rate its melee and ranged weapon damage (:1830-1843, also ResetStats and
/// GetDefaultDamageRange), the spell damage rate the done side of its damage spells (SpellCaster::SpellDamageBonusDone, SpellCaster.cpp:1588-1590,
/// and MeleeDamageBonusDone for a non-weapon spell, :1358-1359). Player-owned pets never use them (vmangos Pet.cpp:1359-1360, SpellCaster.cpp:1358,
/// :1589); here no pet or guardian does (summons with a pet GUID are spawned without them). A change applies to creatures created or respawned
/// afterwards; the section is read at start.
/// </summary>
public sealed class CreatureStatRates
{
    /// <summary>Rate.Creature.Normal.HP (default 1).</summary>
    public float NormalHp { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.Elite.HP (default 1; also the rate of an unknown rank).</summary>
    public float EliteHp { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.RAREELITE.HP (default 1).</summary>
    public float RareEliteHp { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.WORLDBOSS.HP (default 1).</summary>
    public float WorldBossHp { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.RARE.HP (default 1).</summary>
    public float RareHp { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Normal.Damage (default 1).</summary>
    public float NormalDamage { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.Elite.Damage (default 1; also the rate of an unknown rank).</summary>
    public float EliteDamage { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.RAREELITE.Damage (default 1).</summary>
    public float RareEliteDamage { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.WORLDBOSS.Damage (default 1).</summary>
    public float WorldBossDamage { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.RARE.Damage (default 1).</summary>
    public float RareDamage { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Normal.SpellDamage (default 1).</summary>
    public float NormalSpellDamage { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.Elite.SpellDamage (default 1; also the rate of an unknown rank).</summary>
    public float EliteSpellDamage { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.RAREELITE.SpellDamage (default 1).</summary>
    public float RareEliteSpellDamage { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.WORLDBOSS.SpellDamage (default 1).</summary>
    public float WorldBossSpellDamage { get; set; } = 1.0f;

    /// <summary>Rate.Creature.Elite.RARE.SpellDamage (default 1).</summary>
    public float RareSpellDamage { get; set; } = 1.0f;

    /// <summary>vmangos Creature::_GetHealthMod.</summary>
    public float Hp(uint rank) => ByRank(rank, NormalHp, EliteHp, RareEliteHp, WorldBossHp, RareHp);

    /// <summary>vmangos Creature::_GetDamageMod.</summary>
    public float Damage(uint rank) => ByRank(rank, NormalDamage, EliteDamage, RareEliteDamage, WorldBossDamage, RareDamage);

    /// <summary>vmangos Creature::_GetSpellDamageMod.</summary>
    public float SpellDamage(uint rank) => ByRank(rank, NormalSpellDamage, EliteSpellDamage, RareEliteSpellDamage, WorldBossSpellDamage, RareSpellDamage);

    private static float ByRank(uint rank, float normal, float elite, float rareElite, float worldBoss, float rare) => (CreatureRank)rank switch
    {
        CreatureRank.Normal => normal,
        CreatureRank.RareElite => rareElite,
        CreatureRank.WorldBoss => worldBoss,
        CreatureRank.Rare => rare,
        _ => elite,
    };

    /// <summary>vmangos setConfigPos (World.cpp:2959-2967): a negative (or NaN) rate falls back to 1. Returns the names that were replaced.</summary>
    public IReadOnlyList<string> Normalize()
    {
        var replaced = new List<string>();
        foreach (System.Reflection.PropertyInfo property in typeof(CreatureStatRates).GetProperties())
        {
            if (property.PropertyType == typeof(float) && property.GetValue(this) is float value && !(value >= 0.0f))
            {
                property.SetValue(this, 1.0f);
                replaced.Add(property.Name);
            }
        }

        return replaced;
    }
}
