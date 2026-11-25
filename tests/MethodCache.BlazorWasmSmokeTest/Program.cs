using MethodCache.BlazorWasmSmokeTest;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Time.Testing;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Smoke>("#app");

var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
builder.Services.AddSingleton(time);
builder.Services.AddSingleton<TimeProvider>(time);
builder.Services.AddSingleton<Loads>();
builder.Services.AddScoped<IProducts, Products>();
builder.Services.AddMethodCache();

await builder.Build().RunAsync();
