#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Pipeline Models ========================
        // What flows between pipeline steps holds no symbols or compilations, only values that compare equal when
        // nothing relevant changed. The incremental driver then skips the steps after them, so an edit that does not
        // touch a cached interface regenerates nothing.

        /// <summary>An immutable array that compares by its items, so pipeline models built from it compare by value.</summary>
        internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
        {
            private readonly T[]? _items;

            public EquatableArray(IEnumerable<T> items) => _items = items.ToArray();

            public static EquatableArray<T> Empty => default;

            public int Count => _items?.Length ?? 0;

            public T this[int index] => _items![index];

            public bool Equals(EquatableArray<T> other)
                => (_items ?? Array.Empty<T>()).SequenceEqual(other._items ?? Array.Empty<T>(), EqualityComparer<T>.Default);

            public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

            public override int GetHashCode()
            {
                var hash = 17;
                foreach (var item in this)
                {
                    hash = unchecked((hash * 31) + (item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item)));
                }

                return hash;
            }

            public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// Where a diagnostic points. The syntax tree compares by reference, and the compiler reuses the tree of a file
        /// that did not change, so the location stays equal until its own file is edited.
        /// </summary>
        internal sealed record LocationInfo(SyntaxTree Tree, TextSpan Span)
        {
            public static LocationInfo? From(Location? location)
                => location is { IsInSource: true, SourceTree: { } tree } ? new LocationInfo(tree, location.SourceSpan) : null;

            public Location ToLocation() => Location.Create(Tree, Span);
        }

        /// <summary>A diagnostic to report, kept as values until the output step creates it.</summary>
        internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, LocationInfo? Location, EquatableArray<string?> Arguments, string? ParameterProperty)
        {
            public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location? location, params string?[] arguments)
                => new(descriptor, LocationInfo.From(location), new EquatableArray<string?>(arguments), null);

            /// <summary>Like <see cref="Create"/>, with the unknown parameter name the tag code fix reads.</summary>
            public static DiagnosticInfo CreateWithParameter(DiagnosticDescriptor descriptor, Location? location, string parameter, params string?[] arguments)
                => new(descriptor, LocationInfo.From(location), new EquatableArray<string?>(arguments), parameter);

            public string Id => Descriptor.Id;

            public Diagnostic ToDiagnostic()
            {
                var properties = ParameterProperty is null
                    ? null
                    : System.Collections.Immutable.ImmutableDictionary<string, string?>.Empty.Add("Parameter", ParameterProperty);
                return Diagnostic.Create(Descriptor, Location?.ToLocation(), properties, Arguments.Cast<object?>().ToArray());
            }
        }

        /// <summary>The attribute policy of one cached method, as statements for the generated policy source.</summary>
        internal sealed record PolicyEntry(string MethodId, EquatableArray<string> ParameterNames, EquatableArray<string> PolicyStatements, EquatableArray<string> MetadataStatements);

        /// <summary>What the shared generated files need to know about one interface.</summary>
        internal sealed record InterfaceSummary(
            string Name,
            string AssemblyIdentity,
            string InterfaceFqn,
            string OriginalDefinitionFqn,
            string UnboundInterfaceFqn,
            string DecoratorFqn,
            bool IsGeneric,
            bool HasCachedMethods,
            bool HasInvalidateMethods,
            EquatableArray<PolicyEntry> Policies)
        {
            /// <summary>Whether the interface gets a decorator; one whose every method was rejected only reports diagnostics.</summary>
            public bool Generates => HasCachedMethods || HasInvalidateMethods;
        }

        /// <summary>One interface's generated decorator, the facts the shared files need, and its diagnostics.</summary>
        internal sealed record GeneratedInterface(
            string Key,
            InterfaceSummary Summary,
            string HintName,
            string Source,
            EquatableArray<DiagnosticInfo> Diagnostics);

        /// <summary>One <c>[assembly: CacheInterface]</c>, resolved to its generated interface when it is usable.</summary>
        internal sealed record ExternalInterface(
            string? Key,
            string? DisplayName,
            LocationInfo? Location,
            GeneratedInterface? Interface,
            EquatableArray<DiagnosticInfo> Diagnostics);

        /// <summary>
        /// An interface a class implements, as the decorator registrations compare and name it. The assembly tells apart
        /// two interfaces with the same name, such as one reached through an extern alias.
        /// </summary>
        internal sealed record ImplementedInterface(
            string AssemblyIdentity,
            string Fqn,
            string OriginalDefinitionFqn,
            string DisplayName,
            EquatableArray<string> TypeArgumentFqns,
            bool IsNameable);

        /// <summary>A concrete class, so AddMethodCache() can register an implementation the application did not register.</summary>
        internal sealed record ClassInfo(
            string Fqn,
            string Name,
            string DisplayName,
            bool IsAccessible,
            bool HasPublicConstructor,
            EquatableArray<ImplementedInterface> Interfaces);
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>Lets netstandard2.0 compile records.</summary>
    internal static class IsExternalInit
    {
    }
}
