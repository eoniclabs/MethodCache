using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// The derived types MethodCache's generator registers are written with a <c>$type</c> name by the distributed
/// cache's JSON and restored as themselves, whatever options the application passes, while types that name their own
/// derived types keep doing so.
/// </summary>
public class DerivedTypeRegistryTests
{
    [Fact]
    public void WithoutOptions_ADerivedTypeInAList_ComesBackAsItself()
    {
        var serializer = new JsonCacheSerializer(null, Registry((typeof(Animal), [typeof(Dog), typeof(Cat)])));

        var (json, read) = RoundTrip<List<Animal>>(serializer, [new Dog { Name = "rex", Breed = "collie" }, new Cat { Name = "tom", Lives = 9 }, new Animal { Name = "x" }]);

        // Named by full name, with the + of a nested type escaped as JSON escapes it by default
        Assert.Contains($"\"$type\":{JsonSerializer.Serialize(typeof(Dog).FullName)}", json);
        Assert.Equal("collie", Assert.IsType<Dog>(read[0]).Breed);
        Assert.Equal(9, Assert.IsType<Cat>(read[1]).Lives);
        Assert.IsType<Animal>(read[2]);
        Assert.Equal(2, json.Split("$type").Length - 1);
    }

    [Fact]
    public void WithTheApplicationsOptions_TheyStillApply()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var serializer = new JsonCacheSerializer(options, Registry((typeof(Animal), [typeof(Dog)])));

        var (json, read) = RoundTrip<Animal>(serializer, new Dog { Name = "rex", Breed = "collie" });

        Assert.Contains("\"breed\":\"collie\"", json);
        Assert.Equal("collie", Assert.IsType<Dog>(read).Breed);
        Assert.Null(options.TypeInfoResolver);
    }

    [Fact]
    public void AnInterface_AndARecordWithAConstructor_ComeBackAsThemselves()
    {
        var serializer = new JsonCacheSerializer(null, Registry((typeof(IShape), [typeof(Circle)])));

        var (_, read) = RoundTrip<IShape[]>(serializer, [new Circle(2)]);

        Assert.Equal(2, Assert.IsType<Circle>(Assert.Single(read)).Radius);
    }

    [Fact]
    public void ATypeThatNamesItsDerivedTypes_KeepsItsOwnNames()
    {
        var serializer = new JsonCacheSerializer(null, Registry((typeof(Pet), [typeof(Poodle)])));

        var (json, read) = RoundTrip<Pet>(serializer, new Poodle { Name = "fifi" });

        Assert.Contains("\"$type\":\"poodle\"", json);
        Assert.IsType<Poodle>(read);
    }

    [Fact]
    public void ADerivedTypeThatIsNotRegistered_IsWrittenAsItsNearestRegisteredBase()
    {
        // Such as a proxy made at run time; the check before sharing then keeps the value in memory
        var serializer = new JsonCacheSerializer(null, Registry((typeof(Animal), [typeof(Dog)])));

        var (_, read) = RoundTrip<Animal>(serializer, new Puppy { Name = "rex", Breed = "collie", Age = 1 });

        Assert.Equal("collie", Assert.IsType<Dog>(read).Breed);
    }

    [Fact]
    public void WithMessagePack_ADerivedTypeInAList_ComesBackAsItself()
    {
        var serializer = new MethodCache.Serialization.MessagePack.MessagePackCacheSerializer(null, Registry((typeof(Animal), [typeof(Dog), typeof(Cat)])));

        var (_, read) = RoundTrip<List<Animal>>(serializer, [new Dog { Name = "rex", Breed = "collie" }, new Cat { Name = "tom", Lives = 9 }, new Animal { Name = "x" }, null!]);

        Assert.Equal("collie", Assert.IsType<Dog>(read[0]).Breed);
        Assert.Equal(9, Assert.IsType<Cat>(read[1]).Lives);
        Assert.Equal("x", Assert.IsType<Animal>(read[2]).Name);
        Assert.Null(read[3]);
    }

    [Fact]
    public void WithMessagePack_AnUnregisteredDerivedType_IsWrittenAsItsNearestRegisteredBase_AndAnUnknownNameIsNeverRead()
    {
        var serializer = new MethodCache.Serialization.MessagePack.MessagePackCacheSerializer(null, Registry((typeof(Animal), [typeof(Dog)])));
        var (_, read) = RoundTrip<Animal>(serializer, new Puppy { Name = "rex", Breed = "collie", Age = 1 });
        Assert.Equal("collie", Assert.IsType<Dog>(read).Breed);

        // An entry naming a type that isn't registered for Animal, such as one written by someone else
        var crafted = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePack.MessagePackWriter(crafted);
        writer.WriteArrayHeader(2);
        writer.Write(typeof(Cat).FullName);
        writer.WriteMapHeader(0);
        writer.Flush();
        var plain = new MethodCache.Serialization.MessagePack.MessagePackCacheSerializer(
            MessagePack.MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance),
            Registry((typeof(Animal), [typeof(Dog)])));
        Assert.Throws<MessagePack.MessagePackSerializationException>(() => plain.Deserialize<Animal>(new ReadOnlySequence<byte>(crafted.WrittenMemory)));
    }

    [Fact]
    public void WithMessagePack_ATypeThatNamesItsDerivedTypesWithUnion_KeepsItsOwnFormat()
    {
        var serializer = new MethodCache.Serialization.MessagePack.MessagePackCacheSerializer(null, Registry((typeof(Shape), [typeof(Square)])));

        var (_, read) = RoundTrip<Shape>(serializer, new Square { Side = 3 });

        Assert.Equal(3, Assert.IsType<Square>(read).Side);
    }

    [Fact]
    public void ATypeThatIsNotDerived_IsNotRegistered()
    {
        var module = new MethodCache.Generated.GeneratedModuleBuilder(new EmptyModule());
        module.DerivedTypes(typeof(Animal), typeof(Dog), typeof(string), typeof(Animal));

        Assert.Equal([typeof(Dog)], module.Derived[typeof(Animal)]);
    }

    private static DerivedTypeRegistry Registry(params (Type Declared, Type[] Derived)[] registrations)
    {
        var registry = new DerivedTypeRegistry();
        registry.Add(registrations.ToDictionary(static r => r.Declared, static r => r.Derived.ToHashSet()));
        return registry;
    }

    private static (string Json, T Read) RoundTrip<T>(ISerializer serializer, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(value, buffer);
        return (Encoding.UTF8.GetString(buffer.WrittenSpan), serializer.Deserialize<T>(new ReadOnlySequence<byte>(buffer.WrittenMemory))!);
    }

    private sealed class EmptyModule : MethodCache.Generated.GeneratedModule
    {
        public override void Describe(MethodCache.Generated.GeneratedModuleBuilder builder) { }
    }

    public class Animal { public string Name { get; set; } = ""; }
    public class Dog : Animal { public string Breed { get; set; } = ""; }
    public sealed class Puppy : Dog { public int Age { get; set; } }
    public sealed class Cat : Animal { public int Lives { get; set; } }
    public interface IShape;
    public sealed record Circle(double Radius) : IShape;

    [MessagePack.Union(0, typeof(Square))]
    public abstract class Shape;
    [MessagePack.MessagePackObject(AllowPrivate = true)]
    public sealed class Square : Shape { [MessagePack.Key(0)] public int Side { get; set; } }

    [JsonDerivedType(typeof(Poodle), "poodle")]
    public class Pet { public string Name { get; set; } = ""; }
    public sealed class Poodle : Pet;
}
