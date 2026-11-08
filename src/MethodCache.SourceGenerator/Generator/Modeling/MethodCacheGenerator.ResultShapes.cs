#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Result shapes ========================
        // The distributed cache keeps a method's entries apart by a fingerprint of what a serializer writes for its
        // result. A deploy that renames a property, changes its type or renumbers an enum gets new entries, rather than
        // reading the ones the previous build wrote with missing or wrong values. Removals and tags still reach both.

        /// <summary>
        /// The fingerprint of a result type: the types in it, the names, types and serializer attributes of the members
        /// a serializer writes, and the names and values of enum members. Framework types count by name only. The
        /// classes and interfaces in it are returned too, so the derived types registered for them count as well.
        /// </summary>
        internal static (string Shape, EquatableArray<string> Bases) DescribeResultShape(ITypeSymbol type)
        {
            var writer = new ResultShapeWriter();
            writer.Write(type);
            return (ShapeHash(writer.Text.ToString()), new EquatableArray<string>(writer.Bases.Distinct().OrderBy(static b => b, System.StringComparer.Ordinal)));
        }

        /// <summary>
        /// The fingerprint a cached method's entries carry: its result's own, and those of the derived types registered
        /// for the classes and interfaces in it, as <c>fqn=shape</c>.
        /// </summary>
        internal static string CombineResultShape(string shape, EquatableArray<string> bases, ILookup<string, string> derivedShapes)
        {
            var derived = bases.SelectMany(b => derivedShapes[b]).Distinct().OrderBy(static d => d, System.StringComparer.Ordinal).ToList();
            return derived.Count == 0 ? shape : ShapeHash(shape + ";" + string.Join(";", derived));
        }

        /// <summary>64-bit FNV-1a over the text's UTF-16 code units, as 16 hex digits: the same on every machine and build.</summary>
        internal static string ShapeHash(string text)
        {
            var hash = 14695981039346656037UL;
            foreach (var c in text)
            {
                hash = unchecked((hash ^ c) * 1099511628211UL);
            }

            return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }

        private sealed class ResultShapeWriter
        {
            private readonly HashSet<ITypeSymbol> _visited = new(SymbolEqualityComparer.Default);

            internal StringBuilder Text { get; } = new();

            internal List<string> Bases { get; } = new();

            internal void Write(ITypeSymbol type)
            {
                switch (type)
                {
                    case IArrayTypeSymbol array:
                        Text.Append('[').Append(array.Rank).Append(']');
                        Write(array.ElementType);
                        return;
                    case ITypeParameterSymbol parameter:
                        Text.Append('!').Append(parameter.Name).Append(';');
                        return;
                    case not INamedTypeSymbol:
                        Text.Append(type.ToDisplayString()).Append(';');
                        return;
                }

                var named = (INamedTypeSymbol)type;
                var name = Utils.GetFullyQualifiedName(named);
                Text.Append(name);
                if (!_visited.Add(named))
                {
                    Text.Append(';');
                    return;
                }

                Text.Append('{');
                foreach (var argument in Utils.AllTypeArguments(named)) Write(argument);
                if (!IsFrameworkType(named))
                {
                    WriteAttributes(named);
                    if (named.TypeKind == TypeKind.Enum)
                    {
                        WriteEnum(named);
                    }
                    else
                    {
                        if (named.TypeKind == TypeKind.Interface || named.TypeKind == TypeKind.Class && !named.IsSealed && !named.IsStatic) Bases.Add(name);
                        foreach (var member in ShapeMembers(named).OrderBy(static m => m.Name, System.StringComparer.Ordinal))
                        {
                            Text.Append(member.Name).Append(':');
                            WriteAttributes(member);
                            Write(member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type);
                        }
                    }
                }

                Text.Append('}');
            }

            private void WriteEnum(INamedTypeSymbol type)
            {
                Text.Append(type.EnumUnderlyingType?.SpecialType.ToString()).Append('|');
                foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(static f => f.HasConstantValue))
                {
                    Text.Append(field.Name).Append('=').Append(System.Convert.ToString(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture));
                    WriteAttributes(field);
                    Text.Append(',');
                }
            }

            // The attributes serializers read, with their arguments; a type an argument names is written too, so the
            // derived types [JsonDerivedType] or [Union] list count as part of the shape
            private void WriteAttributes(ISymbol symbol)
            {
                foreach (var attribute in symbol.GetAttributes().Where(static a => IsSerializerAttribute(a.AttributeClass)))
                {
                    Text.Append('@').Append(attribute.AttributeClass!.ToDisplayString()).Append('(');
                    foreach (var argument in attribute.ConstructorArguments) WriteArgument(argument);
                    foreach (var named in attribute.NamedArguments)
                    {
                        Text.Append(named.Key).Append('=');
                        WriteArgument(named.Value);
                    }

                    Text.Append(')');
                }
            }

            private void WriteArgument(TypedConstant argument)
            {
                switch (argument.Kind)
                {
                    case TypedConstantKind.Array:
                        Text.Append('[');
                        foreach (var item in argument.Values) WriteArgument(item);
                        Text.Append(']');
                        break;
                    case TypedConstantKind.Type when argument.Value is ITypeSymbol type:
                        Write(type);
                        break;
                    default:
                        Text.Append(System.Convert.ToString(argument.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "null").Append(',');
                        break;
                }
            }

            private static bool IsSerializerAttribute(INamedTypeSymbol? attribute)
            {
                var ns = attribute?.ContainingNamespace?.ToDisplayString();
                return ns is "System.Text.Json.Serialization" or "System.Runtime.Serialization" or "MessagePack" ||
                       ns?.StartsWith("MessagePack.", System.StringComparison.Ordinal) == true;
            }
        }

        /// <summary>
        /// The members a serializer writes for a type: the public ones <see cref="SerializedMemberTypes"/> reads, and
        /// non-public ones a serializer attribute includes, such as <c>[JsonInclude]</c> or MessagePack's <c>[Key]</c>.
        /// </summary>
        private static IEnumerable<ISymbol> ShapeMembers(INamedTypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Interface)
            {
                foreach (var property in type.GetMembers().OfType<IPropertySymbol>().Where(static p => !p.IsStatic && !p.IsIndexer && !IgnoredBySerializers(p)))
                    yield return property;
                yield break;
            }

            var names = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var member in SerializedMembers(type))
            {
                names.Add(member.Name);
                yield return member;
            }

            for (var current = type; current != null && !IsFrameworkType(current); current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member.IsStatic || member.DeclaredAccessibility == Accessibility.Public || member.IsImplicitlyDeclared ||
                        member is not (IPropertySymbol { IsIndexer: false } or IFieldSymbol { IsConst: false }) ||
                        !member.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() is
                            "System.Text.Json.Serialization.JsonIncludeAttribute" or "MessagePack.KeyAttribute" or "System.Runtime.Serialization.DataMemberAttribute"))
                    {
                        continue;
                    }

                    if (names.Add(member.Name)) yield return member;
                }
            }
        }
    }
}
