using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PalLLM.Tests;

/// <summary>
/// Pass 359 — actually executes the PowerShell scripts I've been
/// shipping across Passes 344-358 instead of just grepping their
/// source. The 76+ LlamaCppBundlingTests all pin text presence;
/// none of them prove the scripts parse, that parameter binding
/// works, or that <c>-DryRun</c> exits 0. A syntax error in
/// connect-cloud.ps1 (which I added in Pass 357 and never ran)
/// would slip past every test we have today.
///
/// <para>Each test shells out to pwsh with explicit arguments,
/// captures stdout/stderr, and asserts the documented contract:
/// exit code, expected output strings, expected error behaviour.
/// Tests skip with <c>Assert.Ignore</c> when no pwsh binary is on
/// the PATH so CI environments without PowerShell installed still
/// pass.</para>
/// </summary>
[TestFixture]
public sealed class ScriptExecutionTests
{
    private string? _pwshPath;

    [OneTimeSetUp]
    public void FindPwsh()
    {
        // Prefer pwsh (PowerShell 7+, cross-platform). Fall back to
        // powershell.exe (Windows PowerShell 5.1) when pwsh isn't
        // installed but we're on Windows.
        foreach (string candidate in new[] { "pwsh", "pwsh.exe", "powershell.exe", "powershell" })
        {
            string? resolved = ResolveExe(candidate);
            if (resolved is not null)
            {
                _pwshPath = resolved;
                TestContext.Out.WriteLine($"ScriptExecutionTests using PowerShell at: {_pwshPath}");
                return;
            }
        }
    }

    [Test]
    public void InstallLlamaCpp_DryRun_OnTarget_ExitsCleanly()
    {
        SkipIfNoPwsh();
        string script = LocateRepoFile("scripts", "install-llama-cpp.ps1");

        // -ReleaseTag pinned so the test doesn't hit GitHub's latest-release
        // API; -Platform win-x64 + -Backend cuda12 forces the on-target
        // code path regardless of where the test runner is.
        ProcessResult r = RunPwsh(script,
            "-DryRun",
            "-ReleaseTag", "b9284",
            "-Platform", "win-x64",
            "-Backend", "cuda12");

        Assert.That(r.ExitCode, Is.EqualTo(0),
            $"install-llama-cpp.ps1 -DryRun must exit 0 on the on-target happy path. " +
            $"stdout=<<<{r.Stdout}>>> stderr=<<<{r.Stderr}>>>");
        Assert.That(r.Stdout, Does.Contain("PalLLM bundled-llama.cpp installer"),
            "DryRun must print the installer header.");
        Assert.That(r.Stdout, Does.Contain("DryRun: no download"),
            "DryRun must announce no-download/no-extract before exiting.");
        Assert.That(r.Stdout, Does.Contain("llama-b9284-bin-win-cuda-12.4-x64.zip"),
            "On-target DryRun must resolve to the cuda-12.4 asset name (Pass 347 fix; Pass 349 hardware-aware).");
    }

    [Test]
    public void InstallLlamaCpp_OffTarget_NoExplicitBackend_SkipsLocalInstall()
    {
        SkipIfNoPwsh();
        string script = LocateRepoFile("scripts", "install-llama-cpp.ps1");

        // Off-target platform with no explicit -Backend = Pass 357
        // hard-gate kicks in, prints both escape paths, exits 0
        // without proceeding to a local install.
        ProcessResult r = RunPwsh(script,
            "-Platform", "linux-x64",
            "-ReleaseTag", "b9284");

        Assert.That(r.ExitCode, Is.EqualTo(0),
            $"install-llama-cpp.ps1 must exit 0 on the off-target skip-local path. " +
            $"stdout=<<<{r.Stdout}>>> stderr=<<<{r.Stderr}>>>");
        Assert.That(r.Stdout, Does.Contain("OFF-TARGET"),
            "Off-target hosts must see the OFF-TARGET label (Pass 357).");
        Assert.That(r.Stdout, Does.Contain("Cloud API"),
            "Off-target hosts must see the Cloud API escape path.");
        Assert.That(r.Stdout, Does.Contain("Remote PC"),
            "Off-target hosts must see the Remote PC escape path.");
        Assert.That(r.Stdout, Does.Contain("Skipping local install"),
            "Off-target hosts must explicitly skip local install rather than proceed off-spec.");
    }

