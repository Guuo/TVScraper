using Microsoft.Extensions.Logging;
using TradingViewScraper.Core.Models;

namespace TradingViewScraper.Services;

/// <summary>
/// Defines the interface for parsing command line arguments.
/// </summary>
public interface ICommandLineParser
{
    ScrapeRequest ParseArgs(string[] args);
    void ShowHelp();
}

/// <summary>
/// Implements command line argument parsing for the TradingView scraper.
/// </summary>
public class CommandLineArgumentParser : ICommandLineParser
{
    private readonly ILogger<CommandLineArgumentParser> _logger;

    public CommandLineArgumentParser(ILogger<CommandLineArgumentParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses an array of command line arguments into a <see cref="ScrapeRequest"/>.
    /// </summary>
    public ScrapeRequest ParseArgs(string[] args)
    {
        var request = new ScrapeRequest();
        
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--ticker":
                case "-t":
                    if (i + 1 < args.Length)
                        request.Ticker = args[++i];
                    break;
                    
                case "--timeframe":
                case "-f":
                    if (i + 1 < args.Length)
                        request.Timeframe = args[++i];
                    break;
                    
                case "--bars":
                case "-b":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var barsCount))
                        request.BarsCount = barsCount;
                    break;
                    
                case "--format":
                case "-o":
                    if (i + 1 < args.Length)
                    {
                        var formatString = args[++i].ToLowerInvariant();
                        request.Format = formatString switch
                        {
                            "csv" => OutputFormat.Csv,
                            "parquet" => OutputFormat.Parquet,
                            _ => OutputFormat.Csv
                        };
                    }
                    break;
                    
                case "--output":
                case "-p":
                    if (i + 1 < args.Length)
                        request.OutputPath = args[++i];
                    break;
                    
                case "--help":
                case "-h":
                    ShowHelp();
                    break;
            }
        }
        
        // If no output path is provided, generate a default one based on ticker and timeframe
        if (string.IsNullOrEmpty(request.OutputPath))
        {
            var extension = request.Format == OutputFormat.Csv ? ".csv" : ".parquet";
            request.OutputPath = $"{request.Ticker.Replace(":", "_")}_{request.Timeframe}{extension}";
        }
        
        return request;
    }
    
    public void ShowHelp()
    {
        Console.WriteLine("TradingView OHLCV Scraper");
        Console.WriteLine();
        Console.WriteLine("Usage: vscodeTestApp [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --ticker, -t          TradingView ticker symbol (e.g., NASDAQ:AAPL)");
        Console.WriteLine("  --timeframe, -f       Timeframe (1M, 5M, 15M, 30M, 1H, 4H, 1D, 1W, 1MN) [default: 1D]");
        Console.WriteLine("  --bars, -b            Number of bars to fetch [default: 5000]");
        Console.WriteLine("  --format, -o          Output format (csv, parquet) [default: csv]");
        Console.WriteLine("  --output, -p          Output file path");
        Console.WriteLine("  --help, -h            Show this help message");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  vscodeTestApp --ticker \"NASDAQ:AAPL\" --timeframe \"1D\" --bars 1000 --output \"aapl_daily.csv\"");
    }
}