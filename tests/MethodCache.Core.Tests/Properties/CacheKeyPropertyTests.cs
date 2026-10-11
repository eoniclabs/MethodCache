using System.Globalization;
using System.Text;
using CsCheck;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

/// <summary>
/// Properties of cache keys: the default <see cref="FastHashKeyGenerator"/>, and the inline keys generated decorators
/// build with <see cref="MethodCacheKeys"/> and <see cref="MethodCacheKeyBuilder"/>.
/// </summary>
/// <remarks>
/// <para>Two calls that share a key get each other's cached result, so a key must identify its arguments: arguments
/// that differ must never share a key (that would return wrong data), and the same arguments must always give the
/// same key, on every thread culture (or nodes and requests would miss each other's entries and invalidations).</para>
/// <para>Each argument is described by a canonical text of its own (<see cref="Canon"/>), written independently of
/// the implementation. Two argument lists must share a key exactly when their canonical texts are equal. The canonical
/// text merges only values the documentation treats as the same argument:</para>
/// <list type="bullet">
/// <item>a <see cref="DateTimeKind.Local"/> <see cref="DateTime"/> and the same instant in UTC (keys convert local
/// times to UTC); an Unspecified and a UTC time with the same ticks stay different;</item>
/// <item><c>0.0</c> and <c>-0.0</c> for the hash generator (one key); the inline keys format doubles with "R", which
/// writes "-0", so there they are two keys, which only costs a cache miss;</item>
/// <item><c>0.00m</c> and <c>-0.00m</c> (decimal prints both as "0.00"). Decimals otherwise keep their scale:
/// <c>1.0m</c> and <c>1.00m</c> are two keys, as <see cref="decimal.ToString()"/> shows them.</item>
/// </list>
/// <para>Everything else is kept apart: the argument's type (<c>1</c>, <c>1L</c>, <c>"1"</c> and an enum with value 1
/// are four keys), the enum type, position and count of arguments, the method name and the version, a
/// <see cref="DateTimeOffset"/>'s offset, and string content compared ordinally.</para>
/// <para>Seeds and replay: see <see cref="PropertySettings"/>.</para>
/// </remarks>
public class CacheKeyPropertyTests
{
    public enum Color { Red = 1, Green = 2, Minus = -1 }

    public enum Size : long { Small = 1, Minus = -1, Min = long.MinValue }

    public enum Big : ulong { One = 1, Max = ulong.MaxValue }

    public sealed record Point(int X, string? Name);

    public sealed record Order(string? Id, Point? At, List<string?> Lines, Color Color);

    public sealed class Dto
    {
        public string? Text { get; set; }

        public int Number; // a public field counts too
    }

    private static readonly CacheRuntimePolicy NoVersion = CacheRuntimePolicy.Empty("Test.Method");

    // Small domains, so that equal and almost equal values come up often in one sample
    private static readonly Gen<string> GenText = Gen.String[Gen.Char["a:\\\0é𝄞İı"], 0, 3];
    private static readonly Gen<string?> GenNullableText = GenText.Null(0.2);
    private static readonly Gen<int> GenSmallInt = Gen.Int[-2, 2];

