using System.Net;
using System.Net.Http.Json;
using MethodCache.WebDemo;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class WebDemoInputTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("79228162514264337593543950335")]
    public async Task UpdatePrice_ValidDecimal_PersistsExactlyAndInvalidatesCache(string value)
    {
        await using var factory = new DemoWebFactory();
        using var client = factory.CreateClient();
        await client.GetFromJsonAsync<Product>("/products/1");
        var price = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        using var response = await client.PutAsJsonAsync("/products/1/price", new { price });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(price, (await client.GetFromJsonAsync<Product>("/products/1"))!.Price);
        Assert.Equal(2, factory.Services.GetRequiredService<ProductCatalog>().Queries);
        Assert.Equal(price, (await factory.Services.GetRequiredService<ProductStore>().GetAsync(1))!.Price);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"price\":null}")]
    [InlineData("{\"price\":-1}")]
    public async Task UpdatePrice_MissingNullOrNegativePrice_DoesNotChangeStoredOrCachedProduct(string json)
    {
        await using var factory = new DemoWebFactory();
        using var client = factory.CreateClient();
        var original = await client.GetFromJsonAsync<Product>("/products/1");
        using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PutAsync("/products/1/price", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(original, await client.GetFromJsonAsync<Product>("/products/1"));
        Assert.Equal(1, factory.Services.GetRequiredService<ProductCatalog>().Queries);
        Assert.Equal(original, await factory.Services.GetRequiredService<ProductStore>().GetAsync(1));
    }

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
