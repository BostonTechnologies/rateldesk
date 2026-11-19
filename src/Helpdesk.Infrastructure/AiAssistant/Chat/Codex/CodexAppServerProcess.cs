using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.Codex;

internal interface ICodexAppServerRpc
{
    Task<JsonElement> CallAsync<T>(string method, T parameters, CancellationToken ct);
    ChannelReader<CodexNotification> Notifications { get; }
}

internal sealed class CodexAppServerProcess : ICodexAppServerRpc, IAsyncDisposable
{
    private const int MaximumFrame = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim send = new(1);
    private readonly SemaphoreSlim write = new(1);
    private readonly Task pump;
    private readonly Task drain;
    private readonly Channel<CodexNotification> notifications = Channel.CreateBounded<CodexNotification>(new BoundedChannelOptions(512)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = true
    });
    private volatile PendingRequest? pending;
    private int nextId;

    private CodexAppServerProcess(Process process)
    {
        this.process = process;
        pump = PumpAsync(lifetime.Token);
        // The Codex process can emit account and provider diagnostics on stderr.
        // Drain them without forwarding text containing credentials or prompts.
        drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, lifetime.Token);
    }

    internal bool IsRunning => !process.HasExited && !pump.IsCompleted;
    internal CodexNotification? LoginResult { get; private set; }
    public ChannelReader<CodexNotification> Notifications => notifications.Reader;
    internal CodexNotification? ModelReroute { get; private set; }

    internal static async Task<CodexAppServerProcess> StartAsync(ProcessStartInfo command, CancellationToken ct)
    {
        var process = Process.Start(command) ?? throw new IOException("The Codex runtime could not start.");
        var server = new CodexAppServerProcess(process);
        try
        {
            await server.CallAsync("initialize", new { clientInfo = new { name = "rateldesk", version = "1" } }, ct);
            await server.WriteAsync(new { method = "initialized" }, ct);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async Task<JsonElement> CallAsync<T>(string method, T parameters, CancellationToken ct)
    {
        await send.WaitAsync(ct);
        try
        {
            var request = new PendingRequest(Interlocked.Increment(ref nextId),
                new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));
            pending = request;
            if (!IsRunning) throw new IOException("The Codex runtime is unavailable.");
            await WriteAsync(new { id = request.Id, method, @params = parameters }, ct);
            return await request.Response.Task.WaitAsync(ct);
        }
        finally
        {
            pending = null;
            send.Release();
        }
    }

    private async Task WriteAsync<T>(T value, CancellationToken ct)
    {
        await write.WaitAsync(ct);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, Json).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        finally { write.Release(); }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<char>.Shared.Rent(8192);
        var line = new StringBuilder();
        try
        {
            while (await process.StandardOutput.ReadAsync(buffer.AsMemory(), ct) is var count && count > 0)
            {
                var offset = 0;
                while (offset < count)
                {
                    var newline = Array.IndexOf(buffer, '\n', offset, count - offset);
                    var length = (newline < 0 ? count : newline) - offset;
                    if (line.Length + length > MaximumFrame)
                        throw new InvalidDataException("The Codex runtime returned an oversized message.");
                    line.Append(buffer, offset, length);
                    if (newline < 0) break;
                    if (line.Length != 0)
                        await ReceiveAsync(line.ToString(), ct);
                    line.Clear();
                    offset = newline + 1;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            pending?.Response.TrySetCanceled(ct);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            pending?.Response.TrySetException(new IOException("The Codex App Server connection was interrupted."));
        }
        finally
        {
            pending?.Response.TrySetException(new IOException("The Codex App Server connection was interrupted."));
            notifications.Writer.TryComplete();
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async Task ReceiveAsync(string line, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        if (root.TryGetProperty("method", out var name) && name.GetString() is { } method)
        {
            if (root.TryGetProperty("id", out var serverRequest))
            {
                // This deployment only exposes scoped MCP reads. No runtime
                // approval or elicited input can grant a second authority.
                await WriteAsync(new { id = serverRequest, error = new { code = -32601, message = "Unavailable in this RatelDesk profile." } }, ct);
                return;
            }
            if (root.TryGetProperty("params", out var parameters))
            {
                var notice = new CodexNotification(method, parameters.Clone());
                if (method == "account/login/completed") LoginResult = notice;
                if (method == "model/rerouted") ModelReroute = notice;
                notifications.Writer.TryWrite(notice);
            }
            return;
        }
        if (pending is not { } request || !root.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var value) || value != request.Id)
            return;
        if (root.TryGetProperty("result", out var result)) request.Response.TrySetResult(result.Clone());
        else request.Response.TrySetException(new CodexRpcException());
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        try
        {
            await Task.WhenAll(pump, drain);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            await process.WaitForExitAsync();
        }
        finally
        {
            process.Dispose();
            send.Dispose();
            write.Dispose();
            lifetime.Dispose();
        }
    }

    private sealed record PendingRequest(int Id, TaskCompletionSource<JsonElement> Response);
}

internal sealed record CodexNotification(string Method, JsonElement Parameters);
internal sealed class CodexRpcException() : IOException("Codex App Server did not accept the requested operation.");
