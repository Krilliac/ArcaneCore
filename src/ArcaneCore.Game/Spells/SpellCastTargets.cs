using ArcaneCore.Protocol;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The target block of CMSG_CAST_SPELL, SMSG_SPELL_START and SMSG_SPELL_GO.
/// <para>
/// Read order follows cmangos-classic SpellCastTargets::read and gtker/wow_messages
/// common.wowm SpellCastTargets: u16 mask; packed GUID for UNIT|UNIT_ENEMY; packed GUID for
/// GAMEOBJECT|LOCKED; packed GUID for ITEM|TRADE_ITEM; 3 floats for SOURCE_LOCATION; 3 floats
/// for DEST_LOCATION; CString for STRING; packed GUID for CORPSE_ENEMY|CORPSE_ALLY. vmangos
/// SpellCastTargets::read reads the corpse GUID right after the game object instead —
/// discrepancy recorded in docs/areas/spells.md; cmangos and gtker agree, so their order wins.
/// </para>
/// <para>
/// Write order follows vmangos SpellCastTargets::write: u16 mask; one packed GUID when the mask
/// has UNIT, CORPSE_ENEMY, GAMEOBJECT or CORPSE_ALLY (the unit, else the object, else the corpse,
/// else an empty packed GUID); packed item GUID for ITEM|TRADE_ITEM; source; dest; string.
/// </para>
/// </summary>
public sealed class SpellCastTargets
{
    private const SpellCastTargetFlags UnitGuidFlags = SpellCastTargetFlags.Unit | SpellCastTargetFlags.UnitEnemy;
    private const SpellCastTargetFlags ObjectGuidFlags = SpellCastTargetFlags.GameObject | SpellCastTargetFlags.Locked;
    private const SpellCastTargetFlags ItemGuidFlags = SpellCastTargetFlags.Item | SpellCastTargetFlags.TradeItem;
    private const SpellCastTargetFlags CorpseGuidFlags = SpellCastTargetFlags.CorpseEnemy | SpellCastTargetFlags.CorpseAlly;

    public SpellCastTargetFlags Mask { get; set; }

    public ObjectGuid Unit { get; set; }

    public ObjectGuid GameObject { get; set; }

    public ObjectGuid Item { get; set; }

    public ObjectGuid Corpse { get; set; }

    /// <summary>Set only by the server trade resolver after validating slot 6; never populated by wire parsing.</summary>
    public Item? ServerValidatedTradeItem { get; set; }

    public bool IsRawNonTradedTradeTarget
        => Mask == SpellCastTargetFlags.TradeItem && Item.Value == 6;

    public (float X, float Y, float Z) Source { get; set; }

    public (float X, float Y, float Z) Dest { get; set; }

    public string? Text { get; set; }

    public bool HasDest => (Mask & SpellCastTargetFlags.DestLocation) != 0;

    /// <summary>A self-cast target block (mask 0: the caster is the target).</summary>
    public static SpellCastTargets ForSelf() => new();

    /// <summary>A single-unit target block.</summary>
    public static SpellCastTargets ForUnit(ObjectGuid unit) => new() { Mask = SpellCastTargetFlags.Unit, Unit = unit };

    public static SpellCastTargets Read(ref PacketReader reader)
    {
        var targets = new SpellCastTargets { Mask = (SpellCastTargetFlags)reader.ReadUInt16() };
        SpellCastTargetFlags mask = targets.Mask;
        if (mask == SpellCastTargetFlags.Self)
        {
            return targets;
        }

        if ((mask & UnitGuidFlags) != 0)
        {
            targets.Unit = new ObjectGuid(reader.ReadPackedGuid());
        }

        if ((mask & ObjectGuidFlags) != 0)
        {
            targets.GameObject = new ObjectGuid(reader.ReadPackedGuid());
        }

        if ((mask & ItemGuidFlags) != 0)
        {
            targets.Item = new ObjectGuid(reader.ReadPackedGuid());
        }

        if ((mask & SpellCastTargetFlags.SourceLocation) != 0)
        {
            targets.Source = (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        if ((mask & SpellCastTargetFlags.DestLocation) != 0)
        {
            targets.Dest = (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        if ((mask & SpellCastTargetFlags.String) != 0)
        {
            targets.Text = reader.ReadCString();
        }

        if ((mask & CorpseGuidFlags) != 0)
        {
            targets.Corpse = new ObjectGuid(reader.ReadPackedGuid());
        }

        return targets;
    }

    public void Write(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt16((ushort)Mask);
        if ((Mask & (SpellCastTargetFlags.Unit | CorpseGuidFlags | SpellCastTargetFlags.GameObject)) != 0)
        {
            ObjectGuid guid = (Mask & SpellCastTargetFlags.Unit) != 0 ? Unit
                : (Mask & SpellCastTargetFlags.GameObject) != 0 ? GameObject
                : Corpse;
            writer.WritePackedGuid(guid.Value);
        }

        if ((Mask & ItemGuidFlags) != 0)
        {
            writer.WritePackedGuid(Item.Value);
        }

        if ((Mask & SpellCastTargetFlags.SourceLocation) != 0)
        {
            writer.WriteSingle(Source.X);
            writer.WriteSingle(Source.Y);
            writer.WriteSingle(Source.Z);
        }

        if ((Mask & SpellCastTargetFlags.DestLocation) != 0)
        {
            writer.WriteSingle(Dest.X);
            writer.WriteSingle(Dest.Y);
            writer.WriteSingle(Dest.Z);
        }

        if ((Mask & SpellCastTargetFlags.String) != 0)
        {
            writer.WriteCString(Text ?? string.Empty);
        }
    }
}
