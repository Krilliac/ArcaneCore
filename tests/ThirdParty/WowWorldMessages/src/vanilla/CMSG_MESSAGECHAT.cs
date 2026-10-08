using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using ChatTypeType = OneOf.OneOf<CMSG_MESSAGECHAT.ChatTypeChannel, CMSG_MESSAGECHAT.ChatTypeWhisper, ChatType>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_MESSAGECHAT: VanillaClientMessage, IWorldMessage {
    public class ChatTypeChannel {
        public required string Channel { get; set; }
    }
    public class ChatTypeWhisper {
        public required string TargetPlayer { get; set; }
    }
    public required ChatTypeType ChatType { get; set; }
    internal ChatType ChatTypeValue => ChatType.Match(
        _ => Vanilla.ChatType.Channel,
        _ => Vanilla.ChatType.Whisper,
        v => v
    );
    public required Vanilla.Language Language { get; set; }
    public required string Message { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)ChatTypeValue, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Language, cancellationToken).ConfigureAwait(false);

        if (ChatType.Value is CMSG_MESSAGECHAT.ChatTypeWhisper chatTypeWhisper) {
            await w.WriteCString(chatTypeWhisper.TargetPlayer, cancellationToken).ConfigureAwait(false);

        }
        else if (ChatType.Value is CMSG_MESSAGECHAT.ChatTypeChannel chatTypeChannel) {
            await w.WriteCString(chatTypeChannel.Channel, cancellationToken).ConfigureAwait(false);

        }

        await w.WriteCString(Message, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 149, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 149, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_MESSAGECHAT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        ChatTypeType chatType = (Vanilla.ChatType)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var language = (Vanilla.Language)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        if (chatType.Value is Vanilla.ChatType.Whisper) {
            var targetPlayer = await r.ReadCString(cancellationToken).ConfigureAwait(false);

            chatType = new ChatTypeWhisper {
                TargetPlayer = targetPlayer,
            };
        }
        else if (chatType.Value is Vanilla.ChatType.Channel) {
            var channel = await r.ReadCString(cancellationToken).ConfigureAwait(false);

            chatType = new ChatTypeChannel {
                Channel = channel,
            };
        }

        var message = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_MESSAGECHAT {
            ChatType = chatType,
            Language = language,
            Message = message,
        };
    }

    internal int Size() {
        var size = 0;

        // chat_type: Generator.Generated.DataTypeEnum
        size += 4;

        // language: Generator.Generated.DataTypeEnum
        size += 4;

        if (ChatType.Value is CMSG_MESSAGECHAT.ChatTypeWhisper chatTypeWhisper) {
            // target_player: Generator.Generated.DataTypeCstring
            size += chatTypeWhisper.TargetPlayer.Length + 1;

        }
        else if (ChatType.Value is CMSG_MESSAGECHAT.ChatTypeChannel chatTypeChannel) {
            // channel: Generator.Generated.DataTypeCstring
            size += chatTypeChannel.Channel.Length + 1;

        }

        // message: Generator.Generated.DataTypeCstring
        size += Message.Length + 1;

        return size;
    }

}

