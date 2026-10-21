using System.Collections.Concurrent;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>One construct that commonly reads differently from what it does.</summary>
/// <param name="Rule">RB001..RB004, see <see cref="ReadsDifferentlyScanner"/>.</param>
/// <param name="File">Repository-relative path, forward slashes.</param>
/// <param name="Member">Enclosing "Type.Member" (nested types joined by dots).</param>
/// <param name="Snippet">The construct's source text, white space collapsed, cut to 110 characters.</param>
/// <param name="Line">1-based line, for the report only; the baseline key does not use it.</param>
internal sealed record ReadsDifferentlyHit(string Rule, string File, string Member, string Snippet, int Line)
{
    /// <summary>Baseline key without the duplicate counter: "RULE | file | member | snippet".</summary>
    public string BaseKey => $"{Rule} | {File} | {Member} | {Snippet}";
}

/// <summary>
/// A Roslyn scan of src/ for four constructs that read differently from what they do. Syntax finds the construct; where the answer
/// depends on a type (a float operand, a void-returning delegate) a semantic model of the file's project answers it.
/// <list type="bullet">
/// <item><b>RB001</b> a pattern <c>is not A or B</c> / <c>is not A and B</c>: <c>not</c> binds only to the first operand, so it is
/// <c>(not A) or B</c>, not <c>not (A or B)</c>. <c>is not A or not B</c> is reported as well: it is false only for a value that is both
/// an A and a B (usually none), where a reader applying De Morgan expects <c>not (A or B)</c>. The one form spared is
/// <c>is not A and not B</c>, which means what it says.</item>
/// <item><b>RB002</b> an explicit float, double, Half or decimal to integer cast in packet and serialization code (the scope is
/// <see cref="InPacketScope"/>): the cast truncates toward zero, where rounding is often intended. A cast whose operand is a direct
/// <c>Round</c>/<c>Floor</c>/<c>Ceiling</c>/<c>Truncate</c> call states its rounding and is not reported.</item>
/// <item><b>RB003</b> <c>??</c> directly beside an unparenthesized arithmetic, shift, relational, equality, bitwise or conditional
/// logical operator, or beside <c>is</c>/<c>as</c> including the pattern forms <c>is null</c>, <c>is not null</c>, <c>is { }</c>
/// (<c>a ?? b &lt; c</c> is <c>a ?? (b &lt; c)</c>; <c>a + b ?? c</c> is <c>(a + b) ?? c</c>; <c>a ?? b is null</c> is
/// <c>a ?? (b is null)</c>; <c>a ?? b as T</c> casts only <c>b</c>). <c>x as T ?? y</c> and <c>x as T ?? y as U</c> read as they parse and are not reported.</item>
/// <item><b>RB004</b> <c>async void</c> (a method, local function or lambda converted to a void-returning delegate) that is not an event
/// handler. An event handler has two parameters, the second an EventArgs type.</item>
/// </list>
/// Limits: conditional-compilation branches whose symbols are not defined by default are not parsed; generated files under obj and
/// source-generator output are not seen; a type that cannot be resolved (an internal of a project that grants no
/// InternalsVisibleTo to the test assembly) leaves an RB002 cast, or the delegate type of an RB004 async lambda, unclassified, counted in
/// <see cref="Unresolved"/>. A source file the parser cannot read (syntax newer than the pinned Roslyn) is listed in
/// <see cref="SyntaxErrors"/>, which the audit test requires to be empty.
/// </summary>
internal sealed class ReadsDifferentlyScanner
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> RoundingMethods = new(StringComparer.Ordinal) { "Round", "Floor", "Ceiling", "Truncate" };

    private static readonly SyntaxKind[] CoalesceNeighbours =
    [
        SyntaxKind.AddExpression, SyntaxKind.SubtractExpression, SyntaxKind.MultiplyExpression, SyntaxKind.DivideExpression,
        SyntaxKind.ModuloExpression, SyntaxKind.LeftShiftExpression, SyntaxKind.RightShiftExpression, SyntaxKind.UnsignedRightShiftExpression,
        SyntaxKind.LessThanExpression, SyntaxKind.LessThanOrEqualExpression, SyntaxKind.GreaterThanExpression,
        SyntaxKind.GreaterThanOrEqualExpression, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression,
        SyntaxKind.BitwiseAndExpression, SyntaxKind.BitwiseOrExpression, SyntaxKind.ExclusiveOrExpression,
        SyntaxKind.LogicalAndExpression, SyntaxKind.LogicalOrExpression, SyntaxKind.IsExpression, SyntaxKind.IsPatternExpression, SyntaxKind.AsExpression,
    ];

    private readonly string _root;
    private readonly string _assemblyName;
    private readonly Lazy<MetadataReference[]> _references;
    private readonly ConcurrentDictionary<string, Lazy<CSharpCompilation>> _compilations = new(StringComparer.Ordinal);
    private readonly List<(string Project, string RelativePath, SyntaxTree Tree)> _trees = [];
    private readonly Dictionary<string, SyntaxTree> _globalUsings = new(StringComparer.Ordinal);

    /// <summary>The usings the SDK's ImplicitUsings gives a Microsoft.NET.Sdk project, for a project that has no generated file in obj.</summary>
    private const string DefaultGlobalUsings = "global using global::System; global using global::System.Collections.Generic; global using global::System.IO; "
        + "global using global::System.Linq; global using global::System.Net.Http; global using global::System.Threading; global using global::System.Threading.Tasks;";

    /// <summary>Scan the src/ tree of the checkout at <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The checkout (the directory holding ArcaneCore.slnx).</param>
    /// <param name="assemblyName">The name the per-project compilations take, so the InternalsVisibleTo grants to the test assembly apply.</param>
    public ReadsDifferentlyScanner(string repositoryRoot, string assemblyName)
    {
        _root = repositoryRoot;
        _assemblyName = assemblyName;
        _references = new Lazy<MetadataReference[]>(LoadReferences);
    }

    /// <summary>Source files parsed.</summary>
    public int FilesScanned { get; private set; }

    /// <summary>Source files in the packet and serialization scope of RB002.</summary>
    public int PacketScopeFiles { get; private set; }

    /// <summary>Integral-target casts in the RB002 scope whose operand type was resolved.</summary>
    public int CastsClassified { get; private set; }

    /// <summary>Integral-target casts in the RB002 scope whose operand type could not be resolved.</summary>
    public int UnresolvedCasts { get; private set; }

    /// <summary>Async lambdas (RB004) that were looked at.</summary>
    public int AsyncLambdas { get; private set; }

    /// <summary>Async lambdas whose delegate type could not be resolved, so whether they return void is unknown.</summary>
    public int UnresolvedLambdas { get; private set; }

    /// <summary>"file:line" of the async lambdas counted in <see cref="UnresolvedLambdas"/>.</summary>
    public List<string> UnresolvedLambdaSites { get; } = [];

    /// <summary>Everything the semantic model could not answer: <see cref="UnresolvedCasts"/> plus <see cref="UnresolvedLambdas"/>.</summary>
    public int Unresolved => UnresolvedCasts + UnresolvedLambdas;

    /// <summary>"file(line): message" for every syntax error the parser found in a source file; empty when all of src/ parsed cleanly.</summary>
    public List<string> SyntaxErrors { get; } = [];

    /// <summary>Whether a source file belongs to the packet and serialization code RB002 looks at.</summary>
    public static bool InPacketScope(string relativePath, SyntaxNode root)
    {
        string[] segments = relativePath.Split('/');
        string[] directories = segments[1..^1]; // [0] is "src"; the project directory is the first of these
        if (directories.Length > 0 && directories[0] == "ArcaneCore.Protocol")
        {
            return true;
        }

        if (directories.Skip(1).Any(d => d is "Net" or "Packets"))
        {
            return true;
        }

        if (Path.GetFileNameWithoutExtension(segments[^1]).Contains("Packet", StringComparison.Ordinal))
        {
            return true;
        }

        return root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .Any(n => n.Name.ToString().Split('.').Any(s => s is "Net" or "Packets" or "Protocol"));
    }

    /// <summary>Parse every .cs file under src/ (not obj, bin).</summary>
    public void Load()
    {
        string src = Path.Combine(_root, "src");
        var parsed = new ConcurrentBag<(string Project, string RelativePath, SyntaxTree Tree)>();
        string[] files = [.. Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        Parallel.ForEach(files, file =>
        {
            string relative = Path.GetRelativePath(_root, file).Replace('\\', '/');
            string project = relative.Split('/')[1];
            parsed.Add((project, relative, CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, file)));
        });
        _trees.AddRange(parsed.OrderBy(t => t.RelativePath, StringComparer.Ordinal));
        FilesScanned = _trees.Count;
        foreach (string project in _trees.Select(t => t.Project).Distinct(StringComparer.Ordinal))
        {
            // The implicit global usings are generated into obj by the build; without them Task, Func and the rest do not resolve and
            // every lambda looks unresolved. Take the newest generated file of the project, else the SDK default set.
            string? generated = Directory.Exists(Path.Combine(src, project, "obj"))
                ? Directory.EnumerateFiles(Path.Combine(src, project, "obj"), "*.GlobalUsings.g.cs", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            _globalUsings[project] = CSharpSyntaxTree.ParseText(generated is null ? DefaultGlobalUsings : File.ReadAllText(generated), options);
        }

        foreach ((string _, string relative, SyntaxTree tree) in _trees)
        {
            foreach (Diagnostic diagnostic in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                SyntaxErrors.Add($"{relative}({diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1}): {diagnostic.Id} {diagnostic.GetMessage()}");
            }
        }
    }

    /// <summary>All hits of all four rules, ordered by file, then position.</summary>
    public List<ReadsDifferentlyHit> Scan()
    {
        var hits = new List<ReadsDifferentlyHit>();
        foreach ((string project, string relative, SyntaxTree tree) in _trees)
        {
            SyntaxNode root = tree.GetRoot();
            bool packetScope = InPacketScope(relative, root);
            if (packetScope)
            {
                PacketScopeFiles++;
            }

            foreach (SyntaxNode node in root.DescendantNodes())
            {
                switch (node)
                {
                    case BinaryPatternSyntax pattern:
                        CheckNotPattern(pattern, relative, hits);
                        break;
                    case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce:
                        CheckCoalesce(coalesce, relative, hits);
                        break;
                    case MethodDeclarationSyntax method:
                        CheckAsyncVoidMethod(method, relative, hits);
                        break;
                    case LocalFunctionStatementSyntax local:
                        CheckAsyncVoidLocal(local, relative, hits);
                        break;
                    case AnonymousFunctionExpressionSyntax lambda when lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword):
                        CheckAsyncLambda(lambda, project, tree, relative, hits);
                        break;
                    case CastExpressionSyntax cast when packetScope:
                        CheckCast(cast, project, tree, relative, hits);
                        break;
                }
            }
        }

        return [.. hits.OrderBy(h => h.File, StringComparer.Ordinal).ThenBy(h => h.Line)];
    }

    private static void CheckNotPattern(BinaryPatternSyntax pattern, string file, List<ReadsDifferentlyHit> hits)
    {
        if (pattern.Left is not UnaryPatternSyntax { OperatorToken.RawKind: (int)SyntaxKind.NotKeyword })
        {
            return;
        }

        if (pattern.IsKind(SyntaxKind.AndPattern) && pattern.Right is UnaryPatternSyntax { OperatorToken.RawKind: (int)SyntaxKind.NotKeyword })
        {
            return; // "is not A and not B": both operands negated, reads as it does ("is not A or not B" does not: De Morgan reads it as not (A or B))
        }

        SyntaxNode top = pattern;
        while (top.Parent is BinaryPatternSyntax parent)
        {
            top = parent;
        }

        SyntaxNode shown = top.Parent is IsPatternExpressionSyntax isPattern ? isPattern : top;
        hits.Add(Hit("RB001", file, pattern, shown));
    }

    private static void CheckCoalesce(BinaryExpressionSyntax coalesce, string file, List<ReadsDifferentlyHit> hits)
    {
        // "x as T ?? y" reads as it parses, and so does "x as T ?? y as U"; "x ?? y as T" casts only y.
        bool leftMixed = coalesce.Left.IsKind(SyntaxKind.AsExpression) ? false : CoalesceNeighbours.Contains(coalesce.Left.Kind());
        bool rightMixed = CoalesceNeighbours.Contains(coalesce.Right.Kind())
                          && !(coalesce.Right.IsKind(SyntaxKind.AsExpression) && coalesce.Left.IsKind(SyntaxKind.AsExpression));
        if (leftMixed || rightMixed)
        {
            hits.Add(Hit("RB003", file, coalesce, coalesce));
        }
    }

    private static void CheckAsyncVoidMethod(MethodDeclarationSyntax method, string file, List<ReadsDifferentlyHit> hits)
    {
        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword) && method.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword }
            && !LooksLikeEventHandler(method.ParameterList.Parameters))
        {
            hits.Add(Hit("RB004", file, method, method.Identifier.Parent!, $"async void {method.Identifier.Text}({method.ParameterList.Parameters})"));
        }
    }

    private static void CheckAsyncVoidLocal(LocalFunctionStatementSyntax local, string file, List<ReadsDifferentlyHit> hits)
    {
        if (local.Modifiers.Any(SyntaxKind.AsyncKeyword) && local.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword }
            && !LooksLikeEventHandler(local.ParameterList.Parameters))
        {
            hits.Add(Hit("RB004", file, local, local, $"async void {local.Identifier.Text}({local.ParameterList.Parameters})"));
        }
    }

    private void CheckAsyncLambda(AnonymousFunctionExpressionSyntax lambda, string project, SyntaxTree tree, string file, List<ReadsDifferentlyHit> hits)
    {
        SemanticModel model = Model(project, tree);
        AsyncLambdas++;
        ISymbol? resolved = model.GetSymbolInfo(lambda).Symbol;
        ITypeSymbol? delegateType = model.GetTypeInfo(lambda).ConvertedType;
        if (resolved is null || delegateType is null || delegateType.TypeKind == TypeKind.Error)
        {
            UnresolvedLambdas++;
            UnresolvedLambdaSites.Add($"{file}:{lambda.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
            return;
        }

        if (resolved is not IMethodSymbol { ReturnsVoid: true } symbol)
        {
            return;
        }

        if (symbol.Parameters.Length == 2 && InheritsFrom(symbol.Parameters[1].Type, "EventArgs"))
        {
            return;
        }

        hits.Add(Hit("RB004", file, lambda, lambda, "async " + Collapse(lambda.ToString(), 80)));
    }

    private void CheckCast(CastExpressionSyntax cast, string project, SyntaxTree tree, string file, List<ReadsDifferentlyHit> hits)
    {
        SemanticModel model = Model(project, tree);
        ITypeSymbol? target = model.GetTypeInfo(cast.Type).Type;
        if (target is null || !IsIntegral(target.SpecialType))
        {
            return; // not a cast to an integer (or a target that cannot be resolved; an unresolved name is not a predefined integer)
        }

        ITypeSymbol? source = model.GetTypeInfo(cast.Expression).Type;
        if (source is null || source.TypeKind == TypeKind.Error)
        {
            UnresolvedCasts++;
            return;
        }

        CastsClassified++;
        bool floating = source.SpecialType is SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal
                        || source.ToDisplayString() == "System.Half";
        if (!floating || StatesItsRounding(cast.Expression))
        {
            return;
        }

        hits.Add(Hit("RB002", file, cast, cast));
    }

    private static bool StatesItsRounding(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax p)
        {
            expression = p.Expression;
        }

        return expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } && RoundingMethods.Contains(access.Name.Identifier.Text);
    }

    private static bool IsIntegral(SpecialType type) => type is SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16
        or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64
        or SpecialType.System_UInt64 or SpecialType.System_IntPtr or SpecialType.System_UIntPtr or SpecialType.System_Char;

    private static bool InheritsFrom(ITypeSymbol type, string name)
    {
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (t.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeEventHandler(SeparatedSyntaxList<ParameterSyntax> parameters)
        => parameters.Count == 2 && parameters[1].Type is { } type && type.ToString().EndsWith("EventArgs", StringComparison.Ordinal);

    private static ReadsDifferentlyHit Hit(string rule, string file, SyntaxNode at, SyntaxNode shown, string? snippet = null)
        => new(rule, file, MemberName(at), snippet ?? Collapse(shown.ToString(), 110), at.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

    private static string Collapse(string text, int max)
    {
        string flat = Whitespace.Replace(text, " ").Trim();
        return flat.Length > max ? flat[..(max - 3)] + "..." : flat;
    }

    /// <summary>"Outer.Inner.Member" of the declarations that enclose the node.</summary>
    private static string MemberName(SyntaxNode node)
    {
        var names = new List<string>();
        for (SyntaxNode? n = node; n is not null; n = n.Parent)
        {
            string? name = n switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.Text,
                MethodDeclarationSyntax method => method.Identifier.Text,
                ConstructorDeclarationSyntax ctor => ctor.Identifier.Text,
                PropertyDeclarationSyntax property => property.Identifier.Text,
                IndexerDeclarationSyntax => "this[]",
                OperatorDeclarationSyntax op => "operator " + op.OperatorToken.Text,
                ConversionOperatorDeclarationSyntax => "conversion",
                FieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier.Text,
                EventDeclarationSyntax ev => ev.Identifier.Text,
                LocalFunctionStatementSyntax local => local.Identifier.Text,
                _ => null,
            };
            if (name is not null && !(n is LocalFunctionStatementSyntax && ReferenceEquals(n, node)))
            {
                names.Add(name);
            }
        }

        names.Reverse();
        return names.Count == 0 ? "(file)" : string.Join('.', names);
    }

    private SemanticModel Model(string project, SyntaxTree tree)
    {
        CSharpCompilation compilation = _compilations.GetOrAdd(project, p => new Lazy<CSharpCompilation>(() => CreateCompilation(p))).Value;
        return compilation.GetSemanticModel(tree);
    }

    private CSharpCompilation CreateCompilation(string project)
    {
        List<SyntaxTree> trees = [.. _trees.Where(t => t.Project == project).Select(t => t.Tree)];
        if (_globalUsings.TryGetValue(project, out SyntaxTree? usings))
        {
            trees.Add(usings);
        }
        MetadataReference[] references = [.. _references.Value.Where(r => !string.Equals(Path.GetFileNameWithoutExtension(r.Display), project, StringComparison.OrdinalIgnoreCase))];
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
            .WithMetadataImportOptions(MetadataImportOptions.All);
        return CSharpCompilation.Create(_assemblyName, trees, references, options);
    }

    /// <summary>The runtime's managed assemblies plus every managed assembly beside the test assembly, except the test assembly itself.</summary>
    private MetadataReference[] LoadReferences()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in new[] { RuntimeEnvironment.GetRuntimeDirectory(), AppContext.BaseDirectory })
        {
            foreach (string dll in Directory.EnumerateFiles(directory, "*.dll"))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.Equals(_assemblyName, StringComparison.OrdinalIgnoreCase) || byName.ContainsKey(name) || !HasMetadata(dll))
                {
                    continue;
                }

                byName[name] = dll;
            }
        }

        return [.. byName.Values.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }

    private static bool HasMetadata(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
