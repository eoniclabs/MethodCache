using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpCacheKeysTests
{
    private const string Url = "https://api.example.com/users";

    [Theory]
    [InlineData("Cookie")]
    [InlineData("X-Tenant")]
    public void ForVariant_PreservesHeaderValueBoundaries(string header)
    {
        using var multiple = Get();
        using var single = Get();
        multiple.Headers.TryAddWithoutValidation(header, new[] { "session=A", "other=B" });
        single.Headers.TryAddWithoutValidation(header, "session=A,other=B");

        Assert.NotEqual(HttpCacheKeys.ForVariant("base", "g", multiple, [header]),
            HttpCacheKeys.ForVariant("base", "g", single, [header]));
    }

    [Fact]
    public void ForRequest_IsDeterministic_AndPrefixedWithTheClient()
    {
        var key = HttpCacheKeys.ForRequest("api", Get());

        Assert.StartsWith("api:", key);
        Assert.Equal(key, HttpCacheKeys.ForRequest("api", Get()));
    }

    [Fact]
    public void ForRequest_DiffersByClientMethodAndUrl()
    {
        var keys = new[]
        {
            HttpCacheKeys.ForRequest("a", Get()),
            HttpCacheKeys.ForRequest("b", Get()),
            HttpCacheKeys.ForRequest("a", new HttpRequestMessage(HttpMethod.Head, Url)),
            HttpCacheKeys.ForRequest("a", new HttpRequestMessage(HttpMethod.Get, Url + "?page=2"))
        };

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void ForRequest_IsTheSameForEveryCaller_SoAnInvalidationReachesThemAll()
    {
        var alice = Get(new AuthenticationHeaderValue("Bearer", "alice"));
        alice.Headers.Add("X-Tenant", "a");
        var bob = Get(new AuthenticationHeaderValue("Bearer", "bob"));

        Assert.Equal(HttpCacheKeys.ForRequest("a", alice), HttpCacheKeys.ForRequest("a", bob));
    }

    [Fact]
    public void SelectingHeaders_PrivateCache_SeparatesCallers_SharedCacheDoesNot()
    {
        Assert.Equal(["Authorization", "Cookie"], HttpCacheKeys.SelectingHeaders(new HttpCacheOptions(), null));
        Assert.Empty(HttpCacheKeys.SelectingHeaders(new HttpCacheOptions { IsSharedCache = true }, null));

        var alice = Get(new AuthenticationHeaderValue("Bearer", "alice"));
        var bob = Get(new AuthenticationHeaderValue("Bearer", "bob"));
        Assert.NotEqual(HttpCacheKeys.ForVariant("base", "g", alice, ["Authorization"]), HttpCacheKeys.ForVariant("base", "g", bob, ["Authorization"]));
    }

    [Fact]
    public void SelectingHeaders_CombineKeyHeadersAndVary()
    {
        var options = new HttpCacheOptions { IsSharedCache = true };
        options.KeyHeaders.Add("X-Tenant");

        Assert.Equal(["Accept-Language", "X-Tenant"], HttpCacheKeys.SelectingHeaders(options, ["Accept-Language", "x-tenant"]));
    }

    [Fact]
    public void ForVariant_DiffersByGeneration()
    {
        Assert.NotEqual(HttpCacheKeys.ForVariant("base", "1", Get(), ["Accept"]), HttpCacheKeys.ForVariant("base", "2", Get(), ["Accept"]));
    }

    [Fact]
    public void ForVariant_StartsWithTheBaseKey_AndDiffersByVaryingHeader()
    {
        var json = Get();
        json.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var xml = Get();
        xml.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        var jsonKey = HttpCacheKeys.ForVariant("base", "g", json, ["Accept"]);
        var xmlKey = HttpCacheKeys.ForVariant("base", "g", xml, ["Accept"]);

        Assert.StartsWith("base|", jsonKey);
        Assert.NotEqual(jsonKey, xmlKey);
    }

    [Fact]
    public void ForVariant_IgnoresHeaderNameCaseAndOrder()
    {
        var request = Get();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("sv"));

        Assert.Equal(
            HttpCacheKeys.ForVariant("base", "g", request, ["Accept", "Accept-Language"]),
            HttpCacheKeys.ForVariant("base", "g", request, ["accept-language", "ACCEPT"]));
    }

    [Fact]
    public void ForVariant_VaryStar_IsNeverCached()
        => Assert.Null(HttpCacheKeys.ForVariant("base", "g", Get(), ["*"]));

    private static HttpRequestMessage Get(AuthenticationHeaderValue? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Authorization = authorization;
        return request;
    }
}
