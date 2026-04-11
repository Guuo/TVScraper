namespace TradingViewScraper.Core.Models;

public class OhlcvBar
{
    public DateTime Timestamp { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Volume { get; set; }
}

public class ScrapeRequest
{
    public string Ticker { get; set; } = string.Empty;
    public string Timeframe { get; set; } = "1D";
    public int BarsCount { get; set; } = 5000;
    public OutputFormat Format { get; set; } = OutputFormat.Csv;
    public string OutputPath { get; set; } = string.Empty;
}

public enum OutputFormat
{
    Csv,
    Parquet
}

public static class TimeframeExtensions
{
    public static string ToTradingViewInterval(this string timeframe)
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

    public static int ToMinutes(this string timeframe)
    {
        return timeframe.ToUpperInvariant() switch
        {
            "1M" => 1,
            "5M" => 5,
            "15M" => 15,
            "30M" => 30,
            "1H" => 60,
            "4H" => 240,
            "1D" => 1440,
            "1W" => 10080,
            "1MN" => 43200,
            _ => 1440
        };
    }
}
