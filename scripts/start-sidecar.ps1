[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5088",
    [string]$PublishedRoot
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

$normalizedBaseUrl = $BaseUrl.TrimEnd("/")
$publishedLaunch = $null
if (-not [string]::IsNullOrWhiteSpace($PublishedRoot)) {
    $resolvedPublishedRoot = Resolve-ExistingPath -Path $PublishedRoot
    if (-not $resolvedPublishedRoot) {
        throw "Published sidecar path does not exist: $PublishedRoot"
    }

    $publishedExe = Join-Path $resolvedPublishedRoot "PalLLM.Sidecar.exe"
    $publishedDll = Join-Path $resolvedPublishedRoot "PalLLM.Sidecar.dll"
    if (Test-Path -LiteralPath $publishedExe) {
        $publishedLaunch = [pscustomobject]@{
            Kind = "self_contained_exe"
            FilePath = $publishedExe
            WorkingDirectory = $resolvedPublishedRoot
            RequiresDotNet = $false
        }
    }
    elseif (Test-Path -LiteralPath $publishedDll) {
        $publishedLaunch = [pscustomobject]@{
            Kind = "framework_dependent_dll"
            FilePath = $publishedDll
            WorkingDirectory = $resolvedPublishedRoot
            RequiresDotNet = $true
        }
    }
    else {
        throw "Published sidecar path did not contain PalLLM.Sidecar.exe or PalLLM.Sidecar.dll: $resolvedPublishedRoot"
    }
}

$packagedLaunch = if (-not $publishedLaunch) { Resolve-PalLlmPackagedSidecarLaunchTarget } else { $null }
$resolvedLaunch = if ($publishedLaunch) { $publishedLaunch } else { $packagedLaunch }

if ($resolvedLaunch -and $resolvedLaunch.RequiresDotNet -and -not (Test-CommandAvailable -CommandName "dotnet")) {
    throw "dotnet was not found on PATH. Install the .NET runtime required for PalLLM.Sidecar or package a self-contained sidecar."
}

if ($resolvedLaunch) {
    Write-Host ("Starting packaged PalLLM sidecar from " + $resolvedLaunch.FilePath)
    Push-Location $resolvedLaunch.WorkingDirectory
    try {
        if ($resolvedLaunch.RequiresDotNet) {
            & dotnet $resolvedLaunch.FilePath --urls $normalizedBaseUrl
        }
        else {
            & $resolvedLaunch.FilePath --urls $normalizedBaseUrl
        }
        exit $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}

$sidecarProject = Get-PalLlmSidecarProjectPath
if (-not (Test-Path -LiteralPath $sidecarProject)) {
    throw "No published sidecar was found and the repo project was not available at $sidecarProject."
}

if (-not (Test-CommandAvailable -CommandName "dotnet")) {
    throw "dotnet was not found on PATH. Install the .NET SDK/runtime required for PalLLM.Sidecar before starting the repo project."
}

Write-Host ("Starting repo sidecar project at " + $sidecarProject)
Push-Location (Get-PalLlmRepoRoot)
try {
    & dotnet run --project $sidecarProject --urls $normalizedBaseUrl
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
