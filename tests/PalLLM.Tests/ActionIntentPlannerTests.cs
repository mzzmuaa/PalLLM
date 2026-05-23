using PalLLM.Domain.Configuration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

// Pass 299 - direct unit tests for the action-intent planner: the
// security-critical mapping from a fallback strategy id to a safe,
// allowlist-gated suggestion the runtime is willing to send to the game.
//
// Two safety gates protect the player. (1) `AutomationOptions.Enabled`
// must be true. (2) The mapped action `Type` must appear in
// `AutomationOptions.AllowedActions`. Both gates are independent kill
// switches an operator controls — flipping either off must stop the
// runtime from emitting any action intent, regardless of which strategy
// fired.
//
// Until this pass the planner was only covered indirectly by chat-path
// integration fixtures. The kill-switch branches, the strategy-to-type
// mapping for all 6 known strategies, the unknown-strategy null return,
// and the `ExtractResourceLabel` helper had no direct fast-feedback
// coverage. A regression that, say, started emitting intents when
// `Enabled=false`, or stopped honouring the type allowlist, would have
// shipped through every existing test green.
public sealed class ActionIntentPlannerTests
{
    // ---------- Kill switches ----------

    [Test]
    public void Plan_AutomationDisabled_ReturnsNull()
    {
        var context = NewContext();
        var decision = NewDecision("retreat-and-rally");
        var automation = new AutomationOptions { Enabled = false, AllowedActions = ["recall_pals"] };

        Assert.That(ActionIntentPlanner.Plan(context, decision, automation), Is.Null);
    }

    [Test]
    public void Plan_AllowedActionsEmpty_ReturnsNull()
    {
        var context = NewContext();
        var decision = NewDecision("retreat-and-rally");
        var automation = new AutomationOptions { Enabled = true, AllowedActions = [] };

        Assert.That(ActionIntentPlanner.Plan(context, decision, automation), Is.Null);
    }

    [Test]
    public void Plan_TypeNotOnAllowedList_ReturnsNull()
    {
        // The strategy maps to `recall_pals` but the allowlist only permits
        // `waypoint_suggest`. The planner must NOT emit the intent.
        var context = NewContext();
        var decision = NewDecision("retreat-and-rally");
        var automation = new AutomationOptions { Enabled = true, AllowedActions = ["waypoint_suggest"] };

        Assert.That(ActionIntentPlanner.Plan(context, decision, automation), Is.Null);
    }

    [Test]
    public void Plan_AllowlistMatchIsCaseInsensitive()
    {
        // Operators write `RECALL_PALS` in config and expect the planner to
        // honour it (the canonical form is lowercase, but the comparison
        // uses OrdinalIgnoreCase).
        var context = NewContext();
        var decision = NewDecision("retreat-and-rally");
        var automation = new AutomationOptions
        {
            Enabled = true,
            AllowedActions = ["RECALL_PALS"]
        };

        var intent = ActionIntentPlanner.Plan(context, decision, automation);

        Assert.That(intent, Is.Not.Null);
        Assert.That(intent!.Type, Is.EqualTo("recall_pals"));
    }

    // ---------- Unknown strategy ----------

    [Test]
    public void Plan_UnknownStrategy_ReturnsNull()
    {
        // Tactical-commentary strategies (hero-moment, nemesis-counterplay,
        // morale-rally, etc.) do not produce intents. The planner returns
        // null for any unmapped strategy id.
        var context = NewContext();
        var decision = NewDecision("hero-moment");
        var automation = NewAutomationAllowingAll();

        Assert.That(ActionIntentPlanner.Plan(context, decision, automation), Is.Null);
    }

    [TestCase("")]
    [TestCase("not-a-strategy")]
    [TestCase("morale-rally")]
    public void Plan_UnmappedStrategyIds_ReturnNull(string strategyId)
    {
        var context = NewContext();
        var decision = NewDecision(strategyId);
        var automation = NewAutomationAllowingAll();

        Assert.That(ActionIntentPlanner.Plan(context, decision, automation), Is.Null);
    }

    // ---------- Each mapped strategy ----------

