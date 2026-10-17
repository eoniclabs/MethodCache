using System.Text.Json;

internal static class ImageValidation
{
    internal static void Verify(JsonElement containers, IReadOnlyDictionary<string, string> expected, string label)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in containers.EnumerateArray())
        {
            var id = container.GetProperty("Id").GetString()!;
            if (!expected.TryGetValue(id, out var image) || !seen.Add(id) || container.GetProperty("ImageID").GetString() != image ||
                !container.GetProperty("Labels").TryGetProperty("methodcache-design-bench", out var owner) || owner.GetString() != label)
                throw new InvalidOperationException("A native backend does not match its frozen image/owned label");
        }
        if (seen.Count != expected.Count || expected.Count != 2)
            throw new InvalidOperationException("Missing actual native backend image evidence");
    }
}
