using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ArcaneCore.Analyzers.Tests;

/// <summary>Compiles a snippet in memory and runs <see cref="SafetyAnalyzer"/> over it, the way the compiler does in a real build.</summary>
internal static class AnalyzerHarness
{
    /// <summary>The marker attributes, declared with the same full names as src/ArcaneCore.Kernel/Safety/SafetyAttributes.cs (the analyzer matches by name).</summary>
    public const string Declarations = """
        using System;

        namespace ArcaneCore.Kernel.Safety
        {
            [AttributeUsage(AttributeTargets.All)] public sealed class WorldThreadOnlyAttribute : Attribute { }
            [AttributeUsage(AttributeTargets.All)] public sealed class AnyThreadAttribute : Attribute { }
            [AttributeUsage(AttributeTargets.All)] public sealed class RunsOnWorldThreadAttribute : Attribute { }
            [AttributeUsage(AttributeTargets.All)] public sealed class AuditedUnsafeAttribute(string reason) : Attribute { public string Reason { get; } = reason; }
        }

        [ArcaneCore.Kernel.Safety.WorldThreadOnly]
        public class Entity
        {
            public int Hp;
            public int Level { get; set; }
            public void Tick() { }
            public void Set(int value) { }
            [ArcaneCore.Kernel.Safety.AnyThread] public int Id => 7;
            [ArcaneCore.Kernel.Safety.AnyThread] public void Peek() { }
        }

        public sealed class Creature : Entity { public void Despawn() { } }

        public sealed class World
        {
            [ArcaneCore.Kernel.Safety.RunsOnWorldThread] public void Post(Action action) { }
            public void PostPlain(Action action) { }
            public void Hop([ArcaneCore.Kernel.Safety.RunsOnWorldThread] Action onWorld, Action other) { }
        }

        """;

    private const string Usings = """
        using System;
        using System.Buffers;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Collections.Generic;
        using System.Linq;
        using ArcaneCore.Kernel.Safety;

        """;

    public static IReadOnlyList<Diagnostic> Analyze(string source, string? baseline = null, bool allowUnsafe = false, string sourcePath = "/repo/src/Test.cs")
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        SyntaxTree declarations = CSharpSyntaxTree.ParseText(Declarations, parse, "/repo/src/Declarations.cs");
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Usings + source, parse, sourcePath);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "TestAssembly",
            [declarations, tree],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: allowUnsafe, nullableContextOptions: NullableContextOptions.Enable));

        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(errors.Length == 0, "the snippet does not compile: " + string.Join("; ", errors.Select(e => e.ToString())));

        ImmutableArray<AdditionalText> additional = baseline is null
            ? []
            : [new InMemoryText("/repo/analyzers/safety-baseline.txt", baseline)];
        CompilationWithAnalyzers run = compilation.WithAnalyzers(
            [new SafetyAnalyzer()],
            new AnalyzerOptions(additional));
        return run.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
    }

    public static string[] Ids(IEnumerable<Diagnostic> diagnostics) => [.. diagnostics.Select(d => d.Id).Order(StringComparer.Ordinal)];

    private static ImmutableArray<MetadataReference> References()
    {
        string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return [.. trusted.Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    }

    private sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
}
