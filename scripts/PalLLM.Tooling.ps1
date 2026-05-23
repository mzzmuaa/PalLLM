Set-StrictMode -Version Latest

function Get-PalLlmRepoRoot {
    return (Split-Path -Parent $PSScriptRoot)
}

function Get-PalLlmScriptsRoot {
    return $PSScriptRoot
}

function Get-PalLlmModSourcePath {
    return (Join-Path (Get-PalLlmRepoRoot) "mod\ue4ss\Mods\PalLLM")
}

function Get-PalLlmSidecarProjectPath {
    return (Join-Path (Get-PalLlmRepoRoot) "src\PalLLM.Sidecar\PalLLM.Sidecar.csproj")
}

function Get-PalLlmPackagedSidecarDllPath {
    $packageRoot = Split-Path -Parent $PSScriptRoot
    return (Join-Path $packageRoot "sidecar\publish\PalLLM.Sidecar.dll")
}

function Get-PalLlmPackagedSidecarExePath {
    $packageRoot = Split-Path -Parent $PSScriptRoot
    return (Join-Path $packageRoot "sidecar\publish\PalLLM.Sidecar.exe")
}

function Resolve-PalLlmPackagedSidecarLaunchTarget {
    $packagedExe = Get-PalLlmPackagedSidecarExePath
    if (Test-Path -LiteralPath $packagedExe) {
        return [pscustomobject]@{
            Kind = "self_contained_exe"
            FilePath = $packagedExe
            WorkingDirectory = Split-Path -Parent $packagedExe
            RequiresDotNet = $false
        }
    }

    $packagedDll = Get-PalLlmPackagedSidecarDllPath
    if (Test-Path -LiteralPath $packagedDll) {
        return [pscustomobject]@{
            Kind = "framework_dependent_dll"
            FilePath = $packagedDll
            WorkingDirectory = Split-Path -Parent $packagedDll
            RequiresDotNet = $true
        }
    }

    return $null
}

function Get-PalLlmRuntimeRoot {
    <#
    .SYNOPSIS
        Resolve the active PalLLM runtime root, honoring configured overrides
        from the live appsettings.json when present.
    .DESCRIPTION
        PalLlmOptions exposes two relevant knobs:
          * PalSavedRoot      - parent directory where the runtime tree lives.
                                Defaults to %LOCALAPPDATA%/Pal/Saved when null.
          * RuntimeFolderName - subfolder under PalSavedRoot. Defaults to
                                "PalLLM".
        Operators who set either knob (e.g. to redirect to a fast SSD or share
        runtime data across machines) need every script reading filesystem
        evidence to honor the override, otherwise pal-pack-copy / pal-next /
        pal-health all read from the wrong tree.

        This helper checks the same candidate appsettings.json paths
        connect-ollama / pal-config-* use, parses the two knobs if present,
        and falls back to the historical
        %LOCALAPPDATA%\Pal\Saved\PalLLM default when neither file nor knob
        is set. Pure read; never writes.
    #>
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $defaultRoot = Join-Path $localAppData "Pal\Saved\PalLLM"

    # Same candidate order pal-next + connect-ollama use: prefer the operator's
    # custom path under LOCALAPPDATA (which doctor / play create on first
    # boot), then the published sidecar config, then the source tree config.
    $repoRoot = Get-PalLlmRepoRoot
    $candidates = @(
        (Join-Path $localAppData 'Pal/Saved/PalLLM/appsettings.json'),
        (Join-Path $repoRoot 'sidecar/publish/appsettings.json'),
        (Join-Path $repoRoot 'src/PalLLM.Sidecar/appsettings.json')
    )

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        try {
            $cfg = Get-Content -LiteralPath $candidate -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
            if (-not $cfg.PalLLM) { continue }

            $palSavedRoot = $null
            $runtimeFolderName = "PalLLM"
            if ($cfg.PalLLM.PSObject.Properties['PalSavedRoot'] -and `
                -not [string]::IsNullOrWhiteSpace($cfg.PalLLM.PalSavedRoot)) {
                $palSavedRoot = [string]$cfg.PalLLM.PalSavedRoot
            }
            if ($cfg.PalLLM.PSObject.Properties['RuntimeFolderName'] -and `
                -not [string]::IsNullOrWhiteSpace($cfg.PalLLM.RuntimeFolderName)) {
                $runtimeFolderName = [string]$cfg.PalLLM.RuntimeFolderName
            }

            if ($palSavedRoot) {
                # Operator set an explicit PalSavedRoot. Mirror the runtime's
                # behavior: join the runtime folder name onto it.
                return (Join-Path $palSavedRoot $runtimeFolderName)
            }

            # PalSavedRoot is null -> runtime resolves to LOCALAPPDATA/Pal/Saved.
            # Honor the RuntimeFolderName override if present.
            if ($runtimeFolderName -ne "PalLLM") {
                return (Join-Path (Join-Path $localAppData 'Pal\Saved') $runtimeFolderName)
            }

            # No relevant overrides; fall through to the default.
            break
        } catch {
            # Malformed JSON / unreadable file; fall back to default rather
            # than throwing. The operator can fix the file separately.
            continue
        }
    }

    return $defaultRoot
}

