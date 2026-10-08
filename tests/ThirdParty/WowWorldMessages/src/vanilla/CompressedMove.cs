using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using CompressedMoveOpcodeType = OneOf.OneOf<CompressedMove.CompressedMoveOpcodeSmsgMonsterMove, CompressedMove.CompressedMoveOpcodeSmsgMonsterMoveTransport, CompressedMove.CompressedMoveOpcodeSmsgSplineSetRunSpeed, CompressedMoveOpcode>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class CompressedMove {
    public class CompressedMoveOpcodeSmsgMonsterMove {
        public required MonsterMove MonsterMove { get; set; }
    }
    public class CompressedMoveOpcodeSmsgMonsterMoveTransport {
        public required MonsterMove MonsterMoveTransport { get; set; }
        public required ulong Transport { get; set; }
    }
    public class CompressedMoveOpcodeSmsgSplineSetRunSpeed {
        public required float Speed { get; set; }
    }
    public required CompressedMoveOpcodeType Opcode { get; set; }
    internal CompressedMoveOpcode OpcodeValue => Opcode.Match(
        _ => Vanilla.CompressedMoveOpcode.SmsgMonsterMove,
        _ => Vanilla.CompressedMoveOpcode.SmsgMonsterMoveTransport,
        _ => Vanilla.CompressedMoveOpcode.SmsgSplineSetRunSpeed,
        v => v
    );
    public required ulong Guid { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Size(), cancellationToken).ConfigureAwait(false);

        await w.WriteUShort((ushort)OpcodeValue, cancellationToken).ConfigureAwait(false);

        await w.WritePackedGuid(Guid, cancellationToken).ConfigureAwait(false);

        if (Opcode.Value is CompressedMove.CompressedMoveOpcodeSmsgSplineSetRunSpeed compressedMoveOpcodeSmsgSplineSetRunSpeed) {
            await w.WriteFloat(compressedMoveOpcodeSmsgSplineSetRunSpeed.Speed, cancellationToken).ConfigureAwait(false);

        }
        else if (Opcode.Value is CompressedMove.CompressedMoveOpcodeSmsgMonsterMove compressedMoveOpcodeSmsgMonsterMove) {
            await compressedMoveOpcodeSmsgMonsterMove.MonsterMove.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        }
        else if (Opcode.Value is CompressedMove.CompressedMoveOpcodeSmsgMonsterMoveTransport compressedMoveOpcodeSmsgMonsterMoveTransport) {
            await w.WritePackedGuid(compressedMoveOpcodeSmsgMonsterMoveTransport.Transport, cancellationToken).ConfigureAwait(false);

            await compressedMoveOpcodeSmsgMonsterMoveTransport.MonsterMoveTransport.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        }

    }

    public static async Task<CompressedMove> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var size = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        CompressedMoveOpcodeType opcode = (Vanilla.CompressedMoveOpcode)await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var guid = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        if (opcode.Value is Vanilla.CompressedMoveOpcode.SmsgSplineSetRunSpeed) {
            var speed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            opcode = new CompressedMoveOpcodeSmsgSplineSetRunSpeed {
                Speed = speed,
            };
        }
        else if (opcode.Value is Vanilla.CompressedMoveOpcode.SmsgMonsterMove) {
            var monsterMove = await MonsterMove.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            opcode = new CompressedMoveOpcodeSmsgMonsterMove {
                MonsterMove = monsterMove,
            };
        }
        else if (opcode.Value is Vanilla.CompressedMoveOpcode.SmsgMonsterMoveTransport) {
            var transport = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            var monsterMoveTransport = await MonsterMove.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            opcode = new CompressedMoveOpcodeSmsgMonsterMoveTransport {
                MonsterMoveTransport = monsterMoveTransport,
                Transport = transport,
            };
        }

        return new CompressedMove {
            Opcode = opcode,
            Guid = guid,
        };
    }

    internal int Size() {
        var size = 0;

        // size: Generator.Generated.DataTypeInteger
        size += 1;

        // opcode: Generator.Generated.DataTypeEnum
        size += 2;

        // guid: Generator.Generated.DataTypePackedGuid
        size += Guid.PackedGuidLength();

        if (Opcode.Value is CompressedMove.CompressedMoveOpcodeSmsgSplineSetRunSpeed compressedMoveOpcodeSmsgSplineSetRunSpeed) {
            // speed: Generator.Generated.DataTypeFloatingPoint
            size += 4;

        }
        else if (Opcode.Value is CompressedMove.CompressedMoveOpcodeSmsgMonsterMove compressedMoveOpcodeSmsgMonsterMove) {
            // monster_move: Generator.Generated.DataTypeStruct
            size += compressedMoveOpcodeSmsgMonsterMove.MonsterMove.Size();

        }
        else if (Opcode.Value is CompressedMove.CompressedMoveOpcodeSmsgMonsterMoveTransport compressedMoveOpcodeSmsgMonsterMoveTransport) {
            // transport: Generator.Generated.DataTypePackedGuid
            size += compressedMoveOpcodeSmsgMonsterMoveTransport.Transport.PackedGuidLength();

            // monster_move_transport: Generator.Generated.DataTypeStruct
            size += compressedMoveOpcodeSmsgMonsterMoveTransport.MonsterMoveTransport.Size();

        }

        return size;
    }

}

