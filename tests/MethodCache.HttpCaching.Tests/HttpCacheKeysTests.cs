using System.Net.Http.Headers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
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
    public void UriTarget_RetainsItsFrozenEncoding_WhenHeaderFramingChanges()
    {
        Assert.Equal("D567348E39536CD7EA18F8D42522E6E9D1A2E9C6012DD7631F50B792FB475655:M7L6sA55szvnlkwqPQsgxuvRO/0ltkY3OZ+gNQIPscs=:m:GET", HttpCacheKeys.ForUri("api", HttpMethod.Get, new Uri("https://example.test/a?x=1")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    [InlineData(500)]
    [InlineData(600)]
    [InlineData(4096)]
    public void UriFrame_StackAndPooledPathsMatchTheFrozenIncrementalEncoding(int queryLength)
    {
        var uri = new Uri("https://example.test/å?query=" + new string('x', queryLength));
        var address = uri.GetComponents(UriComponents.HttpRequestUrl | UriComponents.UserInfo, UriFormat.UriEscaped);
        using var reference = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = Encoding.UTF8.GetBytes(address);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        reference.AppendData(length); reference.AppendData(bytes);
        var client = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes("api")));
        Assert.Equal(client + ":" + Convert.ToBase64String(reference.GetHashAndReset()) + ":m:GET", HttpCacheKeys.ForUri("api", HttpMethod.Get, uri));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(300)]
    [InlineData(4096)]
    public void VariantFrame_StackAndStreamingMatchTheExactUtf16IncrementalEncoding(int valueLength)
    {
        using var request = Get();
        var value = new string('é', valueLength) + "\ud800";
        request.Headers.TryAddWithoutValidation("X-Tenant", new[] { value, "two" });
        using var reference = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in new[] { "schema", "x-tenant", "1", "2", value, "two" })
        {
            var bytes = new byte[part.Length * 2];
            for (var i = 0; i < part.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), part[i]);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            reference.AppendData(length); reference.AppendData(bytes);
        }
        Assert.Equal("base|" + Convert.ToBase64String(reference.GetHashAndReset()), HttpCacheKeys.ForVariant("base", "schema", request, ["X-Tenant"]));
    }

    [Fact]
    public void ForRequest_IsDeterministic_AndSeparatesHashedClients()
    {
        var key = HttpCacheKeys.ForRequest("api", Get());

        Assert.DoesNotContain("api", key, StringComparison.Ordinal);
        Assert.NotEqual(key, HttpCacheKeys.ForRequest("other", Get()));
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