function Get-PalLlmExpectedRuntimeDirectories {
    $runtimeRoot = Get-PalLlmRuntimeRoot
    $bridgeRoot = Join-Path $runtimeRoot "Bridge"
    return [pscustomobject]@{
        RuntimeRoot = $runtimeRoot
        BridgeRoot = $bridgeRoot
        Inbox = Join-Path $bridgeRoot "Inbox"
        Outbox = Join-Path $bridgeRoot "Outbox"
        Archive = Join-Path $bridgeRoot "Archive"
        Failed = Join-Path $bridgeRoot "Failed"
        Screenshots = Join-Path $bridgeRoot "Screenshots"
        Tts = Join-Path $runtimeRoot "TTS"
    }
}

function Resolve-ExistingPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return (Resolve-Path -LiteralPath $Path).ProviderPath
}

function Add-UniqueString {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$List,

        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    if (-not $List.Contains($Value)) {
        $List.Add($Value)
    }
}

function ConvertTo-PalLlmRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $basePath = [IO.Path]::GetFullPath($RootPath)
    $targetPath = [IO.Path]::GetFullPath($FilePath)
    if (-not $basePath.EndsWith([IO.Path]::DirectorySeparatorChar.ToString(), [System.StringComparison]::Ordinal)) {
        $basePath += [IO.Path]::DirectorySeparatorChar
    }

    $baseUri = [Uri]$basePath
    $targetUri = [Uri]$targetPath
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('\', '/')
}

