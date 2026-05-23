<#
.SYNOPSIS
    Scaffolds placeholder files for a new PalLLM extension.

.DESCRIPTION
    Generates the boilerplate file shells for the most-common change
    types: a new advisor / builder / validator / feeder / fallback
    strategy / MCP tool / config flag. Each output file carries TODO
    markers naming the exact follow-up steps the cookbook documents.

    The scaffolder NEVER edits existing files. After it runs, you
    still have to:
      - Wire the route into src/PalLLM.Sidecar/Program.cs
      - Add a FeatureDescriptor entry in PalLlmFeatureCatalog.cs
      - Bump counts in README / ROADMAP / etc.
      - Update PROJECT_NUMBERS.json

    Refusing to auto-edit existing files keeps the scaffolder safe to
    run multiple times and avoids the "scaffolder put it in the wrong
    block" failure mode. The COOKBOOK at docs/COOKBOOK.md walks through
    every step the scaffolder doesn't do.

.PARAMETER Kind
    What to scaffold. One of:
      advisor | builder | validator | feeder | fallback | mcp-tool

.PARAMETER Name
    PascalCase name. The scaffolder appends the kind suffix
    automatically (e.g. -Name "Forecast" -Kind advisor produces
    ForecastAdvisor.cs and ForecastAdvisorTests.cs).

.PARAMETER DryRun
    Print what would be written without creating files.

.EXAMPLE
    pwsh ./scripts/scaffold.ps1 -Kind advisor -Name Forecast
    # Creates ForecastAdvisor.cs under the Domain/Runtime folder
    # plus a matching ForecastAdvisorTests.cs under the test folder,
    # then prints a next-step checklist.

.EXAMPLE
    pwsh ./scripts/scaffold.ps1 -Kind fallback -Name PondersilenceStrategy -DryRun

.NOTES
    Pairs with docs/COOKBOOK.md (the recipes) and
    docs/EXTENSION_POINTS.md (the where-does-X-go map).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('advisor', 'builder', 'validator', 'feeder', 'fallback', 'mcp-tool')]
    [string]$Kind,

    [Parameter(Mandatory = $true, Position = 1)]
    [ValidatePattern('^[A-Z][A-Za-z0-9]*$')]
    [string]$Name,

    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$domainRuntime = Join-Path $repoRoot "src/PalLLM.Domain/Runtime"
$testsDir      = Join-Path $repoRoot "tests/PalLLM.Tests"

# --- Templates -----------------------------------------------------

function New-AdvisorTemplate {
    param([string]$BaseName)
    $className = "${BaseName}Advisor"
    @"
namespace PalLLM.Domain.Runtime;

/// <summary>
/// TODO(scaffold): one-paragraph summary of what this advisor recommends
/// and what consumers do with the result. Read CONVENTIONS.md "Advisor"
/// pattern before extending.
/// </summary>
public static class $className
{
    /// <summary>
    /// TODO(scaffold): describe the inputs and the recommendation.
    /// </summary>
    public static ${BaseName}Advisory Recommend(/* TODO: inputs */)
    {
        // TODO(scaffold): pure deterministic logic only. No I/O.
        return new ${BaseName}Advisory(
            Posture: "TODO",
            Headline: "TODO: one-sentence summary",
            CapturedAtUtc: System.DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Result returned by <see cref="$className.Recommend"/>.
/// </summary>
/// <param name="Posture">TODO machine-friendly classification.</param>
/// <param name="Headline">TODO one-sentence plain-English summary.</param>
/// <param name="CapturedAtUtc">When the advisory was captured (UTC).</param>
public sealed record ${BaseName}Advisory(
    string Posture,
    string Headline,
    System.DateTimeOffset CapturedAtUtc);
"@
}

function New-AdvisorTestTemplate {
    param([string]$BaseName)
    $className = "${BaseName}Advisor"
    $testClass = "${className}Tests"
    @"
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// TODO(scaffold): exercise <see cref="$className.Recommend"/>.
/// See docs/TESTING.md "Pure-logic class" for the canonical pattern.
/// </summary>
public sealed class $testClass
{
    [Test]
    public void Recommend_BaselineInputs_ReturnsExpectedShape()
    {
        var result = $className.Recommend(/* TODO: inputs */);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Posture, Is.Not.Empty);
            Assert.That(result.Headline, Is.Not.Empty);
            Assert.That(result.CapturedAtUtc,
                Is.GreaterThan(System.DateTimeOffset.MinValue));
        });
    }
}
"@
}