    [Test]
    public void InstallLlamaCpp_OffTarget_ExplicitBackend_ProceedsToDryRun()
    {
        SkipIfNoPwsh();
        string script = LocateRepoFile("scripts", "install-llama-cpp.ps1");

        // Off-target platform + explicit -Backend = operator opt-in to
        // off-target local install. Must proceed past the Pass 357
        // hard-gate and reach the DryRun exit.
        ProcessResult r = RunPwsh(script,
            "-DryRun",
            "-Platform", "linux-x64",
            "-Backend", "cuda12",
            "-ReleaseTag", "b9284");

        Assert.That(r.ExitCode, Is.EqualTo(0),
            $"Off-target + explicit -Backend opt-in must reach DryRun. " +
            $"stdout=<<<{r.Stdout}>>> stderr=<<<{r.Stderr}>>>");
        Assert.That(r.Stdout, Does.Contain("DryRun: no download"),
            "Explicit -Backend opt-in must override the Pass 357 hard-gate and reach the DryRun block.");
        // Linux-x64 doesn't have the per-backend split upstream yet;
        // the script falls back to the monolithic ubuntu asset.
        Assert.That(r.Stdout, Does.Contain("llama-b9284-bin-ubuntu-x64.zip"),
            "Linux platform must resolve to the ubuntu monolithic asset.");
    }

    [Test]
    public void ConnectCloud_DryRun_OpenaiProvider_ExitsCleanly()
    {
        SkipIfNoPwsh();
        string script = LocateRepoFile("scripts", "connect-cloud.ps1");

        // The script Pass 357 shipped, never previously executed.
        // -DryRun avoids the actual config write; -ApiKey supplied
        // synthetic so the mandatory-parameter check passes.
        ProcessResult r = RunPwsh(script,
            "-Provider", "openai",
            "-Model", "gpt-4o-mini",
            "-ApiKey", "sk-test-abcdef0123456789",
            "-DryRun");

        Assert.That(r.ExitCode, Is.EqualTo(0),
            $"connect-cloud.ps1 -DryRun must exit 0. " +
            $"stdout=<<<{r.Stdout}>>> stderr=<<<{r.Stderr}>>>");
        Assert.That(r.Stdout, Does.Contain("PalLLM <- cloud API"),
            "connect-cloud.ps1 must print the connector header.");
        Assert.That(r.Stdout, Does.Contain("api.openai.com/v1/"),
            "connect-cloud.ps1 must resolve the openai preset URL.");
        Assert.That(r.Stdout, Does.Contain("[DryRun]"),
            "connect-cloud.ps1 must announce DryRun before exiting.");
        // The masked key MUST appear (first 4 + last 4 chars visible);
        // the middle MUST NOT (so logs don't leak the secret).
        Assert.That(r.Stdout, Does.Contain("sk-t"),
            "Masked key display must show the first 4 chars.");
        Assert.That(r.Stdout, Does.Not.Contain("sk-test-abcdef0123456789"),
            "Full ApiKey must NOT appear in the masked display — security regression guard.");
    }

    [Test]
    public void ConnectCloud_CustomProvider_WithoutBaseUrl_FailsCleanly()
    {
        SkipIfNoPwsh();
        string script = LocateRepoFile("scripts", "connect-cloud.ps1");

        // Provider=custom requires -BaseUrl; without it the script
        // throws with a clear message.
        ProcessResult r = RunPwsh(script,
            "-Provider", "custom",
            "-Model", "x",
            "-ApiKey", "x",
            "-DryRun");

        Assert.That(r.ExitCode, Is.Not.EqualTo(0),
            "Provider=custom + missing -BaseUrl must fail (non-zero exit).");
        // PowerShell throw output lands on stderr OR appears in stdout
        // depending on how the host renders errors. Check both.
        string allOutput = r.Stdout + " " + r.Stderr;
        Assert.That(allOutput, Does.Contain("requires -BaseUrl").Or.Contain("BaseUrl"),
            "Failure message must name -BaseUrl as the missing requirement.");
    }

