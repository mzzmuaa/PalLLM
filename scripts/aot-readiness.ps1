<#
.SYNOPSIS
    Checks whether the sidecar is still friendly to trimming and Native AOT probes.

.DESCRIPTION
    Static, local-only readiness scan for the .NET sidecar. The default mode
    does not publish, build, contact package registries, or require a native
    compiler toolchain. It verifies the source-generated configuration and JSON
    metadata paths that keep startup lean, then highlights reflection/dynamic
    code risks that matter before attempting Native AOT.

    Use -PublishProbe when you intentionally want to run a Native AOT publish
    experiment for the selected runtime identifier.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$OutputRoot = "",
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$PublishProbe,
    [switch]$Strict,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$repoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
    $OutputRoot = Join-Path $repoRoot "artifacts\aot-readiness\$timestamp"
}

$outputRootPath = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot
}
else {
    Join-Path $repoRoot $OutputRoot
}

New-Item -ItemType Directory -Force -Path $outputRootPath | Out-Null

$resultsPath = Join-Path $outputRootPath "RESULTS.md"
$jsonPath = Join-Path $outputRootPath "aot-readiness.json"
$checks = [System.Collections.Generic.List[object]]::new()

function ConvertTo-RelativeRepoPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($repoRoot)
    if (-not $base.EndsWith([IO.Path]::DirectorySeparatorChar.ToString(), [StringComparison]::Ordinal)) {
        $base += [IO.Path]::DirectorySeparatorChar
    }

    $baseUri = [Uri]$base
    $fullUri = [Uri]$full
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($fullUri).ToString()).Replace('/', '\')
}

function Get-RepoText {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing expected file: $RelativePath"
    }

    return Get-Content -LiteralPath $path -Raw
}

function Add-Check {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][ValidateSet("pass", "warn", "fail", "skip")][string]$Status,
        [Parameter(Mandatory = $true)][string]$Summary,
        [string[]]$Details = @()
    )

    $checks.Add([pscustomobject]@{
        Id = $Id
        Status = $Status
        Summary = $Summary
        Details = @($Details)
    }) | Out-Null
}

function Test-TextContainsAll {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string[]]$Needles
    )

    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($needle in $Needles) {
        if ($Text.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) {
            $missing.Add($needle) | Out-Null
        }
    }

    return @($missing)
}

function Select-SourcePatternHits {
    param(
        [Parameter(Mandatory = $true)][string]$Pattern,
        [int]$Limit = 25
    )

    $hits = [System.Collections.Generic.List[string]]::new()
    $sourceRoots = @(
        (Join-Path $repoRoot "src\PalLLM.Domain"),
        (Join-Path $repoRoot "src\PalLLM.Sidecar")
    )

    foreach ($sourceRoot in $sourceRoots) {
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
            continue
        }

        foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -Filter "*.cs" -File) {
            if ($file.FullName -match '\\(bin|obj)\\') {
                continue
            }

            $matches = Select-String -LiteralPath $file.FullName -Pattern $Pattern -AllMatches
            foreach ($match in $matches) {
                $relative = ConvertTo-RelativeRepoPath -Path $file.FullName
                $line = $match.Line.Trim()
                $hits.Add(("{0}:{1}: {2}" -f $relative, $match.LineNumber, $line)) | Out-Null
                if ($hits.Count -ge $Limit) {
                    return @($hits)
                }
            }
        }
    }

    return @($hits)
}

