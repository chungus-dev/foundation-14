# Skill Authoring Policy

## Scope

This repository uses `.agents/skills` as the only Codex-facing skill tree.

## Required Rule

Create, update, rename, and delete skills directly under `.agents/skills/<skill-name>`.
Do not add mirrored copies for other agent clients.

## Skill Contract

Each skill directory must contain a `SKILL.md` file with valid YAML frontmatter:

- `name`: exact `<skill-name>` folder name in hyphen-case.
- `description`: concise trigger text that explains when Codex should load the skill.

Keep detailed guidance in the markdown body or referenced files under the skill directory.
