using System.Text.Json.Serialization;

namespace MethodCache;

/// <summary>Where the effective value of a cache setting comes from, as <see cref="CachedMethodInfo.SettingSources"/> reports it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CacheSettingSource>))]
public enum CacheSettingSource
{
    /// <summary>The method's <c>[Cache]</c> attribute, or <c>[assembly: CacheInterface]</c>.</summary>
    Attribute,
    /// <summary>Startup code, such as <c>mc.Defaults(...)</c>, <c>mc.Group(...)</c> or <c>mc.Method(...)</c>.</summary>
    Code,
    /// <summary>A configuration section such as <c>"MethodCache"</c> in appsettings.json.</summary>
    Configuration,
    /// <summary>An override applied while the app runs, through <see cref="IRuntimeCacheConfigurator"/>.</summary>
    RuntimeOverride
}
