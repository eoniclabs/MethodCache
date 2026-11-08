#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Key forms and result types ========================

        /// <summary>
        /// Framework types with a key form: the key generators write their value (see KeyArguments and the MessagePack
        /// key generator). Other framework types are refused (MC0019), since their public properties don't hold their
        /// value (BigInteger has IsZero and Sign) or they are mutable (StringBuilder).
        /// </summary>
        private static readonly HashSet<string> FrameworkTypesWithKeyForm = new(System.StringComparer.Ordinal)
        {
            "System.Guid",
            "System.DateTimeOffset",
            "System.TimeSpan",
            "System.DateOnly",
            "System.TimeOnly",
            "System.Half",
            "System.Int128",
            "System.UInt128",
            "System.Uri",
            "System.Version",
            "System.Numerics.BigInteger",
            "System.Numerics.Complex",
            "System.Net.IPAddress",
            "System.Text.Json.JsonElement",
            "System.Index",
            "System.Range",
            "System.Numerics.Vector2",
            "System.Numerics.Vector3",
            "System.Numerics.Vector4",
            "System.Numerics.Quaternion",
            "System.Numerics.Plane",
            "System.Numerics.Matrix3x2",
            "System.Numerics.Matrix4x4",
            "Microsoft.AspNetCore.Http.PathString",
            "Microsoft.AspNetCore.Http.QueryString",
            "Microsoft.AspNetCore.Http.HostString",
        };

        /// <summary>Framework types whose type arguments are written in their place.</summary>
        private static readonly HashSet<string> FrameworkComposites = new(System.StringComparer.Ordinal)
        {
            "System.Collections.Generic.KeyValuePair`2",
            "System.Memory`1",
            "System.ReadOnlyMemory`1",
            "System.Tuple`1",
            "System.Tuple`2",
            "System.Tuple`3",
            "System.Tuple`4",
            "System.Tuple`5",
            "System.Tuple`6",
            "System.Tuple`7",
            "System.Tuple`8",
        };

        /// <summary>
        /// A type from .NET itself, ASP.NET Core or Microsoft.Extensions, such as System.Text.StringBuilder, told by the
        /// assembly it comes from. A namespace would not do: SDKs such as Microsoft Graph put their own models in
        /// Microsoft.* namespaces, and those are written by their properties like any other class.
        /// </summary>
        internal static bool IsFrameworkType(ITypeSymbol type)
        {
            var assembly = type.ContainingAssembly?.Name;
            return assembly is "mscorlib" or "netstandard" or "System" ||
                   assembly?.StartsWith("System.", System.StringComparison.Ordinal) == true ||
                   assembly?.StartsWith("Microsoft.AspNetCore.", System.StringComparison.Ordinal) == true ||
                   assembly?.StartsWith("Microsoft.Extensions.", System.StringComparison.Ordinal) == true ||
                   assembly?.StartsWith("Microsoft.Win32.", System.StringComparison.Ordinal) == true;
        }

        private static string MetadataName(INamedTypeSymbol type)
            => type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + type.MetadataName : type.MetadataName;

        /// <summary>
        /// The first type without a key form in a key parameter: the parameter's type, an element, a tuple item, or a
        /// public property or field of one of your own types. That is a framework type without one (MC0019), or one of
        /// your own types whose state is all non-public (<c>HiddenState</c>, MC0034), which every value would write the
        /// same. Null when every value has a key form.
        /// </summary>
        private static (string Path, ITypeSymbol Type, bool HiddenState)? FindTypeWithoutKeyForm(ITypeSymbol type, string path, HashSet<ITypeSymbol> visited)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    return FindTypeWithoutKeyForm(array.ElementType, path + "[]", visited);
                case INamedTypeSymbol { ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable:
                    return FindTypeWithoutKeyForm(nullable.TypeArguments[0], path, visited);
                case not INamedTypeSymbol:
                    return null; // type parameters, dynamic and pointers are decided elsewhere
            }

            var named = (INamedTypeSymbol)type;
            if (named.TypeKind is TypeKind.Enum or TypeKind.Error || DecoratorEmitter.ImplementsCacheKeyProvider(named) || !visited.Add(named))
            {
                return null;
            }

            if (named.IsTupleType || FrameworkComposites.Contains(MetadataName(named.OriginalDefinition)))
            {
                var items = named.IsTupleType
                    ? named.TupleElements.Select(e => (Path: "." + e.Name, Type: e.Type))
                    : named.TypeArguments.Select((t, i) => (Path: CompositeItemPath(named, i), Type: t));
                return items.Select(item => FindTypeWithoutKeyForm(item.Type, path + item.Path, visited)).FirstOrDefault(found => found != null);
            }

            // Collections are written element by element
            if (DecoratorEmitter.GetSequenceElementType(named) is { } element)
            {
                return FindTypeWithoutKeyForm(element, path + "[]", visited);
            }

            if (named.SpecialType != SpecialType.None ||
                named.TypeKind == TypeKind.Interface ||
                named.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable))
            {
                // Built-in values, and values whose runtime type decides their key
                return null;
            }

            if (IsFrameworkType(named))
            {
                return FrameworkTypesWithKeyForm.Contains(MetadataName(named.OriginalDefinition)) ? null : (path, named, false);
            }

            if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            {
                return null;
            }

            // A type written with no member but holding state would give every value one key, so the cache refuses it
            // when the call runs; an abstract type's values are of other types, and a JSON converter writes its own form
            if (!named.IsAbstract && !HasJsonConverter(named) && !WritesAnyMember(named) && HoldsState(named))
            {
                return (path, named, true);
            }

            // Your own types are written by their public properties and fields, so check those
            for (var current = named; current is not null && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public || member.IsImplicitlyDeclared ||
                        member.GetAttributes().Any(a => a.AttributeClass?.Name is "IgnoreDataMemberAttribute" or "IgnoreMemberAttribute" or "JsonIgnoreAttribute"))
                    {
                        continue;
                    }

                    var memberType = member switch
                    {
                        IPropertySymbol { IsIndexer: false, GetMethod.DeclaredAccessibility: Accessibility.Public } property => property.Type,
                        IFieldSymbol field => field.Type,
                        _ => null,
                    };
                    if (memberType != null && FindTypeWithoutKeyForm(memberType, path + "." + member.Name, visited) is { } found)
                    {
                        return found;
                    }
                }
            }

            return null;
        }

        private static bool HasJsonConverter(INamedTypeSymbol type)
            => type.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonConverterAttribute");

        /// <summary>
        /// Whether System.Text.Json writes a member of <paramref name="type"/> with the cache's key options: a public
        /// property with a public getter or a public field, without <c>[JsonIgnore]</c>, or a member with <c>[JsonInclude]</c>.
        /// </summary>
        private static bool WritesAnyMember(INamedTypeSymbol type)
        {
            for (var current = type; current is not null && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member.IsStatic || member.IsImplicitlyDeclared || member is not (IPropertySymbol { IsIndexer: false } or IFieldSymbol)) continue;
                    var attributes = member.GetAttributes();
                    if (attributes.Any(a => a.AttributeClass?.Name == "JsonIncludeAttribute")) return true;
                    if (attributes.Any(a => a.AttributeClass?.Name == "JsonIgnoreAttribute")) continue;
                    if (member is IPropertySymbol { GetMethod.DeclaredAccessibility: Accessibility.Public, DeclaredAccessibility: Accessibility.Public } ||
                        member is IFieldSymbol { DeclaredAccessibility: Accessibility.Public })
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Whether <paramref name="type"/> or a base type has an instance field, an auto-property's included.</summary>
        private static bool HoldsState(INamedTypeSymbol type)
        {
            for (var current = type; current is not null && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member.IsStatic) continue;
                    if (member is IFieldSymbol) return true;
                    if (member is IPropertySymbol { IsIndexer: false, IsAbstract: false } property && IsAutoProperty(property)) return true;
                }
            }

            return false;
        }

        // A property declared without accessor bodies keeps its value in a field the compiler adds
        private static bool IsAutoProperty(IPropertySymbol property)
            => property.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>()
                .Any(p => p.ExpressionBody == null && p.AccessorList?.Accessors.All(a => a.Body == null && a.ExpressionBody == null) == true);

        /// <summary>Where a composite's type argument is, as the path in MC0019 names it.</summary>
        private static string CompositeItemPath(INamedTypeSymbol composite, int index) => composite.Name switch
        {
            "Memory" or "ReadOnlyMemory" => "[]",
            "KeyValuePair" => index == 0 ? ".Key" : ".Value",
            _ => index == 7 ? ".Rest" : ".Item" + (index + 1),
        };

        // Types whose instances change while they are read or used, so one cached instance can't be handed to several
        // callers at once: JsonNode parses lazily, a stream has a position, DataTable and StringBuilder aren't thread-safe.
        private static readonly string[] SharedUnsafeTypes =
        {
            "System.Text.Json.Nodes.JsonNode",
            "System.Data.DataTable",
            "System.Data.DataSet",
            "System.Data.DataRow",
            "System.Data.DataView",
            "System.IO.Stream",
            "System.Text.StringBuilder",
        };

        /// <summary>
        /// The declared result type, or the part of it (an element, a tuple item), that is not safe to hand to several
        /// callers at once, such as <c>JsonObject</c> or <c>DataTable</c>. Null when there is none.
        /// </summary>
        private static ITypeSymbol? FindSharedUnsafeResultType(ITypeSymbol type)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    return FindSharedUnsafeResultType(array.ElementType);
                case INamedTypeSymbol { ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable:
                    return FindSharedUnsafeResultType(nullable.TypeArguments[0]);
                case not INamedTypeSymbol:
                    return null;
            }

            var named = (INamedTypeSymbol)type;
            for (var current = named; current != null; current = current.BaseType)
            {
                if (System.Array.IndexOf(SharedUnsafeTypes, MetadataName(current.OriginalDefinition)) >= 0)
                {
                    return named;
                }
            }

            if (named.IsTupleType || FrameworkComposites.Contains(MetadataName(named.OriginalDefinition)))
            {
                return (named.IsTupleType ? named.TupleElements.Select(e => e.Type) : named.TypeArguments)
                    .Select(FindSharedUnsafeResultType).FirstOrDefault(found => found != null);
            }

            return DecoratorEmitter.GetSequenceElementType(named) is { } element ? FindSharedUnsafeResultType(element) : null;
        }

        /// <summary>
        /// The declared result type, or the part of it, that a serializer can't restore from a distributed cache:
        /// <c>object</c>, an interface or an abstract class, also as a collection's element. A collection interface
        /// such as <c>IReadOnlyList&lt;T&gt;</c> itself is fine; it is read back as a list.
        /// </summary>
        private static ITypeSymbol? FindUnrestorableResultType(ITypeSymbol type)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    return FindUnrestorableResultType(array.ElementType);
                case IDynamicTypeSymbol:
                    return type;
                case INamedTypeSymbol { ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable:
                    return FindUnrestorableResultType(nullable.TypeArguments[0]);
                case not INamedTypeSymbol:
                    return null;
            }

            var named = (INamedTypeSymbol)type;
            if (named.SpecialType == SpecialType.System_Object)
            {
                return named;
            }

            if (named.IsTupleType || FrameworkComposites.Contains(MetadataName(named.OriginalDefinition)))
            {
                return (named.IsTupleType ? named.TupleElements.Select(e => e.Type) : named.TypeArguments)
                    .Select(FindUnrestorableResultType).FirstOrDefault(found => found != null);
            }

            if (DecoratorEmitter.GetSequenceElementType(named) is { } element)
            {
                return FindUnrestorableResultType(element);
            }

            return (named.TypeKind == TypeKind.Interface || (named.TypeKind == TypeKind.Class && named.IsAbstract)) && !NamesItsDerivedTypes(named) ? named : null;
        }

        /// <summary>
        /// Whether the serializer is told which types derive from <paramref name="type"/>, with System.Text.Json's
        /// <c>[JsonDerivedType]</c> or <c>[JsonPolymorphic]</c> or MessagePack's <c>[Union]</c>, so it writes and
        /// restores them as themselves.
        /// </summary>
        private static bool NamesItsDerivedTypes(INamedTypeSymbol type)
            => type.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() is
                "System.Text.Json.Serialization.JsonDerivedTypeAttribute" or
                "System.Text.Json.Serialization.JsonPolymorphicAttribute" or
                "MessagePack.UnionAttribute");
    }
}
