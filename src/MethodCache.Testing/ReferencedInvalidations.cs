using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MethodCache.Testing;

/// <summary>
/// The tags the projects next to the tests invalidate, as MethodCache's generator writes them into each project's
/// assembly (<c>MethodCacheInvalidatedTags.g.cs</c>). The assemblies are read as files, not loaded, so reading them runs
/// none of their code.
/// </summary>
internal static class ReferencedInvalidations
{
    /// <summary>The metadata key, as the generator writes it.</summary>
    internal const string Key = "MethodCache.InvalidatedTags";

    /// <summary>The tag patterns written into the assemblies in <paramref name="directory"/>, with <c>{}</c> for any text.</summary>
    public static IReadOnlyList<string> Read(string directory)
    {
        var tags = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name is "mscorlib" or "netstandard" || name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                using var reader = new PEReader(stream);
                if (!reader.HasMetadata) continue;
                var metadata = reader.GetMetadataReader();
                if (!metadata.IsAssembly) continue;
                foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
                {
                    var attribute = metadata.GetCustomAttribute(handle);
                    if (!IsAssemblyMetadata(metadata, attribute)) continue;
                    var value = attribute.DecodeValue(StringArguments.Instance);
                    if (value.FixedArguments.Length == 2 && value.FixedArguments[0].Value as string == Key && value.FixedArguments[1].Value is string tag)
                        tags.Add(tag);
                }
            }
            catch (BadImageFormatException)
            {
                // A native library or another file that isn't a .NET assembly
            }
        }
        return tags;
    }

    private static bool IsAssemblyMetadata(MetadataReader metadata, CustomAttribute attribute)
    {
        var type = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default,
        };
        return type.Kind switch
        {
            HandleKind.TypeReference => Is(metadata.GetTypeReference((TypeReferenceHandle)type).Namespace, metadata.GetTypeReference((TypeReferenceHandle)type).Name),
            HandleKind.TypeDefinition => Is(metadata.GetTypeDefinition((TypeDefinitionHandle)type).Namespace, metadata.GetTypeDefinition((TypeDefinitionHandle)type).Name),
            _ => false,
        };

        bool Is(StringHandle ns, StringHandle name)
            => metadata.StringComparer.Equals(ns, "System.Reflection") && metadata.StringComparer.Equals(name, "AssemblyMetadataAttribute");
    }

    /// <summary>Decodes the string arguments of AssemblyMetadataAttribute; other types are only named.</summary>
    private sealed class StringArguments : ICustomAttributeTypeProvider<string>
    {
        public static readonly StringArguments Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
