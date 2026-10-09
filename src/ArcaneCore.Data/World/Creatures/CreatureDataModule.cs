using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// <c>creature_template</c>: column meanings from cmangos-classic mangos.sql and vmangos
/// CreatureInfo (CreatureDefines.h); see <see cref="Kernel.WorldData.Creatures.CreatureTemplate"/>.
/// </summary>
public sealed class CreatureTemplateRow
{
    public uint Entry { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SubName { get; set; } = string.Empty;

    public byte MinLevel { get; set; } = 1;

    public byte MaxLevel { get; set; } = 1;

    public uint DisplayId1 { get; set; }

    public uint DisplayId2 { get; set; }

    public uint DisplayId3 { get; set; }

    public uint DisplayId4 { get; set; }

    public uint DisplayProbability1 { get; set; }

    public uint DisplayProbability2 { get; set; }

    public uint DisplayProbability3 { get; set; }

    public uint DisplayProbability4 { get; set; }

    public float Scale { get; set; }

    public float DisplayScale2 { get; set; }
    public float DisplayScale3 { get; set; }
    public float DisplayScale4 { get; set; }

    public uint Faction { get; set; }

    public uint NpcFlags { get; set; }

    /// <summary>vmangos CreatureDefines.h:242,272-275; added by CreatureNpcMetadataDataModule.</summary>
    public uint GossipMenuId { get; set; }

    public uint TrainerType { get; set; }

    public byte TrainerClass { get; set; }

    public byte TrainerRace { get; set; }

    public uint TrainerSpell { get; set; }

    public uint UnitFlags { get; set; }

    public uint DynamicFlags { get; set; }

    public uint TypeFlags { get; set; }

    public uint CreatureType { get; set; }

    public uint Family { get; set; }

    public uint Rank { get; set; }

    public byte UnitClass { get; set; }

    public byte InhabitType { get; set; } = 3;

    public bool Civilian { get; set; }

    public bool RacialLeader { get; set; }

    public float SpeedWalk { get; set; } = 1.0f;

    public float SpeedRun { get; set; } = 1.14286f;

    public uint MinLevelHealth { get; set; } = 1;

    public uint MaxLevelHealth { get; set; } = 1;

    public uint MinLevelMana { get; set; }

    public uint MaxLevelMana { get; set; }

    public uint Armor { get; set; }

    public float MinMeleeDamage { get; set; }

    public float MaxMeleeDamage { get; set; }

    public float MinRangedDamage { get; set; }

    public float MaxRangedDamage { get; set; }

    public uint MeleeAttackPower { get; set; }

    public uint RangedAttackPower { get; set; }

    public uint MeleeBaseAttackTime { get; set; } = 2000;

    public uint RangedBaseAttackTime { get; set; } = 2000;

    public uint DamageSchool { get; set; }

    public uint PetSpellDataId { get; set; }

    public byte MovementType { get; set; }

    public uint CorpseDecaySeconds { get; set; }

    public uint ExtraFlags { get; set; }

    /// <summary>cmangos-classic AIName (vmangos ai_name). Added by <see cref="CreatureAiDataModule"/>.</summary>
    public string AIName { get; set; } = string.Empty;

    /// <summary>Detection range in yards; <c>null</c> = the column was absent from the source (the content default, 18, applies). Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public float? Detection { get; set; }

    /// <summary>Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public float CallForHelp { get; set; }

    /// <summary>Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public uint Pursuit { get; set; }

    /// <summary>Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public float Leash { get; set; }

    /// <summary>Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public uint Timeout { get; set; }

    /// <summary>Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public uint StaticFlags1 { get; set; }

    /// <summary>Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public uint StaticFlags2 { get; set; }

    /// <summary><see cref="Kernel.WorldData.Creatures.CreatureExtraFlagsDialect"/> of <see cref="ExtraFlags"/> (0 unknown, 1 cmangos, 2 vmangos). Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public byte ExtraFlagsDialect { get; set; }
}

/// <summary><c>creature_spawn</c>: one placed creature (cmangos/vmangos <c>creature</c>).</summary>
public sealed class CreatureSpawnRow
{
    public uint Guid { get; set; }

