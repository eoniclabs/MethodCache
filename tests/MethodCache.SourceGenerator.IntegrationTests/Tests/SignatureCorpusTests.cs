using System.Reflection;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Runs the generator over a corpus of interface shapes that real code uses, and checks each one end to end:
/// the generated code compiles (with the C# 12 compiler of the .NET 8 SDK and with the latest language version),
/// the generator reports nothing, and through the real <c>AddMethodCache()</c> registration a repeated call is
/// served from the cache while a call that differs only in the shape under test reaches the implementation.
/// </summary>
/// <remarks>
/// Add a case here whenever a signature breaks the generator. Each case is an app on its own: the interface,
/// an implementation that counts calls in <c>Svc.Calls</c>, and two calls, <c>A</c> and <c>B</c>. The scenario
/// runs <c>A</c> twice and then <c>B</c>, and expects the implementation to be called once and then twice.
/// </remarks>
public class SignatureCorpusTests
{
    private readonly SourceGeneratorTestEngine _engine = new();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Cases.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public Task Shape_CompilesAndCaches_WithCSharp12(string name) => RunAsync(name, LanguageVersion.CSharp12);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public Task Shape_CompilesAndCaches_WithLatestLanguageVersion(string name) => RunAsync(name, LanguageVersion.Latest);

    private async Task RunAsync(string name, LanguageVersion languageVersion)
    {
        var shape = Cases[name];
        var source = shape.ToSource();

        GeneratedTestAssembly compiled;
        try
        {
            compiled = await _engine.CompileWithSourceGeneratorAsync(source, $"Corpus_{Sanitize(name)}_{languageVersion}", languageVersion);
        }
        catch (InvalidOperationException ex)
        {
            throw new Xunit.Sdk.XunitException($"{name}: {ex.Message}\n\nSource:\n{source}");
        }

        var generatorDiagnostics = compiled.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        Assert.True(generatorDiagnostics.Count == 0,
            $"{name}: the generator reported {string.Join("; ", generatorDiagnostics)}");

        var run = compiled.Assembly.GetType("Corpus.Scenario")!.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)!;
        var calls = await (Task<int[]>)run.Invoke(null, null)!;