function Test-PalLlmPublicationTextSurface {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [string[]]$RootBrandMinimalFiles = @(),

        [string[]]$ScannerFiles = @()
    )

    if (-not (Test-Path -LiteralPath $RootPath -PathType Container)) {
        throw "Publication text-surface root does not exist: $RootPath"
    }

    $violations = [System.Collections.Generic.List[string]]::new()
    $textExtensions = @(".bat", ".json", ".lua", ".md", ".ps1", ".txt", ".yaml", ".yml")
    $siblingProjectPattern = '(?i)\b(RimLLM|OmniForge|DeepForge|byte-forge|byte-forward|byte-synthesis|byte-qwen-frontier|byte-qwen-modernize|byte-council)\b|D:[\\/]+Coding[\\/]+(?:Byte|RimLLM|OmniForge|DeepForge)'
    $officialImpersonationPattern = '(?i)\b(?:official|endorsed|sponsored|approved|authorized|certified)\b.{0,60}\b(?:Palworld|Pocketpair|Steam|Valve)\b|\b(?:Palworld|Pocketpair|Steam|Valve)\b.{0,60}\b(?:official|endorsed|sponsored|approved|authorized|certified)\b'
    $unrelatedFranchisePattern = '(?i)\b(?:Pok(?:e|\u00E9)mon|Pikachu|Nintendo|Mario|Zelda|Star\s+Wars|Jedi|Sith|Marvel|Avengers|DC(?:\s+Comics)?|Batman|Superman|Wonder\s+Woman|Disney|Minecraft|Fortnite|Roblox|RimWorld|Skyrim|Fallout|Cyberpunk\s+2077|Harry\s+Potter|Hogwarts|Warhammer|Mass\s+Effect|Dragon\s+Age|The\s+Witcher|League\s+of\s+Legends|Dungeons\s*(?:&|and)\s*Dragons|D\s*&\s*D|DnD|Baldur.?s\s+Gate|Elden\s+Ring|Dark\s+Souls|Monster\s+Hunter|Final\s+Fantasy|World\s+of\s+Warcraft|Warcraft|One\s+Piece|Dragon\s+Ball|Naruto|Gundam|Studio\s+Ghibli|Ghibli|ARK\s*:?\s*Survival)\b'
    $scopeDriftPattern = '(?i)\b(?:generic\s+AI\s+platform|generic\s+platform|multi[-\s]?game|cross[-\s]?game|game[-\s]?agnostic|universal\s+game\s+agent|all\s+games|browser\s+agent|computer\s+use)\b'
    $legalOverclaimPattern = '(?i)\b(?:lawyer[-\s]?proof|legal[-\s]?risk[-\s]?free|no\s+legal\s+risk|guaranteed\s+legal|fully\s+IP[-\s]?neutral|100%\s+IP[-\s]?neutral|compliance[-\s]?certified)\b'
    $brandMinimalRootPattern = '(?i)\b(OpenAI|Anthropic|Claude Desktop|ChatGPT|Copilot|Cursor|VS Code|Visual Studio Code|Ollama|LM Studio|vLLM|SGLang|llama\.cpp|DashScope|Qwen[0-9A-Za-z:._-]*|Gemma[0-9A-Za-z:._-]*|Mistral|Unsloth|NVIDIA|TensorRT(?:-LLM)?|Hugging\s+Face|OpenVINO|Foundry\s+Local|DeepSeek|OpenRouter)\b'
    $rootBrandMinimalSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $scannerFileSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $checkedFileCount = 0

    foreach ($path in @($RootBrandMinimalFiles)) {
        if (-not [string]::IsNullOrWhiteSpace($path)) {
            [void]$rootBrandMinimalSet.Add(($path -replace '\\', '/').Trim())
        }
    }

    foreach ($path in @($ScannerFiles)) {
        if (-not [string]::IsNullOrWhiteSpace($path)) {
            [void]$scannerFileSet.Add(($path -replace '\\', '/').Trim())
        }
    }

    foreach ($file in Get-ChildItem -LiteralPath $RootPath -Recurse -File -ErrorAction SilentlyContinue) {
        if ($file.Length -gt 4MB) {
            continue
        }

        if ($textExtensions -notcontains $file.Extension.ToLowerInvariant()) {
            continue
        }

        $relativePath = ConvertTo-PalLlmRelativePath -RootPath $RootPath -FilePath $file.FullName
        if ($scannerFileSet.Contains($relativePath)) {
            continue
        }

        $text = Get-Content -LiteralPath $file.FullName -Raw
        $checkedFileCount++

        $siblingMatch = [regex]::Match($text, $siblingProjectPattern)
        if ($siblingMatch.Success) {
            Add-UniqueString -List $violations -Value ("{0}: private sibling/internal reference '{1}'" -f $relativePath, $siblingMatch.Value)
        }

        $officialMatch = [regex]::Match($text, $officialImpersonationPattern)
        if ($officialMatch.Success) {
            Add-UniqueString -List $violations -Value ("{0}: package copy must not imply official endorsement, sponsorship, approval, authorization, or certification. Found '{1}'" -f $relativePath, $officialMatch.Value)
        }

        $franchiseMatch = [regex]::Match($text, $unrelatedFranchisePattern)
        if ($franchiseMatch.Success) {
            Add-UniqueString -List $violations -Value ("{0}: package copy should avoid unrelated third-party IP or franchise references. Found '{1}'" -f $relativePath, $franchiseMatch.Value)
        }

        $scopeMatch = [regex]::Match($text, $scopeDriftPattern)
        if ($scopeMatch.Success) {
            Add-UniqueString -List $violations -Value ("{0}: package copy should stay scoped to PalLLM for Palworld, not a broader platform or multi-game product. Found '{1}'" -f $relativePath, $scopeMatch.Value)
        }

        $legalOverclaimMatch = [regex]::Match($text, $legalOverclaimPattern)
        if ($legalOverclaimMatch.Success) {
            Add-UniqueString -List $violations -Value ("{0}: package copy should not claim legal, IP-neutrality, or compliance certainty. Found '{1}'" -f $relativePath, $legalOverclaimMatch.Value)
        }

        if ($rootBrandMinimalSet.Contains($relativePath)) {
            $brandMatch = [regex]::Match($text, $brandMinimalRootPattern)
            if ($brandMatch.Success) {
                Add-UniqueString -List $violations -Value ("{0}: root player-facing copy should use protocol/provider-neutral wording instead of '{1}'" -f $relativePath, $brandMatch.Value)
            }
        }
    }

    return [pscustomobject]@{
        CheckedFileCount = $checkedFileCount
        Violations = @($violations)
    }
}

