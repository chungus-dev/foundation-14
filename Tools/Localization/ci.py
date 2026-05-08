#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


LOCALE_PATH = "Resources/Locale"


def main() -> int:
    parser = argparse.ArgumentParser(prog="localization-ci")
    subparsers = parser.add_subparsers(dest="command", required=True)

    checkpoint = subparsers.add_parser("checkpoint")
    checkpoint.add_argument("--label", required=True)
    checkpoint.add_argument("--branch", required=True)
    checkpoint.add_argument("--base", required=True)
    checkpoint.set_defaults(func=_checkpoint)

    translate = subparsers.add_parser("translate-batches")
    translate.add_argument("files", nargs="*", type=Path)
    translate.add_argument("--stage", required=True)
    translate.add_argument("--source-culture", required=True)
    translate.add_argument("--target-culture", required=True)
    translate.add_argument("--branch", required=True)
    translate.add_argument("--base", required=True)
    translate.add_argument("--batch-size", type=int, default=_env_int("LOCALIZATION_CHECKPOINT_FILE_BATCH_SIZE", 10))
    translate.add_argument("--chunk-size", type=int, default=7000)
    translate.add_argument("--concurrency", type=int, default=4)
    translate.set_defaults(func=_translate_batches)

    args = parser.parse_args()
    return args.func(args)


def _checkpoint(args: argparse.Namespace) -> int:
    _checkpoint_changes(args.branch, args.base, args.label)
    return 0


def _translate_batches(args: argparse.Namespace) -> int:
    files = [Path(file) for file in args.files]
    if not files:
        _append_summary(f"- {args.stage}: no files found.")
        return 0

    batch_size = max(1, args.batch_size)
    total_batches = (len(files) + batch_size - 1) // batch_size
    _append_summary(f"### {args.stage}")
    _append_summary(f"- files: {len(files)}")
    _append_summary(f"- batch size: {batch_size}")

    for batch_index, start in enumerate(range(0, len(files), batch_size), start=1):
        batch = files[start:start + batch_size]
        report_path = _report_path(args.stage, batch_index)
        command = [
            sys.executable,
            "Tools/Localization/run.py",
            "translate",
            "--source-culture",
            args.source_culture,
            "--target-culture",
            args.target_culture,
            "--allow-partial",
            "--report-json",
            str(report_path),
            "--chunk-size",
            str(args.chunk_size),
            "--concurrency",
            str(args.concurrency),
            *[str(file) for file in batch],
        ]

        print(
            f"Running {args.stage} batch {batch_index}/{total_batches} "
            f"with {len(batch)} file(s).",
            flush=True,
        )
        completed = subprocess.run(command)
        report = _read_report(report_path)
        failed_files = report.get("failed_files", [])

        _append_summary(
            "- {stage} batch {index}/{total}: files={files} translated_messages={translated} "
            "changed_files={changed} failed_files={failed}".format(
                stage=args.stage,
                index=batch_index,
                total=total_batches,
                files=len(batch),
                translated=report.get("translated_messages", 0),
                changed=report.get("changed_files", 0),
                failed=len(failed_files),
            )
        )

        if failed_files:
            for failed in failed_files[:10]:
                _append_summary(f"  - failed: `{failed.get('path')}`: {failed.get('error')}")
            if len(failed_files) > 10:
                _append_summary(f"  - ... {len(failed_files) - 10} more failed files")

        _checkpoint_changes(
            args.branch,
            args.base,
            f"{args.stage} batch {batch_index}/{total_batches}",
        )

        if completed.returncode != 0:
            return completed.returncode

        if _should_stop_ai(report):
            reason = (
                f"{args.stage} batch {batch_index}/{total_batches} produced no progress "
                f"and failed {len(failed_files)} file(s). Stopping AI translation for this run."
            )
            _request_ai_stop(reason)
            return 0

    return 0


