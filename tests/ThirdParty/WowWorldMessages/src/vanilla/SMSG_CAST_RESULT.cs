using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using SimpleSpellCastResultType = OneOf.OneOf<SMSG_CAST_RESULT.SimpleSpellCastResultSuccess, SimpleSpellCastResult>;
using CastFailureReasonType = OneOf.OneOf<SMSG_CAST_RESULT.CastFailureReasonEquippedItemClass, SMSG_CAST_RESULT.CastFailureReasonRequiresArea, SMSG_CAST_RESULT.CastFailureReasonRequiresSpellFocus, CastFailureReason>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_CAST_RESULT: VanillaServerMessage, IWorldMessage {
    public class SimpleSpellCastResultSuccess {
        public required CastFailureReasonType Reason { get; set; }
        internal CastFailureReason ReasonValue => Reason.Match(
            _ => Vanilla.CastFailureReason.EquippedItemClass,
            _ => Vanilla.CastFailureReason.RequiresArea,
            _ => Vanilla.CastFailureReason.RequiresSpellFocus,
            v => v
        );
    }
    public class CastFailureReasonEquippedItemClass {
        public required uint EquippedItemClass { get; set; }
        public required uint EquippedItemInventoryTypeMask { get; set; }
        public required uint EquippedItemSubclassMask { get; set; }
    }
    public class CastFailureReasonRequiresArea {
        public required Vanilla.Area Area { get; set; }
    }
    public class CastFailureReasonRequiresSpellFocus {
        public required uint RequiredSpellFocus { get; set; }
    }
    public required uint Spell { get; set; }
    public required SimpleSpellCastResultType Result { get; set; }
    internal SimpleSpellCastResult ResultValue => Result.Match(
        _ => Vanilla.SimpleSpellCastResult.Success,
        v => v
    );

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Spell, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)ResultValue, cancellationToken).ConfigureAwait(false);

        if (Result.Value is SMSG_CAST_RESULT.SimpleSpellCastResultSuccess simpleSpellCastResultSuccess) {
            await w.WriteByte((byte)simpleSpellCastResultSuccess.ReasonValue, cancellationToken).ConfigureAwait(false);

            if (simpleSpellCastResultSuccess.Reason.Value is SMSG_CAST_RESULT.CastFailureReasonRequiresSpellFocus castFailureReasonRequiresSpellFocus) {
                await w.WriteUInt(castFailureReasonRequiresSpellFocus.RequiredSpellFocus, cancellationToken).ConfigureAwait(false);

            }
            else if (simpleSpellCastResultSuccess.Reason.Value is SMSG_CAST_RESULT.CastFailureReasonRequiresArea castFailureReasonRequiresArea) {
                await w.WriteUInt((uint)castFailureReasonRequiresArea.Area, cancellationToken).ConfigureAwait(false);

            }
            else if (simpleSpellCastResultSuccess.Reason.Value is SMSG_CAST_RESULT.CastFailureReasonEquippedItemClass castFailureReasonEquippedItemClass) {
                await w.WriteUInt(castFailureReasonEquippedItemClass.EquippedItemClass, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(castFailureReasonEquippedItemClass.EquippedItemSubclassMask, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(castFailureReasonEquippedItemClass.EquippedItemInventoryTypeMask, cancellationToken).ConfigureAwait(false);

            }

        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 304, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 304, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_CAST_RESULT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var spell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        SimpleSpellCastResultType result = (Vanilla.SimpleSpellCastResult)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (result.Value is Vanilla.SimpleSpellCastResult.Success) {
            CastFailureReasonType reason = (Vanilla.CastFailureReason)await r.ReadByte(cancellationToken).ConfigureAwait(false);

            if (reason.Value is Vanilla.CastFailureReason.RequiresSpellFocus) {
                var requiredSpellFocus = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                reason = new CastFailureReasonRequiresSpellFocus {
                    RequiredSpellFocus = requiredSpellFocus,
                };
            }
            else if (reason.Value is Vanilla.CastFailureReason.RequiresArea) {
                var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                reason = new CastFailureReasonRequiresArea {
                    Area = area,
                };
            }
            else if (reason.Value is Vanilla.CastFailureReason.EquippedItemClass) {
                var equippedItemClass = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var equippedItemSubclassMask = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var equippedItemInventoryTypeMask = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                reason = new CastFailureReasonEquippedItemClass {
                    EquippedItemClass = equippedItemClass,
                    EquippedItemInventoryTypeMask = equippedItemInventoryTypeMask,
                    EquippedItemSubclassMask = equippedItemSubclassMask,
                };
            }

            result = new SimpleSpellCastResultSuccess {
                Reason = reason,
            };
        }

        return new SMSG_CAST_RESULT {
            Spell = spell,
            Result = result,
        };
    }

    internal int Size() {
        var size = 0;

        // spell: Generator.Generated.DataTypeSpell
        size += 4;

        // result: Generator.Generated.DataTypeEnum
        size += 1;

        if (Result.Value is SMSG_CAST_RESULT.SimpleSpellCastResultSuccess simpleSpellCastResultSuccess) {
            // reason: Generator.Generated.DataTypeEnum
            size += 1;

            if (simpleSpellCastResultSuccess.Reason.Value is SMSG_CAST_RESULT.CastFailureReasonRequiresSpellFocus castFailureReasonRequiresSpellFocus) {
                // required_spell_focus: Generator.Generated.DataTypeInteger
                size += 4;

            }
            else if (simpleSpellCastResultSuccess.Reason.Value is SMSG_CAST_RESULT.CastFailureReasonRequiresArea castFailureReasonRequiresArea) {
                // area: Generator.Generated.DataTypeEnum
                size += 4;

            }
            else if (simpleSpellCastResultSuccess.Reason.Value is SMSG_CAST_RESULT.CastFailureReasonEquippedItemClass castFailureReasonEquippedItemClass) {
                // equipped_item_class: Generator.Generated.DataTypeInteger
                size += 4;

                // equipped_item_subclass_mask: Generator.Generated.DataTypeInteger
                size += 4;

                // equipped_item_inventory_type_mask: Generator.Generated.DataTypeInteger
                size += 4;

            }

        }

        return size;
    }

}

