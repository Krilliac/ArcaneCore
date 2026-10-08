using WowSrp.Header;
using WowWorldMessages.All;

namespace WowWorldMessages.Vanilla;

using SpellCastTargetFlagsCorpseMulti = OneOf.OneOf<SpellCastTargets.SpellCastTargetFlagsCorpse, SpellCastTargets.SpellCastTargetFlagsPvpCorpse, SpellCastTargetFlags>;
using SpellCastTargetFlagsGameobjectMulti = OneOf.OneOf<SpellCastTargets.SpellCastTargetFlagsGameobject, SpellCastTargets.SpellCastTargetFlagsObjectUnk, SpellCastTargetFlags>;
using SpellCastTargetFlagsItemMulti = OneOf.OneOf<SpellCastTargets.SpellCastTargetFlagsItem, SpellCastTargets.SpellCastTargetFlagsTradeItem, SpellCastTargetFlags>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class SpellCastTargets {
    public class SpellCastTargetFlagsCorpse {
        public required ulong Corpse { get; set; }
    }
    public class SpellCastTargetFlagsPvpCorpse {
        public required ulong PvpCorpse { get; set; }
    }
    public class SpellCastTargetFlagsGameobject {
        public required ulong Gameobject { get; set; }
    }
    public class SpellCastTargetFlagsObjectUnk {
        public required ulong ObjectUnk { get; set; }
    }
    public class SpellCastTargetFlagsItem {
        public required ulong Item { get; set; }
    }
    public class SpellCastTargetFlagsTradeItem {
        public required ulong TradeItem { get; set; }
    }
    public class SpellCastTargetFlagsType {
        public required SpellCastTargetFlags Inner;
        public SpellCastTargetFlagsCorpseMulti Corpse;
        public SpellCastTargetFlagsDestLocation? DestLocation;
        public SpellCastTargetFlagsGameobjectMulti Gameobject;
        public SpellCastTargetFlagsItemMulti Item;
        public SpellCastTargetFlagsSourceLocation? SourceLocation;
        public SpellCastTargetFlagsString? String;
        public SpellCastTargetFlagsUnit? Unit;
    }
    public class SpellCastTargetFlagsDestLocation {
        public required Vector3d Destination { get; set; }
    }
    public class SpellCastTargetFlagsSourceLocation {
        public required Vector3d Source { get; set; }
    }
    public class SpellCastTargetFlagsString {
        public required string TargetString { get; set; }
    }
    public class SpellCastTargetFlagsUnit {
        public required ulong UnitTarget { get; set; }
    }
    public required SpellCastTargetFlagsType TargetFlags { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUShort((ushort)TargetFlags.Inner, cancellationToken).ConfigureAwait(false);

        if (TargetFlags.Unit is SpellCastTargetFlagsUnit spellCastTargetFlagsUnit) {
            await w.WritePackedGuid(spellCastTargetFlagsUnit.UnitTarget, cancellationToken).ConfigureAwait(false);

        }

        if (TargetFlags.Gameobject.Value is SpellCastTargetFlagsGameobject spellCastTargetFlagsGameobject) {
            await w.WritePackedGuid(spellCastTargetFlagsGameobject.Gameobject, cancellationToken).ConfigureAwait(false);

        }
        else if (TargetFlags.Gameobject.Value is SpellCastTargetFlagsObjectUnk spellCastTargetFlagsObjectUnk) {
            await w.WritePackedGuid(spellCastTargetFlagsObjectUnk.ObjectUnk, cancellationToken).ConfigureAwait(false);

        }

        if (TargetFlags.Item.Value is SpellCastTargetFlagsItem spellCastTargetFlagsItem) {
            await w.WritePackedGuid(spellCastTargetFlagsItem.Item, cancellationToken).ConfigureAwait(false);

        }
        else if (TargetFlags.Item.Value is SpellCastTargetFlagsTradeItem spellCastTargetFlagsTradeItem) {
            await w.WritePackedGuid(spellCastTargetFlagsTradeItem.TradeItem, cancellationToken).ConfigureAwait(false);

        }

        if (TargetFlags.SourceLocation is SpellCastTargetFlagsSourceLocation spellCastTargetFlagsSourceLocation) {
            await spellCastTargetFlagsSourceLocation.Source.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        }

        if (TargetFlags.DestLocation is SpellCastTargetFlagsDestLocation spellCastTargetFlagsDestLocation) {
            await spellCastTargetFlagsDestLocation.Destination.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        }

        if (TargetFlags.String is SpellCastTargetFlagsString spellCastTargetFlagsString) {
            await w.WriteCString(spellCastTargetFlagsString.TargetString, cancellationToken).ConfigureAwait(false);

        }

        if (TargetFlags.Corpse.Value is SpellCastTargetFlagsCorpse spellCastTargetFlagsCorpse) {
            await w.WritePackedGuid(spellCastTargetFlagsCorpse.Corpse, cancellationToken).ConfigureAwait(false);

        }
        else if (TargetFlags.Corpse.Value is SpellCastTargetFlagsPvpCorpse spellCastTargetFlagsPvpCorpse) {
            await w.WritePackedGuid(spellCastTargetFlagsPvpCorpse.PvpCorpse, cancellationToken).ConfigureAwait(false);

        }

    }

    public static async Task<SpellCastTargets> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var targetFlags = new SpellCastTargetFlagsType {
            Inner = (SpellCastTargetFlags)await r.ReadUShort(cancellationToken).ConfigureAwait(false),
        };

        if (targetFlags.Inner.HasFlag(Vanilla.SpellCastTargetFlags.Unit)) {
            var unitTarget = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            targetFlags.Unit = new SpellCastTargetFlagsUnit {
                UnitTarget = unitTarget,
            };
        }

        if (targetFlags.Inner.HasFlag(Vanilla.SpellCastTargetFlags.Gameobject)) {
            var gameobject = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            targetFlags.Gameobject = new SpellCastTargetFlagsGameobject {
                Gameobject = gameobject,
            };
        }
        else if (targetFlags.Inner.HasFlag(Vanilla.SpellCastTargetFlags.ObjectUnk)) {
            var objectUnk = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            targetFlags.Gameobject = new SpellCastTargetFlagsObjectUnk {
                ObjectUnk = objectUnk,
            };
        }

        if (targetFlags.Inner.HasFlag(Vanilla.SpellCastTargetFlags.Item)) {
            var item = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            targetFlags.Item = new SpellCastTargetFlagsItem {
                Item = item,
            };
        }
        else if (targetFlags.Inner.HasFlag(Vanilla.SpellCastTargetFlags.TradeItem)) {
            var tradeItem = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            targetFlags.Item = new SpellCastTargetFlagsTradeItem {
                TradeItem = tradeItem,
            };
        }

        if (targetFlags.Inner.HasFlag(Vanilla.SpellCastTargetFlags.SourceLocation)) {
            var source = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            targetFlags.SourceLocation = new SpellCastTargetFlagsSourceLocation {
                Source = source,
            };
        }

        if (targetFlags.Inner.HasFlag(Vanilla.SpellCastTargetFlags.DestLocation)) {
            var destination = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            targetFlags.DestLocation = new SpellCastTargetFlagsDestLocation {
                Destination = destination,
            };
        }

        if (targetFlags.Inner.HasFlag(Vanilla.SpellCastTargetFlags.String)) {
            var targetString = await r.ReadCString(cancellationToken).ConfigureAwait(false);

            targetFlags.String = new SpellCastTargetFlagsString {
                TargetString = targetString,
            };
        }

        if (targetFlags.Inner.HasFlag(Vanilla.SpellCastTargetFlags.Corpse)) {
            var corpse = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            targetFlags.Corpse = new SpellCastTargetFlagsCorpse {
                Corpse = corpse,
            };
        }
        else if (targetFlags.Inner.HasFlag(Vanilla.SpellCastTargetFlags.PvpCorpse)) {
            var pvpCorpse = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            targetFlags.Corpse = new SpellCastTargetFlagsPvpCorpse {
                PvpCorpse = pvpCorpse,
            };
        }

        return new SpellCastTargets {
            TargetFlags = targetFlags,
        };
    }

    internal int Size() {
        var size = 0;

        // target_flags: Generator.Generated.DataTypeFlag
        size += 2;

        if (TargetFlags.Unit is SpellCastTargetFlagsUnit spellCastTargetFlagsUnit) {
            // unit_target: Generator.Generated.DataTypePackedGuid
            size += spellCastTargetFlagsUnit.UnitTarget.PackedGuidLength();

        }

        if (TargetFlags.Gameobject.Value is SpellCastTargetFlagsGameobject spellCastTargetFlagsGameobject) {
            // gameobject: Generator.Generated.DataTypePackedGuid
            size += spellCastTargetFlagsGameobject.Gameobject.PackedGuidLength();

        }
        else if (TargetFlags.Gameobject.Value is SpellCastTargetFlagsObjectUnk spellCastTargetFlagsObjectUnk) {
            // object_unk: Generator.Generated.DataTypePackedGuid
            size += spellCastTargetFlagsObjectUnk.ObjectUnk.PackedGuidLength();

        }

        if (TargetFlags.Item.Value is SpellCastTargetFlagsItem spellCastTargetFlagsItem) {
            // item: Generator.Generated.DataTypePackedGuid
            size += spellCastTargetFlagsItem.Item.PackedGuidLength();

        }
        else if (TargetFlags.Item.Value is SpellCastTargetFlagsTradeItem spellCastTargetFlagsTradeItem) {
            // trade_item: Generator.Generated.DataTypePackedGuid
            size += spellCastTargetFlagsTradeItem.TradeItem.PackedGuidLength();

        }

        if (TargetFlags.SourceLocation is SpellCastTargetFlagsSourceLocation spellCastTargetFlagsSourceLocation) {
            // source: Generator.Generated.DataTypeStruct
            size += 12;

        }

        if (TargetFlags.DestLocation is SpellCastTargetFlagsDestLocation spellCastTargetFlagsDestLocation) {
            // destination: Generator.Generated.DataTypeStruct
            size += 12;

        }

        if (TargetFlags.String is SpellCastTargetFlagsString spellCastTargetFlagsString) {
            // target_string: Generator.Generated.DataTypeCstring
            size += spellCastTargetFlagsString.TargetString.Length + 1;

        }

        if (TargetFlags.Corpse.Value is SpellCastTargetFlagsCorpse spellCastTargetFlagsCorpse) {
            // corpse: Generator.Generated.DataTypePackedGuid
            size += spellCastTargetFlagsCorpse.Corpse.PackedGuidLength();

        }
        else if (TargetFlags.Corpse.Value is SpellCastTargetFlagsPvpCorpse spellCastTargetFlagsPvpCorpse) {
            // pvp_corpse: Generator.Generated.DataTypePackedGuid
            size += spellCastTargetFlagsPvpCorpse.PvpCorpse.PackedGuidLength();

        }

        return size;
    }

}

