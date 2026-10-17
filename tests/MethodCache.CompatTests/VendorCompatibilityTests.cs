using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.CompatTests;

public partial class VendorCompatibilityTests
{
    [Fact]
    public void FrozenGeneratedPublicApi_PreservesEveryDeclaredContractMember()
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Baselines/Contract1FinalGeneratedApi.json")));
        Assert.Matches("^[0-9a-f]{40}$", baseline.RootElement.GetProperty("source_commit").GetString()!);
        var current = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "CurrentGeneratedPublicAPI.txt"))
            .Concat(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "CurrentGeneratedPublicAPI.Shipped.txt")))
            .ToHashSet(StringComparer.Ordinal);
        var frozen = baseline.RootElement.GetProperty("declarations").EnumerateArray().Select(member => member.GetString()!).ToHashSet(StringComparer.Ordinal);
        var generated = current.Where(static member => member.Contains("MethodCache.Generated.", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        Assert.True(frozen.SetEquals(generated), "Generated contract 1 must match its reviewed pre-release freeze.");
    }

    [Fact]
    public void FrozenImages_CoverBoundInlinePartsAndFallback()
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var baselineRoot = Path.Combine(AppContext.BaseDirectory, "Baselines/Contract1Final");
        foreach (var manifestPath in Directory.EnumerateFiles(baselineRoot, "manifest.json", SearchOption.AllDirectories))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (var image in manifest.RootElement.GetProperty("images").EnumerateArray())
            {
                var file = image.GetProperty("file").GetString()!;
                var path = Path.Combine(AppContext.BaseDirectory, file);
                if (!File.Exists(path)) path = Path.Combine(Path.GetDirectoryName(manifestPath)!, file);
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                var metadata = pe.GetMetadataReader();
                foreach (var handle in metadata.MemberReferences)
                {
                    var member = metadata.GetMemberReference(handle);
                    if (member.Parent.Kind != HandleKind.TypeReference) continue;
                    var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                    references.Add($"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}.{metadata.GetString(member.Name)}");
                }
            }
        }

        Assert.Contains("MethodCache.Generated.KeyBuilder..ctor", references);
        Assert.Contains("MethodCache.Generated.KeyBuilder.AppendPart", references);
        Assert.DoesNotContain("MethodCache.Generated.MethodCall.get_KeyPrefix", references);
        Assert.DoesNotContain("MethodCache.Generated.MethodCall.StartKey", references);
        Assert.DoesNotContain("MethodCache.Generated.MethodCall.BindKey", references);
        Assert.DoesNotContain("MethodCache.Generated.MethodCall.BindTextKey", references);
        Assert.Contains("MethodCache.Generated.MethodCall.TryGetValue", references);
        Assert.Contains("MethodCache.Generated.MethodCall.TryGetTask", references);
        Assert.Contains("MethodCache.Generated.MethodCall.TryGetValueTask", references);
    }

}