    private static readonly Gen<DateTime> GenDateTime =
        Gen.Select(Gen.Int[0, 2], Gen.OneOfConst(DateTimeKind.Utc, DateTimeKind.Unspecified, DateTimeKind.Local), (hours, kind) =>
        {
            var utc = new DateTime(2024, 3, 31, 0, 0, 0, DateTimeKind.Utc).AddHours(hours).AddTicks(hours == 2 ? 1 : 0);
            return kind switch
            {
                DateTimeKind.Local => utc.ToLocalTime(), // the same instant as the UTC value
                DateTimeKind.Unspecified => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified),
                _ => utc,
            };
        });

    private static readonly Gen<decimal> GenDecimal =
        Gen.Select(Gen.Int[-2, 2], Gen.Int[0, 2], (value, scale) =>
            new decimal(Math.Abs(value) * (int)Math.Pow(10, scale), 0, 0, value < 0 || (value == 0 && scale == 1), (byte)scale));

    private static readonly Gen<double> GenDouble = Gen.OneOfConst(0.0, -0.0, 1.0, -1.0, 0.1, 1e-300, double.NaN, double.PositiveInfinity);

    private static readonly Gen<Point?> GenPoint = Gen.Select(GenSmallInt, GenNullableText, (x, name) => new Point(x, name)).Null(0.2)!;

    private static readonly Gen<object> GenOrder =
        Gen.Select(GenNullableText, GenPoint, GenNullableText.List[0, 2], Gen.Enum<Color>(),
            (id, at, lines, color) => (object)new Order(id, at, lines, color));

    /// <summary>Any argument a hashed key may get, boxed.</summary>
    private static readonly Gen<object?> GenArgument = Gen.OneOf<object?>(
        Gen.Bool.Select(_ => (object?)null),
        GenText.Select(s => (object?)s),
        GenSmallInt.Select(i => (object?)i),
        GenSmallInt.Select(i => (object?)(long)i),
        GenSmallInt.Select(i => (object?)(short)i),
        Gen.Int[0, 2].Select(i => (object?)(uint)i),
        Gen.Bool.Select(b => (object?)b),
        Gen.Char["a:1"].Select(c => (object?)c),
        GenDouble.Select(d => (object?)d),
        GenDouble.Select(d => (object?)(float)d),
        GenDecimal.Select(d => (object?)d),
        GenDateTime.Select(d => (object?)d),
        Gen.Select(Gen.Int[0, 1], Gen.Int[-1, 1], (h, o) => (object?)new DateTimeOffset(2024, 1, 1, h, 0, 0, TimeSpan.FromHours(o))),
        Gen.Int[0, 2].Select(i => (object?)TimeSpan.FromTicks(i)),
        Gen.OneOfConst(Guid.Empty, new Guid("00000000-0000-0000-0000-000000000001"), new Guid("10000000-0000-0000-0000-000000000000")).Select(g => (object?)g),
        Gen.Enum<Color>().Select(e => (object?)e),
        Gen.Enum<Size>().Select(e => (object?)e),
        Gen.Enum<Big>().Select(e => (object?)e),
        GenPoint.Select(p => (object?)p),
        GenOrder.Select(o => (object?)o),
        Gen.Select(GenNullableText, GenSmallInt, (t, n) => (object?)new Dto { Text = t, Number = n }),
        GenSmallInt.Array[0, 2].Select(a => (object?)a),
        GenNullableText.List[0, 2].Select(l => (object?)l));

    private static readonly Gen<(string Method, int? Version, object?[] Args)> GenCall =
        Gen.Select(Gen.OneOfConst("M", "M:v1", "N"), Gen.OneOfConst<int?>(null, 1, 2), GenArgument.Array[0, 3]);

    [Fact]
    public void FastHash_SharesAKeyExactlyWhenTheArgumentsAreTheSame()
    {
        var generator = new FastHashKeyGenerator();

        // Many calls from a small domain per sample, so some are the same and many are almost the same
        Gen.Select(GenCall.Array[30], Gen.Int[0, PropertySettings.Cultures.Length - 1]).Sample((calls, cultureIndex) =>
        {
            var keys = PropertySettings.InCulture(PropertySettings.Cultures[cultureIndex], () =>
                calls.Select(c => generator.GenerateKey(c.Method, c.Args, Policy(c.Version))).ToArray());
            AssertKeysMatchCanon(keys, calls.Select(c => CanonCall(c.Method, c.Version, c.Args, mergeSignedZero: true)).ToArray());
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations / 4, print: t => Print(t.Item1) + ", culture " + PropertySettings.Cultures[t.Item2].Name);
    }

    [Fact]
    public void FastHash_GivesDistinctStringsDistinctKeys()
    {
        // Strings alone, from a tiny alphabet, so that near misses are frequent: separators, escapes, NUL, a
        // composed and a decomposed é, and the two halves of a surrogate pair on their own
        var generator = new FastHashKeyGenerator();
        var genString = Gen.String[Gen.Char["a:\\\0é\u0301\uD834\uDD1E"], 0, 2];

        Gen.Select(genString, genString).Sample((s1, s2) =>
        {
            var k1 = generator.GenerateKey("M", new object?[] { s1 }, NoVersion);
            var k2 = generator.GenerateKey("M", new object?[] { s2 }, NoVersion);

            Assert.Equal(string.Equals(s1, s2, StringComparison.Ordinal), k1 == k2);
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations, print: t => $"{Escape(t.Item1)} and {Escape(t.Item2)}");

        static string Escape(string s) => string.Concat(s.Select(c => c is >= ' ' and < '\u007f' ? c.ToString() : $"\\u{(int)c:X4}"));
    }

    [Fact]
    public void FastHash_IsCultureIndependent()
    {
        var generator = new FastHashKeyGenerator();

        GenCall.Sample(c =>
        {
            var keys = PropertySettings.Cultures
                .Select(culture => PropertySettings.InCulture(culture, () => generator.GenerateKey(c.Method, c.Args, Policy(c.Version))))
                .ToArray();

            Assert.All(keys, key => Assert.Equal(keys[0], key));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations, print: c => Print(new[] { c }));
    }

    /// <summary>One argument of an inline key: its kind fixes the C# parameter type, as a method signature does.</summary>
    private enum Kind { String, Char, Int, NullableInt, Long, Bool, Double, Float, Decimal, Guid, DateTime, DateTimeOffset, Color, Size }

    private static Gen<object?> GenFor(Kind kind) => kind switch
    {
        Kind.String => GenNullableText.Select(s => (object?)s),
        Kind.Char => Gen.Char["a:\\0"].Select(c => (object?)c),
        Kind.Int => GenSmallInt.Select(i => (object?)i),
        Kind.NullableInt => GenSmallInt.Select(i => (object)i).Null(0.3),
        Kind.Long => GenSmallInt.Select(i => (object?)(long)i),
        Kind.Bool => Gen.Bool.Select(b => (object?)b),
        Kind.Double => GenDouble.Select(d => (object?)d),
        Kind.Float => GenDouble.Select(d => (object?)(float)d),
        Kind.Decimal => GenDecimal.Select(d => (object?)d),
        Kind.Guid => Gen.OneOfConst(Guid.Empty, new Guid("00000000-0000-0000-0000-000000000001")).Select(g => (object?)g),
        Kind.DateTime => GenDateTime.Select(d => (object?)d),
        Kind.DateTimeOffset => Gen.Select(Gen.Int[0, 1], Gen.Int[-1, 1], (h, o) => (object?)new DateTimeOffset(2024, 1, 1, h, 0, 0, TimeSpan.FromHours(o))),
        Kind.Color => Gen.Enum<Color>().Select(e => (object?)e),
        _ => Gen.Enum<Size>().Select(e => (object?)e),
    };

    private static readonly Gen<(Kind[] Signature, object?[][] Calls, int Culture)> GenInlineCase =
        Gen.Enum<Kind>().Array[1, 3].SelectMany(signature =>
            Gen.Select(
                Sequence(signature.Select(GenFor)).Array[30],
                Gen.Int[0, PropertySettings.Cultures.Length - 1],
                (calls, culture) => (signature, calls, culture)));

    [Fact]
    public void InlineKeys_ShareAKeyExactlyWhenTheArgumentsAreTheSame_AndTheBuilderMatchesMethodCacheKeys()
    {
        GenInlineCase.Sample(c =>
        {
            var culture = PropertySettings.Cultures[c.Culture];
            var keys = PropertySettings.InCulture(culture, () => c.Calls.Select(args => InlineKey(c.Signature, args)).ToArray());
            var built = PropertySettings.InCulture(culture, () => c.Calls.Select(args => BuiltKey(c.Signature, args)).ToArray());

            Assert.Equal(keys, built);
            AssertKeysMatchCanon(keys, c.Calls.Select(args => string.Join(",", args.Select(a => Canon(a, mergeSignedZero: false)))).ToArray());
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations / 4, print: c => $"signature ({string.Join(", ", c.Signature)}), culture {PropertySettings.Cultures[c.Culture].Name}: " +
                                                                   string.Join(" | ", c.Calls.Select(a => string.Join(", ", a.Select(Describe)))));
    }

    [Fact]
    public void InlineKeys_AreCultureIndependent()
    {
        Gen.Enum<Kind>().SelectMany(kind => GenFor(kind).Select(value => (Kind: kind, Value: value))).Sample(t =>
        {
            var (kind, value) = t;
            var keys = PropertySettings.Cultures
                .Select(culture => PropertySettings.InCulture(culture, () => (InlineKey(new[] { kind }, new[] { value }), BuiltKey(new[] { kind }, new[] { value }))))
                .ToArray();

            Assert.All(keys, key => Assert.Equal(keys[0], key));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations, print: t => $"{t.Kind}: {Describe(t.Value)}");
    }

    /// <summary>One value from each generator, in order.</summary>
    private static Gen<object?[]> Sequence(IEnumerable<Gen<object?>> gens)
        => gens.Aggregate(Gen.Const(Array.Empty<object?>()), (acc, gen) => acc.SelectMany(values => gen.Select(value => values.Append(value).ToArray())));

    private static void AssertKeysMatchCanon(string[] keys, string[] canons)
    {
        for (var i = 0; i < keys.Length; i++)
        {
            for (var j = i + 1; j < keys.Length; j++)
            {
                if ((keys[i] == keys[j]) != (canons[i] == canons[j]))
                {
                    Assert.Fail(keys[i] == keys[j]
                        ? $"Different arguments share the key {keys[i]}:\n  {canons[i]}\n  {canons[j]}"
                        : $"The same arguments got two keys:\n  {canons[i]} -> {keys[i]}\n  {canons[j]} -> {keys[j]}");
                }
            }
        }
    }

    private static string InlineKey(Kind[] signature, object?[] args)
        => MethodCacheKeys.For(typeof(IInlineService), "Get", args.Select((a, i) => Part(signature[i], a)).ToArray());

    private static string Part(Kind kind, object? value) => kind switch
    {
        Kind.String => MethodCacheKeys.Part((string?)value),
        Kind.Char => MethodCacheKeys.Part((char)value!),
        Kind.Int => MethodCacheKeys.Part((int)value!),
        Kind.NullableInt => value is int i ? MethodCacheKeys.Part(i) : MethodCacheKeys.Null,
        Kind.Long => MethodCacheKeys.Part((long)value!),
        Kind.Bool => MethodCacheKeys.Part((bool)value!),
        Kind.Double => MethodCacheKeys.Part((double)value!),
        Kind.Float => MethodCacheKeys.Part((float)value!),
        Kind.Decimal => MethodCacheKeys.Part((decimal)value!),
        Kind.Guid => MethodCacheKeys.Part((Guid)value!),
        Kind.DateTime => MethodCacheKeys.Part((DateTime)value!),
        Kind.DateTimeOffset => MethodCacheKeys.Part((DateTimeOffset)value!),
        Kind.Color => MethodCacheKeys.Part((Color)value!),
        _ => MethodCacheKeys.Part((Size)value!),
    };

    private static string BuiltKey(Kind[] signature, object?[] args)
    {
        var builder = new MethodCacheKeyBuilder(stackalloc char[8]); // small, so growing is exercised too
        builder.Append(MethodCacheKeys.Prefix(typeof(IInlineService), "Get"));
        for (var i = 0; i < args.Length; i++)
        {
            switch (signature[i])
            {
                case Kind.String: builder.AppendPart((string?)args[i]); break;
                case Kind.Char: builder.AppendPart((char)args[i]!); break;
                case Kind.Int: builder.AppendPart((int)args[i]!); break;
                case Kind.NullableInt when args[i] is int value: builder.AppendPart(value); break;
                case Kind.NullableInt: builder.AppendNullPart(); break;
                case Kind.Long: builder.AppendPart((long)args[i]!); break;
                case Kind.Bool: builder.AppendPart((bool)args[i]!); break;
                case Kind.Double: builder.AppendPart((double)args[i]!); break;
                case Kind.Float: builder.AppendPart((float)args[i]!); break;
                case Kind.Decimal: builder.AppendPart((decimal)args[i]!); break;
                case Kind.Guid: builder.AppendPart((Guid)args[i]!); break;
                case Kind.DateTime: builder.AppendPart((DateTime)args[i]!); break;
                case Kind.DateTimeOffset: builder.AppendPart((DateTimeOffset)args[i]!); break;
                case Kind.Color: builder.AppendPart((Color)args[i]!); break;
                default: builder.AppendPart((Size)args[i]!); break;
            }
        }

        return builder.ToString();
    }

    private static string CanonCall(string method, int? version, object?[] args, bool mergeSignedZero)
        => $"{Text(method)} v{version?.ToString(CultureInfo.InvariantCulture) ?? "-"} ({string.Join(",", args.Select(a => Canon(a, mergeSignedZero)))})";

    /// <summary>
    /// A text that identifies an argument by its type and value, merging only the values listed in the remarks of
    /// this class. Built with invariant formatting and length-prefixed strings, so it is unambiguous.
    /// </summary>
    private static string Canon(object? value, bool mergeSignedZero)
    {
        var invariant = CultureInfo.InvariantCulture;
        return value switch
        {
            null => "null",
            string s => Text(s),
            double d => "double " + (double.IsNaN(d) ? "NaN" : BitConverter.DoubleToInt64Bits(mergeSignedZero && d == 0 ? 0d : d).ToString(invariant)),
            float f => "float " + (float.IsNaN(f) ? "NaN" : BitConverter.SingleToInt32Bits(mergeSignedZero && f == 0 ? 0f : f).ToString(invariant)),
            decimal m => "decimal " + Scale(m) + " " + (m == 0 ? "0" : m.ToString(invariant)),
            DateTime dt => "DateTime " + Normalize(dt).Ticks.ToString(invariant) + " " + Normalize(dt).Kind,
            DateTimeOffset dto => "DateTimeOffset " + dto.UtcTicks.ToString(invariant) + " " + dto.Offset.Ticks.ToString(invariant),
            Enum e => "enum " + e.GetType().Name + " " + (Type.GetTypeCode(e.GetType()) == TypeCode.UInt64
                ? Convert.ToUInt64(e, invariant).ToString(invariant)
                : Convert.ToInt64(e, invariant).ToString(invariant)),
            Point p => $"Point({Canon(p.X, mergeSignedZero)},{Canon(p.Name, mergeSignedZero)})",
            Order o => $"Order({Canon(o.Id, mergeSignedZero)},{Canon(o.At, mergeSignedZero)},{Canon(o.Lines, mergeSignedZero)},{Canon(o.Color, mergeSignedZero)})",
            Dto dto => $"Dto({Canon(dto.Text, mergeSignedZero)},{Canon(dto.Number, mergeSignedZero)})",
            System.Collections.IEnumerable items => value.GetType().Name + "[" + string.Join(",", items.Cast<object?>().Select(i => Canon(i, mergeSignedZero))) + "]",
            IFormattable f => value.GetType().Name + " " + f.ToString(null, invariant),
            _ => value.GetType().Name + " " + value,
        };

        static DateTime Normalize(DateTime dt) => dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
        static int Scale(decimal m) => (decimal.GetBits(m)[3] >> 16) & 0xFF;
    }

    private static string Text(string s) => $"string[{s.Length}]{s}";

    private static CacheRuntimePolicy Policy(int? version) => version == null ? NoVersion : NoVersion with { Version = version };

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => '"' + s.Replace("\0", "\\0") + '"',
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture) + " (" + dt.Kind + ")",
        decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
        double d => BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits(-0.0) ? "-0.0" : d.ToString("R", CultureInfo.InvariantCulture),
        _ => value.GetType().Name + " " + Canon(value, mergeSignedZero: false),
    };

    private static string Print((string Method, int? Version, object?[] Args)[] calls)
        => string.Join(" | ", calls.Select(c => $"{c.Method} v{c.Version}: ({(c.Args == null ? "<null array>" : string.Join(", ", c.Args.Select(Describe)))})"));

    private interface IInlineService { }
}
