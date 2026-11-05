using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MethodCache.Internal;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// The entity types that the compilation maps with <c>EntityFrameworkCoreOptions.Entity&lt;T&gt;</c>, or that a
    /// project it references maps, and the names their tables may have. Read once per compilation, from every file, so a
    /// write in one file is checked against a mapping in another.
    /// </summary>
    internal sealed class EntityTagMapping
    {
        private readonly Dictionary<ITypeSymbol, EntityWrites> _types;

        /// <summary>Normalized names the tables of those types may have (<see cref="SqlText.Normalize"/>).</summary>
        private readonly HashSet<string> _tables = new HashSet<string>(StringComparer.Ordinal);

        private EntityTagMapping(Dictionary<ITypeSymbol, EntityWrites>? types) => _types = types ?? new Dictionary<ITypeSymbol, EntityWrites>(SymbolEqualityComparer.Default);

        /// <summary>
        /// No mapping is visible, as in a project that writes to the database while a project it doesn't reference maps
        /// the types, or a mapping is generic: every write counts.
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
            var options = compilation.GetTypeByMetadataName(EntityMappingCall.OptionsType);
            if (options == null) return new EntityTagMapping(null);
            var builder = compilation.GetTypeByMetadataName(EntityMappingCall.BuilderType);

            var calls = new List<EntityMappingCall>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                SemanticModel? model = null;
                foreach (var invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!EntityMappingCall.IsCandidate(invocation)) continue;
                    // Only the few files with such a call need a semantic model: usually the startup file and the
                    // DbContext's OnModelCreating or entity configurations
#pragma warning disable RS1030 // The mapping is in other files than the write, and the analyzer reads it once per compilation
                    model ??= compilation.GetSemanticModel(tree);
#pragma warning restore RS1030
                    if (EntityMappingCall.Read(invocation, model, options, builder, cancellationToken) is { } call) calls.Add(call);
                }
            }

            // The mapping of a project this one references, such as the API project of a test project, which the
            // generator wrote into that assembly
            var assemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default) { compilation.Assembly };
            foreach (var (assembly, call) in EntityMappingMetadata.Read(compilation))
            {
                calls.Add(call);
                assemblies.Add(assembly);
            }

            var types = new Dictionary<ITypeSymbol, EntityWrites>(SymbolEqualityComparer.Default);
            var contexts = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var tableNames = new List<(ImmutableArray<ITypeSymbol> Entities, string Name)>();
            foreach (var call in calls)
            {
                switch (call.Kind)
                {
                    // A mapping through a type parameter could be any type
                    case EntityMappingKind.AnyType:
                        return new EntityTagMapping(null);
                    case EntityMappingKind.Context:
                        if (call.Types[0] is INamedTypeSymbol context) contexts.Add(context);
                        break;
                    case EntityMappingKind.Table:
                        tableNames.Add((call.Types, call.Table!));
                        break;
                    default:
                        var entity = call.Types[0];
                        types[entity] = types.TryGetValue(entity, out var known) ? known | call.Writes : call.Writes;
                        break;
                }
            }

            var mapping = new EntityTagMapping(types);
            if (!mapping.IsUnknown) mapping.AddTables(compilation, assemblies, contexts, tableNames);
            return mapping;
        }

        /// <summary>The names the tables of the mapped types may have, from the class names, DbSets, ToTable and [Table].</summary>
        private void AddTables(Compilation compilation, IEnumerable<IAssemblySymbol> assemblies, HashSet<INamedTypeSymbol> contexts, List<(ImmutableArray<ITypeSymbol> Entities, string Name)> tableNames)
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
            foreach (var assembly in assemblies)
            {
                foreach (var type in Types(assembly.GlobalNamespace))
                {
                    if (DerivesFrom(type, dbContext)) contexts.Add(type);
                }
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
