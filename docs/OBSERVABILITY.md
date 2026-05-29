# Observability — OpenTelemetry traces, spans, and metrics

Last audited: `2026-05-24`

PalLLM emits OpenTelemetry traces and spans the moment an OTLP
collector is wired up via the `OTEL_EXPORTER_OTLP_ENDPOINT` env
var. By default — and on every release ZIP straight out of the box
— it does **not** export anything. Tracing is genuinely opt-in and
there's no overhead until you turn it on.

This doc is the single-stop tour: what packages we use, what spans
the runtime emits, how to wire up a collector locally, and what the
spans look like on the wire.

## TL;DR — turn on tracing in 30 seconds

```powershell
# 1. Run a Jaeger or any other OTLP-aware collector. Quickest:
docker run -d --name jaeger -p 4317:4317 -p 16686:16686 `
    jaegertracing/all-in-one:latest

# 2. Set the env var, then start the sidecar:
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/PalLLM.Sidecar/PalLLM.Sidecar.csproj

# 3. Make a chat request, then open Jaeger:
#    http://localhost:16686/  -> select service "PalLLM" -> Find traces
```

You'll see one trace per HTTP request, with child spans for the
chat orchestration, the inference call (if enabled), the bridge
drain (if it ran during the request), and the per-builder posture
captures.

## What's emitted

The runtime owns one `ActivitySource` named **`PalLLM.Runtime`**
(see `src/PalLLM.Domain/Runtime/PalLlmTelemetry.cs`). When no
listener is attached, the activity-source calls return `null` and
the runtime cost is a single branch — safe to leave on in
production.

The Sidecar adds three additional sources via NuGet packages:

| Package | Source name | Spans |
|---|---|---|
| `OpenTelemetry.Instrumentation.AspNetCore` | `Microsoft.AspNetCore.*` | One root span per inbound HTTP request |
| `OpenTelemetry.Instrumentation.Http` | `System.Net.Http.*` | One span per outbound `HttpClient` call (inference, vision, TTS, MCP upstream proxy) |
| `OpenTelemetry.Extensions.Hosting` | n/a (host glue) | Wires the above into the ASP.NET Core host's lifetime |

OTLP export uses `OpenTelemetry.Exporter.OpenTelemetryProtocol` and
defaults to gRPC on port 4317. Set `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`
to switch to HTTP on port 4318.

## Span inventory

PalLLM emits three named spans on the `PalLLM.Runtime` activity
source. Auto-instrumentation from `Microsoft.AspNetCore.*` and
`System.Net.Http.*` produces the inbound and outbound HTTP wrapper
spans automatically.

| Span | Source | When | Tags |
|---|---|---|---|
| `pal.chat` | `PalLLM.Runtime` | Once per `ChatAsync` call (`PalLlmRuntime.cs`) | `pal.request_id`, `pal.character_id`, `pal.task_tag`, `pal.visual_context_source`, `pal.inference_model`, `pal.inference_profile`, `pal.inference_lane`, `pal.inference_thinking_requested`, `pal.inference_preserve_thinking_requested`, `pal.response_path`, `pal.used_fallback`, `pal.fallback_strategy`, `pal.inference_attempted` |
| `pal.model_tier.transition` | `PalLLM.Runtime` | Once per tier change (`ModelTierOrchestrator.cs`) | `pal.model_tier.previous`, `pal.model_tier.current`, `pal.model_tier.model`, `pal.model_tier.available_count` |
| `<model-id>` (per GenAI client call) | `PalLLM.Runtime` | Per upstream inference HTTP call (`GenAiTelemetry.cs`) | OpenTelemetry GenAI semantic-convention attributes (model, latency, token counts) |
| (auto) inbound HTTP | `Microsoft.AspNetCore.*` | One per inbound HTTP request | OTel HTTP server semconv |
| (auto) outbound HTTP | `System.Net.Http.*` | One per outbound `HttpClient` call | OTel HTTP client semconv |

The GenAI span and metric provider label stays low-cardinality. Hosted
providers use their vendor label when the host is known; local runtimes are
classified only from stable host/path hints or loopback/LAN default-port hints
(`llama.cpp` (default), `lmstudio`, `vllm`, `sglang`, `tensorrt_llm`,
`openvino`, `foundry_local`, or `transformers`). Ambiguous endpoints, including
a plain `localhost:8000/v1/`, stay `openai_compatible` until the host/path gives
a clearer signal.

The `pal.chat` span is the per-turn root for PalLLM-emitted work
and carries every per-turn diagnostic tag including the fallback
strategy id and the response path. A typical chat request trace
in Jaeger reads:

```
HTTP POST /api/chat                   (Microsoft.AspNetCore.*)
└── pal.chat                           (PalLLM.Runtime, all tags above)
    └── HTTP POST <inference endpoint> (System.Net.Http.*)
        └── <model-id>                  (PalLLM.Runtime, GenAI semconv)
