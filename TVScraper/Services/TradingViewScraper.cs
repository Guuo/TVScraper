using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingViewScraper.Core.Models;
using System.IO.Pipelines;
using System.Buffers;

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
        var pipe = new Pipe();
        try
        {
            // Run producer and consumer concurrently
            var writingTask = FillPipeAsync(socket, pipe.Writer, ct);
            var readingTask = ReadPipeAsync(pipe.Reader, bars, dataCompleted, ct);

            await Task.WhenAll(writingTask, readingTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Error in receive loop"); }
    }

    private async Task FillPipeAsync(ClientWebSocket socket, PipeWriter writer, CancellationToken ct)
    {
        const int minimumBufferSize = 1024 * 16;
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // Get memory directly from the pipe to avoid extra copies
                Memory<byte> memory = writer.GetMemory(minimumBufferSize);
                var result = await socket.ReceiveAsync(memory, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await writer.FlushAsync(ct);
                    break;
                }

                writer.Advance(result.Count);
                // FlushAsync causes reader.ReadAsync in ReadPipeAsync to return and continue the loop
                var done = await writer.FlushAsync(ct);

                if (done.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Error in fill pipe"); }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private async Task ReadPipeAsync(PipeReader reader, List<OhlcvBar> bars, TaskCompletionSource<bool> dataCompleted, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(ct);
                ReadOnlySequence<byte> buffer = result.Buffer;

                int messagesProcessed = ProcessPipeBuffer(ref buffer, bars);

                if (messagesProcessed > 0)
                {
                    dataCompleted.TrySetResult(true);
                }

                // Advance the reader: 'buffer.Start' is the next byte to read, 
                // 'buffer.End' is the furthest point we examined.
                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Error in read pipe"); }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    /// <summary>Parses framed messages from the pipe buffer.</summary>
    private int ProcessPipeBuffer(ref ReadOnlySequence<byte> buffer, List<OhlcvBar> bars)
    {
        int messagesProcessed = 0;
        ReadOnlySpan<byte> delimiter = "~m~"u8;

        while (true)
        {
            var reader = new SequenceReader<byte>(buffer);

            // Search for ~m~ framing delimiters
            bool foundDelimiter = reader.TryReadTo(out ReadOnlySequence<byte> segment, delimiter, advancePastDelimiter: true);
            long firstPos = segment.Length;
            if (!foundDelimiter)
                break;

            foundDelimiter = reader.TryReadTo(out segment, delimiter, advancePastDelimiter: true);
            long secondPos = firstPos + 3 + segment.Length;
            if (!foundDelimiter)
                break;

            // Extract message length from header
            int lengthSize = (int)segment.Length;

            if (lengthSize <= 0)
            {
                buffer = buffer.Slice(firstPos + 3);
                continue;
            }

            var lengthSlice = buffer.Slice(firstPos + 3, lengthSize);
            int messageLength;
            if (lengthSlice.IsSingleSegment)
            {
                if (!int.TryParse(lengthSlice.FirstSpan, out messageLength))
                {
                buffer = buffer.Slice(firstPos + 3);
                continue;
                }
            }
            else
            {
                if (!int.TryParse(Encoding.ASCII.GetString(lengthSlice), out messageLength))
                {
                buffer = buffer.Slice(firstPos + 3);
                continue;
                }
            }

            // Check if full body has arrived
            long endOfFrame = secondPos + 3 + messageLength;

            if (buffer.Length < endOfFrame)
                break;

            var bodySlice = buffer.Slice(secondPos + 3, messageLength);
            string jsonMessage = Encoding.UTF8.GetString(bodySlice);

            // Parse and store extracted bars
            var extractedBars = ParseOhlcvData(jsonMessage);
            if (extractedBars.Any())
            {
                bars.AddRange(extractedBars);
                messagesProcessed++;
                _logger.LogDebug("Parsed message: {Msg}", ExtractMessageName(jsonMessage));
            }

            buffer = buffer.Slice(endOfFrame);
        }

        return messagesProcessed;
    }

    private static long FindSequenceInSequence(ReadOnlySequence<byte> buffer, ReadOnlySpan<byte> pattern)
    {
        if (pattern.Length == 0) return 0;
        
        // For efficiency and simplicity in this refactor, if not single segment, use ToArray search
        if (buffer.IsSingleSegment)
        {
            return buffer.FirstSpan.IndexOf(pattern);
        }

        return buffer.ToArray().AsSpan().IndexOf(pattern);
    }

    private List<OhlcvBar> ParseOhlcvData(string message)
    {
        var bars = new List<OhlcvBar>();
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

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