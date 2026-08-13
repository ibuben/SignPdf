using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SignPdf.Eimzo;

internal sealed class CapiwsClient
{
    private static readonly string[] CandidateUrls =
    {
        "ws://127.0.0.1:64646/service/cryptapi",
        "wss://127.0.0.1:64443/service/cryptapi",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private string? _workingUrl;

    public string? WorkingUrl => _workingUrl;

    public async Task<JsonObject> CallAsync(object request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_workingUrl is not null)
        {
            return await CallUrlAsync(_workingUrl, request, timeout, cancellationToken).ConfigureAwait(false);
        }

        Exception? last = null;
        foreach (var url in CandidateUrls)
        {
            try
            {
                var response = await CallUrlAsync(url, request, timeout, cancellationToken).ConfigureAwait(false);
                _workingUrl = url;
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                last = new TimeoutException($"E-IMZO не ответил за {timeout.TotalSeconds:0} с. ({url})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        throw last is null
            ? new EimzoNotRunningException()
            : new EimzoNotRunningException(last);
    }

    private static async Task<JsonObject> CallUrlAsync(
        string url,
        object request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        try
        {
            socket.Options.SetRequestHeader("Origin", url.StartsWith("wss", StringComparison.OrdinalIgnoreCase)
                ? "https://127.0.0.1"
                : "http://127.0.0.1");
        }
        catch (ArgumentException)
        {
            // Origin is a restricted header on some runtimes; CAPIWS still often accepts the call.
        }

        try
        {
            await socket.ConnectAsync(new Uri(url), timeoutCts.Token).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(request, JsonOptions);
            await SendTextAsync(socket, payload, timeoutCts.Token).ConfigureAwait(false);
            var json = await ReceiveTextAsync(socket, timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new EimzoException("E-IMZO вернул пустой ответ.");
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new EimzoException("E-IMZO вернул не JSON: " + json[..Math.Min(json.Length, 240)], ex);
            }

            if (node is JsonObject obj)
            {
                return obj;
            }

            return new JsonObject { ["success"] = true, ["raw"] = JsonValue.Create(json) };
        }
        catch (WebSocketException ex)
        {
            throw new EimzoNotRunningException(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new EimzoNotRunningException(ex);
        }
        finally
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private static async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        const int chunk = 64 * 1024;
        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            var size = Math.Min(chunk, bytes.Length - offset);
            var end = offset + size >= bytes.Length;
            await socket.SendAsync(new ArraySegment<byte>(bytes, offset, size), WebSocketMessageType.Text, end, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new EimzoException("E-IMZO закрыл соединение до ответа.");
            }

            buffer.Write(chunk, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
