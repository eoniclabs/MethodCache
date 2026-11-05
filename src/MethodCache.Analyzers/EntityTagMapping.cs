using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    /// The entity types that the compilation maps with <c>EntityFrameworkCoreOptions.Entity&lt;T&gt;</c>, and the names
    /// their tables may have. Read once per compilation, from every file, so a write in one file is checked against a
    /// mapping in another.
    /// </summary>
    internal sealed class EntityTagMapping
    {
        private const string OptionsType = "MethodCache.EntityFrameworkCore.EntityFrameworkCoreOptions";
        private const string BuilderType = "MethodCache.EntityFrameworkCore.EntityTagsBuilder`1";

        private readonly Dictionary<ITypeSymbol, EntityWrites> _types;

        /// <summary>Normalized names the tables of those types may have (<see cref="SqlText.Normalize"/>).</summary>
        private readonly HashSet<string> _tables = new HashSet<string>(StringComparer.Ordinal);

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

        /// <summary>
        /// Whether SQL names a table of a type whose changes invalidate tags: its class name, the name of a
        /// <c>DbSet</c> of it, or a name given with <c>ToTable</c> or <c>[Table]</c>, in any case, with or without
        /// underscores and a plural ending. Call only when <see cref="IsUnknown"/> is false.
        /// </summary>
        public bool NamesTable(List<SqlText.Word> words) => words.Any(word => _tables.Contains(SqlText.Normalize(word.Text)));

        public static EntityTagMapping Read(Compilation compilation, CancellationToken cancellationToken)
        {
            var options = compilation.GetTypeByMetadataName(OptionsType);
            if (options == null) return new EntityTagMapping(null);
            var builder = compilation.GetTypeByMetadataName(BuilderType);

            var types = new Dictionary<ITypeSymbol, EntityWrites>(SymbolEqualityComparer.Default);
            var contexts = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var tableNames = new List<(ImmutableArray<ITypeSymbol> Entities, string Name)>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                SemanticModel? model = null;
                foreach (var invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax access) continue;
                    var name = access.Name;
                    var isEntity = name is GenericNameSyntax { Identifier.ValueText: "Entity", TypeArgumentList.Arguments.Count: 1 };
                    var isContext = name is GenericNameSyntax { Identifier.ValueText: "UseEntityFrameworkCore", TypeArgumentList.Arguments.Count: 1 };
                    var isTable = name.Identifier.ValueText == "ToTable" && invocation.ArgumentList.Arguments.Count > 0;
                    if (!isEntity && !isContext && !isTable) continue;

                    // Only the few files with such a call need a semantic model: usually the startup file and the
                    // DbContext's OnModelCreating or entity configurations
#pragma warning disable RS1030 // The mapping is in other files than the write, and the analyzer reads it once per compilation
                    model ??= compilation.GetSemanticModel(tree);
#pragma warning restore RS1030
                    if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method) continue;

                    if (isContext)
                    {
                        if (method.TypeArguments[0] is INamedTypeSymbol context) contexts.Add(context);
                        continue;
                    }
                    if (isTable)
                    {
                        // modelBuilder.Entity<T>().ToTable("name"), or builder.ToTable("name") in an entity configuration
                        if (model.GetConstantValue(invocation.ArgumentList.Arguments[0].Expression, cancellationToken).Value is string table)
                        {
                            var receiver = method.ReducedFrom != null ? method.ReceiverType : method.ContainingType;
                            var entities = method.TypeArguments.AddRange(receiver is INamedTypeSymbol { IsGenericType: true } generic
                                ? generic.TypeArguments
                                : ImmutableArray<ITypeSymbol>.Empty);
                            tableNames.Add((entities, table));
                        }
                        continue;
                    }
                    if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, options)) continue;

                    var entity = method.TypeArguments[0];
                    // A mapping through a type parameter could be any type
                    if (entity is not INamedTypeSymbol) return new EntityTagMapping(null);
                    var writes = method.Parameters.Length == 1 && method.Parameters[0].Type is IArrayTypeSymbol
                        ? EntityWrites.Any
                        : BuilderWrites(invocation, model, builder, cancellationToken);
                    types[entity] = types.TryGetValue(entity, out var known) ? known | writes : writes;
                }
            }

            var mapping = new EntityTagMapping(types);
            if (!mapping.IsUnknown) mapping.AddTables(compilation, contexts, tableNames);
            return mapping;
        }

        /// <summary>The names the tables of the mapped types may have, from the class names, DbSets, ToTable and [Table].</summary>
        private void AddTables(Compilation compilation, HashSet<INamedTypeSymbol> contexts, List<(ImmutableArray<ITypeSymbol> Entities, string Name)> tableNames)
        {
            foreach (var mapped in _types.Keys)
            {
                // A derived type's rows may be in its base type's table
                for (var type = mapped; type != null && type.SpecialType != SpecialType.System_Object; type = type.BaseType) AddEntity(type);
            }

            foreach (var (entities, name) in tableNames)
            {
                if (entities.Any(entity => For(entity) != EntityWrites.None)) AddTable(name);
            }

            var dbContext = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbContext");
            var dbSet = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbSet`1");
            if (dbContext == null || dbSet == null) return;
            foreach (var type in Types(compilation.Assembly.GlobalNamespace))
            {
                if (DerivesFrom(type, dbContext)) contexts.Add(type);
            }
            foreach (var context in contexts)
            {
                for (var type = context; type != null && !SymbolEqualityComparer.Default.Equals(type, dbContext); type = type.BaseType)
                {
                    foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
                    {
                        if (property.Type is not INamedTypeSymbol set || !SymbolEqualityComparer.Default.Equals(set.OriginalDefinition, dbSet)
                            || For(set.TypeArguments[0]) == EntityWrites.None) continue;
                        AddTable(property.Name);
                        AddEntity(set.TypeArguments[0]);
                    }
                }
            }
        }

        private void AddEntity(ITypeSymbol type)
        {
            AddTable(type.Name);
            foreach (var attribute in type.GetAttributes())
            {
                if (attribute.AttributeClass?.Name == "TableAttribute" && attribute.ConstructorArguments.Length > 0
                    && attribute.ConstructorArguments[0].Value is string table)
                {
                    AddTable(table);
                }
            }
        }

        /// <summary>The name and its plural forms: product, products, categories for category, addresses for address.</summary>
        private void AddTable(string name)
        {
            var table = SqlText.Normalize(name);
            if (table.Length == 0) return;
            _tables.Add(table);
            _tables.Add(table + "s");
            _tables.Add(table + "es");
            if (table.EndsWith("y", StringComparison.Ordinal)) _tables.Add(table.Substring(0, table.Length - 1) + "ies");
        }

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
            {
                if (member is INamespaceOrTypeSymbol inner)
                {
                    if (inner is INamedTypeSymbol type) yield return type;
                    foreach (var nested in Types(inner)) yield return nested;
                }
            }
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
