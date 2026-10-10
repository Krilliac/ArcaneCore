using ArcaneCore.Game.Versioning;
using ArcaneCore.Kernel;
using ArcaneCore.Protocol;
using ArcaneCore.Protocol.Versioning;

namespace ArcaneCore.World.Net.Versioning;

/// <summary>
/// WoW 1.12.1 (build 5875): the protocol ArcaneCore has always spoken, wrapped without change. The opcode map is the
/// identity, the cipher is <see cref="WorldHeaderCrypt"/>, and the auth formats are the ones WorldSession wrote inline
/// before S0. tests/ArcaneCore.MockClient.Tests/Replay pins its bytes.
/// </summary>
public sealed class Build5875Protocol : IClientProtocol
{
    public static readonly Build5875Protocol Instance = new();

    private Build5875Protocol()
    {
    }

    public ushort Build => ClientBuild.Vanilla1121;

    public IWorldAuthCodec Auth => Vanilla5875AuthCodec.Instance;

    public IOpcodeMap Opcodes => IdentityOpcodeMap.Instance;

    public IUpdateFieldLayout UpdateFields => Build5875UpdateFieldLayout.Instance;

    public IWorldHeaderCrypt CreateHeaderCrypt() => new WorldHeaderCrypt();

    private sealed class Vanilla5875AuthCodec : IWorldAuthCodec
    {
        internal static readonly Vanilla5875AuthCodec Instance = new();

        public bool TryParseAuthSession(ReadOnlyMemory<byte> payload, out AuthSessionRequest request)
            => AuthSessionRequest.TryParse(payload, out request);

        /// <summary>
        /// SMSG_AUTH_RESPONSE. AUTH_OK carries u32 billing_time, u8 billing_flags, u32 billing_rested after the result
        /// (wow_messages smsg_auth_response.wowm; vmangos World.cpp:324-333; mangos-classic WorldSession::SendAuthOk), all
        /// zero here; every failure code is the bare result byte.
        /// </summary>
        public byte[] AuthResponse(AuthResponseCode code)
            => code == AuthResponseCode.Ok ? new byte[10] { (byte)code, 0, 0, 0, 0, 0, 0, 0, 0, 0 } : [(byte)code];
    }
}
