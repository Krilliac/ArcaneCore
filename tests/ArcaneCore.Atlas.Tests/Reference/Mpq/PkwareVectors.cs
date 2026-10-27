namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>Which of the deterministic plaintexts a <see cref="PkwareVector"/> expands to.</summary>
internal enum PlainText
{
    /// <summary>"The quick brown fox jumps over the lazy dog. " repeated 30 times (1350 bytes): long matches with extra length bits.</summary>
    V0,

    /// <summary>2000 bytes of (i * 7 + i / 13) mod 251: short matches and literals.</summary>
    V1,

    /// <summary>3000 zero bytes, one 'X', 3000 zero bytes: maximal-length runs.</summary>
    V2,

    /// <summary>V1, then V0, then V1 again (5350 bytes): back-references more than 2000 bytes away.</summary>
    V3,

    /// <summary>The single byte 'A'.</summary>
    V4,

    /// <summary>700 bytes of xorshift32 output (seed 0x2545F491, taking the low byte): almost all literals.</summary>
    V5,
}

/// <summary>One PKWARE DCL stream and what it must expand to.</summary>
/// <param name="Source">The plaintext.</param>
/// <param name="DictionaryBytes">Dictionary size the compressor was asked for (1024, 2048 or 4096).</param>
/// <param name="PlainLength">Length of the plaintext.</param>
/// <param name="Hex">The imploded stream, hex.</param>
internal sealed record PkwareVector(PlainText Source, int DictionaryBytes, int PlainLength, string Hex)
{
    /// <summary>The imploded bytes.</summary>
    public byte[] Stream => Convert.FromHexString(Hex);
}

