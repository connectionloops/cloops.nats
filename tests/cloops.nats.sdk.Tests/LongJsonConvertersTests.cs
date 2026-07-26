using System.Text.Json;
using CLOOPS.NATS;
using Xunit;

namespace cloops.nats.sdk.Tests;

/// <summary>
/// Tests for <c>Int64StringJsonConverter</c> / <c>UInt64StringJsonConverter</c>
/// registered on <see cref="BaseNatsUtil.JsonSerializerOptions"/>.
/// </summary>
public class LongJsonConvertersTests
{
    private static readonly JsonSerializerOptions Options = BaseNatsUtil.JsonSerializerOptions;

    [Theory]
    [InlineData(0L)]
    [InlineData(123L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Serialize_Long_WritesJsonString(long value)
    {
        var json = JsonSerializer.Serialize(new LongDto { Id = value }, Options);

        Assert.Contains($"\"id\":\"{value}\"", json);
        Assert.DoesNotContain($"\"id\":{value}", json);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(123UL)]
    [InlineData(ulong.MaxValue)]
    public void Serialize_ULong_WritesJsonString(ulong value)
    {
        var json = JsonSerializer.Serialize(new ULongDto { Id = value }, Options);

        Assert.Contains($"\"id\":\"{value}\"", json);
        Assert.DoesNotContain($"\"id\":{value}", json);
    }

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("-42", -42L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void Deserialize_Long_FromJsonString(string token, long expected)
    {
        var dto = JsonSerializer.Deserialize<LongDto>($"{{\"id\":\"{token}\"}}", Options);

        Assert.NotNull(dto);
        Assert.Equal(expected, dto.Id);
    }

    [Theory]
    [InlineData(123L)]
    [InlineData(-42L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Deserialize_Long_FromJsonNumber(long value)
    {
        var dto = JsonSerializer.Deserialize<LongDto>($"{{\"id\":{value}}}", Options);

        Assert.NotNull(dto);
        Assert.Equal(value, dto.Id);
    }

    [Theory]
    [InlineData("123", 123UL)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    public void Deserialize_ULong_FromJsonString(string token, ulong expected)
    {
        var dto = JsonSerializer.Deserialize<ULongDto>($"{{\"id\":\"{token}\"}}", Options);

        Assert.NotNull(dto);
        Assert.Equal(expected, dto.Id);
    }

    [Theory]
    [InlineData(123UL)]
    [InlineData(0UL)]
    public void Deserialize_ULong_FromJsonNumber(ulong value)
    {
        var dto = JsonSerializer.Deserialize<ULongDto>($"{{\"id\":{value}}}", Options);

        Assert.NotNull(dto);
        Assert.Equal(value, dto.Id);
    }

    [Fact]
    public void RoundTrip_LongDto_PreservesValue()
    {
        var original = new LongDto { Id = 9_007_199_254_740_993L };

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<LongDto>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(original.Id, restored.Id);
    }

    [Fact]
    public void RoundTrip_ULongDto_PreservesValue()
    {
        var original = new ULongDto { Id = ulong.MaxValue };

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<ULongDto>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(original.Id, restored.Id);
    }

    [Fact]
    public void Serialize_NullableLong_WritesJsonStringWhenPresent()
    {
        var json = JsonSerializer.Serialize(new NullableLongDto { Id = 99L }, Options);

        Assert.Contains("\"id\":\"99\"", json);
    }

    [Fact]
    public void Serialize_NullableLong_WritesNullWhenAbsent()
    {
        var json = JsonSerializer.Serialize(new NullableLongDto { Id = null }, Options);

        Assert.Contains("\"id\":null", json);
    }

    [Fact]
    public void Deserialize_NullableLong_FromJsonString()
    {
        var dto = JsonSerializer.Deserialize<NullableLongDto>("{\"id\":\"77\"}", Options);

        Assert.NotNull(dto);
        Assert.Equal(77L, dto.Id);
    }

    [Fact]
    public void Deserialize_NullableLong_FromJsonNumber()
    {
        var dto = JsonSerializer.Deserialize<NullableLongDto>("{\"id\":77}", Options);

        Assert.NotNull(dto);
        Assert.Equal(77L, dto.Id);
    }

    [Fact]
    public void Deserialize_NullableLong_FromNull()
    {
        var dto = JsonSerializer.Deserialize<NullableLongDto>("{\"id\":null}", Options);

        Assert.NotNull(dto);
        Assert.Null(dto.Id);
    }

    [Fact]
    public void Serialize_LongArray_WritesJsonStrings()
    {
        var json = JsonSerializer.Serialize(new[] { 1L, 2L, 3L }, Options);

        Assert.Equal("[\"1\",\"2\",\"3\"]", json);
    }

    private sealed class LongDto
    {
        public long Id { get; set; }
    }

    private sealed class ULongDto
    {
        public ulong Id { get; set; }
    }

    private sealed class NullableLongDto
    {
        public long? Id { get; set; }
    }
}
