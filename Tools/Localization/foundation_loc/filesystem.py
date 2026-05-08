from __future__ import annotations

import os
from pathlib import Path
import shutil
import tempfile


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def write_text_if_changed(path: Path, text: str, dry_run: bool = False) -> bool:
    normalized = text.replace("\r\n", "\n")
    if path.exists() and read_text(path) == normalized:
        return False

    if dry_run:
        return True

    path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path: Path | None = None

    try:
        with tempfile.NamedTemporaryFile(
            "w",
            delete=False,
            dir=path.parent,
            encoding="utf-8",
            newline="\n",
        ) as temporary:
            temporary.write(normalized)
            temporary_path = Path(temporary.name)

        os.replace(temporary_path, path)
    finally:
        if temporary_path is not None and temporary_path.exists():
            temporary_path.unlink()

    return True


def iter_files(root: Path, suffix: str) -> list[Path]:
    if not root.exists():
        return []

    return sorted(path for path in root.rglob(f"*{suffix}") if path.is_file())


def remove_empty_files_and_dirs(root: Path, dry_run: bool = False) -> tuple[int, int]:
    removed_files = 0
    removed_dirs = 0

    if not root.exists():
        return removed_files, removed_dirs

    for path in sorted(root.rglob("*"), key=lambda item: len(item.parts), reverse=True):
        if path.is_file() and path.stat().st_size == 0:
            removed_files += 1
            if not dry_run:
                path.unlink()

    for path in sorted((p for p in root.rglob("*") if p.is_dir()), key=lambda item: len(item.parts), reverse=True):
        if any(path.iterdir()):
            continue

        removed_dirs += 1
        if not dry_run:
            path.rmdir()

    return removed_files, removed_dirs


def remove_tree_if_empty(root: Path, dry_run: bool = False) -> bool:
    if not root.exists() or any(root.iterdir()):
        return False

    if not dry_run:
        shutil.rmtree(root)

    return True