/// <summary>
/// PKWARE Data Compression Library streams made by a compressor this repository did not write: StormLib's <c>implode</c>
/// (StormLib src/pklib/implode.c, binary mode, the three dictionary sizes) compiled with clang 22.1.8 and driven
/// the way <c>SCompImplode</c> drives it (SCompression.cpp:196-243, 674-698). Each stream was also expanded by StormLib's own
/// <c>explode</c> (pklib\explode.c) in the generator and matched its plaintext byte for byte before being recorded here. The
/// plaintexts are rebuilt by <see cref="Plain"/>; nothing here comes from client data.
/// </summary>
internal static class PkwareVectors
{
    /// <summary>The recorded streams.</summary>
    public static readonly PkwareVector[] All =
    [
        new(PlainText.V0, 1024, 1350,
            "0004a8a02903224e9d3463d6801023e7cd1d3720ccbcc103424d9d3670e6807863a78c1c10746fc7268c9e3c20c8bc39" +
            "e3022480bf38809fb60109c501ff"),
        new(PlainText.V1, 2048, 2000,
            "0005001c705081c308153170fc30d2840a97316ae2e0f963a811a54da252c1c2f5cb58336adbc4a583776f8082081940" +
            "9c705183c7102551b07c31d3a60e9f418a2261fa64aa15ad5dc29245c3f6cd5c3bfa52868c1c408e38a92a6538aac469" +
            "94aad85286ba78f8082c9028652c919205cc19b752c62957b5780d532e65207860a10389955206993572f2003a2c6500" +
            "3be6ac1ab7f1524608142f6cf4202a65f421b4485226d052463670e7dcd50b2865e80882e48995ae5286a54ea456c9ca" +
            "2d65c8cb5780c1048d52c6142d61d0bcb12b65bcb2d58bd832e95206840b1e4ab0982965b099a32710a2c75206b267d6" +
            "ba915b2f65a48071c34711a652462146933485422d658443f7ce9e80845286902450ae78292b65782ac56a96aee05286" +
            "3e030d286c1029656c119306ce1dbf52c62d5fc5984dd32e6560f860a2058d9d52069d3d821241ba2c6540bbe6ad1cbb" +
            "f952a61aac94a9062b65aac14a996ab052a61aac9489d1a6ad186dba8dd1268c186da28bd126e8186d8a89d1a6c6186d" +
            "4a8fd126a5186d328dd1a683186d1a8bd1a6df186d0289d126be186dc28ed1a69c186daa8cd1a6f8186d928ad126d718" +
            "6d7a88d1a6b5186d3a8ed12694186d228cd126f0186d0a8ad1a6ce186dca8fd126ad186db28dd1a68b186d9a8bd1a6e7" +
            "186d8289d126c6186d428fd1a6a4186d2a8dd12683186d128bd126df186dfa88d1a6bd186dba8ed1269c186da28cd126" +
            "f8186d8a8ad1a6d6186d7288d126b5186d328ed1a693186d1a8cd1a6ef186d028ad126ce186dc28fd1a6ac186daa8dd1" +
            "268b186d928bd126e7186d7a89d1a6c5186d3a8fd126a4186d228dd1a682186d0a8bd1a6de186df288d126bd186db28e" +
            "d1a69b186d9a8cd1a6f7186d828ad126d6186d6a88d1a6b4186d2a8ed12693186d128cd126ef186dfa89d1a6cd186dba" +
            "8fd126ac186da28dd1a68a186d8a8bd1a6e6186d7289d126c5186d328fd1a6a3186d1a8dd12682186d028bd126de186d" +
            "ea88d1a6bc186daa8ed1269b186d928cd126f7186d7a8ad1a6d5186d62486a33e01f"),
        new(PlainText.V2, 4096, 6001,
            "0006000004f01f04f07b4500bf9604f0f39b007e7a14a079002b801f8907f043f201fc107b003f801fc00fe0077002f8" +
            "01ff"),
        new(PlainText.V3, 4096, 5350,
            "0006001c705081c308153170fc30d2840a97316ae2e0f963a811a54da252c1c2f5cb58336adbc4a583776f8082081940" +
            "9c705183c7102551b07c31d3a60e9f418a2261fa64aa15ad5dc29245c3f6cd5c3bfad2061939801c715255dae0a812a7" +
            "51aa624b1bd4c5c34760814469c3122959c09c712b6d38e5aa16af61caa50d081e58e84062a5b481cc1a3979001d9636" +
            "003be6ac1ab7f1d28610285ed8e84154dad087d0224999404b1bb2813be7ae5e4069838e20489e58e92a6db0d489d42a" +
            "59b9a50df2f215603041a3b4618a963068ded89536bcb2d58bd832e9d20608173c94603153dac0668e9e40881e4b1b90" +
            "3db3d68ddc7a69430a18377c14612a6d28c46892a650a8a50de1d0bdb32720a1b44148122857bc949536782ac56a96ae" +
            "e0d2067d061a50d82052dab0454c1a3877fc4a1b6ef92ac66c9a766903860f265ad0d8296da0b34750224897a50dd0ae" +
            "792bc76ebeb4517da6b4517da6b4517da6b4517da6b4517da6b411e34db662bcc96d8c37c188f1267431de043ac69bc4" +
            "c47893c6186f921ee34d9462bc89698c373988f1266331dee437c69b80c47813be186f821de34d7262bc49658c37898f" +
            "f1265231dec435c69b3cc47893b5186f721ce34d5062bc09618c37018ff1264131dea433c69be4c77813ad186f621be3" +
            "4d2e62bcc95c8c37798ef1263031de8431c69ba0c77893a4186f521ae34d0c62bc89588c37f18df1261f31de642fc69b" +
            "5cc778139c186f4219e34de063bc49548c37698df1260e31de442dc69b18c7789393186f3218e34dbe63bc09508c37e1" +
            "8cf126f831de242bc69bd4c678138b186f2217e34d9c63bcc94b8c37598cf126e731de0429c69b90c6789382186f1216" +
            "e34d7a63bc89478c37d18bf126d631dee426c69b4cc67893f7186f0215e34d5863bc49438c37498bf126c531dec424c6" +
            "9b08c67813ef186ff213e34d3663bcc97d8c37c18af126b431dea422c69bc4c57893e6186fe212e34d1463bc89798c37" +
            "398af126a331de8420c69b80c57813de186fd211e34df262bc49758c37b189f1269231dec43dc69b3cc57893d5186fc2" +
            "90f42654d0940111a74e9a316b408891f3e68e1b1066dee001a1a64e1b38734056a0327240d07d0f9b307af28020f3e6" +
            "8c0b9000fed9007e6f0b48980de087ae02f8a1ab007ee82a8017ba0af807"),
        new(PlainText.V4, 1024, 1,
            "00048202fe01"),
        new(PlainText.V5, 1024, 700,
            "000474ac6265e2d5080d71d844ea1203830f5ed2de4511d149ce936dd9dee470e36a0532112f188c6bc708cd1e518932" +
            "59cb71ea93bc2afe2255c00542951e4a8a403501d6c95f1226dc1c81cb6540168c144ac070f8c6e3969867420a6460b0" +
            "64d6b7707c8aec8312e50c1c3cc908e01bc280169239cb8c15c0f6090bbc36d7cae160e5ee5d3b7eef2ef9b37083d780" +
            "73aa6095c13124c9b52c3bc0fdf9236e54326bae549c31d36e55ad2673fe59523442c6824bef38f4d115c4129e7cd2d2" +
            "f098f566d9362174c205fa22e2da8612a79cd95b0628940755adbe7580854e44342f0454808033e603225dad06a991b4" +
            "65dbb663ef0eedabc0ae142e4b9f2a24190388533243339a1560d143c89a59fc92a8282620043b2196a0c999f0230b0a" +
            "24c6f2ad5a17ce508076685ebd291065969e757ea2c1db448c0a2f538c88b833562f45ba37d352e191014d539b6e6906" +
            "799b8160c4a609a990204093a8832d523974d541078b45171609a4b43012ec06162913b264ca6648948d5b1bf2e090c7" +
            "cc1e213101503128b0ca563d7c0082ac2a90a854244906a830c3b6ab93a432f9f85d91c1e2c28b0ed09e49e9e38d5c33" +
            "0c90e42dc174c851161080e6285277e81b8c5ee470c151d304d71455dd68580256275d3279607a6da803a6de201d0f94" +
            "29d263c1958215fedca473554d8a133b925495d24167c53557a6acbc1b10650bba5239f6f97302c90c112dd376d5bb35" +
            "86531255e3ea901900a653a953ade06cc9268692212aca44c0abc6ed0f2210e91ad538636b49b84c4032980891438609" +
            "4783a608d352ed039007a288958af76e8b1a51b85a6c89c7e04928555ee45010600b46b5180b56f58a904e8937470c18" +
            "81fa96c9c89d592bf014e1e60102bd315c5629e1f26e0d054972fce0e1674e019e04ad86f943b1e3052577e442a170e5" +
            "6a14b5454666b19bb3659db07199b811c9840b5e10425b4a8c8203aa8b1e01ebd03041f063458322963e9df9a7898ab1" +
            "4bbd9ee0928766dd880fc850a4e887ca44b87eae14696a14a70f2a7c51b89d2a37404ebc183b32e179032d4e13471c48" +
            "4cbbd7e15fa71b3548046a96c35f2a4618b8d031a116f00f"),
        new(PlainText.V0, 4096, 1350,
            "0006a8a02903224e9d3463d6801023e7cd1d3720ccbcc103424d9d3670e6807863a78c1c1074dfc3268c9e3c20c8bc39" +
            "e30224807f3680dfdb02126603fe01")
    ];

    /// <summary>Rebuilds a plaintext.</summary>
    public static byte[] Plain(PlainText which)
    {
        byte[] Ramp()
        {
            var bytes = new byte[2000];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(((i * 7) + (i / 13)) % 251);
            }

            return bytes;
        }

        byte[] Fox() => System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 30)));

        switch (which)
        {
            case PlainText.V0:
                return Fox();
            case PlainText.V1:
                return Ramp();
            case PlainText.V2:
                {
                    var bytes = new byte[6001];
                    bytes[3000] = (byte)'X';
                    return bytes;
                }

            case PlainText.V3:
                return [.. Ramp(), .. Fox(), .. Ramp()];
            case PlainText.V4:
                return [(byte)'A'];
            case PlainText.V5:
                {
                    var bytes = new byte[700];
                    uint x = 0x2545F491;
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        x ^= x << 13;
                        x ^= x >> 17;
                        x ^= x << 5;
                        bytes[i] = (byte)(x & 0xFF);
                    }

                    return bytes;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(which));
        }
    }
}