```

When inference is off (default), the inner HTTP/model spans are
absent and the `pal.chat` span's `pal.used_fallback=true` /
`pal.fallback_strategy=<id>` tags carry the decision evidence.

Earlier versions of this doc described a richer hierarchy
(`Chat.Plan`, `Chat.Fallback`, `Bridge.Drain`, `Memory.Recall`,
`Posture.Capture`, `Vision.Describe`, `TTS.Synthesize`) as separate
named spans. That was aspirational, not shipped — the actual code
emits the spans above. Tags on `pal.chat` cover the same diagnostic
ground for per-turn analysis. Subsystem-specific spans for bridge,
memory, posture, vision, and TTS remain a roadmap option but are
not currently emitted.

## Wiring the collector

The sidecar registers OpenTelemetry only when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set. The relevant code is in
`Program.cs`:

```csharp
string? otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("PalLLM"))
        .WithTracing(t => t
            .AddSource("PalLLM.Runtime")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter());
}
```

When the env var is unset, the OpenTelemetry packages contribute
zero per-request overhead — no listeners, no exporter, no buffer.
This is verified by the `airgap-default` test: a default-config
sidecar makes zero outbound network calls.

## Local Jaeger workflow

```powershell
# Boot Jaeger
docker run -d --name jaeger `
    -p 4317:4317 `   # OTLP gRPC ingest
    -p 4318:4318 `   # OTLP HTTP ingest
    -p 16686:16686 ` # Jaeger UI
    jaegertracing/all-in-one:latest

# Run the sidecar with OTLP wired up
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
$env:OTEL_EXPORTER_OTLP_PROTOCOL = "grpc"   # default; set to "http/protobuf" for the 4318 port
dotnet run --project src/PalLLM.Sidecar/PalLLM.Sidecar.csproj

# In another window, fire a chat request
curl -s -X POST http://localhost:5088/api/chat `
    -H "Content-Type: application/json" `
    -d '{"userMessage":"hi","characterId":1}'

# Open Jaeger
Start-Process http://localhost:16686/
# Pick service "PalLLM" -> Find Traces -> click the latest trace
```

The trace tree shows the HTTP request span at the top (from
`Microsoft.AspNetCore.*` auto-instrumentation), with the `pal.chat`
span as a child carrying every per-turn diagnostic tag, and — when
inference fires — the outbound `HttpClient` span and the GenAI
model-named child span below it.

## Spans during deterministic fallback

When inference is off (default) or the breaker is open, you still
get one trace per request. The structure is:

- Inbound HTTP root span from `Microsoft.AspNetCore.*`.
- `pal.chat` child span with these tags carrying the full
  fallback story:
  - `pal.response_path` — which fallback path fired
    (`fallback-after-inference-disabled`,
    `fallback-after-breaker-open`, etc. — see
    `STATE_MACHINES.md` §5 for the full enum).
  - `pal.used_fallback` — `true`.
  - `pal.fallback_strategy` — names the strategy
    (`narrative-recall`, `tactical-suggest`, etc.).
  - `pal.inference_attempted` — `false` if the path was
    inference-disabled, `true` if inference was tried and failed.

There are no separate child spans for fallback / memory / posture
in the current code — the tags above carry the same per-turn
diagnostic information without paying the multi-span cost on
every turn. That tradeoff is intentional given how often the
fallback path fires.

## Cloud collectors

Same pattern as local Jaeger. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to
the collector's URL, set `OTEL_EXPORTER_OTLP_PROTOCOL` if you need
HTTP, and (depending on the vendor) set
`OTEL_EXPORTER_OTLP_HEADERS` to whatever auth header they require.
The runtime ships no vendor-specific code.

Verified-in-CI vendor-neutral pairings:
- Jaeger (self-hosted)
- Tempo (Grafana)
- Honeycomb (`OTEL_EXPORTER_OTLP_HEADERS=x-honeycomb-team=YOUR_API_KEY`)
- Lightstep / ServiceNow Cloud Observability

## Privacy posture

Tracing is opt-in. With `OTEL_EXPORTER_OTLP_ENDPOINT` unset, the
sidecar never opens a connection to any collector. Set, the
sidecar emits to the named endpoint *only* — there is no fallback
"upload anyway" path.

`/api/privacy/posture` reports the OTLP surface explicitly under
the `telemetry-otlp` entry: `never-leaves` when unset,
`active-outbound` when set. See
[`adr/0006-opt-in-everything-by-default.md`](adr/0006-opt-in-everything-by-default.md).

## Disabling specific instrumentation

Sometimes you want PalLLM-only spans without the AspNetCore /
HttpClient noise. Pass the OTLP endpoint as before, then set:

```powershell
$env:PALLLM_OTLP_DISABLE_ASPNETCORE = "1"
$env:PALLLM_OTLP_DISABLE_HTTPCLIENT = "1"
```

The sidecar's tracing wireup checks these and skips the
corresponding instrumentation. Useful for performance benchmarking
when the third-party AspNetCore / Http span overhead is the
variable you're measuring against.

## Related

- Code: `src/PalLLM.Domain/Runtime/PalLlmTelemetry.cs`,
  `src/PalLLM.Sidecar/Configuration/PalLlmObservabilityServiceCollectionExtensions.cs`
  (OpenTelemetry wireup)
- Docs: [`OPERATIONS.md`](OPERATIONS.md) § "Enabling distributed
  tracing", [`PRIVACY.md`](PRIVACY.md) § "Telemetry",
  [`adr/0006-opt-in-everything-by-default.md`](adr/0006-opt-in-everything-by-default.md)