function Invoke-PublishProbe {
    $probeRoot = Join-Path $outputRootPath "publish-probe"
    $publishLog = Join-Path $outputRootPath "dotnet-publish-aot.log"
    New-Item -ItemType Directory -Force -Path $probeRoot | Out-Null

    $project = Join-Path $repoRoot "src\PalLLM.Sidecar\PalLLM.Sidecar.csproj"
    $args = @(
        "publish",
        $project,
        "--configuration", "Release",
        "--runtime", $RuntimeIdentifier,
        "--self-contained", "true",
        "--output", $probeRoot,
        "-p:PublishAot=true",
        "-p:PublishTrimmed=true",
        "-p:InvariantGlobalization=true",
        "-p:IlcGenerateCompleteTypeMetadata=false",
        "--nologo",
        "--verbosity", "minimal"
    )

    $captured = & dotnet @args 2>&1
    $exitCode = $LASTEXITCODE
    Set-Content -LiteralPath $publishLog -Value ($captured -join [Environment]::NewLine) -Encoding UTF8

    $warningLines = @($captured | Where-Object { $_ -match '\b(IL2\d{3}|IL3\d{3}|ILC\d{4}|warning)\b' } | ForEach-Object { $_.ToString() })
    $details = [System.Collections.Generic.List[string]]::new()
    $details.Add(("RID: {0}" -f $RuntimeIdentifier)) | Out-Null
    $details.Add(("Log: {0}" -f (ConvertTo-RelativeRepoPath -Path $publishLog))) | Out-Null

    if ($exitCode -ne 0) {
        $details.Add(("dotnet publish exited with code {0}" -f $exitCode)) | Out-Null
        Add-Check -Id "publish-probe" -Status "fail" -Summary "Native AOT publish probe failed." -Details @($details)
        return
    }

    if ($warningLines.Count -gt 0) {
        $details.AddRange(@($warningLines | Select-Object -First 20)) | Out-Null
        Add-Check -Id "publish-probe" -Status "warn" -Summary "Native AOT publish completed with warnings that need review." -Details @($details)
        return
    }

    Add-Check -Id "publish-probe" -Status "pass" -Summary "Native AOT publish probe completed without obvious warnings." -Details @($details)
}

$domainProjectText = Get-RepoText "src\PalLLM.Domain\PalLLM.Domain.csproj"
$sidecarProjectText = Get-RepoText "src\PalLLM.Sidecar\PalLLM.Sidecar.csproj"

$targetFrameworkDetails = [System.Collections.Generic.List[string]]::new()
if ($domainProjectText -match '<TargetFramework>net10\.0</TargetFramework>') {
    $targetFrameworkDetails.Add("PalLLM.Domain targets net10.0.") | Out-Null
}
else {
    $targetFrameworkDetails.Add("PalLLM.Domain should target net10.0 for current analyzer coverage.") | Out-Null
}

if ($sidecarProjectText -match '<TargetFramework>net10\.0</TargetFramework>') {
    $targetFrameworkDetails.Add("PalLLM.Sidecar targets net10.0.") | Out-Null
}
else {
    $targetFrameworkDetails.Add("PalLLM.Sidecar should target net10.0 for current ASP.NET Core AOT support.") | Out-Null
}

$targetFrameworkStatus = if ($targetFrameworkDetails -join "`n" -match 'should target') { "fail" } else { "pass" }
Add-Check -Id "target-framework" -Status $targetFrameworkStatus -Summary "Projects use the current .NET target framework for AOT analysis." -Details @($targetFrameworkDetails)

if ($sidecarProjectText.IndexOf("<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>", [StringComparison]::Ordinal) -ge 0) {
    Add-Check -Id "config-binding-sourcegen" -Status "pass" -Summary "Sidecar keeps source-generated configuration binding enabled." -Details @("Large PalLLM options tree binds without reflection-heavy startup binding.")
}
else {
    Add-Check -Id "config-binding-sourcegen" -Status "fail" -Summary "Sidecar is missing source-generated configuration binding." -Details @("Add <EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator> to src\PalLLM.Sidecar\PalLLM.Sidecar.csproj.")
}

