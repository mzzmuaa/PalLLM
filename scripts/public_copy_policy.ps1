function Get-PublicCopyPolicy {
    [CmdletBinding()]
    param(
        [string]$RepoRoot = (Join-Path $PSScriptRoot "..")
    )

    $repoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

    $publicFiles = @(
        (Join-Path $repoRoot "README.md"),
        (Join-Path $repoRoot "NOTICE.md"),
        (Join-Path $repoRoot "SECURITY.md"),
        (Join-Path $repoRoot "docs/INDEX.md"),
        (Join-Path $repoRoot "docs/RELEASE.md")
    )

    $publicSupportFiles = @(
        (Join-Path $repoRoot "CONTRIBUTING.md")
    )

    $issueTemplateRoot = Join-Path $repoRoot ".github/ISSUE_TEMPLATE"
    if (Test-Path -LiteralPath $issueTemplateRoot -PathType Container) {
        $publicSupportFiles += Get-ChildItem -LiteralPath $issueTemplateRoot -Recurse -File |
            Where-Object { $_.Extension -in @(".md", ".yml", ".yaml") } |
            ForEach-Object { $_.FullName }
    }

    $blockedPublicScopePatterns = @(
        [pscustomobject]@{
            Pattern = 'generic AI\s+platform'
            Message = "Public-facing copy should describe PalLLM as a focused Palworld companion mod, not a generic AI platform."
        },
        [pscustomobject]@{
            Pattern = 'generic platform'
            Message = "Public-facing copy should not widen PalLLM into a generic platform."
        },
        [pscustomobject]@{
            Pattern = 'cross-game'
            Message = "Public-facing copy should not suggest cross-game product scope."
        },
        [pscustomobject]@{
            Pattern = 'multi-game'
            Message = "Public-facing copy should not suggest multi-game product scope."
        },
        [pscustomobject]@{
            Pattern = 'game-agnostic'
            Message = "Public-facing copy should stay Palworld-first rather than game-agnostic."
        },
        [pscustomobject]@{
            Pattern = 'universal\s+game\s+agent'
            Message = "Public-facing copy should not market PalLLM as a universal game agent."
        },
        [pscustomobject]@{
            Pattern = 'all\s+games'
            Message = "Public-facing copy should not claim broad all-games support."
        }
    )

    $blockedPublicFranchisePatterns = @(
        [pscustomobject]@{
            Pattern = '\b(?:Pok(?:e|\u00E9)mon|Pikachu|Nintendo|Mario|Zelda|Star\s+Wars|Jedi|Sith|Marvel|Avengers|DC(?:\s+Comics)?|Batman|Superman|Wonder\s+Woman|Disney|Minecraft|Fortnite|Roblox|RimWorld|Skyrim|Fallout|Cyberpunk\s+2077|Harry\s+Potter|Hogwarts|Warhammer|Mass\s+Effect|Dragon\s+Age|The\s+Witcher|League\s+of\s+Legends|Dungeons\s*(?:&|and)\s*Dragons|D\s*&\s*D|DnD|Baldur.?s\s+Gate|Elden\s+Ring|Dark\s+Souls|Monster\s+Hunter|Final\s+Fantasy|World\s+of\s+Warcraft|Warcraft|One\s+Piece|Dragon\s+Ball|Naruto|Gundam|Studio\s+Ghibli|Ghibli|ARK\s*:?\s*Survival)\b'
            Message = "Public-facing copy should not use unrelated third-party game or entertainment franchise names as comparisons or shorthand."
        }
    )

    $blockedPublicLegalOverclaimPatterns = @(
        [pscustomobject]@{
            Pattern = '\b(?:lawyer[-\s]?proof|legal[-\s]?risk[-\s]?free|no\s+legal\s+risk|guaranteed\s+legal|fully\s+IP[-\s]?neutral|100%\s+IP[-\s]?neutral|compliance[-\s]?certified)\b'
            Message = "Public-facing copy should not make legal, IP-neutrality, or compliance-certainty overclaims."
        }
    )

    $blockedSiblingProjectPatterns = @(
        [pscustomobject]@{
            # Pass 372 widening: the repo is going public, so guard
            # against any sibling-project leak — bare project names,
            # bare prompt-pack identifiers, and local sibling paths.
            # Match `Byte` only when capitalised (the noun "byte" is
            # legitimate technical vocabulary and stays case-sensitive).
            Pattern = '(?:\bByte\b|\bOmniForge\b|\bDeepForge\b|\bVulcan\b|\bRimLLM\b|\bbyte-(?:forge|forward|synthesis|qwen-frontier|qwen-modernize|council)\b)|D:[\\/]+Coding[\\/]+(?:Byte|RimLLM|OmniForge|DeepForge|Vulcan)'
            Message = "Public-facing copy should not mention sibling project names, local sibling paths, or imported prompt-pack identities. Pass 372 widened the block list to include `Byte` (capitalised), `Vulcan`, and bare `OmniForge`/`DeepForge` so the repo can be made public without leaking the maintainer's other private projects."
        }
    )

    return [pscustomobject]@{
        RepoRoot = $repoRoot
        PublicFiles = $publicFiles
        PublicSupportFiles = $publicSupportFiles
        BlockedPublicBrandPattern = '(?i)\b(OpenAI|Anthropic|Claude Desktop|ChatGPT|Copilot|Cursor|VS Code|Visual Studio Code|Ollama|LM Studio|vLLM|SGLang|llama\.cpp|DashScope|Qwen[0-9A-Za-z:._-]*|Gemma[0-9A-Za-z:._-]*|Mistral|Unsloth|NVIDIA|TensorRT(?:-LLM)?|Hugging\s+Face|OpenVINO|Foundry\s+Local|DeepSeek|OpenRouter)\b'
        BlockedPublicScopePatterns = $blockedPublicScopePatterns
        BlockedPublicFranchisePatterns = $blockedPublicFranchisePatterns
        BlockedPublicLegalOverclaimPatterns = $blockedPublicLegalOverclaimPatterns
        BlockedSiblingProjectPatterns = $blockedSiblingProjectPatterns
    }
}
