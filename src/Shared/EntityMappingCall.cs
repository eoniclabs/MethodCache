using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MethodCache.Internal
{
    /// <summary>Which changes to an entity type invalidate tags, as mapped with <c>ef.Entity&lt;T&gt;(...)</c>.</summary>
    [Flags]
    internal enum EntityWrites
    {
        None = 0,

        /// <summary>A deleted row invalidates tags: <c>WhenDeleted(...)</c>.</summary>
        Delete = 1,

        /// <summary>Any change invalidates tags: <c>Entity&lt;T&gt;("...")</c> or <c>Tags(...)</c>.</summary>
        Any = 3,
    }

    internal enum EntityMappingKind
    {
        /// <summary><c>ef.Entity&lt;T&gt;(...)</c>: the changes to <c>T</c> that invalidate tags.</summary>
        Entity,

        /// <summary><c>ef.Entity&lt;T&gt;(...)</c> through a type parameter: the mapping could be any type.</summary>
        AnyType,

        /// <summary><c>UseEntityFrameworkCore&lt;TContext&gt;(...)</c>: a context whose DbSets name tables.</summary>
        Context,

        /// <summary><c>ToTable("name")</c> on the builder of one or more entity types.</summary>
        Table,
    }

    // Compiled into the analyzer and the generator: MC0027 reads these calls in its own project, and the generator
    // writes them into the assembly, so MC0027 in a project that references it, such as a test project, reads them too.
    internal readonly struct EntityMappingCall
    {
        internal const string OptionsType = "MethodCache.EntityFrameworkCore.EntityFrameworkCoreOptions";
        internal const string BuilderType = "MethodCache.EntityFrameworkCore.EntityTagsBuilder`1";

        private EntityMappingCall(EntityMappingKind kind, ImmutableArray<ITypeSymbol> types, EntityWrites writes = EntityWrites.None, string? table = null)
        {
            Kind = kind;
            Types = types;
            Writes = writes;
            Table = table;
        }

        public EntityMappingKind Kind { get; }

        /// <summary>The entity type, the context type, or the entity types a table name is given to.</summary>
        public ImmutableArray<ITypeSymbol> Types { get; }

        public EntityWrites Writes { get; }

        public string? Table { get; }

        internal static EntityMappingCall Entity(ITypeSymbol entity, EntityWrites writes) => new EntityMappingCall(EntityMappingKind.Entity, ImmutableArray.Create(entity), writes);

        internal static EntityMappingCall AnyType() => new EntityMappingCall(EntityMappingKind.AnyType, ImmutableArray<ITypeSymbol>.Empty);

        internal static EntityMappingCall Context(ITypeSymbol context) => new EntityMappingCall(EntityMappingKind.Context, ImmutableArray.Create(context));

        internal static EntityMappingCall TableOf(ImmutableArray<ITypeSymbol> entities, string table) => new EntityMappingCall(EntityMappingKind.Table, entities, table: table);

        /// <summary>A call that may be a mapping, told from its syntax alone: <c>Entity&lt;T&gt;</c>, <c>UseEntityFrameworkCore&lt;T&gt;</c> or <c>ToTable</c>.</summary>
        public static bool IsCandidate(SyntaxNode node)
        {
            if (node is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } invocation) return false;
            var name = access.Name;
            return name is GenericNameSyntax { Identifier.ValueText: "Entity" or "UseEntityFrameworkCore", TypeArgumentList.Arguments.Count: 1 }
                || (name.Identifier.ValueText == "ToTable" && invocation.ArgumentList.Arguments.Count > 0);
        }

        /// <summary>The mapping a candidate call makes, or null when it makes none.</summary>
        public static EntityMappingCall? Read(InvocationExpressionSyntax invocation, SemanticModel model, INamedTypeSymbol options, INamedTypeSymbol? builder,
            CancellationToken cancellationToken)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access
                || model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method) return null;

            var name = access.Name.Identifier.ValueText;
            if (name == "UseEntityFrameworkCore")
            {
                return method.TypeArguments.Length == 1 && method.TypeArguments[0] is INamedTypeSymbol context ? Context(context) : null;
            }
            if (name == "ToTable")
            {
                // modelBuilder.Entity<T>().ToTable("name"), or builder.ToTable("name") in an entity configuration
                if (invocation.ArgumentList.Arguments.Count == 0
                    || model.GetConstantValue(invocation.ArgumentList.Arguments[0].Expression, cancellationToken).Value is not string table) return null;
                var receiver = method.ReducedFrom != null ? method.ReceiverType : method.ContainingType;
                var entities = method.TypeArguments.AddRange(receiver is INamedTypeSymbol { IsGenericType: true } generic
                    ? generic.TypeArguments
                    : ImmutableArray<ITypeSymbol>.Empty);
                return TableOf(entities, table);
            }
            if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, options) || method.TypeArguments.Length != 1) return null;

            var entity = method.TypeArguments[0];
            if (entity is not INamedTypeSymbol) return AnyType();
            var writes = method.Parameters.Length == 1 && method.Parameters[0].Type is IArrayTypeSymbol
                ? EntityWrites.Any
                : BuilderWrites(invocation, model, builder, cancellationToken);
            return Entity(entity, writes);
        }

        /// <summary>
        /// The changes a builder lambda maps: <c>Tags(...)</c> for any change, <c>WhenDeleted(...)</c> for a deleted
        /// row. A builder handed to other code could map anything.
        /// </summary>
        private static EntityWrites BuilderWrites(InvocationExpressionSyntax invocation, SemanticModel model, INamedTypeSymbol? builder,
            CancellationToken cancellationToken)
        {
            if (invocation.ArgumentList.Arguments.Count != 1
                || invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda) return EntityWrites.Any;
            var parameter = lambda switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
                ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized => parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
                _ => null,
            };
            if (parameter == null) return EntityWrites.Any;

            var writes = EntityWrites.None;
            foreach (var node in lambda.Body.DescendantNodesAndSelf())
            {
                // The builder passed on as an argument
                if (node is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == parameter && identifier.Parent is ArgumentSyntax)
                    return EntityWrites.Any;
                if (node is not InvocationExpressionSyntax call
                    || model.GetSymbolInfo(call, cancellationToken).Symbol is not IMethodSymbol method
                    || !SymbolEqualityComparer.Default.Equals(method.ContainingType.OriginalDefinition, builder)) continue;
                writes |= method.Name == "WhenDeleted" ? EntityWrites.Delete : EntityWrites.Any;
            }
            return writes == EntityWrites.None ? EntityWrites.Any : writes;
        }
    }

    /// <summary>
    /// A project's mapping calls as <c>[assembly: AssemblyMetadata("MethodCache.EntityFrameworkCore.Mapping", "...")]</c>
    /// values: <c>entity:any:Shop.Product</c>, <c>entity:delete:Shop.Product</c>, <c>any-type</c>,
    /// <c>context:Shop.ShopDb</c> and <c>table:Shop.Product;Shop.Item:catalog_items</c>.
    /// </summary>
    internal static class EntityMappingMetadata
    {
        internal const string Key = "MethodCache.EntityFrameworkCore.Mapping";
        internal const string AnyTypeValue = "any-type";

        /// <summary>The value for a call, or null when it names no type that another project can look up.</summary>
        public static string? Write(EntityMappingCall call)
        {
            switch (call.Kind)
            {
                case EntityMappingKind.AnyType:
                    return AnyTypeValue;
                case EntityMappingKind.Entity:
                    // A constructed generic type can't be looked up by name, and could be any of its kind
                    return MetadataName(call.Types[0]) is { } entity
                        ? "entity:" + (call.Writes == EntityWrites.Delete ? "delete" : "any") + ":" + entity
                        : AnyTypeValue;
                case EntityMappingKind.Context:
                    return MetadataName(call.Types[0]) is { } context ? "context:" + context : null;
                default:
                    var entities = call.Types.Select(MetadataName).Where(n => n != null).ToList();
                    return entities.Count == 0 ? null : "table:" + string.Join(";", entities) + ":" + call.Table;
            }
        }

        /// <summary>The mapping calls written into the assemblies a compilation references.</summary>
        public static IEnumerable<(IAssemblySymbol Assembly, EntityMappingCall Call)> Read(Compilation compilation)
        {
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                // Any other assembly may be a project with a mapping, also when it doesn't list MethodCache.EntityFrameworkCore
                // among its references: a reference assembly, as a build passes a project, keeps only what its public
                // signatures use. The attributes are read once and cached with the assembly.
                if (IsFramework(assembly.Identity.Name)) continue;
                foreach (var attribute in assembly.GetAttributes())
                {
                    if (attribute.AttributeClass is not { Name: "AssemblyMetadataAttribute" } || attribute.ConstructorArguments.Length != 2
                        || attribute.ConstructorArguments[0].Value as string != Key
                        || attribute.ConstructorArguments[1].Value is not string value) continue;
                    if (Parse(compilation, assembly, value) is { } call) yield return (assembly, call);
                }
            }
        }

        private static bool IsFramework(string name)
            => name is "mscorlib" or "netstandard" or "System" || name.StartsWith("System.", StringComparison.Ordinal);

        private static EntityMappingCall? Parse(Compilation compilation, IAssemblySymbol assembly, string value)
        {
            if (value == AnyTypeValue) return EntityMappingCall.AnyType();
            var parts = value.Split(new[] { ':' }, 3);
            switch (parts[0])
            {
                case "entity" when parts.Length == 3:
                    return Type(compilation, assembly, parts[2]) is { } entity
                        ? EntityMappingCall.Entity(entity, parts[1] == "delete" ? EntityWrites.Delete : EntityWrites.Any)
                        : null;
                case "context" when parts.Length == 2:
                    return Type(compilation, assembly, parts[1]) is { } context ? EntityMappingCall.Context(context) : null;
                case "table" when parts.Length == 3:
                    var entities = parts[1].Split(';').Select(name => Type(compilation, assembly, name)).OfType<ITypeSymbol>().ToImmutableArray();
                    return entities.IsEmpty ? null : EntityMappingCall.TableOf(entities, parts[2]);
                default:
                    return null;
            }
        }

        private static INamedTypeSymbol? Type(Compilation compilation, IAssemblySymbol assembly, string metadataName)
            => assembly.GetTypeByMetadataName(metadataName) ?? compilation.GetTypeByMetadataName(metadataName);

        /// <summary><c>Namespace.Outer+Inner`1</c>, or null for a type that isn't a named, non-constructed type.</summary>
        internal static string? MetadataName(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol named || type.TypeKind == TypeKind.Error) return null;
            if (named.IsGenericType && !SymbolEqualityComparer.Default.Equals(named, named.OriginalDefinition)) return null;
            var name = new StringBuilder(named.MetadataName);
            for (var outer = named.ContainingType; outer != null; outer = outer.ContainingType) name.Insert(0, outer.MetadataName + "+");
            if (named.ContainingNamespace is { IsGlobalNamespace: false } ns) name.Insert(0, ns.ToDisplayString() + ".");
            return name.ToString();
        }
    }
}