function Invoke-PalLlmTextRedactionRule {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [string]$Replacement,

        [Parameter(Mandatory = $true)]
        [string]$RuleName,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$RuleHits
    )

    $updated = [regex]::Replace($Text, $Pattern, $Replacement)
    if (-not [string]::Equals($updated, $Text, [System.StringComparison]::Ordinal)) {
        Add-UniqueString -List $RuleHits -Value $RuleName
    }

    return $updated
}

function Protect-PalLlmPortableTextSurface {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if (-not (Test-Path -LiteralPath $RootPath -PathType Container)) {
        throw "Portable text-surface root does not exist: $RootPath"
    }

    $textExtensions = @(".bat", ".json", ".lua", ".md", ".ps1", ".txt", ".yaml", ".yml")
    $windowsUserPathPattern = '(?<prefix>[A-Za-z]:\\Users\\)(?<user>[^\\/:"\r\n]+)'
    $posixHomePathPattern = '(?<prefix>/(?:home|Users)/)(?<user>[A-Za-z0-9._-]+)'
    $apiKeyFieldPattern = '(?i)(?<prefix>"?(?:api[_\-\s]?key|authorization|bearer|token|secret)"?\s*[:=]\s*"?)(?<secret>[A-Za-z0-9._~+/\-=]{16,})'
    $bearerTokenPattern = '(?i)(?<prefix>\bBearer\s+)(?<secret>[A-Za-z0-9._~+/\-=]{16,})'
    $prefixedApiKeyPattern = '\b(?:sk-ant-[A-Za-z0-9_\-]{16,}|sk-[A-Za-z0-9_\-]{16,})\b'
    $checkedFileCount = 0
    $redactedFileCount = 0
    $ruleHits = [System.Collections.Generic.List[string]]::new()

    foreach ($file in Get-ChildItem -LiteralPath $RootPath -Recurse -File -ErrorAction SilentlyContinue) {
        if ($file.Length -gt 4MB) {
            continue
        }

        if ($textExtensions -notcontains $file.Extension.ToLowerInvariant()) {
            continue
        }

        $text = Get-Content -LiteralPath $file.FullName -Raw
        $checkedFileCount++
        $redacted = $text
        $redacted = Invoke-PalLlmTextRedactionRule -Text $redacted -Pattern $apiKeyFieldPattern -Replacement '${prefix}[redacted-key]' -RuleName "api-key-field" -RuleHits $ruleHits
        $redacted = Invoke-PalLlmTextRedactionRule -Text $redacted -Pattern $bearerTokenPattern -Replacement '${prefix}[redacted-key]' -RuleName "bearer-token" -RuleHits $ruleHits
        $redacted = Invoke-PalLlmTextRedactionRule -Text $redacted -Pattern $prefixedApiKeyPattern -Replacement '[redacted-key]' -RuleName "prefixed-api-key" -RuleHits $ruleHits
        $redacted = Invoke-PalLlmTextRedactionRule -Text $redacted -Pattern $windowsUserPathPattern -Replacement '${prefix}[user]' -RuleName "windows-user-path" -RuleHits $ruleHits
        $redacted = Invoke-PalLlmTextRedactionRule -Text $redacted -Pattern $posixHomePathPattern -Replacement '${prefix}[user]' -RuleName "posix-home-path" -RuleHits $ruleHits

        if (-not [string]::Equals($redacted, $text, [System.StringComparison]::Ordinal)) {
            Set-Content -LiteralPath $file.FullName -Value $redacted -Encoding UTF8
            $redactedFileCount++
        }
    }

    return [pscustomobject]@{
        CheckedFileCount = $checkedFileCount
        RedactedFileCount = $redactedFileCount
        RuleHits = @($ruleHits)
    }
}

