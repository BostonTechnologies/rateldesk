using System.Text.Json;
using Helpdesk.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class McpRequestBodyRejectionTests
{
    [Theory]
    [InlineData(257)]
    [InlineData(50_000)]
    public async Task Declared_oversize_consumes_only_limit_plus_one_without_buffering(long declaredLength)
    {
        using var body = new MeasuredBody(declaredLength);
        var context = Context(body, declaredLength);

        Assert.False(await McpRequestBody.BufferAsync(context));

        Assert.Equal(257, body.BytesRead);
        Assert.Same(body, context.Request.Body);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        using var problem = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal("request_too_large", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("body", problem.RootElement.GetProperty("limit").GetString());
        Assert.Equal(256, problem.RootElement.GetProperty("maximumBodyBytes").GetInt64());
    }

    [Theory]
    [InlineData("100-continue")]
    [InlineData("100-Continue")]
    [InlineData("custom, 100-continue")]
    public async Task Expect_continue_rejects_before_request_body_is_read(string expectation)
    {
        using var body = new MeasuredBody(257);
        var context = Context(body, 257);
        context.Request.Headers.Expect = expectation;

        Assert.False(await McpRequestBody.BufferAsync(context));

        Assert.Equal(0, body.ReadCalls);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    [Fact]
    public async Task Declaration_exceeding_finite_wire_budget_is_rejected_without_reading()
    {
        using var body = new MeasuredBody(long.MaxValue);
        var context = Context(body, long.MaxValue);

        Assert.False(await McpRequestBody.BufferAsync(context));

        Assert.Equal(0, body.ReadCalls);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal(68_864, context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize);
    }

    [Fact]
    public async Task Declared_oversize_cannot_become_accepted_when_the_body_ends_early()
    {
        using var body = new MeasuredBody(128);
        var context = Context(body, 257);

        Assert.False(await McpRequestBody.BufferAsync(context));

        Assert.Equal(128, body.BytesRead);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    [Fact]
    public async Task Declared_oversize_read_observes_request_cancellation()
    {
        using var body = new MeasuredBody(257);
        var context = Context(body, 257);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        context.RequestAborted = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => McpRequestBody.BufferAsync(context));

        Assert.Equal(1, body.ReadCalls);
        Assert.Equal(0, body.BytesRead);
        Assert.Same(body, context.Request.Body);
    }

    private static DefaultHttpContext Context(Stream body, long declaredLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = declaredLength;
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new RequestBodyLimit { MaxRequestBodySize = 256 });
        context.Response.RegisterForDispose(context.Response.Body);
        return context;
    }

    private sealed class RequestBodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class MeasuredBody(long length) : Stream
    {
        public int ReadCalls { get; private set; }
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..count].Fill((byte)' ');
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
    }
}
