#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Derived types in cached results ========================
        // A distributed cache writes a result as the types it declares, so a Dog in a List<Animal> would come back as a
        // plain Animal. The generator finds the types that derive from a type a cached result declares, and names them to
        // MethodCache's own JSON serializer, which then writes and restores each as itself. It finds them in the project,
        // in the declared type's assembly and the referenced assemblies that use it, and, through the assembly metadata
        // each project writes, in the projects that reference the one with the cached interface. The application's own
        // JSON is unchanged, and only these types are ever created from a stored name.

        /// <summary>
        /// The assembly metadata under which a project lists the types its cached results declare, so the generator in a
        /// project that references it registers that project's own derived types of them.
        /// </summary>
        internal const string DerivedTypeBaseMetadataKey = "MethodCache.DerivedTypeBase";

        /// <summary>
        /// A concrete class or record of the project, and the types of the project and its references it derives from
        /// or implements, so the types a cached result declares can be matched to it. A class generated code can't name,
        /// such as a generic or private one, says why, so the project gets MC0039 when a cached result declares a base.
        /// </summary>
        internal sealed record DerivedTypeInfo(string Fqn, EquatableArray<string> Bases, string? Unregistrable = null, string? DisplayName = null, LocationInfo? Location = null)
        {
            /// <summary>The fingerprint of the type's shape, which the entries of a result that may hold it carry.</summary>
            public string Shape { get; init; } = string.Empty;
        }

        /// <summary>A type a cached result declares, and the types that derive from it or implement it.</summary>
        internal sealed record DerivedTypeRegistration(string DeclaredFqn, EquatableArray<string> DerivedFqns)
        {
            /// <summary>The fingerprint of each derived type's shape, as <c>fqn=shape</c>.</summary>
            public EquatableArray<string> Shapes { get; init; }
        }

        /// <summary>The project's registrations, and MC0039 for each derived type of the project that can't be registered.</summary>
        internal sealed record DerivedTypesModel(EquatableArray<DerivedTypeRegistration> Registrations, EquatableArray<DiagnosticInfo> Diagnostics)
        {
            public static readonly DerivedTypesModel Empty = new(EquatableArray<DerivedTypeRegistration>.Empty, EquatableArray<DiagnosticInfo>.Empty);
        }

        /// <summary>
        /// The facts about one class for <see cref="DerivedTypeRegistrations"/>: null for a type that has no instances
        /// (abstract, static) or derives from nothing of the project's own or its references'.
        /// </summary>
        private static DerivedTypeInfo? GetDerivedTypeInfo(INamedTypeSymbol? symbol)
        {
            if (symbol is not { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false } || symbol.IsImplicitlyDeclared) return null;

            // Only the bases the class's own project can name, as the generated registration names them
            var assembly = symbol.ContainingAssembly;
            var bases = new List<string>();
            for (var current = symbol.BaseType; current != null && !IsFrameworkType(current); current = current.BaseType)
            {
                if (!current.IsGenericType && DecoratorRegistrationsEmitter.IsNameableFrom(current, assembly)) bases.Add(Utils.GetFullyQualifiedName(current));
            }

            bases.AddRange(symbol.AllInterfaces
                .Where(i => !i.IsGenericType && !IsFrameworkType(i) && DecoratorRegistrationsEmitter.IsNameableFrom(i, assembly))
                .Select(static i => Utils.GetFullyQualifiedName(i)));
            if (bases.Count == 0) return null;

            var unregistrable = WhyUnregistrable(symbol, assembly);
            return unregistrable == null
                ? new DerivedTypeInfo(Utils.GetFullyQualifiedName(symbol), new EquatableArray<string>(bases)) { Shape = DescribeResultShape(symbol).Shape }
                : new DerivedTypeInfo(Utils.GetFullyQualifiedName(symbol), new EquatableArray<string>(bases), unregistrable,
                    symbol.ToDisplayString(), LocationInfo.From(symbol.Locations.FirstOrDefault()));
        }

        /// <summary>Why generated code in <paramref name="generating"/> can't name a derived type, or null when it can.</summary>
        private static string? WhyUnregistrable(INamedTypeSymbol type, IAssemblySymbol generating)
        {
            if (type.IsGenericType) return type.TypeParameters.Length > 0 ? "it is generic" : "it is nested in a generic type";
            if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, generating))
                return DecoratorRegistrationsEmitter.IsNameableFrom(type, generating) ? null : "it is not public";

            for (var current = type; current != null; current = current.ContainingType)
            {
                if (current.IsFileLocal) return "it is file-local";
                if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                    return SymbolEqualityComparer.Default.Equals(current, type) ? "it is private" : $"it is nested in '{current.Name}', which is private";
            }

            return null;
        }

        /// <summary>
        /// The types the cached methods of an interface declare in their results that may hold a derived type, the
        /// derived types of those found in referenced assemblies, and MC0039 for each derived type there that generated
        /// code can't name. The project's own derived types are matched to them later, from <see cref="GetDerivedTypeInfo"/>,
        /// so that adding one in another file updates the registrations.
        /// </summary>
        private static (EquatableArray<string> Declared, EquatableArray<DerivedTypeRegistration> Referenced, EquatableArray<DiagnosticInfo> Diagnostics) FindDerivedTypeCandidates(
            IEnumerable<IMethodSymbol> cachedMethods, IAssemblySymbol generating)
        {
            var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var found = new Dictionary<INamedTypeSymbol, IMethodSymbol>(SymbolEqualityComparer.Default);
            foreach (var method in cachedMethods)
            {
                var result = (Utils.IsTask(method.ReturnType, out var inner) || Utils.IsValueTask(method.ReturnType, out inner)) && inner != null
                    ? inner
                    : method.ReturnType;
                CollectDeclaredTypes(result, method, generating, visited, found);
            }

            var ordered = found.Keys.OrderBy(static t => Utils.GetFullyQualifiedName(t), System.StringComparer.Ordinal).ToList();
            var referenced = new List<DerivedTypeRegistration>();
            var diagnostics = new List<DiagnosticInfo>();
            List<IAssemblySymbol>? references = null;
            var typesByAssembly = new Dictionary<IAssemblySymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
            foreach (var declared in ordered)
            {
                var home = declared.ContainingAssembly;
                if (home == null || SymbolEqualityComparer.Default.Equals(home, generating)) continue;

                // The declared type's own assembly, and the referenced assemblies that build on it
                references ??= generating.Modules.SelectMany(static m => m.ReferencedAssemblySymbols).ToList();
                var derived = new List<INamedTypeSymbol>();
                foreach (var assembly in references.Where(a => SymbolEqualityComparer.Default.Equals(a, home) ||
                                                              a.Modules.Any(m => m.ReferencedAssemblySymbols.Contains(home, SymbolEqualityComparer.Default))))
                {
                    if (!typesByAssembly.TryGetValue(assembly, out var types))
                    {
                        typesByAssembly[assembly] = types = AllTypes(assembly.GlobalNamespace)
                            .Where(static t => t is { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, IsImplicitlyDeclared: false, CanBeReferencedByName: true })
                            .ToList();
                    }

                    derived.AddRange(types.Where(t => DerivesFrom(t, declared)));
                }

                var registrable = new SortedDictionary<string, string>(System.StringComparer.Ordinal);
                foreach (var type in derived)
                {
                    if (WhyUnregistrable(type, generating) is { } reason)
                    {
                        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnregistrableDerivedType, found[declared].Locations.FirstOrDefault(),
                            type.ToDisplayString(), declared.ToDisplayString(), reason));
                    }
                    else
                    {
                        registrable[Utils.GetFullyQualifiedName(type)] = DescribeResultShape(type).Shape;
                    }
                }

                if (registrable.Count > 0)
                {
                    referenced.Add(new DerivedTypeRegistration(Utils.GetFullyQualifiedName(declared), new EquatableArray<string>(registrable.Keys))
                    {
                        Shapes = new EquatableArray<string>(registrable.Select(static pair => pair.Key + "=" + pair.Value)),
                    });
                }
            }

            return (new EquatableArray<string>(ordered.Select(static t => Utils.GetFullyQualifiedName(t))),
                new EquatableArray<DerivedTypeRegistration>(referenced),
                new EquatableArray<DiagnosticInfo>(diagnostics));
        }

        /// <summary>
        /// Collects the types in a result that a derived type can stand in for: the result itself, an element, a tuple
        /// item, or a property or field of one of your own types that a serializer writes. Such a type is a class that
        /// isn't sealed or an interface, of your own, not generic, without <c>[JsonDerivedType]</c> or <c>[Union]</c>
        /// (which then decide), and nameable from the generated code.
        /// </summary>
        private static void CollectDeclaredTypes(ITypeSymbol type, IMethodSymbol method, IAssemblySymbol generating, HashSet<ITypeSymbol> visited, Dictionary<INamedTypeSymbol, IMethodSymbol> found)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    CollectDeclaredTypes(array.ElementType, method, generating, visited, found);
                    return;
                case INamedTypeSymbol { ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable:
                    CollectDeclaredTypes(nullable.TypeArguments[0], method, generating, visited, found);
                    return;
                case not INamedTypeSymbol:
                    return;
            }

            var named = (INamedTypeSymbol)type;
            if (!visited.Add(named) || named.SpecialType is SpecialType.System_Object or SpecialType.System_String) return;

            if (named.IsTupleType || FrameworkComposites.Contains(MetadataName(named.OriginalDefinition)))
            {
                foreach (var item in named.IsTupleType ? named.TupleElements.Select(static e => e.Type) : named.TypeArguments)
                    CollectDeclaredTypes(item, method, generating, visited, found);
                return;
            }

            if (DecoratorEmitter.GetSequenceElementType(named) is { } element)
            {
                CollectDeclaredTypes(element, method, generating, visited, found);
                return;
            }

            if (IsFrameworkType(named)) return;

            if (!named.IsGenericType && !NamesItsDerivedTypes(named) &&
                (named.TypeKind == TypeKind.Interface || named.TypeKind == TypeKind.Class && !named.IsSealed && !named.IsStatic) &&
                DecoratorRegistrationsEmitter.IsNameableFrom(named, generating))
            {
                found[named] = method;
            }

            foreach (var member in SerializedMemberTypes(named))
                CollectDeclaredTypes(member, method, generating, visited, found);
        }

        /// <summary>
        /// The types of the public instance members a serializer writes, as MethodCache's check before sharing reads
        /// them: properties with a setter or a backing field, and fields, none marked as ignored.
        /// </summary>
        private static IEnumerable<ITypeSymbol> SerializedMemberTypes(INamedTypeSymbol type)
            => SerializedMembers(type).Select(static member => member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type);

        /// <summary>The public instance properties and fields <see cref="SerializedMemberTypes"/> reads.</summary>
        private static IEnumerable<ISymbol> SerializedMembers(INamedTypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Interface) yield break;
            var names = new HashSet<string>(System.StringComparer.Ordinal);
            for (var current = type; current != null && !IsFrameworkType(current); current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public || IgnoredBySerializers(member)) continue;
                    switch (member)
                    {
                        case IPropertySymbol { IsIndexer: false, GetMethod: not null } property
                            when (property.SetMethod != null || IsAutoProperty(property)) && names.Add(property.Name):
                            yield return property;
                            break;
                        case IFieldSymbol { IsConst: false, IsImplicitlyDeclared: false } field when names.Add(field.Name):
                            yield return field;
                            break;
                    }
                }
            }
        }

        private static bool IgnoredBySerializers(ISymbol member)
        {
            foreach (var attribute in member.GetAttributes())
            {
                switch (attribute.AttributeClass?.ToDisplayString())
                {
                    case "System.Text.Json.Serialization.JsonIgnoreAttribute":
                        var condition = attribute.NamedArguments.FirstOrDefault(static a => a.Key == "Condition").Value;
                        // JsonIgnoreCondition.Always, which is also what the attribute means without a condition
                        if (condition.Value is null or 1) return true;
                        break;
                    case "System.Runtime.Serialization.IgnoreDataMemberAttribute":
                    case "System.NonSerializedAttribute":
                    case "MessagePack.IgnoreMemberAttribute":
                        return true;
                }
            }

            return false;
        }

        private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol declared)
        {
            if (declared.TypeKind == TypeKind.Interface) return type.AllInterfaces.Contains(declared, SymbolEqualityComparer.Default);
            for (var current = type.BaseType; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, declared)) return true;
            }

            return false;
        }

        private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                yield return type;
                foreach (var nested in NestedTypes(type)) yield return nested;
            }

            foreach (var child in ns.GetNamespaceMembers())
            {
                foreach (var type in AllTypes(child)) yield return type;
            }
        }

        private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var deeper in NestedTypes(nested)) yield return deeper;
            }
        }

        /// <summary>
        /// Matches the types the cached results declare, the project's own and those its references list in their
        /// metadata, to the project's classes and to the derived types found in referenced assemblies: one registration
        /// per declared type that has any, and MC0039 for a class of the project that derives from one but can't be named.
        /// A test project gets no MC0039, since its private test doubles never reach a shared cache.
        /// </summary>
        private static DerivedTypesModel DerivedTypeRegistrations(EquatableArray<InterfaceSummary> interfaces, EquatableArray<DerivedTypeInfo> classes,
            EquatableArray<string> referencedBases, bool isTestProject)
        {
            var declared = interfaces.SelectMany(static i => i.DerivedTypeCandidates).Concat(referencedBases)
                .Distinct().OrderBy(static t => t, System.StringComparer.Ordinal).ToList();
            if (declared.Count == 0) return DerivedTypesModel.Empty;

            // A partial class is declared, and so found, more than once
            var unique = classes.GroupBy(static c => c.Fqn, System.StringComparer.Ordinal).Select(static g => g.First()).ToList();
            var byBase = unique.Where(static c => c.Unregistrable == null)
                .SelectMany(static c => c.Bases.Select(b => (Base: b, c.Fqn)))
                .ToLookup(static pair => pair.Base, static pair => pair.Fqn, System.StringComparer.Ordinal);
            var referenced = interfaces.SelectMany(static i => i.ReferencedDerivedTypes)
                .ToLookup(static r => r.DeclaredFqn, System.StringComparer.Ordinal);
            // The shape of each derived type, as fqn=shape: the project's own, and those found in referenced assemblies
            var shapes = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var shape in unique.Where(static c => c.Unregistrable == null).Select(static c => c.Fqn + "=" + c.Shape)
                         .Concat(interfaces.SelectMany(static i => i.ReferencedDerivedTypes).SelectMany(static r => r.Shapes)))
            {
                shapes[shape.Substring(0, shape.IndexOf('='))] = shape;
            }

            var registrations = new List<DerivedTypeRegistration>();
            foreach (var type in declared)
            {
                var derived = byBase[type].Concat(referenced[type].SelectMany(static r => r.DerivedFqns))
                    .Distinct().OrderBy(static t => t, System.StringComparer.Ordinal).ToList();
                if (derived.Count == 0) continue;

                registrations.Add(new DerivedTypeRegistration(type, new EquatableArray<string>(derived))
                {
                    Shapes = new EquatableArray<string>(derived.Where(shapes.ContainsKey).Select(d => shapes[d])),
                });
            }

            var diagnostics = new List<DiagnosticInfo>();
            if (!isTestProject)
            {
                var declaredSet = new HashSet<string>(declared, System.StringComparer.Ordinal);
                foreach (var type in unique)
                {
                    if (type.Unregistrable == null || type.Bases.FirstOrDefault(declaredSet.Contains) is not { } declaredBase) continue;
                    diagnostics.Add(new DiagnosticInfo(Diagnostics.UnregistrableDerivedType, type.Location,
                        new EquatableArray<string?>(new[] { type.DisplayName, declaredBase.StartsWith("global::", System.StringComparison.Ordinal) ? declaredBase.Substring(8) : declaredBase, type.Unregistrable }),
                        null));
                }
            }

            return new DerivedTypesModel(new EquatableArray<DerivedTypeRegistration>(registrations), new EquatableArray<DiagnosticInfo>(diagnostics));
        }

        /// <summary>Lists the types the project's cached results declare, for the projects that reference it.</summary>
        private static void EmitDerivedTypeBases(SourceProductionContext context, EquatableArray<string> declared)
        {
            if (declared.Count == 0) return;

            var source = new System.Text.StringBuilder()
                .AppendLine("// <auto-generated/>")
                .AppendLine("// The types this project's cached results declare. MethodCache's generator in a project that references this one")
                .AppendLine("// registers that project's derived types of them, so the distributed cache restores each as itself.");
            foreach (var type in declared)
            {
                source.Append("[assembly: global::System.Reflection.AssemblyMetadata(")
                    .Append(Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(DerivedTypeBaseMetadataKey, quote: true))
                    .Append(", ")
                    .Append(Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(type, quote: true))
                    .AppendLine(")]");
            }

            context.AddSource("MethodCacheDerivedTypeBases.g.cs", source.ToString());
        }
    }
}
