using System.Text;
using System.Text.Json;
using CLOOPS.NATS;
using NATS.Client.Core;
using Xunit;

namespace cloops.nats.sdk.Tests;

/// <summary>
/// Tests for the compiled <see cref="NatsMsg{T}"/> factory and metadata reader that replaced
/// per-message <c>Activator.CreateInstance</c> and <c>Type.GetProperty</c> lookups.
/// </summary>
public class NatsMsgAccessorTests
{
    [Fact]
    public void CreateTypedMsgWrapper_FromCoreMsg_PreservesMetadataAndDeserializesPayload()
    {
        var payload = new Order { Id = 42, Name = "widget" };
        var raw = CreateCoreMsg(JsonSerializer.SerializeToUtf8Bytes(payload));

        var wrapper = BaseNatsUtil.CreateTypedMsgWrapper(raw, typeof(Order));

        var typed = Assert.IsType<NatsMsg<Order>>(wrapper);
        Assert.Equal("orders.created", typed.Subject);
        Assert.Equal("reply.inbox", typed.ReplyTo);
        Assert.Equal(raw.Size, typed.Size);
        Assert.Equal(42, typed.Data!.Id);
        Assert.Equal("widget", typed.Data.Name);
    }

    [Fact]
    public void CreateTypedMsgWrapper_PreservesHeaders()
    {
        var headers = new NatsHeaders { { "tenant", "acme" } };
        var raw = new NatsMsg<byte[]>(
            subject: "orders.created",
            replyTo: null,
            size: 2,
            headers: headers,
            data: Encoding.UTF8.GetBytes("{}"),
            connection: null!,
            flags: default);

        var typed = Assert.IsType<NatsMsg<Order>>(BaseNatsUtil.CreateTypedMsgWrapper(raw, typeof(Order)));

        Assert.Same(headers, typed.Headers);
        Assert.Null(typed.ReplyTo);
    }

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(bool))]
    public void CreateTypedMsgWrapper_SupportsValueTypePayloads(Type payloadType)
    {
        // Value type payloads exercise the unbox conversion in the compiled constructor.
        var raw = CreateCoreMsg(new byte[8]);

        var wrapper = BaseNatsUtil.CreateTypedMsgWrapper(raw, payloadType);

        Assert.Equal(typeof(NatsMsg<>).MakeGenericType(payloadType), wrapper.GetType());
    }

    [Fact]
    public void CreateTypedMsgWrapper_WithReplacementPayload_PreservesTransportMetadata()
    {
        var original = new NatsMsg<Order>(
            subject: "orders.created",
            replyTo: "reply.inbox",
            size: 17,
            headers: new NatsHeaders { { "tenant", "acme" } },
            data: new Order { Id = 1, Name = "before" },
            connection: null!,
            flags: default);

        var replaced = Assert.IsType<NatsMsg<Order>>(
            BaseNatsUtil.CreateTypedMsgWrapper(original, typeof(Order), new Order { Id = 2, Name = "after" }));

        Assert.Equal(original.Subject, replaced.Subject);
        Assert.Equal(original.ReplyTo, replaced.ReplyTo);
        Assert.Equal(original.Size, replaced.Size);
        Assert.Same(original.Headers, replaced.Headers);
        Assert.Equal(2, replaced.Data!.Id);
        Assert.Equal("after", replaced.Data.Name);
    }

    [Fact]
    public void Read_ReturnsTransportMetadata()
    {
        var headers = new NatsHeaders { { "tenant", "acme" } };
        var msg = new NatsMsg<Order>(
            subject: "orders.created",
            replyTo: "reply.inbox",
            size: 17,
            headers: headers,
            data: new Order { Id = 1, Name = "x" },
            connection: null!,
            flags: default);

        var metadata = NatsMsgAccessor.Read(msg);

        Assert.Equal("orders.created", metadata.Subject);
        Assert.Equal("reply.inbox", metadata.ReplyTo);
        Assert.Equal(17, metadata.Size);
        Assert.Same(headers, metadata.Headers);
        Assert.Null(metadata.Connection);
    }

    [Fact]
    public void GetFactory_ReturnsCachedDelegatePerPayloadType()
    {
        Assert.Same(NatsMsgAccessor.GetFactory(typeof(Order)), NatsMsgAccessor.GetFactory(typeof(Order)));
        Assert.NotSame(NatsMsgAccessor.GetFactory(typeof(Order)), NatsMsgAccessor.GetFactory(typeof(string)));
    }

    private static NatsMsg<byte[]> CreateCoreMsg(byte[] data)
        => new(
            subject: "orders.created",
            replyTo: "reply.inbox",
            size: data.Length,
            headers: null,
            data: data,
            connection: null!,
            flags: default);

    private sealed class Order
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
