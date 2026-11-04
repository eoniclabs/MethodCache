using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0027: in a project that uses MethodCache.EntityFrameworkCore, an <c>ExecuteUpdate</c> or
    /// <c>ExecuteDelete</c> on a mapped entity type, or a raw SQL write, goes past EF Core's change tracker, so no entity
    /// tags are invalidated for it, and the method around it doesn't invalidate anything either.
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
            description: "MethodCache.EntityFrameworkCore invalidates the tags of entities that SaveChanges adds, changes or deletes. ExecuteUpdate, ExecuteDelete and raw SQL change rows without loading them, so cached results that read those rows stay until they expire unless the code invalidates them. ExecuteUpdate and ExecuteDelete are reported for the entity types the project maps with ef.Entity<T>(...), their base and derived types (ExecuteDelete also for types mapped only with WhenDeleted), or for every type when the project maps none. Raw SQL is reported unless it is a single statement that starts with select. The warning is not reported when the same method, or a method around it, also invalidates, including through a call to a [CacheInvalidate] or [InvalidatesCache] method.",
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
                var compilation = start.Compilation;
                var cancellationToken = start.CancellationToken;
                var mapping = new Lazy<EntityTagMapping>(() => EntityTagMapping.Read(compilation, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication);
                start.RegisterOperationBlockStartAction(block => AnalyzeBlock(block, mapping));
            });
        }

        private static void AnalyzeBlock(OperationBlockStartAnalysisContext context, Lazy<EntityTagMapping> mapping)
        {
            var writes = new List<IInvocationOperation>();
            var calls = new List<IInvocationOperation>();
            context.RegisterOperationAction(operation =>
            {
                var invocation = (IInvocationOperation)operation.Operation;
                if (IsBypassingWrite(invocation.TargetMethod)) writes.Add(invocation);
                else calls.Add(invocation);
            }, OperationKind.Invocation);

            context.RegisterOperationBlockEndAction(end =>
            {
                if (writes.Count == 0 || ImplementsCacheInvalidate(end.OwningSymbol)) return;
                // Looked up only in the few methods with a write, since it reads attributes and interfaces
                List<IInvocationOperation>? invalidations = null;
                foreach (var write in writes)
                {
                    if (!WritesCachedRows(write, mapping)) continue;
                    invalidations ??= calls.Where(call => IsInvalidation(call.TargetMethod)).ToList();
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

        /// <summary>
        /// A write to a mapped entity type, or raw SQL that may write. A single statement that starts with select only
        /// reads, as an advisory lock does.
        /// </summary>
        private static bool WritesCachedRows(IInvocationOperation write, Lazy<EntityTagMapping> mapping)
        {
            var method = write.TargetMethod;
            if (SqlWrites.Contains(method.Name)) return !IsSelect(write);
            if (method.TypeArguments.Length != 1) return true;
            var needed = method.Name.StartsWith("ExecuteDelete", StringComparison.Ordinal) ? EntityWrites.Delete : EntityWrites.Any;
            return (mapping.Value.For(method.TypeArguments[0]) & needed) == needed;
        }

        private static bool IsSelect(IInvocationOperation write)
        {
            var argument = write.Arguments.FirstOrDefault(a => a.Parameter?.Name == "sql");
            var sql = argument?.Value;
            while (sql is IConversionOperation conversion) sql = conversion.Operand;
            if (sql?.ConstantValue is { HasValue: true, Value: string constant }) return SqlStartsWithSelect(constant);
            if (sql is not IInterpolatedStringOperation interpolated) return false;

            // ExecuteSql makes each value a parameter, never SQL. ExecuteSqlRaw takes a string, so a value is SQL text
            // that could hold anything.
            var raw = argument!.Parameter!.Type.SpecialType == SpecialType.System_String;
            var text = new StringBuilder();
            foreach (var part in interpolated.Parts)
            {
                if (part is IInterpolatedStringTextOperation { Text.ConstantValue: { HasValue: true, Value: string value } }) text.Append(value);
                else if (raw) return false;
                else text.Append('0');
            }
            return SqlStartsWithSelect(text.ToString());
        }

        internal static bool SqlStartsWithSelect(string sql)
        {
            var i = SkipSpaceAndComments(sql, 0);
            if (string.Compare(sql, i, "select", 0, 6, StringComparison.OrdinalIgnoreCase) != 0) return false;
            if (i + 6 < sql.Length && (char.IsLetterOrDigit(sql[i + 6]) || sql[i + 6] == '_')) return false;
            // One statement: nothing but space and comments after a semicolon
            var semicolon = sql.IndexOf(';', i);
            return semicolon < 0 || SkipSpaceAndComments(sql, semicolon + 1) == sql.Length;
        }

        private static int SkipSpaceAndComments(string sql, int i)
        {
            while (i < sql.Length)
            {
                if (char.IsWhiteSpace(sql[i])) i++;
                else if (string.CompareOrdinal(sql, i, "--", 0, 2) == 0)
                {
                    var end = sql.IndexOf('\n', i);
                    i = end < 0 ? sql.Length : end + 1;
                }
                else if (string.CompareOrdinal(sql, i, "/*", 0, 2) == 0)
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + 2;
                }
                else break;
            }
            return i;
        }

        private static bool IsInvalidation(IMethodSymbol method)
        {
            var type = method.ContainingType;
            if (type == null) return false;
            // A cached interface's [CacheInvalidate] method invalidates its tags after it runs, and an
            // [InvalidatesCache] method invalidates by itself
            if (HasCacheInvalidate(method) || HasInvalidatesCache(method)) return true;
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
            if (HasCacheInvalidate(method) || HasInvalidatesCache(method)) return true;
            var type = method.ContainingType;
            if (type == null) return false;
            // Also an explicit implementation, which has another name than the interface method
            foreach (var @interface in type.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
                {
                    if ((HasCacheInvalidate(member) || HasMethodCacheAttribute(member, "InvalidatesCacheAttribute"))
                        && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method))
                        return true;
                }
            }
            return false;
        }

        private static bool HasCacheInvalidate(IMethodSymbol method) => HasMethodCacheAttribute(method, "CacheInvalidateAttribute");

        /// <summary>
        /// The method, the one it overrides or an interface method it implements is marked [InvalidatesCache]: calls
        /// through the interface and through the class both count.
        /// </summary>
        private static bool HasInvalidatesCache(IMethodSymbol method)
        {
            method = method.ReducedFrom ?? method;
            for (var current = method.OriginalDefinition; current != null; current = current.OverriddenMethod?.OriginalDefinition)
            {
                if (HasMethodCacheAttribute(current, "InvalidatesCacheAttribute")) return true;
            }
            var type = method.ContainingType;
            if (method.IsStatic || type == null || type.TypeKind == TypeKind.Interface) return false;
            foreach (var @interface in type.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers(method.Name).OfType<IMethodSymbol>())
                {
                    // Compared as definitions, so a generic method or a method of a generic type matches too
                    if (HasMethodCacheAttribute(member, "InvalidatesCacheAttribute")
                        && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member)?.OriginalDefinition, method.OriginalDefinition))
                        return true;
                }
            }
            return false;
        }

        private static bool HasMethodCacheAttribute(IMethodSymbol method, string name)
            => method.GetAttributes().Any(a => a.AttributeClass is { ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } } attribute
                && attribute.Name == name);

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
