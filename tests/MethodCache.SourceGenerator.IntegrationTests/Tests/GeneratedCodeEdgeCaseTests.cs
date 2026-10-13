using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Interface shapes that once produced generated code that did not compile: each one is compiled through the
/// generator, and the runtime cases are loaded and run through AddMethodCache().
/// </summary>
public class GeneratedCodeEdgeCaseTests
{
    private readonly ITestOutputHelper _out;
    public GeneratedCodeEdgeCaseTests(ITestOutputHelper output) => _out = output;

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Latest);

    private static List<MetadataReference> Refs()
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var list = tpa.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        foreach (var t in new[] { typeof(MethodCache.CacheAttribute), typeof(MethodCache.Policies.CachePolicy) })
        {
            if (!tpa.Contains(t.Assembly.Location)) list.Add(MetadataReference.CreateFromFile(t.Assembly.Location));
        }
        return list;
    }

    private static readonly List<MetadataReference> References = Refs();

    public sealed record Result(List<Diagnostic> Errors, List<Diagnostic> GeneratorDiagnostics, Dictionary<string, string> Sources, Assembly? Assembly, GeneratorDriver Driver, Compilation Input);

    private Result Run(string source, string name, bool load = false)
    {
        var compilation = CSharpCompilation.Create(name + "_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, Parse) }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MethodCacheGenerator().AsSourceGenerator() }, parseOptions: Parse,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var genDiags);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var sources = driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources).ToDictionary(s => s.HintName, s => s.SourceText.ToString());
        foreach (var d in genDiags) _out.WriteLine("GEN: " + d);
        foreach (var d in errors.Take(15)) _out.WriteLine("ERR: " + d);
        Assembly? asm = null;
        if (load && errors.Count == 0)
        {
            using var ms = new MemoryStream();
            var emit = output.Emit(ms);
            Assert.True(emit.Success);
            asm = Assembly.Load(ms.ToArray());
        }
        return new Result(errors, genDiags.ToList(), sources, asm, driver, compilation);
    }

    private const string Usings = @"
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
";

    private static void AssertCompiles(Result r) => Assert.True(r.Errors.Count == 0 && !r.GeneratorDiagnostics.Any(d => d.Id == "CS8785"),
        string.Join("\n", r.Errors.Concat(r.GeneratorDiagnostics).Take(10)));

    // ---------- generated code compiles ----------

    [Fact]
    public void CancellationTokenDefault_Compiles()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache] Task<string> Get(int id, CancellationToken ct = default);
}}", nameof(CancellationTokenDefault_Compiles));
        AssertCompiles(r);
    }

    [Fact]
    public void PassthroughWithCancellationTokenDefault_Compiles()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache] Task<string> Get(int id);
  Task Save(int id, CancellationToken cancellationToken = default);
}}", nameof(PassthroughWithCancellationTokenDefault_Compiles));
        AssertCompiles(r);
    }

    [Fact]
    public void DefaultValues_Compile()
    {
        var r = Run(Usings + @"namespace App {
public enum Color { Red = 1, Blue = 2 }
public interface ISvc {
  [Cache] Task<string> Get(int id, Color color = Color.Blue, float f = 1.5f, decimal d = 2.5m, string s = ""a\""b"", char c = '\'');
}}", nameof(DefaultValues_Compile));
        AssertCompiles(r);
    }

    [Fact]
    public void KeywordParameterName_Compiles()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache] Task<string> Get(int @class);
  Task<string> Other(string @event);
}}", nameof(KeywordParameterName_Compiles));
        AssertCompiles(r);
    }

    [Fact]
    public void ParameterNamedLikeGeneratedLocal_Compiles()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache] Task<string> Get(int cacheSnapshot);
  [CacheInvalidate(Tags = new[] { ""x"" })] Task<int> Save(int result);
}}", nameof(ParameterNamedLikeGeneratedLocal_Compiles));
        AssertCompiles(r);
    }

    [Fact]
    public void SameInterfaceNameInTwoNamespaces_Generates()
    {
        var r = Run(Usings + @"
namespace App.Users { public interface IService { [Cache] Task<string> Get(int id); } }
namespace App.Orders { public interface IService { [Cache] Task<string> Get(int id); } }
", nameof(SameInterfaceNameInTwoNamespaces_Generates));
        AssertCompiles(r);
        Assert.Equal(2, r.Sources.Keys.Count(k => k.Contains("IServiceDecorator")));
    }

    [Fact]
    public void GenericInterfaceConstraintFromOtherNamespace_Compiles()
    {
        var r = Run(Usings + @"
namespace App.Model { public interface IEntity { int Id { get; } } }
namespace App.Data {
  using App.Model;
  public interface IRepo<T> where T : IEntity { [Cache] Task<T> Get(int id); }
}", nameof(GenericInterfaceConstraintFromOtherNamespace_Compiles));
        AssertCompiles(r);
    }

    [Fact]
    public void InvalidateReturningNonGenericValueTask_Compiles()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache(Tags = new[] { ""users"" })] Task<string> Get(int id);
  [CacheInvalidate(Tags = new[] { ""users"" })] ValueTask Update(int id);
}}", nameof(InvalidateReturningNonGenericValueTask_Compiles));
        AssertCompiles(r);
    }

    [Fact]
    public void TagWithBackslash_Compiles()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache(Tags = new[] { ""path:C:\\data"" })] Task<string> Get(int id);
  [CacheInvalidate(Tags = new[] { ""path:C:\\data"" })] Task Update(int id);
}}", nameof(TagWithBackslash_Compiles));
        AssertCompiles(r);
    }

    [Fact]
    public void InternalInterface_Compiles()
    {
        var r = Run(Usings + @"namespace App {
internal interface ISvc { [Cache] Task<string> Get(int id); }
}", nameof(InternalInterface_Compiles));
        AssertCompiles(r);
    }

    [Fact]
    public void SameMethodInTwoBaseInterfaces_Compiles()
    {
        var r = Run(Usings + @"namespace App {
public interface IReader { [Cache] Task<string> Get(int id); }
public interface ILegacyReader { Task<string> Get(int id); }
public interface ISvc : IReader, ILegacyReader { [Cache] Task<int> Count(); }
}", nameof(SameMethodInTwoBaseInterfaces_Compiles));
        AssertCompiles(r);
    }

    [Fact]
    public void StaticAbstractMember_DiagnosedOrCompiles()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc { static abstract string Name { get; } [Cache] Task<string> Get(int id); }
}", nameof(StaticAbstractMember_DiagnosedOrCompiles));
        _out.WriteLine(string.Join(",", r.GeneratorDiagnostics.Select(d => d.Id)));
        Assert.True(r.Errors.Count == 0 || r.GeneratorDiagnostics.Any(d => d.Id.StartsWith("MCG")),
            "only C# errors in generated code: " + string.Join("\n", r.Errors.Take(3)));
    }

    [Fact]
    public void RefReturnAndRefReadonlyParam_Compile()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc { [Cache] Task<string> Get(int id); ref int Slot(); int Peek(ref readonly int x); }
}", nameof(RefReturnAndRefReadonlyParam_Compile));
        AssertCompiles(r);
    }

    // ---------- runtime behaviour ----------

    private static object Resolve(Assembly asm, string iface, out IServiceProvider sp)
    {
        var services = new ServiceCollection();
        asm.GetType("App.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });
        sp = services.BuildServiceProvider();
        return sp.GetRequiredService(asm.GetType(iface)!);
    }

    [Fact]
    public async Task TagInvalidation_RemovesCachedEntries()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache(Tags = new[] { ""t"" })] Task<string> Describe(int id);
  [CacheInvalidate(Tags = new[] { ""t"" })] Task Reset();
}
public class Svc : ISvc { public int Calls; public Task<string> Describe(int id) { Calls++; return Task.FromResult(""x""); } public Task Reset() => Task.CompletedTask; }
public static class Startup { public static void Configure(IServiceCollection s) { s.AddSingleton<Svc>(); s.AddSingleton<ISvc>(sp => sp.GetRequiredService<Svc>()); s.AddMethodCache(); } }
public static class Probe { public static async Task<int> Go(ISvc s, Svc impl) { await s.Describe(1); await s.Describe(1); await s.Reset(); await s.Describe(1); return impl.Calls; } }
}", nameof(TagInvalidation_RemovesCachedEntries), load: true);
        AssertCompiles(r);
        var svc = Resolve(r.Assembly!, "App.ISvc", out var sp);
        var impl = sp.GetRequiredService(r.Assembly!.GetType("App.Svc")!);
        var calls = await (Task<int>)r.Assembly!.GetType("App.Probe")!.GetMethod("Go")!.Invoke(null, new[] { svc, impl })!;
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CallerCancellingASharedLoad_DoesNotFailTheOtherCaller()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc { [Cache] Task<string> Get(int id, CancellationToken ct); }
public class Svc : ISvc {
  public int Calls; public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
  public async Task<string> Get(int id, CancellationToken ct) { Interlocked.Increment(ref Calls); Started.TrySetResult(); await Task.Delay(100, ct); return ""v"" + id; }
}
public static class Startup { public static void Configure(IServiceCollection s) { s.AddSingleton<Svc>(); s.AddSingleton<ISvc>(sp => sp.GetRequiredService<Svc>()); s.AddMethodCache(); } }
public static class Probe { public static async Task<string> Go(ISvc s, Svc impl) {
  using var cts = new CancellationTokenSource();
  var a = s.Get(1, cts.Token); await impl.Started.Task;
  var b = s.Get(1, CancellationToken.None);
  cts.Cancel();
  var aResult = ""value""; try { await a; } catch (OperationCanceledException) { aResult = ""cancelled""; }
  return aResult + ""|"" + await b + ""|"" + impl.Calls;
} }
}", nameof(CallerCancellingASharedLoad_DoesNotFailTheOtherCaller), load: true);
        AssertCompiles(r);
        Assert.Contains(r.Sources.Values, source => source.Contains("__policy, __mcArgument1);"));
        var svc = Resolve(r.Assembly!, "App.ISvc", out var sp);
        var impl = sp.GetRequiredService(r.Assembly!.GetType("App.Svc")!);
        var text = await (Task<string>)r.Assembly!.GetType("App.Probe")!.GetMethod("Go")!.Invoke(null, new[] { svc, impl })!;
        // A gave up; B never did, so it loads again instead of failing with A's cancellation
        Assert.Equal("cancelled|v1|2", text);
    }

    [Fact]
    public async Task EdgeCaseShapes_CacheAndInvalidateAtRuntime()
    {
        var r = Run(Usings + @"namespace App {
public enum Color { Red = 1, Blue = 2 }
internal interface ISvc {
  [Cache(Tags = new[] { ""path:C:\\data"", ""item:{class}"" })] Task<string> Get(int @class, Color color = Color.Blue, CancellationToken ct = default);
  [CacheInvalidate(Tags = new[] { ""item:{class}"" })] ValueTask Update(int @class);
  [CacheInvalidate(Tags = new[] { ""path:C:\\data"" })] Task<int> Save(int result);
}
internal class Svc : ISvc {
  public int Calls;
  public Task<string> Get(int @class, Color color = Color.Blue, CancellationToken ct = default) { Calls++; return Task.FromResult(@class + "":"" + color); }
  public ValueTask Update(int @class) => default;
  public Task<int> Save(int result) => Task.FromResult(result);
}
public static class Startup { public static void Configure(IServiceCollection s) { s.AddSingleton<Svc>(); s.AddSingleton<ISvc>(sp => sp.GetRequiredService<Svc>()); s.AddMethodCache(); } }
public static class Probe { public static async Task<string> Go(IServiceProvider sp) {
  var s = sp.GetRequiredService<ISvc>(); var impl = sp.GetRequiredService<Svc>();
  var first = await s.Get(7); await s.Get(7);
  await s.Update(7); await s.Get(7);
  var saved = await s.Save(5); await s.Get(7);
  return first + ""|"" + impl.Calls + ""|"" + saved + ""|"" + s.GetType().Name;
} }
}", nameof(EdgeCaseShapes_CacheAndInvalidateAtRuntime), load: true);
        AssertCompiles(r);
        var services = new ServiceCollection();
        r.Assembly!.GetType("App.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });
        using var sp = services.BuildServiceProvider();
        var text = await (Task<string>)r.Assembly!.GetType("App.Probe")!.GetMethod("Go")!.Invoke(null, new object[] { sp })!;
        // One load, one after each invalidation
        Assert.Equal("7:Blue|3|5|ISvcDecorator", text);
    }

    [Fact]
    public async Task GenericMethod_KeysIncludeTypeArgument()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc { [Cache] Task<string> Describe<T>(int id); }
public class Svc : ISvc { public Task<string> Describe<T>(int id) => Task.FromResult(typeof(T).Name + "":"" + id); }
public static class Startup { public static void Configure(IServiceCollection s) { s.AddSingleton<ISvc, Svc>(); s.AddMethodCache(); } }
public static class Probe { public static async Task<string> Go(ISvc s) => await s.Describe<int>(1) + ""|"" + await s.Describe<string>(1); }
}", nameof(GenericMethod_KeysIncludeTypeArgument), load: true);
        AssertCompiles(r);
        var svc = Resolve(r.Assembly!, "App.ISvc", out _);
        Assert.EndsWith("Decorator", svc.GetType().Name);
        var text = await (Task<string>)r.Assembly!.GetType("App.Probe")!.GetMethod("Go")!.Invoke(null, new[] { svc })!;
        _out.WriteLine(text);
        Assert.Equal("Int32:1|String:1", text);
    }

    [Fact]
    public async Task GenericMethod_TagsFromAttributeAreApplied()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache(Tags = new[] { ""t"" })] Task<string> Describe<T>(int id);
  [CacheInvalidate(Tags = new[] { ""t"" })] Task Reset();
}
public class Svc : ISvc { public int Calls; public Task<string> Describe<T>(int id) { Calls++; return Task.FromResult(""x""); } public Task Reset() => Task.CompletedTask; }
public static class Startup { public static void Configure(IServiceCollection s) { s.AddSingleton<Svc>(); s.AddSingleton<ISvc>(sp => sp.GetRequiredService<Svc>()); s.AddMethodCache(); } }
public static class Probe { public static async Task<int> Go(ISvc s, Svc impl) { await s.Describe<int>(1); await s.Describe<int>(1); await s.Reset(); await s.Describe<int>(1); return impl.Calls; } }
}", nameof(GenericMethod_TagsFromAttributeAreApplied), load: true);
        AssertCompiles(r);
        var svc = Resolve(r.Assembly!, "App.ISvc", out var sp);
        var impl = sp.GetRequiredService(r.Assembly!.GetType("App.Svc")!);
        var calls = await (Task<int>)r.Assembly!.GetType("App.Probe")!.GetMethod("Go")!.Invoke(null, new[] { svc, impl })!;
        _out.WriteLine("calls=" + calls + " policies:\n" + (r.Sources.TryGetValue("GeneratedPolicyRegistrations.g.cs", out var p) ? p : "<none>"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DerivedInterfaceWithoutOwnAttributes_IsDecorated()
    {
        var r = Run(Usings + @"namespace App {
public interface IRepo { [Cache] Task<string> Get(int id); }
public interface IUserRepo : IRepo { }
public class UserRepo : IUserRepo { public Task<string> Get(int id) => Task.FromResult(""u"" + id); }
public static class Startup { public static void Configure(IServiceCollection s) { s.AddSingleton<IUserRepo, UserRepo>(); s.AddMethodCache(); } }
}", nameof(DerivedInterfaceWithoutOwnAttributes_IsDecorated), load: true);
        AssertCompiles(r);
        var svc = Resolve(r.Assembly!, "App.IUserRepo", out _);
        _out.WriteLine(svc.GetType().FullName);
        Assert.EndsWith("Decorator", svc.GetType().Name);
    }

    [Fact]
    public void LazySequences_AreReportedAndNotCached()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache] IEnumerable<int> Numbers(int n);
  [Cache] IAsyncEnumerable<int> Stream(int n);
  [Cache] Task<IEnumerable<int>> Loaded(int n);
}}", nameof(LazySequences_AreReportedAndNotCached));
        Assert.Equal(2, r.GeneratorDiagnostics.Count(d => d.Id == "MCG014"));
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void UnknownParameterInCacheTag_IsReported()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc { [Cache(Tags = new[] { ""user:{userid}"", ""order:{orderId}"" })] Task<string> Get(int userId); }
}", nameof(UnknownParameterInCacheTag_IsReported));
        AssertCompiles(r);
        var warning = Assert.Single(r.GeneratorDiagnostics, d => d.Id == "MCG009");
        Assert.Contains("orderId", warning.GetMessage());
    }

    [Fact]
    public void TagPlaceholderThatIsNotAParameterName_IsReported()
    {
        var r = Run(Usings + @"namespace App {
public record Product(int Id);
public interface ISvc {
  [Cache(Tags = new[] { ""product:{product.Id}"", ""user:{ userId }"", ""day:{day:yyyy-MM-dd}"", ""ok:{userId}"", ""json:{}"", ""range:{-1}"", ""unicode:{π.Id}"" })]
  Task<string> Get(Product product, int userId, DateTime day);
  [CacheInvalidate(Tags = new[] { ""product:{product?.Id}"" })] Task Update(Product product);
}}", nameof(TagPlaceholderThatIsNotAParameterName_IsReported));
        AssertCompiles(r);
        var placeholders = r.GeneratorDiagnostics.Where(d => d.Id == "MCG015").Select(d => d.GetMessage()).ToList();
        Assert.Equal(5, placeholders.Count);
        Assert.Contains(placeholders, m => m.Contains("'{π.Id}'"));
        Assert.Contains(placeholders, m => m.Contains("'{product.Id}'"));
        Assert.Contains(placeholders, m => m.Contains("'{ userId }'"));
        Assert.Contains(placeholders, m => m.Contains("'{day:yyyy-MM-dd}'"));
        Assert.Contains(placeholders, m => m.Contains("'{product?.Id}'"));
        Assert.DoesNotContain(r.GeneratorDiagnostics, d => d.Id == "MCG009");
    }

    [Fact]
    public async Task Overloads_AreCachedSeparatelyWithTheirOwnPolicies()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache(Tags = new[] { ""ints"" })] Task<string> Get(int id);
  [Cache(Tags = new[] { ""strings"" })] Task<string> Get(string id);
  [CacheInvalidate(Tags = new[] { ""ints"" })] Task ResetInts();
}
public class Svc : ISvc {
  public int Calls;
  public Task<string> Get(int id) { Calls++; return Task.FromResult(""int:"" + id); }
  public Task<string> Get(string id) { Calls++; return Task.FromResult(""string:"" + id); }
  public Task ResetInts() => Task.CompletedTask;
}
public static class Startup { public static void Configure(IServiceCollection s) { s.AddSingleton<Svc>(); s.AddSingleton<ISvc>(sp => sp.GetRequiredService<Svc>()); s.AddMethodCache(); } }
public static class Probe { public static async Task<string> Go(ISvc s, Svc impl) {
  var a = await s.Get(1); var b = await s.Get(""1""); await s.Get(1); await s.Get(""1"");
  var loads = impl.Calls;
  await s.ResetInts(); await s.Get(1); await s.Get(""1"");
  return a + ""|"" + b + ""|"" + loads + ""|"" + impl.Calls;
} }
}", nameof(Overloads_AreCachedSeparatelyWithTheirOwnPolicies), load: true);
        AssertCompiles(r);
        var svc = Resolve(r.Assembly!, "App.ISvc", out var sp);
        var impl = sp.GetRequiredService(r.Assembly!.GetType("App.Svc")!);
        var text = await (Task<string>)r.Assembly!.GetType("App.Probe")!.GetMethod("Go")!.Invoke(null, new[] { svc, impl })!;
        // Separate entries; invalidating "ints" reloads only Get(int)
        Assert.Equal("int:1|string:1|2|3", text);
        Assert.Contains("\"App.ISvc.Get(int)\"", r.Sources["GeneratedPolicyRegistrations.g.cs"]);
        Assert.Contains("\"App.ISvc.Get(string)\"", r.Sources["GeneratedPolicyRegistrations.g.cs"]);
    }


    [Fact]
    public void OverloadIds_MatchTheRuntimeFormat()
    {
        // MethodIdsTests checks the runtime side of the same ids
        var r = Run(Usings + @"namespace App {
public interface IOrders {
  [Cache] Task<string> Find(int id);
  [Cache] Task<string> Find(string name, CancellationToken cancellationToken);
  [Cache] Task<string> Find<T>(int? id, string[] names, List<Guid> ids, T extra);
}}", nameof(OverloadIds_MatchTheRuntimeFormat));
        AssertCompiles(r);
        var policies = r.Sources["GeneratedPolicyRegistrations.g.cs"];
        Assert.Contains("\"App.IOrders.Find(int)\"", policies);
        Assert.Contains("\"App.IOrders.Find(string, System.Threading.CancellationToken)\"", policies);
        Assert.Contains("\"App.IOrders.Find``1(int?, string[], System.Collections.Generic.List<System.Guid>, T)\"", policies);
    }

    [Fact]
    public void OverloadIds_EqualMethodIdsFor_ForEveryParameterShape()
    {
        // Policies set in code or at runtime are looked up by MethodIds.For(MethodInfo), so every generated id must
        // be exactly the id the runtime computes for the same method
        var r = Run(Usings + @"
public sealed record GlobalRequest(int Id);
namespace App {
public class Outer<T> { public sealed record Inner(int Id); public sealed record Inner2<U>(int Id); }
public class Plain { public sealed record Nested(int Id); }
public interface IOrders {
  [Cache] string Find(int id);
  [Cache] string Find((int, string) pair);
  [Cache] string Find((int Id, string Name) named, bool flag);
  [Cache] string Find(List<(int A, string B)> list);
  [Cache] string Find((int, (string, long)) nested);
  [Cache] string Find((int, int, int, int, int, int, int, int, int) wide);
  [Cache] string Find((int, string)? maybe);
  [Cache] string Find(nint n);
  [Cache] string Find(nuint n);
  [Cache] string Find(IntPtr n, int x);
  [Cache] string Find(UIntPtr n, int x);
  [Cache] string Find(List<nint> list);
  [Cache] string Find(List<dynamic> list);
  [Cache] string Find(dynamic value, int x);
  [Cache] string Find(Outer<int>.Inner inner);
  [Cache] string Find(Outer<string>.Inner2<long> inner);
  [Cache] string Find(Plain.Nested nested);
  [Cache] string Find(GlobalRequest request);
  [Cache] string Find(List<int?[]> list);
  [Cache] string Find(Dictionary<string, List<int[,]>> map);
  [Cache] string Find(int[][] jagged);
  [Cache] string Find(int?[] a, string? s, List<string?> l);
  [Cache] string Find<T>(List<T> list, T x);
  [Cache] string Find(Func<int, string> f);
  [Cache] string Find(KeyValuePair<Guid, DateTime?> pair);
}
public interface IRepo<T> {
  [Cache] string Get(T id);
  [Cache] string Get(List<(T, int)> ids);
}}", nameof(OverloadIds_EqualMethodIdsFor_ForEveryParameterShape), load: true);
        AssertCompiles(r);
        var policies = r.Sources["GeneratedPolicyRegistrations.g.cs"];
        var generated = System.Text.RegularExpressions.Regex.Matches(policies, "\"(App\\.I[A-Za-z]+(?:<T>)?\\.[A-Za-z]+(?:``[0-9]+)?\\([^\"]*\\))\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        foreach (var iface in new[] { "App.IOrders", "App.IRepo`1" })
        {
            foreach (var method in r.Assembly!.GetType(iface)!.GetMethods())
            {
                var id = MethodCache.Core.PolicyPipeline.Model.MethodIds.For(method);
                _out.WriteLine(id);
                Assert.True(generated.Contains(id),
                    $"The runtime id '{id}' has no generated policy id. Generated:\n{string.Join("\n", generated)}");
            }
        }

        Assert.Equal(27, generated.Count);
    }

    [Fact]
    public async Task FluentPolicy_ForTupleOverload_ReachesTheDecorator()
    {
        var r = Run(Usings + @"namespace App {
public interface ISvc {
  [Cache] Task<string> Find(int id);
  [Cache] Task<string> Find((int Id, string Name) key);
}
public class Svc : ISvc {
  public int Calls;
  public Task<string> Find(int id) { Calls++; return Task.FromResult(""int""); }
  public Task<string> Find((int Id, string Name) key) { Calls++; return Task.FromResult(""tuple""); }
}
public static class Startup { public static void Configure(IServiceCollection s) {
  s.AddSingleton<Svc>(); s.AddSingleton<ISvc>(sp => sp.GetRequiredService<Svc>());
  s.AddMethodCache(config => {
    var svc = config.ForService<ISvc>();
    svc.Method(x => x.Find(Any<(int Id, string Name)>.Value)).CacheWhen(v => false);
  });
} }
public static class Probe { public static async Task<int> Go(ISvc s, Svc impl) {
  await s.Find(1); await s.Find(1);
  await s.Find((1, ""a"")); await s.Find((1, ""a""));
  return impl.Calls;
} }
}", nameof(FluentPolicy_ForTupleOverload_ReachesTheDecorator), load: true);
        AssertCompiles(r);
        var svc = Resolve(r.Assembly!, "App.ISvc", out var sp);
        var impl = sp.GetRequiredService(r.Assembly!.GetType("App.Svc")!);
        var calls = await (Task<int>)r.Assembly!.GetType("App.Probe")!.GetMethod("Go")!.Invoke(null, new[] { svc, impl })!;
        // Find(int) is cached; the fluent CacheWhen(false) keeps the tuple overload from being cached
        Assert.Equal(3, calls);
    }
}
