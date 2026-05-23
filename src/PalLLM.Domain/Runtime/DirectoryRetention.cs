namespace PalLLM.Domain.Runtime;

/// <summary>
/// Enforces a bounded-size + bounded-age retention policy on a single directory.
/// Runs synchronously, deterministically, and without materializing the whole
/// file listing. Designed to be called right after a new file is written or
/// archived so unbounded growth is impossible even when no consumer drains the
/// directory.
/// </summary>
internal static class DirectoryRetention
{
    public static int Enforce(string directory, int maxFiles, int maxAgeHours, params string[] patterns)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        if (maxFiles <= 0 && maxAgeHours <= 0)
        {
            return 0;
        }

        string[] normalizedPatterns = patterns is { Length: > 0 }
            ? patterns
            : ["*"];

        int removed = 0;
        DateTime ageCutoff = maxAgeHours > 0
            ? DateTime.UtcNow.AddHours(-maxAgeHours)
            : DateTime.MinValue;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PriorityQueue<FileInfo, long>? newestSurvivors = maxFiles > 0
            ? new PriorityQueue<FileInfo, long>(maxFiles + 1)
            : null;
        var directoryInfo = new DirectoryInfo(directory);

        try
        {
            foreach (string pattern in normalizedPatterns)
            {
                foreach (FileInfo file in directoryInfo.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
                {
                    if (!seen.Add(file.FullName))
                    {
                        continue;
                    }

                    if (maxAgeHours > 0 && file.LastWriteTimeUtc < ageCutoff)
                    {
                        if (TryDelete(file))
                        {
                            removed++;
                            continue;
                        }
                    }

                    if (newestSurvivors is not null &&
                        TryEnforceMaxFiles(newestSurvivors, file, maxFiles))
                    {
                        removed++;
                    }
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return removed;
        }

        return removed;
    }

    private static bool TryEnforceMaxFiles(
        PriorityQueue<FileInfo, long> newestSurvivors,
        FileInfo file,
        int maxFiles)
    {
        long priority = file.LastWriteTimeUtc.Ticks;
        if (newestSurvivors.Count < maxFiles)
        {
            newestSurvivors.Enqueue(file, priority);
            return false;
        }

        if (newestSurvivors.TryPeek(out _, out long oldestRetainedTicks) &&
            priority > oldestRetainedTicks)
        {
            FileInfo oldestRetained = newestSurvivors.Peek();
            if (TryDelete(oldestRetained))
            {
                newestSurvivors.Dequeue();
                newestSurvivors.Enqueue(file, priority);
                return true;
            }

            return TryDelete(file);
        }

        return TryDelete(file);
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
