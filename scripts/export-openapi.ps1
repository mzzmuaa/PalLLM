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
try {
    $previousAspNetCoreEnvironment = $env:ASPNETCORE_ENVIRONMENT
    $previousRuntimeRoot = $env:PalLLM__PalSavedRoot
    $env:ASPNETCORE_ENVIRONMENT = "OpenApiBuild"
    $env:PalLLM__PalSavedRoot = (Join-Path $tempDirectory "runtime-root")

    $projectDirectory = Split-Path -Parent $ProjectPath
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    $openApiCachePath = Join-Path $projectDirectory ("obj/" + $projectName + ".OpenApiFiles.cache")
    if (Test-Path $openApiCachePath) {
        Remove-Item -LiteralPath $openApiCachePath -Force
    }

    $cleanArgs = @(
        "clean", $ProjectPath,
        "--configuration", $Configuration,
        "--nologo",
        "--verbosity", "minimal",
        "--tl:off"
    )

    & dotnet @cleanArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet clean failed with exit code $LASTEXITCODE."
    }

    $buildArgs = @(
        "build", $ProjectPath,
        "--configuration", $Configuration,
        "--nologo",
        "--verbosity", "minimal",
        "--tl:off",
        "--no-incremental",
        "-p:OpenApiGenerateDocuments=true",
        "-p:OpenApiGenerateDocumentsOnBuild=true",
        "-p:OpenApiDocumentsDirectory=$tempDirectory"
    )

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    $generated = Get-ChildItem -Path $tempDirectory -Filter "*.json" -File -Recurse |
        Where-Object { $_.BaseName -eq "palllm-sidecar-v1" } |
        Select-Object -First 1

    if (-not $generated) {
        throw "Build completed but no generated OpenAPI document named 'palllm-sidecar-v1.json' was found under $tempDirectory."
    }

    $generatedContent = [System.IO.File]::ReadAllText($generated.FullName).Replace("`r`n", "`n")

    if ($Verify) {
        if (-not (Test-Path $SnapshotPath)) {
            throw "Committed snapshot missing at $SnapshotPath. Run scripts/export-openapi.ps1 once to create it."
        }

        $snapshotContent = [System.IO.File]::ReadAllText($SnapshotPath).Replace("`r`n", "`n")
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

    if ($null -eq $previousRuntimeRoot) {
        Remove-Item Env:PalLLM__PalSavedRoot -ErrorAction SilentlyContinue
    } else {
        $env:PalLLM__PalSavedRoot = $previousRuntimeRoot
    }

    if (Test-Path $tempDirectory) {
        Remove-Item -LiteralPath $tempDirectory -Recurse -Force
    }
}
