using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace MethodCache.Core.Runtime.Distributed;

/// <summary>
/// Whether a value read back from the serializer holds the same types as the value that was written, all the way
/// down. A <c>Dog</c> inside a <c>List&lt;Animal&gt;</c> that comes back as a plain <c>Animal</c>, or an <c>int</c> in
/// a <c>Dictionary&lt;string, object&gt;</c> that comes back as a <c>JsonElement</c>, writes the same bytes again, so
/// only its type shows that data was lost.
/// </summary>
/// <remarks>
/// The walk follows collections (element by element, a dictionary by key) and the public properties and fields a
/// serializer writes: a property with a setter or an auto-property, and a public field, unless it is marked
/// <c>[JsonIgnore]</c>, <c>[IgnoreDataMember]</c>, <c>[IgnoreMember]</c> or <c>[NonSerialized]</c>, so a computed or
/// lazy-loading getter the serializer skips isn't called. A member that is null on either side isn't compared: the
/// serializer may skip it on purpose. A dictionary or set's comparer is compared too, since a serializer writes only the
/// items and reads them back into one with the default comparer, so a case-insensitive lookup would then miss. Positions whose declared type can't hold another type (an <c>int</c>, a sealed
/// record of strings, a <c>List&lt;int&gt;</c>) are never visited, so most values cost nothing.
/// </remarks>
internal static class RoundTripGraph
{
    // As deep as System.Text.Json writes by default; deeper parts aren't compared
    private const int MaxDepth = 64;

    private static readonly ConcurrentDictionary<Type, Facts> Known = new();

    /// <summary>
    /// Whether <paramref name="read"/>, read back for a value declared as <typeparamref name="T"/>, holds the same
    /// types as <paramref name="written"/>.
    /// </summary>
    /// <param name="written">The value the method returned.</param>
    /// <param name="read">The value read back from what the serializer wrote.</param>
    /// <param name="payloadLength">
    /// How many bytes the serializer wrote. Every object takes at least one, so a walk that meets more objects than
    /// that has met some more than once (a graph written with references kept) and remembers them from then on.
    /// </param>
    internal static bool Same<T>(T written, object read, int payloadLength)
    {
        var declared = Declared<T>.Facts;
        var writtenType = written!.GetType();
        if (!declared.SameType(writtenType, read.GetType())) return false;
        if (!declared.MayDiffer) return true;
        var facts = writtenType == declared.Type ? declared : FactsOf(writtenType);
        return facts.IsLeaf || new Walk(payloadLength).Inside(written, read, declared, facts, 0);
    }

    private static class Declared<T>
    {
        public static readonly Facts Facts = FactsOf(typeof(T));
    }

    private sealed class Walk(int objectsBeforeRemembering)
    {
        private int _objectsBeforeRemembering = objectsBeforeRemembering;
        private HashSet<object>? _visited;
        private Facts? _last;

        private bool Position(object? written, object? read, Facts declared, int depth)
        {
            if (written is null || read is null) return true;
            var writtenType = written.GetType();
            if (!declared.SameType(writtenType, read.GetType())) return false;

            // Mostly the declared type; else lists hold one type mostly, so its facts are looked up once
            Facts facts;
            if (writtenType == declared.Type) facts = declared;
            else if (_last?.Type == writtenType) facts = _last;
            else facts = _last = FactsOf(writtenType);

            if (facts.IsLeaf || depth >= MaxDepth) return true;

            // A graph the serializer keeps references in (ReferenceHandler.Preserve) can be cyclic or share objects
            // many times over, so once more objects were met than it could have written, each is compared once.
            // Remembering every object would cost more than the rest of the walk.
            if (!writtenType.IsValueType && (_visited != null || --_objectsBeforeRemembering < 0) &&
                !(_visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance)).Add(written))
            {
                return true;
            }

            return Inside(written, read, declared, facts, depth);
        }

        internal bool Inside(object written, object read, Facts declared, Facts facts, int depth)
        {
            if (facts.Comparer is { } comparer && !SameComparer(comparer(written), FactsOf(read.GetType()).Comparer?.Invoke(read))) return false;
            if (facts.Element != null)
            {
                // The declared element type, since an array behind IEnumerable<Animal> may be a Dog[]
                var element = declared.Element ?? facts.Element;
                return !element.MayDiffer || Elements((IEnumerable)written, read, element, depth);
            }

            foreach (var member in facts.Members)
            {
                if (!member.TryGet(written, out var writtenValue) || !member.TryGet(read, out var readValue)) continue;
                if (!Position(writtenValue, readValue, member.Facts, depth + 1)) return false;
            }

            return true;
        }