    public uint Entry { get; set; }

    public uint MapId { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float Orientation { get; set; }

    public uint SpawnTimeMinSeconds { get; set; } = 120;

    public uint SpawnTimeMaxSeconds { get; set; } = 120;

    public float WanderDistance { get; set; }

    public byte MovementType { get; set; }
}

/// <summary><c>creature_movement</c>: waypoint paths per spawn (cmangos/vmangos <c>creature_movement</c>).</summary>
public sealed class CreatureMovementRow
{
    public uint SpawnGuid { get; set; }

    public uint Point { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float Orientation { get; set; }

    public uint WaitTimeMs { get; set; }

    /// <summary>Run to this node. Added by <see cref="CreatureAiDataModule"/>.</summary>
    public bool Run { get; set; }

    /// <summary>Creature movement DB script started on arrival.</summary>
    public uint ScriptId { get; set; }
}

/// <summary><c>creature_model_info</c> (cmangos name; vmangos <c>creature_display_info_addon</c>).</summary>
public sealed class CreatureModelInfoRow
{
    public uint DisplayId { get; set; }

    public float BoundingRadius { get; set; }

    public float CombatReach { get; set; }

    public byte Gender { get; set; } = 2;

    public uint DisplayIdOtherGender { get; set; }
}

/// <summary><c>creature_addon</c>: per-spawn mount, stand state, sheath and emote state.</summary>
public sealed class CreatureAddonRow
{
    public uint Guid { get; set; }

    public uint MountDisplayId { get; set; }

    public byte StandState { get; set; }

    public byte SheathState { get; set; }

    public uint EmoteState { get; set; }
}

/// <summary>
/// The creature tables of the world database, contributed as a world-schema step through the
/// <see cref="IDataModule"/> seam (docs/integration/seams.md): the shared
/// <c>WorldDbContext</c> maps them and the world bootstrapper creates them on upgrade, so the
/// world database still has one version sequence that fails closed (ROADMAP § Persistence).
/// <para>World schema version 2 is claimed here (docs/integration/creatures.md); if another
/// world module lands first, this one renumbers.</para>
/// </summary>
public sealed class CreatureDataModule : IDataModule
{
    /// <summary>The world schema version that introduces the creature tables.</summary>
    public const int Version = 2;

    public static readonly IReadOnlyList<string> Tables =
        ["creature_template", "creature_spawn", "creature_movement", "creature_model_info", "creature_addon"];

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [.. Tables.Select(t => new CreateTableChange(t))];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatureTemplateRow>(entity =>
        {
            entity.ToTable("creature_template");
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();

            // cmangos-classic: Name char(100) NOT NULL, SubName char(100).
            entity.Property(r => r.Name).HasMaxLength(100).IsRequired();
            entity.Property(r => r.SubName).HasMaxLength(100).IsRequired();

            // cmangos-classic: AIName char(64) NOT NULL DEFAULT ''.
            entity.Property(r => r.AIName).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<CreatureSpawnRow>(entity =>
        {
            entity.ToTable("creature_spawn");
            entity.HasKey(r => r.Guid);
            entity.Property(r => r.Guid).ValueGeneratedNever();
            entity.HasIndex(r => r.MapId);
        });

        modelBuilder.Entity<CreatureMovementRow>(entity =>
        {
            entity.ToTable("creature_movement");
            entity.HasKey(r => new { r.SpawnGuid, r.Point });
        });

        modelBuilder.Entity<CreatureModelInfoRow>(entity =>
        {
            entity.ToTable("creature_model_info");
            entity.HasKey(r => r.DisplayId);
            entity.Property(r => r.DisplayId).ValueGeneratedNever();
        });

        modelBuilder.Entity<CreatureAddonRow>(entity =>
        {
            entity.ToTable("creature_addon");
            entity.HasKey(r => r.Guid);
            entity.Property(r => r.Guid).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICreatureDataStore, EfCreatureDataStore>();
}
