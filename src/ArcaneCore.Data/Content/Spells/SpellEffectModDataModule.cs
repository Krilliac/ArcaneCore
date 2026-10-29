using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Content.Spells;

/// <summary>
/// One row of vmangos <c>spell_effect_mod</c>: a correction of one effect of a Spell.dbc entry, applied over the spell data when the
/// spell table is built (vmangos SpellModMgr::LoadSpellMods, src/game/Spells/SpellModMgr.cpp:195-262). A value of -1 keeps the spell's
/// own value (ModUInt32ValueIfExplicit and friends, :48-70: the unsigned columns apply when non-negative, the signed and float columns
/// when not -1). The columns are vmangos' names.
/// </summary>
public sealed class SpellEffectModRow
{
    public uint Id { get; set; }

    public byte EffectIndex { get; set; }

    public int Effect { get; set; } = -1;

    public int EffectApplyAuraName { get; set; } = -1;

    public int EffectMechanic { get; set; } = -1;

    public int EffectImplicitTargetA { get; set; } = -1;

    public int EffectImplicitTargetB { get; set; } = -1;

    public int EffectRadiusIndex { get; set; } = -1;

    public long EffectItemType { get; set; } = -1;

    public int EffectMiscValue { get; set; } = -1;

    public int EffectTriggerSpell { get; set; } = -1;

    public int EffectDieSides { get; set; } = -1;

    public int EffectBaseDice { get; set; } = -1;

    public int EffectBasePoints { get; set; } = -1;

    public int EffectAmplitude { get; set; } = -1;

    public int EffectChainTarget { get; set; } = -1;

    public float EffectDicePerLevel { get; set; } = -1f;

    public float EffectRealPointsPerLevel { get; set; } = -1f;

    public float EffectPointsPerComboPoint { get; set; } = -1f;

    public float EffectMultipleValue { get; set; } = -1f;
}

/// <summary>
/// World schema step 63: <c>spell_effect_mod</c> (<see cref="SpellEffectModRow"/>), keyed by spell and effect index as vmangos reads it. The
/// rows come from a vmangos world dump through <c>arcane-content-importer refresh</c> (<c>SpellEffectModDumpImporter</c>); nothing is
/// seeded, the table is GPL content the operator imports (docs/areas/content-import.md). The spell table applies them when it is built
/// (<c>SpellStoreFactory</c>).
/// </summary>
public sealed class SpellEffectModDataModule : IDataModule
{
    public const string Table = "spell_effect_mod";

    /// <summary>The world schema version of this step: the one after the spell DB scripts.</summary>
    public const int Version = ArcaneCore.Data.World.Creatures.DbSpellScriptDataModule.Version + 1;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<SpellEffectModRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => new { r.Id, r.EffectIndex });
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.EffectIndex).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}
