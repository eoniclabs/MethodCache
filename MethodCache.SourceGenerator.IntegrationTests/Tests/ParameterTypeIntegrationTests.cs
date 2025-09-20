using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Integration tests for various parameter type scenarios with real source-generated code
/// </summary>
public class ParameterTypeIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly SourceGeneratorTestEngine _engine;

    public ParameterTypeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _engine = new SourceGeneratorTestEngine();
    }

    [Fact]
    public async Task SourceGenerator_PrimitiveParameterTypes_Works()
    {
        var sourceCode = @"
using System;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public enum Priority
    {
        Low = 1,
        Medium = 2,
        High = 3
    }

    public interface IPrimitiveParameterService
    {
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessIntAsync(int value);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessStringAsync(string text);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessBoolAsync(bool flag);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessDecimalAsync(decimal amount);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessDateTimeAsync(DateTime timestamp);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessEnumAsync(Priority priority);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessGuidAsync(Guid id);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessMultiplePrimitivesAsync(int id, string name, bool active, decimal value);
    }

    public class PrimitiveParameterService : IPrimitiveParameterService
    {
        private static int _intCallCount = 0;
        private static int _stringCallCount = 0;
        private static int _boolCallCount = 0;
        private static int _decimalCallCount = 0;
        private static int _dateTimeCallCount = 0;
        private static int _enumCallCount = 0;
        private static int _guidCallCount = 0;
        private static int _multipleCallCount = 0;
        
        public virtual async Task<string> ProcessIntAsync(int value)
        {
            _intCallCount++;
            await Task.Delay(5);
            return $""Processed int: {value}"";
        }
        
        public virtual async Task<string> ProcessStringAsync(string text)
        {
            _stringCallCount++;
            await Task.Delay(5);
            return $""Processed string: {text}"";
        }
        
        public virtual async Task<string> ProcessBoolAsync(bool flag)
        {
            _boolCallCount++;
            await Task.Delay(5);
            return $""Processed bool: {flag}"";
        }
        
        public virtual async Task<string> ProcessDecimalAsync(decimal amount)
        {
            _decimalCallCount++;
            await Task.Delay(5);
            return $""Processed decimal: {amount}"";
        }
        
        public virtual async Task<string> ProcessDateTimeAsync(DateTime timestamp)
        {
            _dateTimeCallCount++;
            await Task.Delay(5);
            return $""Processed datetime: {timestamp:yyyy-MM-dd}"";
        }
        
        public virtual async Task<string> ProcessEnumAsync(Priority priority)
        {
            _enumCallCount++;
            await Task.Delay(5);
            return $""Processed enum: {priority}"";
        }
        
        public virtual async Task<string> ProcessGuidAsync(Guid id)
        {
            _guidCallCount++;
            await Task.Delay(5);
            return $""Processed guid: {id}"";
        }
        
        public virtual async Task<string> ProcessMultiplePrimitivesAsync(int id, string name, bool active, decimal value)
        {
            _multipleCallCount++;
            await Task.Delay(5);
            return $""Processed multiple: {id}, {name}, {active}, {value}"";
        }
        
        public static void ResetCallCounts()
        {
            _intCallCount = 0;
            _stringCallCount = 0;
            _boolCallCount = 0;
            _decimalCallCount = 0;
            _dateTimeCallCount = 0;
            _enumCallCount = 0;
            _guidCallCount = 0;
            _multipleCallCount = 0;
        }
        
        public static int IntCallCount => _intCallCount;
        public static int StringCallCount => _stringCallCount;
        public static int BoolCallCount => _boolCallCount;
        public static int DecimalCallCount => _decimalCallCount;
        public static int DateTimeCallCount => _dateTimeCallCount;
        public static int EnumCallCount => _enumCallCount;
        public static int GuidCallCount => _guidCallCount;
        public static int MultipleCallCount => _multipleCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.IPrimitiveParameterService");
        var service = serviceProvider.GetService(serviceType);
        Assert.NotNull(service);

        // Reset counters
        var implType = testAssembly.Assembly.GetType("TestNamespace.PrimitiveParameterService");
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var priorityType = testAssembly.Assembly.GetType("TestNamespace.Priority");

        // Test various primitive types
        var processIntMethod = serviceType!.GetMethod("ProcessIntAsync");
        var intTask1 = (Task)processIntMethod!.Invoke(service, new object[] { 42 })!;
        var intResult1 = await GetTaskResult<string>(intTask1);
        var intTask2 = (Task)processIntMethod.Invoke(service, new object[] { 42 })!;
        var intResult2 = await GetTaskResult<string>(intTask2);

        var processStringMethod = serviceType.GetMethod("ProcessStringAsync");
        var stringTask1 = (Task)processStringMethod!.Invoke(service, new object[] { "test" })!;
        var stringResult1 = await GetTaskResult<string>(stringTask1);
        var stringTask2 = (Task)processStringMethod.Invoke(service, new object[] { "test" })!;
        var stringResult2 = await GetTaskResult<string>(stringTask2);

        var processDateTimeMethod = serviceType.GetMethod("ProcessDateTimeAsync");
        var testDate = new DateTime(2023, 1, 1);
        var dateTask1 = (Task)processDateTimeMethod!.Invoke(service, new object[] { testDate })!;
        var dateResult1 = await GetTaskResult<string>(dateTask1);
        var dateTask2 = (Task)processDateTimeMethod.Invoke(service, new object[] { testDate })!;
        var dateResult2 = await GetTaskResult<string>(dateTask2);

        var processEnumMethod = serviceType.GetMethod("ProcessEnumAsync");
        var highPriority = Enum.ToObject(priorityType!, 3); // Priority.High
        var enumTask1 = (Task)processEnumMethod!.Invoke(service, new object[] { highPriority })!;
        var enumResult1 = await GetTaskResult<string>(enumTask1);
        var enumTask2 = (Task)processEnumMethod.Invoke(service, new object[] { highPriority })!;
        var enumResult2 = await GetTaskResult<string>(enumTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 4, expectedMisses: 4);
        
        // Verify caching worked for all primitive types
        var intCallCount = (int)implType?.GetProperty("IntCallCount")?.GetValue(null)!;
        var stringCallCount = (int)implType?.GetProperty("StringCallCount")?.GetValue(null)!;
        var dateTimeCallCount = (int)implType?.GetProperty("DateTimeCallCount")?.GetValue(null)!;
        var enumCallCount = (int)implType?.GetProperty("EnumCallCount")?.GetValue(null)!;
        
        Assert.Equal(1, intCallCount);
        Assert.Equal(1, stringCallCount);
        Assert.Equal(1, dateTimeCallCount);
        Assert.Equal(1, enumCallCount);

        // Verify results are consistent
        Assert.Equal(intResult1, intResult2);
        Assert.Equal(stringResult1, stringResult2);
        Assert.Equal(dateResult1, dateResult2);
        Assert.Equal(enumResult1, enumResult2);

        _output.WriteLine($"✅ Primitive parameter types test passed! Caching works with int, string, DateTime, enum, etc.");
    }

    [Fact]
    public async Task SourceGenerator_ComplexParameterTypes_Works()
    {
        var sourceCode = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public class Address
    {
        public string Street { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string ZipCode { get; set; } = string.Empty;
        
        public override bool Equals(object? obj)
        {
            return obj is Address addr && Street == addr.Street && City == addr.City && ZipCode == addr.ZipCode;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(Street, City, ZipCode);
        }
        
        public override string ToString()
        {
            return $""{Street}, {City} {ZipCode}"";
        }
    }

    public class Person
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public Address Address { get; set; } = new Address();
        
        public override bool Equals(object? obj)
        {
            return obj is Person person && Name == person.Name && Age == person.Age && Address.Equals(person.Address);
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(Name, Age, Address);
        }
        
        public override string ToString()
        {
            return $""{Name}, {Age}, {Address}"";
        }
    }

    public interface IComplexParameterService
    {
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessPersonAsync(Person person);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessAddressAsync(Address address);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessArrayAsync(int[] numbers);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessMultipleObjectsAsync(Person person, Address workAddress);
    }

    public class ComplexParameterService : IComplexParameterService
    {
        private static int _personCallCount = 0;
        private static int _addressCallCount = 0;
        private static int _arrayCallCount = 0;
        private static int _multipleCallCount = 0;
        
        public virtual async Task<string> ProcessPersonAsync(Person person)
        {
            _personCallCount++;
            await Task.Delay(5);
            return $""Processed person: {person}"";
        }
        
        public virtual async Task<string> ProcessAddressAsync(Address address)
        {
            _addressCallCount++;
            await Task.Delay(5);
            return $""Processed address: {address}"";
        }
        
        public virtual async Task<string> ProcessArrayAsync(int[] numbers)
        {
            _arrayCallCount++;
            await Task.Delay(5);
            return $""Processed array: [{string.Join("", "", numbers)}]"";
        }
        
        public virtual async Task<string> ProcessMultipleObjectsAsync(Person person, Address workAddress)
        {
            _multipleCallCount++;
            await Task.Delay(8);
            return $""Processed multiple: {person} | Work: {workAddress}"";
        }
        
        public static void ResetCallCounts()
        {
            _personCallCount = 0;
            _addressCallCount = 0;
            _arrayCallCount = 0;
            _multipleCallCount = 0;
        }
        
        public static int PersonCallCount => _personCallCount;
        public static int AddressCallCount => _addressCallCount;
        public static int ArrayCallCount => _arrayCallCount;
        public static int MultipleCallCount => _multipleCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.IComplexParameterService");
        var service = serviceProvider.GetService(serviceType);
        Assert.NotNull(service);

        // Reset counters
        var implType = testAssembly.Assembly.GetType("TestNamespace.ComplexParameterService");
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        var personType = testAssembly.Assembly.GetType("TestNamespace.Person");
        var addressType = testAssembly.Assembly.GetType("TestNamespace.Address");

        // Create test objects
        var address = Activator.CreateInstance(addressType!)!;
        addressType!.GetProperty("Street")!.SetValue(address, "123 Main St");
        addressType.GetProperty("City")!.SetValue(address, "Test City");
        addressType.GetProperty("ZipCode")!.SetValue(address, "12345");

        var person = Activator.CreateInstance(personType!)!;
        personType!.GetProperty("Name")!.SetValue(person, "John Doe");
        personType.GetProperty("Age")!.SetValue(person, 30);
        personType.GetProperty("Address")!.SetValue(person, address);

        // Test complex object caching
        var processPersonMethod = serviceType!.GetMethod("ProcessPersonAsync");
        var personTask1 = (Task)processPersonMethod!.Invoke(service, new object[] { person })!;
        var personResult1 = await GetTaskResult<string>(personTask1);
        var personTask2 = (Task)processPersonMethod.Invoke(service, new object[] { person })!;
        var personResult2 = await GetTaskResult<string>(personTask2);

        // Test array caching
        var processArrayMethod = serviceType.GetMethod("ProcessArrayAsync");
        var testArray = new int[] { 1, 2, 3, 4, 5 };
        var arrayTask1 = (Task)processArrayMethod!.Invoke(service, new object[] { testArray })!;
        var arrayResult1 = await GetTaskResult<string>(arrayTask1);
        var arrayTask2 = (Task)processArrayMethod.Invoke(service, new object[] { testArray })!;
        var arrayResult2 = await GetTaskResult<string>(arrayTask2);

        // Test multiple objects caching
        var processMultipleMethod = serviceType.GetMethod("ProcessMultipleObjectsAsync");
        var workAddress = (object)Activator.CreateInstance(addressType)!;
        addressType.GetProperty("Street")!.SetValue(workAddress, "456 Work St");
        addressType.GetProperty("City")!.SetValue(workAddress, "Business City");
        addressType.GetProperty("ZipCode")!.SetValue(workAddress, "67890");
        
        var multipleTask1 = (Task)processMultipleMethod!.Invoke(service, new object[] { person, workAddress })!;
        var multipleResult1 = await GetTaskResult<string>(multipleTask1);
        var multipleTask2 = (Task)processMultipleMethod.Invoke(service, new object[] { person, workAddress })!;
        var multipleResult2 = await GetTaskResult<string>(multipleTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 3, expectedMisses: 3);
        
        // Verify caching worked for complex types
        var personCallCount = (int)implType?.GetProperty("PersonCallCount")?.GetValue(null)!;
        var arrayCallCount = (int)implType?.GetProperty("ArrayCallCount")?.GetValue(null)!;
        var multipleCallCount = (int)implType?.GetProperty("MultipleCallCount")?.GetValue(null)!;
        
        Assert.Equal(1, personCallCount);
        Assert.Equal(1, arrayCallCount);
        Assert.Equal(1, multipleCallCount);

        // Verify results are consistent
        Assert.Equal(personResult1, personResult2);
        Assert.Equal(arrayResult1, arrayResult2);
        Assert.Equal(multipleResult1, multipleResult2);

        _output.WriteLine($"✅ Complex parameter types test passed! Caching works with objects, arrays, multiple parameters");
    }

    [Fact]
    public async Task SourceGenerator_OptionalAndDefaultParameters_Works()
    {
        var sourceCode = @"
using System;
using System.Threading.Tasks;
using MethodCache.Core;

namespace TestNamespace
{
    public interface IOptionalParameterService
    {
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessWithOptionalAsync(int required, string optional = ""default"");
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessWithMultipleOptionalAsync(string name, int age = 25, bool active = true, decimal salary = 50000m);
        
        [Cache(Duration = ""00:02:00"")]
        Task<string> ProcessWithNullableAsync(int id, string? optionalText = null, DateTime? optionalDate = null);
    }

    public class OptionalParameterService : IOptionalParameterService
    {
        private static int _optionalCallCount = 0;
        private static int _multipleOptionalCallCount = 0;
        private static int _nullableCallCount = 0;
        
        public virtual async Task<string> ProcessWithOptionalAsync(int required, string optional = ""default"")
        {
            _optionalCallCount++;
            await Task.Delay(5);
            return $""Required: {required}, Optional: {optional}"";
        }
        
        public virtual async Task<string> ProcessWithMultipleOptionalAsync(string name, int age = 25, bool active = true, decimal salary = 50000m)
        {
            _multipleOptionalCallCount++;
            await Task.Delay(5);
            return $""Name: {name}, Age: {age}, Active: {active}, Salary: {salary}"";
        }
        
        public virtual async Task<string> ProcessWithNullableAsync(int id, string? optionalText = null, DateTime? optionalDate = null)
        {
            _nullableCallCount++;
            await Task.Delay(5);
            return $""ID: {id}, Text: {optionalText ?? ""null""}, Date: {optionalDate?.ToString(""yyyy-MM-dd"") ?? ""null""}"";
        }
        
        public static void ResetCallCounts()
        {
            _optionalCallCount = 0;
            _multipleOptionalCallCount = 0;
            _nullableCallCount = 0;
        }
        
        public static int OptionalCallCount => _optionalCallCount;
        public static int MultipleOptionalCallCount => _multipleOptionalCallCount;
        public static int NullableCallCount => _nullableCallCount;
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(sourceCode);
        var metricsProvider = new TestCacheMetricsProvider();
        var serviceProvider = _engine.CreateTestServiceProvider(testAssembly, services =>
        {
            services.AddSingleton<ICacheMetricsProvider>(metricsProvider);
        });

        var serviceType = testAssembly.Assembly.GetType("TestNamespace.IOptionalParameterService");
        var service = serviceProvider.GetService(serviceType);
        Assert.NotNull(service);

        // Reset counters
        var implType = testAssembly.Assembly.GetType("TestNamespace.OptionalParameterService");
        implType?.GetMethod("ResetCallCounts")?.Invoke(null, null);
        metricsProvider.Reset();

        // Test optional parameters with different values
        var processOptionalMethod = serviceType!.GetMethod("ProcessWithOptionalAsync");
        
        // Call with default parameter (should generate specific cache key)
        var optionalTask1 = (Task)processOptionalMethod!.Invoke(service, new object[] { 1, "default" })!;
        var optionalResult1 = await GetTaskResult<string>(optionalTask1);
        var optionalTask2 = (Task)processOptionalMethod.Invoke(service, new object[] { 1, "default" })!;
        var optionalResult2 = await GetTaskResult<string>(optionalTask2);

        // Call with different optional parameter (should generate different cache key)
        var optionalTask3 = (Task)processOptionalMethod.Invoke(service, new object[] { 1, "custom" })!;
        var optionalResult3 = await GetTaskResult<string>(optionalTask3);

        // Test nullable parameters
        var processNullableMethod = serviceType.GetMethod("ProcessWithNullableAsync");
        var nullableTask1 = (Task)processNullableMethod!.Invoke(service, new object[] { 1, null, null })!;
        var nullableResult1 = await GetTaskResult<string>(nullableTask1);
        var nullableTask2 = (Task)processNullableMethod.Invoke(service, new object[] { 1, null, null })!;
        var nullableResult2 = await GetTaskResult<string>(nullableTask2);

        await metricsProvider.WaitForMetricsAsync(expectedHits: 2, expectedMisses: 3);
        
        // Verify caching worked correctly with optional parameters
        var optionalCallCount = (int)implType?.GetProperty("OptionalCallCount")?.GetValue(null)!;
        var nullableCallCount = (int)implType?.GetProperty("NullableCallCount")?.GetValue(null)!;
        
        Assert.Equal(2, optionalCallCount); // Two different parameter combinations
        Assert.Equal(1, nullableCallCount); // Same nullable parameter combination

        // Verify results are consistent for same parameters
        Assert.Equal(optionalResult1, optionalResult2);
        Assert.Equal(nullableResult1, nullableResult2);
        Assert.NotEqual(optionalResult1, optionalResult3); // Different optional value

        _output.WriteLine($"✅ Optional and default parameters test passed! Caching distinguishes between different optional values");
    }

    private static async Task<T> GetTaskResult<T>(Task task)
    {
        await task;
        var property = task.GetType().GetProperty("Result");
        return (T)property!.GetValue(task)!;
    }
}