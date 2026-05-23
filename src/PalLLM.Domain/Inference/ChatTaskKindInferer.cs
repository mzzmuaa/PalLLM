namespace PalLLM.Domain.Inference;

/// <summary>
/// Deterministic keyword-based classifier that infers a
/// <see cref="DuoTaskKind"/> from a raw chat request. Used by the
/// <c>POST /api/chat/plan</c> advisor to return a
/// <see cref="DuoPlan"/> tailored to the actual user message without
/// running a real inference turn.
///
/// <para>Intentionally simple: a sibling to
/// <c>WhyEngine.Classify</c>. Order matters — more-specific keywords
/// win over generic ones. Unknown shape collapses to
/// <see cref="DuoTaskKind.ImplementDraft"/>, which is the safest
/// general-purpose default (Architect → Implementer → Auditor).</para>
///
/// <para>Task-tag override: if the caller passes a TaskTag that
/// exactly matches one of the <see cref="DuoTaskKind"/> enum names
/// (case-insensitive), that wins over any message-keyword inference.
/// Lets automation explicitly request a specific pattern shape
/// without gaming the classifier.</para>
/// </summary>
public static class ChatTaskKindInferer
{
    public static DuoTaskKind Infer(string? userMessage, string? taskTag = null)
    {
        // Explicit override via task tag.
        if (!string.IsNullOrWhiteSpace(taskTag)
            && Enum.TryParse(taskTag.Trim(), ignoreCase: true, out DuoTaskKind tagged))
        {
            return tagged;
        }

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return DuoTaskKind.ImplementDraft;
        }

        string q = userMessage.ToLowerInvariant();

        // High-risk phrasing always wins so the planner forces
        // ParallelDisagreement regardless of surface intent.
        if (Contains(q,
            "delete", "drop table", "wipe", "rm -rf",
            "shutdown", "factory reset", "credit card", "ssn",
            "password", "api key", "production deploy", "release tag"))
        {
            return DuoTaskKind.HighRisk;
        }

        // Architecture / spec / plan intent.
        if (Contains(q,
            "architecture", "design doc", "spec", "specification",
            "contract", "plan this", "design a system", "adr",
            "high level plan", "intent contract"))
        {
            return DuoTaskKind.ArchitecturePlan;
        }

        // Audit / review intent.
        if (Contains(q,
            "audit", "review this", "code review", "security review",
            "lint", "validate", "check correctness"))
        {
            return DuoTaskKind.Audit;
        }

        // Tool execution intent — checked BEFORE synthesis so phrases
        // like "run the script and summarise the output" classify on
        // their primary tool-execution intent, not on the trailing
        // "summarise" verb.
        if (Contains(q,
            "run this", "execute this", "run the tool", "call the api",
            "invoke", "tool call", "browser.navigate", "file.write",
            "run the script"))
        {
            return DuoTaskKind.ToolExecution;
        }

        // Long-context synthesis — checked BEFORE generic synthesis so
        // phrases like "summarise the entire repo" route to the
        // long-context budget instead of the generic synthesis pattern.
        if (Contains(q,
            "entire repo", "whole repository", "long document", "all files",
            "session history", "full transcript", "across every", "across all"))
        {
            return DuoTaskKind.LongContextSynthesis;
        }

        // Parallel candidates / branch tournament intent.
        if (Contains(q,
            "candidates", "variants", "options", "alternatives",
            "brainstorm", "list 3", "list three", "list 5", "give me options",
            "generate many", "tournament"))
        {
            return DuoTaskKind.ParallelCandidates;
        }

        // Final synthesis intent.
        if (Contains(q,
            "synthesize", "merge these", "combine these", "summarize",
            "summarise", "consolidate", "final version", "final answer"))
        {
            return DuoTaskKind.FinalSynthesis;
        }

        // Media / asset prompting.
        if (Contains(q,
            "prompt for", "image prompt", "video prompt", "audio prompt",
            "generate image", "generate video", "generate audio",
            "style genome", "continuity"))
        {
            return DuoTaskKind.MediaPrompting;
        }

        // Short routing intents (usually a few words).
        if (userMessage.Trim().Length <= 40
            && Contains(q,
                "open", "close", "toggle", "enable", "disable",
                "help", "status", "what is", "where is"))
        {
            return DuoTaskKind.CommandRouting;
        }

        // Default for everything else — ImplementDraft covers most
        // coding / conversation turns and maps to the safe
        // Architect → Implementer → Auditor pattern.
        return DuoTaskKind.ImplementDraft;
    }

    private static bool Contains(string text, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.Ordinal)) { return true; }
        }
        return false;
    }
}
