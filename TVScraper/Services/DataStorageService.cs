using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using TradingViewScraper.Core.Models;

namespace TradingViewScraper.Services;

public interface IDataStorageService
{
    Task SaveToCsvAsync(List<OhlcvBar> bars, string filePath);
    Task SaveToParquetAsync(List<OhlcvBar> bars, string filePath);
    Task SaveAsync(List<OhlcvBar> bars, string filePath, OutputFormat format);
}

public class DataStorageService : IDataStorageService
{
    private readonly ILogger<DataStorageService> _logger;

    public DataStorageService(ILogger<DataStorageService> logger)
    {
        _logger = logger;
    }

    public async Task SaveToCsvAsync(List<OhlcvBar> bars, string filePath)
    {
        _logger.LogInformation("Saving {Count} bars to CSV: {FilePath}", bars.Count, filePath);
        
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        };
        
        await using var writer = new StreamWriter(filePath);
        await using var csv = new CsvWriter(writer, config);
        
        await csv.WriteRecordsAsync(bars.Select(b => new
        {
            b.Timestamp,
            b.Open,
            b.High,
            b.Low,
            b.Close,
            b.Volume
        }));
        
        _logger.LogInformation("CSV saved successfully");
    }

    public async Task SaveToParquetAsync(List<OhlcvBar> bars, string filePath)
    {
        _logger.LogInformation("Saving {Count} bars to Parquet: {FilePath}", bars.Count, filePath);
        
        var schema = new ParquetSchema(
            new DataField<DateTime>("timestamp"),
            new DataField<double>("open"),
            new DataField<double>("high"),
            new DataField<double>("low"),
            new DataField<double>("close"),
            new DataField<double>("volume")
        );
        
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = await ParquetWriter.CreateAsync(schema, fileStream);
        
        using var rowGroup = writer.CreateRowGroup();
        
        await rowGroup.WriteColumnAsync(new DataColumn(
            schema.DataFields[0],
            bars.Select(b => b.Timestamp).ToArray()));
        
        await rowGroup.WriteColumnAsync(new DataColumn(
            schema.DataFields[1],
            bars.Select(b => b.Open).ToArray()));
        
        await rowGroup.WriteColumnAsync(new DataColumn(
            schema.DataFields[2],
            bars.Select(b => b.High).ToArray()));
        
        await rowGroup.WriteColumnAsync(new DataColumn(
            schema.DataFields[3],
            bars.Select(b => b.Low).ToArray()));
        
        await rowGroup.WriteColumnAsync(new DataColumn(
            schema.DataFields[4],
            bars.Select(b => b.Close).ToArray()));
        
        await rowGroup.WriteColumnAsync(new DataColumn(
            schema.DataFields[5],
            bars.Select(b => b.Volume).ToArray()));
        
        _logger.LogInformation("Parquet saved successfully");
    }

    public async Task SaveAsync(List<OhlcvBar> bars, string filePath, OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Csv:
                await SaveToCsvAsync(bars, filePath);
                break;
            case OutputFormat.Parquet:
                await SaveToParquetAsync(bars, filePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}
