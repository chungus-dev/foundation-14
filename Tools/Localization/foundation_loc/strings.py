from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .filesystem import iter_files, read_text, write_text_if_changed
from .fluent import FluentMessage, message_map, normalize_fluent_text


@dataclass(frozen=True)
class SyncResult:
    scanned_files: int
    changed_files: int
    added_messages: int

    def __add__(self, other: SyncResult) -> SyncResult:
        return SyncResult(
            self.scanned_files + other.scanned_files,
            self.changed_files + other.changed_files,
            self.added_messages + other.added_messages,
        )


@dataclass(frozen=True)
class PrepareTargetFilesResult:
    target_files: tuple[Path, ...]
    prepared_files: int
    skipped_existing_messages: int
    dry_run_missing_files: int


def sync_locale_strings(source_root: Path, target_root: Path, dry_run: bool = False) -> SyncResult:
    scanned = 0
    changed = 0
    added = 0
    known_target_messages = _locale_message_ids(target_root)

    for source_path in iter_files(source_root, ".ftl"):
        scanned += 1
        relative = source_path.relative_to(source_root)
        target_path = target_root / relative
        source_text = read_text(source_path)
        missing = _messages_missing_from_locale(source_text, known_target_messages)

        if not missing:
            continue

        merged = _merge_missing_messages(
            read_text(target_path) if target_path.exists() else "",
            missing,
            relative,
        )
        added_count = len(missing)

        added += added_count
        changed += 1 if write_text_if_changed(target_path, normalize_fluent_text(merged), dry_run=dry_run) else 0
        known_target_messages.update(message.id for message in missing)

    return SyncResult(scanned, changed, added)


def prepare_target_files(
    source_culture_root: Path,
    target_culture_root: Path,
    relative_roots: list[Path],
    dry_run: bool = False,
) -> PrepareTargetFilesResult:
    known_target_messages = _locale_message_ids(target_culture_root)
    target_files: list[Path] = []
    prepared_files = 0
    skipped_existing_messages = 0
    dry_run_missing_files = 0

    for relative_root in relative_roots:
        source_root = source_culture_root / relative_root
        target_root = target_culture_root / relative_root

        for source_path in iter_files(source_root, ".ftl"):
            relative = source_path.relative_to(source_root)
            target_path = target_root / relative
            missing = _messages_missing_from_locale(read_text(source_path), known_target_messages)
            target_exists = target_path.exists()

            if missing:
                merged = _merge_missing_messages(
                    read_text(target_path) if target_exists else "",
                    missing,
                    relative,
                )
                if write_text_if_changed(target_path, normalize_fluent_text(merged), dry_run=dry_run):
                    prepared_files += 1

                known_target_messages.update(message.id for message in missing)
            elif not target_exists:
                skipped_existing_messages += 1

            if dry_run and not target_exists:
                dry_run_missing_files += 1
                continue

            if target_path.exists() or missing:
                target_files.append(target_path)

    return PrepareTargetFilesResult(
        tuple(sorted(target_files)),
        prepared_files,
        skipped_existing_messages,
        dry_run_missing_files,
    )


def filter_messages_missing_from_locale(source_text: str, target_locale_root: Path) -> list[FluentMessage]:
    return _messages_missing_from_locale(source_text, _locale_message_ids(target_locale_root))


def write_missing_messages_for_file(
    source_path: Path,
    target_path: Path,
    target_locale_root: Path,
    relative: Path,
    dry_run: bool = False,
) -> tuple[int, bool]:
    missing = filter_messages_missing_from_locale(read_text(source_path), target_locale_root)
    if not missing:
        return 0, False

    merged = _merge_missing_messages(
        read_text(target_path) if target_path.exists() else "",
        missing,
        relative,
    )
    changed = write_text_if_changed(target_path, normalize_fluent_text(merged), dry_run=dry_run)
    return len(missing), changed


def _locale_message_ids(root: Path) -> set[str]:
    ids: set[str] = set()
    for path in iter_files(root, ".ftl"):
        ids.update(message_map(read_text(path)))
    return ids


def _messages_missing_from_locale(source_text: str, known_target_messages: set[str]) -> list[FluentMessage]:
    return [
        message
        for message_id, message in message_map(source_text).items()
        if message_id not in known_target_messages
    ]


def _merge_missing_messages(target_text: str, missing: list[FluentMessage], relative: Path) -> str:
    output = target_text.rstrip("\n").splitlines()
    if output:
        output.append("")
        output.append("")

    output.append(f"### Missing from {relative.as_posix()}")
    for message in missing:
        output.append("")
        output.extend(message.lines)

    return "\n".join(output)