    [Test]
    public void ConnectCloud_EmptyApiKey_FailsCleanly()
    {
        SkipIfNoPwsh();
        string script = LocateRepoFile("scripts", "connect-cloud.ps1");

        // The script's [Parameter(Mandatory)] attribute catches missing
        // -ApiKey, but an empty-string -ApiKey gets past the
        // parameter binder and must be caught by the validation block.
        ProcessResult r = RunPwsh(script,
            "-Provider", "openai",
            "-Model", "gpt-4o-mini",
            "-ApiKey", "",
            "-DryRun");

        Assert.That(r.ExitCode, Is.Not.EqualTo(0),
            "Empty -ApiKey must fail (non-zero exit).");
    }

    // ---------- Pass 360: cold-start benchmark ----------

    [Test]
    public void ColdStartBenchmark_DryRun_EmitsExpectedArtifact()
    {
        SkipIfNoPwsh();
        string script = LocateRepoFile("scripts", "pal-benchmark-coldstart.ps1");
        string outDir = Path.Combine(Path.GetTempPath(), $"PalLLM.ColdStart.{Guid.NewGuid():N}");
        try
        {
            ProcessResult r = RunPwsh(script,
                "-DryRun",
                "-OutputDir", outDir);

            Assert.That(r.ExitCode, Is.EqualTo(0),
                $"pal-benchmark-coldstart.ps1 -DryRun must exit 0. " +
                $"stdout=<<<{r.Stdout}>>> stderr=<<<{r.Stderr}>>>");
            Assert.That(r.Stdout, Does.Contain("[DryRun]"),
                "DryRun must announce itself before exiting.");
            Assert.That(r.Stdout, Does.Contain("ColdStart: build=skipped ready=DryRun chat=DryRun"),
                "DryRun must print the canonical summary line shape so operators copy-paste-pasting it back have something readable.");

            // The artifact JSON must parse + contain the expected
            // phase keys so downstream consumers (CI dashboard,
            // ticket templates) can rely on the shape.
            string[] artifacts = Directory.GetFiles(outDir, "*.json");
            Assert.That(artifacts.Length, Is.EqualTo(1),
                $"DryRun must write exactly one artifact JSON. Found {artifacts.Length} in {outDir}.");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(artifacts[0]));
            Assert.That(doc.RootElement.TryGetProperty("phases", out JsonElement phases), Is.True);
            Assert.That(phases.TryGetProperty("buildSeconds", out _), Is.True,
                "Artifact must declare phases.buildSeconds.");
            Assert.That(phases.TryGetProperty("readyTimeSeconds", out _), Is.True,
                "Artifact must declare phases.readyTimeSeconds.");
            Assert.That(phases.TryGetProperty("firstChatSeconds", out _), Is.True,
                "Artifact must declare phases.firstChatSeconds.");
            Assert.That(doc.RootElement.TryGetProperty("host", out JsonElement host), Is.True,
                "Artifact must carry host OS / arch / CPU metadata so cross-rig comparisons stay honest.");
            Assert.That(host.TryGetProperty("os", out _), Is.True);
            Assert.That(host.TryGetProperty("arch", out _), Is.True);
            Assert.That(host.TryGetProperty("processorCount", out _), Is.True);
        }
        finally
        {
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true); } catch { }
        }
    }

    [Test]
    public void ModelProbe_DryRun_EmitsExpectedArtifact()
    {
        SkipIfNoPwsh();
        string script = LocateRepoFile("scripts", "pal-model-probe.ps1");
        string outDir = Path.Combine(Path.GetTempPath(), $"PalLLM.ModelProbe.{Guid.NewGuid():N}");
        try
        {
            ProcessResult r = RunPwsh(script,
                "-DryRun",
                "-OutputDir", outDir);

            Assert.That(r.ExitCode, Is.EqualTo(0),
                $"pal-model-probe.ps1 -DryRun must exit 0. " +
                $"stdout=<<<{r.Stdout}>>> stderr=<<<{r.Stderr}>>>");
            Assert.That(r.Stdout, Does.Contain("PalLLM model endpoint probe"),
                "DryRun must print the operator-facing probe header.");
            Assert.That(r.Stdout, Does.Contain("Verdict   : dry-run"),
                "DryRun must print the deterministic dry-run verdict.");

            string[] artifacts = Directory.GetFiles(outDir, "*.json");
            Assert.That(artifacts.Length, Is.EqualTo(1),
                $"DryRun must write exactly one artifact JSON. Found {artifacts.Length} in {outDir}.");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(artifacts[0]));
            JsonElement root = doc.RootElement;
            Assert.That(root.GetProperty("dryRun").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("verdict").GetString(), Is.EqualTo("dry-run"));
            Assert.That(root.GetProperty("privacy").GetString(), Does.Contain("No chat"));

            JsonElement metrics = root.GetProperty("metrics");
            Assert.That(metrics.GetProperty("engineGuess").GetString(), Is.EqualTo("vllm"));
            JsonElement families = metrics.GetProperty("families");
            Assert.That(families.GetProperty("vllmPrefixCache").GetProperty("present").GetBoolean(), Is.True,
                "DryRun must demonstrate the prefix-cache metric family.");
            Assert.That(families.GetProperty("vllmKvCache").GetProperty("present").GetBoolean(), Is.True,
                "DryRun must demonstrate the KV-cache metric family.");
            Assert.That(families.GetProperty("vllmSpeculativeDecoding").GetProperty("present").GetBoolean(), Is.True,
                "DryRun must demonstrate the speculative-decoding metric family.");
        }
        finally
        {
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true); } catch { }
        }
    }

    [Test]
    public void Cleanup_NoApplyFlag_PreviewsOnly_DoesNotDelete()
    {
        // Pass 361: pal-cleanup.ps1 deletes things. The preview-by-default
        // contract is load-bearing — invoking the script with no -Apply
        // flag must NEVER touch the filesystem. This test creates a
        // sentinel coverage-shaped directory tree, runs cleanup without
        // -Apply, and asserts the sentinel survives.
        SkipIfNoPwsh();
        string script = LocateRepoFile("scripts", "pal-cleanup.ps1");
        string repoRoot = Path.GetDirectoryName(LocateRepoFile("PalLLM.sln"))!;

        // We need a sentinel that pal-cleanup's pattern would match,
        // but in a sandbox dir so we don't touch the real repo. Build
        // a fake artifacts/full-audit/<ts>/coverage tree in a temp
        // copy of the repo layout.
        string sandbox = Path.Combine(Path.GetTempPath(), $"PalLLM.CleanupTest.{Guid.NewGuid():N}");
        string fakeRepoRoot = Path.Combine(sandbox, "repo");
        string fakeAuditDir = Path.Combine(fakeRepoRoot, "artifacts", "full-audit", "20990101-000000");
        string fakeCoverage = Path.Combine(fakeAuditDir, "coverage");
        string sentinelFile = Path.Combine(fakeCoverage, "sentinel.html");
        try
        {
            Directory.CreateDirectory(fakeCoverage);
            File.WriteAllText(sentinelFile, "<html>sentinel</html>");
            // Place a copy of the cleanup script + a stub PalLLM.sln so
            // its repo-root detection (Split-Path -Parent $PSScriptRoot)
            // anchors on the sandbox.
            string fakeScriptsDir = Path.Combine(fakeRepoRoot, "scripts");
            Directory.CreateDirectory(fakeScriptsDir);
            string fakeScriptPath = Path.Combine(fakeScriptsDir, "pal-cleanup.ps1");
            File.Copy(script, fakeScriptPath);
            File.WriteAllText(Path.Combine(fakeRepoRoot, "PalLLM.sln"), "stub");

            ProcessResult r = RunPwsh(fakeScriptPath);
            Assert.That(r.ExitCode, Is.EqualTo(0),
                $"pal-cleanup.ps1 (no -Apply) must exit 0 in preview mode. " +
                $"stdout=<<<{r.Stdout}>>> stderr=<<<{r.Stderr}>>>");
            Assert.That(r.Stdout, Does.Contain("Preview").Or.Contain("preview"),
                "pal-cleanup.ps1 without -Apply must announce preview mode.");
            Assert.That(File.Exists(sentinelFile), Is.True,
                "pal-cleanup.ps1 in preview mode MUST NOT delete the sentinel file. This is the production-safety contract: no -Apply, no deletes.");
            Assert.That(Directory.Exists(fakeCoverage), Is.True,
                "pal-cleanup.ps1 in preview mode MUST NOT delete the candidate directory.");
        }
        finally
        {
            try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    [Test]
    public void HotPathDoc_DeclaresColdStartBudgetRow()
    {
        // Pass 360: HOT_PATH.md must include the cold-start section
        // so the benchmark output has a budget to compare against.
        string text = File.ReadAllText(LocateRepoFile("docs", "HOT_PATH.md"));
        Assert.That(text, Does.Contain("## Cold-start"),
            "HOT_PATH.md must include a Cold-start section.");
        Assert.That(text, Does.Contain("pal-benchmark-coldstart.ps1"),
            "HOT_PATH.md cold-start section must reference the benchmark script.");
        Assert.That(text, Does.Contain("dotnet run"),
            "Cold-start section must call out the dotnet run -> /health phase.");
        Assert.That(text, Does.Contain("/api/chat"),
            "Cold-start section must call out the first /api/chat phase.");
        Assert.That(text, Does.Contain("< 8 s"),
            "Cold-start section must declare the ready < 8s budget.");
        Assert.That(text, Does.Contain("< 10 s"),
            "Cold-start section must declare the combined < 10s budget.");
    }

    // ---------- Helpers ----------

    private void SkipIfNoPwsh()
    {
        if (string.IsNullOrEmpty(_pwshPath))
        {
            Assert.Ignore("No PowerShell binary on PATH (pwsh / powershell). Script-execution tests skipped.");
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private ProcessResult RunPwsh(string scriptPath, params string[] args)
    {
        if (string.IsNullOrEmpty(_pwshPath))
        {
            throw new InvalidOperationException("RunPwsh called without a resolved PowerShell binary. SkipIfNoPwsh should have run first.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = _pwshPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // -NoProfile keeps the runner deterministic; -ExecutionPolicy
        // Bypass mirrors how pal.ps1 launches its children.
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        bool exited = proc.WaitForExit(30_000);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException($"pwsh script {Path.GetFileName(scriptPath)} did not exit within 30s. Stdout so far: {stdout}");
        }
        // Ensure async readers flushed
        proc.WaitForExit();

        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string? ResolveExe(string name)
    {
        // Mirror `where` / `which` resolution: check PATHEXT-aware
        // names first, fall back to the literal arg.
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        char sep = Path.PathSeparator;
        foreach (string dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* skip malformed PATH entries */ }
        }
        return null;
    }

    private static string LocateRepoFile(params string[] segments)
    {
        string testBin = TestContext.CurrentContext.TestDirectory;
        DirectoryInfo? current = new(testBin);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PalLLM.sln")))
            {
                string candidate = Path.Combine(new[] { current.FullName }.Concat(segments).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                throw new FileNotFoundException(
                    $"Could not locate {string.Join(Path.DirectorySeparatorChar, segments)} under repo root {current.FullName}.");
            }
            current = current.Parent;
        }
        throw new FileNotFoundException("Could not locate the repo root.");
    }
}
