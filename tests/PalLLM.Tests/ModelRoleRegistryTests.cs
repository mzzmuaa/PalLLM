using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="ModelRoleRegistry"/>. Contract pinned here:
///
/// 1. An empty <c>ModelRoles</c> list reports every role as
///    unconfigured + inactive, flags Edge + Worker as critical gaps,
///    and still returns a useful pairing-pattern message.
/// 2. Binding Worker + Judge returns the "Worker + Judge only" pairing
///    pattern that matches the 2035 doc's common-starter recommendation.
/// 3. Binding all three of Edge + Worker + Judge returns the full
///    "local mesh" pattern string.
/// 4. A disabled binding does NOT count as active for coverage, but is
///    still reported under the slot's <c>Bindings</c> list so operators
///    can see what has been pre-declared.
/// 5. Every slot always carries a non-empty <c>Description</c> and
///    <c>Recommendation</c> so AI clients + humans get guidance on
///    what to put in each slot.
/// </summary>
public sealed class ModelRoleRegistryTests
{
    [Test]
    public void GetCoverage_WithNoBindings_ReportsCriticalGaps()
    {
        var options = new PalLlmOptions();
        var registry = new ModelRoleRegistry(options);

        ModelRoleCoverage coverage = registry.GetCoverage();

        Assert.That(coverage.Slots, Has.Count.EqualTo(5));
        Assert.That(coverage.ActiveBindings, Is.EqualTo(0));
        Assert.That(coverage.TotalBindings, Is.EqualTo(0));
        Assert.That(coverage.CriticalGaps, Is.EquivalentTo(new[] { "Edge", "Worker" }));
        Assert.That(coverage.PairingPattern, Is.Not.Empty);

        foreach (ModelRoleSlot slot in coverage.Slots)
        {
            Assert.That(slot.Description, Is.Not.Empty, $"Slot {slot.Role} must describe what the role is for.");
            Assert.That(slot.Recommendation, Is.Not.Empty, $"Slot {slot.Role} must give a recommendation.");
            Assert.That(slot.IsConfigured, Is.False);
            Assert.That(slot.IsActive, Is.False);
        }
    }

    [Test]
    public void GetCoverage_WithWorkerAndJudge_SuggestsWorkerPlusJudgePattern()
    {
        var options = new PalLlmOptions();
        options.ModelRoles.Add(new ModelRoleBinding
        {
            Role = ModelRole.Worker,
            Id = "qwen-fast",
            ModelId = "qwen3.6:35b-a3b",
            BaseUrl = "http://127.0.0.1:11434/v1/",
            Enabled = true,
        });
        options.ModelRoles.Add(new ModelRoleBinding
        {
            Role = ModelRole.Judge,
            Id = "qwen-dense",
            ModelId = "qwen3.6:27b",
            BaseUrl = "http://127.0.0.1:11434/v1/",
            Enabled = true,
        });
        var registry = new ModelRoleRegistry(options);

        ModelRoleCoverage coverage = registry.GetCoverage();

        Assert.That(coverage.ActiveBindings, Is.EqualTo(2));
        Assert.That(coverage.CriticalGaps, Contains.Item("Edge"),
            "Edge is a critical gap until a perception model is bound.");
        Assert.That(coverage.CriticalGaps, Does.Not.Contain("Worker"));
        Assert.That(coverage.PairingPattern, Does.Contain("Worker + Judge"));
    }

    [Test]
    public void GetCoverage_WithFullMesh_SuggestsFullMeshPattern()
    {
        var options = new PalLlmOptions();
        foreach (ModelRole role in new[] { ModelRole.Edge, ModelRole.Worker, ModelRole.Judge })
        {
            options.ModelRoles.Add(new ModelRoleBinding
            {
                Role = role,
                Id = role.ToString().ToLowerInvariant(),
                ModelId = "placeholder",
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Enabled = true,
            });
        }
        var registry = new ModelRoleRegistry(options);

        ModelRoleCoverage coverage = registry.GetCoverage();

        Assert.That(coverage.CriticalGaps, Is.Empty, "Edge + Worker both bound means no critical gap.");
        Assert.That(coverage.PairingPattern, Does.Contain("Full local mesh"));
    }

    [Test]
    public void GetCoverage_DisabledBinding_DoesNotCountAsActive()
    {
        var options = new PalLlmOptions();
        options.ModelRoles.Add(new ModelRoleBinding
        {
            Role = ModelRole.Edge,
            Id = "gemma-placeholder",
            Enabled = false,
        });
        var registry = new ModelRoleRegistry(options);

        ModelRoleCoverage coverage = registry.GetCoverage();

        ModelRoleSlot edge = coverage.Slots.Single(s => s.Role == "Edge");
        Assert.That(edge.IsConfigured, Is.True, "The binding is recorded...");
        Assert.That(edge.IsActive, Is.False, "...but a disabled binding is not treated as active.");
        Assert.That(edge.Bindings, Has.Count.EqualTo(1),
            "Disabled bindings are still listed so operators see them as pre-declared options.");
        Assert.That(coverage.ActiveBindings, Is.EqualTo(0));
        Assert.That(coverage.TotalBindings, Is.EqualTo(1));
        Assert.That(coverage.CriticalGaps, Contains.Item("Edge"));
    }

    [Test]
    public void GetCoverage_MultipleBindingsPerRole_FirstEnabledWins()
    {
        var options = new PalLlmOptions();
        options.ModelRoles.Add(new ModelRoleBinding
        {
            Role = ModelRole.Worker,
            Id = "primary",
            Enabled = false,
        });
        options.ModelRoles.Add(new ModelRoleBinding
        {
            Role = ModelRole.Worker,
            Id = "secondary",
            Enabled = true,
        });
        var registry = new ModelRoleRegistry(options);

        ModelRoleCoverage coverage = registry.GetCoverage();
        ModelRoleSlot worker = coverage.Slots.Single(s => s.Role == "Worker");

        Assert.That(worker.IsActive, Is.True);
        Assert.That(worker.Bindings, Has.Count.EqualTo(2));
    }
}
