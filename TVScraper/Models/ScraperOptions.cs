namespace TradingViewScraper.Core.Models;

public class ScraperOptions
{
    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 10;
    public int BarsCount { get; set; } = 5000;
}
