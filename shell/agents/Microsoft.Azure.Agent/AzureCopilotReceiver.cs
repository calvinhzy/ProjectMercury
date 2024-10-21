using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.Agent;

internal class AzureCopilotReceiver : IDisposable
{
    private const int BufferSize = 4096;

    private readonly byte[] _buffer;
    private readonly ClientWebSocket _webSocket;
    private readonly MemoryStream _memoryStream;
    private readonly CancellationTokenSource _cancelMessageReceiving;
    private readonly BlockingCollection<CopilotActivity> _activityQueue;
    private readonly ILogger _logger;

    private AzureCopilotReceiver(ClientWebSocket webSocket)
    {
        _webSocket = webSocket;
        _buffer = new byte[BufferSize];
        _memoryStream = new MemoryStream();
        _cancelMessageReceiving = new CancellationTokenSource();
        _activityQueue = new BlockingCollection<CopilotActivity>();

        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = factory.CreateLogger("AzureCopilotReceiver");

        Watermark = -1;
    }

    internal int Watermark { get; private set; }

    internal static async Task<AzureCopilotReceiver> CreateAsync(string streamUrl)
    {
        var webSocket = new ClientWebSocket();
        await webSocket.ConnectAsync(new Uri(streamUrl), CancellationToken.None);

        var copilotReader = new AzureCopilotReceiver(webSocket);
        _ = Task.Run(copilotReader.ProcessActivities);

        return copilotReader;
    }

    private async Task ProcessActivities()
    {
        while (_webSocket.State is WebSocketState.Open)
        {
            string closingMessage = null;
            WebSocketReceiveResult result = null;

            try
            {
                result = await _webSocket.ReceiveAsync(_buffer, _cancelMessageReceiving.Token);
                if (result.MessageType is WebSocketMessageType.Close)
                {
                    closingMessage = "Close message received";
                    _activityQueue.Add(new CopilotActivity { Error = new ConnectionDroppedException("The server websocket is closing. Connection dropped.") });
                }
            }
            catch (OperationCanceledException e)
            {
                // TODO: log the cancellation of the message receiving thread.
                _logger.LogWarning($"Message receiving thread has been cancelled because of {e}");
                // Close the web socket before the thread is going away.
                closingMessage = "Client closing";
            }

            if (closingMessage is not null)
            {
                // TODO: log the closing request.
                _logger.LogInformation($"Closing async websocket, {closingMessage}");
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, closingMessage, CancellationToken.None);
                _activityQueue.CompleteAdding();
                break;
            }

            // Occasionally, the Direct Line service sends an empty message as a liveness ping.
            // We simply ignore these messages.
            if (result.Count is 0)
            {
                continue;
            }

            _memoryStream.Write(_buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                _memoryStream.Position = 0;
                var rawResponse =  JsonSerializer.Deserialize<RawResponse>(_memoryStream, Utils.JsonOptions);
                _memoryStream.SetLength(0);

                if (rawResponse.Watermark is not null)
                {
                    Watermark = int.Parse(rawResponse.Watermark);
                }

                foreach (CopilotActivity activity in rawResponse.Activities)
                {
                    if (activity.IsFromCopilot)
                    {
                        _activityQueue.Add(activity);
                    }
                }
            }
        }

        // TODO: log the current state of the web socket.
        _logger.LogWarning($"The websocket got in '{_webSocket.State}' state. Connection dropped.");
        _activityQueue.Add(new CopilotActivity { Error = new ConnectionDroppedException($"The websocket got in '{_webSocket.State}' state. Connection dropped.") });
        _activityQueue.CompleteAdding();
    }

    internal CopilotActivity Take(CancellationToken cancellationToken)
    {
        CopilotActivity activity = _activityQueue.Take(cancellationToken);
        if (activity.Error is not null)
        {
            ExceptionDispatchInfo.Capture(activity.Error).Throw();
        }

        return activity;
    }

    public void Dispose()
    {
        _webSocket.Dispose();
        _cancelMessageReceiving.Cancel();
    }
}
