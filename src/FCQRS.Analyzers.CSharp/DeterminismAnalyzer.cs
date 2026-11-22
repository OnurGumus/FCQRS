using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FCQRS.Analyzers.CSharp;

/// <summary>
/// Warns when a workflow body or an aggregate's fold reads the clock, a GUID generator, a random
/// source, or the environment. A workflow body is a method that returns <c>WorkflowTask</c>: it runs
/// again on every recovery. A fold is an override of <c>Aggregate.ApplyEvent</c>: it runs again for
/// every stored event on every recovery. A lambda over <c>AttemptContext</c> is an activity's call,
/// whose result is stored, so it is not checked.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeterminismAnalyzer : DiagnosticAnalyzer
{
    const string Help = "https://onurgumus.github.io/FCQRS/concepts/workflows.html#The-body-must-be-deterministic";

    public static readonly DiagnosticDescriptor Body = new(
        "FCQRS001",
        "A workflow body must be deterministic",
        "A workflow body {0}. The body runs again on every recovery and must take the same path: read the time with ctx.Now(), make IDs with ctx.NewGuid() and random numbers with ctx.Random(), or move the call into an activity.",
        "FCQRS",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: Help);

    public static readonly DiagnosticDescriptor Fold = new(
        "FCQRS002",
        "An aggregate's fold must be deterministic",
        "An aggregate's fold {0}. The fold runs again for every stored event on recovery and must give the same state: read the value in HandleCommand and store it in the event.",
        "FCQRS",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: Help);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Body, Fold);

    // Reads whose value changes between runs, by containing type and member name, with the name a
    // message shows.
    static readonly Dictionary<(string Type, string Member), string> Reads = new()
    {
        [("System.DateTime", "Now")] = "DateTime.Now",
        [("System.DateTime", "UtcNow")] = "DateTime.UtcNow",
        [("System.DateTime", "Today")] = "DateTime.Today",
        [("System.DateTimeOffset", "Now")] = "DateTimeOffset.Now",
        [("System.DateTimeOffset", "UtcNow")] = "DateTimeOffset.UtcNow",
        [("System.TimeProvider", "GetUtcNow")] = "TimeProvider.GetUtcNow",
        [("System.TimeProvider", "GetLocalNow")] = "TimeProvider.GetLocalNow",
        [("System.TimeProvider", "GetTimestamp")] = "TimeProvider.GetTimestamp",
        [("System.Diagnostics.Stopwatch", "StartNew")] = "Stopwatch.StartNew",
        [("System.Diagnostics.Stopwatch", "GetTimestamp")] = "Stopwatch.GetTimestamp",
        [("System.Environment", "TickCount")] = "Environment.TickCount",
        [("System.Environment", "TickCount64")] = "Environment.TickCount64",
        [("System.Environment", "GetEnvironmentVariable")] = "Environment.GetEnvironmentVariable",
        [("System.Environment", "GetEnvironmentVariables")] = "Environment.GetEnvironmentVariables",
        [("System.Guid", "NewGuid")] = "Guid.NewGuid",
        [("System.Guid", "CreateVersion7")] = "Guid.CreateVersion7",
        [("System.Random", ".ctor")] = "new Random",
        [("System.Random", "Shared")] = "Random.Shared",
    };

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var workflowTask = start.Compilation.GetTypeByMetadataName("FCQRS.Workflows.CSharp.WorkflowTask`1");
            var attemptContext = start.Compilation.GetTypeByMetadataName("FCQRS.Workflows.AttemptContext");
            var aggregate = start.Compilation.GetTypeByMetadataName("FCQRS.CSharp+Aggregate`3");
            if (workflowTask is null && aggregate is null)
                return;
            var checker = new Checker(start.Compilation, attemptContext);
            start.RegisterSyntaxNodeAction(node =>
            {
                var method = (MethodDeclarationSyntax)node.Node;
                if (node.SemanticModel.GetDeclaredSymbol(method, node.CancellationToken) is not IMethodSymbol symbol)
                    return;
                DiagnosticDescriptor? rule =
                    workflowTask is not null && SymbolEqualityComparer.Default.Equals(symbol.ReturnType.OriginalDefinition, workflowTask) ? Body
                    : aggregate is not null && IsApplyEvent(symbol, aggregate) ? Fold
                    : null;
                if (rule is null)
                    return;
                SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body is null)
                    return;
                foreach (var (location, what) in checker.Reads(body, node.SemanticModel))
                    node.ReportDiagnostic(Diagnostic.Create(rule, location, what));
            }, SyntaxKind.MethodDeclaration);
        });
    }

    static bool IsApplyEvent(IMethodSymbol method, INamedTypeSymbol aggregate)
    {
        for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
            if (overridden.Name == "ApplyEvent" && SymbolEqualityComparer.Default.Equals(overridden.ContainingType.OriginalDefinition, aggregate))
                return true;
        return false;
    }

    /// <summary>Finds the reads in a body, and the calls it makes to source methods that read.</summary>
    sealed class Checker(Compilation compilation, INamedTypeSymbol? attemptContext)
    {
        // The first read each source method makes; null when it makes none, or while it is being
        // looked at, so recursion ends.
        readonly ConcurrentDictionary<IMethodSymbol, string?> methods = new(SymbolEqualityComparer.Default);

        public IEnumerable<(Location Location, string What)> Reads(SyntaxNode body, SemanticModel model)
        {
            foreach (var node in Nodes(body, model))
            {
                var symbol = model.GetSymbolInfo(node).Symbol;
                if (symbol is null)
                    continue;
                if (ReadOf(symbol) is { } read)
                    yield return (node.GetLocation(), $"reads {read}");
                else if (symbol is IMethodSymbol method && FirstReadOf(method) is { } inner)
                    yield return (node.GetLocation(), $"calls {method.Name}, which reads {inner}");
            }
        }

        // The member accesses, invocations, and object creations in a body, without the lambdas
        // that are activities.
        IEnumerable<SyntaxNode> Nodes(SyntaxNode body, SemanticModel model) =>
            body.DescendantNodes(child => !IsActivity(child, model))
                .Where(node => node is InvocationExpressionSyntax
                    || node is BaseObjectCreationExpressionSyntax
                    || (node is MemberAccessExpressionSyntax access && access.Parent is not InvocationExpressionSyntax)
                    || (node is IdentifierNameSyntax && node.Parent is not MemberAccessExpressionSyntax && node.Parent is not InvocationExpressionSyntax));

        bool IsActivity(SyntaxNode node, SemanticModel model) =>
            attemptContext is not null
            && node is AnonymousFunctionExpressionSyntax lambda
            && model.GetSymbolInfo(lambda).Symbol is IMethodSymbol function
            && function.Parameters.Any(parameter => SymbolEqualityComparer.Default.Equals(parameter.Type, attemptContext));

        static string? ReadOf(ISymbol symbol)
        {
            var member = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ? ".ctor" : symbol.Name;
            var type = symbol.ContainingType?.OriginalDefinition.ToDisplayString();
            return type is not null && DeterminismAnalyzer.Reads.TryGetValue((type, member), out var name) ? name : null;
        }

        string? FirstReadOf(IMethodSymbol method)
        {
            method = method.OriginalDefinition;
            if (method.DeclaringSyntaxReferences.IsEmpty)
                return null;
            if (methods.TryGetValue(method, out var known))
                return known;
            methods[method] = null;
            string? found = null;
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax();
                SyntaxNode? body = syntax switch
                {
                    MethodDeclarationSyntax declaration => (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody,
                    LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody,
                    _ => null,
                };
                if (body is null)
                    continue;
#pragma warning disable RS1030 // The method's body can be in another tree of the same compilation.
                var model = compilation.GetSemanticModel(syntax.SyntaxTree);
#pragma warning restore RS1030
                found = Reads(body, model).Select(read => read.What.StartsWith("reads ") ? read.What.Substring(6) : read.What.Substring(read.What.LastIndexOf("reads ") + 6)).FirstOrDefault();
                if (found is not null)
                    break;
            }
            methods[method] = found;
            return found;
        }
    }
}
