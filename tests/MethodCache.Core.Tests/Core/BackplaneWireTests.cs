using System.Buffers.Binary;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class BackplaneWireTests
{
    [Fact]
    public void FrozenContractOneVectors_AreDecodedAndEmittedExactly()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "wire-v1.json")));
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var type = item.GetProperty("Operation").GetString() switch
            {
                "key" => BackplaneMessageType.KeyInvalidation,
                "tag" => BackplaneMessageType.TagInvalidation,
                "method" => BackplaneMessageType.MethodInvalidation,
                _ => BackplaneMessageType.ClearAll
            };
            var units = item.GetProperty("TargetUnits");
            var target = units.ValueKind == System.Text.Json.JsonValueKind.Null ? null : new string(units.EnumerateArray().Select(unit => (char)unit.GetInt32()).ToArray());
            var source = new BackplaneMessage
            {
                Type = type,
                InstanceId = item.GetProperty("Sender").GetString()!,
                Timestamp = DateTimeOffset.UnixEpoch,
                Key = type is BackplaneMessageType.KeyInvalidation or BackplaneMessageType.MethodInvalidation ? target : null,
                Tag = type == BackplaneMessageType.TagInvalidation ? target : null
            };
            var wire = item.GetProperty("Wire").GetString()!;
            Assert.True(BackplaneWire.TryDecode(wire, out var decoded));
            Assert.Equal(source.Type, decoded.Type);
            Assert.Equal(source.Key, decoded.Key);
            Assert.Equal(source.Tag, decoded.Tag);
            Assert.Equal(wire, BackplaneWire.Encode(source));
        }
    }

    [Theory]
    [InlineData((int)BackplaneMessageType.KeyInvalidation)]
    [InlineData((int)BackplaneMessageType.TagInvalidation)]
    [InlineData((int)BackplaneMessageType.MethodInvalidation)]
    [InlineData((int)BackplaneMessageType.ClearAll)]
    public void ContractOne_RoundTripsOrdinalIdentities(int operation)
    {
        var type = (BackplaneMessageType)operation;
        const string identity = "line\n:\0\ud800\udfff\\text";
        var source = new BackplaneMessage
        {
            Type = type,
            InstanceId = "sender",
            Timestamp = DateTimeOffset.UnixEpoch,
            Key = identity,
            Tag = identity
        };
        Assert.True(BackplaneWire.TryDecode(BackplaneWire.Encode(source), out var result));
        Assert.Equal(type, result.Type);
        Assert.Equal("sender", result.InstanceId);
        Assert.Equal(source.Timestamp, result.Timestamp);
        Assert.Equal(type is BackplaneMessageType.KeyInvalidation or BackplaneMessageType.MethodInvalidation ? identity : null, result.Key);
        Assert.Equal(type == BackplaneMessageType.TagInvalidation ? identity : null, result.Tag);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ExtensionFields_OnlyUnknownMandatoryFieldsAreRejected(bool required, bool accepted)
    {
        var bytes = Convert.FromBase64String(BackplaneWire.Encode(new BackplaneMessage
        { Type = BackplaneMessageType.TagInvalidation, InstanceId = "sender", Tag = "tag" })[4..]);
        var expanded = new byte[bytes.Length + 8];
        bytes.CopyTo(expanded, 0);
        expanded[bytes.Length] = (byte)(required ? 0xfe : 0x7e);
        BinaryPrimitives.WriteInt32LittleEndian(expanded.AsSpan(bytes.Length + 1), 3);
        BinaryPrimitives.WriteInt32LittleEndian(expanded.AsSpan(4), expanded.Length - 8);
        Assert.Equal(accepted, BackplaneWire.TryDecode("MC1:" + Convert.ToBase64String(expanded), out var result));
        if (accepted) Assert.Equal("tag", result.Tag);
    }

    [Theory]
    [InlineData(0, 2)] // Unknown version.
    [InlineData(1, 99)] // Unknown required operation.
    [InlineData(3, 1)] // Unknown mandatory flags.
    [InlineData(4, 0)] // Incorrect frame length.
    [InlineData(9, 255)] // Field length exceeds the frame.
    public void CorruptRequiredFrame_IsRejected(int index, byte value)
    {
        var bytes = Convert.FromBase64String(BackplaneWire.Encode(new BackplaneMessage
        { Type = BackplaneMessageType.ClearAll, InstanceId = "sender" })[4..]);
        bytes[index] = value;
        Assert.False(BackplaneWire.TryDecode("MC1:" + Convert.ToBase64String(bytes), out _));
    }

    [Fact]
    public void MalformedOrOversizedFrames_AreRejectedBeforeIdentityDecoding()
    {
        foreach (var wire in new[] { null, "", "MC2:AAAA", "MC1:!", "MC1:" + new string('A', BackplaneWire.MaximumBytes * 2) })
            Assert.False(BackplaneWire.TryDecode(wire, out _));
    }
}
