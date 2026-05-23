# PalLLM Makefile -- thin wrapper around pal.ps1.
#
# This file exists so contributors who reach for `make build` / `make test`
# muscle memory get a working command. Every target delegates to pal.ps1,
# which is the source of truth.
#
# Requires PowerShell (`pwsh` on Linux/macOS, `powershell` on Windows).
# `make` itself is optional -- the README and CHEAT_SHEET reference
# `pwsh ./pal.ps1 <verb>` directly so anyone without `make` can still
# follow the instructions.

PAL := pwsh -NoProfile -ExecutionPolicy Bypass -File ./pal.ps1

.PHONY: help build test audit fast-audit run play onboard doctor smoke openapi package publish-audit aot-readiness recover uninstall status context scaffold hello demo campfire fortune whisper quest tale patrol-report pack models config mcp connect support benchmark welcome next news harvest health proof logs preflight check-updates readiness where explain list

# Default target: show the pal.ps1 verb table.
help:
	@$(PAL) help

# Build / test / audit -- the most-used three.
build:
	@$(PAL) build

test:
	@$(PAL) test

audit:
	@$(PAL) audit

fast-audit:
	@$(PAL) fast-audit

# Day-to-day operations.
run:
	@$(PAL) run

play:
	@$(PAL) play

onboard:
	@$(PAL) onboard

doctor:
	@$(PAL) doctor

smoke:
	@$(PAL) smoke

openapi:
	@$(PAL) openapi

package:
	@$(PAL) package

publish-audit:
	@$(PAL) publish-audit

aot-readiness:
	@$(PAL) aot-readiness $(if $(PUBLISHPROBE),-PublishProbe) $(if $(RID),-RuntimeIdentifier $(RID))

recover:
	@$(PAL) recover

uninstall:
	@$(PAL) uninstall

status:
	@$(PAL) status

context:
	@$(PAL) context

scaffold:
	@$(PAL) scaffold $(KIND) $(NAME)

hello:
	@$(PAL) hello

demo:
	@$(PAL) demo

campfire:
	@$(PAL) campfire

fortune:
	@$(PAL) fortune

whisper:
	@$(PAL) whisper

# pal quest [-Tier <tier>] -- pass TIER=any|easy|medium|spicy|quiet.
quest:
	@$(PAL) quest -Tier $(or $(TIER),any)

# pal tale [-Title <prefix>] -- pass TITLE="..." for a specific tale.
tale:
	@$(PAL) tale $(if $(TITLE),-Title "$(TITLE)")

# pal pack <subcommand> -- manage personality packs.
# Pass SUB=new (or others when added) plus the right args via ARGS=...
pack:
	@$(PAL) pack $(or $(SUB),new) $(ARGS)

# pal models [serving] -- pass SUB=serving plus ARGS=... for the live
# model-server checklist.
models:
	@$(PAL) models $(SUB) $(ARGS)

config:
	@$(PAL) config $(SUB) $(ARGS)

# pal benchmark -- real-world latency measurement against a running sidecar.
# Pass PROBES=N to override the default 10-probe sample size.
benchmark:
	@$(PAL) benchmark $(if $(PROBES),-Probes $(PROBES))

welcome:
	@$(PAL) welcome $(if $(QUICK),-Quick)

# pal next -- context-aware "what should I do right now?" advisor.
next:
	@$(PAL) next

# pal news -- show most recent CHANGELOG entry (or last N with COUNT=N).
news:
	@$(PAL) news $(if $(COUNT),-Count $(COUNT))

# pal harvest [list|show <name>] - browse harvestable units. Pass NAME="..."
# for show; default action is list.
harvest:
	@$(PAL) harvest $(or $(SUB),list) $(if $(NAME),"$(NAME)")

# pal health - write one Markdown + JSON health snapshot under artifacts/.
health:
	@$(PAL) health $(if $(JSON),-Json) $(if $(NOWRITE),-NoWrite)

# pal proof - read-only native proof status. Pass REQUIREPROVEN=1 to fail
# when delivery_proven evidence is missing.
proof:
	@$(PAL) proof $(if $(JSON),-Json) $(if $(REQUIREPROVEN),-RequireProven)

# pal logs - recent activity (launch evidence + native artifacts + audit).
logs:
	@$(PAL) logs $(if $(WHEREONLY),-WhereOnly)

preflight:
	@$(PAL) preflight

# pal patrol-report -- companion narrates the night they spent watching.
# Pass TITLE="..." for a specific report.
patrol-report:
	@$(PAL) patrol-report $(if $(TITLE),-Title "$(TITLE)")

readiness:
	@$(PAL) readiness

# pal where '<query>' -- natural-language file lookup. Pass QUERY="...".
where:
	@$(PAL) where $(QUERY)

# pal explain <path> -- structured explanation of a file or directory.
explain:
	@$(PAL) explain $(PATH_)

# pal mcp connect <client> -- wire PalLLM into an MCP client config.
# Pass CLIENT=vscode (or cursor / claude-desktop). Defaults to claude-desktop.
mcp:
	@$(PAL) mcp connect $(CLIENT)

# pal connect <target> -- wire PalLLM's inference path to a local engine.
# Pass TARGET=ollama (or vllm). Defaults to ollama.
connect:
	@$(PAL) connect $(or $(TARGET),ollama)

# pal support -- export an anonymized support bundle.
support:
	@$(PAL) support

check-updates:
	@$(PAL) check-updates

list:
	@$(PAL) list
