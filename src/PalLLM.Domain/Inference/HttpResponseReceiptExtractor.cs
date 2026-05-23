using System.Globalization;
using System.Net.Http.Headers;

namespace PalLLM.Domain.Inference;

internal static class HttpResponseReceiptExtractor
{
    private const int MaxIdentifierLength = 128;
    private const double MaxProcessingMs = 86_400_000d;

    private static readonly string[] UpstreamRequestIdHeaders =
    [
        "x-request-id",
        "request-id",
        "x-correlation-id",
    ];

    private static readonly string[] UpstreamProcessingMsHeaders =
    [
        "openai-processing-ms",
        "x-processing-ms",
        "x-processing-time-ms",
        "x-upstream-service-time",
    ];

    private static readonly string[] UpstreamQueueMsHeaders =
    [
        "x-queue-ms",
        "x-request-queue-ms",
        "x-upstream-queue-ms",
    ];

    private static readonly string[] UpstreamTimeToFirstTokenMsHeaders =
    [
        "x-ttft-ms",
        "x-time-to-first-token-ms",
        "x-time-to-first-chunk-ms",
    ];

    private static readonly string[] UpstreamPrefillMsHeaders =
    [
        "x-prefill-ms",
        "x-request-prefill-ms",
        "x-upstream-prefill-ms",
    ];

    private static readonly string[] UpstreamDecodeMsHeaders =
    [
        "x-decode-ms",
        "x-request-decode-ms",
        "x-upstream-decode-ms",
    ];

    public static string GetUpstreamRequestId(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        foreach (string headerName in UpstreamRequestIdHeaders)
        {
            string value = GetFirstHeaderValue(response.Headers, headerName);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            if (response.Content is not null)
            {
                value = GetFirstHeaderValue(response.Content.Headers, headerName);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    public static double? GetUpstreamProcessingMs(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        foreach (string headerName in UpstreamProcessingMsHeaders)
        {
            double? value = GetFirstProcessingHeaderValue(response.Headers, headerName);
            if (value is not null)
            {
                return value;
            }

            if (response.Content is not null)
            {
                value = GetFirstProcessingHeaderValue(response.Content.Headers, headerName);
                if (value is not null)
                {
                    return value;
                }
            }
        }

        return GetServerTimingDurationMs(response.Headers)
            ?? (response.Content is null ? null : GetServerTimingDurationMs(response.Content.Headers));
    }

    public static UpstreamPhaseTimingReceipt GetUpstreamPhaseTimings(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        UpstreamPhaseTimingReceipt receipt = UpstreamPhaseTimingReceipt.Empty;
        receipt = MergeHeaderTimings(receipt, response.Headers);
        receipt = MergeServerTimingPhases(receipt, response.Headers);

        if (response.Content is not null)
        {
            receipt = MergeHeaderTimings(receipt, response.Content.Headers);
            receipt = MergeServerTimingPhases(receipt, response.Content.Headers);
        }

        return receipt;
    }

    public static string NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxIdentifierLength)
        {
            return string.Empty;
        }

        foreach (char ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                return string.Empty;
            }
        }

        return trimmed;
    }

    public static double? NormalizeProcessingMs(double? value)
    {
        if (value is null || !double.IsFinite(value.Value) || value.Value < 0 || value.Value > MaxProcessingMs)
        {
            return null;
        }

        return Math.Round(value.Value, 3, MidpointRounding.AwayFromZero);
    }

    public static UpstreamPhaseTimingReceipt NormalizePhaseTimings(UpstreamPhaseTimingReceipt receipt) =>
        new(
            NormalizeProcessingMs(receipt.QueueMs),
            NormalizeProcessingMs(receipt.TimeToFirstTokenMs),
            NormalizeProcessingMs(receipt.PrefillMs),
            NormalizeProcessingMs(receipt.DecodeMs));

    private static string GetFirstHeaderValue(HttpHeaders headers, string headerName)
    {
        if (!headers.TryGetValues(headerName, out IEnumerable<string>? values))
        {
            return string.Empty;
        }

        foreach (string value in values)
        {
            string normalized = NormalizeIdentifier(value);
            if (!string.IsNullOrEmpty(normalized))
            {
                return normalized;
            }
        }

        return string.Empty;
    }

