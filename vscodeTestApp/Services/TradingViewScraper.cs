using System.IO;
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
    private static readonly System.Text.RegularExpressions.Regex HeaderRegex = new(@"^~m~(\d+)~m~", System.Text.RegularExpressions.RegexOptions.Compiled);
    private const int MaxBufferSize = 10 * 1024 * 1024;

    public CoreScraper(ILogger<CoreScraper> logger, ScraperOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task<ScrapeResult> ScrapeAsync(ScrapeRequest request)
    {
        _logger.LogInformation("Starting scrape for {Ticker}, {Count} bars", 
            request.Ticker, request.BarsCount);
        
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
        var symbol = ticker.Replace(":", "_").Replace("=", "");
        
        _logger.LogInformation("Connecting to TradingView WebSocket for {Symbol} interval {Interval}", symbol, interval);
        
        var quoteSession = GenerateSession("qs");
        var chartSession = GenerateSession("cs");
        
        try
        {
            using var client = new ClientWebSocket();
            client.Options.SetRequestHeader("Origin", "https://www.tradingview.com");
            
            var wsUrl = "wss://data.tradingview.com/socket.io/websocket?from=chart/";
            _logger.LogDebug("WebSocket URL: {Url}", wsUrl);
            
            await client.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
            
            if (client.State == WebSocketState.Open)
            {
                _logger.LogInformation("WebSocket connected successfully");
                
                var tasks = new List<Task>();
                var cts = new CancellationTokenSource();
                var dataReceivedTcs = new TaskCompletionSource<bool>();
                
                tasks.Add(ReceiveMessagesAsync(client, bars, cts.Token, dataReceivedTcs));
                
                await Task.Delay(500);
                
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
                await SendMessage(client, ConstructMessageString("resolve_symbol", new[] { chartSession, "sds_sym_1", "={\"symbol\":\"" + ticker + "\",\"adjustment\":\"splits\",\"session\":\"regular\"}" }));
                await Task.Delay(100);
                
                var createSeriesMessage = "{\"m\":\"create_series\",\"p\":[\"" + chartSession + "\",\"sds_1\",\"s1\",\"sds_sym_1\",\"" + interval + "\"," + barsCount + ",\"\"]}";
                await SendMessage(client, PrependHeaders(createSeriesMessage));
                
                _logger.LogInformation("Sent all subscription messages");
                
                var timeoutMs = _options.TimeoutSeconds * 1000;
                var completedTask = await Task.WhenAny(
                    dataReceivedTcs.Task,
                    Task.Delay(timeoutMs, cts.Token));
                
                if (dataReceivedTcs.Task.IsCompletedSuccessfully)
                {
                    _logger.LogInformation("Data reception completed via signal");
                }
                else
                {
                    _logger.LogWarning("Timeout reached after {Timeout}s - proceeding with collected data", _options.TimeoutSeconds);
                }
                
                cts.Cancel();
                
                try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
                
                if (client.State == WebSocketState.Open)
                {
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                }
            }
            else
            {
                _logger.LogWarning("WebSocket failed to connect, state: {State}", client.State);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket error");
        }
        
        return bars;
    }

    private async Task SendMessage(ClientWebSocket socket, string message)
    {
        _logger.LogDebug("Sending: {Message}", message);
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveMessagesAsync(ClientWebSocket socket, List<OhlcvBar> bars, CancellationToken ct, TaskCompletionSource<bool>? dataCompleted)
    {
        var buffer = new byte[1024 * 16];
        var receiveBuffer = new StringBuilder();
        
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    
                    File.AppendAllText("tvwebsockettest.txt", msg + Environment.NewLine);
                    
                    receiveBuffer.Append(msg);
                    
                    var processed = ProcessBuffer(receiveBuffer, bars, out var completedMessages);
                    if (completedMessages > 0)
                    {
                        if (dataCompleted != null && !dataCompleted.Task.IsCompleted)
                        {
                            dataCompleted.TrySetResult(true);
                        }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (receiveBuffer.Length > 0)
                    {
                        var processed = ProcessBuffer(receiveBuffer, bars, out var completedMessages);
                        if (completedMessages > 0)
                        {
                            if (dataCompleted != null && !dataCompleted.Task.IsCompleted)
                            {
                                dataCompleted.TrySetResult(true);
                            }
                        }
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }
    
    private int ProcessBuffer(StringBuilder buffer, List<OhlcvBar> bars, out int completedMessages)
    {
        var processedCount = 0;
        completedMessages = 0;
        var totalLength = 0;
        
        var messageNames = new List<string>();
        
        while (buffer.Length > 0)
        {
            var headerLength = ParseHeader(buffer, out int messageLength);
            
            if (headerLength == 0)
            {
                break;
            }
            
            if (buffer.Length < headerLength + messageLength)
            {
                break;
            }
            
            var bufferLen = buffer.Length;
            Console.WriteLine("Entire buffer was " + bufferLen);

            // 
            var jsonMessage = buffer.ToString(headerLength, messageLength);
            if (jsonMessage.Length > 0 && jsonMessage[0] == '~')
            {
                jsonMessage = jsonMessage.Substring(1);
            }
            buffer.Remove(0, headerLength + messageLength);
            
            totalLength += messageLength;
            
            //Somewhere around here we need to chop the jsonMessage into 
            // actual json pieces and/or read more buffer if the json is not finished
            //by the end of previous buffer
            //ExtractMessageName only gets the first name, and it's a logging method anyway I think
            messageNames.Add(ExtractMessageName(jsonMessage));
            
            var extractedBars = ParseOhlcvData(jsonMessage);
            if (extractedBars.Any())
            {
                bars.AddRange(extractedBars);
                processedCount += extractedBars.Count;
                completedMessages++;
            }
        }
        
        if (completedMessages > 0)
        {
            _logger.LogInformation("Read {Count} messages, {Len} bytes: {Names}", 
                completedMessages, totalLength, string.Join(", ", messageNames));
        }
        
        return processedCount;
    }

    //Useless, only gets the length of the header part
    private int ParseHeader(StringBuilder buffer, out int messageLength)
    {
        messageLength = 0;
        
        // Minimum buffer size: "~m~X~m~" needs at least 6 chars
        if (buffer.Length < 6)
        {
            return 0;
        }
        
        // Find the opening "~m~" prefix that marks the start of a header
        var startIndex = 0;
        while (startIndex < buffer.Length - 2)
        {
            if (buffer[startIndex] == '~' && buffer[startIndex + 1] == 'm' && buffer[startIndex + 2] == '~')
            {
                break;
            }
            startIndex++;
        }
        
        // No valid header prefix found in buffer
        if (startIndex >= buffer.Length - 2)
        {
            return 0;
        }
        
        // Remove any garbage characters before the header prefix
        if (startIndex > 0)
        {
            buffer.Remove(0, startIndex);
        }
        
        // Parse the length digits after "~m~"
        var start = 3;
        var end = start;
        
        while (end < buffer.Length && char.IsDigit(buffer[end]))
        {
            end++;
        }
        
        // Length digits not found or buffer ends prematurely
        if (end <= start || end + 2 > buffer.Length)
        {
            return 0;
        }
        
        // Closing "~m~" not found at expected position
        if (!(buffer[end] == '~' && buffer[end + 1] == 'm'))
        {
            return 0;
        }
        
        var headerLength = end + 2;
        
        // Failed to parse length as integer
        if (!int.TryParse(buffer.ToString(start, end - start), out messageLength))
        {
            return 0;
        }
        
        // Invalid length value (negative, zero, or unreasonably large)
        if (messageLength <= 0 || messageLength > 100 * 1024 * 1024)
        {
            return 0;
        }
        
        return headerLength;
    }
    
    private List<OhlcvBar> ParseOhlcvData(string message)
    {
        var bars = new List<OhlcvBar>();
        
        try
        {
            if (!message.Contains("timescale_update"))
            {
                return bars;
            }
            
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("m", out var method)) return bars;
            
            var methodStr = method.GetString();
            
            if (methodStr != "timescale_update" && methodStr != "series_loading" && methodStr != "series_completed")
            {
                return bars;
            }
            
            if (root.TryGetProperty("p", out var parametersElement) && parametersElement.ValueKind == JsonValueKind.Array)
            {
                var pArray = parametersElement.EnumerateArray().ToArray();
                if (pArray.Length >= 2 && pArray[1].ValueKind == JsonValueKind.Object)
                {
                    var dataObj = pArray[1];
                    if (dataObj.TryGetProperty("sds_1", out var seriesObject) && seriesObject.ValueKind == JsonValueKind.Object)
                    {
                        if (seriesObject.TryGetProperty("s", out var seriesArray))
                        {
                            foreach (var seriesItem in seriesArray.EnumerateArray())
                            {
                                if (seriesItem.TryGetProperty("v", out var valuesProperty))
                                {
                                    var valuesList = valuesProperty.EnumerateArray().ToList();
                                    if (valuesList.Count >= 6)
                                    {
                                        var time = (long)valuesList[0].GetDouble();
                                        var open = valuesList[1].GetDouble();
                                        var high = valuesList[2].GetDouble();
                                        var low = valuesList[3].GetDouble();
                                        var close = valuesList[4].GetDouble();
                                        var volume = valuesList[5].GetDouble();
                                        
                                        bars.Add(new OhlcvBar
                                        {
                                            Timestamp = DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime,
                                            Open = open,
                                            High = high,
                                            Low = low,
                                            Close = close,
                                            Volume = volume
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }
        
        return bars;
    }
    
    private string ConstructMessageString(string method, string[] args)
    {
        var parameters = new List<string>();
        foreach (var arg in args)
        {
            parameters.Add(arg);
        }
        
        var obj = new Dictionary<string, object>
        {
            ["m"] = method,
            ["p"] = parameters
        };
        
        var json = JsonSerializer.Serialize(obj);
        return PrependHeaders(json);
    }
    
    private string PrependHeaders(string s)
    {
        return "~m~" + s.Length + "~m~" + s;
    }
    
    private string GenerateSession(string prefix)
    {
        var session = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 12);
        return prefix + "_" + session;
    }
    
    private string ExtractMessageName(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("m", out var method))
            {
                return method.GetString() ?? "unknown";
            }
        }
        catch
        {
        }
        return "unknown";
    }
    
    private string TimeframeToTvInterval(string timeframe)
    {
        return timeframe.ToUpperInvariant() switch
        {
            "1M" => "1",
            "5M" => "5",
            "15M" => "15",
            "30M" => "30",
            "1H" => "60",
            "4H" => "240",
            "1D" => "D",
            "1W" => "W",
            "1MN" => "M",
            _ => "D"
        };
    }
}