if ($domainProjectText.IndexOf("<IsAotCompatible>true</IsAotCompatible>", [StringComparison]::Ordinal) -ge 0) {
    Add-Check -Id "domain-aot-analyzers" -Status "pass" -Summary "Portable domain assembly opts into trim, single-file, and AOT analyzers." -Details @(
        "PalLLM.Domain is the harvestable runtime surface; analyzer coverage keeps dynamic-code and reflection regressions visible during ordinary builds."
    )
}
else {
    Add-Check -Id "domain-aot-analyzers" -Status "fail" -Summary "Portable domain assembly is missing explicit AOT analyzer opt-in." -Details @(
        "Add <IsAotCompatible>true</IsAotCompatible> to src\PalLLM.Domain\PalLLM.Domain.csproj once the domain hot paths are source-generated and warning-free."
    )
}

$jsonContextFiles = @(
    "src\PalLLM.Domain\PalLlmDomainJsonSerializerContext.cs",
    "src\PalLLM.Sidecar\PalLlmJsonSerializerContext.cs"
)
$jsonDetails = [System.Collections.Generic.List[string]]::new()
$jsonStatus = "pass"
foreach ($relativePath in $jsonContextFiles) {
    $absolutePath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        $jsonStatus = "fail"
        $jsonDetails.Add(("Missing JSON source-generation context: {0}" -f $relativePath)) | Out-Null
        continue
    }

    $text = Get-Content -LiteralPath $absolutePath -Raw
    $jsonDetails.Add(("Found {0}" -f $relativePath)) | Out-Null
    if ($text.IndexOf("DefaultJsonTypeInfoResolver", [StringComparison]::Ordinal) -ge 0) {
        if ($relativePath -like "*PalLlmDomainJsonSerializerContext.cs") {
            $jsonStatus = "fail"
            $jsonDetails.Add(("{0} reintroduces reflection fallback via DefaultJsonTypeInfoResolver." -f $relativePath)) | Out-Null
        }
        else {
            if ($jsonStatus -eq "pass") { $jsonStatus = "warn" }
            $jsonDetails.Add(("{0} keeps a DefaultJsonTypeInfoResolver fallback for sidecar-only payloads; verify with -PublishProbe before trusting Native AOT." -f $relativePath)) | Out-Null
        }
    }
}
Add-Check -Id "json-sourcegen-contexts" -Status $jsonStatus -Summary "JSON serializer contexts exist and domain hot paths avoid reflection fallback." -Details @($jsonDetails)

$inferenceText = Get-RepoText "src\PalLLM.Domain\Inference\InferenceClient.cs"
$visionText = Get-RepoText "src\PalLLM.Domain\Inference\VisionClient.cs"
$ttsText = Get-RepoText "src\PalLLM.Domain\Inference\TtsClient.cs"
$sessionText = Get-RepoText "src\PalLLM.Domain\Runtime\SessionPersistence.cs"
$runtimeText = Get-RepoText "src\PalLLM.Domain\Runtime\PalLlmRuntime.cs"

$hotPathMissing = [System.Collections.Generic.List[string]]::new()
foreach ($missing in @(Test-TextContainsAll -Text $inferenceText -Needles @(
    "PalLlmDomainJsonSerializerContext.Default.InferenceChatCompletionsRequestBody",
    "PalLlmDomainJsonSerializerContext.Default.OllamaWarmupRequestBody"
))) { $hotPathMissing.Add($missing) | Out-Null }
foreach ($missing in @(Test-TextContainsAll -Text $visionText -Needles @(
    "PalLlmDomainJsonSerializerContext.Default.VisionChatCompletionsRequestBody"
))) { $hotPathMissing.Add($missing) | Out-Null }
foreach ($missing in @(Test-TextContainsAll -Text $ttsText -Needles @(
    "PalLlmDomainJsonSerializerContext.Default.TtsHttpRequestBody"
))) { $hotPathMissing.Add($missing) | Out-Null }
foreach ($missing in @(Test-TextContainsAll -Text $sessionText -Needles @(
    "JsonContext.SessionFile"
))) { $hotPathMissing.Add($missing) | Out-Null }
foreach ($missing in @(Test-TextContainsAll -Text $runtimeText -Needles @(
    "BridgeJsonContext.BridgeEventEnvelope",
    "OutboxJsonContext.OutboxEnvelope",
    "UiProbeDumpJsonContext"
))) { $hotPathMissing.Add($missing) | Out-Null }