    private static double? GetFirstProcessingHeaderValue(HttpHeaders headers, string headerName)
    {
        if (!headers.TryGetValues(headerName, out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string value in values)
        {
            double? normalized = ParseProcessingMilliseconds(value);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return null;
    }

    private static double? GetFirstProcessingHeaderValue(HttpHeaders headers, IEnumerable<string> headerNames)
    {
        foreach (string headerName in headerNames)
        {
            double? value = GetFirstProcessingHeaderValue(headers, headerName);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static double? GetServerTimingDurationMs(HttpHeaders headers)
    {
        if (!headers.TryGetValues("Server-Timing", out IEnumerable<string>? values))
        {
            return null;
        }

        double? firstDuration = null;
        foreach (string value in values)
        {
            foreach (string metric in SplitHeaderValue(value, ','))
            {
                double? duration = TryGetServerTimingMetricDuration(metric);
                if (duration is null)
                {
                    continue;
                }

                firstDuration ??= duration;
                string metricName = GetServerTimingMetricName(metric);
                if (IsPreferredServerTimingMetric(metricName))
                {
                    return duration;
                }
            }
        }

        return firstDuration;
    }

    private static UpstreamPhaseTimingReceipt MergeHeaderTimings(
        UpstreamPhaseTimingReceipt current,
        HttpHeaders headers)
    {
        double? queueMs = current.QueueMs ?? GetFirstProcessingHeaderValue(headers, UpstreamQueueMsHeaders);
        double? timeToFirstTokenMs =
            current.TimeToFirstTokenMs ?? GetFirstProcessingHeaderValue(headers, UpstreamTimeToFirstTokenMsHeaders);
        double? prefillMs = current.PrefillMs ?? GetFirstProcessingHeaderValue(headers, UpstreamPrefillMsHeaders);
        double? decodeMs = current.DecodeMs ?? GetFirstProcessingHeaderValue(headers, UpstreamDecodeMsHeaders);
        return new UpstreamPhaseTimingReceipt(queueMs, timeToFirstTokenMs, prefillMs, decodeMs);
    }

    private static UpstreamPhaseTimingReceipt MergeServerTimingPhases(
        UpstreamPhaseTimingReceipt current,
        HttpHeaders headers)
    {
        if (!headers.TryGetValues("Server-Timing", out IEnumerable<string>? values))
        {
            return current;
        }

        double? queueMs = current.QueueMs;
        double? timeToFirstTokenMs = current.TimeToFirstTokenMs;
        double? prefillMs = current.PrefillMs;
        double? decodeMs = current.DecodeMs;
        foreach (string value in values)
        {
            foreach (string metric in SplitHeaderValue(value, ','))
            {
                double? duration = TryGetServerTimingMetricDuration(metric);
                if (duration is null)
                {
                    continue;
                }

                string metricName = NormalizeMetricName(GetServerTimingMetricName(metric));
                if (queueMs is null && IsQueueTimingMetric(metricName))
                {
                    queueMs = duration;
                }
                else if (timeToFirstTokenMs is null && IsTimeToFirstTokenTimingMetric(metricName))
                {
                    timeToFirstTokenMs = duration;
                }
                else if (prefillMs is null && IsPrefillTimingMetric(metricName))
                {
                    prefillMs = duration;
                }
                else if (decodeMs is null && IsDecodeTimingMetric(metricName))
                {
                    decodeMs = duration;
                }
            }
        }

        return new UpstreamPhaseTimingReceipt(queueMs, timeToFirstTokenMs, prefillMs, decodeMs);
    }

    private static double? TryGetServerTimingMetricDuration(string metric)
    {
        foreach (string directive in SplitHeaderValue(metric, ';').Skip(1))
        {
            string trimmed = directive.Trim();
            if (!trimmed.StartsWith("dur", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            string rawValue = trimmed[(equalsIndex + 1)..].Trim().Trim('"');
            return ParseProcessingMilliseconds(rawValue);
        }

        return null;
    }

    private static string GetServerTimingMetricName(string metric)
    {
        int semicolonIndex = metric.IndexOf(';');
        string name = semicolonIndex < 0 ? metric : metric[..semicolonIndex];
        return name.Trim();
    }

    private static bool IsPreferredServerTimingMetric(string metricName) =>
        metricName.Equals("inference", StringComparison.OrdinalIgnoreCase) ||
        metricName.Equals("model", StringComparison.OrdinalIgnoreCase) ||
        metricName.Equals("generation", StringComparison.OrdinalIgnoreCase) ||
        metricName.Equals("completion", StringComparison.OrdinalIgnoreCase) ||
        metricName.Equals("openai", StringComparison.OrdinalIgnoreCase) ||
        metricName.Equals("upstream", StringComparison.OrdinalIgnoreCase) ||
        metricName.Equals("total", StringComparison.OrdinalIgnoreCase) ||
        metricName.Equals("app", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMetricName(string metricName)
    {
        if (string.IsNullOrWhiteSpace(metricName) || metricName.Length > MaxIdentifierLength)
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[metricName.Length];
        int written = 0;
        foreach (char ch in metricName)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[written++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer[..written]);
    }

    private static bool IsQueueTimingMetric(string metricName) =>
        metricName is "queue" or "requestqueue" or "requestqueuetime" or "timeinqueue" or "wait" or "waiting" or "schedulerqueue";

    private static bool IsTimeToFirstTokenTimingMetric(string metricName) =>
        metricName is "ttft" or "tft" or "timetofirsttoken" or "timetofirstchunk" or "firsttoken" or "firstchunk";

    private static bool IsPrefillTimingMetric(string metricName) =>
        metricName is "prefill" or "requestprefill" or "requestprefilltime" or "promptprefill" or "contextprefill";

    private static bool IsDecodeTimingMetric(string metricName) =>
        metricName is "decode" or "requestdecode" or "requestdecodetime" or "tokendecode" or "generationdecode";

    private static double? ParseProcessingMilliseconds(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return double.TryParse(
            rawValue.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed)
            ? NormalizeProcessingMs(parsed)
            : null;
    }

    private static IEnumerable<string> SplitHeaderValue(string value, char separator)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield break;
        }

        int start = 0;
        bool inQuotes = false;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (current == separator && !inQuotes)
            {
                yield return value[start..index];
                start = index + 1;
            }
        }

        yield return value[start..];
    }
}

internal readonly record struct UpstreamPhaseTimingReceipt(
    double? QueueMs,
    double? TimeToFirstTokenMs,
    double? PrefillMs,
    double? DecodeMs)
{
    public static UpstreamPhaseTimingReceipt Empty { get; } = new(null, null, null, null);
}
