using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ZYC.LiveShare.Rpc;

/// <summary>
///     Live Share's JSON-RPC dialect: JSON-RPC 2.0 messages with <c>Content-Length</c> framing, method
///     names of the form <c>service.method</c>, positional params, plus a <c>messageId</c> on every
///     message and an <c>ackId</c> acknowledging the last message received.
/// </summary>
public sealed class RpcConnection : IAsyncDisposable
{
    public const int MethodNotFound = -32601;
    public const int ServerError = -32000;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource disposeCancellation = new();
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pendingRequests = new();
    private readonly MessageReader reader;

    private readonly Stream stream;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private long lastMessageId;
    private long lastReceivedMessageId;
    private int started;

    public RpcConnection(Stream stream, ILogger logger)
    {
        this.stream = stream;
        this.logger = logger;
        reader = new MessageReader(stream);
    }

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Handles requests from the peer; return the result or throw. Unhandled methods get "method not found".</summary>
    public Func<RpcNotification, CancellationToken, Task<object?>>? RequestHandler { get; set; }

    /// <summary>Completes when the connection closes; faults if reading failed.</summary>
    public Task Completion => completion.Task;

    public async ValueTask DisposeAsync()
    {
        disposeCancellation.Cancel();
        await stream.DisposeAsync();
        await Completion.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Raised for each notification (events such as <c>serverSharing.sharingStarted</c>).</summary>
    public event EventHandler<RpcNotification>? NotificationReceived;

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0)
        {
            _ = Task.Run(ReadLoopAsync);
        }
    }

    public async Task<T?> InvokeAsync<T>(string method, object?[] args, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(method, args, cancellationToken);
        return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? default
            : result.Deserialize<T>(JsonOptions);
    }

    public async Task<JsonElement> InvokeAsync(string method, object?[] args, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref lastMessageId);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingRequests[id] = response;
        if (Completion.IsCompleted)
        {
            pendingRequests.TryRemove(id, out _);
            throw new RpcDisconnectedException("The RPC connection is closed.");
        }

        try
        {
            var message = CreateMessage(id);
            message["id"] = id;
            message["method"] = method;
            message["params"] = JsonSerializer.SerializeToNode(args, JsonOptions);
            await SendAsync(message, cancellationToken);
            return await response.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a notification; for events, <paramref name="args" /> is the event args object.</summary>
    public Task NotifyAsync(string method, object? args, CancellationToken cancellationToken)
    {
        var message = CreateMessage(Interlocked.Increment(ref lastMessageId));
        message["method"] = method;
        message["params"] = JsonSerializer.SerializeToNode(args, JsonOptions);
        return SendAsync(message, cancellationToken);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await reader.ReadAsync(disposeCancellation.Token) is { } body)
            {
                Dispatch(body);
            }

            completion.TrySetResult();
        }
        catch (Exception) when (disposeCancellation.IsCancellationRequested)
        {
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "RPC read loop failed");
            completion.TrySetException(ex);
        }
        finally
        {
            foreach (var request in pendingRequests.Values)
            {
                request.TrySetException(
                    new RpcDisconnectedException("The RPC connection closed before a response was received."));
            }
        }
    }

    private void Dispatch(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var message = document.RootElement;
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("<- {Message}", Truncate(Encoding.UTF8.GetString(body)));
        }

        if (message.TryGetProperty("messageId", out var messageId) && messageId.TryGetInt64(out var received))
        {
            InterlockedMax(ref lastReceivedMessageId, received);
        }

        var hasId = message.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number;
        if (message.TryGetProperty("method", out var methodElement) && methodElement.GetString() is { } method)
        {
            var args = message.TryGetProperty("params", out var p) ? p.Clone() : default;
            var notification = new RpcNotification(method, args);
            if (hasId)
            {
                _ = HandleRequestAsync(idElement.GetInt64(), notification);
            }
            else
            {
                RaiseNotification(notification);
            }

            return;
        }

        if (hasId && pendingRequests.TryRemove(idElement.GetInt64(), out var pending))
        {
            if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var value)
                    ? value
                    : ServerError;
                var text = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                pending.TrySetException(new RpcRemoteException(code, text ?? "Remote error."));
            }
            else
            {
                pending.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
        }
    }

    private void RaiseNotification(RpcNotification notification)
    {
        try
        {
            NotificationReceived?.Invoke(this, notification);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification handler for {Method} failed", notification.Method);
        }
    }

    private async Task HandleRequestAsync(long id, RpcNotification request)
    {
        var response = CreateMessage(Interlocked.Increment(ref lastMessageId));
        response["id"] = id;
        try
        {
            if (RequestHandler is not { } handler)
            {
                throw new RpcRemoteException(MethodNotFound, $"Method not found: {request.Method}");
            }

            response["result"] =
                JsonSerializer.SerializeToNode(await handler(request, disposeCancellation.Token), JsonOptions);
        }
        catch (Exception ex)
        {
            var code = ex is RpcRemoteException remote ? remote.Code : ServerError;
            logger.LogDebug("Request {Method} from host failed: {Message}", request.Method, ex.Message);
            response["error"] = new JsonObject { ["code"] = code, ["message"] = ex.Message };
        }

        try
        {
            await SendAsync(response, disposeCancellation.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not send response to {Method}: {Message}", request.Method, ex.Message);
        }
    }

    private JsonObject CreateMessage(long messageId)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["messageId"] = messageId };
        var ackId = Interlocked.Read(ref lastReceivedMessageId);
        if (ackId > 0)
        {
            message["ackId"] = ackId;
        }

        return message;
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("-> {Message}", Truncate(Encoding.UTF8.GetString(body)));
        }

        var frame = MessageWriter.Frame(body);
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(frame, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static void InterlockedMax(ref long location, long value)
    {
        var current = Interlocked.Read(ref location);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref location, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private static string Truncate(string text)
    {
        return text.Length <= 2000 ? text : text[..2000] + "...";
    }
}

/// <summary>A request or notification received from the peer.</summary>
public sealed record RpcNotification(string Method, JsonElement Params)
{
    /// <summary>Event notifications carry the event args object as params.</summary>
    public T? GetEventArgs<T>()
    {
        return Params.ValueKind == JsonValueKind.Object ? Params.Deserialize<T>(RpcConnection.JsonOptions) : default;
    }
}

public sealed class RpcRemoteException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed class RpcDisconnectedException(string message) : Exception(message);