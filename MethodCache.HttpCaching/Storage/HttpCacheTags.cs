using System.Net;

namespace MethodCache.HttpCaching.Storage;

public static class HttpCacheTags
{
    public const string AllEntries = "http-cache:all";

    public static string ForMethod(string method) => $"http-cache:method:{method.ToUpperInvariant()}";
    public static string ForUriPattern(string pattern) => $"http-cache:uri:{pattern}";
    public static string ForHost(string host) => $"http-cache:host:{host}";
    public static string ForPath(string path) => $"http-cache:path:{path}";
    public static string ForParentPath(string path) => $"http-cache:parent:{path}";
    public static string ForContentType(string contentType) => $"http-cache:content-type:{contentType}";
    public static string ForStatus(HttpStatusCode statusCode) => $"http-cache:status:{(int)statusCode}";
}
