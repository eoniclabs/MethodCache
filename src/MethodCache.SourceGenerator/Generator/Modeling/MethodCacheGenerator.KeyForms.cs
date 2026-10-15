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
        /// The first framework type without a key form in a key parameter: the parameter's type, an element, a tuple
        /// item, or a public property or field of one of your own types. Null when every value has a key form.
        /// </summary>
        private static (string Path, ITypeSymbol Type)? FindTypeWithoutKeyForm(ITypeSymbol type, string path, HashSet<ITypeSymbol> visited)
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
                return FrameworkTypesWithKeyForm.Contains(MetadataName(named.OriginalDefinition)) ? null : (path, named);
            }

            if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            {
                return null;
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

        /// <summary>Where a composite's type argument is, as the path in MC0019 names it.</summary>
        private static string CompositeItemPath(INamedTypeSymbol composite, int index) => composite.Name switch
        {
            "Memory" or "ReadOnlyMemory" => "[]",
            "KeyValuePair" => index == 0 ? ".Key" : ".Value",
            _ => index == 7 ? ".Rest" : ".Item" + (index + 1),
        };

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

            return named.TypeKind == TypeKind.Interface || (named.TypeKind == TypeKind.Class && named.IsAbstract) ? named : null;
        }
    }
}
