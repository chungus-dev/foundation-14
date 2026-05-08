---
trigger: always_on
---

# Rule: Defining the Scp codebase prefix, project folder and edit markers

This rule is mandatory for any task in this SS14 fork.

## 1. What you need to determine before starting work

Before analyzing, planning paths and making changes, always fix three values:

1. Active codebase prefix: `Scp`.
2. Forked project folder: `_Scp`.
3. Edit markers: `Scp edit` and `Scp added`.

Don't start editing vanilla files until these three values ​​are defined.

## 2. Fixed fork values

For this repository, do not infer the fork from a GitHub organization, remote owner, repository
name, copied upstream content, or historical markers. The active fork is always:

| Prefix | Project folder | Single-line marker | Block markers |
| --- | --- | --- | --- |
| `Scp` | `_Scp` | `Scp edit`, `Scp added` | `Scp edit start/end`, `Scp added start/end` |

If nearby code still contains historical markers from another fork, do not copy that style for new
work. Use `Scp` markers for all new edits.

## 3. How to apply a marker in a specific file

The same general rules apply in any file:

1. Use the fixed `Scp` prefix, `_Scp` project folder and `Scp` markers.
2. Do not change the marker text, just adapt the comment syntax to the file language.
3. For a one-line fork edit, use a single-line marker.
4. For a multi-line fork edit, use matching `start` and `end` block markers.

Select the comment syntax to match the file language:

- C#, C++, Java: `// Scp edit - reason`, `// Scp added start - reason`
- YAML, FTL, Python, Shell: `# Scp edit - reason`, `# Scp added start - reason`
- XML, HTML: `<!-- Scp edit - reason -->`, only if comments in this format are allowed and really needed

## 4. How does this affect the structure of edits?

1. Place all new fork-owned files in `_Scp`.
2. Mark minimal hooks in vanilla files with `Scp` markers.
3. Do not put new fork-owned code outside `_Scp` only because the original code was copied from elsewhere.
4. Do not introduce markers from other forks in new edits.
