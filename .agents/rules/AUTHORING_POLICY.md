# Rule Authoring Policy

## Scope

This repository uses `.agents/rules` as the only Codex-facing rule tree.

## Required Rule

Create, update, rename, and delete rules directly under `.agents/rules`.
Do not add mirrored copies for other agent clients.

## Rule Contract

Each rule file must contain valid YAML frontmatter with a `trigger` field, followed by the
markdown rule body. Keep always-on repository behavior in rules with `trigger: always_on`.
