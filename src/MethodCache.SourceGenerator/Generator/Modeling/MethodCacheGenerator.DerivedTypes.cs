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
        // plain Animal. The generator finds the types in the project and its references that derive from a type a cached
        // result declares, and names them to MethodCache's own JSON serializer, which then writes and restores each as
        // itself. The application's own JSON is unchanged, and only these types are ever created from a stored name.

        /// <summary>
        /// A concrete class or record of the project, and the types of the project and its references it derives from
        /// or implements, so the types a cached result declares can be matched to it.
        /// </summary>
        internal sealed record DerivedTypeInfo(string Fqn, EquatableArray<string> Bases);

        /// <summary>A type a cached result declares, and the types that derive from it or implement it.</summary>
        internal sealed record DerivedTypeRegistration(string DeclaredFqn, EquatableArray<string> DerivedFqns);

        /// <summary>
        /// The facts about one class for <see cref="DerivedTypeRegistrations"/>: null for a type generated code can't
        /// name or create (abstract, generic, private), or one that derives from nothing of the project's own.
        /// </summary>
        private static DerivedTypeInfo? GetDerivedTypeInfo(INamedTypeSymbol? symbol)
        {
            if (symbol is not { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, IsGenericType: false } ||
                !DecoratorRegistrationsEmitter.IsAccessibleFromAssembly(symbol))
            {
                return null;
            }

            var bases = new List<string>();
            for (var current = symbol.BaseType; current != null && !IsFrameworkType(current); current = current.BaseType)
            {
                if (!current.IsGenericType) bases.Add(Utils.GetFullyQualifiedName(current));
            }

            bases.AddRange(symbol.AllInterfaces.Where(static i => !i.IsGenericType && !IsFrameworkType(i)).Select(static i => Utils.GetFullyQualifiedName(i)));
            return bases.Count == 0 ? null : new DerivedTypeInfo(Utils.GetFullyQualifiedName(symbol), new EquatableArray<string>(bases));
        }

        /// <summary>
        /// The types the cached methods of an interface declare in their results that may hold a derived type, and the
        /// derived types of those found in referenced assemblies. The project's own derived types are matched to them
        /// later, from <see cref="GetDerivedTypeInfo"/>, so that adding one in another file updates the registrations.
        /// </summary>
        private static (EquatableArray<string> Declared, EquatableArray<DerivedTypeRegistration> Referenced) FindDerivedTypeCandidates(
            IEnumerable<IMethodSymbol> cachedMethods, IAssemblySymbol generating)
        {
            var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var found = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var method in cachedMethods)
            {
                var result = (Utils.IsTask(method.ReturnType, out var inner) || Utils.IsValueTask(method.ReturnType, out inner)) && inner != null
                    ? inner
                    : method.ReturnType;
                CollectDeclaredTypes(result, generating, visited, found);
            }

            var ordered = found.OrderBy(static t => Utils.GetFullyQualifiedName(t), System.StringComparer.Ordinal).ToList();
            var referenced = new List<DerivedTypeRegistration>();
            var typesByAssembly = new Dictionary<IAssemblySymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
            foreach (var declared in ordered)
            {
                var assembly = declared.ContainingAssembly;
                if (assembly == null || SymbolEqualityComparer.Default.Equals(assembly, generating)) continue;
                if (!typesByAssembly.TryGetValue(assembly, out var types))
                {
                    typesByAssembly[assembly] = types = AllTypes(assembly.GlobalNamespace)
                        .Where(t => t is { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, IsGenericType: false } &&
                                    DecoratorRegistrationsEmitter.IsNameableFrom(t, generating))
                        .ToList();
                }

                var derived = types.Where(t => DerivesFrom(t, declared)).Select(static t => Utils.GetFullyQualifiedName(t))
                    .OrderBy(static name => name, System.StringComparer.Ordinal).ToList();
                if (derived.Count > 0) referenced.Add(new DerivedTypeRegistration(Utils.GetFullyQualifiedName(declared), new EquatableArray<string>(derived)));
            }

            return (new EquatableArray<string>(ordered.Select(static t => Utils.GetFullyQualifiedName(t))), new EquatableArray<DerivedTypeRegistration>(referenced));
        }

        /// <summary>
        /// Collects the types in a result that a derived type can stand in for: the result itself, an element, a tuple
        /// item, or a property or field of one of your own types that a serializer writes. Such a type is a class that
        /// isn't sealed or an interface, of your own, not generic, without <c>[JsonDerivedType]</c> or <c>[Union]</c>
        /// (which then decide), and nameable from the generated code.
        /// </summary>
        private static void CollectDeclaredTypes(ITypeSymbol type, IAssemblySymbol generating, HashSet<ITypeSymbol> visited, HashSet<INamedTypeSymbol> found)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    CollectDeclaredTypes(array.ElementType, generating, visited, found);
                    return;
                case INamedTypeSymbol { ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable:
                    CollectDeclaredTypes(nullable.TypeArguments[0], generating, visited, found);
                    return;
                case not INamedTypeSymbol:
                    return;
            }

            var named = (INamedTypeSymbol)type;
            if (!visited.Add(named) || named.SpecialType is SpecialType.System_Object or SpecialType.System_String) return;

            if (named.IsTupleType || FrameworkComposites.Contains(MetadataName(named.OriginalDefinition)))
            {
                foreach (var item in named.IsTupleType ? named.TupleElements.Select(static e => e.Type) : named.TypeArguments)
                    CollectDeclaredTypes(item, generating, visited, found);
                return;
            }

            if (DecoratorEmitter.GetSequenceElementType(named) is { } element)
            {
                CollectDeclaredTypes(element, generating, visited, found);
                return;
            }

            if (IsFrameworkType(named)) return;

            if (!named.IsGenericType && !NamesItsDerivedTypes(named) &&
                (named.TypeKind == TypeKind.Interface || named.TypeKind == TypeKind.Class && !named.IsSealed && !named.IsStatic) &&
                DecoratorRegistrationsEmitter.IsNameableFrom(named, generating))
            {
                found.Add(named);
            }

            foreach (var member in SerializedMemberTypes(named))
                CollectDeclaredTypes(member, generating, visited, found);
        }

        /// <summary>
        /// The types of the public instance members a serializer writes, as MethodCache's check before sharing reads
        /// them: properties with a setter or a backing field, and fields, none marked as ignored.
        /// </summary>
        private static IEnumerable<ITypeSymbol> SerializedMemberTypes(INamedTypeSymbol type)
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
                            yield return property.Type;
                            break;
                        case IFieldSymbol { IsConst: false, IsImplicitlyDeclared: false } field when names.Add(field.Name):
                            yield return field.Type;
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
        /// Matches the types the cached results declare to the project's classes and to the derived types found in
        /// referenced assemblies: one registration per declared type that has any.
        /// </summary>
        private static EquatableArray<DerivedTypeRegistration> DerivedTypeRegistrations(EquatableArray<InterfaceSummary> interfaces, EquatableArray<DerivedTypeInfo> classes)
        {
            var declared = interfaces.SelectMany(static i => i.DerivedTypeCandidates).Distinct().OrderBy(static t => t, System.StringComparer.Ordinal).ToList();
            if (declared.Count == 0) return EquatableArray<DerivedTypeRegistration>.Empty;

            // A partial class is declared, and so found, more than once
            var byBase = classes.GroupBy(static c => c.Fqn, System.StringComparer.Ordinal).Select(static g => g.First())
                .SelectMany(static c => c.Bases.Select(b => (Base: b, c.Fqn)))
                .ToLookup(static pair => pair.Base, static pair => pair.Fqn, System.StringComparer.Ordinal);
            var referenced = interfaces.SelectMany(static i => i.ReferencedDerivedTypes)
                .ToLookup(static r => r.DeclaredFqn, static r => r.DerivedFqns, System.StringComparer.Ordinal);

            var registrations = new List<DerivedTypeRegistration>();
            foreach (var type in declared)
            {
                var derived = byBase[type].Concat(referenced[type].SelectMany(static d => d))
                    .Distinct().OrderBy(static t => t, System.StringComparer.Ordinal).ToList();
                if (derived.Count > 0) registrations.Add(new DerivedTypeRegistration(type, new EquatableArray<string>(derived)));
            }

            return new EquatableArray<DerivedTypeRegistration>(registrations);
        }
    }
}