        private bool Elements(IEnumerable written, object read, Facts element, int depth)
        {
            if (read is not IEnumerable readItems) return false;

            // A dictionary may enumerate in another order once it is rebuilt, so its values are paired by key
            if (written is IDictionary writtenMap && read is IDictionary readMap && element.DictionaryValue is { } value)
            {
                if (writtenMap.Count != readMap.Count) return false;
                var entries = writtenMap.GetEnumerator();
                try
                {
                    while (entries.MoveNext())
                    {
                        var entry = entries.Entry;
                        if (!readMap.Contains(entry.Key)) return false;
                        if (!Position(entry.Value, readMap[entry.Key], value, depth + 1)) return false;
                    }
                }
                finally
                {
                    (entries as IDisposable)?.Dispose();
                }

                return true;
            }

            if (written is IList writtenList && read is IList readList)
            {
                if (writtenList.Count != readList.Count) return false;
                for (var i = 0; i < writtenList.Count; i++)
                {
                    if (!Position(writtenList[i], readList[i], element, depth + 1)) return false;
                }

                return true;
            }

            var left = written.GetEnumerator();
            var right = readItems.GetEnumerator();
            try
            {
                while (true)
                {
                    var hasLeft = left.MoveNext();
                    if (hasLeft != right.MoveNext()) return false;
                    if (!hasLeft) return true;
                    if (!Position(left.Current, right.Current, element, depth + 1)) return false;
                }
            }
            finally
            {
                (left as IDisposable)?.Dispose();
                (right as IDisposable)?.Dispose();
            }
        }
    }

    private static Facts FactsOf(Type type) => Known.GetOrAdd(type, static type => new Facts(type));

    /// <summary>
    /// Whether a collection read back looks its items up as the written one did. A collection read back without a
    /// comparer of its own isn't compared; <see cref="StringComparer.Ordinal"/> tells strings apart as the default
    /// equality comparer does.
    /// </summary>
    private static bool SameComparer(object? written, object? read)
        => read is null || written is null || written.Equals(read) || (Ordinal(written) && Ordinal(read));

    private static bool Ordinal(object comparer)
        => ReferenceEquals(comparer, StringComparer.Ordinal) || ReferenceEquals(comparer, EqualityComparer<string>.Default);

    /// <summary>
    /// What the walk needs to know about a type, worked out once: as a declared type, whether a value there may come
    /// back holding other types; as a runtime type, what is compared inside it.
    /// </summary>
    private sealed class Facts
    {
        private const int Unknown = 0, Working = 1, No = 2, Yes = 3;

        private readonly Type? _element;
        private readonly bool _collectionInterface;
        private int _mayDiffer;
        private Facts? _elementFacts;
        private Member[]? _serialized;
        private Member[]? _members;
        private Facts? _dictionaryValue;
        private bool _dictionaryValueKnown;
        private Func<object, object?>? _comparer;
        private bool _comparerKnown;

        public Facts(Type type)
        {
            Type = type;
            _element = ElementOf(type);
            _collectionInterface = (type.IsInterface || type.IsAbstract) && _element != null;
        }

        public Type Type { get; }

        /// <summary>For a collection, the facts of its element type; null for anything else.</summary>
        public Facts? Element => _element == null ? null : _elementFacts ??= FactsOf(_element);

        /// <summary>
        /// Nothing inside to compare: not a collection, and no member that may hold another type. A collection is
        /// never one, since what its elements may hold depends on the element type it is declared with.
        /// </summary>
        public bool IsLeaf => _element == null && Members.Length == 0;

        /// <summary>
        /// For a collection with a comparer, such as a <c>Dictionary</c> or <c>HashSet</c>, the getter of its
        /// <c>Comparer</c> or <c>KeyComparer</c>; null for anything else.
        /// </summary>
        public Func<object, object?>? Comparer
        {
            get
            {
                if (Volatile.Read(ref _comparerKnown)) return _comparer;
                _comparer = _element == null ? null : ComparerOf(Type);
                Volatile.Write(ref _comparerKnown, true);
                return _comparer;
            }
        }

        /// <summary>The members compared inside a value of this runtime type: those whose declared type may differ.</summary>
        public Member[] Members => _members ??= Serialized.Where(static m => m.Facts.MayDiffer).ToArray();

        private Member[] Serialized => _serialized ??= _element == null ? SerializedMembers(Type) : [];

        /// <summary>
        /// Whether a value read back as <paramref name="read"/> is the value written as <paramref name="written"/>
        /// where this type is declared: the same type, or, for a collection declared as an interface or abstract
        /// type, any implementation of it, since only its elements are written.
        /// </summary>
        public bool SameType(Type written, Type read)
            => read == written || (_collectionInterface && Type.IsAssignableFrom(read));

        /// <summary>
        /// Whether a value declared as this type may come back holding other types, at the position itself or
        /// inside it: a type that can be derived from, an interface, <c>object</c>, or one that holds such a type in
        /// an element or member.
        /// </summary>
        public bool MayDiffer
        {
            get
            {
                var known = Volatile.Read(ref _mayDiffer);
                if (known >= No) return known == Yes;

                // A type that holds itself is taken to differ while it is worked out, which can only walk more
                if (known == Working || Interlocked.CompareExchange(ref _mayDiffer, Working, Unknown) != Unknown) return true;
                var result = Compute();
                Volatile.Write(ref _mayDiffer, result ? Yes : No);
                return result;
            }
        }

        /// <summary>
        /// For a dictionary's element type, the facts of its values when they are paired by key: keys that come back
        /// equal, and a non-generic dictionary's values as <c>object</c>. Null when the elements are paired in order.
        /// </summary>
        public Facts? DictionaryValue
        {
            get
            {
                if (Volatile.Read(ref _dictionaryValueKnown)) return _dictionaryValue;
                Facts? value = null;
                if (IsKeyValuePair(Type, out var keyType, out var valueType))
                {
                    if (!FactsOf(keyType).MayDiffer) value = FactsOf(valueType);
                }
                else if (Type == typeof(object) || Type == typeof(DictionaryEntry))
                {
                    value = FactsOf(typeof(object));
                }

                _dictionaryValue = value;
                Volatile.Write(ref _dictionaryValueKnown, true);
                return value;
            }
        }

        private bool Compute()
        {
            var type = Nullable.GetUnderlyingType(Type) ?? Type;
            if (IsSimple(type)) return false;
            if (type != Type) return FactsOf(type).MayDiffer;
            // A collection declared with a comparer, or as an interface a dictionary or set may stand behind
            if (_element != null) return Element!.MayDiffer || ComparerOf(type) != null || LooksUp(type);
            if (type.IsInterface || type.IsAbstract || (!type.IsValueType && !type.IsSealed)) return true;
            return Serialized.Any(static m => m.Facts.MayDiffer);
        }
    }

    private sealed class Member(Type type, Func<object, object?> get)
    {
        private Facts? _facts;

        public Facts Facts => _facts ??= FactsOf(type);

        /// <summary>A getter that throws says nothing about the serializer, so that member isn't compared.</summary>
        public bool TryGet(object target, out object? value)
        {
            try
            {
                value = get(target);
                return true;
            }
            catch (Exception)
            {
                value = null;
                return false;
            }
        }
    }

    /// <summary>The getter of a collection's public <c>Comparer</c> or <c>KeyComparer</c>, or null when it has none.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "A check only: a comparer trimming removed isn't compared, so it finds less, never a false difference.")]
    private static Func<object, object?>? ComparerOf(Type type)
    {
        if (type.IsInterface || type.IsAbstract) return null;
        try
        {
            foreach (var name in (ReadOnlySpan<string>)["Comparer", "KeyComparer"])
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property is { GetMethod.IsPublic: true } && property.GetIndexParameters().Length == 0 && property.PropertyType.IsGenericType &&
                    property.PropertyType.GetGenericTypeDefinition() is var definition &&
                    (definition == typeof(IEqualityComparer<>) || definition == typeof(IComparer<>)))
                {
                    return Getter(property);
                }
            }
        }
        catch (Exception)
        {
            // A comparer that can't be read by reflection isn't compared
        }

        return null;
    }

    /// <summary>A dictionary or set interface, behind which a collection with a comparer may stand.</summary>
    private static bool LooksUp(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() is var definition &&
           (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>) ||
            definition == typeof(ISet<>) || definition == typeof(IReadOnlySet<>));

    private static bool IsSimple(Type type)
        => type.IsPrimitive || type.IsEnum || type.IsPointer || type.IsByRefLike ||
           type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
           type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(DateOnly) || type == typeof(TimeOnly) ||
           type == typeof(Half) || type == typeof(Int128) || type == typeof(UInt128) || type == typeof(Uri) || type == typeof(Version);

    private static bool IsKeyValuePair(Type type, out Type key, out Type value)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            var arguments = type.GetGenericArguments();
            (key, value) = (arguments[0], arguments[1]);
            return true;
        }

        (key, value) = (typeof(object), typeof(object));
        return false;
    }

    /// <summary>The element type of a collection, <c>object</c> when it has none or several, or null for a non-collection.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "A check only: an interface trimming removed makes the value look like an object, whose members are compared instead.")]
    private static Type? ElementOf(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return type.GetGenericArguments()[0];
        if (!typeof(IEnumerable).IsAssignableFrom(type)) return null;

        Type? element = null;
        foreach (var @interface in type.GetInterfaces())
        {
            if (!@interface.IsGenericType || @interface.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;
            if (element != null) return typeof(object);
            element = @interface.GetGenericArguments()[0];
        }

        return element ?? typeof(object);
    }

    /// <summary>
    /// The public instance members a serializer writes: properties with a setter or a backing field (an
    /// auto-property), so a computed getter isn't called, and fields; none that is marked as ignored.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "A check only: members trimming removed aren't compared, so it finds less, never a false difference.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "A check only: members trimming removed aren't compared, so it finds less, never a false difference.")]
    private static Member[] SerializedMembers(Type type)
    {
        var members = new List<Member>();
        try
        {
            if (IsKeyValuePair(type, out var key, out var value))
            {
                var keyProperty = type.GetProperty(nameof(KeyValuePair<int, int>.Key))!;
                var valueProperty = type.GetProperty(nameof(KeyValuePair<int, int>.Value))!;
                members.Add(new Member(key, keyProperty.GetValue));
                members.Add(new Member(value, valueProperty.GetValue));
                return members.ToArray();
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true } ||
                    property.PropertyType.IsByRefLike || property.PropertyType.IsPointer || Ignored(property))
                {
                    continue;
                }

                var serialized = property.SetMethod != null ||
                    property.DeclaringType?.GetField($"<{property.Name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance) != null;
                if (serialized) members.Add(new Member(property.PropertyType, Getter(property)));
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType.IsByRefLike || field.FieldType.IsPointer || Ignored(field)) continue;
                members.Add(new Member(field.FieldType, field.GetValue));
            }
        }
        catch (Exception)
        {
            // Members that can't be read by reflection (trimmed, say) aren't compared
        }

        return members.ToArray();
    }

    /// <summary>
    /// A compiled getter where the runtime can make one, several times faster than reflection for a list of objects.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only called where dynamic code is supported; reflection is used otherwise.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Only called where dynamic code is supported; reflection is used otherwise.")]
    private static Func<object, object?> Getter(PropertyInfo property)
    {
        if (RuntimeFeature.IsDynamicCodeSupported && property.DeclaringType is { IsValueType: false, ContainsGenericParameters: false } declaring &&
            property.GetMethod is { } getter)
        {
            try
            {
                return (Func<object, object?>)typeof(RoundTripGraph).GetMethod(nameof(CompiledGetter), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(declaring, property.PropertyType).Invoke(null, [getter])!;
            }
            catch (Exception)
            {
                // Reflection reads it instead
            }
        }

        return property.GetValue;
    }

    private static Func<object, object?> CompiledGetter<TTarget, TValue>(MethodInfo getter) where TTarget : class
    {
        var get = getter.CreateDelegate<Func<TTarget, TValue>>();
        return target => get((TTarget)target);
    }

    private static bool Ignored(MemberInfo member)
    {
        foreach (var attribute in member.GetCustomAttributesData())
        {
            if (attribute.AttributeType == typeof(JsonIgnoreAttribute))
            {
                var condition = attribute.NamedArguments.FirstOrDefault(static a => a.MemberName == nameof(JsonIgnoreAttribute.Condition));
                if (condition.TypedValue.Value is not int value || (JsonIgnoreCondition)value == JsonIgnoreCondition.Always) return true;
                continue;
            }

            // MessagePack's [IgnoreMember], DataContract's [IgnoreDataMember] and [NonSerialized], by name so no
            // serializer package is needed
            if (attribute.AttributeType.Name is "IgnoreMemberAttribute" or "IgnoreDataMemberAttribute" or "NonSerializedAttribute") return true;
        }

        return false;
    }
}
