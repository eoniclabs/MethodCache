using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MethodCache.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// Reports MC0043: code changes a cached result, such as <c>orders.Add(order)</c> or <c>order.Status = Status.Closed</c>
    /// on the result of a cached method or of <c>ICacheManager.GetOrCreateAsync</c>. The memory cache hands that same
    /// instance to later callers on this instance, while other instances read the stored copy, so they see different values.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ChangedResultAnalyzer : DiagnosticAnalyzer
    {
        private static string HelpLink(string id) => "https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md#" + id.ToLowerInvariant();

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "MC0043",
            "Code changes a cached result",
            "This changes a cached result of '{0}'. The memory cache hands that same instance to every later caller on this instance, while other instances read the stored copy, so they see different values. Change a copy instead, such as new List<T>(result), result.ToList() or a record's with expression, or return a type that can't be changed.",
            "Reliability",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "A cached result is shared: the memory cache keeps the instance a cached method or ICacheManager.GetOrCreateAsync returned and hands it to later calls, and the distributed cache keeps a serialized copy. Setting a property or field of the result or of an object in it, adding to or removing from a collection in it, or writing to an array in it, also through a Span<T>, Array.Copy or CopyTo, changes what later callers on this instance get, but not what the distributed cache stores. Reported for results kept in a local or read in a foreach, and for objects reached from them through properties, fields, indexers, First, Single, Last, ElementAt and Find, the elements of ToList, ToArray, Where and other LINQ copies, and the values of GetOrCreateManyAsync. Only the method that holds the result is read, so a method of yours that changes it isn't seen: neither one on the object, such as order.Close(), nor a helper you pass it to, such as AddDraft(orders). In Development, MethodCache also compares cached values with what it stored now and then, and logs a warning naming the method when one has changed. Not reported in test projects.",
            helpLinkUri: HelpLink("MC0043"));

        // Methods of System.Collections types that change the collection they are called on
        private static readonly HashSet<string> ChangingMethods = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "RemoveWhere", "Clear",
            "Sort", "Reverse", "TryAdd", "TryRemove", "TryUpdate", "AddOrUpdate", "GetOrAdd", "Push", "Pop", "TryPop", "Enqueue", "Dequeue", "TryDequeue",
            "AddFirst", "AddLast", "AddBefore", "AddAfter", "RemoveFirst", "RemoveLast", "UnionWith", "IntersectWith", "ExceptWith", "SymmetricExceptWith",
            "Move", "SetItem", "Set", "Fill",
        };

        // Static System.Array methods that change the array passed first
        private static readonly HashSet<string> ChangingArrayMethods = new HashSet<string>(System.StringComparer.Ordinal) { "Sort", "Reverse", "Clear", "Fill", "Resize" };

        // Span<T> methods that change the memory the span covers
        private static readonly HashSet<string> ChangingSpanMethods = new HashSet<string>(System.StringComparer.Ordinal) { "Clear", "Fill" };

        // MemoryExtensions methods that change the span passed first
        private static readonly HashSet<string> ChangingMemoryExtensions = new HashSet<string>(System.StringComparer.Ordinal) { "Sort", "Reverse", "Replace" };

        // Methods that copy into an array, span or memory passed as the argument of one of these parameters
        private static readonly HashSet<string> CopyMethods = new HashSet<string>(System.StringComparer.Ordinal) { "Copy", "ConstrainedCopy", "BlockCopy", "CopyTo", "TryCopyTo" };
        private static readonly HashSet<string> Destinations = new HashSet<string>(System.StringComparer.Ordinal) { "destinationArray", "dst", "destination", "array" };

        // ICacheManager methods whose result is the cached value, or a dictionary of its own holding cached values
        private static readonly HashSet<string> ManagerResults = new HashSet<string>(System.StringComparer.Ordinal) { "GetOrCreateAsync", "TryGetAsync" };
        private const string ManagerMany = "GetOrCreateManyAsync";

        // LINQ methods whose result is an element of the source
        private static readonly HashSet<string> Elements = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault", "ElementAt", "ElementAtOrDefault", "Find", "FindLast", "Min", "Max", "MinBy", "MaxBy",
        };

        // LINQ methods whose result holds the elements of the source, but is a sequence or collection of its own
        private static readonly HashSet<string> Copies = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "ToList", "ToArray", "ToHashSet", "AsEnumerable", "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "Skip", "SkipWhile",
            "SkipLast", "Take", "TakeWhile", "TakeLast", "Distinct", "DistinctBy", "Reverse", "Cast", "OfType", "Concat", "Union", "Intersect", "Except",
            "Append", "Prepend", "FindAll", "GetRange", "AsReadOnly", "ToImmutableArray", "ToImmutableList", "ToFrozenSet",
        };

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                if (start.Compilation.GetTypeByMetadataName("MethodCache.CacheAttribute") is null || InvalidationTags.IsTestProject(start.Compilation)) return;
                var external = ExternalInterfaces(start.Compilation);
                start.RegisterOperationAction(operation => Analyze(operation, external),
                    OperationKind.SimpleAssignment, OperationKind.CompoundAssignment, OperationKind.CoalesceAssignment,
                    OperationKind.Increment, OperationKind.Decrement, OperationKind.Invocation);
            });
        }

        private enum Reach
        {
            None,
            // The result, or an object reached from it: changing it changes the cached value
            Result,
            // A collection of its own holding the result's elements: changing it is fine, its elements are the result's
            Copy,
        }

        private static void Analyze(OperationAnalysisContext context, ImmutableDictionary<INamedTypeSymbol, ImmutableHashSet<string>> external)
        {
            var changed = context.Operation switch
            {
                IAssignmentOperation assignment => Changed(assignment.Target),
                IIncrementOrDecrementOperation step => Changed(step.Target),
                IInvocationOperation invocation => ChangedBy(invocation),
                _ => null,
            };
            if (changed == null) return;

            var methods = new List<IMethodSymbol>();
            if (ReachOf(changed, external, methods, new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default)) != Reach.Result || methods.Count == 0) return;
            var method = methods[0];
            var owner = IsCacheManager(method) ? "ICacheManager" : method.ContainingType.Name;
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), owner + "." + method.Name));
        }

        /// <summary>The object an assignment changes: the instance of a property, field or indexer, or the array of an element.</summary>
        private static IOperation? Changed(IOperation target)
        {
            var changed = target switch
            {
                IPropertyReferenceOperation property => property.Instance,
                IFieldReferenceOperation field => field.Instance,
                IArrayElementReferenceOperation element => element.ArrayReference,
                _ => null,
            };
            return Storage(changed);
        }

        /// <summary>The collection, array or span a call to one of its changing methods changes, or copies into.</summary>
        private static IOperation? ChangedBy(IInvocationOperation invocation)
        {
            var method = invocation.TargetMethod;
            var type = method.ContainingType;
            if (type == null) return null;
            if (type.SpecialType == SpecialType.System_Array && method.IsStatic && ChangingArrayMethods.Contains(method.Name) && invocation.Arguments.Length > 0)
                return invocation.Arguments[0].Value;
            if (CopyMethods.Contains(method.Name) && CopiesInto(type))
                return invocation.Arguments.FirstOrDefault(a => a.Parameter is { } parameter && Destinations.Contains(parameter.Name))?.Value;
            if (IsSystem(type, "MemoryExtensions") && ChangingMemoryExtensions.Contains(method.Name) && method.Parameters.Length > 0 &&
                method.Parameters[0].Type is INamedTypeSymbol first && IsView(first) && invocation.Arguments.Length > 0)
                return invocation.Arguments[0].Value;
            if (IsView(type) && !method.IsStatic && ChangingSpanMethods.Contains(method.Name)) return invocation.Instance;
            if (method.IsStatic || !ChangingMethods.Contains(method.Name) || !IsChangingCollection(type)) return null;
            return Storage(invocation.Instance);
        }

        // Types whose Copy, BlockCopy or CopyTo writes to an array, span or memory passed as an argument
        private static bool CopiesInto(INamedTypeSymbol type)
            => type.SpecialType == SpecialType.System_Array || IsSystem(type, "Buffer") || IsSystem(type, "MemoryExtensions") ||
               IsSystem(type, "Span") || IsSystem(type, "ReadOnlySpan") || IsSystem(type, "Memory") || IsSystem(type, "ReadOnlyMemory") ||
               IsChangingCollection(type);

        // Span<T>, Memory<T> and ArraySegment<T> are structs that write to the array they cover
        private static bool IsView(ITypeSymbol? type)
            => type is INamedTypeSymbol { Arity: 1 } named && (IsSystem(named, "Span") || IsSystem(named, "Memory") || IsSystem(named, "ArraySegment"));

        private static bool IsSystem(INamedTypeSymbol type, string name)
            => type.Name == name && type.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true };

        private static bool IsCacheManager(IMethodSymbol method)
            => method.ContainingType is { Name: "ICacheManager" or "CacheManagerExtensions", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } };

        private static bool IsChangingCollection(INamedTypeSymbol type)
        {
            var ns = type.ContainingNamespace?.ToDisplayString();
            return ns != null && ns.StartsWith("System.Collections", System.StringComparison.Ordinal) &&
                   ns != "System.Collections.Immutable" && ns != "System.Collections.Frozen";
        }

        /// <summary>
        /// Where a change lands: a struct changes where it is stored, so a struct field of an object or an array element
        /// is followed to that object or array, while a struct in a local or returned by a property is a copy.
        /// </summary>
        private static IOperation? Storage(IOperation? operation)
        {
            while (operation is IConversionOperation conversion) operation = conversion.Operand;
            if (operation?.Type is not { IsValueType: true } || IsView(operation.Type)) return operation;
            return operation switch
            {
                IFieldReferenceOperation field => Storage(field.Instance),
                IArrayElementReferenceOperation element => Storage(element.ArrayReference),
                _ => null,
            };
        }

        private static Reach ReachOf(IOperation? operation, ImmutableDictionary<INamedTypeSymbol, ImmutableHashSet<string>> external,
            List<IMethodSymbol> methods, HashSet<ILocalSymbol> visited)
        {
            while (operation is IConversionOperation or IParenthesizedOperation)
                operation = operation is IConversionOperation conversion ? conversion.Operand : ((IParenthesizedOperation)operation).Operand;

            switch (operation)
            {
                case null:
                    return Reach.None;
                case IAwaitOperation awaited:
                    return ReachOf(awaited.Operation, external, methods, visited);
                case IConditionalAccessInstanceOperation:
                    var access = operation.Parent;
                    while (access != null && access is not IConditionalAccessOperation) access = access.Parent;
                    return access is IConditionalAccessOperation conditional ? Inside(ReachOf(conditional.Operation, external, methods, visited)) : Reach.None;
                case IPropertyReferenceOperation property:
                    return property.Property.IsIndexer ? Inside(ReachOf(property.Instance, external, methods, visited)) : Member(ReachOf(property.Instance, external, methods, visited));
                case IFieldReferenceOperation field:
                    return Member(ReachOf(field.Instance, external, methods, visited));
                case IArrayElementReferenceOperation element:
                    return Inside(ReachOf(element.ArrayReference, external, methods, visited));
                case IObjectCreationOperation creation when IsView(creation.Type):
                    // new Span<T>(result) covers the result's own array
                    return creation.Arguments.Length > 0 ? ReachOf(creation.Arguments[0].Value, external, methods, visited) : Reach.None;
                case IObjectCreationOperation creation:
                    // new List<T>(result) holds the result's elements in a collection of its own
                    return creation.Arguments.Any(a => ReachOf(a.Value, external, methods, visited) != Reach.None) ? Reach.Copy : Reach.None;
                case IInvocationOperation invocation:
                    if (IsCached(invocation.TargetMethod, external))
                    {
                        methods.Add(invocation.TargetMethod);
                        return Reach.Result;
                    }

                    if (IsCacheManager(invocation.TargetMethod) && (ManagerResults.Contains(invocation.TargetMethod.Name) || invocation.TargetMethod.Name == ManagerMany))
                    {
                        methods.Add(invocation.TargetMethod);
                        // GetOrCreateManyAsync returns a dictionary of its own; its values are the cached ones
                        return invocation.TargetMethod.Name == ManagerMany ? Reach.Copy : Reach.Result;
                    }

                    var source = invocation.Instance ?? (invocation.TargetMethod.IsExtensionMethod && invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value : null);
                    // A span or memory over the result, or a slice of one, writes to the result's own array
                    if (invocation.TargetMethod.Name is "AsSpan" or "AsMemory" && invocation.TargetMethod.ContainingType is { } owner &&
                        (IsSystem(owner, "MemoryExtensions") || owner is { Name: "CollectionsMarshal", ContainingNamespace.Name: "InteropServices" }) ||
                        invocation.TargetMethod.Name == "Slice" && IsView(invocation.TargetMethod.ContainingType))
                    {
                        if (source == null && invocation.Arguments.Length > 0) source = invocation.Arguments[0].Value;
                        return ReachOf(source, external, methods, visited);
                    }

                    if (Elements.Contains(invocation.TargetMethod.Name)) return Inside(ReachOf(source, external, methods, visited));
                    if (Copies.Contains(invocation.TargetMethod.Name)) return ReachOf(source, external, methods, visited) == Reach.None ? Reach.None : Reach.Copy;
                    return Reach.None;
                case ILocalReferenceOperation local:
                    return ReachOfLocal(local, external, methods, visited);
                default:
                    return Reach.None;
            }
        }

        // An element or item of the result or of a copy of it is part of the result
        private static Reach Inside(Reach reach) => reach == Reach.None ? Reach.None : Reach.Result;

        // A property or field of the result is part of it; one of a copy, such as Count, is the copy's own
        private static Reach Member(Reach reach) => reach == Reach.Result ? Reach.Result : Reach.None;

        /// <summary>
        /// What a local holds: the value it was declared with, when nothing assigns it another, or for a foreach
        /// variable an element of the collection it reads.
        /// </summary>
        private static Reach ReachOfLocal(ILocalReferenceOperation reference, ImmutableDictionary<INamedTypeSymbol, ImmutableHashSet<string>> external,
            List<IMethodSymbol> methods, HashSet<ILocalSymbol> visited)
        {
            var local = reference.Local;
            if (!visited.Add(local)) return Reach.None;

            // The declaration first, which is cheap to read; most locals hold no cached result
            var syntax = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            var reach = (syntax == null ? null : reference.SemanticModel?.GetOperation(syntax)) switch
            {
                IVariableDeclaratorOperation { Initializer: { } initializer } => ReachOf(initializer.Value, external, methods, visited),
                IForEachLoopOperation { LoopControlVariable: IVariableDeclaratorOperation control } loop when SymbolEqualityComparer.Default.Equals(control.Symbol, local)
                    => Inside(ReachOf(loop.Collection, external, methods, visited)),
                _ => Reach.None,
            };
            if (reach == Reach.None) return Reach.None;

            // A local assigned again, or passed by ref, may hold something else when it is changed
            var body = reference.Parent;
            while (body?.Parent != null) body = body.Parent;
            foreach (var operation in body?.Descendants() ?? Enumerable.Empty<IOperation>())
            {
                if (operation is IAssignmentOperation { Target: ILocalReferenceOperation target } && SymbolEqualityComparer.Default.Equals(target.Local, local) ||
                    operation is IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out, Value: ILocalReferenceOperation passed } && SymbolEqualityComparer.Default.Equals(passed.Local, local))
                {
                    return Reach.None;
                }
            }

            return reach;
        }

        private static bool IsCached(IMethodSymbol method, ImmutableDictionary<INamedTypeSymbol, ImmutableHashSet<string>> external)
        {
            var definition = method.OriginalDefinition;
            if (definition.GetAttributes().Any(a => a.AttributeClass is { Name: "CacheAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } }))
                return true;
            return definition.ContainingType is { } type && external.TryGetValue(type.OriginalDefinition, out var names) && (names.IsEmpty || names.Contains(definition.Name));
        }

        /// <summary>The interfaces <c>[assembly: CacheInterface]</c> caches, with the methods it names, or none for all.</summary>
        private static ImmutableDictionary<INamedTypeSymbol, ImmutableHashSet<string>> ExternalInterfaces(Compilation compilation)
        {
            var interfaces = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, ImmutableHashSet<string>>(SymbolEqualityComparer.Default);
            foreach (var attribute in compilation.Assembly.GetAttributes())
            {
                if (attribute.AttributeClass is not { Name: "CacheInterfaceAttribute", ContainingNamespace: { Name: "MethodCache", ContainingNamespace.IsGlobalNamespace: true } } ||
                    attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol type)
                {
                    continue;
                }

                var names = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Kind == TypedConstantKind.Array
                    ? attribute.ConstructorArguments[1].Values.Select(v => v.Value as string).Where(n => n != null).Select(n => n!).ToImmutableHashSet(System.StringComparer.Ordinal)
                    : ImmutableHashSet<string>.Empty.WithComparer(System.StringComparer.Ordinal);
                interfaces[type.OriginalDefinition] = names;
            }

            return interfaces.ToImmutable();
        }
    }
}
