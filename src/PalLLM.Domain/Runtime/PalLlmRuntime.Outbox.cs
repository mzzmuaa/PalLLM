using System.Text.Json;
using PalLLM.Domain;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

public sealed partial class PalLlmRuntime
{
    /// Processes screenshots in Bridge/Screenshots through the vision world-state
    /// extractor and merges the result into the snapshot. Exposed as a single method
    /// so the background watcher and tests can share one code path. By default the
    /// method drains the full queue; the watcher can pass a smaller maxFiles budget
    /// to keep long-running sessions responsive under backlog.
    public async Task<ScreenshotIngestResult> ProcessScreenshotsAsync(
        CancellationToken cancellationToken,
        int maxFiles = int.MaxValue)
    {
        if (_visionOrchestrator is null || !_options.Vision.Enabled)
        {
            return new ScreenshotIngestResult();
        }

        _options.EnsureDirectories();
        PrunePendingScreenshots();
        string[] files = GetSortedFiles(_options.BridgeScreenshotsDir, "*.png", "*.jpg", "*.jpeg")
            .Take(ClampPositiveBudget(maxFiles))
            .ToArray();

        int processed = 0;
        int failed = 0;
        foreach (string file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var fileInfo = new FileInfo(file);
                if (!fileInfo.Exists)
                {
                    continue;
                }

                if (fileInfo.Length == 0)
                {
                    Archive(file, _options.BridgeFailedDir);
                    failed++;
                    continue;
                }

                if (fileInfo.Length > _options.Vision.MaxImageBytes)
                {
                    Adapter.Logger.Warning(
                        $"Screenshot {fileInfo.Name} exceeded the configured cap of {_options.Vision.MaxImageBytes} bytes.");
                    Archive(file, _options.BridgeFailedDir);
                    failed++;
                    continue;
                }

                BoundedBase64FileReader.Base64ReadResult readResult =
                    await BoundedBase64FileReader.TryReadAsync(file, _options.Vision.MaxImageBytes, cancellationToken)
                        .ConfigureAwait(false);
                if (!readResult.Succeeded)
                {
                    Adapter.Logger.Warning(
                        $"Screenshot {fileInfo.Name} {DescribeScreenshotReadFailure(readResult.FailureCode, _options.Vision.MaxImageBytes)}");
                    Archive(file, _options.BridgeFailedDir);
                    failed++;
                    continue;
                }

                string mime = file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                VisionWorldStateResponse response = await ExtractWorldStateAsync(new VisionWorldStateRequest
                {
                    ImageBase64 = readResult.Base64!,
                    ImageMimeType = mime,
                    ApplyToSnapshot = true,
                    Hint = $"screenshot-ingest:{Path.GetFileName(file)}",
                }, cancellationToken).ConfigureAwait(false);

                if (response.Success)
                {
                    Archive(file, _options.BridgeArchiveDir);
                    processed++;
                }
                else
                {
                    Adapter.Logger.Warning($"Screenshot {Path.GetFileName(file)} failed world-state extract: {response.StatusMessage}");
                    Archive(file, _options.BridgeFailedDir);
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Adapter.Logger.Warning($"Screenshot {Path.GetFileName(file)} processing errored: {DescribeScreenshotProcessingFailure(ex)}");
                try
                {
                    Archive(file, _options.BridgeFailedDir);
                }
                catch (IOException)
                {
                    // best-effort
                }

                failed++;
            }
        }

        return new ScreenshotIngestResult
        {
            ProcessedCount = processed,
            FailedCount = failed,
        };
    }

    /// Prunes the pending screenshot queue so the bridge cannot accumulate an
    /// arbitrarily stale image backlog while vision is disabled or slower than the
    /// screenshot producer. This keeps disk usage bounded and preserves low-latency
    /// processing by preferring fresher screenshots over very old ones.
    public int PrunePendingScreenshots()
    {
        _options.EnsureDirectories();
        int removed = DirectoryRetention.Enforce(
            _options.BridgeScreenshotsDir,
            _options.Vision.PendingScreenshotMaxFiles,
            _options.Vision.PendingScreenshotMaxAgeHours,
            "*.png",
            "*.jpg",
            "*.jpeg");
        if (removed > 0)
        {
            InvalidateDirectoryActivitySnapshot();
        }

        return removed;
    }

