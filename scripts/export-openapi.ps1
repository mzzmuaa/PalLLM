[CmdletBinding()]
param(
    [switch]$Verify,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$ProjectPath,
    [string]$SnapshotPath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $repoRoot "src/PalLLM.Sidecar/PalLLM.Sidecar.csproj"
}

if ([string]::IsNullOrWhiteSpace($SnapshotPath)) {
    $SnapshotPath = Join-Path $repoRoot "docs/openapi/palllm-sidecar-v1.json"
}

$snapshotDirectory = Split-Path -Parent $SnapshotPath
New-Item -ItemType Directory -Force -Path $snapshotDirectory | Out-Null

$tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("PalLLM.OpenApi." + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempDirectory | Out-Null

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Normalize-OpenApiDocument {
    param(
        [Parameter(Mandatory)][string]$Content
    )

    $normalized = $Content.Replace("`r`n", "`n").Replace('\r\n', '\n')
    $lines = $normalized.Split([string[]]@("`n"), [System.StringSplitOptions]::None)
    $kept = [System.Collections.Generic.List[string]]::new()
    $skippingServers = $false
    foreach ($line in $lines) {
        if (-not $skippingServers -and $line -match '^\s{2}"servers":\s*\[') {
            $skippingServers = $true
            continue
        }

        if ($skippingServers) {
            if ($line -match '^\s{2}\]\s*,?\s*$') {
                $skippingServers = $false
            }
            continue
        }

        $kept.Add($line)
    }

    return ($kept -join "`n")
}

function Wait-ForOpenApiDocument {
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200 -and -not [string]::IsNullOrWhiteSpace($response.Content)) {
                return [string]$response.Content
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for OpenAPI document at $Url. Last error: $lastError"
}

try {
    $previousAspNetCoreEnvironment = $env:ASPNETCORE_ENVIRONMENT
    $previousAspNetCoreUrls = $env:ASPNETCORE_URLS
    $previousRuntimeRoot = $env:PalLLM__PalSavedRoot
    $env:ASPNETCORE_ENVIRONMENT = "OpenApiBuild"
    $port = Get-FreeLoopbackPort
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
    $env:PalLLM__PalSavedRoot = (Join-Path $tempDirectory "runtime-root")

    $projectDirectory = Split-Path -Parent $ProjectPath
    $buildArgs = @(
        "build", $ProjectPath,
        "--configuration", $Configuration,
        "--nologo",
        "--verbosity", "minimal",
        "--tl:off"
    )

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    $assemblyPath = Join-Path $projectDirectory ("bin/$Configuration/net10.0/PalLLM.Sidecar.dll")
    if (-not (Test-Path $assemblyPath)) {
        throw "Build completed but sidecar assembly was not found at $assemblyPath."
    }

    $processInfo = [System.Diagnostics.ProcessStartInfo]::new("dotnet")
    $processInfo.Arguments = '"' + $assemblyPath + '"'
    $processInfo.WorkingDirectory = $projectDirectory
    $processInfo.UseShellExecute = $false
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = $env:ASPNETCORE_ENVIRONMENT
    $processInfo.EnvironmentVariables["ASPNETCORE_URLS"] = $env:ASPNETCORE_URLS
    $processInfo.EnvironmentVariables["PalLLM__PalSavedRoot"] = $env:PalLLM__PalSavedRoot

    $sidecarProcess = [System.Diagnostics.Process]::Start($processInfo)
    if ($null -eq $sidecarProcess) {
        throw "Failed to launch sidecar for live OpenAPI export."
    }

    try {
        $generatedContent = Normalize-OpenApiDocument (Wait-ForOpenApiDocument -Url "http://127.0.0.1:$port/openapi/v1.json")
    }
    finally {
        if (-not $sidecarProcess.HasExited) {
            $sidecarProcess.Kill()
            $sidecarProcess.WaitForExit(5000) | Out-Null
        }
    }

    if ($Verify) {
        if (-not (Test-Path $SnapshotPath)) {
            throw "Committed snapshot missing at $SnapshotPath. Run scripts/export-openapi.ps1 once to create it."
        }

        $snapshotContent = Normalize-OpenApiDocument ([System.IO.File]::ReadAllText($SnapshotPath))
        if (-not [string]::Equals($generatedContent, $snapshotContent, [System.StringComparison]::Ordinal)) {
            throw "Committed snapshot drift detected at $SnapshotPath. Re-run scripts/export-openapi.ps1 and commit the updated file."
        }

        Write-Host "OpenAPI snapshot matches committed contract: $SnapshotPath" -ForegroundColor Green
        return
    }

    [System.IO.File]::WriteAllText($SnapshotPath, $generatedContent, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Wrote OpenAPI snapshot: $SnapshotPath" -ForegroundColor Green

    if ($null -eq $previousAspNetCoreEnvironment) {
        Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    } else {
        $env:ASPNETCORE_ENVIRONMENT = $previousAspNetCoreEnvironment
    }

    if ($null -eq $previousAspNetCoreUrls) {
        Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
    } else {
        $env:ASPNETCORE_URLS = $previousAspNetCoreUrls
    }

    if ($null -eq $previousRuntimeRoot) {
        Remove-Item Env:PalLLM__PalSavedRoot -ErrorAction SilentlyContinue
    } else {
        $env:PalLLM__PalSavedRoot = $previousRuntimeRoot
    }
}
finally {
    if ($null -eq $previousAspNetCoreEnvironment) {
        Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    } else {
        $env:ASPNETCORE_ENVIRONMENT = $previousAspNetCoreEnvironment
    }

    if ($null -eq $previousAspNetCoreUrls) {
        Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
    } else {
        $env:ASPNETCORE_URLS = $previousAspNetCoreUrls
    }

    if ($null -eq $previousRuntimeRoot) {
        Remove-Item Env:PalLLM__PalSavedRoot -ErrorAction SilentlyContinue
    } else {
        $env:PalLLM__PalSavedRoot = $previousRuntimeRoot
    }

    if (Test-Path $tempDirectory) {
        Remove-Item -LiteralPath $tempDirectory -Recurse -Force
    }
}
