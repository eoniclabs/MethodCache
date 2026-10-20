namespace MethodCache.Core.Configuration.Surfaces.ConfigFile;

/// <summary>
/// The error for a configuration value MethodCache can't use. Its message quotes the value, for the developer who
/// sees startup fail; a rejected reload is logged with the setting's path and the reason only, since the value may
/// be anything the configuration holds. The exception stays an <see cref="InvalidOperationException"/>, and carries
/// the path and the reason in <see cref="Exception.Data"/>.
/// </summary>
internal static class ConfigurationValueError
{
    private const string PathKey = "MethodCache.ConfigurationPath";
    private const string ReasonKey = "MethodCache.Reason";

    /// <param name="path">The setting's path in the configuration, such as <c>MethodCache:Defaults:Duration</c>.</param>
    /// <param name="reason">What is wrong with the value, without the value, such as <c>is not a positive time span</c>.</param>
    /// <param name="message">The full message, which may quote the value.</param>
    internal static InvalidOperationException Create(string path, string reason, string message)
    {
        var error = new InvalidOperationException(message);
        error.Data[PathKey] = path;
        error.Data[ReasonKey] = reason;
        return error;
    }

    /// <summary>The path and the reason of an error from <see cref="Create"/>.</summary>
    internal static bool TryGet(Exception error, out string path, out string reason)
    {
        path = error.Data[PathKey] as string ?? string.Empty;
        reason = error.Data[ReasonKey] as string ?? string.Empty;
        return path.Length > 0 && reason.Length > 0;
    }
}
