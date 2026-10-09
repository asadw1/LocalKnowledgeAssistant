using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assistant.Core.Indexing;

/// <summary>Migrates the metadata database, then indexes the root at startup and on an interval.</summary>
public sealed class IndexingService(
    MetadataDatabase database,
    FileIndexer indexer,
    IOptions<IndexingOptions> options,
    ILogger<IndexingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            logger.LogInformation("Indexing is disabled");
            return;
        }

        try
        {
            database.Migrate();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not prepare the metadata database at {Path}; indexing is off", database.Path);
            return;
        }

        do
        {
            try
            {
                var result = await Task.Run(() => indexer.Index(o.Root, stoppingToken), stoppingToken);
                logger.LogInformation("Indexed {Root}: {Result}", o.Root, result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Indexing {Root} failed; will retry on the next interval", o.Root);
            }

            if (o.IntervalMinutes == 0) return;
            await Task.Delay(TimeSpan.FromMinutes(o.IntervalMinutes), stoppingToken);
        } while (!stoppingToken.IsCancellationRequested);
    }
}