function New-BuilderTemplate {
    param([string]$BaseName)
    $className = "${BaseName}Builder"
    @"
namespace PalLLM.Domain.Runtime;

/// <summary>
/// TODO(scaffold): one-paragraph description. Builders compose a
/// snapshot from multiple sources; if read-heavy, follow the
/// TTL-cache pattern from adr/0005.
/// </summary>
public static class $className
{
    /// <summary>
    /// TODO(scaffold): describe inputs and the snapshot returned.
    /// </summary>
    public static ${BaseName}Snapshot Build(/* TODO: inputs */)
    {
        // TODO(scaffold): pure deterministic snapshot composition.
        return new ${BaseName}Snapshot(
            CapturedAtUtc: System.DateTimeOffset.UtcNow,
            Headline: "TODO");
    }
}

/// <summary>Snapshot returned by <see cref="$className.Build"/>.</summary>
/// <param name="CapturedAtUtc">When captured (UTC).</param>
/// <param name="Headline">TODO summary line.</param>
public sealed record ${BaseName}Snapshot(
    System.DateTimeOffset CapturedAtUtc,
    string Headline);
"@
}

function New-ValidatorTemplate {
    param([string]$BaseName)
    $className = "${BaseName}Validator"
    @"
namespace PalLLM.Domain.Runtime;

/// <summary>
/// TODO(scaffold): one-paragraph description. Validators check shape
/// and return a structured result; they never throw on
/// invalid input. See PersonalityPackValidator for the canonical example.
/// </summary>
public static class $className
{
    public static ${BaseName}ValidationResult Validate(/* TODO: input */)
    {
        var issues = new System.Collections.Generic.List<string>();
        // TODO(scaffold): populate issues for any invariant violations.
        return new ${BaseName}ValidationResult(
            IsValid: issues.Count == 0,
            Issues: issues);
    }
}

/// <summary>Result of <see cref="$className.Validate"/>.</summary>
/// <param name="IsValid">True iff Issues is empty.</param>
/// <param name="Issues">Plain-English list of invariant violations.</param>
public sealed record ${BaseName}ValidationResult(
    bool IsValid,
    System.Collections.Generic.IReadOnlyList<string> Issues);
"@
}

function New-FeederTemplate {
    param([string]$BaseName)
    $className = "${BaseName}Feeder"
    @"
namespace PalLLM.Domain.Runtime;

/// <summary>
/// TODO(scaffold): one-paragraph description. Feeders observe runtime
/// events and write elsewhere (typically the promotion ledger or a
/// metric). Pure observer — never mutates the source it watches.
/// </summary>
public sealed class $className
{
    public $className()
    {
        // TODO(scaffold): wire dependencies (subject to portable-domain seam).
    }

    public void Tick()
    {
        // TODO(scaffold): pull observations and forward them.
    }
}
"@
}

function New-FallbackTemplate {
    param([string]$BaseName)
    @"
// TODO(scaffold): integrate this strategy into FallbackBehaviorEngine.cs.
// This file is a placeholder showing the canonical Try_* shape; copy
// the body into FallbackBehaviorEngine.cs alongside the existing 19
// strategies. See COOKBOOK.md §2 for the full recipe.

// Inside FallbackBehaviorEngine, add:
//
//   private static FallbackResult? Try_$BaseName(FallbackContext ctx)
//   {
//       // TODO: pattern-match on ctx and return a FallbackResult,
//       //       or null if this strategy doesn't apply this turn.
//       if (/* not applicable */ true) return null;
//
//       return FallbackResult.From(
//           reply: "TODO multi-sentence reply",
//           strategy: "$BaseName",
//           tier: "primary");
//   }
//
// Then:
//   - Add the method to the chained call sites in CreateGeneralDirector.
//   - Extend PresentationCuePlanner with a cue family for this strategy.
//   - Bump fallback strategy count in docs/ROADMAP.md.
//   - Add tests under tests/PalLLM.Tests/RuntimeTests.cs.
"@
}

function New-McpToolTemplate {
    param([string]$BaseName)
    $toolName = ($BaseName -creplace '([A-Z])', '_$1').ToLowerInvariant().TrimStart('_')
    $methodName = "Pal${BaseName}"
    @"
// TODO(scaffold): integrate this MCP tool into PalLlmMcpTools.cs.
// Copy the method below into src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs.
// See COOKBOOK.md §3 for the full recipe.
//
//   [McpTool(name: "pal$toolName", description: "TODO short description")]
//   public async System.Threading.Tasks.Task<object> $methodName(
//       /* TODO: parameters */)
//   {
//       // TODO: implement. Most tools delegate to a runtime method or
//       //       an existing /api/* endpoint.
//       return new { };
//   }
//
// Don't forget:
//   - Add a test in tests/PalLLM.Tests/McpEndpointTests.cs
//   - Bump MCP tool count in README / ARCHITECTURE
//   - Add a FeatureDescriptor entry if this is observably new functionality
"@
}

# --- File plan --------------------------------------------------------

$plan = @()

