using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using UpdateTypeType = OneOf.OneOf<Object.UpdateTypeCreateObject, Object.UpdateTypeCreateObject2, Object.UpdateTypeMovement, Object.UpdateTypeNearObjects, Object.UpdateTypeOutOfRangeObjects, Object.UpdateTypeValues, UpdateType>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class Object {
    public class UpdateTypeCreateObject {
        public required ulong Guid3 { get; set; }
        public required UpdateMask Mask2 { get; set; }
        public required MovementBlock Movement2 { get; set; }
        public required Vanilla.ObjectType ObjectType { get; set; }
    }
    public class UpdateTypeCreateObject2 {
        public required ulong Guid3 { get; set; }
        public required UpdateMask Mask2 { get; set; }
        public required MovementBlock Movement2 { get; set; }
        public required Vanilla.ObjectType ObjectType { get; set; }
    }
    public class UpdateTypeMovement {
        public required ulong Guid2 { get; set; }
        public required MovementBlock Movement1 { get; set; }
    }
    public class UpdateTypeNearObjects {
        public required List<ulong> Guids { get; set; }
    }
    public class UpdateTypeOutOfRangeObjects {
        public required List<ulong> Guids { get; set; }
    }
    public class UpdateTypeValues {
        public required ulong Guid1 { get; set; }
        public required UpdateMask Mask1 { get; set; }
    }
    public required UpdateTypeType UpdateType { get; set; }
    internal UpdateType UpdateTypeValue => UpdateType.Match(
        _ => Vanilla.UpdateType.CreateObject,
        _ => Vanilla.UpdateType.CreateObject2,
        _ => Vanilla.UpdateType.Movement,
        _ => Vanilla.UpdateType.NearObjects,
        _ => Vanilla.UpdateType.OutOfRangeObjects,
        _ => Vanilla.UpdateType.Values,
        v => v
    );

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)UpdateTypeValue, cancellationToken).ConfigureAwait(false);

        if (UpdateType.Value is Object.UpdateTypeValues updateTypeValues) {
            await w.WritePackedGuid(updateTypeValues.Guid1, cancellationToken).ConfigureAwait(false);

            await updateTypeValues.Mask1.WriteAsync(w, cancellationToken).ConfigureAwait(false);

        }
        else if (UpdateType.Value is Object.UpdateTypeMovement updateTypeMovement) {
            await w.WritePackedGuid(updateTypeMovement.Guid2, cancellationToken).ConfigureAwait(false);

            await updateTypeMovement.Movement1.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        }
        else if (UpdateType.Value is Object.UpdateTypeCreateObject updateTypeCreateObject) {
            await w.WritePackedGuid(updateTypeCreateObject.Guid3, cancellationToken).ConfigureAwait(false);

            await w.WriteByte((byte)updateTypeCreateObject.ObjectType, cancellationToken).ConfigureAwait(false);

            await updateTypeCreateObject.Movement2.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

            await updateTypeCreateObject.Mask2.WriteAsync(w, cancellationToken).ConfigureAwait(false);

        }
        else if (UpdateType.Value is Object.UpdateTypeCreateObject2 updateTypeCreateObject2) {
            await w.WritePackedGuid(updateTypeCreateObject2.Guid3, cancellationToken).ConfigureAwait(false);

            await w.WriteByte((byte)updateTypeCreateObject2.ObjectType, cancellationToken).ConfigureAwait(false);

            await updateTypeCreateObject2.Movement2.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

            await updateTypeCreateObject2.Mask2.WriteAsync(w, cancellationToken).ConfigureAwait(false);

        }
        else if (UpdateType.Value is Object.UpdateTypeOutOfRangeObjects updateTypeOutOfRangeObjects) {
            await w.WriteUInt((uint)updateTypeOutOfRangeObjects.Guids.Count, cancellationToken).ConfigureAwait(false);

            foreach (var v in updateTypeOutOfRangeObjects.Guids) {
                await w.WritePackedGuid(v, cancellationToken).ConfigureAwait(false);
            }

        }
        else if (UpdateType.Value is Object.UpdateTypeNearObjects updateTypeNearObjects) {
            await w.WriteUInt((uint)updateTypeNearObjects.Guids.Count, cancellationToken).ConfigureAwait(false);

            foreach (var v in updateTypeNearObjects.Guids) {
                await w.WritePackedGuid(v, cancellationToken).ConfigureAwait(false);
            }

        }

    }

    public static async Task<Object> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        UpdateTypeType updateType = (Vanilla.UpdateType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (updateType.Value is Vanilla.UpdateType.Values) {
            var guid1 = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            var mask1 = await UpdateMask.ReadAsync(r, cancellationToken).ConfigureAwait(false);

            updateType = new UpdateTypeValues {
                Guid1 = guid1,
                Mask1 = mask1,
            };
        }
        else if (updateType.Value is Vanilla.UpdateType.Movement) {
            var guid2 = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            var movement1 = await MovementBlock.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            updateType = new UpdateTypeMovement {
                Guid2 = guid2,
                Movement1 = movement1,
            };
        }
        else if (updateType.Value is Vanilla.UpdateType.CreateObject) {
            var guid3 = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            var objectType = (Vanilla.ObjectType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

            var movement2 = await MovementBlock.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            var mask2 = await UpdateMask.ReadAsync(r, cancellationToken).ConfigureAwait(false);

            updateType = new UpdateTypeCreateObject {
                Guid3 = guid3,
                Mask2 = mask2,
                Movement2 = movement2,
                ObjectType = objectType,
            };
        }
        else if (updateType.Value is Vanilla.UpdateType.CreateObject2) {
            var guid3 = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            var objectType = (Vanilla.ObjectType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

            var movement2 = await MovementBlock.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            var mask2 = await UpdateMask.ReadAsync(r, cancellationToken).ConfigureAwait(false);

            updateType = new UpdateTypeCreateObject2 {
                Guid3 = guid3,
                Mask2 = mask2,
                Movement2 = movement2,
                ObjectType = objectType,
            };
        }
        else if (updateType.Value is Vanilla.UpdateType.OutOfRangeObjects) {
            // ReSharper disable once UnusedVariable.Compiler
            var count = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var guids = new List<ulong>();
            for (var i = 0; i < count; ++i) {
                guids.Add(await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false));
            }

            updateType = new UpdateTypeOutOfRangeObjects {
                Guids = guids,
            };
        }
        else if (updateType.Value is Vanilla.UpdateType.NearObjects) {
            // ReSharper disable once UnusedVariable.Compiler
            var count = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var guids = new List<ulong>();
            for (var i = 0; i < count; ++i) {
                guids.Add(await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false));
            }

            updateType = new UpdateTypeNearObjects {
                Guids = guids,
            };
        }

        return new Object {
            UpdateType = updateType,
        };
    }

    internal int Size() {
        var size = 0;

        // update_type: Generator.Generated.DataTypeEnum
        size += 1;

        if (UpdateType.Value is Object.UpdateTypeValues updateTypeValues) {
            // guid1: Generator.Generated.DataTypePackedGuid
            size += updateTypeValues.Guid1.PackedGuidLength();

            // mask1: Generator.Generated.DataTypeUpdateMask
            size += updateTypeValues.Mask1.Length();

        }
        else if (UpdateType.Value is Object.UpdateTypeMovement updateTypeMovement) {
            // guid2: Generator.Generated.DataTypePackedGuid
            size += updateTypeMovement.Guid2.PackedGuidLength();

            // movement1: Generator.Generated.DataTypeStruct
            size += updateTypeMovement.Movement1.Size();

        }
        else if (UpdateType.Value is Object.UpdateTypeCreateObject updateTypeCreateObject) {
            // guid3: Generator.Generated.DataTypePackedGuid
            size += updateTypeCreateObject.Guid3.PackedGuidLength();

            // object_type: Generator.Generated.DataTypeEnum
            size += 1;

            // movement2: Generator.Generated.DataTypeStruct
            size += updateTypeCreateObject.Movement2.Size();

            // mask2: Generator.Generated.DataTypeUpdateMask
            size += updateTypeCreateObject.Mask2.Length();

        }
        else if (UpdateType.Value is Object.UpdateTypeCreateObject2 updateTypeCreateObject2) {
            // guid3: Generator.Generated.DataTypePackedGuid
            size += updateTypeCreateObject2.Guid3.PackedGuidLength();

            // object_type: Generator.Generated.DataTypeEnum
            size += 1;

            // movement2: Generator.Generated.DataTypeStruct
            size += updateTypeCreateObject2.Movement2.Size();

            // mask2: Generator.Generated.DataTypeUpdateMask
            size += updateTypeCreateObject2.Mask2.Length();

        }
        else if (UpdateType.Value is Object.UpdateTypeOutOfRangeObjects updateTypeOutOfRangeObjects) {
            // count: Generator.Generated.DataTypeInteger
            size += 4;

            // guids: Generator.Generated.DataTypeArray
            size += updateTypeOutOfRangeObjects.Guids.Sum(e => e.PackedGuidLength());

        }
        else if (UpdateType.Value is Object.UpdateTypeNearObjects updateTypeNearObjects) {
            // count: Generator.Generated.DataTypeInteger
            size += 4;

            // guids: Generator.Generated.DataTypeArray
            size += updateTypeNearObjects.Guids.Sum(e => e.PackedGuidLength());

        }

        return size;
    }

}

