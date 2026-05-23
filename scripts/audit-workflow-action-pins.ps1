<#
.SYNOPSIS
    Verifies GitHub Actions workflow dependencies are pinned to full SHAs.

.DESCRIPTION
    Scans .github/workflows/*.yml and *.yaml for external `uses:` entries.
    Local actions (`./...`) and Docker action references (`docker://...`) are
    ignored. Every other action reference must include a 40-character commit
    SHA so release and CI execution is reproducible and not silently controlled
    by a mutable tag.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$workflowRoot = Join-Path $RepoRoot ".github/workflows"
if (-not (Test-Path $workflowRoot)) {
    throw "Workflow directory not found: $workflowRoot"
}

$usesPattern = [regex]'^\s*(?:-\s*)?uses:\s*(?<value>[^\s#]+)'
$shaPattern = [regex]'^[0-9a-fA-F]{40}$'
$ignoredPrefixes = @("./", "docker://")
$checked = 0
$issues = [System.Collections.Generic.List[string]]::new()

$workflowFiles = Get-ChildItem -Path (Join-Path $workflowRoot "*") -File -Include *.yml,*.yaml |
    Sort-Object FullName

foreach ($file in $workflowFiles) {
    $lines = Get-Content -LiteralPath $file.FullName
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        $match = $usesPattern.Match($line)
        if (-not $match.Success) {
            continue
        }

        $value = $match.Groups["value"].Value.Trim()
        $isIgnored = $false
        foreach ($prefix in $ignoredPrefixes) {
            if ($value.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $isIgnored = $true
                break
            }
        }
        if ($isIgnored) {
            continue
        }

        if (-not $value.Contains("@")) {
            $issues.Add(("{0}:{1}: missing @ref in {2}" -f $file.FullName, ($index + 1), $value))
            continue
        }

        $checked++
        $ref = $value.Substring($value.LastIndexOf("@", [StringComparison]::Ordinal) + 1)
        if (-not $shaPattern.IsMatch($ref)) {
            $issues.Add(("{0}:{1}: unpinned action reference {2}" -f $file.FullName, ($index + 1), $value))
        }
    }
}

if ($issues.Count -gt 0) {
    Write-Error ("External GitHub Actions must be pinned to full-length commit SHAs.`n  " + ($issues -join "`n  "))
    exit 1
}

Write-Output ("All external workflow actions are pinned to full-length SHAs ({0} refs checked)." -f $checked)
