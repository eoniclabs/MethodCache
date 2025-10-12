using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core;
using MethodCache.Core.Infrastructure;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Integration tests for inheritance and polymorphism scenarios with real source-generated code
/// </summary>
public class InheritanceIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine;

    public InheritanceIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _engine = new SourceGeneratorTestEngine();
    }

    [Fact]
    public async Task SourceGenerator_SimpleInheritance_Works()
    {
        var sourceCode = @"
using System;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    // Simple interface with caching methods
    public interface IUserService
    {
        [Cache(Duration = ""00:02:00"", Tags = new[] { ""users"" })]
        Task<User> GetByIdAsync(int id);
        
        [Cache(Duration = ""00:01:00"", Tags = new[] { ""users"" })]
        Task<User> GetByEmailAsync(string email);
        
        [CacheInvalidate(Tags = new[] { ""users"" })]
        Task UpdateUserAsync(User user);
    }

    public class UserService : IUserService
    {
        private static int _getByIdCallCount = 0;
        private static int _getByEmailCallCount = 0;
        
        public virtual async Task<User> GetByIdAsync(int id)
        {
            _getByIdCallCount++;
            await Task.Delay(5);
            return new User { Id = id, Name = $""User {id}"", Email = $""user{id}@test.com"" };
        }
        
        public virtual async Task<User> GetByEmailAsync(string email)
        {
            _getByEmailCallCount++;
            await Task.Delay(5);
            var id = email.GetHashCode() % 1000;
            return new User { Id = id, Name = $""User for {email}"", Email = email };
        }
        
        public virtual async Task UpdateUserAsync(User user)
        {
            await Task.Delay(5);
        }
        
        public static void ResetCallCounts()
        {
            _getByIdCallCount = 0;
            _getByEmailCallCount = 0;
        }
        
        public static int GetByIdCallCount => _getByIdCallCount;
        public static int GetByEmailCallCount => _getByEmailCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.IUserService");
        Assert.NotNull(serviceType);
        var service = serviceProvider.GetService(serviceType);
        Assert.NotNull(service);

        // Reset counters
        var implType = testAssembly.Assembly.GetType("TestNamespace.UserService");
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var userType = testAssembly.Assembly.GetType("TestNamespace.User");
        Assert.NotNull(userType);

        // Test basic caching
        var getByIdMethod = serviceType!.GetMethod("GetByIdAsync");
        var getByEmailMethod = serviceType.GetMethod("GetByEmailAsync");
        var updateUserMethod = serviceType.GetMethod("UpdateUserAsync");

        Assert.NotNull(getByIdMethod);
        Assert.NotNull(getByEmailMethod);
        Assert.NotNull(updateUserMethod);

        // Test GetByIdAsync caching
        var task1 = (Task)getByIdMethod!.Invoke(service, new object[] { 1 })!;
        var user1 = await GetTaskResult(task1, userType);
        
        var task2 = (Task)getByIdMethod.Invoke(service, new object[] { 1 })!;
        var user2 = await GetTaskResult(task2, userType);

        // Test GetByEmailAsync caching
        var emailTask1 = (Task)getByEmailMethod!.Invoke(service, new object[] { "test@example.com" })!;
        var emailUser1 = await GetTaskResult(emailTask1, userType);
        
        var emailTask2 = (Task)getByEmailMethod.Invoke(service, new object[] { "test@example.com" })!;
        var emailUser2 = await GetTaskResult(emailTask2, userType);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 2);
        
        // Verify caching worked
        var getByIdCallCount = (int)implType?.GetProperty("GetByIdCallCount")?.GetValue(null)!;
        var getByEmailCallCount = (int)implType?.GetProperty("GetByEmailCallCount")?.GetValue(null)!;
        
        Assert.Equal(1, getByIdCallCount);
        Assert.Equal(1, getByEmailCallCount);

        // Test invalidation
        var dummyUser = Activator.CreateInstance(userType)!;
        var updateTask = (Task)updateUserMethod!.Invoke(service, new object[] { dummyUser })!;
        await updateTask;

        // Verify invalidation metrics
        Assert.True(metricsProvider.Metrics.TagInvalidations.ContainsKey("users"));

        _output.WriteLine($"✅ Simple inheritance test passed! Basic caching and invalidation works");
    }

    [Fact]
    public async Task SourceGenerator_MultipleInterfaceImplementation_Works()
    {
        var sourceCode = @"
using System;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public class Document
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    public interface IDocumentReader
    {
        [Cache(Duration = ""00:02:00"", Tags = new[] { ""documents"", ""read"" })]
        Task<Document> ReadDocumentAsync(int id);
        
        [Cache(Duration = ""00:01:00"", Tags = new[] { ""documents"", ""search"" })]
        Task<Document[]> SearchDocumentsAsync(string query);
    }

    public interface IDocumentWriter
    {
        [CacheInvalidate(Tags = new[] { ""documents"" })]
        Task<Document> CreateDocumentAsync(Document document);
        
        [CacheInvalidate(Tags = new[] { ""documents"" })]
        Task UpdateDocumentAsync(Document document);
    }

    public interface IDocumentProcessor
    {
        [Cache(Duration = ""00:03:00"", Tags = new[] { ""documents"", ""processing"" })]
        Task<string> ProcessDocumentAsync(int documentId, string operation);
    }

    // Service implementing multiple interfaces
    public class DocumentService : IDocumentReader, IDocumentWriter, IDocumentProcessor
    {
        private static int _readCallCount = 0;
        private static int _searchCallCount = 0;
        private static int _createCallCount = 0;
        private static int _updateCallCount = 0;
        private static int _processCallCount = 0;
        
        public virtual async Task<Document> ReadDocumentAsync(int id)
        {
            _readCallCount++;
            await Task.Delay(5);
            return new Document { Id = id, Title = $""Document {id}"", Content = $""Content for doc {id}"" };
        }
        
        public virtual async Task<Document[]> SearchDocumentsAsync(string query)
        {
            _searchCallCount++;
            await Task.Delay(10);
            return new Document[]
            {
                new Document { Id = 1, Title = $""Result 1 for {query}"", Content = ""Content 1"" },
                new Document { Id = 2, Title = $""Result 2 for {query}"", Content = ""Content 2"" }
            };
        }
        
        public virtual async Task<Document> CreateDocumentAsync(Document document)
        {
            _createCallCount++;
            await Task.Delay(5);
            document.Id = new Random().Next(1000, 9999);
            return document;
        }
        
        public virtual async Task UpdateDocumentAsync(Document document)
        {
            _updateCallCount++;
            await Task.Delay(5);
        }
        
        public virtual async Task<string> ProcessDocumentAsync(int documentId, string operation)
        {
            _processCallCount++;
            await Task.Delay(8);
            return $""Processed document {documentId} with operation: {operation}"";
        }
        
        public static void ResetCallCounts()
        {
            _readCallCount = 0;
            _searchCallCount = 0;
            _createCallCount = 0;
            _updateCallCount = 0;
            _processCallCount = 0;
        }
        
        public static int ReadCallCount => _readCallCount;
        public static int SearchCallCount => _searchCallCount;
        public static int CreateCallCount => _createCallCount;
        public static int UpdateCallCount => _updateCallCount;
        public static int ProcessCallCount => _processCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        // Get each interface separately to test multiple interface implementation
        var readerType = testAssembly.Assembly.GetType("TestNamespace.IDocumentReader");
        var writerType = testAssembly.Assembly.GetType("TestNamespace.IDocumentWriter");
        var processorType = testAssembly.Assembly.GetType("TestNamespace.IDocumentProcessor");

        Assert.NotNull(readerType);
        Assert.NotNull(writerType);
        Assert.NotNull(processorType);

        var readerService = serviceProvider.GetService(readerType);
        var writerService = serviceProvider.GetService(writerType);
        var processorService = serviceProvider.GetService(processorType);

        Assert.NotNull(readerService);
        Assert.NotNull(writerService);
        Assert.NotNull(processorService);

        // Reset counters
        var implType = testAssembly.Assembly.GetType("TestNamespace.DocumentService");
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var documentType = testAssembly.Assembly.GetType("TestNamespace.Document");
        Assert.NotNull(documentType);

        // Test caching across multiple interfaces
        var readMethod = readerType!.GetMethod("ReadDocumentAsync");
        var processMethod = processorType!.GetMethod("ProcessDocumentAsync");

        // Test read caching (IDocumentReader)
        var readTask1 = (Task)readMethod!.Invoke(readerService, new object[] { 1 })!;
        var doc1 = await GetTaskResult(readTask1, documentType);
        
        var readTask2 = (Task)readMethod.Invoke(readerService, new object[] { 1 })!;
        var doc2 = await GetTaskResult(readTask2, documentType);

        // Test process caching (IDocumentProcessor)
        var processTask1 = (Task)processMethod!.Invoke(processorService, new object[] { 1, "validate" })!;
        var result1 = await GetTaskResult<string>(processTask1);
        
        var processTask2 = (Task)processMethod.Invoke(processorService, new object[] { 1, "validate" })!;
        var result2 = await GetTaskResult<string>(processTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 2);
        
        // Verify caching worked independently for each interface
        var readCallCount = (int)implType?.GetProperty("ReadCallCount")?.GetValue(null)!;
        var processCallCount = (int)implType?.GetProperty("ProcessCallCount")?.GetValue(null)!;
        
        Assert.Equal(1, readCallCount);
        Assert.Equal(1, processCallCount);

        // Test cross-interface invalidation
        var createMethod = writerType!.GetMethod("CreateDocumentAsync");
        var newDoc = Activator.CreateInstance(documentType)!;
        var createTask = (Task)createMethod!.Invoke(writerService, new object[] { newDoc })!;
        await createTask;

        // Verify invalidation affected cached data
        Assert.True(metricsProvider.Metrics.TagInvalidations.ContainsKey("documents"));

        _output.WriteLine($"✅ Multiple interface implementation test passed! Caching works independently across interfaces");
    }

    private static async Task<T> GetTaskResult<T>(Task task)
    {
        await task;
        var property = task.GetType().GetProperty("Result");
        return (T)property!.GetValue(task)!;
    }

    private static async Task<object> GetTaskResult(Task task, Type expectedType)
    {
        await task;
        var property = task.GetType().GetProperty("Result");
        var result = property!.GetValue(task)!;
        
        Assert.True(expectedType.IsAssignableFrom(result.GetType()), 
            $"Expected type {expectedType.Name}, but got {result.GetType().Name}");
        
        return result;
    }
}