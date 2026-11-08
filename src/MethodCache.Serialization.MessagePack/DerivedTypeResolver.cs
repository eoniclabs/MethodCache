using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using MessagePack;
using MessagePack.Formatters;
using MethodCache.Infrastructure;

namespace MethodCache.Serialization.MessagePack;

/// <summary>
/// Writes the derived types MethodCache's generator registered for a declared type, such as a <c>Dog</c> in a
/// <c>List&lt;Animal&gt;</c>, with their name, and restores each as itself. Other types, and declared types that name
/// their derived types with <see cref="UnionAttribute"/>, are left to the resolver it wraps.
/// </summary>
internal sealed class DerivedTypeResolver : IFormatterResolver
{
    private readonly IFormatterResolver _inner;
    private readonly Dictionary<Type, Type[]> _derived;
    private readonly ConcurrentDictionary<Type, object?> _formatters = new();

    internal DerivedTypeResolver(IFormatterResolver inner, Dictionary<Type, Type[]> derived)
    {
        _inner = inner;
        _derived = derived;
    }

    public IMessagePackFormatter<T>? GetFormatter<T>()
        => (IMessagePackFormatter<T>?)_formatters.GetOrAdd(typeof(T), static (type, self) =>
            self._derived.TryGetValue(type, out var derived) && !type.IsDefined(typeof(UnionAttribute), inherit: false)
                ? new DerivedTypeFormatter<T>(self._inner, derived)
                : self._inner.GetFormatter<T>(), this);
}

/// <summary>
/// A declared type's value as an array of its derived type's name, or nil for the declared type itself, and the value.
/// A derived type that isn't registered, such as a proxy made at run time, is written as its nearest registered base,
/// so the check before sharing keeps the value in memory; a name that isn't registered is never read.
/// </summary>
internal sealed class DerivedTypeFormatter<T> : IMessagePackFormatter<T>
{
    private readonly IFormatterResolver _inner;
    private readonly Dictionary<Type, (string Name, DerivedTypeWriters.ITyped Typed)> _byType = [];
    private readonly Dictionary<string, DerivedTypeWriters.ITyped> _byName = new(StringComparer.Ordinal);

    internal DerivedTypeFormatter(IFormatterResolver inner, Type[] derived)
    {
        _inner = inner;
        foreach (var type in derived)
        {
            var typed = DerivedTypeWriters.Create(type, inner);
            _byType.Add(type, (DerivedTypeRegistry.NameOf(type), typed));
            _byName.Add(DerivedTypeRegistry.NameOf(type), typed);
        }
    }

    public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteArrayHeader(2);
        for (var type = value.GetType(); type != null && type != typeof(T); type = type.BaseType)
        {
            if (!_byType.TryGetValue(type, out var entry)) continue;
            writer.Write(entry.Name);
            entry.Typed.Write(ref writer, value, options);
            return;
        }

        writer.WriteNil();
        _inner.GetFormatterWithVerify<T>().Serialize(ref writer, value, options);
    }

    public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) return default!;

        options.Security.DepthStep(ref reader);
        try
        {
            if (reader.ReadArrayHeader() != 2)
                throw new MessagePackSerializationException($"A {typeof(T)} is stored as its type's name and its value.");
            if (reader.TryReadNil()) return _inner.GetFormatterWithVerify<T>().Deserialize(ref reader, options);

            var name = reader.ReadString();
            return name != null && _byName.TryGetValue(name, out var typed)
                ? (T)typed.Read(ref reader, options)!
                : throw new MessagePackSerializationException($"'{name}' is not a type registered for {typeof(T)}.");
        }
        finally
        {
            reader.Depth--;
        }
    }
}

/// <summary>Writes and reads a value as its derived type, with that type's own formatter from the wrapped resolver.</summary>
internal static class DerivedTypeWriters
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The contractless resolver MessagePack's defaults use already needs dynamic code.")]
    [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = "The derived types are referenced by the generated registration.")]
    internal static ITyped Create(Type type, IFormatterResolver inner)
        => (ITyped)Activator.CreateInstance(typeof(Typed<>).MakeGenericType(type), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [inner], null)!;

    internal interface ITyped
    {
        void Write(ref MessagePackWriter writer, object value, MessagePackSerializerOptions options);
        object? Read(ref MessagePackReader reader, MessagePackSerializerOptions options);
    }

    /// <summary>The derived type's own formatter from the wrapped resolver, so it isn't named again.</summary>
    private sealed class Typed<TDerived>(IFormatterResolver inner) : ITyped
    {
        public void Write(ref MessagePackWriter writer, object value, MessagePackSerializerOptions options)
            => inner.GetFormatterWithVerify<TDerived>().Serialize(ref writer, (TDerived)value, options);

        public object? Read(ref MessagePackReader reader, MessagePackSerializerOptions options)
            => inner.GetFormatterWithVerify<TDerived>().Deserialize(ref reader, options);
    }
}
