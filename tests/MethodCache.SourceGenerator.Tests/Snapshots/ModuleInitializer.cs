using System.Runtime.CompilerServices;
using VerifyTests;

namespace MethodCache.SourceGenerator.Tests.Snapshots;

public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifySourceGenerators.Initialize();
        // Snapshots are stored next to the tests, one folder for all of them
        Verifier.DerivePathInfo((sourceFile, projectDirectory, type, method) =>
            new PathInfo(Path.Combine(Path.GetDirectoryName(sourceFile)!, "Verified"), type.Name, method.Name));
    }
}