if ($hotPathMissing.Count -eq 0) {
    Add-Check -Id "hotpath-json-metadata" -Status "pass" -Summary "Hot-path JSON calls use source-generated metadata markers." -Details @("Inference, vision, TTS, session, bridge, outbox, and ui-probe markers are present.")
}
else {
    Add-Check -Id "hotpath-json-metadata" -Status "fail" -Summary "One or more hot-path JSON source-generation markers are missing." -Details @($hotPathMissing)
}

$programText = Get-RepoText "src\PalLLM.Sidecar\Program.cs"
$sidecarJsonContextText = Get-RepoText "src\PalLLM.Sidecar\PalLlmJsonSerializerContext.cs"
$chatStreamText = Get-RepoText "src\PalLLM.Sidecar\ChatStreamWriter.cs"
$selfHealingText = Get-RepoText "src\PalLLM.Sidecar\SelfHealingStatusReader.cs"
$mcpToolsText = Get-RepoText "src\PalLLM.Sidecar\Mcp\PalLlmMcpTools.cs"
$sidecarDynamicJsonMarkers = [ordered]@{
    "health-object-data" = @{
        Text = $sidecarJsonContextText
        Pattern = "Dictionary<string, object?> Data"
    }
    "sse-object-payload" = @{
        Text = $chatStreamText
        Pattern = "object payload"
    }
    "bridge-clear-anonymous-response" = @{
        Text = $programText
        Pattern = "new { removed ="
    }
    "sse-anonymous-started" = @{
        Text = $programText
        Pattern = "new { request_id ="
    }
    "sse-anonymous-phase" = @{
        Text = $programText
        Pattern = "new { name ="
    }
    "self-healing-anonymous-marker" = @{
        Text = $selfHealingText
        Pattern = "JsonSerializer.Serialize(new { status, detail })"
    }
    "mcp-anonymous-status" = @{
        Text = $mcpToolsText
        Pattern = "new { status ="
    }
}
$sidecarDynamicJsonHits = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $sidecarDynamicJsonMarkers.GetEnumerator()) {
    $candidate = $entry.Value
    if ($candidate.Text.IndexOf($candidate.Pattern, [StringComparison]::Ordinal) -ge 0) {
        $sidecarDynamicJsonHits.Add(("{0}: {1}" -f $entry.Key, $candidate.Pattern)) | Out-Null
    }
}

if ($sidecarDynamicJsonHits.Count -eq 0) {
    Add-Check -Id "sidecar-sourcegen-payloads" -Status "pass" -Summary "Common sidecar-only JSON payloads use named source-generated shapes." -Details @("Health probe data, SSE progress frames, bridge clear responses, self-healing markers, and MCP status payloads avoid anonymous/object serialization.")
}
else {
    Add-Check -Id "sidecar-sourcegen-payloads" -Status "warn" -Summary "Sidecar JSON still has anonymous/object payload markers." -Details @($sidecarDynamicJsonHits)
}

$aspNetBlocked = @(
    "AddControllers(",
    "MapControllers(",
    "AddMvc(",
    "AddRazorPages(",
    "UseSession(",
    "MapRazorPages("
)
$aspNetHits = [System.Collections.Generic.List[string]]::new()
foreach ($blocked in $aspNetBlocked) {
    if ($programText.IndexOf($blocked, [StringComparison]::Ordinal) -ge 0) {
        $aspNetHits.Add($blocked) | Out-Null
    }
}

if ($aspNetHits.Count -eq 0 -and $programText.IndexOf("WebApplication.CreateBuilder", [StringComparison]::Ordinal) -ge 0) {
    Add-Check -Id "aspnet-aot-shape" -Status "pass" -Summary "Sidecar keeps the Minimal API shape that ASP.NET Core Native AOT supports best." -Details @("No MVC, Razor Pages, or Session markers found in Program.cs.")
}
else {
    $details = @($aspNetHits)
    if ($programText.IndexOf("WebApplication.CreateBuilder", [StringComparison]::Ordinal) -lt 0) {
        $details += "Program.cs does not use WebApplication.CreateBuilder."
    }
    Add-Check -Id "aspnet-aot-shape" -Status "warn" -Summary "Sidecar host shape has AOT review markers." -Details $details
}

