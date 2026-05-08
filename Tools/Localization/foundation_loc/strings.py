from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .filesystem import iter_files, read_text, write_text_if_changed
from .fluent import message_map, normalize_fluent_text


@dataclass(frozen=True)
class SyncResult:
    scanned_files: int
    changed_files: int
    added_messages: int


def sync_locale_strings(source_root: Path, target_root: Path, dry_run: bool = False) -> SyncResult:
    scanned = 0
    changed = 0
    added = 0

    for source_path in iter_files(source_root, ".ftl"):
        scanned += 1
        relative = source_path.relative_to(source_root)
        target_path = target_root / relative
        source_text = read_text(source_path)

        if target_path.exists():
            target_text = read_text(target_path)
            merged, added_count = _merge_missing_messages(source_text, target_text, relative)
        else:
            merged = source_text
            added_count = len(message_map(source_text))

        if added_count == 0:
            continue

        added += added_count
        changed += 1 if write_text_if_changed(target_path, normalize_fluent_text(merged), dry_run=dry_run) else 0

    return SyncResult(scanned, changed, added)


def _merge_missing_messages(source_text: str, target_text: str, relative: Path) -> tuple[str, int]:
    source_messages = message_map(source_text)
    target_messages = message_map(target_text)
    missing = [message for message_id, message in source_messages.items() if message_id not in target_messages]

    if not missing:
        return target_text, 0

    output = target_text.rstrip("\n").splitlines()
    if output:
        output.append("")
        output.append("")

    output.append(f"### Missing from {relative.as_posix()}")
    for message in missing:
        output.append("")
        output.extend(message.lines)

    return "\n".join(output), len(missing)