    [Test]
    public void Plan_RetreatAndRally_EmitsRecallPalsIntent()
    {
        var intent = ActionIntentPlanner.Plan(
            NewContext(primaryBase: "Outpost-A"),
            NewDecision("retreat-and-rally"),
            NewAutomationAllowingAll());

        Assert.That(intent, Is.Not.Null);
        Assert.That(intent!.Type, Is.EqualTo("recall_pals"));
        Assert.That(intent.SourceStrategy, Is.EqualTo("retreat-and-rally"));
        Assert.That(intent.Priority, Is.EqualTo(90));
        Assert.That(intent.Arguments["reason"], Is.EqualTo("combat_retreat"));
        Assert.That(intent.Arguments["anchor"], Is.EqualTo("Outpost-A"));
        Assert.That(intent.Arguments["mode"], Is.EqualTo("defensive_regroup"));
    }

    [Test]
    public void Plan_PerimeterLockdown_EmitsRecallPalsIntent()
    {
        var intent = ActionIntentPlanner.Plan(
            NewContext(primaryBase: "Bunker"),
            NewDecision("perimeter-lockdown"),
            NewAutomationAllowingAll());

        Assert.That(intent, Is.Not.Null);
        Assert.That(intent!.Type, Is.EqualTo("recall_pals"));
        Assert.That(intent.Priority, Is.EqualTo(85));
        Assert.That(intent.Arguments["reason"], Is.EqualTo("base_defense"));
        Assert.That(intent.Arguments["mode"], Is.EqualTo("base_lockdown"));
        Assert.That(intent.Arguments["anchor"], Is.EqualTo("Bunker"));
    }

    [Test]
    public void Plan_SafeTravel_InBase_DestinationIsSecondaryBase()
    {
        var intent = ActionIntentPlanner.Plan(
            NewContext(inBase: true, primaryBase: "Home", secondaryBase: "Forest-Camp"),
            NewDecision("safe-travel"),
            NewAutomationAllowingAll());

        Assert.That(intent, Is.Not.Null);
        Assert.That(intent!.Type, Is.EqualTo("waypoint_suggest"));
        Assert.That(intent.Priority, Is.EqualTo(60));
        Assert.That(intent.Arguments["origin"], Is.EqualTo("Home"));
        Assert.That(intent.Arguments["destination"], Is.EqualTo("Forest-Camp"));
        Assert.That(intent.Arguments["waypoint"], Is.EqualTo("Forest-Camp"));
    }

    [Test]
    public void Plan_SafeTravel_NotInBase_OriginIsCurrentPosition()
    {
        var intent = ActionIntentPlanner.Plan(
            NewContext(inBase: false, primaryBase: "Home", secondaryBase: "Forest-Camp"),
            NewDecision("safe-travel"),
            NewAutomationAllowingAll());

        Assert.That(intent, Is.Not.Null);
        Assert.That(intent!.Arguments["origin"], Is.EqualTo("current_position"));
        Assert.That(intent.Arguments["destination"], Is.EqualTo("Forest-Camp"));
        Assert.That(intent.Arguments["waypoint"], Is.EqualTo("Home"));
    }

    [Test]
    public void Plan_HarvestWindow_ResourceHintIsForwardedToArguments()
    {
        var intent = ActionIntentPlanner.Plan(
            NewContext(resourceHint: "iron ore"),
            NewDecision("harvest-window"),
            NewAutomationAllowingAll());

        Assert.That(intent, Is.Not.Null);
        Assert.That(intent!.Type, Is.EqualTo("waypoint_suggest"));
        Assert.That(intent.Priority, Is.EqualTo(55));
        Assert.That(intent.Arguments["reason"], Is.EqualTo("resource_gather"));
        Assert.That(intent.Arguments["resource"], Is.EqualTo("iron ore"));
        Assert.That(intent.Arguments["destination"], Is.EqualTo("iron ore"));
    }