    public IReadOnlyList<OutboxListing> GetOutboxListings()
    {
        if (!Directory.Exists(_options.BridgeOutboxDir))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(_options.BridgeOutboxDir, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new OutboxListing
                    {
                        FileName = info.Name,
                        WrittenAtUtc = info.LastWriteTimeUtc,
                        SizeBytes = info.Length,
                    };
                })
                .OrderBy(listing => listing.WrittenAtUtc)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public int ClearOutbox()
    {
        if (!Directory.Exists(_options.BridgeOutboxDir))
        {
            return 0;
        }

        int removed = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_options.BridgeOutboxDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Adapter.Logger.Warning($"Failed to clear outbox entry {Path.GetFileName(file)}: {DescribeOutboxEntryDeleteFailure(ex)}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Adapter.Logger.Warning($"Failed to enumerate outbox entries for clearing: {DescribeOutboxEnumerationFailure(ex)}");
        }

        if (removed > 0)
        {
            InvalidateDirectoryActivitySnapshot();
        }

        return removed;
    }

    private async Task WriteOutboxReplyAsync(OutboxChatReply payload, CancellationToken cancellationToken)
    {
        try
        {
            _options.EnsureDirectories();
            var envelope = new OutboxEnvelope
            {
                EventType = "chat_reply",
                Source = "palllm",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = payload,
            };

            string fileName = $"chat_reply-{envelope.TimestampUtc:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json";
            string path = Path.Combine(_options.BridgeOutboxDir, fileName);
            await using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        envelope,
                        OutboxJsonContext.OutboxEnvelope,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // Enforce retention synchronously so an unattended outbox can never grow
            // unbounded. Cap is configurable; default 100 files / 24 hours.
            DirectoryRetention.Enforce(
                _options.BridgeOutboxDir,
                _options.Bridge.OutboxMaxFiles,
                _options.Bridge.OutboxMaxAgeHours,
                "*.json");
            RememberOutboxReply(payload, envelope.TimestampUtc, envelope.Source);
            InvalidateDirectoryActivitySnapshot();
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled - do not treat as an outbox failure.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Outbox failures must not break the chat response - log and move on.
            Adapter.Logger.Warning($"Outbox write failed: {DescribeOutboxWriteFailure(ex)}");
        }
    }

    private void Archive(string file, string archiveRoot)
    {
        Directory.CreateDirectory(archiveRoot);
        string destination = Path.Combine(archiveRoot, Path.GetFileName(file));
        if (File.Exists(destination))
        {
            destination = Path.Combine(
                archiveRoot,
                $"{Path.GetFileNameWithoutExtension(file)}-{Guid.NewGuid():N}{Path.GetExtension(file)}");
        }

        File.Move(file, destination);

        // Archive directories can fill up fast - every drained bridge event and every
        // processed screenshot lands here. Enforce retention immediately so a long
        // session cannot run the disk out. Archive and failed have separate caps.
        bool isFailed = string.Equals(archiveRoot, _options.BridgeFailedDir, StringComparison.OrdinalIgnoreCase);
        int maxFiles = isFailed ? _options.Bridge.FailedMaxFiles : _options.Bridge.ArchiveMaxFiles;
        int maxAge = isFailed ? _options.Bridge.FailedMaxAgeHours : _options.Bridge.ArchiveMaxAgeHours;
        DirectoryRetention.Enforce(archiveRoot, maxFiles, maxAge);
        InvalidateDirectoryActivitySnapshot();
    }

    private static readonly JsonSerializerOptions OutboxSerializerOptions = PalLlmDomainJsonOptions.Create(static options =>
    {
        options.WriteIndented = false;
        options.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

    private static readonly PalLlmDomainJsonSerializerContext OutboxJsonContext = new(OutboxSerializerOptions);
}
