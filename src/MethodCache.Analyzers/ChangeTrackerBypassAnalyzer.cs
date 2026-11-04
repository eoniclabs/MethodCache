using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0027: in a project that uses MethodCache.EntityFrameworkCore, an <c>ExecuteUpdate</c>,
    /// <c>ExecuteDelete</c> or raw SQL write goes past EF Core's change tracker, so no entity tags are invalidated for
    /// it, and the method around it doesn't invalidate anything either.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ChangeTrackerBypassAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0027",
            "Write goes past EF Core's change tracker without an invalidation",
            "'{0}' writes past EF Core's change tracker, so MethodCache invalidates no entity tags for it. Invalidate the tags of the rows it changes: with db.InvalidateAfterCommit(...) inside a transaction, or with ICacheManager.InvalidateTagsAsync(...) once the write is committed.",
            "Reliability",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "MethodCache.EntityFrameworkCore invalidates the tags of entities that SaveChanges adds, changes or deletes. ExecuteUpdate, ExecuteDelete and raw SQL change rows without loading them, so cached results that read those rows stay until they expire unless the code invalidates them. The warning is not reported when the same method, or a method around it, also invalidates, including through a call to a [CacheInvalidate] method.",
            helpLinkUri: HelpLink("MC0027"));

        private static readonly ImmutableHashSet<string> QueryableWrites = ImmutableHashSet.Create(
            "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync");

        private static readonly ImmutableHashSet<string> QueryableWriteTypes = ImmutableHashSet.Create(
            "EntityFrameworkQueryableExtensions", "RelationalQueryableExtensions");

        private static readonly ImmutableHashSet<string> SqlWrites = ImmutableHashSet.Create(
            "ExecuteSql", "ExecuteSqlAsync", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync");

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                // Only where the app uses MethodCache.EntityFrameworkCore: without it, nothing invalidates entity tags
                if (start.Compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.MethodCacheDbContextExtensions") == null) return;
                start.RegisterOperationBlockStartAction(AnalyzeBlock);
            });
        }

        private static void AnalyzeBlock(OperationBlockStartAnalysisContext context)
        {
            var writes = new List<IInvocationOperation>();
            var invalidations = new List<IInvocationOperation>();
            context.RegisterOperationAction(operation =>
            {
                var invocation = (IInvocationOperation)operation.Operation;
                if (IsBypassingWrite(invocation.TargetMethod)) writes.Add(invocation);
                else if (IsInvalidation(invocation.TargetMethod)) invalidations.Add(invocation);
            }, OperationKind.Invocation);

            context.RegisterOperationBlockEndAction(end =>
            {
                if (writes.Count == 0 || ImplementsCacheInvalidate(end.OwningSymbol)) return;
                foreach (var write in writes)
                {
                    var functions = EnclosingFunctions(write);
                    // An invalidation in the same function, or in a function around it (such as the method that runs
                    // an execution strategy's lambda), covers the write
                    if (invalidations.Any(invalidation => functions.Contains(InnermostFunction(invalidation)))) continue;
                    end.ReportDiagnostic(Diagnostic.Create(Rule, NameLocation(write), write.TargetMethod.Name));
                }
            });
        }

        private static bool IsBypassingWrite(IMethodSymbol method)
        {
            var type = method.ContainingType;
            if (type == null || !IsNamespace(type.ContainingNamespace, "Microsoft", "EntityFrameworkCore")) return false;
            if (QueryableWrites.Contains(method.Name)) return QueryableWriteTypes.Contains(type.Name);
            return SqlWrites.Contains(method.Name) && type.Name == "RelationalDatabaseFacadeExtensions";
        }

        private static bool IsInvalidation(IMethodSymbol method)
        {
            var type = method.ContainingType;
            if (type == null) return false;
            // A cached interface's [CacheInvalidate] method invalidates its tags after it runs
            if (HasCacheInvalidate(method)) return true;
            if (method.Name == "InvalidateAfterCommit") return type.Name == "MethodCacheDbContextExtensions";
            if (!method.Name.StartsWith("Invalidate", System.StringComparison.Ordinal) && method.Name != "ClearAsync") return false;
            for (var ns = type.ContainingNamespace; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
            {
                if (ns.ContainingNamespace.IsGlobalNamespace) return ns.Name == "MethodCache";
            }
            return false;
        }

        private static bool ImplementsCacheInvalidate(ISymbol owner)
        {
            if (owner is not IMethodSymbol method) return false;
            if (HasCacheInvalidate(method)) return true;
            var type = method.ContainingType;
            if (type == null) return false;
            foreach (var @interface in type.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (HasCacheInvalidate(member) && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method))
                        return true;
                }
            }
            return false;
        }

        private static bool HasCacheInvalidate(IMethodSymbol method)
            => method.GetAttributes().Any(a => a.AttributeClass is
                { Name: "CacheInvalidateAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } });

        private static IOperation? InnermostFunction(IOperation operation)
        {
            for (var current = operation.Parent; current != null; current = current.Parent)
            {
                if (current is IAnonymousFunctionOperation or ILocalFunctionOperation) return current;
            }
            return null;
        }

        private static HashSet<IOperation?> EnclosingFunctions(IOperation operation)
        {
            var functions = new HashSet<IOperation?> { null };
            for (var current = operation.Parent; current != null; current = current.Parent)
            {
                if (current is IAnonymousFunctionOperation or ILocalFunctionOperation) functions.Add(current);
            }
            return functions;
        }

        private static Location NameLocation(IInvocationOperation invocation)
            => invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
                ? access.Name.GetLocation()
                : invocation.Syntax.GetLocation();

        private static bool IsNamespace(INamespaceSymbol? ns, string outer, string inner)
            => ns is { Name: var name, ContainingNamespace: { Name: var outerName, ContainingNamespace.IsGlobalNamespace: true } }
               && name == inner && outerName == outer;
    }
}
