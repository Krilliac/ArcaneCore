namespace ArcaneCore.Analyzers.Tests;

/// <summary>ARC0004 (rent without return), ARC0005 (use after return) and ARC0006 (blocking waits).</summary>
public sealed class PooledBufferTests
{
    [Fact]
    public void RentWithoutAReturn_IsReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { byte[] M() => ArrayPool<byte>.Shared.Rent(16); }
            """);
        Assert.Equal(["ARC0004"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void RentWithAReturnInFinally_IsAccepted()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                int M()
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
                    try { return buffer.Length; }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ReturnInALocalFunctionOfTheSameMember_CountsAsOwned()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                int M()
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
                    int n = buffer.Length;
                    Release();
                    return n;
                    void Release() => ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MemoryPoolRent_NeedsAUsing()
    {
        var bad = AnalyzerHarness.Analyze("""
            class C { IMemoryOwner<byte> M() => MemoryPool<byte>.Shared.Rent(16); }
            """);
        Assert.Equal(["ARC0004"], AnalyzerHarness.Ids(bad));

        var good = AnalyzerHarness.Analyze("""
            class C { int M() { using IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(16); return owner.Memory.Length; } }
            """);
        Assert.Empty(good);
    }

    [Fact]
    public void UseAfterReturn_IsReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                int M()
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
                    ArrayPool<byte>.Shared.Return(buffer);
                    return buffer.Length;
                }
            }
            """);
        Assert.Equal(["ARC0005"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void UseAfterAReturnInFinally_IsReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                int M()
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
                    try { buffer[0] = 1; }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                    return buffer[0];
                }
            }
            """);
        Assert.Equal(["ARC0005"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void ReassigningTheBufferAfterTheReturn_EndsTheScan()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                int M()
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = Array.Empty<byte>();
                    return buffer.Length;
                }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void UsesBeforeTheReturn_AreFine()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                int M()
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
                    int n = buffer.Length;
                    ArrayPool<byte>.Shared.Return(buffer);
                    return n;
                }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("int M(Task<int> t) => t.Result;")]
    [InlineData("void M(Task t) => t.Wait();")]
    [InlineData("void M(Task t) => Task.WaitAll(t);")]
    [InlineData("void M(Task t) => Task.WaitAny(t);")]
    [InlineData("int M(Task<int> t) => t.GetAwaiter().GetResult();")]
    [InlineData("void M(Task t) => t.GetAwaiter().GetResult();")]
    [InlineData("void M(Task t) => t.ConfigureAwait(false).GetAwaiter().GetResult();")]
    [InlineData("int M(ValueTask<int> t) => t.GetAwaiter().GetResult();")]
    public void BlockingWaits_AreReported(string member)
    {
        var diagnostics = AnalyzerHarness.Analyze("class C { " + member + " }");
        Assert.Equal(["ARC0006"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AwaitingAndUnrelatedResultProperties_AreNotReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class Outcome { public int Result => 1; }
            class C
            {
                async Task<int> M(Task<int> t) => await t;
                int N(Outcome o) => o.Result;
                bool Done(Task t) => t.IsCompleted;
            }
            """);
        Assert.Empty(diagnostics);
    }
}