function Get-SteamLibraryRoots {
    $roots = [System.Collections.Generic.List[string]]::new()

    $defaultCandidates = @(
        "C:\Program Files (x86)\Steam",
        "C:\Program Files\Steam",
        "C:\Steam",
        "D:\SteamLibrary",
        "E:\SteamLibrary",
        "F:\SteamLibrary",
        "G:\SteamLibrary",
        "H:\SteamLibrary"
    )

    foreach ($candidate in $defaultCandidates) {
        $resolved = Resolve-ExistingPath -Path $candidate
        if ($resolved) {
            Add-UniqueString -List $roots -Value $resolved
        }
    }

    try {
        $steamPath = Get-ItemPropertyValue -Path "HKCU:\Software\Valve\Steam" -Name "SteamPath" -ErrorAction Stop
        $resolved = Resolve-ExistingPath -Path $steamPath
        if ($resolved) {
            Add-UniqueString -List $roots -Value $resolved
        }
    }
    catch {
    }

    foreach ($steamRoot in @($roots)) {
        $libraryFoldersPath = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
        if (-not (Test-Path -LiteralPath $libraryFoldersPath)) {
            continue
        }

        try {
            $matches = Select-String -Path $libraryFoldersPath -Pattern '"path"\s+"([^"]+)"'
            foreach ($match in $matches) {
                $libraryPath = $match.Matches[0].Groups[1].Value -replace '\\\\', '\'
                $resolvedLibrary = Resolve-ExistingPath -Path $libraryPath
                if ($resolvedLibrary) {
                    Add-UniqueString -List $roots -Value $resolvedLibrary
                }
            }
        }
        catch {
        }
    }

    return $roots.ToArray()
}

function Get-PalworldCandidateRoots {
    $candidates = [System.Collections.Generic.List[string]]::new()

    foreach ($steamLibrary in Get-SteamLibraryRoots) {
        $candidate = Join-Path $steamLibrary "steamapps\common\Palworld"
        if (Test-Path -LiteralPath (Join-Path $candidate "Pal\Binaries\Win64")) {
            Add-UniqueString -List $candidates -Value (Resolve-ExistingPath -Path $candidate)
        }
    }

    return $candidates.ToArray()
}

function Resolve-PalworldInstall {
    param(
        [string]$PalworldPath
    )

    $resolvedRoot = $null
    $resolvedInput = $null

    if (-not [string]::IsNullOrWhiteSpace($PalworldPath)) {
        $resolvedInput = Resolve-ExistingPath -Path $PalworldPath
        if (-not $resolvedInput) {
            throw "Palworld path does not exist: $PalworldPath"
        }

        if (Test-Path -LiteralPath (Join-Path $resolvedInput "Pal\Binaries\Win64")) {
            $resolvedRoot = $resolvedInput
        }
        elseif (Test-Path -LiteralPath (Join-Path $resolvedInput "Binaries\Win64")) {
            $resolvedRoot = Split-Path -Parent $resolvedInput
        }
        elseif ((Split-Path -Leaf $resolvedInput) -ieq "Win64") {
            $parent = Split-Path -Parent $resolvedInput
            $grandParent = Split-Path -Parent $parent
            if ((Split-Path -Leaf $parent) -ieq "Binaries" -and (Split-Path -Leaf $grandParent) -ieq "Pal") {
                $resolvedRoot = Split-Path -Parent $grandParent
            }
        }

        if (-not $resolvedRoot) {
            throw "Palworld path must point to the game root or to Pal\\Binaries\\Win64. Received: $resolvedInput"
        }
    }
    else {
        $resolvedRoot = Get-PalworldCandidateRoots | Select-Object -First 1
        if (-not $resolvedRoot) {
            throw "Palworld could not be auto-detected. Pass -PalworldPath with the game root or Win64 folder."
        }
    }

    $win64Path = Join-Path $resolvedRoot "Pal\Binaries\Win64"
    if (-not (Test-Path -LiteralPath $win64Path)) {
        throw "Palworld Win64 folder was not found under $resolvedRoot"
    }

    $candidateModRoots = @(
        (Join-Path $win64Path "ue4ss\Mods"),
        (Join-Path $win64Path "Mods")
    )

    $existingModRoot = $candidateModRoots | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    $modRoot = if ($existingModRoot) { $existingModRoot } else { $candidateModRoots[0] }

    return [pscustomobject]@{
        Root = $resolvedRoot
        Win64Path = $win64Path
        CandidateModRoots = $candidateModRoots
        ModRoot = $modRoot
        InstalledModPath = Join-Path $modRoot "PalLLM"
    }
}

