using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace ArcaneCore.Analyzers;

/// <summary>
/// The safety rules (docs/ops/memory-safety.md): thread ownership of world objects (ARC0001/ARC0002), banned unsafe APIs (ARC0003),
/// pooled-buffer ownership (ARC0004/ARC0005) and blocking task waits (ARC0006), each filtered through <see cref="SafetyBaseline"/>.
/// <para>
/// Threads and state: Roslyn runs the registered actions concurrently; all per-compilation state lives in <see cref="Run"/>, whose only mutable
/// members are concurrent collections. Findings are collected during the walk and reported once at compilation end, sorted by position, so
/// which occurrence of a baselined key is "the extra one" does not depend on scheduling.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SafetyAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => SafetyRules.All;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var run = new Run(start.Compilation, SafetyBaseline.Load(start.Options, start.CancellationToken));
            start.RegisterOperationAction(run.OnInvocation, OperationKind.Invocation);
            start.RegisterOperationAction(run.OnPropertyReference, OperationKind.PropertyReference);
            start.RegisterOperationAction(run.OnFieldReference, OperationKind.FieldReference);
            start.RegisterOperationAction(run.OnEventReference, OperationKind.EventReference);
            start.RegisterOperationAction(run.OnMethodReference, OperationKind.MethodReference);
            start.RegisterSymbolAction(run.OnMethodSymbol, SymbolKind.Method);
            start.RegisterCompilationEndAction(run.OnCompilationEnd);
        });
    }

    private sealed class Finding
    {
        public Finding(DiagnosticDescriptor rule, string key, Location location, string message)
        {
            Rule = rule;
            Key = key;
            Location = location;
            Message = message;
        }

        public DiagnosticDescriptor Rule { get; }

        public string Key { get; }

        public Location Location { get; }

        public string Message { get; }
    }

    private enum LambdaThread
    {
        Unknown,
        Off,
        World,
    }

    private sealed class Run
    {
        private const string WorldThreadOnly = "ArcaneCore.Kernel.Safety.WorldThreadOnlyAttribute";
        private const string AnyThread = "ArcaneCore.Kernel.Safety.AnyThreadAttribute";
        private const string RunsOnWorldThread = "ArcaneCore.Kernel.Safety.RunsOnWorldThreadAttribute";
        private const string AuditedUnsafe = "ArcaneCore.Kernel.Safety.AuditedUnsafeAttribute";

        private static readonly HashSet<string> UnsafeTypes = new(StringComparer.Ordinal)
        {
            "System.Runtime.CompilerServices.Unsafe",
            "System.Runtime.InteropServices.MemoryMarshal",
            "System.Runtime.InteropServices.NativeMemory",
            "System.Runtime.InteropServices.Marshal",
        };

        private static readonly HashSet<string> SafeMarshalMembers = new(StringComparer.Ordinal)
        {
            "GetLastPInvokeError", "GetLastWin32Error", "SetLastPInvokeError", "GetLastSystemError", "SetLastSystemError",
        };

        private readonly Compilation _compilation;
        private readonly SafetyBaseline _baseline;
        private readonly ConcurrentQueue<Finding> _findings = new();
        private readonly ConcurrentDictionary<ISymbol, bool> _worldOnly = new(SymbolEqualityComparer.Default);
        private readonly ConditionalWeakTable<ControlFlowGraph, FlowIndex> _flow = new();
        private readonly bool _assemblyAudited;

        public Run(Compilation compilation, SafetyBaseline baseline)
        {
            _compilation = compilation;
            _baseline = baseline;
            _assemblyAudited = HasAttribute(compilation.Assembly, AuditedUnsafe);
        }

        // ---- action entry points ------------------------------------------------------------------------------------------------

        public void OnInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            IMethodSymbol method = invocation.TargetMethod;

            CheckWorldThread(context, invocation, method);
            CheckUnsafeApi(context, invocation, method);
            CheckBlockingMethod(context, invocation, method);
            CheckPoolRent(context, invocation, method);
            CheckUseAfterReturn(invocation, method);
        }

        public void OnPropertyReference(OperationAnalysisContext context)
        {
            var reference = (IPropertyReferenceOperation)context.Operation;
            CheckWorldThread(context, reference, reference.Property);
            CheckBlockingProperty(context, reference);
        }

        public void OnFieldReference(OperationAnalysisContext context)
        {
            var reference = (IFieldReferenceOperation)context.Operation;
            if (!reference.Field.IsConst)
            {
                CheckWorldThread(context, reference, reference.Field);
            }
        }

        public void OnEventReference(OperationAnalysisContext context)
        {
            var reference = (IEventReferenceOperation)context.Operation;
            CheckWorldThread(context, reference, reference.Event);
        }

        /// <summary><c>Task.Run(player.Tick)</c>: a method group handed to an off-thread API runs its target there.</summary>
        public void OnMethodReference(OperationAnalysisContext context)
        {
            var reference = (IMethodReferenceOperation)context.Operation;
            if (!IsWorldThreadOnly(reference.Method))
            {
                return;
            }

            if (ClassifyArgumentTarget(reference, out string api) == LambdaThread.Off)
            {
                Add(SafetyRules.OffThreadWorldAccess, context.ContainingSymbol, reference.Syntax, Describe(reference.Method),
                    "World-thread-only " + Describe(reference.Method) + " is handed to " + api + " as a method group; post it to the world thread instead");
            }
        }

        /// <summary>P/Invoke declarations (a pseudo-attribute, so they are found on the symbol, not in an expression).</summary>
        public void OnMethodSymbol(SymbolAnalysisContext context)
        {
            var method = (IMethodSymbol)context.Symbol;
            bool pinvoke = method.GetDllImportData() is not null
                || method.GetAttributes().Any(a => a.AttributeClass is { Name: "LibraryImportAttribute" });
            if (!pinvoke || IsAudited(method) || method.Locations.Length == 0 || !method.Locations[0].IsInSource)
            {
                return;
            }

            Location where = method.Locations[0];
            Add(SafetyRules.UnsafeApi, method, where, "P/Invoke " + method.Name,
                "P/Invoke " + method.Name + " needs [AuditedUnsafe(\"reason\")] on it, its type or the assembly");
        }

        public void OnCompilationEnd(CompilationAnalysisContext context)
        {
            if (_compilation.Options is CSharpCompilationOptions { AllowUnsafe: true } && !_assemblyAudited)
            {
                string key = Key(SafetyRules.UnsafeApi, "assembly:" + _compilation.AssemblyName, "<assembly>", "AllowUnsafeBlocks");
                _findings.Enqueue(new Finding(SafetyRules.UnsafeApi, key, Location.None,
                    "AllowUnsafeBlocks is on for " + _compilation.AssemblyName + " without an assembly-level [AuditedUnsafe(\"reason\")]. " + Suffix(key)));
            }

            foreach ((string text, Location where) in _baseline.Malformed)
            {
                context.ReportDiagnostic(Diagnostic.Create(SafetyRules.MalformedBaseline, where, "Malformed safety baseline line: " + text));
            }

            Dictionary<string, List<Finding>> byKey = _findings.GroupBy(f => f.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            foreach (KeyValuePair<string, List<Finding>> pair in byKey)
            {
                _baseline.Allowed.TryGetValue(pair.Key, out int allowed);
                List<Finding> ordered = pair.Value
                    .OrderBy(f => f.Location.SourceTree?.FilePath ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(f => f.Location.SourceSpan.Start)
                    .ToList();
                foreach (Finding extra in ordered.Skip(allowed))
                {
                    context.ReportDiagnostic(Diagnostic.Create(extra.Rule, extra.Location, extra.Message));
                }
            }

            // A baseline entry for a file this compilation owns must still be fully used; entries of other projects are not ours to judge.
            HashSet<string> ownedPaths = new(
                _compilation.SyntaxTrees.Select(t => _baseline.Relative(t.FilePath)), StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> entry in _baseline.Allowed)
            {
                string[] parts = entry.Key.Split('|');
                string path = parts[1];
                bool owned = path.StartsWith("assembly:", StringComparison.Ordinal)
                    ? string.Equals(path.Substring("assembly:".Length), _compilation.AssemblyName, StringComparison.Ordinal)
                    : ownedPaths.Contains(path);
                if (!owned)
                {
                    continue;
                }

                int found = byKey.TryGetValue(entry.Key, out List<Finding>? list) ? list.Count : 0;
                if (found < entry.Value)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SafetyRules.StaleBaseline,
                        Location.None,
                        "safety-baseline.txt allows " + entry.Value + " of '" + entry.Key + "' but the code has " + found + "; lower the count to " + found
                        + (found == 0 ? " (remove the line)" : string.Empty)));
                }
            }
        }

        // ---- ARC0001 / ARC0002: thread ownership --------------------------------------------------------------------------------

        private void CheckWorldThread(OperationAnalysisContext context, IOperation operation, ISymbol referenced)
        {
            if (!IsWorldThreadOnly(referenced))
            {
                return;
            }

            string what = Describe(referenced);
            int position = EvaluationPoint(operation);
            SyntaxNode reference = operation.Syntax;
            bool awaitSearchClosed = false;

            for (IOperation? parent = operation.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is INameOfOperation)
                {
                    return; // a compile-time constant: nothing is accessed
                }

                if (parent is IAnonymousFunctionOperation lambda)
                {
                    switch (ClassifyArgumentTarget(lambda, out string api))
                    {
                        case LambdaThread.Off:
                            Add(SafetyRules.OffThreadWorldAccess, context.ContainingSymbol, operation.Syntax, what,
                                "World-thread-only " + what + " is used in a callback passed to " + api + "; post the work to the world thread (WorldRuntime.Post)");
                            return;
                        case LambdaThread.World:
                            return;
                    }
                }

                if (!awaitSearchClosed && IsFunctionBoundary(parent))
                {
                    if (SuspendsBefore(context, parent, reference, position))
                    {
                        ReportAfterAwait(context, operation, what);
                        return;
                    }

                    if (parent is ILocalFunctionOperation)
                    {
                        // A local function can be called from anywhere in its enclosing member, so where it is declared says nothing
                        // about what ran before it: only suspensions inside the local function itself count. The walk goes on, though,
                        // because an enclosing lambda still decides which thread the local function runs on (ARC0001).
                        awaitSearchClosed = true;
                    }
                }
            }

            if (!awaitSearchClosed && SuspendsBefore(context, Root(operation), reference, position))
            {
                ReportAfterAwait(context, operation, what);
            }
        }

        private void ReportAfterAwait(OperationAnalysisContext context, IOperation operation, string what)
            => Add(SafetyRules.WorldAccessAfterAwait, context.ContainingSymbol, operation.Syntax, what,
                "World-thread-only " + what + " is used after an await; hop back with WorldRuntime.Post/InvokeAsync first");

        private static bool IsFunctionBoundary(IOperation operation) => operation is IAnonymousFunctionOperation or ILocalFunctionOperation;

        private static IOperation Root(IOperation operation)
        {
            IOperation current = operation;
            while (current.Parent is not null)
            {
                current = current.Parent;
            }

            return current;
        }

        /// <summary>
        /// Where the member access really happens: an assignment target is written after its value is (<c>e.Level = await x</c>); anything else
        /// runs once its receiver and arguments are evaluated, so at its end (<c>e.Set(await x)</c>, <c>(await t).Level</c>).
        /// </summary>
        private static int EvaluationPoint(IOperation operation)
        {
            return operation.Parent is IAssignmentOperation assignment && ReferenceEquals(assignment.Target, operation)
                ? assignment.Syntax.Span.End
                : operation.Syntax.Span.End;
        }

        /// <summary>
        /// Whether <paramref name="function"/> (a lambda, local function or the root of a member) itself suspends at a point from which
        /// <paramref name="reference"/> (a node inside it, evaluated at <paramref name="position"/>) can still run: decided on the Roslyn
        /// control-flow graph of the function (<see cref="FlowIndex"/>). Without a graph (an operation root the graph does not model) every
        /// suspension that ends before <paramref name="position"/> counts, which can only over-report.
        /// </summary>
        private bool SuspendsBefore(OperationAnalysisContext context, IOperation function, SyntaxNode reference, int position)
        {
            ControlFlowGraph? graph = FunctionGraph(context, function);
            if (graph is null)
            {
                return function.Descendants().Any(d => d is IAwaitOperation && d.Syntax.Span.End <= position && ReferenceEquals(NearestFunction(d), function));
            }

            return _flow.GetValue(graph, static g => new FlowIndex(g)).SuspendsBefore(reference, function.Syntax, position);
        }

        /// <summary>
        /// The control-flow graph of <paramref name="function"/>: the graph of the member (built once per operation block by the analyzer driver
        /// and shared by every action) for the root, and for a lambda or local function the nested graph found by walking the chain of enclosing
        /// functions down from the root. Null when the root is not something a graph is defined for (an attribute, for instance).
        /// </summary>
        private ControlFlowGraph? FunctionGraph(OperationAnalysisContext context, IOperation function)
        {
            if (Root(function) is not (IBlockOperation or IMethodBodyBaseOperation or IFieldInitializerOperation or IPropertyInitializerOperation or IParameterInitializerOperation))
            {
                return null;
            }

            var chain = new List<IOperation>();
            for (IOperation? current = function; current is not null; current = current.Parent)
            {
                if (IsFunctionBoundary(current))
                {
                    chain.Add(current);
                }
            }

            ControlFlowGraph? graph = context.GetControlFlowGraph();
            for (int i = chain.Count - 1; i >= 0 && graph is not null; i--)
            {
                switch (chain[i])
                {
                    case ILocalFunctionOperation local:
                        graph = graph.LocalFunctions.Contains(local.Symbol, SymbolEqualityComparer.Default)
                            ? graph.GetLocalFunctionControlFlowGraph(local.Symbol, context.CancellationToken)
                            : null;
                        break;
                    case IAnonymousFunctionOperation lambda:
                        IFlowAnonymousFunctionOperation? flow = _flow.GetValue(graph, static g => new FlowIndex(g)).FindLambda(lambda.Syntax);
                        graph = flow is null ? null : graph.GetAnonymousFunctionControlFlowGraph(flow, context.CancellationToken);
                        break;
                    default:
                        graph = null;
                        break;
                }
            }

            return graph;
        }

        private static IOperation NearestFunction(IOperation operation)
        {
            IOperation current = operation;
            while (current.Parent is not null)
            {
                current = current.Parent;
                if (IsFunctionBoundary(current))
                {
                    return current;
                }
            }

            return current;
        }

        /// <summary>
        /// Reachability questions over the <see cref="ControlFlowGraph"/> of one function, built once per graph. The graph keeps <c>await</c> as an
        /// expression (an <see cref="IAwaitOperation"/> inside the operations or branch value of a block) and lowers <c>await foreach</c> and
        /// <c>await using</c> to awaits of <c>MoveNextAsync</c>/<c>DisposeAsync</c>, so one rule covers all three: a reference runs after a
        /// suspension when it is later in the same block (by operation index, then by evaluation point inside one operation) or its block is
        /// reachable from the block of the suspension. Successors are the normal and conditional branches, the finally regions a branch passes
        /// through, the handlers of every enclosing try (an await can complete with an exception, so the catch and finally of each try around it
        /// are continuations too), and for the end of a finally region the suspension itself is in, the destinations of every branch that runs that finally.
        /// </summary>
        private sealed class FlowIndex
        {
            private readonly ControlFlowGraph _graph;
            private readonly Dictionary<SyntaxNode, List<Site>> _sites = new();
            private readonly List<Site> _awaits = new();
            private readonly List<IFlowAnonymousFunctionOperation> _lambdas = new();
            private readonly Dictionary<ControlFlowRegion, List<int>> _finallyExits = new();
            private readonly Dictionary<int, HashSet<int>> _reachable = new();

            public FlowIndex(ControlFlowGraph graph)
            {
                _graph = graph;
                foreach (BasicBlock block in graph.Blocks)
                {
                    for (int i = 0; i < block.Operations.Length; i++)
                    {
                        Visit(block.Operations[i], block.Ordinal, i);
                    }

                    if (block.BranchValue is { } value)
                    {
                        Visit(value, block.Ordinal, block.Operations.Length);
                    }

                    foreach (ControlFlowBranch? branch in new[] { block.FallThroughSuccessor, block.ConditionalSuccessor })
                    {
                        if (branch?.Destination is not { } destination)
                        {
                            continue;
                        }

                        foreach (ControlFlowRegion region in branch.FinallyRegions)
                        {
                            if (!_finallyExits.TryGetValue(region, out List<int>? exits))
                            {
                                _finallyExits[region] = exits = new List<int>();
                            }

                            exits.Add(destination.Ordinal);
                        }
                    }
                }
            }

            private readonly struct Site
            {
                public Site(int block, int index, int end)
                {
                    Block = block;
                    Index = index;
                    End = end;
                }

                public int Block { get; }

                /// <summary>Position in the block: the index into <see cref="BasicBlock.Operations"/>, or their count for the branch value.</summary>
                public int Index { get; }

                /// <summary>Syntax end; orders a suspension against a reference inside the same operation.</summary>
                public int End { get; }
            }

            public IFlowAnonymousFunctionOperation? FindLambda(SyntaxNode syntax)
                => _lambdas.Find(l => ReferenceEquals(l.Syntax, syntax));

            public bool SuspendsBefore(SyntaxNode reference, SyntaxNode function, int position)
            {
                if (_awaits.Count == 0)
                {
                    return false;
                }

                List<Site>? sites = Locate(reference, function);
                if (sites is null)
                {
                    // Not modelled by the graph (a constant-folded nameof, for instance): the positional answer, which can only over-report.
                    return _awaits.Any(a => a.End <= position);
                }

                foreach (Site await in _awaits)
                {
                    foreach (Site site in sites)
                    {
                        if (await.Block == site.Block && (await.Index < site.Index || (await.Index == site.Index && await.End <= position)))
                        {
                            return true;
                        }

                        if (ReachableFrom(await.Block).Contains(site.Block))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private void Visit(IOperation operation, int block, int index)
            {
                foreach (IOperation node in operation.DescendantsAndSelf())
                {
                    if (!_sites.TryGetValue(node.Syntax, out List<Site>? sites))
                    {
                        _sites[node.Syntax] = sites = new List<Site>();
                    }

                    sites.Add(new Site(block, index, node.Syntax.Span.End));
                    switch (node)
                    {
                        case IAwaitOperation:
                            _awaits.Add(new Site(block, index, node.Syntax.Span.End));
                            break;
                        case IFlowAnonymousFunctionOperation lambda:
                            _lambdas.Add(lambda);
                            break;
                    }
                }
            }

            /// <summary>
            /// The graph operations whose syntax is <paramref name="reference"/>, or else the nearest enclosing node the graph does have: a lambda
            /// (whose body is a graph of its own) or a construct the graph replaced by one operation. Null once the walk reaches the function itself.
            /// </summary>
            private List<Site>? Locate(SyntaxNode reference, SyntaxNode function)
            {
                for (SyntaxNode? node = reference; node is not null && !ReferenceEquals(node, function); node = node.Parent)
                {
                    if (_sites.TryGetValue(node, out List<Site>? sites))
                    {
                        return sites;
                    }
                }

                return null;
            }

            private HashSet<int> ReachableFrom(int start)
            {
                lock (_reachable)
                {
                    if (_reachable.TryGetValue(start, out HashSet<int>? cached))
                    {
                        return cached;
                    }

                    // The finally regions the suspension itself sits in: when their end is reached, control goes on to wherever the branch that
                    // entered them was heading. The end of any other finally met on the way adds nothing: a branch that runs it already contributed
                    // its destination, and an exception that runs it keeps propagating (the region walk in Successors).
                    var own = new HashSet<ControlFlowRegion>();
                    for (ControlFlowRegion? region = _graph.Blocks[start].EnclosingRegion; region is not null; region = region.EnclosingRegion)
                    {
                        if (region.Kind == ControlFlowRegionKind.Finally)
                        {
                            own.Add(region);
                        }
                    }

                    var seen = new HashSet<int>();
                    var queue = new Queue<int>();
                    foreach (int next in Successors(start, own))
                    {
                        if (seen.Add(next))
                        {
                            queue.Enqueue(next);
                        }
                    }

                    while (queue.Count > 0)
                    {
                        foreach (int next in Successors(queue.Dequeue(), own))
                        {
                            if (seen.Add(next))
                            {
                                queue.Enqueue(next);
                            }
                        }
                    }

                    _reachable[start] = seen;
                    return seen;
                }
            }

            private IEnumerable<int> Successors(int ordinal, HashSet<ControlFlowRegion> ownFinallies)
            {
                BasicBlock block = _graph.Blocks[ordinal];
                foreach (ControlFlowBranch? branch in new[] { block.FallThroughSuccessor, block.ConditionalSuccessor })
                {
                    if (branch is null)
                    {
                        continue;
                    }

                    if (branch.Destination is { } destination)
                    {
                        yield return destination.Ordinal;
                    }

                    foreach (ControlFlowRegion region in branch.FinallyRegions)
                    {
                        yield return region.FirstBlockOrdinal;
                    }

                    if (branch.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling)
                    {
                        // The end of a finally the suspension is inside: control goes on to wherever the branch that ran this finally was heading.
                        // (The end of a filter that rejected the exception has no normal successor; the exception keeps propagating, see below.)
                        for (ControlFlowRegion? region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                        {
                            if (region.Kind == ControlFlowRegionKind.Finally)
                            {
                                if (ownFinallies.Contains(region) && _finallyExits.TryGetValue(region, out List<int>? exits))
                                {
                                    foreach (int exit in exits)
                                    {
                                        yield return exit;
                                    }
                                }

                                break;
                            }
                        }
                    }
                }

                // Anything in a try can throw (an await included): the catch, filter and finally regions of every enclosing try are successors too.
                // Walking all the way up also covers an exception leaving a catch or a finally for the next handler out.
                for (ControlFlowRegion? region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                {
                    if (region.Kind != ControlFlowRegionKind.Try || region.EnclosingRegion is not { } owner)
                    {
                        continue;
                    }

                    foreach (ControlFlowRegion sibling in owner.NestedRegions)
                    {
                        if (!ReferenceEquals(sibling, region))
                        {
                            yield return sibling.FirstBlockOrdinal;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// What runs the delegate <paramref name="function"/> (a lambda or method group): found from the call it is an argument of. Unknown when it is
        /// assigned, returned or passed to anything not recognised.
        /// </summary>
        private LambdaThread ClassifyArgumentTarget(IOperation function, out string api)
        {
            api = string.Empty;
            IOperation? current = function.Parent;
            while (current is IDelegateCreationOperation or IConversionOperation or IParenthesizedOperation or IArrayInitializerOperation or IArrayCreationOperation)
            {
                current = current.Parent;
            }

            if (current is not IArgumentOperation argument)
            {
                return LambdaThread.Unknown;
            }

            IMethodSymbol? method = argument.Parent switch
            {
                IInvocationOperation invocation => invocation.TargetMethod,
                IObjectCreationOperation creation => creation.Constructor,
                _ => null,
            };
            if (method is null)
            {
                return LambdaThread.Unknown;
            }

            api = method.MethodKind == MethodKind.Constructor ? "new " + method.ContainingType.Name : method.ContainingType.Name + "." + method.Name;
            if (HasAttribute(argument.Parameter, RunsOnWorldThread) || HasAttribute(method, RunsOnWorldThread)
                || HasAttribute(method.OriginalDefinition, RunsOnWorldThread))
            {
                return LambdaThread.World;
            }

            return IsOffThreadApi(method) ? LambdaThread.Off : LambdaThread.Unknown;
        }

        private static bool IsOffThreadApi(IMethodSymbol method)
        {
            string type = FullName(method.ContainingType);
            string name = method.Name;
            switch (type)
            {
                case "System.Threading.Tasks.Task":
                    return name is "Run" or "ContinueWith";
                case "System.Threading.Tasks.TaskFactory":
                    return name is "StartNew" or "ContinueWhenAll" or "ContinueWhenAny" or "FromAsync";
                case "System.Threading.ThreadPool":
                    return name is "QueueUserWorkItem" or "UnsafeQueueUserWorkItem" or "RegisterWaitForSingleObject" or "UnsafeRegisterWaitForSingleObject";
                case "System.Threading.Tasks.Parallel":
                    return name is "For" or "ForEach" or "Invoke" or "ForAsync" or "ForEachAsync";
                case "System.Threading.Timer":
                case "System.Threading.Thread":
                    return method.MethodKind == MethodKind.Constructor;
                case "System.Threading.CancellationToken":
                    return name is "Register" or "UnsafeRegister";
                default:
                    return false;
            }
        }

        private bool IsWorldThreadOnly(ISymbol symbol)
        {
            return _worldOnly.GetOrAdd(symbol.OriginalDefinition, static s =>
            {
                if (HasAttribute(s, AnyThread))
                {
                    return false;
                }

                if (HasAttribute(s, WorldThreadOnly))
                {
                    return true;
                }

                for (INamedTypeSymbol? type = s.ContainingType; type is not null; type = type.BaseType)
                {
                    if (HasAttribute(type.OriginalDefinition, WorldThreadOnly))
                    {
                        return true;
                    }
                }

                return false;
            });
        }

        // ---- ARC0003: unsafe APIs ----------------------------------------------------------------------------------------------------

        private void CheckUnsafeApi(OperationAnalysisContext context, IInvocationOperation invocation, IMethodSymbol method)
        {
            if (!UnsafeTypes.Contains(FullName(method.ContainingType))
                || (FullName(method.ContainingType).EndsWith(".Marshal", StringComparison.Ordinal) && SafeMarshalMembers.Contains(method.Name)))
            {
                return;
            }

            if (IsAudited(context.ContainingSymbol))
            {
                return;
            }

            string what = method.ContainingType.Name + "." + method.Name;
            Add(SafetyRules.UnsafeApi, context.ContainingSymbol, invocation.Syntax, what,
                what + " needs [AuditedUnsafe(\"reason\")] on the member, its type or the assembly");
        }

        private bool IsAudited(ISymbol? symbol)
        {
            if (_assemblyAudited)
            {
                return true;
            }

            for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
            {
                if (HasAttribute(current, AuditedUnsafe) || (current is IMethodSymbol { AssociatedSymbol: { } associated } && HasAttribute(associated, AuditedUnsafe)))
                {
                    return true;
                }

                if (current is INamespaceSymbol)
                {
                    break;
                }
            }

            return false;
        }

        // ---- ARC0006: blocking waits ---------------------------------------------------------------------------------------------------

        private void CheckBlockingMethod(OperationAnalysisContext context, IInvocationOperation invocation, IMethodSymbol method)
        {
            INamedTypeSymbol type = method.ContainingType;
            bool blocking = method.Name switch
            {
                "GetResult" => IsTaskAwaiter(type),
                "Wait" => !method.IsStatic && DerivesFromTask(type),
                "WaitAll" or "WaitAny" => method.IsStatic && FullName(type) == "System.Threading.Tasks.Task",
                _ => false,
            };

            if (blocking)
            {
                string what = (type.ContainingType is null ? type.Name : type.ContainingType.Name) + "." + method.Name;
                Add(SafetyRules.BlockingWait, context.ContainingSymbol, invocation.Syntax, what,
                    what + " blocks the calling thread; await it, or post the continuation to the world thread");
            }
        }

        private static bool IsTaskAwaiter(INamedTypeSymbol type)
        {
            INamedTypeSymbol top = type;
            while (top.ContainingType is not null)
            {
                top = top.ContainingType;
            }

            return FullName(top.ContainingNamespace) == "System.Runtime.CompilerServices"
                && top.Name is "TaskAwaiter" or "ConfiguredTaskAwaitable" or "ValueTaskAwaiter" or "ConfiguredValueTaskAwaitable";
        }

        private void CheckBlockingProperty(OperationAnalysisContext context, IPropertyReferenceOperation reference)
        {
            IPropertySymbol property = reference.Property;
            if (property.Name != "Result" || property.ContainingType is not { } type)
            {
                return;
            }

            if (DerivesFromTask(type) || FullName(type) == "System.Threading.Tasks.ValueTask")
            {
                Add(SafetyRules.BlockingWait, context.ContainingSymbol, reference.Syntax, type.Name + ".Result",
                    type.Name + ".Result blocks until the task completes (and wraps its exception); await it, or post the continuation to the world thread");
            }
        }

        private static bool DerivesFromTask(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                if (FullName(current) == "System.Threading.Tasks.Task")
                {
                    return true;
                }
            }

            return false;
        }

        // ---- ARC0004 / ARC0005: pooled buffers --------------------------------------------------------------------------------

        private void CheckPoolRent(OperationAnalysisContext context, IInvocationOperation invocation, IMethodSymbol method)
        {
            if (method.Name != "Rent")
            {
                return;
            }

            string owner = FullName(method.ContainingType);
            if (owner == "System.Buffers.ArrayPool")
            {
                IOperation root = Root(invocation);
                bool returned = root.Descendants().OfType<IInvocationOperation>().Any(i => IsPoolReturn(i.TargetMethod));
                if (!returned)
                {
                    Add(SafetyRules.PoolRentWithoutReturn, context.ContainingSymbol, invocation.Syntax, "ArrayPool.Rent",
                        "ArrayPool.Rent has no ArrayPool.Return in the same member; if ownership is handed off on purpose, baseline it with the owner named");
                }
            }
            else if (owner == "System.Buffers.MemoryPool" && !IsInUsing(invocation))
            {
                Add(SafetyRules.PoolRentWithoutReturn, context.ContainingSymbol, invocation.Syntax, "MemoryPool.Rent",
                    "MemoryPool.Rent returns an IMemoryOwner that must be disposed; declare it with 'using'");
            }
        }

        private static bool IsPoolReturn(IMethodSymbol method) => method.Name == "Return" && FullName(method.ContainingType) == "System.Buffers.ArrayPool";

        private static bool IsInUsing(IOperation operation)
        {
            for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
            {
                if (current is IUsingOperation or IUsingDeclarationOperation)
                {
                    return true;
                }

                if (current is IBlockOperation or IAnonymousFunctionOperation)
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// <c>pool.Return(x);</c> followed, in the same block (or after the try whose finally holds it), by a statement that reads <c>x</c> before
        /// any reassignment. Straight-line only: conditional returns and loops are not followed, which keeps the rule free of false positives.
        /// </summary>
        private void CheckUseAfterReturn(IInvocationOperation invocation, IMethodSymbol method)
        {
            if (!IsPoolReturn(method) || invocation.Arguments.Length == 0 || invocation.SemanticModel is not { } model)
            {
                return;
            }

            ISymbol? buffer = StripConversions(invocation.Arguments[0].Value) switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                IFieldReferenceOperation field => field.Field,
                _ => null,
            };
            if (buffer is null || invocation.Syntax.Parent is not ExpressionStatementSyntax statement)
            {
                return;
            }

            foreach (StatementSyntax later in StatementsAfter(statement))
            {
                if (later is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } assignment }
                    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(left).Symbol, buffer)
                    && !ReadsSymbol(assignment.Right, buffer, model))
                {
                    return;
                }

                IdentifierNameSyntax? use = later.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .FirstOrDefault(id => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, buffer));
                if (use is not null)
                {
                    ISymbol? container = model.GetEnclosingSymbol(use.SpanStart);
                    Add(SafetyRules.UseAfterReturn, container, use, buffer.Name,
                        "'" + buffer.Name + "' is used after ArrayPool.Return gave it back; another renter may already be writing to it");
                    return;
                }
            }
        }

        private static bool ReadsSymbol(SyntaxNode node, ISymbol symbol, SemanticModel model)
            => node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, symbol));

        private static IOperation StripConversions(IOperation operation)
        {
            IOperation current = operation;
            while (current is IConversionOperation conversion)
            {
                current = conversion.Operand;
            }

            return current;
        }

        private static IEnumerable<StatementSyntax> StatementsAfter(StatementSyntax statement)
        {
            if (statement.Parent is not BlockSyntax block)
            {
                yield break;
            }

            bool after = false;
            foreach (StatementSyntax sibling in block.Statements)
            {
                if (after)
                {
                    yield return sibling;
                }
                else if (sibling == statement)
                {
                    after = true;
                }
            }

            if (block.Parent is FinallyClauseSyntax { Parent: TryStatementSyntax tryStatement })
            {
                foreach (StatementSyntax next in StatementsAfter(tryStatement))
                {
                    yield return next;
                }
            }
        }

        // ---- shared helpers -------------------------------------------------------------------------------------------------------

        private void Add(DiagnosticDescriptor rule, ISymbol? containing, SyntaxNode node, string detail, string message)
            => Add(rule, containing, node.GetLocation(), detail, message);

        private void Add(DiagnosticDescriptor rule, ISymbol? containing, Location location, string detail, string message)
        {
            string path = _baseline.Relative(location.SourceTree?.FilePath ?? string.Empty);
            string key = Key(rule, path, MemberName(containing), detail);
            _findings.Enqueue(new Finding(rule, key, location, message + ". " + Suffix(key)));
        }

        private static string Key(DiagnosticDescriptor rule, string path, string member, string detail) => rule.Id + "|" + path + "|" + member + "|" + detail;

        private static string Suffix(string key) => "Baseline line: " + key + "|1";

        private static string MemberName(ISymbol? symbol)
        {
            if (symbol is IMethodSymbol { AssociatedSymbol: { } associated })
            {
                symbol = associated;
            }

            if (symbol is null)
            {
                return "<unknown>";
            }

            string name = symbol.Name;
            for (INamedTypeSymbol? type = symbol.ContainingType; type is not null; type = type.ContainingType)
            {
                name = type.Name + "." + name;
            }

            return name;
        }

        private static string Describe(ISymbol symbol) => symbol.ContainingType is { } type ? type.Name + "." + symbol.Name : symbol.Name;

        private static string FullName(ISymbol symbol)
        {
            if (symbol.ContainingType is { } outer)
            {
                return FullName(outer) + "." + symbol.Name;
            }

            return symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? FullName(ns) + "." + symbol.Name : symbol.Name;
        }

        private static bool HasAttribute(ISymbol? symbol, string fullName)
        {
            if (symbol is null)
            {
                return false;
            }

            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                if (attribute.AttributeClass is { } type && FullName(type) == fullName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