switch ($Kind) {
    'advisor' {
        $plan += @{
            Path     = Join-Path $domainRuntime "${Name}Advisor.cs"
            Content  = New-AdvisorTemplate $Name
        }
        $plan += @{
            Path     = Join-Path $testsDir "${Name}AdvisorTests.cs"
            Content  = New-AdvisorTestTemplate $Name
        }
    }
    'builder' {
        $plan += @{
            Path     = Join-Path $domainRuntime "${Name}Builder.cs"
            Content  = New-BuilderTemplate $Name
        }
    }
    'validator' {
        $plan += @{
            Path     = Join-Path $domainRuntime "${Name}Validator.cs"
            Content  = New-ValidatorTemplate $Name
        }
    }
    'feeder' {
        $plan += @{
            Path     = Join-Path $domainRuntime "${Name}Feeder.cs"
            Content  = New-FeederTemplate $Name
        }
    }
    'fallback' {
        $plan += @{
            Path     = Join-Path (Resolve-Path $repoRoot) "scaffold-${Name}-fallback.txt"
            Content  = New-FallbackTemplate $Name
        }
    }
    'mcp-tool' {
        $plan += @{
            Path     = Join-Path (Resolve-Path $repoRoot) "scaffold-${Name}-mcp-tool.txt"
            Content  = New-McpToolTemplate $Name
        }
    }
}

# --- Execute ---------------------------------------------------------

Write-Host ""
Write-Host "PalLLM scaffolder -- $Kind '$Name'" -ForegroundColor Cyan
Write-Host ""

foreach ($item in $plan) {
    $rel = $item.Path.Replace($repoRoot.ToString(), '').TrimStart('\', '/')
    if ($DryRun) {
        Write-Host "  [DRY] would write $rel" -ForegroundColor Yellow
    } else {
        if (Test-Path $item.Path) {
            Write-Host "  [SKIP] $rel (already exists; refusing to overwrite)" -ForegroundColor Yellow
            continue
        }
        $dir = Split-Path -Parent $item.Path
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Set-Content -Path $item.Path -Value $item.Content -Encoding UTF8
        Write-Host "  [WRITE] $rel" -ForegroundColor Green
    }
}

# --- Next steps ------------------------------------------------------

Write-Host ""
Write-Host "Next steps:" -ForegroundColor White

switch ($Kind) {
    'advisor' {
        Write-Host "  1. Fill in TODO blocks in the new ${Name}Advisor.cs and tests"
        Write-Host "  2. (Optional) wire the advisor to an HTTP route in Program.cs (see docs/COOKBOOK.md §1)"
        Write-Host "  3. Add a FeatureDescriptor entry in PalLlmFeatureCatalog.cs"
        Write-Host "  4. Add a row to docs/ADVISORS.md"
        Write-Host "  5. pwsh ./pal.ps1 audit"
    }
    'builder' {
        Write-Host "  1. Fill in TODO blocks in the new ${Name}Builder.cs"
        Write-Host "  2. Add a corresponding test file (see docs/TESTING.md)"
        Write-Host "  3. (Optional) wire to an HTTP route in Program.cs (see docs/COOKBOOK.md §1)"
        Write-Host "  4. Add a row to docs/ADVISORS.md"
        Write-Host "  5. pwsh ./pal.ps1 audit"
    }
    'validator' {
        Write-Host "  1. Fill in TODO blocks in the new ${Name}Validator.cs"
        Write-Host "  2. Add a test file"
        Write-Host "  3. Add a row to docs/ADVISORS.md"
        Write-Host "  4. pwsh ./pal.ps1 audit"
    }
    'feeder' {
        Write-Host "  1. Fill in TODO blocks in the new ${Name}Feeder.cs"
        Write-Host "  2. Wire it into the host (background worker or runtime hook)"
        Write-Host "  3. Add a test file"
        Write-Host "  4. pwsh ./pal.ps1 audit"
    }
    'fallback' {
        Write-Host "  1. Read the scaffold-${Name}-fallback.txt file"
        Write-Host "  2. Copy the Try_${Name} method into FallbackBehaviorEngine.cs"
        Write-Host "  3. Wire into CreateGeneralDirector chain"
        Write-Host "  4. Extend PresentationCuePlanner with a cue family"
        Write-Host "  5. Bump fallback strategy count in docs/ROADMAP.md"
        Write-Host "  6. Add tests in tests/PalLLM.Tests/RuntimeTests.cs"
        Write-Host "  7. pwsh ./pal.ps1 audit"
    }
    'mcp-tool' {
        Write-Host "  1. Read the scaffold-${Name}-mcp-tool.txt file"
        Write-Host "  2. Copy the method into src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs"
        Write-Host "  3. Add a test in tests/PalLLM.Tests/McpEndpointTests.cs"
        Write-Host "  4. Bump MCP tool count in README / ARCHITECTURE"
        Write-Host "  5. pwsh ./pal.ps1 audit"
    }
}

Write-Host ""
Write-Host "See also: docs/COOKBOOK.md (recipes), docs/EXTENSION_POINTS.md (where-does-X-go map)" -ForegroundColor DarkGray
Write-Host ""