        Assert.True(calls[0] == 1, $"{name}: calling A twice reached the implementation {calls[0]} times; the second call should be a cache hit");
        Assert.True(calls[1] == 2, $"{name}: B reached the implementation {calls[1] - calls[0]} times after A; it should be a separate cache entry (or the entry was invalidated)");
    }

    public static TheoryData<string> UnsupportedCaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Unsupported.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>
    /// A shape MethodCache can't cache gets its diagnostic and still compiles: the generator never emits code that
    /// breaks the build.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnsupportedCaseNames))]
    public async Task UnsupportedShape_ReportsItsDiagnostic_AndTheProjectStillCompiles(string name)
    {
        var (member, implementation, expectedId) = Unsupported[name];
        // Keep one valid cached method too: unsupported members must still forward without breaking its decorator.
        var source = $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using MethodCache;
            namespace Corpus
            {
                public interface ISvc
                {
                    [Cache] string Ping(int id);
                    [Cache] {{member}};
                }
                public class Svc : ISvc
                {
                    public string Ping(int id) => id.ToString();
                    public {{implementation}}
                }
            }
            """;

        GeneratedTestAssembly compiled;
        try
        {
            compiled = await _engine.CompileWithSourceGeneratorAsync(source, $"Unsupported_{Sanitize(name)}", LanguageVersion.CSharp12);
        }
        catch (InvalidOperationException ex)
        {
            throw new Xunit.Sdk.XunitException($"{name}: the generated code does not compile: {ex.Message}\n\nSource:\n{source}");
        }

        Assert.Contains(compiled.Diagnostics, d => d.Id == expectedId);
    }

    [Theory]
    [InlineData("protected abstract void Touch();", "void ISvc.Touch() { }")]
    [InlineData("protected abstract int Value { get; }", "int ISvc.Value => 1;")]
    [InlineData("protected abstract event Action Changed;", "event Action ISvc.Changed { add { } remove { } }")]
    public async Task NonPublicAbstractContract_ReportsDiagnostic_WithoutInaccessibleForwarding(string member, string implementation)
    {
        var source = $$"""
            using System;
            using MethodCache;
            public interface ISvc { [Cache] int Get(int id); {{member}} }
            public class Svc : ISvc { public int Get(int id) => id; {{implementation}} }
            """;
        var compiled = await _engine.CompileWithSourceGeneratorAsync(source, "NonPublicAbstractContract", LanguageVersion.CSharp12);
        Assert.Contains(compiled.Diagnostics, d => d.Id == "MCG013");
    }

    [Fact]
    public async Task AmbiguousPlaceholder_ReportsWarning_AndKeepsLiteralTag()
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync("""
            using MethodCache;
            public interface ISvc { [Cache(Tags = new[] { "n:{NAME}" })] int Get(int name, int Name); }
            public class Svc : ISvc { public int Get(int name, int Name) => name + Name; }
            """, "AmbiguousPlaceholder", LanguageVersion.CSharp12);
        Assert.Contains(compiled.Diagnostics, d => d.Id == "MCG016");
        Assert.DoesNotContain(compiled.Diagnostics, d => d.Id == "CS8785");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactoryOwnedImplementation_DisposesResourcesWithoutDisposableInterface(bool asyncOnly)
    {
        var disposalInterface = asyncOnly ? "IAsyncDisposable" : "IDisposable";
        var dispose = asyncOnly ? "public ValueTask DisposeAsync() { Disposed++; return default; }" : "public void Dispose() { Disposed++; }";
        var source = $$"""
            using System;
            using System.Threading.Tasks;
            using MethodCache;
            using Microsoft.Extensions.DependencyInjection;
            namespace Corpus {
                public interface ISvc { [Cache] int Get(int id); }
                public class Svc : ISvc, {{disposalInterface}} {
                    public static int Disposed;
                    public int Get(int id) => id;
                    {{dispose}}
                }
                public static class Scenario {
                    public static async Task<int> RunAsync() {
                        var services = new ServiceCollection();
                        services.AddMethodCache();
                        services.AddISvcWithCaching(_ => new Svc());
                        await using (var provider = services.BuildServiceProvider()) {
                            provider.GetRequiredService<ISvc>().Get(1);
                        }
                        return Svc.Disposed;
                    }
                }
            }
            """;
        var compiled = await _engine.CompileWithSourceGeneratorAsync(source, "FactoryOwnedHiddenDisposal" + asyncOnly, LanguageVersion.CSharp12);
        var run = compiled.Assembly.GetType("Corpus.Scenario")!.GetMethod("RunAsync")!;
        Assert.Equal(1, await (Task<int>)run.Invoke(null, null)!);
    }

    [Fact]
    public void ReferencedNestedInterfaces_ArityLookingNames_HaveDistinctDecoratorNamespaces()
    {
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var library = CSharpCompilation.Create("ExternalNaming", new[] { CSharpSyntaxTree.ParseText("""
            namespace External {
                public class Outer_Arity1 { public interface IInner { int Get(int id); } }
                public class Outer<T> { public interface IInner { int Get(int id); } }
            }
            """) }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = library.Emit(image);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        var consumer = CSharpCompilation.Create("NamingConsumer", references: refs.Append(MetadataReference.CreateFromImage(image.ToArray())));
        var a = consumer.GetTypeByMetadataName("External.Outer_Arity1+IInner")!;
        var b = consumer.GetTypeByMetadataName("External.Outer`1+IInner")!;
        var utilities = typeof(MethodCacheGenerator).GetNestedType("Utils", BindingFlags.NonPublic)!;
        var name = utilities.GetMethod("GetDecoratorNamespace", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotEqual(name.Invoke(null, new object[] { a }), name.Invoke(null, new object[] { b }));
    }

    private static readonly Dictionary<string, (string Member, string Implementation, string ExpectedId)> Unsupported = new()
    {
        ["void"] = ("void Touch(int id)", "void Touch(int id) { }", "MCG001"),
        ["Task without result"] = ("Task Save(int id)", "Task Save(int id) => Task.CompletedTask;", "MCG002"),
        ["ValueTask without result"] = ("ValueTask Save(int id)", "ValueTask Save(int id) => default;", "MCG002"),
        ["in parameter"] = ("Task<string> Get(in int id)", "Task<string> Get(in int id) => Task.FromResult(\"v\");", "MCG003"),
        ["ref parameter"] = ("Task<string> Get(ref int id)", "Task<string> Get(ref int id) => Task.FromResult(\"v\");", "MCG003"),
        ["out parameter"] = ("string Get(int id, out int found)", "string Get(int id, out int found) { found = 1; return \"v\"; }", "MCG003"),
        ["ReadOnlySpan parameter"] = ("string Get(ReadOnlySpan<char> id)", "string Get(ReadOnlySpan<char> id) => \"v\";", "MCG007"),
        ["Span result"] = ("Span<int> Get(int id)", "Span<int> Get(int id) => new[] { id };", "MCG007"),
        ["ReadOnlySpan result"] = ("ReadOnlySpan<int> Get(int id)", "ReadOnlySpan<int> Get(int id) => new[] { id };", "MCG007"),
        ["ref result"] = ("ref int Get(int id)", "ref int Get(int id) => ref _value; private int _value;", "MCG003"),
        ["ref readonly result"] = ("ref readonly int Get(int id)", "ref readonly int Get(int id) => ref _value; private int _value;", "MCG003"),
        ["pointer result"] = ("unsafe int* Get(int id)", "unsafe int* Get(int id) => null;", "MCG006"),
        ["function pointer result"] = ("unsafe delegate*<int> Get(int id)", "unsafe delegate*<int> Get(int id) => null;", "MCG006"),
        ["pointer array parameter"] = ("unsafe string Get(int*[] ids)", "unsafe string Get(int*[] ids) => \"v\";", "MCG006"),
        ["pointer array result"] = ("unsafe int*[] Get(int id)", "unsafe int*[] Get(int id) => new int*[1];", "MCG006"),
        ["function pointer array parameter"] = ("unsafe string Get(delegate*<int>[] f)", "unsafe string Get(delegate*<int>[] f) => \"v\";", "MCG006"),
        ["pointer to pointer parameter"] = ("unsafe string Get(int** p)", "unsafe string Get(int** p) => \"v\";", "MCG006"),
        ["IEnumerable result"] = ("IEnumerable<int> Get(int id)", "IEnumerable<int> Get(int id) { yield return id; }", "MCG014"),
        ["IAsyncEnumerable result"] = ("IAsyncEnumerable<int> Get(int id)", "async IAsyncEnumerable<int> Get(int id) { await Task.Yield(); yield return id; }", "MCG014"),
    };

    private static string Sanitize(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>
    /// One app in the corpus. <see cref="Declarations"/> holds the cached interface and its implementation
    /// <c>Svc</c>, which must count calls in a static <c>Calls</c> field via <c>Svc.Hit()</c>.
    /// </summary>
    private sealed record Shape(string Declarations, string Service, string A, string B, string Usings = "", string Registrations = "")
    {
        public string ToSource() => $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using MethodCache;
            using Microsoft.Extensions.DependencyInjection;
            {{Usings}}

            namespace Corpus
            {
            {{Declarations}}

                public static class Counter
                {
                    public static int Calls;
                    public static void Hit() => Interlocked.Increment(ref Calls);
                }

                public static class Scenario
                {
                    public static async global::System.Threading.Tasks.Task<int[]> RunAsync()
                    {
                        var services = new ServiceCollection();
                        {{Registrations}}
                        services.AddMethodCache();
                        await using var provider = services.BuildServiceProvider();
                        var s = provider.GetRequiredService<{{Service}}>();
                        if (!s.GetType().Name.Contains("Decorator", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(s.GetType().Name + " is not a generated decorator");
                        }

                        Counter.Calls = 0;
                        {{A}};
                        {{A}};
                        var afterA = Counter.Calls;
                        {{B}};
                        return new[] { afterA, Counter.Calls };
                    }
                }
            }
            """;
    }

    /// <summary>A single cached member on <c>ISvc</c>, implemented by <c>Svc</c>.</summary>
    private static Shape Member(string member, string implementation, string a, string b, string extraTypes = "", string usings = "")
        => new($$"""
                {{extraTypes}}

                public interface ISvc
                {
                    [Cache]
                    {{member}};
                }

                public class Svc : ISvc
                {
                    public {{implementation}}
                }
            """, "ISvc", a, b, usings);

    private static readonly Dictionary<string, Shape> Cases = new()
    {
        ["runtime tags see every case-distinct argument"] = new("""
            public interface ISvc { [Cache(Tags = new[] { "attribute:{a}" })] Task<int> Get(int a, int A); }
            public class Svc : ISvc { public Task<int> Get(int a, int A) { Counter.Hit(); return Task.FromResult(a + A); } }
            """, "ISvc", "await s.Get(1, 2)", "await provider.GetRequiredService<ICacheManager>().InvalidateByTagsAsync(\"runtime:2\"); await s.Get(1, 2)",
            Registrations: "services.AddMethodCache(c => c.ForService<ISvc>().Method(x => x.Get(default, default)).Configure(o => o.WithTags(\"runtime:{A}\")));"),
        ["runtime tags retain ambiguous placeholders"] = new("""
            public interface ISvc { [Cache(Tags = new[] { "attribute:{name}" })] Task<int> Get(int name, int Name); }
            public class Svc : ISvc { public Task<int> Get(int name, int Name) { Counter.Hit(); return Task.FromResult(name + Name); } }
            """, "ISvc", "await s.Get(1, 2)", "await provider.GetRequiredService<ICacheManager>().InvalidateByTagsAsync(\"runtime:{NAME}\"); await s.Get(1, 2)",
            Registrations: "services.AddMethodCache(c => c.ForService<ISvc>().Method(x => x.Get(default, default)).Configure(o => o.WithTags(\"runtime:{NAME}\")));"),

        ["application types shadow framework and cache infrastructure"] = new("""
            public class Task { }
            public class ValueTask { }
            public class List<T> { }
            public class CachedMethod { }
            public class CacheRuntimePolicy { }
            public class ICacheManager { }
            public class ICacheKeyGenerator { }
            public class ICacheMetricsProvider { }
            public class IPolicyRegistry { }
            public class PolicyHandle { }
            public class CacheTagTemplates { }
            public class MethodCacheKeys { }
            public class MethodCacheKeyBuilder { }
            public interface ISvc {
                [Cache(Tags = new[] { "id:{id}" })] global::System.Threading.Tasks.Task<int> Get(int id);
                [CacheInvalidate(Tags = new[] { "id:{id}" })] global::System.Threading.Tasks.ValueTask Clear(int id);
            }
            public class Svc : ISvc {
                public global::System.Threading.Tasks.Task<int> Get(int id) { Counter.Hit(); return global::System.Threading.Tasks.Task.FromResult(id); }
                public global::System.Threading.Tasks.ValueTask Clear(int id) => default;
            }
            """, "ISvc", "await s.Get(1)", "await s.Clear(1); await s.Get(1)"),

        ["sealed default helper members"] = new("""
            public interface ISvc {
                [Cache] int Get(int id);
                public sealed int Helper() => 7;
                public sealed int Value => 8;
                public sealed event Action Changed { add { } remove { } }
            }
            public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } }
            """, "ISvc", "s.Get(s.Helper())", "s.Get(s.Value)"),

        ["keyword generic method parameter"] = Member("T Get<@class, T>(T id) where @class : class", "T Get<@class, T>(T id) where @class : class { Counter.Hit(); return id; }", "s.Get<object, int>(1)", "s.Get<object, int>(2)"),
        ["keyword generic interface parameter"] = new("public interface ISvc<@class> where @class : class { [Cache] int Get(int id); } public class Svc : ISvc<string> { public int Get(int id) { Counter.Hit(); return id; } }", "ISvc<string>", "s.Get(1)", "s.Get(2)"),
        ["keyword namespace"] = new("namespace @class { public interface ISvc { [Cache] int Get(int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } } }", "@class.ISvc", "s.Get(1)", "s.Get(2)"),
        ["private default interface members"] = new("public interface ISvc { [Cache] int Get(int id); private int Helper() => 42; private int Hidden => 42; private event Action Changed { add { } remove { } } } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } }", "ISvc", "s.Get(1)", "s.Get(2)"),
        ["protected default interface members"] = new("public interface ISvc { [Cache] int Get(int id); protected int Helper() => 42; protected int Hidden => 42; } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } }", "ISvc", "s.Get(1)", "s.Get(2)"),
        ["reserved parameter __mcKey"] = Member("int Get(int __mcKey)", "int Get(int __mcKey) { Counter.Hit(); return __mcKey; }", "s.Get(1)", "s.Get(2)"),
        ["reserved parameter _cacheManager"] = Member("int Get(int _cacheManager)", "int Get(int _cacheManager) { Counter.Hit(); return _cacheManager; }", "s.Get(1)", "s.Get(2)"),
        ["reserved parameter _decorated"] = Member("int Get(int _decorated)", "int Get(int _decorated) { Counter.Hit(); return _decorated; }", "s.Get(1)", "s.Get(2)"),
        ["reserved parameter _keyGenerator"] = Member("int Get(int _keyGenerator)", "int Get(int _keyGenerator) { Counter.Hit(); return _keyGenerator; }", "s.Get(1)", "s.Get(2)"),
        ["reserved parameter __cacheKey"] = Member("int Get(int __cacheKey)", "int Get(int __cacheKey) { Counter.Hit(); return __cacheKey; }", "s.Get(1)", "s.Get(2)"),
        ["reserved parameter __policy"] = Member("int Get(int __policy)", "int Get(int __policy) { Counter.Hit(); return __policy; }", "s.Get(1)", "s.Get(2)"),
        ["reserved parameter __mcTags"] = Member("int Get(int __mcTags)", "int Get(int __mcTags) { Counter.Hit(); return __mcTags; }", "s.Get(1)", "s.Get(2)"),
        ["reserved parameter __mcResult"] = Member("int Get(int __mcResult)", "int Get(int __mcResult) { Counter.Hit(); return __mcResult; }", "s.Get(1)", "s.Get(2)"),
        ["reserved parameter _method_Get"] = Member("int Get(int _method_Get)", "int Get(int _method_Get) { Counter.Hit(); return _method_Get; }", "s.Get(1)", "s.Get(2)"),
        ["reserved parameter __mcArgument0"] = Member("int Get(int __mcArgument0)", "int Get(int __mcArgument0) { Counter.Hit(); return __mcArgument0; }", "s.Get(1)", "s.Get(2)"),
        ["reserved generic method parameter"] = Member("T Get<__mcKey, T>(T value)", "T Get<__mcKey, T>(T value) { Counter.Hit(); return value; }", "s.Get<object, int>(1)", "s.Get<object, int>(2)"),
        ["overload field name collision"] = new("public interface ISvc { [Cache] int Get(int id); [Cache] int Get(string id); [Cache] int Get_0(int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public int Get(string id) { Counter.Hit(); return id.Length; } public int Get_0(int id) { Counter.Hit(); return id; } }", "ISvc", "s.Get(1)", "s.Get_0(1)"),
        ["multiple generic outers with shadowed parameters"] = new("public class Outer<T> where T : struct { public class Middle<T> where T : class { public interface IInner<U> where U : struct { [Cache] string Get(T value, U other); } } } public class Svc : Outer<int>.Middle<string>.IInner<Guid> { public string Get(string value, Guid other) { Counter.Hit(); return value; } }", "Outer<int>.Middle<string>.IInner<Guid>", "s.Get(\"a\", Guid.Empty)", "s.Get(\"b\", Guid.Empty)"),
        ["interface in generic outer type"] = new("public class Outer<T> where T : class { public interface IInner { [Cache] int Get(int id); } } public class Svc : Outer<string>.IInner { public int Get(int id) { Counter.Hit(); return id; } }", "Outer<string>.IInner", "s.Get(1)", "s.Get(2)"),
        ["generic inner and outer constraints"] = new("public class Outer<T> where T : unmanaged { public interface IInner<U> where U : class { [Cache] int Get(T id, U value); } } public class Svc : Outer<int>.IInner<string> { public int Get(int id, string value) { Counter.Hit(); return id; } }", "Outer<int>.IInner<string>", "s.Get(1, \"a\")", "s.Get(2, \"a\")"),
        ["generic outer arity collision"] = new("public class Outer { public interface IInner { [Cache] int Get(int id); } } public class Outer<T> { public interface IInner { [Cache] int Get(int id); } } public class Plain : Outer.IInner { public int Get(int id) { Counter.Hit(); return id; } } public class Svc : Outer<string>.IInner { public int Get(int id) { Counter.Hit(); return id; } }", "Outer<string>.IInner", "s.Get(1)", "s.Get(2)"),
        ["generic outer arity lookalike"] = new("public class Outer_Arity1 { public interface IInner { [Cache] int Get(int id); } } public class Outer<T> { public interface IInner { [Cache] int Get(int id); } } public class Plain : Outer_Arity1.IInner { public int Get(int id) { Counter.Hit(); return id; } } public class Svc : Outer<string>.IInner { public int Get(int id) { Counter.Hit(); return id; } }", "Outer<string>.IInner", "s.Get(1)", "s.Get(2)"),
        ["case distinct parameters no tags"] = new("public interface ISvc { [Cache] int Get(int a, int A); } public class Svc : ISvc { public int Get(int a, int A) { Counter.Hit(); return a + A; } }", "ISvc", "s.Get(1,2)", "s.Get(2,3)"),
        ["case distinct parameters exact lower"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{a}\" })] int Get(int a, int A); [CacheInvalidate(Tags = new[] { \"n:{a}\" })] void Touch(int a); } public class Svc : ISvc { public int Get(int a, int A) { Counter.Hit(); return a + A; } public void Touch(int a) { } }", "ISvc", "s.Get(1,2)", "s.Touch(1); s.Get(1,2)"),
        ["case distinct parameters exact upper"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{A}\" })] int Get(int a, int A); [CacheInvalidate(Tags = new[] { \"n:{A}\" })] void Touch(int A); } public class Svc : ISvc { public int Get(int a, int A) { Counter.Hit(); return a + A; } public void Touch(int A) { } }", "ISvc", "s.Get(1,2)", "s.Touch(2); s.Get(1,2)"),
        ["invalidation pending ref"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] Task<int> Touch(ref int id); } public class Svc : ISvc { public static TaskCompletionSource<int> Pending = new(); public int Get(int id) { Counter.Hit(); return id; } public Task<int> Touch(ref int id) => Pending.Task; }", "ISvc", "s.Get(1)", "int id = 1; var pending = s.Touch(ref id); s.Get(1); if (Counter.Calls != 1) throw new Exception(\"Invalidated before completion\"); Svc.Pending.SetResult(7); if (await pending != 7) throw new Exception(\"Lost result\"); s.Get(1)"),
        ["invalidation failure ref"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] Task<int> Touch(ref int id); } public class Svc : ISvc { public static TaskCompletionSource<int> Pending = new(); public int Get(int id) { Counter.Hit(); return id; } public Task<int> Touch(ref int id) => Pending.Task; }", "ISvc", "s.Get(1)", "int id = 1; var pending = s.Touch(ref id); Svc.Pending.SetException(new InvalidOperationException(\"failed\")); try { await pending; throw new Exception(\"Swallowed failure\"); } catch (InvalidOperationException) { } s.Get(1); if (Counter.Calls != 1) throw new Exception(\"Invalidated after failure\"); Svc.Pending = new TaskCompletionSource<int>(); pending = s.Touch(ref id); Svc.Pending.SetResult(7); await pending; s.Get(1)"),
        ["invalidation Task ref"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] Task Touch(ref int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public Task Touch(ref int id) { return Task.CompletedTask; } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(ref id); s.Get(1)"),
        ["invalidation Task out"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] Task Touch(out int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public Task Touch(out int id) { id = 1; return Task.CompletedTask; } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(out id); s.Get(1)"),
        ["invalidation Task in"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] Task Touch(in int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public Task Touch(in int id) { return Task.CompletedTask; } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(in id); s.Get(1)"),
        ["invalidation ValueTask ref"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] ValueTask Touch(ref int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public ValueTask Touch(ref int id) { return default; } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(ref id); s.Get(1)"),
        ["invalidation ValueTask out"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] ValueTask Touch(out int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public ValueTask Touch(out int id) { id = 1; return default; } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(out id); s.Get(1)"),
        ["invalidation ValueTask in"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] ValueTask Touch(in int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public ValueTask Touch(in int id) { return default; } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(in id); s.Get(1)"),
        ["invalidation Task<int> ref"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] Task<int> Touch(ref int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public Task<int> Touch(ref int id) { return Task.FromResult(7); } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(ref id); s.Get(1)"),
        ["invalidation Task<int> out"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] Task<int> Touch(out int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public Task<int> Touch(out int id) { id = 1; return Task.FromResult(7); } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(out id); s.Get(1)"),
        ["invalidation Task<int> in"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] Task<int> Touch(in int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public Task<int> Touch(in int id) { return Task.FromResult(7); } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(in id); s.Get(1)"),
        ["invalidation ValueTask<int> ref"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] ValueTask<int> Touch(ref int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public ValueTask<int> Touch(ref int id) { return new ValueTask<int>(7); } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(ref id); s.Get(1)"),
        ["invalidation ValueTask<int> out"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] ValueTask<int> Touch(out int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public ValueTask<int> Touch(out int id) { id = 1; return new ValueTask<int>(7); } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(out id); s.Get(1)"),
        ["invalidation ValueTask<int> in"] = new("public interface ISvc { [Cache(Tags = new[] { \"n:{id}\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n:{id}\" })] ValueTask<int> Touch(in int id); } public class Svc : ISvc { public int Get(int id) { Counter.Hit(); return id; } public ValueTask<int> Touch(in int id) { return new ValueTask<int>(7); } }", "ISvc", "s.Get(1)", "int id = 1; await s.Touch(in id); s.Get(1)"),
        ["invalidation ref result"] = new("public interface ISvc { [Cache(Tags = new[] { \"n\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n\" })] ref int Reference(); } public class Svc : ISvc { private int _value; public int Get(int id) { Counter.Hit(); return id; } public ref int Reference() => ref _value; }", "ISvc", "s.Get(1)", "_ = s.Reference(); s.Get(1)"),
        ["invalidation ref readonly result"] = new("public interface ISvc { [Cache(Tags = new[] { \"n\" })] int Get(int id); [CacheInvalidate(Tags = new[] { \"n\" })] ref readonly int Reference(); } public class Svc : ISvc { private int _value; public int Get(int id) { Counter.Hit(); return id; } public ref readonly int Reference() => ref _value; }", "ISvc", "s.Get(1)", "_ = s.Reference(); s.Get(1)"),
        // Return types
        ["Task<T>"] = Member("Task<string> Get(int id)", "Task<string> Get(int id) { Counter.Hit(); return Task.FromResult(\"v\" + id); }", "await s.Get(1)", "await s.Get(2)"),
        ["ValueTask<T>"] = Member("ValueTask<string> Get(int id)", "ValueTask<string> Get(int id) { Counter.Hit(); return new ValueTask<string>(\"v\" + id); }", "await s.Get(1)", "await s.Get(2)"),
        ["sync reference type"] = Member("string Get(int id)", "string Get(int id) { Counter.Hit(); return \"v\" + id; }", "s.Get(1)", "s.Get(2)"),
        ["sync value type"] = Member("int Get(int id)", "int Get(int id) { Counter.Hit(); return id * 2; }", "s.Get(1)", "s.Get(2)"),
        ["Task<value type>"] = Member("Task<long> Get(int id)", "Task<long> Get(int id) { Counter.Hit(); return Task.FromResult((long)id); }", "await s.Get(1)", "await s.Get(2)"),
        ["Task<nullable value type>"] = Member("Task<int?> Get(int id)", "Task<int?> Get(int id) { Counter.Hit(); return Task.FromResult<int?>(null); }", "await s.Get(1)", "await s.Get(2)"),
        ["null result"] = Member("Task<string?> Find(int id)", "Task<string?> Find(int id) { Counter.Hit(); return Task.FromResult<string?>(null); }", "await s.Find(1)", "await s.Find(2)"),
        ["Task<tuple>"] = Member("Task<(int Id, string Name)> Get(int id)", "Task<(int Id, string Name)> Get(int id) { Counter.Hit(); return Task.FromResult((id, \"n\")); }", "await s.Get(1)", "await s.Get(2)"),
        ["Task<record>"] = Member("Task<User> Get(int id)", "Task<User> Get(int id) { Counter.Hit(); return Task.FromResult(new User(id, \"n\")); }", "await s.Get(1)", "await s.Get(2)",
            "public record User(int Id, string Name);"),
        ["Task<List<T>>"] = Member("Task<List<string>> Get(int id)", "Task<List<string>> Get(int id) { Counter.Hit(); return Task.FromResult(new List<string> { \"a\" }); }", "await s.Get(1)", "await s.Get(2)"),
        ["Task<Dictionary>"] = Member("Task<Dictionary<string, int>> Get(int id)", "Task<Dictionary<string, int>> Get(int id) { Counter.Hit(); return Task.FromResult(new Dictionary<string, int> { [\"a\"] = id }); }", "await s.Get(1)", "await s.Get(2)"),
        ["Task<array>"] = Member("Task<int[]> Get(int id)", "Task<int[]> Get(int id) { Counter.Hit(); return Task.FromResult(new[] { id }); }", "await s.Get(1)", "await s.Get(2)"),
        ["Task<nullable generic>"] = Member("Task<List<string>?> Get(int id)", "Task<List<string>?> Get(int id) { Counter.Hit(); return Task.FromResult<List<string>?>(null); }", "await s.Get(1)", "await s.Get(2)"),

        // CancellationToken and optional parameters
        ["CancellationToken = default"] = Member("Task<string> Get(int id, CancellationToken ct = default)", "Task<string> Get(int id, CancellationToken ct = default) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1)", "await s.Get(2)"),
        ["CancellationToken passed"] = Member("Task<string> Get(int id, CancellationToken ct = default)", "Task<string> Get(int id, CancellationToken ct = default) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1, CancellationToken.None)", "await s.Get(2, new CancellationTokenSource().Token)"),
        ["CancellationToken required"] = Member("Task<string> Get(int id, CancellationToken cancellationToken)", "Task<string> Get(int id, CancellationToken cancellationToken) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1, default)", "await s.Get(2, default)"),
        ["int default"] = Member("Task<string> Get(string q, int page = 1)", "Task<string> Get(string q, int page = 1) { Counter.Hit(); return Task.FromResult(q); }", "await s.Get(\"a\")", "await s.Get(\"a\", 2)"),
        ["negative default"] = Member("Task<string> Get(string q, int offset = -1)", "Task<string> Get(string q, int offset = -1) { Counter.Hit(); return Task.FromResult(q); }", "await s.Get(\"a\")", "await s.Get(\"a\", 1)"),
        ["long default"] = Member("Task<string> Get(long max = long.MaxValue)", "Task<string> Get(long max = long.MaxValue) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(1)"),
        ["double default"] = Member("Task<string> Get(double factor = 1.5)", "Task<string> Get(double factor = 1.5) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(2.5)"),
        ["float default"] = Member("Task<string> Get(float factor = 1.5f)", "Task<string> Get(float factor = 1.5f) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(2.5f)"),
        ["decimal default"] = Member("Task<string> Get(decimal price = 1.5m)", "Task<string> Get(decimal price = 1.5m) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(2.5m)"),
        ["bool default"] = Member("Task<string> Get(bool all = false)", "Task<string> Get(bool all = false) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(true)"),
        ["char default"] = Member("Task<string> Get(char sep = ',')", "Task<string> Get(char sep = ',') { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(';')"),
        ["string default with escapes"] = Member("Task<string> Get(string text = \"a\\\"b\\\\c\\td\")", "Task<string> Get(string text = \"a\\\"b\\\\c\\td\") { Counter.Hit(); return Task.FromResult(text); }", "await s.Get()", "await s.Get(\"x\")"),
        ["null default"] = Member("Task<string> Get(string? filter = null)", "Task<string> Get(string? filter = null) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(\"x\")"),
        ["nullable int default"] = Member("Task<string> Get(int? page = null)", "Task<string> Get(int? page = null) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(3)"),
        ["enum default"] = Member("Task<string> Get(Kind kind = Kind.B)", "Task<string> Get(Kind kind = Kind.B) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(Kind.A)",
            "public enum Kind { A, B, C }"),
        ["flags enum default"] = Member("Task<string> Get(Options options = Options.X | Options.Y)", "Task<string> Get(Options options = Options.X | Options.Y) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(Options.X)",
            "[Flags] public enum Options { None = 0, X = 1, Y = 2 }"),
        ["struct default"] = Member("Task<string> Get(DateTime since = default)", "Task<string> Get(DateTime since = default) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get()", "await s.Get(new DateTime(2020, 1, 1))"),
        ["several defaults"] = Member("Task<string> Get(int id, string? q = null, int page = 1, bool desc = false, CancellationToken ct = default)", "Task<string> Get(int id, string? q = null, int page = 1, bool desc = false, CancellationToken ct = default) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1)", "await s.Get(1, desc: true)"),

        // Native-sized integers, which System.Text.Json can't write on its own
        ["nint parameter"] = Member("Task<string> Get(nint handle)", "Task<string> Get(nint handle) { Counter.Hit(); return Task.FromResult(\"v\" + handle); }", "await s.Get(1)", "await s.Get(-1)"),
        ["nuint parameter"] = Member("string Get(nuint size)", "string Get(nuint size) { Counter.Hit(); return \"v\" + size; }", "s.Get(1)", "s.Get(nuint.MaxValue)"),
        ["record with an nint member"] = Member("Task<string> Get(Handle handle)", "Task<string> Get(Handle handle) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(new Handle(1, \"a\"))", "await s.Get(new Handle(2, \"a\"))",
            "public sealed record Handle(nint Value, string Name);"),

        // Parameter names that are keywords or collide with names in generated code
        ["keyword parameter names"] = Member("Task<string> Get(int @class, string @event)", "Task<string> Get(int @class, string @event) { Counter.Hit(); return Task.FromResult(@event); }", "await s.Get(1, \"a\")", "await s.Get(1, \"b\")"),
        ["unsafe members next to a cached method"] = new("""
                public unsafe interface ISvc
                {
                    [Cache] Task<string> Get(int id);
                    int*[] GetPointers(int id);
                    void Take(delegate*<int>[] functions);
                    int** Deep(int id);
                    int* Pointer { get; }
                    int*[] Pointers { get; set; }
                    int* this[int i] { get; }
                }
                public unsafe class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult(id.ToString()); }
                    public int*[] GetPointers(int id) => new int*[id];
                    public void Take(delegate*<int>[] functions) { }
                    public int** Deep(int id) => null;
                    public int* Pointer => null;
                    public int*[] Pointers { get; set; } = new int*[0];
                    public int* this[int i] => null;
                }
            """, "ISvc", "await s.Get(1)", "await s.Get(2)"),
        ["keyword property and event names"] = new("""
                public interface ISvc
                {
                    [Cache] Task<string> Get(int id);
                    int @class { get; set; }
                    event Action? @event;
                }
                public class Svc : ISvc
                {
                    private int _value;
                    public int @class { get => _value; set { _value = value; @event?.Invoke(); } }
                    public event Action? @event;
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult(id.ToString()); }
                }
                public static class Checks
                {
                    public static Task<string> Call(ISvc service, int id)
                    {
                        var raised = false;
                        Action handler = () => raised = true;
                        service.@event += handler;
                        service.@class = id;
                        service.@event -= handler;
                        if (!raised || service.@class != id) throw new InvalidOperationException("passthrough failed");
                        return service.Get(service.@class);
                    }
                }
            """, "ISvc", "await Checks.Call(s, 1)", "await Checks.Call(s, 2)"),
        ["keyword indexer parameter name"] = new("""
                public interface ISvc
                {
                    [Cache] Task<string> Get(int id);
                    int this[int @class] { get; set; }
                }
                public class Svc : ISvc
                {
                    private readonly Dictionary<int, int> _values = new();
                    public int this[int @class] { get => _values[@class]; set => _values[@class] = value; }
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult(id.ToString()); }
                }
                public static class Checks
                {
                    public static Task<string> Call(ISvc service, int id)
                    {
                        service[0] = id;
                        return service.Get(service[0]);
                    }
                }
            """, "ISvc", "await Checks.Call(s, 1)", "await Checks.Call(s, 2)"),
        ["generated local names as parameters"] = Member("Task<string> Get(int result, int key, int value, int policy, int cacheKey, int cacheSnapshot)", "Task<string> Get(int result, int key, int value, int policy, int cacheKey, int cacheSnapshot) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1, 2, 3, 4, 5, 6)", "await s.Get(1, 2, 3, 4, 5, 7)"),
        ["more generated names as parameters"] = Member("Task<string> Get(int factory, int services, int inner, int metrics, int buffer, int args)", "Task<string> Get(int factory, int services, int inner, int metrics, int buffer, int args) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1, 2, 3, 4, 5, 6)", "await s.Get(1, 2, 3, 4, 5, 7)"),

        // Argument types
        ["string"] = Member("Task<string> Get(string id)", "Task<string> Get(string id) { Counter.Hit(); return Task.FromResult(id); }", "await s.Get(\"å\")", "await s.Get(\"a\")"),
        ["null vs empty string"] = Member("Task<string> Get(string? id)", "Task<string> Get(string? id) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(null)", "await s.Get(\"\")"),
        ["separator inside strings"] = Member("Task<string> Get(string a, string b)", "Task<string> Get(string a, string b) { Counter.Hit(); return Task.FromResult(a + b); }", "await s.Get(\"a_b\", \"c\")", "await s.Get(\"a\", \"b_c\")"),
        ["colon inside strings"] = Member("Task<string> Get(string a, string b)", "Task<string> Get(string a, string b) { Counter.Hit(); return Task.FromResult(a + b); }", "await s.Get(\"a:b\", \"c\")", "await s.Get(\"a\", \"b:c\")"),
        ["Guid"] = Member("Task<string> Get(Guid id)", "Task<string> Get(Guid id) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(new Guid(\"11111111-1111-1111-1111-111111111111\"))", "await s.Get(new Guid(\"11111111-1111-1111-1111-111111111112\"))"),
        ["DateTime ticks"] = Member("Task<string> Get(DateTime at)", "Task<string> Get(DateTime at) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))", "await s.Get(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1))"),
        ["DateTimeOffset ticks"] = Member("Task<string> Get(DateTimeOffset at)", "Task<string> Get(DateTimeOffset at) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))", "await s.Get(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1))"),
        ["TimeSpan"] = Member("Task<string> Get(TimeSpan window)", "Task<string> Get(TimeSpan window) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(TimeSpan.FromSeconds(1))", "await s.Get(TimeSpan.FromSeconds(2))"),
        ["double"] = Member("Task<string> Get(double x)", "Task<string> Get(double x) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(0.1 + 0.2)", "await s.Get(0.3)"),
        ["decimal"] = Member("Task<string> Get(decimal x)", "Task<string> Get(decimal x) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1.5m)", "await s.Get(2.5m)"),
        ["negative numbers"] = Member("Task<string> Get(int x)", "Task<string> Get(int x) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1)", "await s.Get(-1)"),
        ["enum argument"] = Member("Task<string> Get(Kind kind)", "Task<string> Get(Kind kind) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(Kind.A)", "await s.Get(Kind.B)",
            "public enum Kind { A, B }"),
        ["nullable argument"] = Member("Task<string> Get(int? id)", "Task<string> Get(int? id) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(null)", "await s.Get(0)"),
        ["bool argument"] = Member("Task<string> Get(bool b)", "Task<string> Get(bool b) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(true)", "await s.Get(false)"),
        ["array argument"] = Member("Task<string> Get(int[] ids)", "Task<string> Get(int[] ids) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(new[] { 1, 2 })", "await s.Get(new[] { 12 })"),
        ["params argument"] = Member("Task<string> Get(params int[] ids)", "Task<string> Get(params int[] ids) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1, 2)", "await s.Get(1, 2, 3)"),
        ["List argument"] = Member("Task<string> Get(List<string> names)", "Task<string> Get(List<string> names) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(new List<string> { \"a\", \"b\" })", "await s.Get(new List<string> { \"a\" })"),
        ["IEnumerable argument"] = Member("Task<string> Get(IEnumerable<int> ids)", "Task<string> Get(IEnumerable<int> ids) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(new[] { 1, 2 })", "await s.Get(new[] { 2, 1 })"),
        ["record argument"] = Member("Task<string> Get(Query q)", "Task<string> Get(Query q) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(new Query(\"a\", 1))", "await s.Get(new Query(\"a\", 2))",
            "public record Query(string Text, int Page);"),
        ["record struct argument"] = Member("Task<string> Get(Point p)", "Task<string> Get(Point p) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(new Point(1, 2))", "await s.Get(new Point(2, 1))",
            "public readonly record struct Point(int X, int Y);"),
        ["tuple argument"] = Member("Task<string> Get((int, string) key)", "Task<string> Get((int, string) key) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get((1, \"a\"))", "await s.Get((1, \"b\"))"),
        ["many parameters"] = Member("Task<string> Get(int a, int b, int c, int d, int e, int f, int g, int h, int i, int j)", "Task<string> Get(int a, int b, int c, int d, int e, int f, int g, int h, int i, int j) { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1, 2, 3, 4, 5, 6, 7, 8, 9, 10)", "await s.Get(1, 2, 3, 4, 5, 6, 7, 8, 9, 11)"),
        ["no parameters"] = new("""
                public interface ISvc
                {
                    [Cache] Task<string> GetAll();
                    [Cache] Task<string> GetOther();
                }

                public class Svc : ISvc
                {
                    public Task<string> GetAll() { Counter.Hit(); return Task.FromResult("a"); }
                    public Task<string> GetOther() { Counter.Hit(); return Task.FromResult("b"); }
                }
            """, "ISvc", "await s.GetAll()", "await s.GetOther()"),

        // Overloads and generics
        ["overloads with equal type names in different namespaces"] = new("""
                namespace A { public sealed record Request(int Id); }
                namespace B { public sealed record Request(int Id); }
                public interface ISvc
                {
                    [Cache] Task<string> Get(A.Request request);
                    [Cache] Task<string> Get(B.Request request);
                }
                public class Svc : ISvc
                {
                    public Task<string> Get(A.Request request) { Counter.Hit(); return Task.FromResult("A"); }
                    public Task<string> Get(B.Request request) { Counter.Hit(); return Task.FromResult("B"); }
                }
            """, "ISvc", "await s.Get(new A.Request(1))", "await s.Get(new B.Request(1))"),
        ["overloads"] = new("""
                public interface ISvc
                {
                    [Cache] Task<string> Get(int id);
                    [Cache] Task<string> Get(string id);
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("int"); }
                    public Task<string> Get(string id) { Counter.Hit(); return Task.FromResult("string"); }
                }
            """, "ISvc", "await s.Get(1)", "await s.Get(\"1\")"),
        ["overloads by arity"] = new("""
                public interface ISvc
                {
                    [Cache] Task<string> Get(int id);
                    [Cache] Task<string> Get(int id, int version);
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("1"); }
                    public Task<string> Get(int id, int version) { Counter.Hit(); return Task.FromResult("2"); }
                }
            """, "ISvc", "await s.Get(1)", "await s.Get(1, 0)"),
        ["generic method"] = Member("Task<T?> Get<T>(int id)", "Task<T?> Get<T>(int id) { Counter.Hit(); return Task.FromResult<T?>(default); }", "await s.Get<int>(1)", "await s.Get<string>(1)"),
        ["generic method with class constraint"] = Member("Task<T?> Get<T>(int id) where T : class", "Task<T?> Get<T>(int id) where T : class { Counter.Hit(); return Task.FromResult<T?>(null); }", "await s.Get<string>(1)", "await s.Get<object>(1)"),
        ["generic method with struct constraint"] = Member("Task<T> Get<T>(int id) where T : struct", "Task<T> Get<T>(int id) where T : struct { Counter.Hit(); return Task.FromResult(default(T)); }", "await s.Get<int>(1)", "await s.Get<long>(1)"),
        ["generic method with unmanaged constraint"] = Member("Task<T> Get<T>(int id) where T : unmanaged", "Task<T> Get<T>(int id) where T : unmanaged { Counter.Hit(); return Task.FromResult(default(T)); }", "await s.Get<int>(1)", "await s.Get<long>(1)"),
        ["generic interface with unmanaged constraint"] = new("""
                public interface ISvc<T> where T : unmanaged
                {
                    [Cache] Task<T> Get(int id);
                }
                public class Svc : ISvc<int>
                {
                    public Task<int> Get(int id) { Counter.Hit(); return Task.FromResult(id); }
                }
            """, "ISvc<int>", "await s.Get(1)", "await s.Get(2)"),
        ["generic method with interface constraint"] = Member("Task<string> Get<T>(T value) where T : IComparable<T>", "Task<string> Get<T>(T value) where T : IComparable<T> { Counter.Hit(); return Task.FromResult(\"v\"); }", "await s.Get(1)", "await s.Get(2)"),
        ["generic constraint from another namespace"] = new("""
                public interface ISvc
                {
                    [Cache] Task<string> Get<T>(int id) where T : Other.IEntity;
                }

                public class Svc : ISvc
                {
                    public Task<string> Get<T>(int id) where T : Other.IEntity { Counter.Hit(); return Task.FromResult("v"); }
                }
            }

            namespace Other
            {
                public interface IEntity { }
                public class A : IEntity { }
                public class B : IEntity { }
            }

            namespace Corpus
            {
            """, "ISvc", "await s.Get<Other.A>(1)", "await s.Get<Other.B>(1)"),
        ["generic interface"] = new("""
                public interface IRepo<T>
                {
                    [Cache] Task<T?> Get(int id);
                }

                public class Svc : IRepo<string>
                {
                    public Task<string?> Get(int id) { Counter.Hit(); return Task.FromResult<string?>("v" + id); }
                }
            """, "IRepo<string>", "await s.Get(1)", "await s.Get(2)"),

        ["generic interface with two closed types"] = new("""
                public interface IRepo<T>
                {
                    [Cache] Task<T?> Get(int id);
                }

                public class Svc : IRepo<int>
                {
                    public Task<int> Get(int id) { Counter.Hit(); return Task.FromResult(id); }
                }

                public class Names : IRepo<string>
                {
                    public Task<string?> Get(int id) => Task.FromResult<string?>("n" + id);
                }
            """, "IRepo<int>", "await s.Get(1)", "await s.Get(2)"),

        ["generic interface implemented by a private class registered through a factory"] = new("""
                public interface IRepo<T>
                {
                    [Cache] Task<T?> Get(int id);
                }

                public static class Repos
                {
                    public static IRepo<string> Create() => new Svc();

                    private sealed class Svc : IRepo<string>
                    {
                        public Task<string?> Get(int id) { Counter.Hit(); return Task.FromResult<string?>("v" + id); }
                    }
                }
            """, "IRepo<string>", "await s.Get(1)", "await s.Get(2)",
            Registrations: "services.AddSingleton<IRepo<string>>(_ => Repos.Create());"),

        // Interface shapes
        ["internal interface"] = new("""
                internal interface ISvc
                {
                    [Cache] Task<string> Get(int id);
                }

                internal class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                }
            """, "ISvc", "await s.Get(1)", "await s.Get(2)"),
        ["nested interface"] = new("""
                public static class Outer
                {
                    public interface ISvc
                    {
                        [Cache] Task<string> Get(int id);
                    }

                    public class Svc : ISvc
                    {
                        public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                    }
                }
            """, "Outer.ISvc", "await s.Get(1)", "await s.Get(2)"),
        ["same interface name in two namespaces"] = new("""
                public interface ISvc
                {
                    [Cache] Task<string> Get(int id);
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                }
            }

            namespace Corpus.Other
            {
                public interface ISvc
                {
                    [MethodCache.Cache] Task<string> Get(int id);
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) => Task.FromResult("other");
                }
            }

            namespace Corpus
            {
            """, "ISvc", "await s.Get(1)", "await s.Get(2)"),
        ["generic and non-generic interface with the same name"] = new("""
                public interface IRepo
                {
                    [Cache] Task<string> Get(int id);
                }

                public interface IRepo<T>
                {
                    [Cache] Task<T?> Find(int id);
                }

                public class Svc : IRepo
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                }

                public class Repo : IRepo<string>
                {
                    public Task<string?> Find(int id) => Task.FromResult<string?>("v");
                }
            """, "IRepo", "await s.Get(1)", "await s.Get(2)"),
        ["derived interface without own attributes"] = new("""
                public interface IRepo<T>
                {
                    [Cache] Task<T?> Get(int id);
                }

                public record User(int Id);

                public interface IUserRepo : IRepo<User> { }

                public class Svc : IUserRepo
                {
                    public Task<User?> Get(int id) { Counter.Hit(); return Task.FromResult<User?>(new User(id)); }
                }
            """, "IUserRepo", "await s.Get(1)", "await s.Get(2)"),
        ["same method in two base interfaces"] = new("""
                public interface IReader
                {
                    [Cache] Task<string> Get(int id);
                }

                public interface IOtherReader
                {
                    Task<string> Get(int id);
                }

                public interface ISvc : IReader, IOtherReader { }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                }
            """, "ISvc", "await ((IReader)s).Get(1)", "await ((IReader)s).Get(2)"),
        ["interface with properties, events and uncached members"] = new("""
                public interface ISvc
                {
                    string Name { get; set; }
                    int this[int index] { get; }
                    event EventHandler? Changed;
                    bool TryFind(int id, out string value);
                    void Touch(ref int counter);
                    Task SaveAsync(int id);

                    [Cache] Task<string> Get(int id);
                }

                public class Svc : ISvc
                {
                    public string Name { get; set; } = "";
                    public int this[int index] => index;
                    public event EventHandler? Changed;
                    public bool TryFind(int id, out string value) { value = "v"; Changed?.Invoke(this, EventArgs.Empty); return true; }
                    public void Touch(ref int counter) => counter++;
                    public Task SaveAsync(int id) => Task.CompletedTask;
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                }
            """, "ISvc", "await s.Get(1)", "await s.Get(2)"),
        ["default interface method"] = new("""
                public interface ISvc
                {
                    [Cache] Task<string> Get(int id);
                    string Describe() => "svc";
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                }
            """, "ISvc", "await s.Get(1)", "await s.Get(2)"),
        ["disposable service"] = new("""
                public interface ISvc : IDisposable
                {
                    [Cache] Task<string> Get(int id);
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                    public void Dispose() { }
                }
            """, "ISvc", "await s.Get(1)", "await s.Get(2)"),
        ["nullable disabled"] = new("""
            #nullable disable
                public interface ISvc
                {
                    [Cache] Task<string> Get(string id);
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(string id) { Counter.Hit(); return Task.FromResult(id); }
                }
            #nullable restore
            """, "ISvc", "await s.Get(\"a\")", "await s.Get(null)"),

        // Attribute settings
        ["tags with escapes"] = new("""
                public interface ISvc
                {
                    [Cache(Tags = new[] { "C:\\data", "tab\there", "quote\"d" })] Task<string> Get(int id);
                    [CacheInvalidate(Tags = new[] { "C:\\data" })] Task Clear();
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                    public Task Clear() => Task.CompletedTask;
                }
            """, "ISvc", "await s.Get(1)", "await s.Clear(); await s.Get(1)"),
        ["dynamic tags"] = new("""
                public interface ISvc
                {
                    [Cache(Tags = new[] { "user:{userId}" })] Task<string> Get(int userId);
                    [CacheInvalidate(Tags = new[] { "user:{userId}" })] Task Update(int userId);
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int userId) { Counter.Hit(); return Task.FromResult("v"); }
                    public Task Update(int userId) => Task.CompletedTask;
                }
            """, "ISvc", "await s.Get(1)", "await s.Update(1); await s.Get(1)"),
        ["CacheInvalidate on ValueTask"] = new("""
                public interface ISvc
                {
                    [Cache(Tags = new[] { "all" })] Task<string> Get(int id);
                    [CacheInvalidate(Tags = new[] { "all" })] ValueTask Clear();
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                    public ValueTask Clear() => default;
                }
            """, "ISvc", "await s.Get(1)", "await s.Clear(); await s.Get(1)"),
        ["CacheInvalidate on sync void"] = new("""
                public interface ISvc
                {
                    [Cache(Tags = new[] { "all" })] Task<string> Get(int id);
                    [CacheInvalidate(Tags = new[] { "all" })] void Clear();
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                    public void Clear() { }
                }
            """, "ISvc", "await s.Get(1)", "s.Clear(); await s.Get(1)"),
        ["Duration and sliding settings"] = new("""
                public interface ISvc
                {
                    [Cache(Duration = "00:05:00", SlidingExpiration = "00:01:00", Tags = new[] { "t" })] Task<string> Get(int id);
                }

                public class Svc : ISvc
                {
                    public Task<string> Get(int id) { Counter.Hit(); return Task.FromResult("v"); }
                }
            """, "ISvc", "await s.Get(1)", "await s.Get(2)"),
    };
}