$packageIds = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($projectFile in Get-ChildItem -LiteralPath (Join-Path $repoRoot "src") -Recurse -Filter "*.csproj" -File) {
    $projectText = Get-Content -LiteralPath $projectFile.FullName -Raw
    foreach ($match in [regex]::Matches($projectText, '<PackageReference\s+Include="([^"]+)"')) {
        $packageIds.Add($match.Groups[1].Value) | Out-Null
    }
}

$dependencyDetails = [System.Collections.Generic.List[string]]::new()
foreach ($packageId in $packageIds) {
    $dependencyDetails.Add(("PackageReference: {0}" -f $packageId)) | Out-Null
}

$dependencyStatus = "pass"
if ($packageIds.Contains("ModelContextProtocol.AspNetCore")) {
    $dependencyStatus = "warn"
    $dependencyDetails.Add("ModelContextProtocol.AspNetCore should be validated with -PublishProbe because MCP tool discovery may depend on reflection/metadata.") | Out-Null
}
if (($packageIds | Where-Object { $_ -like "OpenTelemetry.*" } | Measure-Object).Count -gt 0) {
    if ($dependencyStatus -eq "pass") { $dependencyStatus = "warn" }
    $dependencyDetails.Add("OpenTelemetry instrumentation packages should remain opt-in and publish-probed because they add dependency surface.") | Out-Null
}

Add-Check -Id "dependency-aot-review" -Status $dependencyStatus -Summary "NuGet dependency surface identified for AOT publish review." -Details @($dependencyDetails)

$dynamicPatternMap = [ordered]@{
    "assembly-load" = '\bAssembly\.(Load|LoadFile|LoadFrom)\b'
    "reflection-emit" = '\bReflection\.Emit\b|\bSystem\.Reflection\.Emit\b'
    "runtime-activator" = '\bActivator\.CreateInstance\b'
    "type-gettype" = '\bType\.GetType\s*\('
    "member-reflection" = '\.(GetMethod|GetMethods|GetProperty|GetProperties|GetFields|GetConstructors)\s*\('
    "expression-compile" = '\.Compile\s*\(\s*\)'
}

$dynamicHits = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $dynamicPatternMap.GetEnumerator()) {
    $hits = @(Select-SourcePatternHits -Pattern $entry.Value -Limit 10)
    foreach ($hit in $hits) {
        $dynamicHits.Add(("{0}: {1}" -f $entry.Key, $hit)) | Out-Null
    }
}

if ($dynamicHits.Count -eq 0) {
    Add-Check -Id "dynamic-code-scan" -Status "pass" -Summary "No common dynamic-code or reflection markers found in source hot areas." -Details @("Scanned src\PalLLM.Domain and src\PalLLM.Sidecar C# files.")
}
else {
    Add-Check -Id "dynamic-code-scan" -Status "warn" -Summary "Dynamic-code markers need review before Native AOT is trusted." -Details @($dynamicHits | Select-Object -First 40)
}

if ($sidecarProjectText.IndexOf("<PublishAot>true</PublishAot>", [StringComparison]::Ordinal) -ge 0) {
    Add-Check -Id "default-publish-mode" -Status "warn" -Summary "Sidecar project enables PublishAot by default." -Details @("Keep the packaged player EXE path on the proven publish mode until a live AOT package is validated.")
}
else {
    Add-Check -Id "default-publish-mode" -Status "pass" -Summary "Default sidecar publish mode remains the proven packaged-EXE path." -Details @("Use this script's -PublishProbe switch for explicit Native AOT experiments.")
}

