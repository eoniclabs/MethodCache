using System.Net;
using System.Net.Http.Json;
using MethodCache.WebDemo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

public class WebDemoInputTests
{
    [Theory]
    [InlineData(999999, 10, HttpStatusCode.NotFound)]
    [InlineData(999999, -1, HttpStatusCode.BadRequest)]
    public async Task UpdatePrice_InvalidRequest_ReturnsAClientError(int id, decimal price, HttpStatusCode status)
    {
        await using var factory = new DemoWebFactory();
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"/products/{id}/price", new { price });

        Assert.Equal(status, response.StatusCode);
    }
}
