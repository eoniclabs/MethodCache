using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MethodCache.Internal
{
    // Compiled into the analyzer and the generator. MC0026 looks for per-request state in the constructors a cached
    // implementation takes. The generator writes the classes of a project whose constructors take it, by the interfaces
    // they implement, so MC0026 in a project that references it finds them behind an interface.
    internal static class RequestState
    {
        /// <summary>The .editorconfig option that names the application's own request-state types, comma-separated.</summary>
        internal const string Option = "methodcache.request_state_types";

        /// <summary>
        /// <c>[assembly: AssemblyMetadata(MetadataKey, "Data.IStats|RavenStats|HeaderReader|IHttpContextAccessor")]</c>:
        /// a class of the project that implements the interface takes the state, itself or through the class named third
        /// (empty when the class takes it itself).
        /// </summary>
        internal const string MetadataKey = "MethodCache.RequestState";

        internal static readonly ImmutableHashSet<string> BuiltIn = ImmutableHashSet.Create(StringComparer.Ordinal,
            "Microsoft.AspNetCore.Http.IHttpContextAccessor",
            "Microsoft.AspNetCore.Http.HttpContext",
            "Microsoft.AspNetCore.Http.HttpRequest",
            "System.Security.Claims.ClaimsPrincipal",
            "System.Security.Principal.IPrincipal");

        /// <summary>The names in an <see cref="Option"/> value, such as <c>MyApp.ICurrentUser, MyApp.TenantContext</c>.</summary>
        public static ImmutableHashSet<string> Configured(string? value)
            => value == null
                ? ImmutableHashSet<string>.Empty
                : value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(name => name.Trim())
                    .Where(name => name.Length > 0).ToImmutableHashSet(StringComparer.Ordinal);

        /// <summary>The names a type answers to: its own, its base classes' and its interfaces', as in <c>MyApp.ICurrentUser</c>.</summary>
        public static IEnumerable<string> Names(ITypeSymbol type)
        {
            yield return type.OriginalDefinition.ToDisplayString();
            for (var current = type.BaseType; current is { SpecialType: not SpecialType.System_Object }; current = current.BaseType)
                yield return current.OriginalDefinition.ToDisplayString();
            foreach (var @interface in type.AllInterfaces) yield return @interface.OriginalDefinition.ToDisplayString();
        }

        /// <summary>Whether the type is per-request state: built in, named in <paramref name="configured"/>, or derived from one.</summary>
        public static bool Is(ITypeSymbol type, ImmutableHashSet<string> configured)
            => Names(type).Any(name => BuiltIn.Contains(name) || configured.Contains(name));

        /// <summary>The constructors that dependency injection can call.</summary>
        public static IEnumerable<IMethodSymbol> Constructors(INamedTypeSymbol type)
            => type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared && c.DeclaredAccessibility != Accessibility.Private);

        public static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

        /// <summary>The classes of the compilation's own assembly that implement each interface.</summary>
        public static ILookup<ISymbol, INamedTypeSymbol> Implementations(Compilation compilation)
            => Types(compilation.Assembly.GlobalNamespace)
                .Where(type => type.TypeKind == TypeKind.Class && !type.IsAbstract)
                .SelectMany(type => type.AllInterfaces.Select(i => (Interface: (ISymbol)i.OriginalDefinition, Class: type)))
                .ToLookup(pair => pair.Interface, pair => pair.Class, SymbolEqualityComparer.Default);

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol container)
        {
            foreach (var type in container.GetTypeMembers())
            {
                yield return type;
                foreach (var nested in Types(type)) yield return nested;
            }
            if (container is INamespaceSymbol ns)
                foreach (var child in ns.GetNamespaceMembers())
                    foreach (var type in Types(child))
                        yield return type;
        }

        /// <summary>The classes a constructor parameter brings in: its class, the project's implementations of its interface, and its type arguments' classes.</summary>
        public static IEnumerable<INamedTypeSymbol> Classes(ITypeSymbol type, ILookup<ISymbol, INamedTypeSymbol> implementations)
        {
            var candidates = new List<ITypeSymbol> { type };
            if (type is INamedTypeSymbol { IsGenericType: true } generic) candidates.AddRange(generic.TypeArguments);
            foreach (var candidate in candidates)
            {
                switch (candidate)
                {
                    case INamedTypeSymbol { TypeKind: TypeKind.Class } named:
                        yield return named;
                        break;
                    case INamedTypeSymbol { TypeKind: TypeKind.Interface } named:
                        foreach (var implementation in implementations[named.OriginalDefinition]) yield return implementation;
                        break;
                }
            }
        }

        /// <summary>What a class of a referenced project takes, as its project wrote it.</summary>
        internal sealed class Taken
        {
            public Taken(string @class, string via, string state)
            {
                Class = @class;
                Via = via;
                State = state;
            }

            /// <summary>The class that implements the interface, such as <c>RavenStats</c>.</summary>
            public string Class { get; }

            /// <summary>The class whose constructor takes the state: <see cref="Class"/>, or a class it takes.</summary>
            public string Via { get; }

            /// <summary>The state, such as <c>IHttpContextAccessor</c>.</summary>
            public string State { get; }
        }

        /// <summary>A metadata value; <paramref name="via"/> is null when the class takes the state itself.</summary>
        public static string Write(string interfaceMetadataName, string @class, string? via, string state)
            => interfaceMetadataName + "|" + @class + "|" + via + "|" + state;

        /// <summary>The classes the referenced projects wrote, by the metadata name of the interface they implement.</summary>
        public static ILookup<string, Taken> Read(Compilation compilation)
        {
            var found = new List<(string Interface, Taken Taken)>();
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                var name = assembly.Identity.Name;
                if (name is "mscorlib" or "netstandard" or "System" || name.StartsWith("System.", StringComparison.Ordinal)) continue;
                foreach (var attribute in assembly.GetAttributes())
                {
                    if (attribute.AttributeClass is not { Name: "AssemblyMetadataAttribute" } || attribute.ConstructorArguments.Length != 2
                        || attribute.ConstructorArguments[0].Value as string != MetadataKey
                        || attribute.ConstructorArguments[1].Value is not string value) continue;
                    var parts = value.Split('|');
                    if (parts.Length != 4 || parts[0].Length == 0 || parts[1].Length == 0 || parts[3].Length == 0) continue;
                    found.Add((parts[0], new Taken(parts[1], parts[2].Length == 0 ? parts[1] : parts[2], parts[3])));
                }
            }
            return found.ToLookup(f => f.Interface, f => f.Taken, StringComparer.Ordinal);
        }
    }
}