function Get-PalworldExecutablePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PalworldRoot
    )

    $resolvedRoot = Resolve-ExistingPath -Path $PalworldRoot
    if (-not $resolvedRoot) {
        throw "Palworld root does not exist: $PalworldRoot"
    }

    $gameExe = Join-Path $resolvedRoot "Pal\Binaries\Win64\Palworld-Win64-Shipping.exe"
    if (-not (Test-Path -LiteralPath $gameExe)) {
        throw "Palworld executable was not found at $gameExe"
    }

    return $gameExe
}

function Test-CommandAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommandName
    )

    return $null -ne (Get-Command -Name $CommandName -ErrorAction SilentlyContinue)
}

# Shared HTTP helpers for smoke / replay / doctor scripts. Each script used to
# redefine its own ConvertTo-JsonBody + Invoke-PalApi pair; consolidating here
# keeps the JSON depth, header list, and error-unwrapping in one place.

function ConvertTo-PalLlmJsonBody {
    param(
        [Parameter(Mandatory = $true)]
        [object]$InputObject
    )

    return ((ConvertTo-PalLlmJsonValue -InputObject $InputObject) | ConvertTo-Json -Depth 12 -Compress)
}

function Get-PalLlmNormalizedBaseUrl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl
    )

    return ($BaseUrl.TrimEnd('/'))
}

function Invoke-PalLlmApi {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("GET", "POST")]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [object]$Body,

        [int]$TimeoutSeconds = 8
    )

    $normalized = Get-PalLlmNormalizedBaseUrl -BaseUrl $BaseUrl
    $uri = "{0}{1}" -f $normalized, $Path
    $splat = @{
        Method          = $Method
        Uri             = $uri
        TimeoutSec      = $TimeoutSeconds
        UseBasicParsing = $true
    }

    if ($null -ne $Body) {
        $splat.ContentType = "application/json"
        $splat.Body = (ConvertTo-PalLlmJsonBody -InputObject $Body)
    }

    try {
        return Invoke-RestMethod @splat
    }
    catch {
        throw "PalLLM API call failed: $Method $uri - $($_.Exception.Message)"
    }
}

function Get-PalLlmPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $InputObject -or [string]::IsNullOrWhiteSpace($Name)) {
        return $null
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        if ($InputObject.Contains($Name)) {
            return $InputObject[$Name]
        }

        return $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function ConvertTo-PalLlmJsonValue {
    param(
        [AllowNull()]
        [object]$InputObject
    )

    if ($null -eq $InputObject) {
        return $null
    }

    if ($InputObject -is [DateTimeOffset]) {
        return $InputObject.ToUniversalTime().ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    }

    if ($InputObject -is [DateTime]) {
        return ([DateTimeOffset]$InputObject).ToUniversalTime().ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    }

    if ($InputObject -is [string] -or $InputObject -is [ValueType]) {
        return $InputObject
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        $map = [ordered]@{}
        foreach ($key in $InputObject.Keys) {
            $map[[string]$key] = ConvertTo-PalLlmJsonValue -InputObject $InputObject[$key]
        }

        return $map
    }

    if ($InputObject -is [System.Collections.IEnumerable]) {
        $items = @()
        foreach ($item in $InputObject) {
            $items += ,(ConvertTo-PalLlmJsonValue -InputObject $item)
        }

        return ,$items
    }

    $objectMap = [ordered]@{}
    foreach ($property in $InputObject.PSObject.Properties) {
        if (-not $property.IsGettable) {
            continue
        }

        $objectMap[$property.Name] = ConvertTo-PalLlmJsonValue -InputObject $property.Value
    }

    return $objectMap
}
