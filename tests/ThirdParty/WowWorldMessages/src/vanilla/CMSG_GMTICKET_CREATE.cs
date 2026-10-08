using WowSrp.Header;
using WowWorldMessages.All;

namespace WowWorldMessages.Vanilla;

using GmTicketTypeType = OneOf.OneOf<CMSG_GMTICKET_CREATE.GmTicketTypeBehaviorHarassment, GmTicketType>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GMTICKET_CREATE: VanillaClientMessage, IWorldMessage {
    public class GmTicketTypeBehaviorHarassment {
        public required uint ChatDataLineCount { get; set; }
        public required List<byte> CompressedChatData { get; set; }
    }
    public required GmTicketTypeType Category { get; set; }
    internal GmTicketType CategoryValue => Category.Match(
        _ => Vanilla.GmTicketType.BehaviorHarassment,
        v => v
    );
    public required Vanilla.Map Map { get; set; }
    public required Vector3d Position { get; set; }
    public required string Message { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Pre-TBC: 'Reserved for future use'
    /// cmangos/vmangos/mangoszero: Unused
    /// </summary>
    public required string ReservedForFutureUse { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)CategoryValue, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Map, cancellationToken).ConfigureAwait(false);

        await Position.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Message, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(ReservedForFutureUse, cancellationToken).ConfigureAwait(false);

        if (Category.Value is CMSG_GMTICKET_CREATE.GmTicketTypeBehaviorHarassment gmTicketTypeBehaviorHarassment) {
            await w.WriteUInt(gmTicketTypeBehaviorHarassment.ChatDataLineCount, cancellationToken).ConfigureAwait(false);

            if (gmTicketTypeBehaviorHarassment.CompressedChatData.Count != 0) {
                var oldStream = w;
                w = new MemoryStream();
                foreach (var v in gmTicketTypeBehaviorHarassment.CompressedChatData) {
                    await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
                }
                var uncompressedLength = w.Position;

                var compressedOutput = new MemoryStream();
                var zlib = new System.IO.Compression.ZLibStream(compressedOutput, System.IO.Compression.CompressionMode.Compress);
                zlib.Write((w as MemoryStream)!.ToArray());
                zlib.Flush();

                w = oldStream;
                await w.WriteUInt((uint)uncompressedLength, cancellationToken).ConfigureAwait(false);
                await w.WriteAsync(compressedOutput.ToArray(), cancellationToken).ConfigureAwait(false);
            }
            else {
                await w.WriteUInt(0, cancellationToken).ConfigureAwait(false);
            }

        }

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 517, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 517, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GMTICKET_CREATE> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        GmTicketTypeType category = (Vanilla.GmTicketType)await r.ReadByte(cancellationToken).ConfigureAwait(false);
        __size += 1;

        var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        var position = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);
        __size += 12;

        var message = await r.ReadCString(cancellationToken).ConfigureAwait(false);
        __size += message.Length + 1;

        var reservedForFutureUse = await r.ReadCString(cancellationToken).ConfigureAwait(false);
        __size += reservedForFutureUse.Length + 1;

        if (category.Value is Vanilla.GmTicketType.BehaviorHarassment) {
            var chatDataLineCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var decompressedLength = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var decompressed = new byte[decompressedLength];
            var remaining = new byte[bodySize - __size];
            await r.ReadExactlyAsync(remaining, cancellationToken).ConfigureAwait(false);

            var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(remaining), System.IO.Compression.CompressionMode.Decompress);
            zlib.ReadAtLeast(decompressed, remaining.Length);

            r = new MemoryStream(decompressed);
            var compressedChatData = new List<byte>();
            while (r.Position < r.Length) {
                compressedChatData.Add(await r.ReadByte(cancellationToken).ConfigureAwait(false));
                __size += 1;
            }

            category = new GmTicketTypeBehaviorHarassment {
                ChatDataLineCount = chatDataLineCount,
                CompressedChatData = compressedChatData,
            };
        }

        return new CMSG_GMTICKET_CREATE {
            Category = category,
            Map = map,
            Position = position,
            Message = message,
            ReservedForFutureUse = reservedForFutureUse,
        };
    }

    internal int Size() {
        var memory = new MemoryStream();
        Task.WaitAll(WriteBodyAsync(memory));
        return (int)memory.Position;
    }

}

