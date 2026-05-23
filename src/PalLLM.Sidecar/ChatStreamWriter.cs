using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PalLLM.Sidecar;

/// <summary>
/// Minimal Server-Sent-Events (SSE) writer used by
/// <c>POST /api/chat/stream</c>. Emits events in the standard SSE
/// <c>event: &lt;name&gt;\ndata: &lt;json&gt;\n\n</c> format and flushes
/// immediately so clients see progress before the final payload lands.
///
/// <para>Deliberately tiny: we don't pull in <c>System.Net.ServerSentEvents</c>
/// (.NET 10 has a preview, but it's heavier than we need for one-shot
/// chat progress). The format is exactly what every browser's
/// <c>EventSource</c> and every MCP SSE client already speaks.</para>
/// </summary>
internal static class ChatStreamWriter
{
    public static async Task EmitAsync<T>(
        HttpResponse response,
        string eventName,
        T payload,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        string json = JsonSerializer.Serialize(payload, jsonTypeInfo);
        StringBuilder sb = new(json.Length + eventName.Length + 16);
        sb.Append("event: ");
        sb.Append(eventName);
        sb.Append('\n');
        sb.Append("data: ");
        sb.Append(json);
        sb.Append("\n\n");

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
