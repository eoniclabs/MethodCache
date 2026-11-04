using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MethodCache.Analyzers
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

    /// <summary>
    /// The entity types that the compilation maps with <c>EntityFrameworkCoreOptions.Entity&lt;T&gt;</c>. Read once per
    /// compilation, from every file, so a write in one file is checked against a mapping in another.
    /// </summary>
    internal sealed class EntityTagMapping
    {
        private const string OptionsType = "MethodCache.EntityFrameworkCore.EntityFrameworkCoreOptions";
        private const string BuilderType = "MethodCache.EntityFrameworkCore.EntityTagsBuilder`1";

        private readonly Dictionary<ITypeSymbol, EntityWrites> _types;

        private EntityTagMapping(Dictionary<ITypeSymbol, EntityWrites>? types) => _types = types ?? new Dictionary<ITypeSymbol, EntityWrites>(SymbolEqualityComparer.Default);

        /// <summary>
        /// No mapping is visible, as in a project that writes to the database while another one maps the types, or a
        /// mapping is generic: every write counts.
        /// </summary>
        public bool IsUnknown => _types.Count == 0;

        /// <summary>
        /// The changes to <paramref name="entityType"/> that invalidate tags: its own mapping, a base type's (a derived
        /// row has its base type's tags) and a derived type's (a write to the base type's rows can reach derived rows).
        /// </summary>
        public EntityWrites For(ITypeSymbol entityType)
        {
            if (IsUnknown || entityType is not INamedTypeSymbol) return EntityWrites.Any;
            var writes = EntityWrites.None;
            foreach (var mapped in _types)
            {
                if (SymbolEqualityComparer.Default.Equals(mapped.Key, entityType)
                    || DerivesFrom(entityType, mapped.Key) || DerivesFrom(mapped.Key, entityType))
                {
                    writes |= mapped.Value;
                }
            }
            return writes;
        }

        public static EntityTagMapping Read(Compilation compilation, CancellationToken cancellationToken)
        {
            var options = compilation.GetTypeByMetadataName(OptionsType);
            if (options == null) return new EntityTagMapping(null);
            var builder = compilation.GetTypeByMetadataName(BuilderType);

            var types = new Dictionary<ITypeSymbol, EntityWrites>(SymbolEqualityComparer.Default);
            foreach (var tree in compilation.SyntaxTrees)
            {
                SemanticModel? model = null;
                foreach (var name in tree.GetRoot(cancellationToken).DescendantNodes().OfType<GenericNameSyntax>())
                {
                    if (name.Identifier.ValueText != "Entity" || name.TypeArgumentList.Arguments.Count != 1) continue;
                    if (name.Parent is not MemberAccessExpressionSyntax access || access.Name != name
                        || access.Parent is not InvocationExpressionSyntax invocation) continue;

                    // Only the few files with a call named Entity<T> need a semantic model: usually the startup file
                    // and the DbContext's OnModelCreating
#pragma warning disable RS1030 // The mapping is in other files than the write, and the analyzer reads it once per compilation
                    model ??= compilation.GetSemanticModel(tree);
#pragma warning restore RS1030
                    if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method
                        || !SymbolEqualityComparer.Default.Equals(method.ContainingType, options)) continue;

                    var entity = method.TypeArguments[0];
                    // A mapping through a type parameter could be any type
                    if (entity is not INamedTypeSymbol) return new EntityTagMapping(null);
                    var writes = method.Parameters.Length == 1 && method.Parameters[0].Type is IArrayTypeSymbol
                        ? EntityWrites.Any
                        : BuilderWrites(invocation, model, builder, cancellationToken);
                    types[entity] = types.TryGetValue(entity, out var known) ? known | writes : writes;
                }
            }
            return new EntityTagMapping(types);
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

        private static bool DerivesFrom(ITypeSymbol type, ITypeSymbol baseType)
        {
            for (var current = type.BaseType; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
            }
            return false;
        }
    }
}
