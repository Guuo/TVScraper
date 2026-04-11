# TVScraper

A C# command-line tool for scraping OHLCV candlestick data from TradingView via WebSocket.

## Features

- Real-time data extraction via TradingView's WebSocket API
- Multiple output formats: JSON, CSV, Parquet
- Configurable timeframes (1M, 5M, 15M, 30M, 1H, 4H, 1D, 1W, 1MN)
- Customizable bar count and timeout settings

## Installation

```bash
dotnet restore
dotnet build
```

## Usage

```bash
dotnet run --project TVScraper -- --ticker OANDA:SPX500USD --timeframe 1D --bars 1000 --format csv --output data.csv
```

### Arguments

| Argument | Alias | Description | Default |
|----------|-------|--------------|---------|
| `--ticker` | `-t` | TradingView symbol (e.g., `OANDA:SPX500USD`) | Required |
| `--timeframe` | `-f` | Timeframe (1M, 5M, 15M, 30M, 1H, 4H, 1D, 1W, 1MN) | `1D` |
| `--bars` | `-b` | Number of bars to fetch | `100` |
| `--format` | `-o` | Output format (json, csv, parquet) | `json` |
| `--output` | `-out` | Output file path | `output.json` |
| `--timeout` | `-timeout` | Connection timeout in seconds | `30` |
| `--help` | `-h` | Show help | - |

### Examples

```bash
# Fetch 1000 daily bars in CSV format
dotnet run --project TVScraper -- -t OANDA:SPX500USD -f 1D -b 1000 -o csv -out daily_bars.csv

# Fetch hourly data in Parquet format
dotnet run --project TVScraper -- -t NASDAQ:AAPL -f 1H -b 500 -o parquet -out hourly_data.parquet

# Fetch with custom timeout
dotnet run --project TVScraper -- -t BINANCE:BTCUSDT -f 4H -b 200 -timeout 60
```

## Project Structure

```
TVScraper/
├── Models/
│   ├── OhlcvBar.cs       # OHLCV data model
│   └── ScraperOptions.cs # Configuration options
├── Services/
│   ├── TradingViewScraper.cs # WebSocket scraper
│   ├── CommandLineParser.cs  # CLI argument parsing
│   └── DataStorageService.cs # Output file handling
└── Program.cs            # Application entry point
```

## Dependencies

- .NET 10.0
- CsvHelper 33.1.0
- Parquet.Net 5.5.0
- Microsoft.Extensions.Logging 10.0.5

## License

MIT