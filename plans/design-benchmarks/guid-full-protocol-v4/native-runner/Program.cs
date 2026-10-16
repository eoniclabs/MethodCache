using System.Diagnostics;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Testcontainers.Redis;
using Testcontainers.MsSql;

var label = Environment.GetEnvironmentVariable("METHODCACHE_BENCH_LABEL") ?? throw new Exception("Missing owned-resource label");
var receiptPath = Environment.GetEnvironmentVariable("METHODCACHE_BENCH_BACKEND_RECEIPT") ?? throw new Exception("Missing backend receipt path");
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "backend-images.json")));
string Reference(string name, string repository)
{
    var value = manifest.RootElement.GetProperty(name).GetProperty("reference").GetString()!;
    if (!Regex.IsMatch(value, "^" + Regex.Escape(repository) + "@sha256:[0-9a-f]{64}$"))
        throw new Exception("Backend image must use the frozen repository digest");
    return value;
}
var redisImage = Reference("redis", "redis");
var sqlImage = Reference("sqlserver", "mcr.microsoft.com/mssql/server");
await using var redis = new RedisBuilder(redisImage).WithLabel("methodcache-design-bench", label).Build();
await using var sql = new MsSqlBuilder(sqlImage).WithLabel("methodcache-design-bench", label)
    .WithPassword("Aa!" + Guid.NewGuid().ToString("N")).Build();
await Task.WhenAll(redis.StartAsync(), sql.StartAsync()).ConfigureAwait(false);

using var docker = new HttpClient(new SocketsHttpHandler
{
    ConnectCallback = async (_, token) =>
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}) { BaseAddress = new Uri("http://localhost") };

async Task VerifyImages()
{
    // Container-list metadata contains image IDs and labels, without reading environment/connection strings.
    var filter = JsonSerializer.Serialize(new Dictionary<string, string[]> { ["id"] = [redis.Id, sql.Id] });
    var containers = await docker.GetFromJsonAsync<JsonElement>("/containers/json?all=1&filters=" + Uri.EscapeDataString(filter)).ConfigureAwait(false);
    var expected = new Dictionary<string, string>
    {
        [redis.Id] = manifest.RootElement.GetProperty("redis").GetProperty("image_id").GetString()!,
        [sql.Id] = manifest.RootElement.GetProperty("sqlserver").GetProperty("image_id").GetString()!
    };
    ImageValidation.Verify(containers, expected, label);
}

await VerifyImages().ConfigureAwait(false);
var receipt = new Dictionary<string, object?>
{
    ["label"] = label,
    ["images"] = manifest.RootElement.Clone(),
    ["redis_container_id"] = redis.Id,
    ["sql_container_id"] = sql.Id,
    ["verified_before"] = DateTimeOffset.UtcNow,
    ["verified_after"] = null,
    ["child_exit"] = null
};
void WriteReceipt() => File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
WriteReceipt();
if (args.Length < 2) throw new Exception("Supply the runtime working directory and one command");
var info = new ProcessStartInfo(args[1]) { WorkingDirectory = args[0] };
foreach (var argument in args.Skip(2)) info.ArgumentList.Add(argument);
info.Environment["METHODCACHE_REDIS_URL"] = redis.GetConnectionString();
info.Environment["METHODCACHE_SQLSERVER_CONNECTION"] = sql.GetConnectionString();
info.Environment["METHODCACHE_REQUIRE_INFRA_TESTS"] = "true";
using var child = Process.Start(info)!;
await child.WaitForExitAsync().ConfigureAwait(false);
await VerifyImages().ConfigureAwait(false);
receipt["verified_after"] = DateTimeOffset.UtcNow;
receipt["child_exit"] = child.ExitCode;
WriteReceipt();
Console.WriteLine("Verified both actual native container image IDs against their frozen digests; connection strings omitted.");
return child.ExitCode;