    [Test]
    public void Plan_HarvestWindow_NearPrefixStrippedFromResourceLabel()
    {
        // The fallback engine sometimes emits hints like "near coal" — the
        // planner strips the "near " prefix so the arg shape stays clean.
        var intent = ActionIntentPlanner.Plan(
            NewContext(resourceHint: "near coal"),
            NewDecision("harvest-window"),
            NewAutomationAllowingAll());

        Assert.That(intent!.Arguments["resource"], Is.EqualTo("coal"));
        Assert.That(intent.Arguments["destination"], Is.EqualTo("coal"));
    }

    [Test]
    public void Plan_HarvestWindow_BlankResourceFallsBackToNearestResource()
    {
        var intent = ActionIntentPlanner.Plan(
            NewContext(resourceHint: "   "),
            NewDecision("harvest-window"),
            NewAutomationAllowingAll());

        Assert.That(intent!.Arguments["resource"], Is.EqualTo("nearest_resource"));
    }

    [Test]
    public void Plan_ObjectivePush_BuildsStagedPushIntent()
    {
        var intent = ActionIntentPlanner.Plan(
            NewContext(currentObjective: "Sealed Realm", focusThreat: "Mammorest"),
            NewDecision("objective-push"),
            NewAutomationAllowingAll());

        Assert.That(intent, Is.Not.Null);
        Assert.That(intent!.Type, Is.EqualTo("waypoint_suggest"));
        Assert.That(intent.Priority, Is.EqualTo(70));
        Assert.That(intent.Arguments["destination"], Is.EqualTo("Sealed Realm"));
        Assert.That(intent.Arguments["waypoint"], Is.EqualTo("Mammorest"));
        Assert.That(intent.Justification, Does.Contain("Sealed Realm"));
    }

    [Test]
    public void Plan_BaseNetwork_EmitsRequestCraftQueueIntent()
    {
        var intent = ActionIntentPlanner.Plan(
            NewContext(primaryBase: "Iron-Hall", secondaryBase: "Pasture"),
            NewDecision("base-network"),
            NewAutomationAllowingAll());

        Assert.That(intent, Is.Not.Null);
        Assert.That(intent!.Type, Is.EqualTo("request_craft_queue"));
        Assert.That(intent.Priority, Is.EqualTo(65));
        Assert.That(intent.Arguments["reason"], Is.EqualTo("specialization"));
        Assert.That(intent.Arguments["primary_base"], Is.EqualTo("Iron-Hall"));
        Assert.That(intent.Arguments["secondary_base"], Is.EqualTo("Pasture"));
        Assert.That(intent.Arguments["station"], Is.EqualTo("logistics_planner"));
    }

    // ---------- Source-strategy traceability ----------

    [Test]
    public void Plan_IntentRecordsSourceStrategy()
    {
        // The `SourceStrategy` field is load-bearing for the proof-packet
        // trail and the player-facing trace note that explains why a
        // particular suggestion was emitted.
        var intent = ActionIntentPlanner.Plan(
            NewContext(),
            NewDecision("safe-travel"),
            NewAutomationAllowingAll());

        Assert.That(intent!.SourceStrategy, Is.EqualTo("safe-travel"));
    }

    // ---------- Helpers ----------

    private static FallbackBehaviorContext NewContext(
        bool inBase = true,
        string primaryBase = "Home",
        string secondaryBase = "Forest-Camp",
        string currentObjective = "",
        string focusThreat = "the nearest threat",
        string resourceHint = "")
    {
        return new FallbackBehaviorContext
        {
            InBase = inBase,
            PrimaryBaseLabel = primaryBase,
            SecondaryBaseLabel = secondaryBase,
            HasObjective = !string.IsNullOrWhiteSpace(currentObjective),
            CurrentObjectiveLabel = string.IsNullOrWhiteSpace(currentObjective)
                ? "the next objective"
                : currentObjective,
            FocusThreat = focusThreat,
            ResourceHint = resourceHint,
        };
    }

    private static FallbackBehaviorDecision NewDecision(string strategyId) =>
        new(strategyId,
            FallbackPacingPhase.Peak,
            message: "test message",
            priority: 50,
            signals: [],
            isApplicable: true);

    private static AutomationOptions NewAutomationAllowingAll() => new()
    {
        Enabled = true,
        AllowedActions = ["recall_pals", "waypoint_suggest", "request_craft_queue"],
    };
}
