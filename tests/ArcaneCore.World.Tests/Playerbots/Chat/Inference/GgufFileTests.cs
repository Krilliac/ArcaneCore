using ArcaneCore.World.Playerbots.Chat.Inference;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference;

/// <summary>The GGUF reader on synthetic files: header, metadata of every kind, the tensor table, alignment, and the refusals.</summary>
public sealed class GgufFileTests
{
    private static GgufBuilder Sample(int alignment = 32)
    {
        float[] matrix = [.. Enumerable.Range(0, 12).Select(i => i * 0.5f)];
        return new GgufBuilder { Alignment = alignment }
            .String("general.architecture", "llama")
            .String("general.name", "Tiny Test")
            .U32("llama.block_count", 3)
            .I32("llama.signed", -7)
            .F32("llama.rope.freq_base", 10000f)
            .Bool("tokenizer.ggml.add_bos_token", true)
            .U64("big", 1UL << 40)
            .Strings("tokenizer.ggml.tokens", ["a", "b", "<|im_end|>"])
            .Ints("tokenizer.ggml.token_type", [1, 1, 3])
            .Floats("scores", [0.5f, -1.25f])
            .TensorF32("norm.weight", [1f, 2f, 3f], 3)
            .TensorF32("matrix.weight", matrix, 4, 3);
    }

    [Fact]
    public void Reads_the_header_metadata_and_tensor_table()
    {
        string path = Sample().WriteTemp();
        try
        {
            using GgufFile file = GgufFile.Open(path);
            Assert.Equal(3u, file.Version);
            Assert.Equal(32, file.Alignment);
            Assert.Equal("llama", file.String("general.architecture"));
            Assert.Equal("Tiny Test", file.String("general.name"));
            Assert.Equal(3L, file.Integer("llama.block_count"));
            Assert.Equal(-7L, file.Integer("llama.signed"));
            Assert.Equal(10000.0, file.Real("llama.rope.freq_base"));
            Assert.True(file.Boolean("tokenizer.ggml.add_bos_token"));
            Assert.Equal(1L << 40, file.Integer("big"));
            Assert.Equal(["a", "b", "<|im_end|>"], file.Array<string>("tokenizer.ggml.tokens")!);
            Assert.Equal([1, 1, 3], file.IntArray("tokenizer.ggml.token_type")!);
            Assert.Equal([0.5f, -1.25f], file.Array<float>("scores")!);
            Assert.Null(file.String("missing"));

            Assert.Equal(2, file.Tensors.Count);
            GgufTensor norm = file.Tensor("norm.weight")!;
            Assert.Equal([3L], norm.Dims);
            Assert.Equal(GgmlType.F32, norm.Type);
            Assert.Equal(1, norm.Rows);
            Assert.Equal(12, norm.ByteSize);
            GgufTensor matrix = file.Tensor("matrix.weight")!;
            Assert.Equal([4L, 3L], matrix.Dims);
            Assert.Equal(4, matrix.RowLength);
            Assert.Equal(3, matrix.Rows);
            Assert.Equal(16, matrix.RowBytes);
            Assert.Equal(0, file.DataStart % 32);
            Assert.Equal(0, matrix.RelativeOffset % 32);
            Assert.Equal(32, matrix.RelativeOffset); // 12 bytes of norm, padded to the next 32

            float[] row = new float[4];
            GgmlQuant.DequantizeRowScalar(GgmlType.F32, file.Row(matrix, 2), row);
            Assert.Equal([4f, 4.5f, 5f, 5.5f], row);
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }

    [Fact]
    public void Honours_a_larger_alignment()
    {
        string path = Sample(alignment: 64).WriteTemp();
        try
        {
            using GgufFile file = GgufFile.Open(path);
            Assert.Equal(64, file.Alignment);
            Assert.Equal(0, file.DataStart % 64);
            Assert.Equal(64, file.Tensor("matrix.weight")!.RelativeOffset);
            float[] row = new float[4];
            GgmlQuant.DequantizeRowScalar(GgmlType.F32, file.Row(file.Tensor("matrix.weight")!, 0), row);
            Assert.Equal([0f, 0.5f, 1f, 1.5f], row);
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }

    [Fact]
    public void Refuses_a_bad_magic_a_bad_version_and_truncation()
    {
        byte[] good = Sample().Build();

        byte[] magic = (byte[])good.Clone();
        magic[0] = (byte)'X';
        AssertRefused(magic, "not a GGUF file");

        byte[] version = (byte[])good.Clone();
        version[4] = 1;
        AssertRefused(version, "version 1");

        // Cut inside the tensor data: the last tensor runs past the end.
        AssertRefused(good[..(good.Length - 40)], "past the end");

        // Cut inside the metadata.
        AssertRefused(good[..60], "past the end");
    }

    [Fact]
    public void Refuses_a_lying_array_count()
    {
        byte[] bytes = new GgufBuilder().Strings("tokens", ["x"]).Build();
        // The count of the one array: magic(4) version(4) tensors(8) kvs(8) key(8 + 6) type(4) element type(4) -> count at 46.
        BitConverter.GetBytes((ulong)(GgufFile.MaxArrayLength + 1L)).CopyTo(bytes, 46);
        AssertRefused(bytes, "elements");
    }

    [Fact]
    public void Sizes_only_supported_types()
    {
        Assert.Equal(34, GgmlQuant.RowBytes(GgmlType.Q8_0, 32));
        Assert.Equal(144 * 2, GgmlQuant.RowBytes(GgmlType.Q4_K, 512));
        Assert.Equal(210, GgmlQuant.RowBytes(GgmlType.Q6_K, 256));
        Assert.Equal(-1, GgmlQuant.RowBytes(GgmlType.Q8_0, 33));   // not whole blocks
        Assert.Equal(-1, GgmlQuant.RowBytes(GgmlType.Q5_K, 256));  // unsupported
        Assert.Equal(-1, GgmlQuant.RowBytes((GgmlType)99, 32));
    }

    private static void AssertRefused(byte[] bytes, string reason)
    {
        string path = GgufBuilder.WriteTemp(bytes);
        try
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => GgufFile.Open(path).Dispose());
            Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }
}
