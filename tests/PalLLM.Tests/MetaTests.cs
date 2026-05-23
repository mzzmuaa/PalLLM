using System.Text.Json;
using System.Text.RegularExpressions;

namespace PalLLM.Tests;

/// <summary>
/// Meta-tests that pin the documentation + agent-readiness invariants as
/// part of the test suite. The drift gates in <c>scripts/run_full_audit.ps1</c>
/// already enforce most of these, but the test layer adds belt-and-suspenders
/// coverage and lets an agent verify documentation health by running
/// <c>dotnet test</c> alone (no PowerShell required).
///
/// <para>If one of these tests fails, the corresponding doc / artifact
/// drifted from the agreed-upon shape — fix the artifact, not the test.</para>
/// </summary>
public sealed class MetaTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Test]
    public void AgentBriefings_AllExpectedDoorwayFiles_Exist()
    {
        // PalLLM publishes multiple agent-onboarding "doorways" so any
        // tool's per-repo convention finds the source of truth.
        string[] required =
        {
            "AGENTS.md",
            "CLAUDE.md",
            "llms.txt",
            ".cursorrules",
            ".github/copilot-instructions.md",
        };

        foreach (string rel in required)
        {
            string abs = Path.Combine(RepoRoot, rel);
            Assert.That(File.Exists(abs), $"Expected agent-doorway file missing: {rel}");
        }
    }

    [Test]
    public void ProjectNumbers_Json_IsParseableAndContainsRequiredKeys()
    {
        string path = Path.Combine(RepoRoot, "docs", "PROJECT_NUMBERS.json");
        Assert.That(File.Exists(path), "docs/PROJECT_NUMBERS.json missing");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;

        string[] requiredKeys =
        {
            "project", "tests", "driftGates", "buildWarnings",
            "apiRoutes", "mcpTools", "fallbackStrategies",
            "featureCatalog", "featureReady", "featureScaffolded", "featureDeferred",
            "adrsAccepted", "honestRoadmap", "linksToSourceOfTruth",
        };

        foreach (string key in requiredKeys)
        {
            Assert.That(root.TryGetProperty(key, out _), $"PROJECT_NUMBERS.json missing required key: {key}");
        }
    }

    [Test]
    public void ProjectNumbers_TestCount_AgreesWithLiveTestCount()
    {
        // The single source of truth is the executable NUnit case count in
        // this assembly: [Test] methods plus each declared [TestCase].
        // PROJECT_NUMBERS.json should match.
        int liveCount = CountExecutableNUnitCasesAcrossSuite();

        string path = Path.Combine(RepoRoot, "docs", "PROJECT_NUMBERS.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int recorded = doc.RootElement.GetProperty("tests").GetInt32();

        Assert.That(recorded, Is.EqualTo(liveCount),
            $"PROJECT_NUMBERS.json says {recorded} tests but the suite has {liveCount}.");
    }

    [Test]
    public void SelfDescriptionBuilder_HardcodedLiterals_MatchProjectNumbers()
    {
        // SelfDescriptionBuilder.cs hands MCP / curl callers a structured
        // self-description that includes literal counts (MCP tool count,
        // fallback strategy count) and a baseline-date stamp. None of those
        // literals are reached by the regex-based drift gates, so they can
        // silently age past the rest of the rolling state.
        //
        // This test pins them against PROJECT_NUMBERS.json so any future
        // bump to the canonical numbers also requires the C# literal to be
        // updated, the same shape Drift_Test_count_docs uses.
        string projectNumbersPath = Path.Combine(RepoRoot, "docs", "PROJECT_NUMBERS.json");
        Assert.That(File.Exists(projectNumbersPath), "docs/PROJECT_NUMBERS.json missing");

        using var doc = JsonDocument.Parse(File.ReadAllText(projectNumbersPath));
        JsonElement root = doc.RootElement;
        int expectedMcpTools = root.GetProperty("mcpTools").GetInt32();
        int expectedFallbackStrategies = root.GetProperty("fallbackStrategies").GetInt32();
        string expectedBaselineDate = root.GetProperty("baselineDate").GetString() ?? string.Empty;

        string selfDescriptionPath = Path.Combine(
            RepoRoot, "src", "PalLLM.Sidecar", "SelfDescriptionBuilder.cs");
        Assert.That(File.Exists(selfDescriptionPath), "SelfDescriptionBuilder.cs missing");

        string source = File.ReadAllText(selfDescriptionPath);

        Assert.That(source, Does.Contain($"{expectedMcpTools} tools, "),
            $"SelfDescriptionBuilder.cs MCP tool literal must read '{expectedMcpTools} tools, ...' to match PROJECT_NUMBERS.json. " +
            "Update both when bumping the MCP surface.");

        Assert.That(source, Does.Contain($"FallbackStrategyCount: {expectedFallbackStrategies}"),
            $"SelfDescriptionBuilder.cs FallbackStrategyCount must equal {expectedFallbackStrategies} to match PROJECT_NUMBERS.json. " +
            "Update both when adding a Try_* method.");

        Assert.That(source, Does.Contain($"AuditedOn: \"{expectedBaselineDate}\""),
            $"SelfDescriptionBuilder.cs AuditedOn must read \"{expectedBaselineDate}\" to match PROJECT_NUMBERS.json baselineDate. " +
            "Update both during the baseline-roll pass.");
    }

    [Test]
    public void LuaBridge_ConsistentAcrossDocsAndRuntime()
    {
        // The Lua version PalLLM targets is stated in six places: the
        // .luacheckrc and lua.yml workflow are the load-bearing source
        // (luacheck is configured against this version), and four docs
        // mirror the fact in human-readable form. They drifted before
        // (Pass 158 fixed five docs that said "5.1" while the toolchain
        // pinned 5.4); this test prevents the next drift from sneaking
        // through unnoticed.
        string ToolchainVersion()
        {
            string luacheck = File.ReadAllText(Path.Combine(RepoRoot, ".luacheckrc"));
            Match m = Regex.Match(luacheck, @"Lua\s*(?<v>5\.\d)", RegexOptions.IgnoreCase);
            Assert.That(m.Success, ".luacheckrc must declare the target Lua version (e.g. '-- UE4SS targets Lua 5.4').");
            return m.Groups["v"].Value;
        }

        string expected = ToolchainVersion();

        (string path, string pattern, string label)[] consumers =
        {
            (".github/workflows/lua.yml", @"Lua\s*5\.\d", "lua workflow"),
            ("agents.json", "\"lua\"\\s*:\\s*\"5\\.\\d via UE4SS\"", "agents.json"),
            ("CLAUDE.md", @"Lua 5\.\d via UE4SS", "CLAUDE.md"),
            ("mod/README.md", @"\*\*Lua 5\.\d\*\*", "mod/README.md"),
            ("docs/MENTAL_MODEL.md", @"Lua 5\.\d", "MENTAL_MODEL.md"),
            ("docs/PROJECT_NUMBERS.json", "\"lua\"\\s*:\\s*\"5\\.\\d via UE4SS\"", "PROJECT_NUMBERS.json"),
        };

        foreach ((string rel, string pattern, string label) in consumers)
        {
            string content = File.ReadAllText(Path.Combine(RepoRoot, rel));
            Match m = Regex.Match(content, pattern);
            Assert.That(m.Success, $"{label} ({rel}) must mention Lua 5.x somewhere matching pattern: {pattern}");
            Match version = Regex.Match(m.Value, @"5\.\d");
            Assert.That(version.Value, Is.EqualTo(expected),
                $"{label} ({rel}) declares Lua {version.Value}; .luacheckrc pins {expected}. They must agree.");
        }

        string runtimeSource = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "PalLLM.Domain", "Runtime", "PalLlmRuntime.cs"));
        string luaSource = File.ReadAllText(Path.Combine(
            RepoRoot, "mod", "ue4ss", "Mods", "PalLLM", "Scripts", "main.lua"));

        foreach (string extension in new[] { ".mp3", ".m4a", ".aac", ".wma", ".ogg", ".opus", ".flac" })
        {
            Assert.That(runtimeSource, Does.Contain($"\"{extension}\""),
                $"PalLlmRuntime.cs must keep {extension} in the media_player playback-hint surface.");
            Assert.That(luaSource, Does.Contain($"\"{extension}\""),
                $"main.lua must recognize {extension} when the runtime emits PlaybackHint=media_player.");
        }

        foreach (string mime in new[]
                 {
                     "audio/mpeg", "audio/mp3", "audio/mp4", "audio/x-m4a", "audio/aac",
                     "audio/wma", "audio/x-ms-wma", "audio/ogg", "audio/opus", "audio/flac",
                 })
        {
            Assert.That(runtimeSource, Does.Contain($"\"{mime}\""),
                $"PalLlmRuntime.cs must keep {mime} in the media_player playback-hint surface.");
            Assert.That(luaSource, Does.Contain($"\"{mime}\""),
                $"main.lua must recognize {mime} when the runtime emits PlaybackHint=media_player.");
        }

        Assert.That(runtimeSource, Does.Contain("\".pcm\""),
            "PalLlmRuntime.cs must keep .pcm in the raw_pcm playback-hint surface.");
        Assert.That(luaSource, Does.Contain("\".pcm\""),
            "main.lua must recognize .pcm as raw_pcm proof-only audio.");
        foreach (string mime in new[] { "audio/pcm", "audio/l16" })
        {
            Assert.That(runtimeSource, Does.Contain($"\"{mime}\""),
                $"PalLlmRuntime.cs must keep {mime} in the raw_pcm playback-hint surface.");
            Assert.That(luaSource, Does.Contain($"\"{mime}\""),
                $"main.lua must recognize {mime} as raw_pcm proof-only audio.");
        }

        Assert.That(luaSource, Does.Contain("speech raw pcm requires native mixer binding"),
            "main.lua must report raw PCM as an explicit native-mixer blocker instead of launching a desktop helper.");
        Assert.That(luaSource, Does.Contain("\\\"FailureCode\\\""),
            "main.lua must emit a stable speech_playback failure code so proof tooling does not parse reason prose.");
        Assert.That(luaSource, Does.Contain("raw_pcm_native_mixer_required"),
            "main.lua must pair raw PCM receipts with a stable native-mixer failure code.");
        Assert.That(luaSource, Does.Contain("native_audio_mixer_enabled"),
            "main.lua must keep native raw-PCM mixer playback behind an explicit default-off bridge switch.");
        Assert.That(luaSource, Does.Contain("native_audio_mixer_unavailable"),
            "main.lua must emit a stable native-mixer-unavailable failure code when the raw-PCM mixer callback is not bound.");
        Assert.That(luaSource, Does.Contain("\"native_mixer\""),
            "main.lua must emit PlaybackMode=native_mixer only for the explicit native audio mixer path.");
        Assert.That(luaSource, Does.Contain("mime_type_base"),
            "main.lua must route audio MIME types by media-type base so parameters such as audio/L16; rate=24000 do not break playback hints.");
        Assert.That(luaSource, Does.Contain("raw_pcm_block_alignment_invalid"),
            "main.lua must reject raw PCM artifacts that do not contain complete sample frames before native-mixer promotion.");
        foreach (string field in new[] { "PlaybackSequence", "SupersededRequestId", "SupersededSpeechCount", "SupersededSpeechAgeMs", "SupersededSpeechBufferedMs", "SupersededSpeechRemainingMs", "CancellationMode", "SampleRateHz", "ChannelCount", "BitsPerSample", "DurationMs", "ByteRate", "BlockAlignBytes", "AudioDataBytes", "FrameCount", "BlockRemainderBytes", "ValidBitsPerSample", "ChannelMask", "AudioEncoding", "SampleFormat", "ByteOrder", "MixerConversionHint", "MixerQuantumMs", "MixerQuantumFrames", "MixerQueueDepthEstimate", "MixerTailFrames", "MixerBufferedMs", "MixerTailMs" })
        {
            Assert.That(luaSource, Does.Contain($"\\\"{field}\\\""),
                $"main.lua must emit {field} on speech_playback receipts so native-audio proof can verify format metadata without storing audio bytes.");
        }

        Assert.That(luaSource, Does.Match("generation = delivery_queue_generation,\\s+request_id = tostring\\(card\\.request_id or \"\"\\)"),
            "main.lua must preserve request ids when it copies scheduled delivery cards so live reply_delivery proof remains correlated.");

        Assert.That(luaSource, Does.Contain("wave_encoding_unsupported"),
            "main.lua must distinguish unsupported WAV encodings before claiming helper playback started.");
        Assert.That(luaSource, Does.Contain("wave_block_alignment_invalid"),
            "main.lua must reject WAV data chunks that are not aligned to their declared atomic block size before claiming helper playback started.");
    }

    [Test]
    public void ConnectorInventory_PalJson_AgentsJson_AndFilesystem_Agree()
    {
        // PalLLM ships eight inference connectors (Ollama support removed in
        // Pass 339; llama.cpp is the default, vLLM for high-config GPUs).
        // Three sources should agree
        // on which ones exist: the connect-*.ps1 filesystem, pal.json's
        // `connect` verb scripts array, and agents.json's `inferenceWiring`
        // capability map. They drifted before (Pass 161 fixed pal.json
        // missing llamacpp + agents.json missing vllm-omni and transformers).
        // Source of truth: the filesystem (the actual scripts that exist).
        string scriptsDir = Path.Combine(RepoRoot, "scripts");
        HashSet<string> filesystem = Directory
            .EnumerateFiles(scriptsDir, "connect-*.ps1")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            // connect-mcp-client wires an MCP host (Claude Desktop / VS Code) — it's
            // not an inference connector, so it's the one connect-*.ps1 the inventory
            // explicitly excludes from the inference-engine list.
            .Where(name => name != "connect-mcp-client")
            .Select(name => name.Substring("connect-".Length))
            .ToHashSet();

        string palJsonRaw = File.ReadAllText(Path.Combine(RepoRoot, "pal.json"));
        using JsonDocument palJson = JsonDocument.Parse(palJsonRaw);
        HashSet<string> palJsonInventory = new();
        foreach (JsonElement group in palJson.RootElement.GetProperty("groups").EnumerateArray())
        {
            foreach (JsonElement verb in group.GetProperty("verbs").EnumerateArray())
            {
                if (verb.GetProperty("verb").GetString() != "connect") continue;
                foreach (JsonElement script in verb.GetProperty("scripts").EnumerateArray())
                {
                    string scriptPath = script.GetString() ?? string.Empty;
                    string name = Path.GetFileNameWithoutExtension(scriptPath);
                    if (name.StartsWith("connect-")) palJsonInventory.Add(name.Substring("connect-".Length));
                }
            }
        }

        Assert.That(palJsonInventory.SetEquals(filesystem), Is.True,
            "pal.json `connect` verb scripts array must list every connect-*.ps1 file (excluding connect-mcp-client). " +
            $"Filesystem: [{string.Join(", ", filesystem.OrderBy(s => s))}]; " +
            $"pal.json: [{string.Join(", ", palJsonInventory.OrderBy(s => s))}].");

        // agents.json `inferenceWiring` is a capability map keyed by friendly
        // names (ollama, llamaCppRaw, vllmBlackwell, ...). Each value is a
        // command line that contains "pal connect <target>". Extract the
        // target word and assert it covers every filesystem entry.
        string agentsJsonRaw = File.ReadAllText(Path.Combine(RepoRoot, "agents.json"));
        using JsonDocument agentsJson = JsonDocument.Parse(agentsJsonRaw);
        HashSet<string> agentsInventory = new();
        JsonElement wiring = agentsJson.RootElement
            .GetProperty("capabilities")
            .GetProperty("inferenceWiring");
        foreach (JsonProperty entry in wiring.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String) continue;
            string command = entry.Value.GetString() ?? string.Empty;
            // The actual command strings carry the .ps1 extension
            // (`pwsh ./pal.ps1 connect ollama`), so the regex must allow it.
            Match m = Regex.Match(command, @"\bpal(?:\.ps1)?\s+connect\s+(?<target>\S+)");
            if (!m.Success) continue;
            string target = m.Groups["target"].Value;
            // Map MCP-friendly verb aliases back to their connect-*.ps1 stem
            // so the comparison stays meaningful: `omni` routes to
            // `connect-vllm-omni.ps1` per pal.ps1's Run-Connect switch.
            string mapped = target switch
            {
                "omni" => "vllm-omni",
                _ => target,
            };
            agentsInventory.Add(mapped);
        }

        Assert.That(agentsInventory.SetEquals(filesystem), Is.True,
            "agents.json `inferenceWiring` capability map must reference every connect-*.ps1 file (excluding connect-mcp-client). " +
            $"Filesystem: [{string.Join(", ", filesystem.OrderBy(s => s))}]; " +
            $"agents.json: [{string.Join(", ", agentsInventory.OrderBy(s => s))}].");
    }

    [Test]
    public void HonestVerdict_AgentsJson_AgreesWithReadiness()
    {
        // agents.json's honestVerdict.aggregateScore mirrors the headline of
        // docs/READINESS.md. They drifted before (Pass 162 found agents.json
        // claiming ~7.7 while READINESS claimed ~8.5 — both wrong, the
        // arithmetic mean of the per-aspect column is ~8.0). This test
        // prevents the two surfaces from disagreeing again.
        string agentsJsonRaw = File.ReadAllText(Path.Combine(RepoRoot, "agents.json"));
        using JsonDocument agentsJson = JsonDocument.Parse(agentsJsonRaw);
        string agentsScore = agentsJson.RootElement
            .GetProperty("honestVerdict")
            .GetProperty("aggregateScore")
            .GetString() ?? string.Empty;

        string readiness = File.ReadAllText(Path.Combine(RepoRoot, "docs", "READINESS.md"));
        // The headline is wrapped in `**...**` and includes trailing text
        // ("across 23 aspects."), so we don't anchor on `**` after the score.
        Match m = Regex.Match(readiness, @"Aggregate honest score:\s*(?<score>~?\d+(?:\.\d+)?\s*/\s*10)");
        Assert.That(m.Success, "READINESS.md must carry a headline of the form 'Aggregate honest score: ~X / 10'.");
        string readinessScore = m.Groups["score"].Value;

        // Normalise whitespace so "~8.0 / 10" and "~8.0/10" both match.
        string Normalise(string s) => Regex.Replace(s, @"\s+", string.Empty);

        Assert.That(Normalise(agentsScore), Is.EqualTo(Normalise(readinessScore)),
            $"agents.json honestVerdict.aggregateScore (\"{agentsScore}\") must match the " +
            $"READINESS.md headline (\"{readinessScore}\"). They drifted before (Pass 162). " +
            "Update both files when the per-aspect arithmetic moves.");
    }

    [Test]
    public void HardwareTierThresholds_AgreeBetween_DeriveTier_AndCompatibilityDoc()
    {
        // Pass 175 found that COMPATIBILITY.md's hardware-tier table
        // had drifted from the live HardwareProfiler.DeriveTier code:
        // the doc described an "entry-GPU Constrained" sub-tier that
        // didn't exist in the live classification. Pass 177 closes
        // that drift class with a meta-test.
        //
        // The test extracts the cpu-core and ram-GiB threshold integers
        // from BOTH:
        //   1. `DeriveTier(cores, ramGb, gpuLikely)` in HardwareProfiler.cs
        //   2. The "Hardware tier recommendations" table in COMPATIBILITY.md
        // and asserts they agree. If a future contributor bumps the
        // Generous threshold from `>= 16 cores AND >= 48 GiB` to
        // `>= 20 cores AND >= 64 GiB`, the doc must move with the code
        // or this test fires.

        string profilerPath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Inference", "HardwareProfiler.cs");
        Assert.That(File.Exists(profilerPath), $"missing: {profilerPath}");
        string profilerText = File.ReadAllText(profilerPath);

        // Match the live classification rule. Looks for two specific
        // numeric pairs (Constrained-promotion floor, Generous-promotion
        // floor) inside the DeriveTier method body. The pattern relies
        // on the canonical structure: `cores < N` paired with `ramGb < M`,
        // and `cores >= P` paired with `ramGb >= Q`.
        Match constrainedFloor = Regex.Match(
            profilerText,
            @"cores\s*<\s*(?<cores>\d+)\s*\|\|\s*ramGb\s*<\s*(?<ram>\d+)");
        Match generousFloor = Regex.Match(
            profilerText,
            @"cores\s*>=\s*(?<cores>\d+)\s*&&\s*ramGb\s*>=\s*(?<ram>\d+)");
        Assert.That(constrainedFloor.Success,
            "DeriveTier must contain a `cores < X || ramGb < Y` constraint pair.");
        Assert.That(generousFloor.Success,
            "DeriveTier must contain a `cores >= P && ramGb >= Q` Generous-promotion rule.");

        int liveStandardCores  = int.Parse(constrainedFloor.Groups["cores"].Value);
        int liveStandardRam    = int.Parse(constrainedFloor.Groups["ram"].Value);
        int liveGenerousCores  = int.Parse(generousFloor.Groups["cores"].Value);
        int liveGenerousRam    = int.Parse(generousFloor.Groups["ram"].Value);

        // Now read the doc table and pull the same four scalars. Each
        // table cell quotes the threshold inside backticks, e.g.
        //   `< 8 cores`, `< 16 GiB RAM`, `>= 16 cores`, `>= 48 GiB RAM`
        // We pin the rendering format so future tweaks to the table
        // can't silently break the test.
        string docPath = Path.Combine(RepoRoot, "docs", "COMPATIBILITY.md");
        Assert.That(File.Exists(docPath), $"missing: {docPath}");
        string docText = File.ReadAllText(docPath);

        // The doc renders `<` and `>=` either as ASCII (`< 8`, `>= 16`)
        // or as the Unicode operators `<` and `≥`. Accept either so a
        // future markdown polish pass that swaps the rendering doesn't
        // break the test.
        Match docConstrained = Regex.Match(
            docText,
            @"`Constrained`\s*\|\s*no GPU,\s*OR\s*`< (?<cores>\d+) cores`,\s*OR\s*`< (?<ram>\d+) GiB RAM`");
        Match docGenerous = Regex.Match(
            docText,
            @"`Generous`\s*\|\s*GPU present \+\s*`(?:>=|≥) (?<cores>\d+) cores`\s*\+\s*`(?:>=|≥) (?<ram>\d+) GiB RAM`");
        Assert.That(docConstrained.Success,
            "COMPATIBILITY.md must contain the canonical Constrained row " +
            "(`Constrained` | no GPU, OR `< N cores`, OR `< M GiB RAM` ...).");
        Assert.That(docGenerous.Success,
            "COMPATIBILITY.md must contain the canonical Generous row " +
            "(`Generous` | GPU present + `>= P cores` + `>= Q GiB RAM` ...).");

        int docStandardCores  = int.Parse(docConstrained.Groups["cores"].Value);
        int docStandardRam    = int.Parse(docConstrained.Groups["ram"].Value);
        int docGenerousCores  = int.Parse(docGenerous.Groups["cores"].Value);
        int docGenerousRam    = int.Parse(docGenerous.Groups["ram"].Value);

        var failures = new List<string>();
        if (docStandardCores != liveStandardCores)
        {
            failures.Add($"Constrained-tier core threshold: doc={docStandardCores}, code={liveStandardCores}");
        }
        if (docStandardRam != liveStandardRam)
        {
            failures.Add($"Constrained-tier RAM threshold: doc={docStandardRam} GiB, code={liveStandardRam} GiB");
        }
        if (docGenerousCores != liveGenerousCores)
        {
            failures.Add($"Generous-tier core threshold: doc={docGenerousCores}, code={liveGenerousCores}");
        }
        if (docGenerousRam != liveGenerousRam)
        {
            failures.Add($"Generous-tier RAM threshold: doc={docGenerousRam} GiB, code={liveGenerousRam} GiB");
        }

        Assert.That(failures, Is.Empty,
            "COMPATIBILITY.md hardware-tier table must agree with the live " +
            "HardwareProfiler.DeriveTier thresholds. Failures:\n  - " +
            string.Join("\n  - ", failures));
    }

    [Test]
    public void AuxiliaryCounts_DocsAdrsSchemas_AgreeWithLiveFilesystem_AndCompletionSurfaces()
    {
        // Pass 169 found that the long-claimed `38 MCP tools` was off-by-one
        // because the live attribute count was never compared to docs/JSON
        // claims. Pass 170 closes the equivalent gap for the auxiliary
        // counts that surface in `pal complete` and `docs/COMPLETION.md`:
        // doc count, ADR count, JSON Schema count, and the meta-test count
        // itself (which advertises drift protection in those same two
        // surfaces). Real drift was found on first run: pal-complete.ps1
        // and COMPLETION.md were claiming `21 meta-tests` while
        // MetaTests.cs had grown to 23 (plus this test = 24).

        // 1) docsCount -- stamped docs in docs/ vs PROJECT_NUMBERS.docsCount.
        string[] docsFolderMarkdown = Directory.GetFiles(Path.Combine(RepoRoot, "docs"), "*.md");
        int liveStampedDocs = docsFolderMarkdown.Count(f =>
        {
            string txt = File.ReadAllText(f);
            return Regex.IsMatch(txt, @"^Last audited:\s*`\d{4}-\d{2}-\d{2}`", RegexOptions.Multiline);
        });

        string projectNumbersRaw = File.ReadAllText(Path.Combine(RepoRoot, "docs", "PROJECT_NUMBERS.json"));
        using JsonDocument projectNumbers = JsonDocument.Parse(projectNumbersRaw);
        int declaredDocsCount = projectNumbers.RootElement.GetProperty("docsCount").GetInt32();
        int declaredAdrsAccepted = projectNumbers.RootElement.GetProperty("adrsAccepted").GetInt32();

        // 2) adrsAccepted -- ADRs with **Status:** Accepted in docs/adr/.
        string adrDir = Path.Combine(RepoRoot, "docs", "adr");
        string[] adrFiles = Directory.GetFiles(adrDir, "*.md")
            .Where(f => !string.Equals(Path.GetFileName(f), "README.md", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        int liveAcceptedAdrs = adrFiles.Count(f =>
        {
            string txt = File.ReadAllText(f);
            return Regex.IsMatch(txt, @"\*\*Status:\*\*\s+Accepted\b");
        });

        // 3) JSON Schema count -- *.schema.json in docs/schemas/ vs claims.
        string[] schemaFiles = Directory.GetFiles(Path.Combine(RepoRoot, "docs", "schemas"), "*.schema.json");
        int liveSchemaCount = schemaFiles.Length;

        // 4) Meta-test count -- [Test] attributes in this very file vs claims.
        // This file is `tests/PalLLM.Tests/MetaTests.cs`. The advertised
        // counts in pal-complete.ps1 + COMPLETION.md must keep up.
        string metaTestsPath = Path.Combine(RepoRoot, "tests", "PalLLM.Tests", "MetaTests.cs");
        string metaTestsText = File.ReadAllText(metaTestsPath);
        int liveMetaTestCount = Regex.Matches(metaTestsText, @"^\s*\[Test\]\s*$", RegexOptions.Multiline).Count;

        // 5) Parse the claims from pal-complete.ps1 + COMPLETION.md.
        string palCompletePath = Path.Combine(RepoRoot, "scripts", "pal-complete.ps1");
        string palCompleteText = File.ReadAllText(palCompletePath);
        Match palCompleteClaim = Regex.Match(
            palCompleteText,
            @"(?<gates>\d+)\s*gates\s*\+\s*(?<meta>\d+)\s*meta-tests\s*\+\s*(?<schemas>\d+)\s*JSON\s*Schemas");
        Assert.That(palCompleteClaim.Success,
            "scripts/pal-complete.ps1 must contain 'N gates + M meta-tests + K JSON Schemas' literal.");
        int palCompleteMeta    = int.Parse(palCompleteClaim.Groups["meta"].Value);
        int palCompleteSchemas = int.Parse(palCompleteClaim.Groups["schemas"].Value);

        string completionPath = Path.Combine(RepoRoot, "docs", "COMPLETION.md");
        string completionText = File.ReadAllText(completionPath);
        Match completionClaim = Regex.Match(
            completionText,
            @"`(?<meta>\d+)`\s*meta-tests\s*\+\s*`(?<schemas>\d+)`\s*JSON\s*Schemas\s*\+\s*`(?<gates>\d+)`");
        Assert.That(completionClaim.Success,
            "docs/COMPLETION.md must contain '`N` meta-tests + `K` JSON Schemas + `G`' literal.");
        int completionMeta    = int.Parse(completionClaim.Groups["meta"].Value);
        int completionSchemas = int.Parse(completionClaim.Groups["schemas"].Value);

        // Cross-checks.
        var failures = new List<string>();
        if (liveStampedDocs != declaredDocsCount)
        {
            failures.Add($"stamped docs in docs/ = {liveStampedDocs} != PROJECT_NUMBERS.docsCount = {declaredDocsCount}");
        }
        if (liveAcceptedAdrs != declaredAdrsAccepted)
        {
            failures.Add($"ADRs with **Status:** Accepted = {liveAcceptedAdrs} != PROJECT_NUMBERS.adrsAccepted = {declaredAdrsAccepted}");
        }
        if (liveMetaTestCount != palCompleteMeta)
        {
            failures.Add($"MetaTests.cs [Test] count = {liveMetaTestCount} != pal-complete.ps1 meta-tests claim = {palCompleteMeta}");
        }
        if (liveMetaTestCount != completionMeta)
        {
            failures.Add($"MetaTests.cs [Test] count = {liveMetaTestCount} != COMPLETION.md meta-tests claim = {completionMeta}");
        }
        if (liveSchemaCount != palCompleteSchemas)
        {
            failures.Add($"docs/schemas/*.schema.json count = {liveSchemaCount} != pal-complete.ps1 schemas claim = {palCompleteSchemas}");
        }
        if (liveSchemaCount != completionSchemas)
        {
            failures.Add($"docs/schemas/*.schema.json count = {liveSchemaCount} != COMPLETION.md schemas claim = {completionSchemas}");
        }

        Assert.That(failures, Is.Empty,
            "Auxiliary counts must agree between live filesystem, PROJECT_NUMBERS.json, " +
            "and the completion-surface advertised counts. Failures:\n  - " +
            string.Join("\n  - ", failures));
    }

    [Test]
    public void McpInventory_LiveAttributeCounts_AgreeWith_ProjectNumbers()
    {
        // PROJECT_NUMBERS.json declares mcpTools, mcpPrompts, mcpResources,
        // mcpResourceTemplates. The existing
        // SelfDescriptionBuilder_HardcodedLiterals_AgreeWith_ProjectNumbers
        // meta-test pins SelfDescriptionBuilder.cs's literal to those JSON
        // fields. But nothing pins the JSON fields to the LIVE ATTRIBUTE
        // COUNT in the actual MCP source. So if a contributor added a 39th
        // [McpServerTool] without bumping PROJECT_NUMBERS, both PROJECT_NUMBERS
        // and SelfDescriptionBuilder would still say 38, the meta-test would
        // pass, and reality would have silently drifted.
        //
        // This test closes that loop: count attributes in the live source,
        // assert each count agrees with the matching PROJECT_NUMBERS field.
        // Mirrors how Drift_Api_route_count, Drift_Feature_catalog_count,
        // and Drift_Fallback_strategy_count each pin their respective
        // surface in scripts/run_full_audit.ps1 — but for MCP, where there
        // was no equivalent end-to-end gate.

        string projectNumbersRaw = File.ReadAllText(Path.Combine(RepoRoot, "docs", "PROJECT_NUMBERS.json"));
        using JsonDocument doc = JsonDocument.Parse(projectNumbersRaw);
        JsonElement root = doc.RootElement;
        int expectedTools             = root.GetProperty("mcpTools").GetInt32();
        int expectedPrompts           = root.GetProperty("mcpPrompts").GetInt32();
        int expectedResources         = root.GetProperty("mcpResources").GetInt32();
        int expectedResourceTemplates = root.GetProperty("mcpResourceTemplates").GetInt32();

        string toolsPath     = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar", "Mcp", "PalLlmMcpTools.cs");
        string promptsPath   = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar", "Mcp", "PalLlmMcpPrompts.cs");
        string resourcesPath = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar", "Mcp", "PalLlmMcpResources.cs");
        Assert.That(File.Exists(toolsPath),     $"missing: {toolsPath}");
        Assert.That(File.Exists(promptsPath),   $"missing: {promptsPath}");
        Assert.That(File.Exists(resourcesPath), $"missing: {resourcesPath}");

        string toolsText     = File.ReadAllText(toolsPath);
        string promptsText   = File.ReadAllText(promptsPath);
        string resourcesText = File.ReadAllText(resourcesPath);

        // Tools — every method-level [McpServerTool] attribute counts.
        int liveTools = Regex.Matches(toolsText, @"\[McpServerTool\b").Count;

        // Prompts — only `[McpServerPrompt(Name = "...")]` per-method declarations
        // count. The class-level `[McpServerPromptType]` is a different attribute
        // we explicitly exclude.
        int livePrompts = Regex.Matches(promptsText, @"\[McpServerPrompt\s*\(\s*Name\s*=").Count;

        // Resources — every `[McpServerResource(...)]` per-method declaration
        // counts toward total resources. Among those, the ones whose
        // UriTemplate has a `{placeholder}` are the templated subset.
        var resourceMatches = Regex.Matches(
            resourcesText,
            @"\[McpServerResource\s*\((?<args>(?:[^()]|\([^()]*\))*)\)\]",
            RegexOptions.Singleline);
        int liveResourceTotal = resourceMatches.Count;
        int liveTemplatedResources = resourceMatches
            .Cast<Match>()
            .Count(m => Regex.IsMatch(m.Groups["args"].Value, @"UriTemplate\s*=\s*""[^""]*\{[^}]+\}"));
        int liveSimpleResources = liveResourceTotal - liveTemplatedResources;

        var failures = new List<string>();
        if (liveTools != expectedTools)
        {
            failures.Add($"[McpServerTool] live count = {liveTools} != PROJECT_NUMBERS.mcpTools = {expectedTools}");
        }
        if (livePrompts != expectedPrompts)
        {
            failures.Add($"[McpServerPrompt(Name=...)] live count = {livePrompts} != PROJECT_NUMBERS.mcpPrompts = {expectedPrompts}");
        }
        if (liveSimpleResources != expectedResources)
        {
            failures.Add(
                $"[McpServerResource(...)] simple-URI live count = {liveSimpleResources} != " +
                $"PROJECT_NUMBERS.mcpResources = {expectedResources} (live total = {liveResourceTotal}, " +
                $"live templated = {liveTemplatedResources})");
        }
        if (liveTemplatedResources != expectedResourceTemplates)
        {
            failures.Add(
                $"[McpServerResource(UriTemplate=\"...{{...}}...\")] live count = {liveTemplatedResources} != " +
                $"PROJECT_NUMBERS.mcpResourceTemplates = {expectedResourceTemplates}");
        }

        Assert.That(failures, Is.Empty,
            "MCP inventory must agree between live attribute counts and PROJECT_NUMBERS.json. " +
            "Failures:\n  - " + string.Join("\n  - ", failures));
    }

    [Test]
    public void Pal_VerbInventory_AgreesAcross_PalJson_PalPs1Dispatch_RunList()
    {
        // The PalLLM verb table is rendered in four places that have to
        // agree:
        //   1. pal.ps1's top-level switch dispatch — what `pal <verb>`
        //      actually runs.
        //   2. pal.json's machine-readable verb manifest — what any agent
        //      reading the JSON sees.
        //   3. pal.ps1's Run-List function — what `pal list` prints.
        //   4. pal.ps1's $known suggestion array — what the "did you mean?"
        //      hint considers when an unknown verb is typed.
        //
        // Pass 168 found a real drift: six Pass-164/165 verbs (native-proof,
        // replay, proof-bundle, hud-bind, verify, complete) had landed in
        // (1)+(2)+(3) but never in (4), so a typo like `pal verfy` would not
        // suggest `verify`. This test pins all four surfaces so the next
        // contributor adding a verb has to add it everywhere — or face a
        // failed `dotnet test` with the exact missing entries named.

        string palPs1 = File.ReadAllText(Path.Combine(RepoRoot, "pal.ps1"));
        string palJsonRaw = File.ReadAllText(Path.Combine(RepoRoot, "pal.json"));

        // 1) Top-level dispatch — the switch ($Verb.ToLowerInvariant()) block.
        // Match from the canonical dispatch line up to the first 'default' case.
        Match dispatchBlock = Regex.Match(
            palPs1,
            @"switch\s*\(\$Verb\.ToLowerInvariant\(\)\)\s*\{(?<body>.*?)^\s*default\s*\{",
            RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.That(dispatchBlock.Success,
            "pal.ps1 must have a `switch ($Verb.ToLowerInvariant())` block ending with `default {`.");
        var dispatchVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(
            dispatchBlock.Groups["body"].Value,
            @"^\s*'(?<verb>[a-z][a-z0-9-]*)'\s*\{",
            RegexOptions.Multiline))
        {
            dispatchVerbs.Add(m.Groups["verb"].Value);
        }
        Assert.That(dispatchVerbs.Count, Is.GreaterThanOrEqualTo(40),
            $"Expected at least 40 dispatch cases; found {dispatchVerbs.Count}");

        // 2) pal.json's manifest — flatten every verb across every group.
        var jsonVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (JsonDocument doc = JsonDocument.Parse(palJsonRaw))
        {
            foreach (JsonElement group in doc.RootElement.GetProperty("groups").EnumerateArray())
            {
                foreach (JsonElement entry in group.GetProperty("verbs").EnumerateArray())
                {
                    string verb = entry.GetProperty("verb").GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(verb)) { jsonVerbs.Add(verb); }
                }
            }
        }

        // 3) Run-List — every `Verb = '<name>'` entry.
        var runListVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(palPs1, @"Verb\s*=\s*'(?<verb>[a-z][a-z0-9-]*)'"))
        {
            runListVerbs.Add(m.Groups["verb"].Value);
        }

        // 4) $known suggestion array — the literal in the default branch.
        Match knownBlock = Regex.Match(
            palPs1,
            @"\$known\s*=\s*(?<body>(?:'[a-z][a-z0-9-]*'\s*,?\s*)+)",
            RegexOptions.Singleline);
        Assert.That(knownBlock.Success,
            "pal.ps1 must declare a `$known = '...','...'` suggestion array in the default branch.");
        var knownVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(knownBlock.Groups["body"].Value, @"'(?<verb>[a-z][a-z0-9-]*)'"))
        {
            knownVerbs.Add(m.Groups["verb"].Value);
        }

        // Cross-checks — every surface must enumerate the same set.
        var failures = new List<string>();
        void Diff(string aName, HashSet<string> a, string bName, HashSet<string> b)
        {
            var missingFromB = a.Except(b, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToArray();
            var missingFromA = b.Except(a, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToArray();
            if (missingFromB.Length > 0)
            {
                failures.Add($"verbs in {aName} but missing from {bName}: {string.Join(", ", missingFromB)}");
            }
            if (missingFromA.Length > 0)
            {
                failures.Add($"verbs in {bName} but missing from {aName}: {string.Join(", ", missingFromA)}");
            }
        }

        Diff("pal.ps1 dispatch", dispatchVerbs, "pal.json", jsonVerbs);
        Diff("pal.ps1 dispatch", dispatchVerbs, "pal.ps1 Run-List", runListVerbs);
        Diff("pal.ps1 dispatch", dispatchVerbs, "pal.ps1 $known", knownVerbs);

        Assert.That(failures, Is.Empty,
            "Verb inventory must agree across pal.ps1 dispatch, pal.json manifest, " +
            "pal.ps1 Run-List, and pal.ps1 $known suggester. Failures:\n  - " +
            string.Join("\n  - ", failures));
    }

    [Test]
    public void QueueInventory_PalComplete_CompletionMd_ImplementationQueue_Agree()
    {
        // Pass 165 shipped two completion surfaces — `pal complete`
        // (`scripts/pal-complete.ps1`) and the canonical written
        // `docs/COMPLETION.md` — each rendering the same six queues
        // from `docs/IMPLEMENTATION_QUEUE.md`. Without a gate, those
        // three surfaces can silently drift: a queue percentage
        // bumps in one, a queue name reframes in another, and the
        // operator gets contradictory answers depending on which
        // surface they hit. This test pins the inventory so any
        // future drift fails `dotnet test`.
        //
        // Cross-checks:
        //  - script and Markdown both list six queues with matching
        //    Id (Q1..Q6), Name, and Pct.
        //  - IMPLEMENTATION_QUEUE.md has exactly six `## Queue N:`
        //    headings (the upstream source). Heading text is more
        //    formal than the script/table short names; we don't
        //    insist on byte-equivalence there, only on the count.
        //  - Each queue's `Roadmap value unlocked: about N.N%` line
        //    in IMPLEMENTATION_QUEUE.md matches the script's Pct
        //    after normalising "about N.N%" / "~N.N%" -> "N.N%".

        // 1) Script source -- pal-complete.ps1 queue table.
        string scriptPath = Path.Combine(RepoRoot, "scripts", "pal-complete.ps1");
        Assert.That(File.Exists(scriptPath), $"missing: {scriptPath}");
        string scriptText = File.ReadAllText(scriptPath);
        var scriptQueueRegex = new Regex(
            @"Id\s*=\s*'(?<id>Q\d)'\s*\r?\n\s*Name\s*=\s*'(?<name>[^']+)'\s*\r?\n\s*Pct\s*=\s*'(?<pct>[^']+)'",
            RegexOptions.Multiline);
        var scriptQueues = scriptQueueRegex.Matches(scriptText)
            .Select(m => (Id: m.Groups["id"].Value, Name: m.Groups["name"].Value, Pct: m.Groups["pct"].Value))
            .ToList();
        Assert.That(scriptQueues.Count, Is.EqualTo(6),
            $"pal-complete.ps1 must declare exactly 6 queues; found {scriptQueues.Count}");

        // 2) Markdown source -- COMPLETION.md table rows `| N | name | `~X.X%` | ...`.
        string completionPath = Path.Combine(RepoRoot, "docs", "COMPLETION.md");
        Assert.That(File.Exists(completionPath), $"missing: {completionPath}");
        string completionText = File.ReadAllText(completionPath);
        var tableRowRegex = new Regex(
            @"^\|\s*(?<num>\d)\s*\|\s*(?<name>[^|]+?)\s*\|\s*`(?<pct>[^`]+)`\s*\|",
            RegexOptions.Multiline);
        var tableQueues = tableRowRegex.Matches(completionText)
            .Where(m => int.TryParse(m.Groups["num"].Value, out int n) && n >= 1 && n <= 6)
            .Select(m => (
                Id: "Q" + m.Groups["num"].Value,
                Name: m.Groups["name"].Value.Trim(),
                Pct: m.Groups["pct"].Value.Trim()))
            .ToList();
        Assert.That(tableQueues.Count, Is.EqualTo(6),
            $"COMPLETION.md must contain exactly 6 queue rows numbered 1..6; found {tableQueues.Count}");

        // 3) IMPLEMENTATION_QUEUE.md -- count headings and parse per-queue percentages.
        string queuePath = Path.Combine(RepoRoot, "docs", "IMPLEMENTATION_QUEUE.md");
        Assert.That(File.Exists(queuePath), $"missing: {queuePath}");
        string queueText = File.ReadAllText(queuePath);
        var headingRegex = new Regex(@"^## Queue (?<num>\d):", RegexOptions.Multiline);
        var headings = headingRegex.Matches(queueText)
            .Select(m => "Q" + m.Groups["num"].Value)
            .ToList();
        Assert.That(headings.Count, Is.EqualTo(6),
            $"IMPLEMENTATION_QUEUE.md must have exactly 6 `## Queue N:` headings; found {headings.Count}");

        var unlockedRegex = new Regex(@"Roadmap value unlocked:\s*\r?\n\s*\r?\n\s*-\s*about\s*`(?<pct>[^`]+)`",
            RegexOptions.Multiline);
        var unlockedPcts = unlockedRegex.Matches(queueText)
            .Select(m => m.Groups["pct"].Value.Trim())
            .ToList();
        Assert.That(unlockedPcts.Count, Is.EqualTo(6),
            $"IMPLEMENTATION_QUEUE.md must list 6 'Roadmap value unlocked: about `X.X%`' lines; found {unlockedPcts.Count}");

        // Normalise "~X%" / "X%" / "about X%" -> "X%" for comparison.
        string Norm(string s) => Regex.Replace(s, @"^[~]?\s*", string.Empty).Trim();

        // 4) Cross-checks.
        var failures = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            string id = "Q" + (i + 1);

            (string Id, string Name, string Pct) script = scriptQueues[i];
            (string Id, string Name, string Pct) table = tableQueues[i];

            if (script.Id != id) failures.Add($"pal-complete.ps1 queue at index {i} should be {id}; got {script.Id}");
            if (table.Id != id)  failures.Add($"COMPLETION.md row {i + 1} should be {id}; got {table.Id}");

            if (script.Name != table.Name)
            {
                failures.Add($"{id}: pal-complete.ps1 name \"{script.Name}\" != COMPLETION.md name \"{table.Name}\"");
            }
            if (Norm(script.Pct) != Norm(table.Pct))
            {
                failures.Add($"{id}: pal-complete.ps1 Pct \"{script.Pct}\" != COMPLETION.md Pct \"{table.Pct}\"");
            }
            if (Norm(script.Pct) != Norm(unlockedPcts[i]))
            {
                failures.Add($"{id}: pal-complete.ps1 Pct \"{script.Pct}\" != IMPLEMENTATION_QUEUE.md unlock \"{unlockedPcts[i]}\"");
            }
        }

        Assert.That(failures, Is.Empty,
            "Queue inventory must agree across pal-complete.ps1, COMPLETION.md, and " +
            "IMPLEMENTATION_QUEUE.md. Failures:\n  - " + string.Join("\n  - ", failures));
    }

    [Test]
    public void OperatorTurnkeyScripts_AreEachExposedAsAPalVerb()
    {
        // IMPLEMENTATION_QUEUE.md Queues 1/3/6 each name a script that an
        // operator with a live Palworld session (or a clean machine) must
        // run to advance the roadmap. Pass 164 added five `pal` verbs
        // (native-proof, replay, hud-bind, proof-bundle, verify) so each
        // queue-gating script is a single command, not a script path the
        // operator has to remember. This test pins that coverage so the
        // next contributor cannot quietly delete a verb or rename a script
        // and re-introduce the operator-friction class of drift the pass
        // closed.
        //
        // Each tuple: (scriptRelativePath, expectedPalVerb, queueAnchor).
        // queueAnchor is purely documentary — it explains why the
        // gate exists in case the test ever fails and someone reads the
        // assertion message cold.
        (string script, string verb, string queue)[] turnkey =
        {
            ("scripts/run-sidecar-smoke.ps1",            "smoke",        "Queue 1: full bridge loop smoke proof"),
            ("scripts/run-delivery-replay.ps1",          "replay",       "Queue 1: delivery envelope replay"),
            ("scripts/run-native-proof.ps1",             "native-proof", "Queue 1: live Palworld delivery_proven watcher"),
            ("scripts/export-release-proof-bundle.ps1",  "proof-bundle", "Queue 1: release-friendly evidence bundle"),
            ("scripts/apply-hud-bind-recommendation.ps1","hud-bind",     "Queue 3: native HUD widget seam bind"),
            ("scripts/verify-release-package.ps1",       "verify",       "Queue 6: clean-machine release-package verification"),
            ("scripts/package-release.ps1",              "package",      "Queue 6: release zip build"),
            ("scripts/pal-cleanup.ps1",                   "cleanup",      "Repo hygiene: generated-artifact cleanup"),
            ("scripts/pal-proof.ps1",                    "proof",        "Queue 1: read-only proof status summary"),
            ("scripts/pal-preflight.ps1",                "preflight",    "Pre-Queue: single-command readiness checklist"),
        };

        // Source 1: pal.json's machine-readable verb manifest.
        string palJsonRaw = File.ReadAllText(Path.Combine(RepoRoot, "pal.json"));
        using JsonDocument palJson = JsonDocument.Parse(palJsonRaw);
        var verbToScripts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement group in palJson.RootElement.GetProperty("groups").EnumerateArray())
        {
            foreach (JsonElement entry in group.GetProperty("verbs").EnumerateArray())
            {
                string verb = entry.GetProperty("verb").GetString() ?? string.Empty;
                var scripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("script", out JsonElement single) &&
                    single.ValueKind == JsonValueKind.String)
                {
                    scripts.Add(single.GetString()!);
                }
                if (entry.TryGetProperty("scripts", out JsonElement many) &&
                    many.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement s in many.EnumerateArray())
                    {
                        scripts.Add(s.GetString() ?? string.Empty);
                    }
                }
                verbToScripts[verb] = scripts;
            }
        }

        // Source 2: pal.ps1's switch-statement dispatch table. The dispatch
        // is the only thing PowerShell actually reads at runtime; a verb
        // that's in pal.json but not pal.ps1 is a phantom.
        string palPs1 = File.ReadAllText(Path.Combine(RepoRoot, "pal.ps1"));

        var failures = new List<string>();
        foreach ((string script, string verb, string queue) in turnkey)
        {
            string absScript = Path.Combine(RepoRoot, script.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absScript))
            {
                failures.Add($"missing script: {script} (was supposed to back `pal {verb}` for {queue})");
                continue;
            }

            if (!verbToScripts.TryGetValue(verb, out HashSet<string>? scripts))
            {
                failures.Add($"pal.json missing verb '{verb}' (should reference {script}; gates {queue})");
                continue;
            }

            // Normalise both sides to forward slashes since pal.json uses '/'.
            string normalised = script.Replace('\\', '/');
            bool any = scripts.Any(s => string.Equals(s.Replace('\\', '/'), normalised, StringComparison.OrdinalIgnoreCase));
            if (!any)
            {
                failures.Add(
                    $"pal.json verb '{verb}' does not reference {script} " +
                    $"(currently: {(scripts.Count == 0 ? "<none>" : string.Join(", ", scripts))}; " +
                    $"gates {queue})");
            }

            // pal.ps1 dispatch: match either single-quoted verb literal in
            // the switch, or as a switch case label.
            var dispatchPattern = new Regex(
                $@"'\s*{Regex.Escape(verb)}\s*'\s*\{{",
                RegexOptions.IgnoreCase);
            if (!dispatchPattern.IsMatch(palPs1))
            {
                failures.Add($"pal.ps1 dispatch table missing case '{verb}' (gates {queue})");
            }
        }

        Assert.That(failures, Is.Empty,
            "Live-operator turnkey scripts must each be exposed as a `pal` verb. " +
            "Failures:\n  - " + string.Join("\n  - ", failures));
    }

    [Test]
    public void Adr_EveryFile_HasStatusMetadata()
    {
        string adrDir = Path.Combine(RepoRoot, "docs", "adr");
        Assert.That(Directory.Exists(adrDir), "docs/adr/ directory missing");

        string[] adrFiles = Directory.GetFiles(adrDir, "*.md")
            .Where(f => Path.GetFileName(f) != "README.md")
            .OrderBy(f => f)
            .ToArray();

        Assert.That(adrFiles.Length, Is.GreaterThanOrEqualTo(6),
            "Expected at least 6 ADRs under docs/adr/");

        var statusRegex = new Regex(@"\*\*Status:\*\*\s+(\w+)");
        var validStatuses = new HashSet<string> { "Accepted", "Proposed", "Deprecated", "Superseded" };

        foreach (string file in adrFiles)
        {
            string content = File.ReadAllText(file);
            Match m = statusRegex.Match(content);
            Assert.That(m.Success, $"ADR {Path.GetFileName(file)} missing **Status:** metadata");
            Assert.That(validStatuses.Contains(m.Groups[1].Value),
                $"ADR {Path.GetFileName(file)} has invalid Status: {m.Groups[1].Value}");
        }
    }

    [Test]
    public void Schemas_EveryFile_IsValidJson_AndDeclaresIdAndDraft()
    {
        string schemaDir = Path.Combine(RepoRoot, "docs", "schemas");
        Assert.That(Directory.Exists(schemaDir), "docs/schemas/ directory missing");

        string[] schemaFiles = Directory.GetFiles(schemaDir, "*.schema.json");
        Assert.That(schemaFiles.Length, Is.GreaterThanOrEqualTo(3),
            "Expected at least 3 JSON Schemas under docs/schemas/");

        foreach (string file in schemaFiles)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;

                Assert.That(root.TryGetProperty("$schema", out JsonElement schemaUri),
                    $"{Path.GetFileName(file)} missing $schema declaration");
                Assert.That(schemaUri.GetString(), Does.Contain("json-schema.org"),
                    $"{Path.GetFileName(file)} $schema should reference json-schema.org");

                Assert.That(root.TryGetProperty("$id", out _),
                    $"{Path.GetFileName(file)} missing $id");
                Assert.That(root.TryGetProperty("title", out _),
                    $"{Path.GetFileName(file)} missing title");
            }
            catch (JsonException ex)
            {
                Assert.Fail($"Schema {Path.GetFileName(file)} is not valid JSON: {ex.Message}");
            }
        }
    }

    [Test]
    public void OutboundHttpHotPaths_StayStreamingAndPooled()
    {
        string inferenceDir = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Inference");
        string[] hotPathFiles =
        [
            "InferenceClient.cs",
            "VisionClient.cs",
            "TtsClient.cs",
            "ModelAvailabilityProbe.cs",
        ];

        foreach (string fileName in hotPathFiles)
        {
            string file = Path.Combine(inferenceDir, fileName);
            Assert.That(File.Exists(file), $"Hot-path inference source missing: {fileName}");

            string content = File.ReadAllText(file);
            Assert.That(content, Does.Contain("HttpCompletionOption.ResponseHeadersRead"),
                $"{fileName} should request headers-first streaming responses.");
            Assert.That(content, Does.Not.Contain("ReadAsStringAsync("),
                $"{fileName} should not buffer hot-path HTTP bodies with ReadAsStringAsync.");
            Assert.That(content, Does.Not.Contain("ReadAsByteArrayAsync("),
                $"{fileName} should not buffer hot-path HTTP bodies with ReadAsByteArrayAsync.");
        }

        string contentLimiterPath = Path.Combine(inferenceDir, "HttpContentReadLimiter.cs");
        string contentLimiterText = File.ReadAllText(contentLimiterPath);
        Assert.That(contentLimiterText, Does.Contain("ReadToEndAsync(cancellationToken)"),
            "HttpContentReadLimiter should keep bounded text-body decoding cancellation-aware on current .NET.");
        Assert.That(contentLimiterText, Does.Not.Contain("new MemoryStream("),
            "HttpContentReadLimiter should not double-buffer bounded text bodies through an intermediate MemoryStream.");

        string inferenceClientPath = Path.Combine(inferenceDir, "InferenceClient.cs");
        string inferenceClientContent = File.ReadAllText(inferenceClientPath);
        Assert.That(inferenceClientContent, Does.Contain("HttpContentReadLimiter.BuildExceededLimitMessage(ResponseLabel, maxResponseBytes)"),
            "InferenceClient should map oversized success bodies through the shared explicit cap-status helper.");
        Assert.That(inferenceClientContent, Does.Not.Contain("catch (InvalidDataException ex)"),
            "InferenceClient should not depend on InvalidDataException variable text for public oversized-body status.");
        Assert.That(inferenceClientContent, Does.Not.Contain("ex.Message"),
            "InferenceClient should not echo InvalidDataException text into public inference status messages.");

        string visionClientPath = Path.Combine(inferenceDir, "VisionClient.cs");
        string visionClientContent = File.ReadAllText(visionClientPath);
        Assert.That(visionClientContent, Does.Contain("HttpContentReadLimiter.BuildExceededLimitMessage(ResponseLabel, maxResponseBytes)"),
            "VisionClient should map oversized success bodies through the shared explicit cap-status helper.");
        Assert.That(visionClientContent, Does.Not.Contain("catch (InvalidDataException ex)"),
            "VisionClient should not depend on InvalidDataException variable text for public oversized-body status.");
        Assert.That(visionClientContent, Does.Not.Contain("ex.Message"),
            "VisionClient should not echo InvalidDataException text into public vision status messages.");

        string ttsClientPath = Path.Combine(inferenceDir, "TtsClient.cs");
        string ttsClientContent = File.ReadAllText(ttsClientPath);
        Assert.That(ttsClientContent, Does.Contain("HttpContentReadLimiter.ReadBytesAsync("),
            "TtsClient should route successful audio bodies through the shared bounded byte reader.");
        Assert.That(ttsClientContent, Does.Not.Contain("new MemoryStream("),
            "TtsClient should not grow successful audio bodies through a dedicated MemoryStream buffer.");
        Assert.That(ttsClientContent, Does.Not.Contain("TtsResult.Failed(ex.Message)"),
            "TtsClient should not echo raw InvalidDataException text into public TTS status messages.");

        string programPath = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar", "Program.cs");
        string programText = File.ReadAllText(programPath);
        Assert.That(programText, Does.Contain("AddRequestTimeouts"),
            "Program.cs should register ASP.NET Core request timeouts for bounded heavy-work HTTP lanes.");
        Assert.That(programText, Does.Contain("UseRequestTimeouts"),
            "Program.cs should enable the request-timeout middleware before endpoint execution.");
        Assert.That(programText, Does.Contain(".WithRequestTimeout(\"chat-timeout\")"),
            "Chat-class heavy HTTP lanes should use the named chat request-timeout policy.");
        Assert.That(programText, Does.Contain(".WithRequestTimeout(\"vision-timeout\")"),
            "Vision heavy HTTP lanes should use the named vision request-timeout policy.");
        Assert.That(programText, Does.Contain(".WithRequestTimeout(\"tts-timeout\")"),
            "TTS heavy HTTP lanes should use the named TTS request-timeout policy.");
        AssertHttpClientRegistrationUsesPooling(programText, "AddHttpClient<IModelAvailabilityProbe, HttpModelAvailabilityProbe>");
        AssertHttpClientRegistrationUsesPooling(programText, "AddHttpClient<IInferenceClient, HttpJsonInferenceClient>");
        AssertHttpClientRegistrationUsesPooling(programText, "AddHttpClient<IVisionClient, HttpVisionClient>");
        AssertHttpClientRegistrationUsesPooling(programText, "AddHttpClient<ITtsClient, HttpTtsClient>");
        AssertHttpClientRegistrationUsesPooling(programText, "AddHttpClient(PalLLM.Sidecar.Mcp.McpUpstreamClientPool.HttpClientName");
    }

    [Test]
    public void ProcessAndArtifactIngress_StayBoundedAndSanitized()
    {
        string thermalGatePath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Runtime", "ThermalGate.cs");
        string thermalGateContent = File.ReadAllText(thermalGatePath);

        Assert.That(thermalGateContent, Does.Contain("ProcessTextReadLimiter.ReadAsync"),
            "ThermalGate should drain redirected process output through the bounded helper.");
        Assert.That(thermalGateContent, Does.Not.Contain("ReadToEnd("),
            "ThermalGate should not buffer redirected process output with ReadToEnd().");

        string sidecarDir = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar");
        string[] artifactReaderFiles =
        [
            "ReleaseSmokeEvidenceBuilder.cs",
            "ReleaseNativeProofEvidenceBuilder.cs",
            "ReleaseProofBundleEvidenceBuilder.cs",
            "ReleaseSupportBundleEvidenceBuilder.cs",
            "ReleasePackageVerificationEvidenceBuilder.cs",
            "ReleaseArtifactIntegrityEvidenceBuilder.cs",
            "ReleaseFullAuditEvidenceBuilder.cs",
            "SelfHealingStatusReader.cs",
        ];

        foreach (string fileName in artifactReaderFiles)
        {
            string filePath = Path.Combine(sidecarDir, fileName);
            string content = File.ReadAllText(filePath);
            Assert.That(content, Does.Contain("ArtifactJsonFileReader"),
                $"{fileName} should use the bounded local-artifact JSON reader.");
            Assert.That(content, Does.Not.Contain("File.ReadAllText("),
                $"{fileName} should not buffer local artifact JSON with File.ReadAllText.");
            Assert.That(content, Does.Not.Contain("ex.Message"),
                $"{fileName} should not echo raw local file-read exceptions into public API payloads.");
        }

        string programPath = Path.Combine(sidecarDir, "Program.cs");
        string programContent = File.ReadAllText(programPath);
        Assert.That(programContent, Does.Contain("BoundedJsonFileReader.TryRead("),
            "The lifetime-relationships endpoint should read its persisted aggregate through the bounded local JSON reader.");
        Assert.That(programContent, Does.Not.Contain("LifetimeRelationshipAggregator.Deserialize(File.ReadAllText("),
            "The lifetime-relationships endpoint should not buffer latest.json with File.ReadAllText.");

        string runtimePath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Runtime", "PalLlmRuntime.cs");
        string runtimeContent = File.ReadAllText(runtimePath);
        Assert.That(runtimeContent, Does.Contain("TryReadBridgeEventEnvelope(file, _options.Bridge.MaxInboxEventBytes)"),
            "Bridge inbox drain should route filesystem ingress through the shared bounded JSON reader.");
        Assert.That(runtimeContent, Does.Not.Contain("using (FileStream stream = File.OpenRead(file))"),
            "Bridge inbox drain should not deserialize inbox events directly from raw File.OpenRead streams.");
        Assert.That(runtimeContent, Does.Contain("BoundedBase64FileReader.TryReadAsync("),
            "Screenshot ingest should read bridge screenshots through the bounded pooled base64 reader.");
        Assert.That(runtimeContent, Does.Not.Contain("File.ReadAllBytesAsync(file"),
            "Screenshot ingest should not allocate a fresh byte[] per screenshot with File.ReadAllBytesAsync.");
        Assert.That(runtimeContent, Does.Contain("foreach (string file in Directory.EnumerateFiles(_options.BridgeOutboxDir"),
            "ClearOutbox should enumerate outbox files lazily instead of pre-materializing the full list.");
        Assert.That(runtimeContent, Does.Not.Contain("string[] files = Directory.GetFiles(_options.BridgeOutboxDir"),
            "ClearOutbox should not pre-materialize outbox files with Directory.GetFiles.");
        Assert.That(runtimeContent, Does.Contain("TryReadUiProbeDump(file, _options.Http.LocalArtifactMaxBytes)"),
            "Ui-probe diagnostics should flow the configured local-artifact byte cap into the dump reader.");
        Assert.That(runtimeContent, Does.Contain("BoundedJsonFileReader.TryRead("),
            "Ui-probe diagnostics should read dump files through the bounded local JSON reader.");
        Assert.That(runtimeContent, Does.Not.Contain("string json = File.ReadAllText(file);"),
            "Ui-probe diagnostics should not buffer dump JSON into a string before parsing.");
        Assert.That(runtimeContent, Does.Contain("DescribeScreenshotProcessingFailure(ex)"),
            "Screenshot processing warnings should flow through a stable local helper instead of raw exception text.");
        Assert.That(runtimeContent, Does.Contain("DescribeOutboxEntryDeleteFailure(ex)"),
            "Outbox clear warnings should flow through a stable local helper instead of raw exception text.");
        Assert.That(runtimeContent, Does.Contain("DescribeOutboxEnumerationFailure(ex)"),
            "Outbox enumeration warnings should flow through a stable local helper instead of raw exception text.");
        Assert.That(runtimeContent, Does.Contain("DescribeOutboxWriteFailure(ex)"),
            "Outbox write warnings should flow through a stable local helper instead of raw exception text.");
        Assert.That(runtimeContent, Does.Contain("DescribeBridgeProcessingFailure(ex)"),
            "Bridge event processing warnings should flow through a stable local helper instead of raw exception text.");
        Assert.That(runtimeContent, Does.Not.Contain("processing errored: {ex.Message}"),
            "Screenshot processing warnings should not echo raw exception text.");
        Assert.That(runtimeContent, Does.Not.Contain("Failed to clear outbox entry {Path.GetFileName(file)}: {ex.Message}"),
            "Outbox clear warnings should not echo raw exception text.");
        Assert.That(runtimeContent, Does.Not.Contain("Failed to enumerate outbox entries for clearing: {ex.Message}"),
            "Outbox enumeration warnings should not echo raw exception text.");
        Assert.That(runtimeContent, Does.Not.Contain("Outbox write failed: {ex.Message}"),
            "Outbox write warnings should not echo raw exception text.");
        Assert.That(runtimeContent, Does.Not.Contain("Bridge event processing failed for {Path.GetFileName(file)}: {ex.Message}"),
            "Bridge event processing warnings should not echo raw exception text.");

        string hardwareProfilerPath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Inference", "HardwareProfiler.cs");
        string hardwareProfilerContent = File.ReadAllText(hardwareProfilerPath);
        Assert.That(hardwareProfilerContent, Does.Contain("BoundedTextFileReader.TryRead"),
            "HardwareProfiler should read the Linux GPU information probe through the bounded text-file reader.");
        Assert.That(hardwareProfilerContent, Does.Not.Contain("File.ReadAllText(infoPath)"),
            "HardwareProfiler should not buffer /proc GPU info through File.ReadAllText.");
        Assert.That(hardwareProfilerContent, Does.Contain("GlobalMemoryStatusEx"),
            "HardwareProfiler should use the OS-backed Windows physical-memory API before falling back to GC-visible memory.");
        Assert.That(hardwareProfilerContent, Does.Contain(@"SYSTEM\CurrentControlSet\Control\Video"),
            "HardwareProfiler should keep the no-subprocess Windows display-adapter registry probe.");

        string narrativePackServicePath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Packs", "NarrativePackService.cs");
        string narrativePackServiceContent = File.ReadAllText(narrativePackServicePath);
        Assert.That(narrativePackServiceContent, Does.Contain("BoundedJsonFileReader.TryRead("),
            "NarrativePackService should read on-disk pack JSON through the bounded local JSON reader.");
        Assert.That(narrativePackServiceContent, Does.Contain("Directory.EnumerateFiles("),
            "NarrativePackService should enumerate pack files lazily instead of pre-materializing the full recursive list.");
        Assert.That(narrativePackServiceContent, Does.Not.Contain("Directory.GetFiles("),
            "NarrativePackService should not pre-materialize recursive pack file lists with Directory.GetFiles.");
        Assert.That(narrativePackServiceContent, Does.Not.Contain("File.ReadAllText("),
            "NarrativePackService should not buffer narrative-pack JSON with File.ReadAllText.");
        Assert.That(narrativePackServiceContent, Does.Contain("BuildPackDisplayPath(pack.FilePath)"),
            "NarrativePackService should normalize loaded pack paths before exposing them publicly.");
        Assert.That(narrativePackServiceContent, Does.Not.Contain("FilePath = pack.FilePath"),
            "NarrativePackService should not expose absolute loaded pack paths through PackSummary.");

        string narrativePackValidatorPath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Packs", "NarrativePackValidator.cs");
        string narrativePackValidatorContent = File.ReadAllText(narrativePackValidatorPath);
        Assert.That(narrativePackValidatorContent, Does.Contain("DescribeParseFailure(ex)"),
            "NarrativePackValidator should route JSON parse failures through an explicit stable helper.");
        Assert.That(narrativePackValidatorContent, Does.Contain("near line {lineNumber + 1}, byte {bytePositionInLine + 1}."),
            "NarrativePackValidator should surface parse-location guidance without relying on serializer exception prose.");
        Assert.That(narrativePackValidatorContent, Does.Not.Contain("Pack JSON could not be parsed: {ex.Message}"),
            "NarrativePackValidator should not echo raw JsonException.Message text into public validation results.");
        Assert.That(narrativePackValidatorContent, Does.Contain("PackPublicationSafetyValidator.CollectNarrativeFindings(definition)"),
            "NarrativePackValidator should route shareable pack text through the publication-safety helper.");

        string publicationSafetyPath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Packs", "PackPublicationSafetyValidator.cs");
        string publicationSafetyContent = File.ReadAllText(publicationSafetyPath);
        Assert.That(publicationSafetyContent, Does.Contain("OfficialImpersonationRegex"),
            "Pack publication-safety validation should block obvious official endorsement claims.");
        Assert.That(publicationSafetyContent, Does.Contain("ThirdPartyIpRegex"),
            "Pack publication-safety validation should block unrelated third-party IP references.");
        Assert.That(publicationSafetyContent, Does.Contain("Pok(?:e|\\\\u00E9)mon")
            .And.Contain("Warhammer")
            .And.Contain("Mass")
            .And.Contain("League"),
            "Pack publication-safety validation should stay aligned with the release-facing public-copy franchise scanner.");
        Assert.That(publicationSafetyContent, Does.Contain("ThirdPartyRuntimeBrandRegex"),
            "Pack publication-safety validation should block model/runtime/vendor brand references.");
        Assert.That(publicationSafetyContent, Does.Contain("Qwen[0-9A-Za-z:._-]*")
            .And.Contain("Gemma[0-9A-Za-z:._-]*")
            .And.Contain("SGLang")
            .And.Contain("TensorRT"),
            "Pack publication-safety validation should keep pace with current local-model runtime brand leakage risks.");
        Assert.That(publicationSafetyContent, Does.Contain("BroadScopeRegex"),
            "Pack publication-safety validation should keep public packs scoped to PalLLM for Palworld.");
        Assert.That(publicationSafetyContent, Does.Contain("LegalOverclaimRegex")
            .And.Contain("lawyer[-\\\\s]?proof")
            .And.Contain("fully\\\\s+IP[-\\\\s]?neutral"),
            "Pack publication-safety validation should block legal/IP/compliance overclaims in shareable pack text.");

        string personalityPackPath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Packs", "PersonalityPack.cs");
        string personalityPackContent = File.ReadAllText(personalityPackPath);
        Assert.That(personalityPackContent, Does.Contain("BoundedJsonFileReader.TryRead("),
            "PersonalityPackValidator should read pack.json through the bounded local JSON reader.");
        Assert.That(personalityPackContent, Does.Contain("IncrementalHash.CreateHash"),
            "PersonalityPackValidator should stream content-hash computation instead of concatenating full file contents into memory.");
        Assert.That(personalityPackContent, Does.Not.Contain("File.ReadAllText(manifestPath)"),
            "PersonalityPackValidator should not buffer pack.json with File.ReadAllText.");
        Assert.That(personalityPackContent, Does.Not.Contain("File.ReadAllBytes(abs)"),
            "PersonalityPackValidator should not buffer tracked pack assets with File.ReadAllBytes.");
        Assert.That(personalityPackContent, Does.Not.Contain("new MemoryStream()"),
            "PersonalityPackValidator should not build a full in-memory concatenation buffer for content hashing.");
        Assert.That(personalityPackContent, Does.Contain("PackPublicationSafetyValidator.CollectPersonalityManifestFindings(manifest)"),
            "PersonalityPackValidator should scan manifest text through the shared publication-safety helper.");
        Assert.That(personalityPackContent, Does.Contain("PackPublicationSafetyValidator.CollectPersonalityPromptFindings(promptRead.Text)"),
            "PersonalityPackValidator should scan prompt text through the shared publication-safety helper.");

        string sessionPersistencePath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Runtime", "SessionPersistence.cs");
        string sessionPersistenceContent = File.ReadAllText(sessionPersistencePath);
        Assert.That(sessionPersistenceContent, Does.Contain("TryReadSessionFile(path, _options.Session.MaxPersistedBytes)"),
            "SessionPersistence should route startup session ingress through a bounded JSON reader.");
        Assert.That(sessionPersistenceContent, Does.Not.Contain("using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);"),
            "SessionPersistence should not deserialize session.json straight from an unbounded FileStream open.");
        Assert.That(sessionPersistenceContent, Does.Contain("DescribeSessionSaveFailure(ex)"),
            "SessionPersistence should map save failures through a stable public-status helper.");
        Assert.That(sessionPersistenceContent, Does.Contain("DescribeSessionLoadFailure(ex)"),
            "SessionPersistence should map load failures through a stable public-status helper.");
        Assert.That(sessionPersistenceContent, Does.Contain("FilePath = string.Empty"),
            "SessionPersistence failure results should blank FilePath instead of disclosing the local session path.");
        Assert.That(
            Regex.IsMatch(
                sessionPersistenceContent,
                @"Success\s*=\s*false\s*,[\s\S]{0,160}?FilePath\s*=\s*path"),
            Is.False,
            "SessionPersistence should not pair Success=false with a public FilePath that exposes the local session path.");
        Assert.That(sessionPersistenceContent, Does.Not.Contain("Session save failed: {ex.Message}"),
            "SessionPersistence should not echo raw save exception text into public session status.");
        Assert.That(sessionPersistenceContent, Does.Not.Contain("Session load failed: {ex.Message}"),
            "SessionPersistence should not echo raw load exception text into public session status.");

        string directoryRetentionPath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Runtime", "DirectoryRetention.cs");
        string directoryRetentionContent = File.ReadAllText(directoryRetentionPath);
        Assert.That(directoryRetentionContent, Does.Contain("directoryInfo.EnumerateFiles("),
            "DirectoryRetention should enumerate retention candidates lazily instead of pre-materializing the whole directory.");
        Assert.That(directoryRetentionContent, Does.Contain("PriorityQueue<FileInfo, long>"),
            "DirectoryRetention should keep only the newest retained candidates bounded by maxFiles.");
        Assert.That(directoryRetentionContent, Does.Not.Contain(".GetFiles("),
            "DirectoryRetention should not pre-materialize matching files with GetFiles.");
        Assert.That(directoryRetentionContent, Does.Not.Contain("Array.Sort("),
            "DirectoryRetention should not sort the full file list just to keep newest files.");

        string mcpToolsPath = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar", "Mcp", "PalLlmMcpTools.cs");
        string mcpToolsContent = File.ReadAllText(mcpToolsPath);
        Assert.That(mcpToolsContent, Does.Contain("PromotionLedger.TryNormalizeOutcome(outcome, out _)"),
            "pal_promotion_record should validate outcomes explicitly so MCP callers get stable rejection details.");
        Assert.That(mcpToolsContent, Does.Not.Contain("detail = ex.Message"),
            "MCP promotion-record rejections should not echo raw ArgumentException text.");
    }

    [Test]
    public void Docs_EveryLongFormDoc_HasLastAuditedStamp()
    {
        // Long-form docs in docs/ root carry Last audited stamps.
        // Excludes:
        //  - per-folder README.md files (orientation only, no stamp needed)
        //  - ADR files (use the **Date:** convention from the Nygard template)
        //  - the openapi/ directory (generated)
        string docsDir = Path.Combine(RepoRoot, "docs");
        string[] mdFiles = Directory.GetFiles(docsDir, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains("openapi"))
            .Where(f => !f.Contains(Path.Combine("docs", "adr")) && !f.Contains("docs/adr"))
            .Where(f => Path.GetFileName(f) != "README.md")
            .ToArray();

        // Accept "Last audited:" and "Last audited against the code:".
        var stampRegex = new Regex(@"Last audited(?:\s+against\s+the\s+code)?:\s+`\d{4}-\d{2}-\d{2}`");
        var missing = new List<string>();

        foreach (string file in mdFiles)
        {
            string head = File.ReadAllText(file);
            string slice = head.Length > 1000 ? head.Substring(0, 1000) : head;
            if (!stampRegex.IsMatch(slice))
            {
                missing.Add(Path.GetRelativePath(RepoRoot, file).Replace('\\', '/'));
            }
        }

        Assert.That(missing, Is.Empty,
            "Long-form docs missing 'Last audited:' stamp:\n  " + string.Join("\n  ", missing));
    }

    [Test]
    public void PublicationFacingDocs_DoNotReferenceSiblingProjects()
    {
        string[] publicationFiles =
        [
            "README.md",
            "docs/PITCH.md",
            "docs/FAQ.md",
            "docs/API.md",
            "docs/ARCHITECTURE.md",
            "docs/ROADMAP.md",
            "docs/RELEASE.md",
            "docs/PRIVACY.md",
        ];

        string[] blockedTerms =
        [
            "RimLLM",
            "OmniForge",
            "DeepForge",
            @"D:\Coding\Byte",
            "byte-forge",
            "byte-forward",
            "byte-synthesis",
            "byte-qwen-frontier",
            "byte-qwen-modernize",
            "byte-council",
        ];

        var offenses = new List<string>();

        foreach (string relativePath in publicationFiles)
        {
            string absolutePath = Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.That(File.Exists(absolutePath), $"Publication-facing doc missing: {relativePath}");

            string[] lines = File.ReadAllLines(absolutePath);
            for (int index = 0; index < lines.Length; index++)
            {
                foreach (string blockedTerm in blockedTerms)
                {
                    if (lines[index].Contains(blockedTerm, StringComparison.Ordinal))
                    {
                        offenses.Add($"{relativePath}:{index + 1}: found '{blockedTerm}'");
                    }
                }
            }
        }

        Assert.That(offenses, Is.Empty,
            "Publication-facing docs should stay PalLLM-specific and should not drift toward sibling-project naming:\n  " +
            string.Join("\n  ", offenses));
    }

    [Test]
    public void GithubWorkflows_UseFullLengthShaPinnedActions()
    {
        string workflowDir = Path.Combine(RepoRoot, ".github", "workflows");
        Assert.That(Directory.Exists(workflowDir), ".github/workflows directory missing.");

        string scriptPath = Path.Combine(RepoRoot, "scripts", "audit-workflow-action-pins.ps1");
        Assert.That(File.Exists(scriptPath), "Workflow action pin audit script missing.");
        string script = File.ReadAllText(scriptPath);
        Assert.That(script, Does.Contain("full-length commit SHAs"),
            "Workflow action pin audit script should explain the full-SHA policy.");
        Assert.That(script, Does.Contain("(?:-\\s*)?uses:"),
            "Workflow action pin audit script should catch both `uses:` and `- uses:` forms.");

        var usesRegex = new Regex(@"^\s*(?:-\s*)?uses:\s*(?<value>[^\s#]+)", RegexOptions.CultureInvariant);
        var shaRegex = new Regex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);
        string[] ignoredPrefixes = ["./", "docker://"];
        var issues = new List<string>();
        int checkedCount = 0;

        string[] workflowFiles = Directory.GetFiles(workflowDir, "*.yml")
            .Concat(Directory.GetFiles(workflowDir, "*.yaml"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string file in workflowFiles)
        {
            string relative = Path.GetRelativePath(RepoRoot, file);
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                Match match = usesRegex.Match(lines[index]);
                if (!match.Success) continue;

                string value = match.Groups["value"].Value.Trim();
                if (ignoredPrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                int atIndex = value.LastIndexOf('@');
                if (atIndex < 0)
                {
                    issues.Add($"{relative}:{index + 1}: missing @ref in {value}");
                    continue;
                }

                checkedCount++;
                string actionRef = value[(atIndex + 1)..];
                if (!shaRegex.IsMatch(actionRef))
                {
                    issues.Add($"{relative}:{index + 1}: unpinned action reference {value}");
                }
            }
        }

        Assert.That(checkedCount, Is.GreaterThan(0),
            "Expected at least one external GitHub Action reference to audit.");
        Assert.That(issues, Is.Empty,
            "External GitHub Actions should stay pinned to full-length commit SHAs:\n  " +
            string.Join("\n  ", issues));
    }

    [Test]
    public void ReleasePackageScript_BundlesDocsReferencedByPlayerReadme()
    {
        string packageScriptPath = Path.Combine(RepoRoot, "scripts", "package-release.ps1");
        Assert.That(File.Exists(packageScriptPath), "Release packaging script missing.");

        string content = File.ReadAllText(packageScriptPath);
        Assert.That(content, Does.Contain("if ([string]::IsNullOrWhiteSpace($OutputRoot))"),
            "package-release.ps1 should resolve its default OutputRoot after $PSScriptRoot is bound.");
        Assert.That(content, Does.Not.Contain("[string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot)"),
            "package-release.ps1 should not use $PSScriptRoot inside a param default; Windows PowerShell binds it too late.");

        Match readmeMatch = Regex.Match(
            content,
            "\\$playerReadme\\s*=\\s*@\"\\r?\\n(?<body>[\\s\\S]*?)\\r?\\n\"@",
            RegexOptions.CultureInvariant);
        Assert.That(readmeMatch.Success, "package-release.ps1 should define the generated PLAYER_README.txt body.");

        Match docsMatch = Regex.Match(
            content,
            "\\$packagedDocs\\s*=\\s*@\\((?<body>[\\s\\S]*?)\\)",
            RegexOptions.CultureInvariant);
        Assert.That(docsMatch.Success, "package-release.ps1 should define the bundled docs list in $packagedDocs.");

        string[] referencedDocs = Regex.Matches(
                readmeMatch.Groups["body"].Value,
                @"docs\\(?<name>[A-Z0-9_]+\.md)",
                RegexOptions.CultureInvariant)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.That(referencedDocs, Is.Not.Empty, "PLAYER_README.txt should name at least one packaged doc.");

        var packagedDocs = Regex.Matches(
                docsMatch.Groups["body"].Value,
                "\"(?<name>[A-Z0-9_]+\\.md)\"",
                RegexOptions.CultureInvariant)
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] missingDocs = referencedDocs
            .Where(doc => !packagedDocs.Contains(doc))
            .ToArray();

        Assert.That(missingDocs, Is.Empty,
            "PLAYER_README.txt references docs that package-release.ps1 does not bundle:\n  " +
            string.Join("\n  ", missingDocs));

        string readmeBody = readmeMatch.Groups["body"].Value;
        string[] rootCopyBlockedTerms =
        [
            "Claude Desktop",
            "ChatGPT",
            "Cursor",
            "VS Code",
            "Visual Studio Code",
            "Ollama",
            "LM Studio",
            "vLLM",
            "SGLang",
            "llama.cpp",
            "DashScope",
            "Qwen",
            "Gemma",
            "TensorRT",
            "Hugging Face",
            "OpenVINO",
            "Foundry Local",
            "DeepSeek",
            "OpenRouter",
        ];

        foreach (string blockedTerm in rootCopyBlockedTerms)
        {
            Assert.That(readmeBody, Does.Not.Contain(blockedTerm),
                $"Generated PLAYER_README.txt should use neutral protocol/provider wording instead of {blockedTerm}.");
        }

        Assert.That(content, Does.Contain("Write-PlayerChangelog"),
            "Release packaging should generate a player-facing package changelog.");
        Assert.That(content, Does.Not.Contain("\"CHANGELOG.md\", \"install.bat\""),
            "Release packaging should not copy the raw developer changelog into player packages.");

        string verifyScriptPath = Path.Combine(RepoRoot, "scripts", "verify-release-package.ps1");
        Assert.That(File.Exists(verifyScriptPath), "Release package verification script missing.");

        string verifyScript = File.ReadAllText(verifyScriptPath);
        Assert.That(verifyScript, Does.Contain("Test-PackagePublicationSurface"),
            "Release package verification should scan the expanded package's publication surface.");
        Assert.That(verifyScript, Does.Contain("Test-PalLlmPublicationTextSurface"),
            "Release package verification should use the shared publication text-surface scanner.");
        Assert.That(verifyScript, Does.Contain("PublicationScanViolations"),
            "Release package verification artifacts should report publication-scan violations explicitly.");

        string toolingPath = Path.Combine(RepoRoot, "scripts", "PalLLM.Tooling.ps1");
        Assert.That(File.Exists(toolingPath), "Shared PalLLM tooling script missing.");
        string tooling = File.ReadAllText(toolingPath);
        Assert.That(tooling, Does.Contain("Test-PalLlmPublicationTextSurface"),
            "Shared tooling should expose one reusable publication text-surface scanner.");
        Assert.That(tooling, Does.Contain("$officialImpersonationPattern"),
            "Publication text-surface scanning should block official endorsement, sponsorship, approval, authorization, or certification claims.");
        Assert.That(tooling, Does.Contain("$unrelatedFranchisePattern"),
            "Publication text-surface scanning should block unrelated third-party IP/franchise references in shipped text files.");
        Assert.That(tooling, Does.Contain("Pok(?:e|\\u00E9)mon"),
            "Publication text-surface scanning should catch both plain and accented spellings of the common off-scope comparison term.");
        Assert.That(tooling, Does.Contain("$scopeDriftPattern"),
            "Publication text-surface scanning should keep shipped text files scoped to PalLLM for Palworld.");
        Assert.That(tooling, Does.Contain("$legalOverclaimPattern"),
            "Publication text-surface scanning should block legal, IP-neutrality, or compliance-certainty overclaims.");
        Assert.That(tooling, Does.Contain("vLLM")
                .And.Contain("SGLang")
                .And.Contain("llama\\.cpp")
                .And.Contain("TensorRT")
                .And.Contain("OpenVINO")
                .And.Contain("Foundry\\s+Local")
                .And.Contain("DeepSeek"),
            "Publication text-surface scanning should block current model/runtime/vendor brands in root player-facing copy.");
        Assert.That(tooling, Does.Contain("Protect-PalLlmPortableTextSurface"),
            "Shared tooling should expose one reusable privacy redaction pass for portable proof/support bundles.");
        Assert.That(tooling, Does.Contain("$windowsUserPathPattern"),
            "Portable bundle privacy redaction should scrub Windows user-profile paths before archive sharing.");
        Assert.That(tooling, Does.Contain("$apiKeyFieldPattern"),
            "Portable bundle privacy redaction should scrub API-key-like fields before archive sharing.");

        string checksumScriptPath = Path.Combine(RepoRoot, "scripts", "compute-release-checksums.ps1");
        Assert.That(File.Exists(checksumScriptPath), "Release checksum script missing.");
        string checksumScript = File.ReadAllText(checksumScriptPath);
        Assert.That(checksumScript, Does.Contain("latest-artifact-integrity.json"),
            "Release checksum script should persist artifact-integrity evidence for /api/release/readiness.");
        Assert.That(checksumScript, Does.Contain("SHA256SUMS.minisig"),
            "Release checksum script should keep detached signature sidecars out of the digest manifest.");
        Assert.That(checksumScript, Does.Contain("DetachedSignaturePresent"),
            "Release checksum evidence should report whether detached signature files were present.");

        string proofBundleScriptPath = Path.Combine(RepoRoot, "scripts", "export-release-proof-bundle.ps1");
        Assert.That(File.Exists(proofBundleScriptPath), "Release proof-bundle export script missing.");
        string proofBundleScript = File.ReadAllText(proofBundleScriptPath);
        Assert.That(proofBundleScript, Does.Contain("Protect-PalLlmPortableTextSurface"),
            "Release proof-bundle export should redact staged portable text before writing archives.");
        Assert.That(proofBundleScript, Does.Contain("Test-PalLlmPublicationTextSurface"),
            "Release proof-bundle export should scan portable evidence text before writing the manifest.");
        Assert.That(proofBundleScript, Does.Contain("PrivacyRedactionApplied"),
            "Release proof-bundle manifests should report whether portable privacy redaction ran.");
        Assert.That(proofBundleScript, Does.Contain("PublicationScanViolations"),
            "Release proof-bundle manifests should report publication-scan violations explicitly.");

        string proofBundleBuilderPath = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar", "ReleaseProofBundleEvidenceBuilder.cs");
        Assert.That(File.Exists(proofBundleBuilderPath), "Release proof-bundle evidence builder missing.");
        string proofBundleBuilder = File.ReadAllText(proofBundleBuilderPath);
        Assert.That(proofBundleBuilder, Does.Contain("ReleaseBundleArchiveInspector.InspectProofBundle"),
            "Release-readiness should verify the paired proof-bundle zip before trusting the latest manifest.");

        string nativeProofScriptPath = Path.Combine(RepoRoot, "scripts", "run-native-proof.ps1");
        Assert.That(File.Exists(nativeProofScriptPath), "Native proof runner script missing.");
        string nativeProofScript = File.ReadAllText(nativeProofScriptPath);
        Assert.That(nativeProofScript, Does.Contain("SkipPalworldProcessCheck"),
            "Native proof runner should allow remote-sidecar runs to opt out of local Palworld process detection.");
        Assert.That(nativeProofScript, Does.Contain("Test-PalworldProcessCheckApplies"),
            "Native proof runner should only enforce process detection for local sidecar URLs.");
        Assert.That(nativeProofScript, Does.Contain("Test-PalworldProcessActive"),
            "Native proof runner should fail fast when a local proof is attempted without the Palworld process.");
        Assert.That(nativeProofScript, Does.Contain("AdditionalBlockers"),
            "Native proof runner should persist local preflight blockers into the native-proof artifact.");
        Assert.That(nativeProofScript, Does.Contain("StatusTransitions"),
            "Native proof runner should persist a bounded status-transition trail for failed or successful live proof runs.");
        Assert.That(nativeProofScript, Does.Contain("WatcherCompletionReason"),
            "Native proof runner should name why the active watcher stopped so release evidence is replayable.");

        string supportBundleScriptPath = Path.Combine(RepoRoot, "scripts", "export-support-bundle.ps1");
        Assert.That(File.Exists(supportBundleScriptPath), "Support-bundle export script missing.");
        string supportBundleScript = File.ReadAllText(supportBundleScriptPath);
        Assert.That(supportBundleScript, Does.Contain("Protect-PalLlmPortableTextSurface"),
            "Support-bundle export should redact staged portable text before writing archives.");
        Assert.That(supportBundleScript, Does.Contain("Test-PalLlmPublicationTextSurface"),
            "Support-bundle export should scan portable evidence text before writing the manifest.");
        Assert.That(supportBundleScript, Does.Contain("PrivacyRedactionApplied"),
            "Support-bundle manifests should report whether portable privacy redaction ran.");
        Assert.That(supportBundleScript, Does.Contain("PublicationScanViolations"),
            "Support-bundle manifests should report publication-scan violations explicitly.");

        string supportBundleBuilderPath = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar", "ReleaseSupportBundleEvidenceBuilder.cs");
        Assert.That(File.Exists(supportBundleBuilderPath), "Release support-bundle evidence builder missing.");
        string supportBundleBuilder = File.ReadAllText(supportBundleBuilderPath);
        Assert.That(supportBundleBuilder, Does.Contain("ReleaseBundleArchiveInspector.InspectSupportBundle"),
            "Release-readiness should verify the paired support-bundle zip before trusting the latest manifest.");

        string bundleArchiveInspectorPath = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar", "ReleaseBundleArchiveInspector.cs");
        Assert.That(File.Exists(bundleArchiveInspectorPath), "Portable bundle archive inspector missing.");
        string bundleArchiveInspector = File.ReadAllText(bundleArchiveInspectorPath);
        Assert.That(bundleArchiveInspector, Does.Contain("ZipFile.OpenRead"),
            "Portable bundle archive inspection should read the zip central directory without extracting files.");
        Assert.That(bundleArchiveInspector, Does.Not.Contain("ExtractToDirectory"),
            "Portable bundle archive inspection should not extract tester/proof archives to disk.");
        Assert.That(bundleArchiveInspector, Does.Contain("IsSafeArchiveEntryName"),
            "Portable bundle archive inspection should reject absolute, drive-qualified, traversal, or duplicate entry names before trusting archives.");

        string publicCopyPolicyPath = Path.Combine(RepoRoot, "scripts", "public_copy_policy.ps1");
        Assert.That(File.Exists(publicCopyPolicyPath), "Public copy policy script missing.");
        string publicCopyPolicy = File.ReadAllText(publicCopyPolicyPath);
        Assert.That(publicCopyPolicy, Does.Contain("BlockedPublicScopePatterns"),
            "Public copy audit should guard release-facing repo docs against broader-platform scope drift.");
        Assert.That(publicCopyPolicy, Does.Contain("BlockedPublicFranchisePatterns"),
            "Public copy audit should guard release-facing repo docs against unrelated third-party franchise references.");
        Assert.That(publicCopyPolicy, Does.Contain("Pok(?:e|\\u00E9)mon"),
            "Public copy audit should catch both plain and accented spellings of the common off-scope comparison term.");
        Assert.That(publicCopyPolicy, Does.Contain("BlockedSiblingProjectPatterns"),
            "Public copy audit should guard release-facing repo docs against sibling-project bleed.");
        Assert.That(publicCopyPolicy, Does.Contain("BlockedPublicLegalOverclaimPatterns"),
            "Public copy audit should guard release-facing repo docs against legal/IP/compliance overclaims.");
        Assert.That(publicCopyPolicy, Does.Contain("DeepForge"),
            "Public copy audit should block known sibling-project names in publication-facing surfaces.");
        Assert.That(publicCopyPolicy, Does.Contain("byte-qwen-frontier"),
            "Public copy audit should block imported sibling prompt-pack identities in publication-facing surfaces.");
        Assert.That(publicCopyPolicy, Does.Contain("vLLM")
                .And.Contain("SGLang")
                .And.Contain("llama\\.cpp")
                .And.Contain("TensorRT")
                .And.Contain("OpenVINO")
                .And.Contain("Foundry\\s+Local")
                .And.Contain("DeepSeek"),
            "Public copy audit should block current model/runtime/vendor brands in publication-facing surfaces.");

        string publishAuditPath = Path.Combine(RepoRoot, "scripts", "publish-audit.ps1");
        Assert.That(File.Exists(publishAuditPath), "Focused publication audit script missing.");
        string publishAudit = File.ReadAllText(publishAuditPath);
        Assert.That(publishAudit, Does.Contain("audit_public_copy.ps1"),
            "Publish audit should compose the release-facing public-copy audit.");
        Assert.That(publishAudit, Does.Contain("path_reference_audit.ps1"),
            "Publish audit should compose the local path-reference audit.");
        Assert.That(publishAudit, Does.Contain("audit-workflow-action-pins.ps1"),
            "Publish audit should compose the full-SHA GitHub Actions pin audit.");
        Assert.That(publishAudit, Does.Contain("THIRD_PARTY_NOTICES.md"),
            "Publish audit should verify package-reference notice coverage.");
        Assert.That(publishAudit, Does.Contain("<PackageReference"),
            "Publish audit should derive notice coverage from current csproj PackageReference entries.");
        Assert.That(publishAudit, Does.Contain("artifacts\\publish-audit"),
            "Publish audit should persist timestamped artifacts for release review.");

        string contractsPath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Integration", "Contracts.cs");
        string contracts = File.ReadAllText(contractsPath);
        Assert.That(contracts, Does.Contain("PublicationScanPassed"),
            "Release readiness contracts should expose whether package and portable-bundle publication scanning passed.");
        Assert.That(contracts, Does.Contain("PublicationScanViolations"),
            "Release readiness contracts should expose package and portable-bundle publication-scan violations.");
        Assert.That(contracts, Does.Contain("PrivacyRedactionApplied"),
            "Release readiness contracts should expose whether portable proof/support bundles were privacy-redacted.");
        Assert.That(contracts, Does.Contain("ReleaseArtifactIntegrityEvidenceSnapshot"),
            "Release readiness contracts should expose checksum/signature evidence for release artifacts.");
    }

    [Test]
    public void Adr_NumbersAreSequential_StartingAtZeroOneOne()
    {
        string adrDir = Path.Combine(RepoRoot, "docs", "adr");
        var adrFiles = Directory.GetFiles(adrDir, "*.md")
            .Select(Path.GetFileName)
            .Where(name => name != "README.md")
            .OrderBy(name => name)
            .ToList();

        var numberRegex = new Regex(@"^(\d{4})-");
        for (int i = 0; i < adrFiles.Count; i++)
        {
            string name = adrFiles[i]!;
            Match m = numberRegex.Match(name);
            Assert.That(m.Success, $"ADR file {name} doesn't start with NNNN-");
            int parsed = int.Parse(m.Groups[1].Value);
            Assert.That(parsed, Is.EqualTo(i + 1),
                $"ADR numbering gap: expected {i + 1:D4} but found {parsed:D4} ({name})");
        }
    }

    [Test]
    public void ReadingOrder_FilesNamedInAgentsMd_ExistOnDisk()
    {
        string agentsPath = Path.Combine(RepoRoot, "AGENTS.md");
        Assert.That(File.Exists(agentsPath), "AGENTS.md missing");

        string content = File.ReadAllText(agentsPath);
        // Pull out backticked references that look like repo-relative
        // paths (must contain a slash, so bare filenames like "Program.cs"
        // mentioned by name aren't treated as path references).
        var pathRegex = new Regex(@"`([A-Za-z0-9_./-]+/[A-Za-z0-9_./-]+\.(?:md|cs|ps1|json|toml|yml|txt))`");
        var matches = pathRegex.Matches(content);

        var unresolved = new List<string>();
        foreach (Match m in matches)
        {
            string rel = m.Groups[1].Value;
            if (rel.StartsWith("/") || rel.Contains(":")) continue;
            string abs = Path.GetFullPath(Path.Combine(RepoRoot, rel));
            if (!File.Exists(abs) && !Directory.Exists(abs))
            {
                unresolved.Add(rel);
            }
        }

        Assert.That(unresolved, Is.Empty,
            "AGENTS.md references missing repo-relative paths:\n  " + string.Join("\n  ", unresolved));
    }

    [Test]
    public void IndexDoc_CataloguesEveryDocInTheDocsDirectory()
    {
        // Pass 297 - permanent regression guard. The user's directive "any
        // coding agent, even small models should be able to understand and
        // pickup and replicate entire project just from documentation"
        // depends on `docs/INDEX.md` listing every doc that exists under
        // `docs/`. An uncatalogued doc is effectively invisible to a small
        // model that started at INDEX. Pass 296 fixed a one-off catalogue
        // gap (RESEARCH_NOTES_2026-05.md was missing); this meta-test makes
        // sure no future doc lands without an INDEX entry.
        //
        // The check is bi-directional. The forward check ensures every
        // `docs/*.md` is referenced by INDEX (catalogue completeness). The
        // reverse check parses INDEX markdown links `[text](target)` and
        // verifies that every link whose target is a docs-leaf (no slash,
        // no leading `..`) resolves to a real file under `docs/`.
        //
        // INDEX.md is excluded from the forward check (it cannot reference
        // itself). References that use a path prefix (`../CONTRIBUTING.md`,
        // `schemas/agents.schema.json`, `adr/0001-...`) intentionally point
        // outside `docs/` and are NOT validated by this gate — the existing
        // `Drift_Dangling_markdown_links` audit step handles cross-tree
        // path resolution.

        string docsDir = Path.Combine(RepoRoot, "docs");
        string indexPath = Path.Combine(docsDir, "INDEX.md");
        Assert.That(File.Exists(indexPath), "docs/INDEX.md missing");

        // INDEX.md and README.md are excluded by design: INDEX cannot
        // reference itself, and README.md is the docs-folder readme (a
        // pointer to INDEX.md) rather than a catalogued content doc.
        var folderMetaDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INDEX.md",
            "README.md",
        };

        string[] docsOnDisk = Directory.GetFiles(docsDir, "*.md")
            .Select(Path.GetFileName)
            .Where(name => name is not null && !folderMetaDocs.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;

        string indexContent = File.ReadAllText(indexPath);
        // Parse markdown links `[text](target)` and keep only targets that
        // are docs-folder leaves (no slash, no `..`, ends in `.md`). Strip
        // any `#fragment` suffix before comparison.
        var linkRegex = new Regex(@"\[[^\]]+\]\(([^)\s#]+)(#[^)]*)?\)");
        HashSet<string> indexedLeaves = linkRegex.Matches(indexContent)
            .Select(m => m.Groups[1].Value)
            .Where(target =>
                target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                !target.Contains('/') &&
                !target.Contains('\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = docsOnDisk
            .Where(name => !indexedLeaves.Contains(name!))
            .ToArray();

        Assert.That(missing, Is.Empty,
            "docs/INDEX.md must catalogue every doc under docs/. Missing entries:\n  "
            + string.Join("\n  ", missing));

        var unresolved = indexedLeaves
            .Where(leaf => !File.Exists(Path.Combine(docsDir, leaf)))
            .OrderBy(leaf => leaf, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.That(unresolved, Is.Empty,
            "docs/INDEX.md references docs that no longer exist:\n  "
            + string.Join("\n  ", unresolved));
    }

    [Test]
    public void SecondaryTestCountMirrors_AgreeWithProjectNumbers()
    {
        // Pass 305 - drift guard for "secondary mirrors" of the test count.
        // The existing Drift_Test_count_docs gate only checks 5 anchors
        // (README / ROADMAP / ARCHITECTURE / CODE_MAP / HANDOFF). Every
        // other place that summarises the count (agents.json, pal.json,
        // pal.ps1, CLAUDE.md, CONTRIBUTING.md, .cursorrules,
        // .github/copilot-instructions.md, scripts/onboard.ps1,
        // PalLlmRuntime.cs gate-pin comment) can drift silently because
        // none of them are gate-checked.
        //
        // Empirically observed in this repo's history: every test-count
        // bump leaves 5-10 of these secondary mirrors stale until a
        // human notices. This meta-test pins all of them to
        // `PROJECT_NUMBERS.tests` so the next test addition either:
        //   1. Updates all anchors in lockstep (intentional), or
        //   2. Lists exactly which files need updating (precise error).
        //
        // The check is intentionally narrow: it only verifies the
        // *current-state* test count, not historical pass-entry counts
        // inside CHANGELOG/HANDOFF (those legitimately freeze older
        // numbers).

        string projectNumbersPath = Path.Combine(RepoRoot, "docs", "PROJECT_NUMBERS.json");
        using JsonDocument projectNumbers = JsonDocument.Parse(File.ReadAllText(projectNumbersPath));
        int liveTestCount = projectNumbers.RootElement.GetProperty("tests").GetInt32();
        string expected = liveTestCount.ToString();

        // (relativePath, regex-of-line-pattern, description)
        var mirrors = new (string Path, string Pattern, string Description)[]
        {
            ("agents.json",
                @"""tests"":\s*(\d+),",
                "agents.json -> rollingState.tests"),
            ("agents.json",
                @"PalLLM\.sln \((\d+) expected\)",
                "agents.json -> validationGates Tests claim"),
            ("pal.json",
                @"expects (\d+) / \d+",
                "pal.json -> 'test' verb summary"),
            ("pal.ps1",
                @"expects (\d+) / \d+",
                "pal.ps1 -> 'test' verb description"),
            ("CONTRIBUTING.md",
                @"currently `(\d+) passed`",
                "CONTRIBUTING.md -> pre-flight checklist"),
            ("CLAUDE.md",
                @"\*\*Test count:\*\* `(\d+)`",
                "CLAUDE.md -> TL;DR"),
            (".cursorrules",
                @"- (\d+) tests, \d+ / \d+",
                ".cursorrules -> Project facts"),
            (".cursorrules",
                @"#\s*(\d+) / \d+ expected",
                ".cursorrules -> run loop"),
            (".github/copilot-instructions.md",
                @"Test count: `(\d+)`",
                ".github/copilot-instructions.md -> TL;DR"),
            ("scripts/onboard.ps1",
                @"confirms (\d+) / \d+ tests pass",
                "scripts/onboard.ps1 -> banner"),
            ("scripts/onboard.ps1",
                @"Release\) - (\d+) expected",
                "scripts/onboard.ps1 -> Write-Step"),
            ("src/PalLLM.Domain/Runtime/PalLlmRuntime.cs",
                @"Drift_Test_count_docs \((\d+) expected\)",
                "PalLlmRuntime.cs -> gate-pin header comment"),
            ("tests/README.md",
                @"\*\*(\d+) tests\*\*",
                "tests/README.md -> opening line"),
        };

        var failures = new List<string>();
        foreach (var (relativePath, pattern, description) in mirrors)
        {
            string absolutePath = Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
            {
                failures.Add($"{description}: file missing at {relativePath}");
                continue;
            }

            string content = File.ReadAllText(absolutePath);
            Match match = Regex.Match(content, pattern);
            if (!match.Success)
            {
                failures.Add($"{description}: anchor pattern not found in {relativePath} (regex: {pattern})");
                continue;
            }

            string found = match.Groups[1].Value;
            if (!string.Equals(found, expected, StringComparison.Ordinal))
            {
                failures.Add($"{description}: found `{found}`, PROJECT_NUMBERS says `{expected}` ({relativePath})");
            }
        }

        Assert.That(failures, Is.Empty,
            "Secondary test-count mirrors must agree with PROJECT_NUMBERS.tests "
            + $"({expected}). Mismatches:\n  - "
            + string.Join("\n  - ", failures));
    }

    [Test]
    public void DirectoryBuildProps_Exists_AndSuppressesCs1591()
    {
        string path = Path.Combine(RepoRoot, "Directory.Build.props");
        Assert.That(File.Exists(path), "Directory.Build.props missing — repo-wide build settings should be centralized");

        string content = File.ReadAllText(path);
        Assert.That(content, Does.Contain("CS1591"),
            "Directory.Build.props should reference CS1591 (the documentation-warning suppression).");
    }

    [Test]
    public void ProjectHotPaths_UseSourceGeneratedConfigurationAndJsonMetadata()
    {
        string path = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar", "PalLLM.Sidecar.csproj");
        Assert.That(File.Exists(path), "PalLLM.Sidecar.csproj missing.");

        string content = File.ReadAllText(path);
        Assert.That(content, Does.Contain("<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>"),
            "The sidecar should bind the large PalLlmOptions tree through the .NET configuration binding source generator.");

        string domainProjectPath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "PalLLM.Domain.csproj");
        Assert.That(File.Exists(domainProjectPath), "PalLLM.Domain.csproj missing.");
        string domainProject = File.ReadAllText(domainProjectPath);
        Assert.That(domainProject, Does.Contain("<IsAotCompatible>true</IsAotCompatible>"),
            "The portable domain runtime should keep trim/single-file/AOT analyzers enabled.");

        string aotReadinessScriptPath = Path.Combine(RepoRoot, "scripts", "aot-readiness.ps1");
        Assert.That(File.Exists(aotReadinessScriptPath), "AOT readiness script missing.");
        string aotReadinessScript = File.ReadAllText(aotReadinessScriptPath);
        Assert.That(aotReadinessScript, Does.Contain("EnableConfigurationBindingGenerator")
                .And.Contain("domain-aot-analyzers")
                .And.Contain("PalLlmDomainJsonSerializerContext.cs")
                .And.Contain("PalLlmJsonSerializerContext.cs")
                .And.Contain("-p:PublishAot=true")
                .And.Contain("ModelContextProtocol.AspNetCore"),
            "The AOT readiness script should pin the source-generated config/JSON checks and optional native publish probe.");

        string palRunnerPath = Path.Combine(RepoRoot, "pal.ps1");
        string palRunner = File.ReadAllText(palRunnerPath);
        Assert.That(palRunner, Does.Contain("aot-readiness").And.Contain("scripts/aot-readiness.ps1"),
            "pal.ps1 should expose the AOT readiness scan as a first-class verb.");
        Assert.That(palRunner, Does.Contain("'health'").And.Contain("scripts/pal-health.ps1"),
            "pal.ps1 should expose the health snapshot as a first-class verb.");
        Assert.That(palRunner, Does.Contain("'proof'").And.Contain("scripts/pal-proof.ps1"),
            "pal.ps1 should expose the native proof status as a first-class verb.");
        Assert.That(palRunner, Does.Contain("'serving'").And.Contain("scripts/pal-model-serving.ps1"),
            "pal.ps1 should expose the model serving profile checklist under pal models serving.");
        Assert.That(palRunner, Does.Contain("'probe'").And.Contain("scripts/pal-model-probe.ps1"),
            "pal.ps1 should expose the model endpoint evidence probe under pal models probe.");
        Assert.That(palRunner, Does.Contain("'transformers'").And.Contain("scripts/connect-transformers.ps1"),
            "pal.ps1 should expose the transformers serve connection helper under pal connect transformers.");
        Assert.That(palRunner, Does.Contain("'lmstudio'").And.Contain("scripts/connect-lmstudio.ps1"),
            "pal.ps1 should expose the LM Studio connection helper under pal connect lmstudio.");
        Assert.That(palRunner, Does.Contain("'llamacpp'").And.Contain("scripts/connect-llamacpp.ps1"),
            "pal.ps1 should expose the llama.cpp connection helper under pal connect llamacpp.");
        Assert.That(palRunner, Does.Contain("'openvino'").And.Contain("scripts/connect-openvino.ps1"),
            "pal.ps1 should expose the OpenVINO Model Server connection helper under pal connect openvino.");

        string healthScriptPath = Path.Combine(RepoRoot, "scripts", "pal-health.ps1");
        Assert.That(File.Exists(healthScriptPath), "Health snapshot script missing.");
        string healthScript = File.ReadAllText(healthScriptPath);
        Assert.That(healthScript,
            Does.Contain("artifacts/health-snapshot")
                .And.Contain("PROJECT_NUMBERS.json")
                .And.Contain("/api/release/readiness")
                .And.Contain("/api/bridge/proof")
                .And.Contain("run-native-proof.ps1"),
            "Health snapshot should compose local counts, release/bridge posture, and the live native-proof next action.");

        string proofScriptPath = Path.Combine(RepoRoot, "scripts", "pal-proof.ps1");
        Assert.That(File.Exists(proofScriptPath), "Native proof status script missing.");
        string proofScript = File.ReadAllText(proofScriptPath);
        Assert.That(proofScript,
            Does.Contain("/api/bridge/proof")
                .And.Contain("latest-native-proof.json")
                .And.Contain("RequireProven")
                .And.Contain("run-native-proof.ps1")
                .And.Contain("native-proof-status-v1.schema.json")
                .And.Contain("STALE PROOF")
                .And.Contain("EvidenceFreshnessStatus")
                .And.Contain("DiagnosisCode")
                .And.Contain("DiagnosisAction")
                .And.Contain("DiagnosisCommand")
                .And.Contain("-Name 'HudBindReady'"),
            "Native proof status should compose live bridge proof, durable proof artifacts, freshness-gated release evidence, diagnosis remediation, the active watcher next action, and the schema-backed live HUD-bind field.");

        string proofStatusSchemaPath = Path.Combine(RepoRoot, "docs", "schemas", "native-proof-status-v1.schema.json");
        Assert.That(File.Exists(proofStatusSchemaPath), "Native proof status schema missing.");
        string proofStatusSchema = File.ReadAllText(proofStatusSchemaPath);
        Assert.That(proofStatusSchema,
            Does.Contain("DiagnosisAction").And.Contain("DiagnosisCommand"),
            "Native proof status schema must expose diagnosis remediation fields for automation consumers.");

        string modelServingScriptPath = Path.Combine(RepoRoot, "scripts", "pal-model-serving.ps1");
        Assert.That(File.Exists(modelServingScriptPath), "Model serving profile script missing.");
        string modelServingScript = File.ReadAllText(modelServingScriptPath);
        Assert.That(modelServingScript,
            Does.Contain("/api/inference/collaboration")
                .And.Contain("Capability.ServingProfile")
                .And.Contain("AdmissionControls")
                .And.Contain("SecurityControls")
                .And.Contain("VerificationChecks")
                .And.Contain("pal connect llamacpp")
                .And.Contain("pal connect omni"),
            "Model serving profile script should project live per-lane runtime policy without rewriting config.");

        string modelProbeScriptPath = Path.Combine(RepoRoot, "scripts", "pal-model-probe.ps1");
        Assert.That(File.Exists(modelProbeScriptPath), "Model endpoint probe script missing.");
        string modelProbeScript = File.ReadAllText(modelProbeScriptPath);
        Assert.That(modelProbeScript,
            Does.Contain("/v1/models")
                .And.Contain("/metrics")
                .And.Contain("vllm:prefix_cache_queries")
                .And.Contain("vllm:kv_cache_usage_perc")
                .And.Contain("vllm:spec_decode")
                .And.Contain("No chat, image, audio, tool-call, or player payload content was sent or stored."),
            "pal models probe should produce no-prompt model endpoint evidence for cache, KV, and speculative decoding metrics.");

        string connectTransformersScriptPath = Path.Combine(RepoRoot, "scripts", "connect-transformers.ps1");
        Assert.That(File.Exists(connectTransformersScriptPath), "transformers serve connection wizard missing.");
        string connectTransformersScript = File.ReadAllText(connectTransformersScriptPath);
        Assert.That(connectTransformersScript,
            Does.Contain("transformers[serving]")
                .And.Contain("--continuous-batching")
                .And.Contain("transformers serve $modelRef")
                .And.Contain("/load_model")
                .And.Contain("Revision")
                .And.Contain("WireVision")
                .And.Contain("DryRun = $DryRun.IsPresent"),
            "pal connect transformers should keep Hugging Face serving local, revision-aware, continuous-batching-capable, and dry-run safe.");

        string connectLmStudioScriptPath = Path.Combine(RepoRoot, "scripts", "connect-lmstudio.ps1");
        Assert.That(File.Exists(connectLmStudioScriptPath), "LM Studio connection wizard missing.");
        string connectLmStudioScript = File.ReadAllText(connectLmStudioScriptPath);
        Assert.That(connectLmStudioScript,
            Does.Contain("lms server start")
                .And.Contain("/v1/models")
                .And.Contain("ResidencyProvider")
                .And.Contain("LmStudio")
                .And.Contain("ttl")
                .And.Contain("DryRun = $DryRun.IsPresent"),
            "pal connect lmstudio should keep the desktop server local, model-id explicit, TTL-aware, and dry-run safe.");

        string connectLlamaCppScriptPath = Path.Combine(RepoRoot, "scripts", "connect-llamacpp.ps1");
        Assert.That(File.Exists(connectLlamaCppScriptPath), "llama.cpp connection wizard missing.");
        string connectLlamaCppScript = File.ReadAllText(connectLlamaCppScriptPath);
        Assert.That(connectLlamaCppScript,
            Does.Contain("llama-server")
                .And.Contain("/health")
                .And.Contain("/v1/models")
                .And.Contain("/metrics")
                .And.Contain("--cache-reuse")
                .And.Contain("-ctk")
                .And.Contain("--sleep-idle-seconds")
                .And.Contain("ResidencyProvider")
                .And.Contain("Disabled")
                .And.Contain("DryRun = $DryRun.IsPresent"),
            "pal connect llamacpp should keep raw llama-server lanes local, metrics-visible, residency-neutral, and dry-run safe.");

        string connectOpenVinoScriptPath = Path.Combine(RepoRoot, "scripts", "connect-openvino.ps1");
        Assert.That(File.Exists(connectOpenVinoScriptPath), "OpenVINO Model Server connection wizard missing.");
        string connectOpenVinoScript = File.ReadAllText(connectOpenVinoScriptPath);
        Assert.That(connectOpenVinoScript,
            Does.Contain("/v3/chat/completions")
                .And.Contain("/v3/models")
                .And.Contain("--target_device")
                .And.Contain("OpenVINO/Qwen3-8B-int4-ov")
                .And.Contain("DryRun = $DryRun.IsPresent")
                .And.Contain("WireVision"),
            "pal connect openvino should keep OpenVINO Model Server local, /v3-aware, target-device explicit, and dry-run safe.");

        string connectVllmScriptPath = Path.Combine(RepoRoot, "scripts", "connect-vllm.ps1");
        Assert.That(File.Exists(connectVllmScriptPath), "vLLM connection wizard missing.");
        string connectVllmScript = File.ReadAllText(connectVllmScriptPath);
        Assert.That(connectVllmScript,
            Does.Contain("--kv-cache-dtype $kvCacheDtype")
                .And.Contain("--performance-mode $performanceMode")
                .And.Contain("'interactivity'")
                .And.Contain("--prefix-caching-hash-algo sha256_cbor")
                .And.Contain("--enable-chunked-prefill")
                .And.Contain("DryRun = $DryRun.IsPresent")
                .And.Not.Contain("--calculate-kv-scales"),
            "pal connect vllm should emit current proof-gated cache and low-latency flags, report dry runs accurately, and avoid deprecated dynamic KV-scale calculation.");

        string connectOmniScriptPath = Path.Combine(RepoRoot, "scripts", "connect-vllm-omni.ps1");
        Assert.That(File.Exists(connectOmniScriptPath), "vLLM-Omni connection wizard missing.");
        string connectOmniScript = File.ReadAllText(connectOmniScriptPath);
        Assert.That(connectOmniScript,
            Does.Contain("WireInference")
                .And.Contain("same-endpoint text+media proof")
                .And.Contain("$pal['Vision']['BaseUrl'] = $baseUrl")
                .And.Contain("if ($WireInference.IsPresent)")
                .And.Contain("$pal['Inference']['BaseUrl'] = $baseUrl"),
            "pal connect omni should wire the media lane by default and require explicit proof-lane intent before replacing text inference.");

        string domainJsonContextPath = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "PalLlmDomainJsonSerializerContext.cs");
        string domainJsonContext = File.ReadAllText(domainJsonContextPath);
        Assert.That(domainJsonContext, Does.Not.Contain("DefaultJsonTypeInfoResolver"),
            "The domain JSON context should not reintroduce reflection fallback when used for trim/AOT probes.");

        string sidecarJsonContextPath = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar", "PalLlmJsonSerializerContext.cs");
        string sidecarJsonContext = File.ReadAllText(sidecarJsonContextPath);
        Assert.That(sidecarJsonContext,
            Does.Contain("Dictionary<string, HealthReportDataValue>")
                .And.Contain("ChatStreamStartedPayload")
                .And.Contain("McpStatusPayload")
                .And.Not.Contain("Dictionary<string, object?> Data"),
            "Common sidecar-only health/SSE/MCP payloads should be named source-generated shapes instead of object dictionaries.");

        string sidecarDir = Path.Combine(RepoRoot, "src", "PalLLM.Sidecar");
        string chatStreamWriter = File.ReadAllText(Path.Combine(sidecarDir, "ChatStreamWriter.cs"));
        Assert.That(chatStreamWriter,
            Does.Contain("JsonTypeInfo<T>")
                .And.Not.Contain("object payload"),
            "The SSE writer should require source-generated metadata for each progress frame payload.");

        string programText = File.ReadAllText(Path.Combine(sidecarDir, "Program.cs"));
        Assert.That(programText,
            Does.Contain("ClearOutboxResponse")
                .And.Contain("ChatStreamStartedPayload")
                .And.Contain("ChatStreamFinalPrepPayload")
                .And.Not.Contain("new { removed =")
                .And.Not.Contain("new { request_id =")
                .And.Not.Contain("new { name ="),
            "Bridge clear and chat-stream endpoints should avoid anonymous response payloads.");

        string selfHealingStatusReader = File.ReadAllText(Path.Combine(sidecarDir, "SelfHealingStatusReader.cs"));
        Assert.That(selfHealingStatusReader,
            Does.Contain("SelfHealingStatusMarker")
                .And.Not.Contain("JsonSerializer.Serialize(new { status, detail })"),
            "Self-healing pending markers should use a source-generated DTO.");

        string mcpTools = File.ReadAllText(Path.Combine(sidecarDir, "Mcp", "PalLlmMcpTools.cs"));
        Assert.That(mcpTools,
            Does.Contain("SerializeStatus")
                .And.Contain("McpStatusPayload")
                .And.Not.Contain("new { status ="),
            "MCP status/error result payloads should avoid anonymous object serialization.");

        string inferenceDir = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Inference");
        // Pass 346: OllamaWarmupRequestBody / BuildOllamaWarmupBody assertions
        // removed. The Ollama-native warmup transport was deleted alongside
        // the rest of the Ollama back-compat path, so the source-generation
        // surface now only carries the OpenAI-compatible chat-completions
        // request DTO.
        Assert.That(File.ReadAllText(Path.Combine(inferenceDir, "InferenceClient.cs")),
            Does.Contain("PalLlmDomainJsonSerializerContext.Default.InferenceChatCompletionsRequestBody")
                .And.Not.Contain("OllamaWarmupRequestBody")
                .And.Not.Contain("BuildOllamaWarmupBody")
                .And.Not.Contain("JsonContent.Create(requestBody);"),
            "Inference request JSON should use source-generated type metadata.");
        Assert.That(File.ReadAllText(Path.Combine(inferenceDir, "VisionClient.cs")),
            Does.Contain("PalLlmDomainJsonSerializerContext.Default.VisionChatCompletionsRequestBody")
                .And.Not.Contain("JsonContent.Create(requestBody);")
                .And.Not.Contain("Dictionary<string, object?> BuildRequestBody"),
            "Vision request JSON should use typed DTOs and source-generated metadata.");
        Assert.That(File.ReadAllText(Path.Combine(inferenceDir, "TtsClient.cs")),
            Does.Contain("PalLlmDomainJsonSerializerContext.Default.TtsHttpRequestBody")
                .And.Not.Contain("JsonContent.Create(new { text = request.Text, voice })"),
            "TTS request JSON should use source-generated type metadata.");

        string runtimeDir = Path.Combine(RepoRoot, "src", "PalLLM.Domain", "Runtime");
        Assert.That(File.ReadAllText(Path.Combine(runtimeDir, "SessionPersistence.cs")),
            Does.Contain("JsonContext.SessionFile")
                .And.Not.Contain("JsonSerializer.Serialize(stream, snapshot, JsonOptions)")
                .And.Not.Contain("JsonSerializer.Deserialize<SessionFile>(stream, JsonOptions)"),
            "Session persistence should use source-generated JSON metadata.");
        Assert.That(File.ReadAllText(Path.Combine(runtimeDir, "PalLlmRuntime.cs")),
            Does.Contain("BridgeJsonContext.BridgeEventEnvelope")
                .And.Contain("OutboxJsonContext.OutboxEnvelope")
                .And.Contain("UiProbeDumpJsonContext")
                .And.Not.Contain(".Deserialize<BridgeBootPayload>(BridgeJsonOptions)")
                .And.Not.Contain("JsonSerializer.SerializeAsync(stream, envelope, OutboxSerializerOptions"),
            "Bridge and outbox JSON should use source-generated metadata.");
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static string ResolveRepoRoot()
    {
        // Walk up from the test assembly directory until we hit a folder
        // containing PalLLM.sln. This works regardless of how the tests
        // are launched (dotnet test from repo root, IDE test runner, etc.).
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "PalLLM.sln")))
            {
                return dir;
            }
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            dir = parent;
        }
        throw new InvalidOperationException(
            $"Could not locate PalLLM.sln walking up from {AppContext.BaseDirectory}");
    }

    private static int CountExecutableNUnitCasesAcrossSuite()
    {
        // Count executable NUnit cases the same way Drift_Test_count_docs
        // does: each [Test] contributes one case and each declared
        // [TestCase(...)] contributes one additional executable case.
        // Excludes [TestFixture], [TestCaseSource], and other metadata.
        string testsDir = Path.Combine(RepoRoot, "tests", "PalLLM.Tests");
        var testRegex = new Regex(@"^\s*\[(?:TestCase|Test)(?:\(|\])", RegexOptions.Multiline);
        int count = 0;
        foreach (string file in Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            // Skip generated files
            if (file.Contains("\\obj\\") || file.Contains("/obj/")) continue;
            string content = File.ReadAllText(file);
            count += testRegex.Matches(content).Count;
        }
        return count;
    }

    private static void AssertHttpClientRegistrationUsesPooling(string programText, string registrationMarker)
    {
        int start = programText.IndexOf(registrationMarker, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0),
            $"Program.cs missing HTTP client registration marker: {registrationMarker}");

        int nextRegistration = programText.IndexOf("builder.Services.", start + registrationMarker.Length, StringComparison.Ordinal);
        string section = nextRegistration >= 0
            ? programText[start..nextRegistration]
            : programText[start..];

        Assert.That(section, Does.Contain("UseSocketsHttpHandler"),
            $"{registrationMarker} should configure SocketsHttpHandler pooling.");
        Assert.That(section, Does.Contain("PooledConnectionLifetime"),
            $"{registrationMarker} should set PooledConnectionLifetime.");
        Assert.That(section, Does.Contain("PooledConnectionIdleTimeout"),
            $"{registrationMarker} should set PooledConnectionIdleTimeout.");
        Assert.That(section, Does.Contain("SetHandlerLifetime(Timeout.InfiniteTimeSpan)"),
            $"{registrationMarker} should delegate handler rotation to SocketsHttpHandler pooling.");
    }
}
