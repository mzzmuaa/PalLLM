---
name: Support export (with `support.bat` bundle)
about: Report a runtime issue with the full support-bundle attached. This is the highest-signal way to report problems.
title: "[support-export] "
labels: needs-triage, support-bundle
---

## Before you open this

**Please run `support.bat` first.** It lives in the root of your extracted
PalLLM release zip and produces a single `Runtime/SupportEvidence/latest-support-bundle.zip`
containing launch evidence, health snapshots, bridge proof, release
readiness, and redacted log tails. Attaching it as a GitHub comment
saves several back-and-forth rounds.

- [ ] I ran `support.bat` and am attaching the resulting zip below.

## What happened

<!-- One-paragraph description of the symptom. -->

## Reproduction steps

1.
2.
3.

## Expected vs observed

- Expected:
- Observed:

## Environment

<!-- Fill in from `/api/describe` or `/api/hardware` if you can reach them. -->

- PalLLM version:
- OS:
- Hardware tier (from `/api/hardware`):
- Inference enabled: yes / no
- Upstream inference endpoint: (redact auth keys)

## Attached files

- [ ] `Runtime/SupportEvidence/latest-support-bundle.zip`
- [ ] (optional) screenshot or short video clip

## Privacy note

The support bundle is produced locally and contains runtime health +
launch evidence. Check the attached zip before posting if you're worried
about what it includes; see `docs/PRIVACY.md` for the full inventory.