def _checkpoint_changes(branch: str, base: str, label: str) -> bool:
    if not _has_locale_changes():
        _append_summary(f"- {label}: no localization changes to checkpoint.")
        return False

    _run(["git", "add", LOCALE_PATH])

    diff = subprocess.run(["git", "diff", "--cached", "--quiet", "--", LOCALE_PATH])
    if diff.returncode == 0:
        _append_summary(f"- {label}: no staged localization changes to checkpoint.")
        return False
    if diff.returncode != 1:
        diff.check_returncode()

    _run(["git", "commit", "-m", "Automate localization update"])
    _run(["git", "push", "--set-upstream", "origin", branch])
    _ensure_pull_request(branch, base, label)
    _append_summary(f"- {label}: checkpoint pushed.")
    return True


def _ensure_pull_request(branch: str, base: str, label: str) -> None:
    if not (os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")):
        _append_summary(f"- {label}: skipped PR update because no GitHub token is available.")
        return

    existing = subprocess.run(
        ["gh", "pr", "list", "--head", branch, "--state", "open", "--json", "number", "--jq", ".[0].number // empty"],
        check=True,
        text=True,
        capture_output=True,
    ).stdout.strip()

    workflow_id = os.environ.get("GITHUB_RUN_ID", "local")
    body = f"Generated by Localization Automation workflow {workflow_id}.\n\nLast checkpoint: {label}."

    if existing:
        _run(["gh", "pr", "edit", existing, "--title", "Automated localization update", "--body", body, "--base", base])
        return

    _run([
        "gh",
        "pr",
        "create",
        "--title",
        "Automated localization update",
        "--body",
        body,
        "--base",
        base,
        "--head",
        branch,
    ])


def _read_report(path: Path) -> dict[str, object]:
    if not path.exists():
        return {
            "translated_messages": 0,
            "changed_files": 0,
            "failed_files": [],
        }

    return json.loads(path.read_text(encoding="utf-8"))


def _should_stop_ai(report: dict[str, object]) -> bool:
    failed_files = report.get("failed_files", [])
    return (
        isinstance(failed_files, list)
        and len(failed_files) > 0
        and report.get("translated_messages", 0) == 0
        and report.get("changed_files", 0) == 0
    )


def _request_ai_stop(reason: str) -> None:
    print(f"::warning::{reason}", flush=True)
    _append_summary(f"- AI translation stopped: {reason}")
    _write_github_env("LOCALIZATION_AI_STOP", "1")

    stop_file = os.environ.get("LOCALIZATION_AI_STOP_FILE")
    if stop_file:
        Path(stop_file).write_text(reason, encoding="utf-8")


def _has_locale_changes() -> bool:
    status = subprocess.run(
        ["git", "status", "--porcelain", "--", LOCALE_PATH],
        check=True,
        text=True,
        capture_output=True,
    ).stdout
    return bool(status.strip())


def _report_path(stage: str, batch_index: int) -> Path:
    slug = re.sub(r"[^A-Za-z0-9_.-]+", "-", stage).strip("-").lower() or "translation"
    root = Path(os.environ.get("RUNNER_TEMP", tempfile.gettempdir()))
    return root / f"localization-{slug}-{batch_index}.json"


def _append_summary(line: str) -> None:
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if not summary:
        return

    with Path(summary).open("a", encoding="utf-8") as file:
        file.write(line)
        file.write("\n")


def _write_github_env(name: str, value: str) -> None:
    env_path = os.environ.get("GITHUB_ENV")
    if not env_path:
        return

    with Path(env_path).open("a", encoding="utf-8") as file:
        file.write(f"{name}={value}\n")


def _run(command: list[str]) -> None:
    print("+ " + " ".join(command), flush=True)
    subprocess.run(command, check=True)


def _env_int(name: str, default: int) -> int:
    value = os.environ.get(name)
    if value is None or not value.strip():
        return default

    return int(value)


if __name__ == "__main__":
    raise SystemExit(main())
