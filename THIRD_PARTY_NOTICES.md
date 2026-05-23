# Third-party Notices

PalLLM depends at runtime on the components listed below. None of them
are redistributed with this repository; operators install each one
separately. Each component is covered by its own license, which the
operator must review before use.

## Runtime dependencies

### .NET Runtime (Microsoft)

PalLLM targets the `net10.0` framework (LTS, supported through November
2028) for both the Domain class library
and the Sidecar ASP.NET Core host. The .NET runtime is distributed by
its owner under its own terms.

### Unreal Engine scripting framework

The in-game bridge is a Lua module loaded by a third-party Unreal Engine
scripting framework that the operator installs separately into their
game's `Win64` directory. The framework is distributed by its owner
under its own license. PalLLM does not redistribute it.

### Portable adapter surface (inlined, no external reference)

`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs` contains the
full portable adapter surface — `IGameAdapter`, `IWorldClock`,
`IPathProvider`, `ICharacter`, `ILogger`, `Vec3`, `SemanticEmbedder`,
and `ResponseCleanup` — directly inside this repository. There is no
project reference to any external adapter library, no NuGet package
for this surface, and no code is pulled from outside at build time.
The surface is MIT-licensed as part of this repository and can be
copied verbatim by other LLM-companion runtimes (see
[`docs/CORE_LIBRARY.md`](docs/CORE_LIBRARY.md)).

### HTTP-reachable inference, vision, and TTS servers

At operator option, PalLLM calls out to a user-chosen HTTP endpoint
implementing:

- the OpenAI-style `POST /v1/chat/completions` JSON schema for text
  inference,
- the same schema with multimodal `image_url` content parts for vision,
- a `POST { "text", "voice" } → audio/*` shape for TTS.

PalLLM does not ship any of these servers. Any model tag, voice id, or
host string appearing in `appsettings.json` is an illustrative
placeholder; the operator must supply their own endpoint and comply
with its license.

## NuGet package references

The table below is the local publication-audit coverage list for current
`PackageReference` entries. License expressions use SPDX identifiers. Treat
the package's own nuspec/license URL as authoritative if a package owner
changes terms in a future version.

| Package | Scope | SPDX license expression |
|---|---|---|
| `Microsoft.AspNetCore.OpenApi` | Runtime/build OpenAPI support | `MIT` |
| `Microsoft.Extensions.ApiDescription.Server` | Build-time OpenAPI export | `MIT` |
| `ModelContextProtocol.AspNetCore` | Runtime MCP server surface | `Apache-2.0` |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | Optional OTLP tracing export | `Apache-2.0` |
| `OpenTelemetry.Extensions.Hosting` | Optional OpenTelemetry hosting integration | `Apache-2.0` |
| `OpenTelemetry.Instrumentation.AspNetCore` | Optional ASP.NET Core tracing instrumentation | `Apache-2.0` |
| `OpenTelemetry.Instrumentation.Http` | Optional outbound HTTP tracing instrumentation | `Apache-2.0` |
| `Microsoft.AspNetCore.Mvc.Testing` | Test-only in-process ASP.NET Core fixture | `MIT` |
| `Microsoft.NET.Test.Sdk` | Test-only NUnit execution host | `MIT` |
| `NUnit` | Test-only framework | `MIT` |
| `NUnit.Analyzers` | Test-only analyzer package | `MIT` |
| `NUnit3TestAdapter` | Test-only adapter package | `MIT` |
| `coverlet.collector` | Test-only coverage collector | `MIT` |

## Operator obligations

By installing PalLLM the operator agrees to review and comply with the
licenses of every component listed above, plus any other component the
operator configures PalLLM to call into at runtime.
