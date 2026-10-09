# Codex Agent Work Log

Last audited: `2026-06-05`

Agent: Codex

Purpose: concise handoff notes for Codex-authored PalLLM maintenance passes.
This file records what changed, what was verified, and what remains blocked.
It does not replace `README.md`, `docs/HANDOFF.md`, or `CHANGELOG.md`.

## Pass 452 - secondary documentation truth resync

Date: `2026-06-05`

Scope:

- Verified the existing dirty Pass 448-451 tree started green:
  `dotnet test PalLLM.sln -c Release --nologo` passed `1310/1310`; full audit
  passed `16/16` with `0` warnings at
  `artifacts/full-audit/20260605-223811/RESULTS.md`.
- Updated secondary docs that are read by humans and small agents but are not
  direct count-gate mirrors: stale `1309/1309` references now say `1310/1310`.
- Refreshed the hot-file line maps in `docs/CODE_MAP.md`,
  `docs/REFACTORING_ROADMAP.md`, and the `docs/HANDOFF.md` current-state block
  so they match the current working tree.
- Tightened readiness/compatibility wording around the active local inference
  surface: Qwen3.5 9B fast lane + Gemma 4 12B smart/multimodal lane through
  llama.cpp, with deterministic fallback and the cloud escape path when that
  mesh does not fit. No code, route, MCP, feature-catalog, OpenAPI, or
  executable-test surface changed.
- Final logged-state `dotnet test PalLLM.sln -c Release --nologo` passed
  `1310/1310`; full audit passed `16/16` with `0` warnings at
  `artifacts/full-audit/20260605-225518/RESULTS.md`.

## Pass 451 - README scope boundary hardening

Date: `2026-06-05`

Scope:

- Preserved the existing dirty Pass 448-450 tree and verified the start state:
  `dotnet test PalLLM.sln -c Release --nologo` passed `1310/1310`; full audit
  passed `16/16` with `0` warnings at
  `artifacts/full-audit/20260605-192517/RESULTS.md`.
- Added a central README scope/ownership boundary so future agents keep PalLLM
  focused on this mod and import only generic engineering patterns from
  adjacent local projects, never their names, assets, prompts, lore, gameplay
  rules, or product identity.
- Updated `CHANGELOG.md` and `docs/HANDOFF.md` append-only pass notes. No code,
  route, MCP, feature-catalog, model-surface, or test-count surface changed.
- Final `dotnet test PalLLM.sln -c Release --nologo` passed `1310/1310`; final
  full audit passed `16/16` with `0` warnings at
  `artifacts/full-audit/20260605-192935/RESULTS.md`.

## Pass 450 - active local-model example guard

Date: `2026-06-05`

Scope:

- Preserved the existing dirty Pass 448/449 tree and verified the start state:
  `dotnet test PalLLM.sln -c Release --nologo` passed `1310/1310`; full audit
  passed `16/16` with `0` warnings at
  `artifacts/full-audit/20260605-190346/RESULTS.md`.
- Rechecked current public anchors for the active assumptions: Google announced
  Gemma 4 12B on June 3, 2026; the Qwen3.5-9B model card is live; Palworld
  Server Guide 0.7.2 still exposes admin/settings/player/metrics REST surfaces,
  not structure placement.
- Updated active operator examples that still promoted retired local model
  families: `docs/examples/compose.yaml`, `docs/MCP_QUICKSTART.md`,
  `docs/OPERATIONS.md`, `docs/API.md`, `docs/ARCHITECTURE.md`,
  `docs/CHEAT_SHEET.md`, and `scripts/connect-llamacpp.ps1`.
- Strengthened the existing `BundledDoc_DoesNotPromoteRetiredHeavyweightFamilies`
  test to scan the active llama.cpp documentation and scripts surface for retired local model
  families without adding a new `[Test]` count.
- Focused `LlamaCppBundlingTests` passed `57/57`; final `dotnet test
  PalLLM.sln -c Release --nologo` passed `1310/1310`; full audit passed
  `16/16` at `artifacts/full-audit/20260605-191553/RESULTS.md`.

## Pass 449 - agent-work log and 2035 horizon refresh

Date: `2026-06-05`

Scope:

- Read the automation memory and the README authority model before editing.
- Verified the dirty tree started green: `dotnet test PalLLM.sln -c Release
  --nologo` passed `1310/1310`, then the full audit passed `16/16` with
  `0` warnings at `artifacts/full-audit/20260605-172504/RESULTS.md`.
- Reviewed the active local changes from the prior pass and preserved them.
- Checked adjacent local project READMEs for documentation-governance patterns;
  carried forward only the generic pattern of a single authoritative README,
  explicit gates, and concise handoff notes. No sibling code, names, assets, or
  product identity were imported into PalLLM.
- Refreshed `docs/FUTURE_2035.md` with a June 5, 2026 research scan focused on
  advisory base-layout planning, official Palworld server/API limits, grounded
  reflective planning, world-action-model ideas, and current agentic-AI risk
  management.
- Added this `docs/AgentWork/` log and linked it from the README and docs index
  so future agents have a stable place for succinct pass notes.
- Fixed a drift-gate failure introduced by refreshing this doc set: a
  hypothetical future router was documented as a concrete repo path even though
  that file does not exist. The reference is now a non-path future type name.

Current interpretation:

- The central README is already the right source of truth and should remain the
  first document both humans and agents read.
- The missing non-autonomous blocker is still live Palworld/UE4SS native proof.
- Base/autobuild work should stay advisory until PalLLM has live structure
  catalog, placement validity, pathing-clearance, and native-hook receipts.

## 2026-10-08 — GitHub publication refresh review

Preserved the five unpublished committed passes and newer local lane/documentation simplification as a publication candidate. Added the compatible Microsoft.OpenApi 2.7.5 security pin for GHSA-v5pm-xwqc-g5wc; 1310 tests passed. Changed the current-status historical ignored-artifact pointer to plain text so it survives a fresh clone. Full audit retains its stale-document finding: old audit dates were not falsely refreshed. No live Palworld or model proof.