if ($PublishProbe) {
    Invoke-PublishProbe
}
else {
    Add-Check -Id "publish-probe" -Status "skip" -Summary "Native AOT publish probe skipped by default." -Details @("Run: pwsh .\pal.ps1 aot-readiness -PublishProbe -RuntimeIdentifier $RuntimeIdentifier")
}

$failed = @($checks | Where-Object { $_.Status -eq "fail" })
$warnings = @($checks | Where-Object { $_.Status -eq "warn" })
$overall = if ($failed.Count -gt 0) {
    "FAIL"
}
elseif ($warnings.Count -gt 0) {
    "WARN"
}
else {
    "PASS"
}

$reportLines = [System.Collections.Generic.List[string]]::new()
$reportLines.Add("# PalLLM AOT Readiness Results") | Out-Null
$reportLines.Add("") | Out-Null
$reportLines.Add(("- Generated: {0} UTC" -f ([DateTimeOffset]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'")))) | Out-Null
$reportLines.Add(("- Repo root: {0}" -f $repoRoot)) | Out-Null
$reportLines.Add(("- Runtime identifier: {0}" -f $RuntimeIdentifier)) | Out-Null
$reportLines.Add(("- Publish probe: {0}" -f ([bool]$PublishProbe))) | Out-Null
$reportLines.Add(("- Overall: **{0}**" -f $overall)) | Out-Null
$reportLines.Add("") | Out-Null
$reportLines.Add("| # | Check | Status | Summary |") | Out-Null
$reportLines.Add("| -: | :--- | :---: | :--- |") | Out-Null

for ($i = 0; $i -lt $checks.Count; $i++) {
    $check = $checks[$i]
    $reportLines.Add((
        "| {0} | {1} | **{2}** | {3} |" -f
        ($i + 1),
        $check.Id,
        $check.Status.ToUpperInvariant(),
        $check.Summary.Replace("|", "\|")
    )) | Out-Null
}

$reportLines.Add("") | Out-Null
$reportLines.Add("## Details") | Out-Null
foreach ($check in $checks) {
    $reportLines.Add("") | Out-Null
    $reportLines.Add(("### {0}" -f $check.Id)) | Out-Null
    $reportLines.Add("") | Out-Null
    $reportLines.Add(("- Status: **{0}**" -f $check.Status.ToUpperInvariant())) | Out-Null
    $reportLines.Add(("- Summary: {0}" -f $check.Summary)) | Out-Null
    foreach ($detail in $check.Details) {
        $reportLines.Add(("- {0}" -f $detail)) | Out-Null
    }
}

$reportLines.Add("") | Out-Null
$reportLines.Add("## Notes") | Out-Null
$reportLines.Add("") | Out-Null
$reportLines.Add("- This is an offline static audit unless -PublishProbe is set.") | Out-Null
$reportLines.Add("- WARN means the repo can keep shipping on the proven JIT/self-contained path, but a native publish still needs focused review.") | Out-Null
$reportLines.Add("- Native AOT must be tested as a packaged app before replacing the current packaged-EXE path.") | Out-Null

Set-Content -LiteralPath $resultsPath -Value ($reportLines -join [Environment]::NewLine) -Encoding UTF8

$payload = [pscustomobject]@{
    Status = if ($overall -eq "PASS") { "passed" } elseif ($overall -eq "WARN") { "warning" } else { "failed" }
    GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'")
    RepoRoot = $repoRoot
    ResultsPath = $resultsPath
    RuntimeIdentifier = $RuntimeIdentifier
    PublishProbe = [bool]$PublishProbe
    Strict = [bool]$Strict
    Checks = @($checks)
}

$payloadJson = $payload | ConvertTo-Json -Depth 8
Set-Content -LiteralPath $jsonPath -Value $payloadJson -Encoding UTF8

if ($Json) {
    $payloadJson
}
else {
    Get-Content -LiteralPath $resultsPath
}

if ($failed.Count -gt 0 -or ($Strict -and $warnings.Count -gt 0)) {
    exit 1
}
