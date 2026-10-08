using ArcaneCore.Cryptography;
using Xunit;

namespace ArcaneCore.Realm.Tests;

/// <summary>
/// Regression vectors published with gtker/wow_srp_csharp 85ad800 (MIT OR Apache-2.0, see
/// THIRD_PARTY_NOTICES.md): the first rows of WowSrp.Test/tests/pin/regression.txt and
/// tests/integrity/{generic,reconnect}_regression.txt, kept as published.
/// </summary>
public sealed class PinIntegrityVectorTests
{
    // digits, grid seed, server salt, client salt, expected hash.
    [Theory]
    [InlineData("09020100010500080801", 2979784221u, "68968A7F7F6CC8A62E484850F27BE7B4", "DEF2B7BDA60CEB7B01D19E25B94946A8", "5F871E26F5F1E2F49F6CE1FD284A521AFB04F060")]
    [InlineData("020705030803080305", 1693110222u, "376E3E575FD1B2FDB623D3BC31620F2D", "F67AA135DB554E6C5A17344463EA4616", "5F26C2210605F5CD85D18CCD3BE18D10A27E5B51")]
    [InlineData("08000407070609080201", 329475677u, "98C663561D9A21D7D93C39704177C439", "B3E7C9FA94B7329E3F75EF848B16D540", "EE63B67B757F4E0B2A22CFF78E3137A4E9F0AD9D")]
    public void PinVector_ClientHashAndServerVerificationAgree(
        string pin, uint gridSeed, string serverSalt, string clientSalt, string hash)
    {
        byte[] digits = UpstreamBytes(pin, padding: false);
        byte[] server = UpstreamBytes(serverSalt);
        byte[] client = UpstreamBytes(clientSalt);
        byte[] expected = UpstreamBytes(hash);

        // Client side: what the MockClient sends. Server side: what LogonSession checks.
        Assert.Equal(expected, PinHash.Calculate(digits, gridSeed, server, client));
        Assert.True(PinHash.Verify(digits, gridSeed, server, client, expected));

        digits[0] = (byte)((digits[0] + 1) % 10);
        Assert.False(PinHash.Verify(digits, gridSeed, server, client, expected));
    }

    // RFC 6238 Appendix B, SHA1 vector at T=59: 8-digit 94287082 -> classic 6-digit 287082.
    [Fact]
    public void Totp_Rfc6238SixDigitVector()
    {
        Assert.Equal(287082u, Totp.Generate("12345678901234567890"u8, 59));
        Assert.True(Totp.TryDecodeSecret("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", out byte[] key));
        Assert.Equal("12345678901234567890"u8.ToArray(), key);
    }

    // files, checksum salt, client public key, expected.
    [Theory]
    [InlineData("ECF5DEA19D6FD74C16ADE57E4AFD9A73440299F12C9F123A517662EB5492AC63C53CBE9D2CEBB1B484289C482E1F1E3ACC41", "730313B3811ACCC1EC5798F8362F8C16", "C82357F1CDBB5A52F9FD7B06412227D09B16A91FD5C6A649B07858DDE0F076EE", "04F3A8CF79294AF370AC41ECE3CD85F65EF8E7F8")]
    [InlineData("FB0BDE1C15A8F5F8DDA076690506972DAC0C6BFFD0C1387216ECFEAA84DEE4963836F9FA655318637BF29A06CB9E7EF9BDC2", "4D8965726A7E4522DD5023C6222FA59E", "EFEE0909ADAAFAB5AD32FF4175160C0DA49BD36C02F93CC207BDBAA59FEE7458", "0624BA52840ABDB09BAF07874DEC8C22800169D8")]
    [InlineData("BA052675FB1D2D656C63DB46B72FAEA9F3F1BF641BA68F4E370911F347F9CBFBB783D83820C56061937319DC1CB06621CC92", "09DA3008791F3F5874D405085E40E554", "0846224C2021FE9577DBCA1AC76733829001887D0602479068698C37F5F9A710", "0FD95E60FC3A7CE0EE5CD0F318158911A67B7100")]
    public void IntegrityVector_ClientFileChecksumMatchesUpstream(string files, string salt, string publicKey, string expected)
        => Assert.Equal(UpstreamBytes(expected),
            ClientIntegrity.GenericCheck(UpstreamBytes(files), UpstreamBytes(salt), UpstreamBytes(publicKey)));

    // Reconnect: SHA1(R1 || 20 zero bytes), which LogonSession checks under StrictVersionCheck.
    [Theory]
    [InlineData("4C6032217F73D72CF33096B15A4FA87F", "ADC17936EB30586305C56A53CBD17D913DAD89DE")]
    [InlineData("4F0DE88452C3DA710B83DFE642B9222B", "47305CFFF77043E6FF8CF810F94F39A4CA012D13")]
    [InlineData("9550A2ACAC434A9174861FB5EA40EA50", "7C7FEC770E0A08C6CC8444B74F7758EDE040B7C1")]
    public void IntegrityVector_ReconnectProofMatchesUpstream(string r1, string expected)
        => Assert.Equal(UpstreamBytes(expected), ClientIntegrity.VersionProof(UpstreamBytes(r1), new byte[20]));

    // WowSrp.Test/src/TestUtils.cs reads fixture hex as little-endian: an odd byte count is
    // padded with a leading zero byte (unless padding is off, as for PIN digits), then reversed.
    private static byte[] UpstreamBytes(string hex, bool padding = true)
    {
        byte[] bytes = Convert.FromHexString(hex);
        if (padding && bytes.Length % 2 != 0) bytes = [0, .. bytes];
        Array.Reverse(bytes);
        return bytes;
    }
}
