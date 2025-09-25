using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Tests;
using Xunit;

namespace MethodCache.HttpCaching.IntegrationTests;

public class VaryHeaderIntegrationTests
{
    [Fact]
    public async Task CacheRespectVaryHeaders_DifferentAcceptHeaders_StoreSeparateEntries()
    {
        var options = new HttpCacheOptions();
        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var jsonResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"data\": \"json\" }", System.Text.Encoding.UTF8, "application/json"),
            Headers =
            {
                Date = DateTimeOffset.UtcNow,
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "Accept" }
            }
        };

        var xmlResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<data>xml</data>", System.Text.Encoding.UTF8, "application/xml"),
            Headers =
            {
                Date = DateTimeOffset.UtcNow,
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "Accept" }
            }
        };

        innerHandler.SetResponses(jsonResponse, xmlResponse);

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response1 = await client.SendAsync(request1);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        var response2 = await client.SendAsync(request2);

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount);

        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();

        Assert.Contains("json", content1);
        Assert.Contains("xml", content2);
    }

    [Fact]
    public async Task CacheRespectVaryHeaders_SameAcceptHeader_ServeFromCache()
    {
        var options = new HttpCacheOptions();
        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"data\": \"json\" }", System.Text.Encoding.UTF8, "application/json"),
            Headers =
            {
                Date = DateTimeOffset.UtcNow,
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "Accept" }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response1 = await client.SendAsync(request1);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response2 = await client.SendAsync(request2);

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount);

        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();

        Assert.Equal(content1, content2);
    }

    [Fact]
    public async Task CacheRespectVaryHeaders_VaryAsterisk_NeverCaches()
    {
        var options = new HttpCacheOptions();
        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("uncacheable", System.Text.Encoding.UTF8, "text/plain"),
            Headers =
            {
                Date = DateTimeOffset.UtcNow,
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "*" }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await client.GetAsync("https://api.example.com/data");
        var response2 = await client.GetAsync("https://api.example.com/data");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount);
    }

    [Fact]
    public async Task CacheRespectVaryHeaders_MultipleVaryHeaders_WorksCorrectly()
    {
        var options = new HttpCacheOptions();
        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"data\": \"json\" }", System.Text.Encoding.UTF8, "application/json"),
            Headers =
            {
                Date = DateTimeOffset.UtcNow,
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "Accept", "Accept-Language" }
            }
        };
        response.Content.Headers.ContentLanguage.Add("en-US");

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request1.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var response1 = await client.SendAsync(request1);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request2.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var response2 = await client.SendAsync(request2);

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount);
    }

    [Fact]
    public async Task CacheIgnoreVary_WhenDisabled_IgnoresVaryHeaders()
    {
        var options = new HttpCacheOptions();
        options.Behavior.RespectVary = false;
        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("content", System.Text.Encoding.UTF8, "text/plain"),
            Headers =
            {
                Date = DateTimeOffset.UtcNow,
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "Accept" }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response1 = await client.SendAsync(request1);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        var response2 = await client.SendAsync(request2);

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount);
    }
}
