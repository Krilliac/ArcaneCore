using System.Globalization;
using ArcaneCore.Data.Content;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>Which world-database layout a dump uses (detected from its column names).</summary>
public enum CreatureDumpDialect
{
    Unknown,

    /// <summary>cmangos classic-db: <c>creature_template.Entry/MinLevel/…</c> (mangos.sql).</summary>
    CMangos,

    /// <summary>vmangos world db: <c>creature_template.entry/level_min/…</c>, patch-versioned rows.</summary>
    VMangos,
}

/// <summary>What an import read and wrote.</summary>
public sealed record CreatureImportReport(
    CreatureDumpDialect Dialect,
    int Templates,
    int Spawns,
    int Waypoints,
    int Models,
    int Addons,
    int SkippedSpawns,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Maps the creature tables of a cmangos classic-db or vmangos world dump into ArcaneCore's
/// creature schema, by column <b>name</b> (ROADMAP § Content: the importer maps by name so
/// several sources can feed one schema). Read one or more dump files with <see cref="Read"/>,
/// then <see cref="WriteAsync"/> the result. The dumps are GPL data and are never committed.
/// <para>
/// cmangos columns come from mangos-classic <c>sql/base/mangos.sql</c>; vmangos columns from
/// <c>ObjectMgr::LoadCreatureTemplates / LoadCreatures</c>, <c>WaypointManager::Load</c> and
/// <c>LoadCreatureDisplayInfoAddon</c>. vmangos rows are patch-versioned: the row with the
/// highest <c>patch</c> (templates) or the rows whose <c>patch_min..patch_max</c> contains
/// <see cref="MaxPatch"/> (spawns) are used, like vmangos with <c>WowPatch</c> = 10 (1.12).
/// </para>
/// </summary>
public sealed class CreatureDumpImporter
{
    /// <summary>vmangos WowPatch for 1.12.1 (vmangos <c>WOW_PATCH_112</c> = 10).</summary>
    public const int MaxPatch = 10;

    /// <summary>vmangos build filter for progressive tables (<c>SUPPORTED_CLIENT_BUILD</c> 5875).</summary>
    public const int ClientBuild = 5875;

    private readonly Dictionary<uint, (int Patch, CreatureTemplateRow Row, VMangosStats? Stats)> _templates = [];
    private readonly Dictionary<uint, CreatureSpawnRow> _spawns = [];
    private readonly Dictionary<(uint, uint), CreatureMovementRow> _movement = [];
    private readonly Dictionary<uint, (int Build, CreatureModelInfoRow Row)> _models = [];
    private readonly Dictionary<uint, CreatureAddonRow> _addons = [];
    private readonly Dictionary<(byte Class, byte Level), ClassLevelStats> _classLevelStats = [];
    private readonly List<string> _warnings = [];
    private int _skippedSpawns;

    public CreatureDumpDialect Dialect { get; private set; }

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        var reader = new MySqlDumpReader(dump);
        foreach (object item in reader.Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            switch (row.Table.ToLowerInvariant())
            {
                case "creature_template":
                    ReadTemplate(row);
                    break;
                case "creature":
                    ReadSpawn(row);
                    break;
                case "creature_movement":
                    ReadMovement(row);
                    break;
                case "creature_model_info":
                case "creature_display_info_addon":
                    ReadModel(row);
                    break;
                case "creature_addon":
                    ReadAddon(row);
                    break;
                case "creature_classlevelstats":
                    ReadClassLevelStats(row);
                    break;
            }
        }
    }

    /// <summary>Write everything read so far. With <paramref name="replace"/> the creature tables are emptied first.</summary>
    public async Task<CreatureImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        FinishTemplates();

        if (replace)
        {
            await db.Set<CreatureAddonRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<CreatureMovementRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<CreatureSpawnRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<CreatureModelInfoRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<CreatureTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            await InsertBatchedAsync(db, _templates.Values.Select(t => t.Row), cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _models.Values.Select(m => m.Row), cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _spawns.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _movement.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _addons.Values, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = detect;
        }

        return BuildReport();
    }

    /// <summary>The rows that would be written (for inspection and tests).</summary>
    public (IReadOnlyCollection<CreatureTemplateRow> Templates, IReadOnlyCollection<CreatureSpawnRow> Spawns,
        IReadOnlyCollection<CreatureMovementRow> Movement, IReadOnlyCollection<CreatureModelInfoRow> Models,
        IReadOnlyCollection<CreatureAddonRow> Addons) Snapshot()
    {
        FinishTemplates();
        return ([.. _templates.Values.Select(t => t.Row)], [.. _spawns.Values], [.. _movement.Values],
            [.. _models.Values.Select(m => m.Row)], [.. _addons.Values]);
    }

    public CreatureImportReport BuildReport() => new(
        Dialect, _templates.Count, _spawns.Count, _movement.Count, _models.Count, _addons.Count, _skippedSpawns, [.. _warnings]);

    private static async Task InsertBatchedAsync<T>(WorldDbContext db, IEnumerable<T> rows, CancellationToken ct)
        where T : class
    {
        const int BatchSize = 2000;
        int pending = 0;
        foreach (T row in rows)
        {
            db.Set<T>().Add(row);
            if (++pending == BatchSize)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    // --- creature_template --------------------------------------------------------------

    private void ReadTemplate(DumpRow row)
    {
        bool vmangos = row.Has("level_min");
        SetDialect(vmangos ? CreatureDumpDialect.VMangos : CreatureDumpDialect.CMangos);

        uint entry = U32(row, "Entry");
        int patch = vmangos ? (int)U32(row, "patch") : 0;
        if (patch > MaxPatch)
        {
            return;
        }

        if (_templates.TryGetValue(entry, out var existing) && existing.Patch > patch)
        {
            return;
        }

        var t = new CreatureTemplateRow
        {
            Entry = entry,
            Name = Str(row, "Name"),
            SubName = Str(row, "SubName", "subname"),
            MinLevel = U8(row, "MinLevel", "level_min"),
            MaxLevel = U8(row, "MaxLevel", "level_max"),
            DisplayId1 = U32(row, "DisplayId1", "ModelId1", "display_id1"),
            DisplayId2 = U32(row, "DisplayId2", "ModelId2", "display_id2"),
            DisplayId3 = U32(row, "DisplayId3", "ModelId3", "display_id3"),
            DisplayId4 = U32(row, "DisplayId4", "ModelId4", "display_id4"),
            DisplayProbability1 = U32(row, "DisplayIdProbability1", "display_probability1"),
            DisplayProbability2 = U32(row, "DisplayIdProbability2", "display_probability2"),
            DisplayProbability3 = U32(row, "DisplayIdProbability3", "display_probability3"),
            DisplayProbability4 = U32(row, "DisplayIdProbability4", "display_probability4"),
            Scale = F32(row, 0f, "Scale", "display_scale1"),
            Faction = U32(row, "Faction", "FactionAlliance", "faction"),
            NpcFlags = U32(row, "NpcFlags", "npc_flags"),
            UnitFlags = U32(row, "UnitFlags"),
            DynamicFlags = U32(row, "DynamicFlags"),
            TypeFlags = U32(row, "CreatureTypeFlags"),
            CreatureType = U32(row, "CreatureType", "type"),
            Family = U32(row, "Family", "pet_family"),
            Rank = U32(row, "Rank"),
            UnitClass = U8(row, "UnitClass", "unit_class"),
            InhabitType = row.TryGet(out _, "InhabitType", "inhabit_type") ? U8(row, "InhabitType", "inhabit_type") : (byte)3,
            Civilian = U32(row, "Civilian") != 0,
            RacialLeader = U32(row, "RacialLeader", "racial_leader") != 0,
            SpeedWalk = F32(row, 1.0f, "SpeedWalk", "speed_walk"),
            SpeedRun = F32(row, 1.14286f, "SpeedRun", "speed_run"),
            MinLevelHealth = U32(row, "MinLevelHealth"),
            MaxLevelHealth = U32(row, "MaxLevelHealth"),
            MinLevelMana = U32(row, "MinLevelMana"),
            MaxLevelMana = U32(row, "MaxLevelMana"),
            Armor = U32(row, "Armor"),
            MinMeleeDamage = F32(row, 0f, "MinMeleeDmg"),
            MaxMeleeDamage = F32(row, 0f, "MaxMeleeDmg"),
            MinRangedDamage = F32(row, 0f, "MinRangedDmg"),
            MaxRangedDamage = F32(row, 0f, "MaxRangedDmg"),
            MeleeAttackPower = U32(row, "MeleeAttackPower"),
            RangedAttackPower = U32(row, "RangedAttackPower"),
            MeleeBaseAttackTime = U32Or(row, 2000, "MeleeBaseAttackTime", "base_attack_time"),
            RangedBaseAttackTime = U32Or(row, 2000, "RangedBaseAttackTime", "ranged_attack_time"),
            DamageSchool = U32(row, "DamageSchool", "damage_school"),
            PetSpellDataId = U32(row, "PetSpellDataId", "pet_spell_list_id"),
            MovementType = U8(row, "MovementType", "movement_type"),
            CorpseDecaySeconds = U32(row, "CorpseDecay"),
            ExtraFlags = U32(row, "ExtraFlags", "flags_extra"),
        };

        VMangosStats? stats = null;
        if (vmangos)
        {
            uint static1 = U32(row, "static_flags1");
            uint static2 = U32(row, "static_flags2");
            t.TypeFlags = VMangosTypeFlags(static1, static2);
            t.UnitFlags = VMangosUnitFlags(static1);
            stats = new VMangosStats(
                F32(row, 1f, "health_multiplier"), F32(row, 1f, "mana_multiplier"), F32(row, 1f, "armor_multiplier"),
                F32(row, 1f, "damage_multiplier"), F32(row, 0.14f, "damage_variance"));
        }

        _templates[entry] = (patch, t, stats);
    }

    /// <summary>vmangos CreatureInfo::GetTypeFlags — the client-visible subset of the static flags.</summary>
    internal static uint VMangosTypeFlags(uint static1, uint static2)
    {
        uint flags = 0;
        if ((static1 & 0x00000010) != 0) flags |= 0x01;  // TAMEABLE
        if ((static1 & 0x00200000) != 0) flags |= 0x02;  // VISIBLE_TO_GHOSTS
        if ((static1 & 0x00010000) != 0) flags |= 0x04;  // RAID_BOSS_MOB
        if ((static1 & 0x00800000) != 0) flags |= 0x08;  // DO_NOT_PLAY_WOUND_ANIM
        if ((static1 & 0x01000000) != 0) flags |= 0x10;  // NO_FACTION_TOOLTIP
        if ((static1 & 0x40000000) != 0) flags |= 0x20;  // MORE_AUDIBLE
        if ((static2 & 0x00000008) != 0) flags |= 0x40;  // NO_HARMFUL_VERTEX_COLORING
        return flags;
    }

    /// <summary>vmangos Creature::ToggleUnitFlagsFromStaticFlags — unit flags implied by static flags.</summary>
    internal static uint VMangosUnitFlags(uint static1)
    {
        uint flags = 0;
        if ((static1 & 0x00000020) != 0) flags |= 0x00000100; // IMMUNE_TO_PC -> UNIT_FLAG_IMMUNE_TO_PLAYER
        if ((static1 & 0x00000040) != 0) flags |= 0x00000200; // IMMUNE_TO_NPC -> UNIT_FLAG_IMMUNE_TO_NPC
        if ((static1 & 0x00000200) != 0) flags |= 0x02000000; // UNINTERACTIBLE -> UNIT_FLAG_NOT_SELECTABLE
        if ((static1 & 0x10000000) != 0) flags |= 0x00008000; // CAN_SWIM -> UNIT_FLAG_USE_SWIM_ANIMATION
        return flags;
    }

    /// <summary>
    /// vmangos keeps health/mana/armor/damage in <c>creature_classlevelstats</c> × per-template
    /// multipliers (Creature::InitStatsForLevel); ArcaneCore stores the resulting per-level
    /// values like cmangos' MinLevelHealth/MaxLevelHealth columns.
    /// </summary>
    private void FinishTemplates()
    {
        foreach (uint entry in _templates.Keys.ToArray())
        {
            (int patch, CreatureTemplateRow t, VMangosStats? stats) = _templates[entry];
            if (stats is null)
            {
                continue;
            }

            byte cls = t.UnitClass == 0 ? (byte)1 : t.UnitClass;
            if (_classLevelStats.TryGetValue((cls, t.MinLevel), out ClassLevelStats min)
                && _classLevelStats.TryGetValue((cls, t.MaxLevel), out ClassLevelStats max))
            {
                // health = max(1, round(cls.health * health_multiplier)); mana = round(cls.mana * mana_multiplier)
                t.MinLevelHealth = Math.Max(1u, (uint)MathF.Round(min.Health * stats.HealthMultiplier));
                t.MaxLevelHealth = Math.Max(1u, (uint)MathF.Round(max.Health * stats.HealthMultiplier));
                t.MinLevelMana = (uint)MathF.Round(min.Mana * stats.ManaMultiplier);
                t.MaxLevelMana = (uint)MathF.Round(max.Mana * stats.ManaMultiplier);
                t.Armor = (uint)MathF.Round(max.Armor * stats.ArmorMultiplier);

                // average ± average × variance (InitStatsForLevel)
                float melee = max.MeleeDamage * stats.DamageMultiplier;
                float ranged = max.RangedDamage * stats.DamageMultiplier;
                t.MinMeleeDamage = melee - (melee * stats.DamageVariance);
                t.MaxMeleeDamage = melee + (melee * stats.DamageVariance);
                t.MinRangedDamage = ranged - (ranged * stats.DamageVariance);
                t.MaxRangedDamage = ranged + (ranged * stats.DamageVariance);
                t.MeleeAttackPower = (uint)Math.Max(0, max.AttackPower);
                t.RangedAttackPower = (uint)Math.Max(0, max.RangedAttackPower);
            }
            else
            {
                t.MinLevelHealth = Math.Max(t.MinLevelHealth, 1);
                t.MaxLevelHealth = Math.Max(t.MaxLevelHealth, 1);
                Warn($"creature_template {entry}: no creature_classlevelstats for class {cls} levels {t.MinLevel}-{t.MaxLevel}; health left at 1");
            }

            _templates[entry] = (patch, t, null);
        }
    }

    private void ReadClassLevelStats(DumpRow row)
    {
        var key = (U8(row, "class"), U8(row, "level"));
        _classLevelStats[key] = new ClassLevelStats(
            F32(row, 0f, "health"), F32(row, 0f, "mana"), F32(row, 0f, "armor"),
            F32(row, 0f, "melee_damage"), F32(row, 0f, "ranged_damage"),
            (int)F32(row, 0f, "attack_power"), (int)F32(row, 0f, "ranged_attack_power"));
    }

    // --- creature ------------------------------------------------------------------------

    private void ReadSpawn(DumpRow row)
    {
        if (row.TryGet(out string? patchMin, "patch_min") && row.TryGet(out string? patchMax, "patch_max"))
        {
            if (Int(patchMin) > MaxPatch || Int(patchMax) < MaxPatch)
            {
                _skippedSpawns++;
                return;
            }
        }

        uint min = row.TryGet(out _, "spawntimesecsmin") ? U32(row, "spawntimesecsmin") : U32Or(row, 120, "spawntimesecs");
        uint max = row.TryGet(out _, "spawntimesecsmax") ? U32(row, "spawntimesecsmax") : min;
        if (row.Has("id2") && U32(row, "id2") != 0)
        {
            Warn($"creature {U32(row, "guid")}: alternative ids (id2-id5) are not imported; using id {U32(row, "id")}");
        }

        var spawn = new CreatureSpawnRow
        {
            Guid = U32(row, "guid"),
            Entry = U32(row, "id"),
            MapId = U32(row, "map"),
            X = F32(row, 0f, "position_x"),
            Y = F32(row, 0f, "position_y"),
            Z = F32(row, 0f, "position_z"),
            Orientation = F32(row, 0f, "orientation"),
            SpawnTimeMinSeconds = Math.Min(min, max),
            SpawnTimeMaxSeconds = Math.Max(min, max),
            WanderDistance = F32(row, 0f, "spawndist", "wander_distance"),
            MovementType = U8(row, "MovementType", "movement_type"),
        };
        _spawns[spawn.Guid] = spawn;
    }

    private void ReadMovement(DumpRow row)
    {
        var point = new CreatureMovementRow
        {
            SpawnGuid = U32(row, "Id"),
            Point = U32(row, "Point"),
            X = F32(row, 0f, "PositionX", "position_x"),
            Y = F32(row, 0f, "PositionY", "position_y"),
            Z = F32(row, 0f, "PositionZ", "position_z"),
            Orientation = F32(row, 0f, "Orientation"),
            WaitTimeMs = U32(row, "WaitTime"),
        };
        _movement[(point.SpawnGuid, point.Point)] = point;
    }

    private void ReadModel(DumpRow row)
    {
        int build = row.TryGet(out string? b, "build") && b is not null ? Int(b) : 0;
        if (build > ClientBuild)
        {
            return;
        }

        uint displayId = U32(row, "modelid", "display_id");
        if (_models.TryGetValue(displayId, out var existing) && existing.Build > build)
        {
            return;
        }

        _models[displayId] = (build, new CreatureModelInfoRow
        {
            DisplayId = displayId,
            BoundingRadius = F32(row, 0f, "bounding_radius"),
            CombatReach = F32(row, 0f, "combat_reach"),
            Gender = row.TryGet(out _, "gender") ? U8(row, "gender") : (byte)2,
            DisplayIdOtherGender = U32(row, "modelid_other_gender", "display_id_other_gender"),
        });
    }

    private void ReadAddon(DumpRow row)
    {
        var addon = new CreatureAddonRow
        {
            Guid = U32(row, "guid"),
            MountDisplayId = (uint)Math.Max(0, Int(Get(row, "mount", "mount_display_id"))),
            StandState = U8(row, "stand_state"),
            SheathState = U8(row, "sheath_state"),
            EmoteState = U32(row, "emote", "emote_state"),
        };
        _addons[addon.Guid] = addon;
    }

    // --- helpers ---------------------------------------------------------------------------

    private void SetDialect(CreatureDumpDialect dialect)
    {
        if (Dialect == CreatureDumpDialect.Unknown)
        {
            Dialect = dialect;
        }
        else if (Dialect != dialect)
        {
            throw new FormatException($"mixed dump dialects: {Dialect} and {dialect}");
        }
    }

    private void Warn(string message)
    {
        if (_warnings.Count < 1000)
        {
            _warnings.Add(message);
        }
    }

    private static string? Get(DumpRow row, params ReadOnlySpan<string> names)
        => row.TryGet(out string? value, names) ? value : null;

    private static string Str(DumpRow row, params ReadOnlySpan<string> names) => Get(row, names) ?? string.Empty;

    private static int Int(string? raw)
        => string.IsNullOrEmpty(raw) ? 0 : (int)double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static uint U32(DumpRow row, params ReadOnlySpan<string> names) => U32Or(row, 0, names);

    private static uint U32Or(DumpRow row, uint fallback, params ReadOnlySpan<string> names)
    {
        string? raw = Get(row, names);
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        double value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        return value <= 0 ? 0 : value >= uint.MaxValue ? uint.MaxValue : (uint)value;
    }

    private static byte U8(DumpRow row, params ReadOnlySpan<string> names) => (byte)Math.Min(U32(row, names), byte.MaxValue);

    private static float F32(DumpRow row, float fallback, params ReadOnlySpan<string> names)
    {
        string? raw = Get(row, names);
        return string.IsNullOrEmpty(raw) ? fallback : float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private sealed record VMangosStats(float HealthMultiplier, float ManaMultiplier, float ArmorMultiplier, float DamageMultiplier, float DamageVariance);

    private readonly record struct ClassLevelStats(
        float Health, float Mana, float Armor, float MeleeDamage, float RangedDamage, int AttackPower, int RangedAttackPower);
}
