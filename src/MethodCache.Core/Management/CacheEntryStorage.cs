using System.Text.Json.Serialization;

namespace MethodCache;

/// <summary>The type of cached representation described by an administrator entry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CacheEntryKind>))]
public enum CacheEntryKind
{
    /// <summary>A method result.</summary>
    Method,
    /// <summary>An HTTP representation.</summary>
    Http
}

/// <summary>Where this container obtained an administrator entry description.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CacheEntryStorage>))]
public enum CacheEntryStorage
{
    /// <summary>The value is resident in this container's memory.</summary>
    LocalMemory,
    /// <summary>A bounded historical observation on this node; not verified backend presence or a global inventory.</summary>
    ObservedDistributed
}
