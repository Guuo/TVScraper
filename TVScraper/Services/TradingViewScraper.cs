using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingViewScraper.Core.Models;

namespace TradingViewScraper.Services;

public interface ITradingViewScraper
{
    Task<ScrapeResult> ScrapeAsync(ScrapeRequest request);
}

public class ScrapeResult
{
    public bool Success { get; set; }
    public List<OhlcvBar> Bars { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public class CoreScraper : ITradingViewScraper
{
    private readonly ILogger<CoreScraper> _logger;
    private readonly ScraperOptions _options;

    public CoreScraper(ILogger<CoreScraper> logger, ScraperOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task<ScrapeResult> ScrapeAsync(ScrapeRequest request)
    {
        _logger.LogInformation("Starting scrape for {Ticker}, {Count} bars", request.Ticker, request.BarsCount);
        var result = new ScrapeResult();

        try
        {
            var bars = await FetchDataViaWebSocketAsync(request.Ticker, request.Timeframe, request.BarsCount);
            if (bars.Any())
            {
                result.Bars = bars;
                result.Success = true;
                _logger.LogInformation("Successfully scraped {Count} bars", result.Bars.Count);
            }
            else
            {
                result.ErrorMessage = "No data extracted from WebSocket";
                _logger.LogWarning("No data extracted from WebSocket");
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Error during scraping");
        }

        return result;
    }

    private async Task<List<OhlcvBar>> FetchDataViaWebSocketAsync(string ticker, string timeframe, int barsCount)
    {
        var bars = new List<OhlcvBar>();
        var interval = TimeframeToTvInterval(timeframe);
        var quoteSession = GenerateSession("qs");
        var chartSession = GenerateSession("cs");

        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", "https://www.tradingview.com");

        var wsUrl = "wss://data.tradingview.com/socket.io/websocket?from=chart/";
        await client.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        if (client.State != WebSocketState.Open)
            return bars;

        using var cts = new CancellationTokenSource(_options.TimeoutSeconds * 1000);
        var dataReceivedTcs = new TaskCompletionSource<bool>();

        var receiveTask = ReceiveMessagesAsync(client, bars, cts.Token, dataReceivedTcs);

        await SendMessage(client, ConstructMessageString("set_auth_token", new[] { "unauthorized_user_token" }));
        await Task.Delay(100);
        await SendMessage(client, ConstructMessageString("chart_create_session", new[] { chartSession, "" }));
        await Task.Delay(100);
        await SendMessage(client, ConstructMessageString("switch_timezone", new[] { chartSession, "Etc/UTC" }));
        await Task.Delay(100);
        await SendMessage(client, ConstructMessageString("quote_create_session", new[] { quoteSession }));
        await Task.Delay(100);

        var symbolJson = "={\"symbol\":\"" + ticker + "\",\"adjustment\":\"splits\",\"session\":\"regular\"}";
        await SendMessage(client, ConstructMessageString("quote_add_symbols", new[] { quoteSession, symbolJson }));
        await Task.Delay(100);
        await SendMessage(client, ConstructMessageString("resolve_symbol", new[] { chartSession, "sds_sym_1", symbolJson }));
        await Task.Delay(100);

        var createSeriesMessage = "{\"m\":\"create_series\",\"p\":[\"" + chartSession + "\",\"sds_1\",\"s1\",\"sds_sym_1\",\"" + interval + "\"," + barsCount + ",\"\"]}";
        await SendMessage(client, PrependHeaders(createSeriesMessage));

        var completedTask = await Task.WhenAny(dataReceivedTcs.Task, Task.Delay(_options.TimeoutSeconds * 1000, cts.Token));

        if (completedTask == dataReceivedTcs.Task)
            _logger.LogInformation("Data collection completed successfully.");
        else
            _logger.LogWarning("Scrape timed out or was cancelled.");

        cts.Cancel();
        try { await receiveTask; } catch (OperationCanceledException) { }
        
        if (client.State == WebSocketState.Open)
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);

        return bars;
    }

    private async Task ReceiveMessagesAsync(ClientWebSocket socket, List<OhlcvBar> bars, CancellationToken ct, TaskCompletionSource<bool> dataCompleted)
    {
        var buffer = new byte[1024 * 16];
        var streamBuffer = new List<byte>();

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                
                if (result.MessageType == WebSocketMessageType.Close) break;

                // Append raw bytes to buffer - only decode to string after we have a complete frame
                for (int i = 0; i < result.Count; i++) streamBuffer.Add(buffer[i]);

                int processedCount = ProcessBuffer(streamBuffer, bars);
                
                if (processedCount > 0)
                {
                    dataCompleted.TrySetResult(true);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Error in receive loop"); }
    }

    private int ProcessBuffer(List<byte> buffer, List<OhlcvBar> bars)
    {
        int messagesProcessedInThisCall = 0;

        while (true)
        {
            // Socket.io framing: ~m~length~m~jsonbody
            int headerStartIndex = FindSequence(buffer, "~m~"u8);
            if (headerStartIndex == -1) break;

            int headerEndIndex = FindSequence(buffer, "~m~"u8, headerStartIndex + 3);
            if (headerEndIndex == -1) break;

            var lengthBytes = buffer.GetRange(headerStartIndex + 3, headerEndIndex - (headerStartIndex + 3));
            string lengthStr = Encoding.UTF8.GetString(lengthBytes.ToArray());

            if (!int.TryParse(lengthStr, out int messageLength))
            {
                buffer.RemoveRange(0, headerStartIndex + 3);
                continue;
            }

            int totalFrameSize = (headerEndIndex + 3) - headerStartIndex + messageLength;
            // Wait for more data if frame is incomplete
            if (buffer.Count < totalFrameSize)
            {
                break;
            }

            int bodyStartIndex = headerEndIndex + 3;
            var bodyBytes = buffer.GetRange(bodyStartIndex, messageLength);
            string jsonMessage = Encoding.UTF8.GetString(bodyBytes.ToArray());

            buffer.RemoveRange(0, totalFrameSize);

            var extractedBars = ParseOhlcvData(jsonMessage);
            if (extractedBars.Any())
            {
                bars.AddRange(extractedBars);
                messagesProcessedInThisCall++;
                _logger.LogDebug("Parsed message: {Msg}", ExtractMessageName(jsonMessage));
            }
        }

        return messagesProcessedInThisCall;
    }

    private List<OhlcvBar> ParseOhlcvData(string message)
    {
        var bars = new List<OhlcvBar>();
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            // Guard clauses - validate structure before accessing nested data
            if (!root.TryGetProperty("m", out var method) || method.GetString() != "timescale_update")
                return bars;

            if (!root.TryGetProperty("p", out var pArray) || pArray.ValueKind != JsonValueKind.Array) return bars;
            
            var pElements = pArray.EnumerateArray().ToList();
            if (pElements.Count < 2) return bars;

            if (!pElements[1].TryGetProperty("sds_1", out var seriesObj)) return bars;
            if (!seriesObj.TryGetProperty("s", out var seriesArray)) return bars;

            foreach (var item in seriesArray.EnumerateArray())
            {
                if (!item.TryGetProperty("v", out var v) || v.GetArrayLength() < 6) continue;

                var vals = v.EnumerateArray().ToList();
                bars.Add(new OhlcvBar
                {
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)vals[0].GetDouble()).UtcDateTime,
                    Open = vals[1].GetDouble(),
                    High = vals[2].GetDouble(),
                    Low = vals[3].GetDouble(),
                    Close = vals[4].GetDouble(),
                    Volume = vals[5].GetDouble()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON message");
        }
        return bars;
    }

    // Searches for a byte sequence in the buffer - used to locate ~m~ frame markers
private static int FindSequence(List<byte> buffer, ReadOnlySpan<byte> sequence, int startFrom = 0)
    {
        for (int i = startFrom; i <= buffer.Count - sequence.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < sequence.Length; j++)
            {
                if (buffer[i + j] != sequence[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    private async Task SendMessage(ClientWebSocket socket, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private string ConstructMessageString(string method, string[] args)
    {
        var obj = new { m = method, p = args };
        return PrependHeaders(JsonSerializer.Serialize(obj));
    }

    private string PrependHeaders(string s) => $"~m~{s.Length}~m~{s}";

    private string GenerateSession(string prefix) => 
        $"{prefix}_{Convert.ToBase64String(Guid.NewGuid().ToByteArray())[..12]}";

    private string ExtractMessageName(string message)
    {
        try {
            using var doc = JsonDocument.Parse(message);
            return doc.RootElement.GetProperty("m").GetString() ?? "unknown";
        } catch { return "unknown"; }
    }

    private string TimeframeToTvInterval(string timeframe) => timeframe.ToUpperInvariant() switch
    {
        "1M" => "1", "5M" => "5", "15M" => "15", "30M" => "30",
        "1H" => "60", "4H" => "240", "1D" => "D", "1W" => "W", "1MN" => "M",
        _ => "D"
    };
}