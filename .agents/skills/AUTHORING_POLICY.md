# Skill Authoring Policy

## Scope

This repository uses two skill trees:

- `.agents/skills` is the source of truth and stores full skill content and resources.
- `.claude/skills` is the Claude Code compatibility layer.

## Required Rule

For every skill in `.agents/skills/<skill-name>`, keep matching bridge files:

- `.claude/skills/<skill-name>/SKILL.md`

When creating, updating, renaming, or deleting a skill in `.agents/skills`, apply the same bridge
change in `.claude/skills` in the same pull request.

## Bridge Contract

Each Claude bridge SKILL file must contain:

- `name`: exact `<skill-name>` folder name in hyphen-case.
- `description`: synchronized copy of canonical description from `.agents/skills/<skill-name>/SKILL.md`.
- A reference in the markdown body to `../../../.agents/skills/<skill-name>/SKILL.md`.

## PR Checklist Gate

A PR is incomplete if any skill exists in `.agents/skills` without matching bridges in
`.claude/skills`.

Run this check before pushing:

`pwsh ./.agents/skills/check-skill-bridges.ps1`
