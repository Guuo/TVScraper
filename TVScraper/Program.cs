using Microsoft.Extensions.Logging;
using TradingViewScraper.Core.Models;
using TradingViewScraper.Services;

namespace TradingViewScraper;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Initialize logger factory to provide logging throughout the application
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        
        var logger = loggerFactory.CreateLogger<Program>();
        
        var options = new ScraperOptions();
        var cliParser = new CommandLineArgumentParser(loggerFactory.CreateLogger<CommandLineArgumentParser>());
        
        // Parse command line arguments into a request object
        var scrapeRequest = cliParser.ParseArgs(args);
        
        if (args.Contains("--help") || args.Contains("-h"))
        {
            cliParser.ShowHelp();
            return 0;
        }
        
        if (string.IsNullOrEmpty(scrapeRequest.Ticker))
        {
            logger.LogError("Ticker is required. Use --ticker or -t to specify.");
            cliParser.ShowHelp();
            return 1;
        }
        
        logger.LogInformation("Starting scrape job:");
        logger.LogInformation("  Ticker: {Ticker}", scrapeRequest.Ticker);
        logger.LogInformation("  Timeframe: {Timeframe}", scrapeRequest.Timeframe);
        logger.LogInformation("  Bars: {Bars}", scrapeRequest.BarsCount);
        logger.LogInformation("  Format: {Format}", scrapeRequest.Format);
        logger.LogInformation("  Output: {Output}", scrapeRequest.OutputPath);
        
        var storageService = new DataStorageService(loggerFactory.CreateLogger<DataStorageService>());
        var scraper = new CoreScraper(
            loggerFactory.CreateLogger<CoreScraper>(),
            options);
        
        try
        {
            var scrapeResult = await scraper.ScrapeAsync(scrapeRequest);
            
            if (scrapeResult.Success)
            {
                await storageService.SaveAsync(scrapeResult.Bars, scrapeRequest.OutputPath, scrapeRequest.Format);
                
                logger.LogInformation("Scrape completed successfully!");
                logger.LogInformation("Total bars scraped: {Count}", scrapeResult.Bars.Count);
                logger.LogInformation("Data saved to: {Path}", scrapeRequest.OutputPath);
                return 0;
            }
            else
            {
                logger.LogError("Scrape failed: {Error}", scrapeResult.ErrorMessage);
                return 1;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error occurred");
            return 1;
        }
        finally
        {
            loggerFactory.Dispose();
        }
    }
}
