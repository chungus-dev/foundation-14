# Rule Authoring Policy

## Scope

This repository uses two rule trees:

- `.agents/rules` is the source of truth and stores full rule content.
- `.claude/rules` is the Claude Code compatibility layer.

## Required Rule

For every rule file in `.agents/rules/<rule-name>.md`, keep matching bridge files:

- `.claude/rules/<rule-name>.md`

When creating, updating, renaming, or deleting a rule in `.agents/rules`, apply the same bridge
change in `.claude/rules` in the same pull request.

## Bridge Contract

Each Claude bridge rule file must contain:

- `trigger`: synchronized copy of canonical trigger from `.agents/rules/<rule-name>.md`.
- A reference in the markdown body to `../../../.agents/rules/<rule-name>.md`.

## PR Checklist Gate

A PR is incomplete if any rule exists in `.agents/rules` without matching bridges in
`.claude/rules`.

Run this check before pushing:

`pwsh ./.agents/rules/check-rule-bridges.ps1`
