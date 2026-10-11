using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpCacheKeysTests
{
    private const string Url = "https://api.example.com/users";

    [Fact]
    public void ForRequest_IsDeterministic_AndPrefixedWithTheClient()
    {
        var key = HttpCacheKeys.ForRequest("api", Get(), new HttpCacheOptions());

        Assert.StartsWith("api:", key);
        Assert.Equal(key, HttpCacheKeys.ForRequest("api", Get(), new HttpCacheOptions()));
    }

    [Fact]
    public void ForRequest_DiffersByClientMethodAndUrl()
    {
        var options = new HttpCacheOptions();
        var keys = new[]
        {
            HttpCacheKeys.ForRequest("a", Get(), options),
            HttpCacheKeys.ForRequest("b", Get(), options),
            HttpCacheKeys.ForRequest("a", new HttpRequestMessage(HttpMethod.Head, Url), options),
            HttpCacheKeys.ForRequest("a", new HttpRequestMessage(HttpMethod.Get, Url + "?page=2"), options)
        };

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void ForRequest_PrivateCache_SeparatesCallers_SharedCacheDoesNot()
    {
        var alice = Get(new AuthenticationHeaderValue("Bearer", "alice"));
        var bob = Get(new AuthenticationHeaderValue("Bearer", "bob"));

        Assert.NotEqual(HttpCacheKeys.ForRequest("a", alice, new HttpCacheOptions()), HttpCacheKeys.ForRequest("a", bob, new HttpCacheOptions()));

        var shared = new HttpCacheOptions { IsSharedCache = true };
        Assert.Equal(HttpCacheKeys.ForRequest("a", alice, shared), HttpCacheKeys.ForRequest("a", bob, shared));
    }

    [Fact]
    public void ForRequest_IncludesKeyHeaders()
    {
        var options = new HttpCacheOptions();
        options.KeyHeaders.Add("X-Tenant");
        var tenantA = Get();
        tenantA.Headers.Add("X-Tenant", "a");
        var tenantB = Get();
        tenantB.Headers.Add("X-Tenant", "b");

        Assert.NotEqual(HttpCacheKeys.ForRequest("a", tenantA, options), HttpCacheKeys.ForRequest("a", tenantB, options));
    }

    [Fact]
    public void ForVariant_StartsWithTheBaseKey_AndDiffersByVaryingHeader()
    {
        var json = Get();
        json.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var xml = Get();
        xml.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        var jsonKey = HttpCacheKeys.ForVariant("base", json, ["Accept"]);
        var xmlKey = HttpCacheKeys.ForVariant("base", xml, ["Accept"]);

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
            HttpCacheKeys.ForVariant("base", request, ["Accept", "Accept-Language"]),
            HttpCacheKeys.ForVariant("base", request, ["accept-language", "ACCEPT"]));
    }

    [Fact]
    public void ForVariant_VaryStar_IsNeverCached()
        => Assert.Null(HttpCacheKeys.ForVariant("base", Get(), ["*"]));

    private static HttpRequestMessage Get(AuthenticationHeaderValue? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Authorization = authorization;
        return request;
    }
}
