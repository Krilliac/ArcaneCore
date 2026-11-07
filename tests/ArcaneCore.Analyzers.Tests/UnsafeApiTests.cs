namespace ArcaneCore.Analyzers.Tests;

/// <summary>ARC0003: unsafe APIs need an [AuditedUnsafe] on the member, type or assembly.</summary>
public sealed class UnsafeApiTests
{
    [Theory]
    [InlineData("int M(ref byte b) => Unsafe.As<byte, int>(ref b);")]
    [InlineData("Span<byte> M(Span<int> s) => MemoryMarshal.AsBytes(s);")]
    [InlineData("IntPtr M() => Marshal.AllocHGlobal(8);")]
    [InlineData("int M() => Unsafe.SizeOf<long>();")]
    public void UnsafeApis_AreReported(string member)
    {
        var diagnostics = AnalyzerHarness.Analyze("class C { " + member + " }");
        Assert.Equal(["ARC0003"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void PInvoke_IsReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { [DllImport("kernel32.dll")] static extern int GetTickCount(); }
            """);
        Assert.Equal(["ARC0003"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AuditedMember_IsAccepted()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                [AuditedUnsafe("reads the header in place; the span is exactly one header long")]
                int M(ref byte b) => Unsafe.As<byte, int>(ref b);
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AuditedType_CoversItsMembersAndNestedTypes()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            [AuditedUnsafe("native timer")]
            class C
            {
                [DllImport("kernel32.dll")] static extern int GetTickCount();
                int M() => Unsafe.SizeOf<long>();
                class Nested { int N() => Unsafe.SizeOf<int>(); }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AuditedProperty_CoversItsAccessors()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                [AuditedUnsafe("size probe")]
                int P => Unsafe.SizeOf<long>();
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AuditedAssembly_CoversEverything()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            [assembly: AuditedUnsafe("interop assembly")]
            class C { int M() => Unsafe.SizeOf<long>(); }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ReadingTheLastPInvokeError_IsNotUnsafe()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { int M() => Marshal.GetLastPInvokeError(); }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void CollectionsMarshalAndOrdinaryCode_AreNotReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { Span<int> M(List<int> l) => CollectionsMarshal.AsSpan(l); }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AllowUnsafeBlocks_NeedsAnAssemblyAudit()
    {
        var diagnostics = AnalyzerHarness.Analyze("class C { }", allowUnsafe: true);
        Assert.Equal(["ARC0003"], AnalyzerHarness.Ids(diagnostics));

        var audited = AnalyzerHarness.Analyze("""
            [assembly: AuditedUnsafe("codec")]
            class C { }
            """, allowUnsafe: true);
        Assert.Empty(audited);
    }
}
