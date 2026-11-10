namespace MethodCache;

/// <summary>The effective cache policy of one method.</summary>
public sealed record CachedMethodInfo
{
    /// <summary>
    /// The method id, such as <c>[MyApp]MyApp.IUserService.GetUserAsync</c>; an overloaded method adds its parameter
    /// types, such as <c>[MyApp]MyApp.IUserService.Find(int)</c>. <see cref="IMethodCacheKeys.MethodId{TService}"/>
    /// returns the id of a call.
    /// </summary>
    public required string MethodId { get; init; }
    /// <summary>
    /// What the method's sources set, resolved from its attribute, startup code, configuration and runtime overrides:
    /// a setting no source sets is null here. <see cref="Lifetime"/> and <see cref="Storage"/> show what applies, with
    /// the defaults filled in.
    /// </summary>
    public required CacheEntryOptions Options { get; init; }
    /// <summary>
    /// Where each setting with an effective value comes from, keyed by its <see cref="CacheEntryOptions"/> property name,
    /// such as <c>Duration</c> or <c>FailSafeTimeout</c>. A setting no source sets is left out: it has the library's
    /// default, which <see cref="Lifetime"/> shows.
    /// </summary>
    public IReadOnlyDictionary<string, CacheSettingSource> SettingSources { get; init; } = new Dictionary<string, CacheSettingSource>();
    /// <summary>
    /// The settings that one of the method's own sources sets and a higher one of them replaces, with the sources
    /// replaced, highest first: such as <c>Duration</c> with <c>Attribute</c> when startup code sets a <c>DurationFor</c>
    /// for a method whose <c>[Cache]</c> sets a <c>Duration</c>. <see cref="SettingSources"/> names the source that
    /// applies. A default from <c>Defaults</c> or a group that a method's own setting replaces isn't listed. Empty when
    /// no setting of the method is set twice.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CacheSettingSource>> ReplacedSources { get; init; } = new Dictionary<string, IReadOnlyList<CacheSettingSource>>();
    /// <summary>
    /// How long the method's entries live on this instance: what <see cref="Options"/> comes to, with the defaults
    /// filled in and the cache-wide limits applied.
    /// </summary>
    public CacheLifetimeInfo Lifetime { get; init; } = new();
    /// <summary>Where the method's values are kept on this instance, and why only in memory when they are.</summary>
    public CacheStorageInfo Storage { get; init; } = new();
    /// <summary>
    /// The parameters whose values make up the method's keys, in order; a <c>CancellationToken</c> isn't part of a key.
    /// <see cref="CacheEntryOptions.Version"/> is part of every key too.
    /// </summary>
    public IReadOnlyList<string> KeyParameters { get; init; } = [];
    /// <summary>
    /// Result types whose values this instance kept in memory only although the method shares its values, and why,
    /// such as a type the serializer doesn't read back unchanged. Empty when every value was shared.
    /// </summary>
    public IReadOnlyList<NotSharedResultInfo> NotShared { get; init; } = [];
}

/// <summary>How long a method's entries live, with defaults filled in.</summary>
public sealed record CacheLifetimeInfo
{
    /// <summary>
    /// The longest an entry lives in memory: the method's <c>Duration</c>, from <c>Defaults</c> or its group, else the
    /// default of five minutes; for a sliding window without a <c>Duration</c>, <see cref="MaxExpiration"/>. Null when
    /// <c>DurationFor</c> gives each result its own duration.
    /// </summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>The sliding window: an entry nobody reads for this long expires. Null without one, or when <c>SlidingFor</c> gives each result its own.</summary>
    public TimeSpan? SlidingExpiration { get; init; }
    /// <summary>Whether <c>DurationFor</c> gives each result its own duration, up to <see cref="MaxExpiration"/>.</summary>
    public bool DurationFromResult { get; init; }
    /// <summary>Whether <c>SlidingFor</c> gives each result its own sliding window.</summary>
    public bool SlidingFromResult { get; init; }
    /// <summary>
    /// The longest any entry lives: <c>MethodCacheMemoryOptions.MaxExpiration</c>, a limit for the whole cache (7 days
    /// unless it is set with <c>mc.Memory(o => o.MaxExpiration = ...)</c>), not a setting of the method.
    /// </summary>
    public TimeSpan MaxExpiration { get; init; }
    /// <summary>
    /// How long the copy in the distributed cache lives from when it was stored: the <see cref="Duration"/>, at most
    /// one sliding window, and at most <c>TagRetention</c> less <c>ClockSkewTolerance</c>. Null when values aren't
    /// stored there, or when the result gives its own duration or window.
    /// </summary>
    public TimeSpan? Distributed { get; init; }
}

/// <summary>Where a method's values are kept.</summary>
public sealed record CacheStorageInfo
{
    /// <summary>Whether the method's values are stored in the distributed cache as well as in memory.</summary>
    public bool Distributed { get; init; }
    /// <summary>The distributed cache, such as <c>Redis</c>, <c>SqlServer</c> or <c>PostgreSql</c>; null without one.</summary>
    public string? Store { get; init; }
    /// <summary>Why the method's values stay in memory only; null when they are stored in the distributed cache.</summary>
    public string? MemoryOnlyReason { get; init; }
    /// <summary>
    /// Whether other instances with the same configuration read the values this instance stores in the distributed
    /// cache. False when the values stay in memory, and while a runtime override that only this instance applies gives
    /// it a namespace of its own there.
    /// </summary>
    public bool Shared { get; init; }
}

/// <summary>Values of one result type that an instance kept in memory only instead of sharing them.</summary>
public sealed record NotSharedResultInfo
{
    /// <summary>The full name of the values' type.</summary>
    public required string Type { get; init; }
    /// <summary>The full name of the type the method declares it returns.</summary>
    public required string DeclaredType { get; init; }
    /// <summary>Why the values aren't shared.</summary>
    public required string Reason { get; init; }
    /// <summary>When the first such value was kept in memory.</summary>
    public DateTimeOffset Since { get; init; }
    /// <summary>How many values were kept in memory instead of being shared.</summary>
    public long Count { get; init; }
}
