from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

from .constants import (
    DEFAULT_LOCALE_ROOT,
    DEFAULT_PROTOTYPES_ROOT,
    DEFAULT_PROTOTYPE_OUTPUT,
    DEFAULT_PROTOTYPE_STATE,
    DEFAULT_SOURCE_CULTURE,
    DEFAULT_TARGET_CULTURE,
)
from .filesystem import iter_files, read_text, remove_empty_files_and_dirs, write_text_if_changed
from .fluent import normalize_fluent_text
from .prototypes import build_entity_ftl, extract_entity_localizations, write_entity_ftl
from .strings import sync_locale_strings
from .validation import validate_locale


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="foundation-loc")
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    subparsers = parser.add_subparsers(dest="command", required=True)

    normalize = subparsers.add_parser("normalize")
    normalize.add_argument("--culture", default=DEFAULT_TARGET_CULTURE)
    normalize.add_argument("--locale-root", type=Path, default=DEFAULT_LOCALE_ROOT)
    normalize.add_argument("--dry-run", action="store_true")
    normalize.set_defaults(func=_normalize)

    extract = subparsers.add_parser("extract-prototypes")
    extract.add_argument("--culture", default=DEFAULT_TARGET_CULTURE)
    extract.add_argument("--locale-root", type=Path, default=DEFAULT_LOCALE_ROOT)
    extract.add_argument("--prototypes-root", type=Path, default=DEFAULT_PROTOTYPES_ROOT)
    extract.add_argument("--output", type=Path, default=DEFAULT_PROTOTYPE_OUTPUT)
    extract.add_argument("--state-output", type=Path, default=DEFAULT_PROTOTYPE_STATE)
    extract.add_argument("--dry-run", action="store_true")
    extract.set_defaults(func=_extract_prototypes)

    sync = subparsers.add_parser("sync-strings")
    sync.add_argument("--source-culture", default=DEFAULT_SOURCE_CULTURE)
    sync.add_argument("--target-culture", default=DEFAULT_TARGET_CULTURE)
    sync.add_argument("--locale-root", type=Path, default=DEFAULT_LOCALE_ROOT)
    sync.add_argument("--bidirectional", action="store_true")
    sync.add_argument("--dry-run", action="store_true")
    sync.set_defaults(func=_sync_strings)

    validate = subparsers.add_parser("validate")
    validate.add_argument("--source-culture", default=DEFAULT_SOURCE_CULTURE)
    validate.add_argument("--target-culture", default=DEFAULT_TARGET_CULTURE)
    validate.add_argument("--locale-root", type=Path, default=DEFAULT_LOCALE_ROOT)
    validate.add_argument("--fail-on-errors", action="store_true")
    validate.set_defaults(func=_validate)

    translate = subparsers.add_parser("translate")
    translate.add_argument("files", nargs="+", type=Path)
    translate.add_argument("--source-culture", default=DEFAULT_SOURCE_CULTURE)
    translate.add_argument("--target-culture", default=DEFAULT_TARGET_CULTURE)
    translate.add_argument("--locale-root", type=Path, default=DEFAULT_LOCALE_ROOT)
    translate.add_argument("--prototypes-root", type=Path, default=DEFAULT_PROTOTYPES_ROOT)
    translate.add_argument("--prototype-output", type=Path, default=DEFAULT_PROTOTYPE_OUTPUT)
    translate.add_argument("--prompt", type=Path)
    translate.add_argument("--glossary", type=Path, default=Path("Tools") / "Localization" / "glossary.md")
    translate.add_argument("--chunk-size", type=int, default=4000)
    translate.add_argument("--concurrency", type=int, default=2)
    translate.add_argument("--allow-partial", action="store_true")
    translate.add_argument("--report-json", type=Path)
    translate.add_argument("--dry-run", action="store_true")
    translate.set_defaults(func=_translate)

    args = parser.parse_args(argv)
    return args.func(args)


def _normalize(args: argparse.Namespace) -> int:
    root = args.repo_root / args.locale_root / args.culture
    changed = 0

    for path in iter_files(root, ".ftl"):
        normalized = normalize_fluent_text(read_text(path))
        if normalized:
            changed += 1 if write_text_if_changed(path, normalized, dry_run=args.dry_run) else 0
        else:
            changed += 1
            if not args.dry_run:
                path.unlink()

    removed_files, removed_dirs = remove_empty_files_and_dirs(root, dry_run=args.dry_run)
    print(f"normalized={changed} removed_files={removed_files} removed_dirs={removed_dirs}")
    return 0


def _extract_prototypes(args: argparse.Namespace) -> int:
    output = args.locale_root / args.culture / args.output
    state = args.locale_root / args.culture / args.state_output
    count, changed = write_entity_ftl(
        args.repo_root,
        args.prototypes_root,
        output,
        state_path=state,
        dry_run=args.dry_run,
    )
    print(f"entity_messages={count} changed={changed}")
    return 0


def _sync_strings(args: argparse.Namespace) -> int:
    locale_root = args.repo_root / args.locale_root
    result = sync_locale_strings(
        locale_root / args.source_culture,
        locale_root / args.target_culture,
        dry_run=args.dry_run,
    )

    if args.bidirectional:
        reverse = sync_locale_strings(
            locale_root / args.target_culture,
            locale_root / args.source_culture,
            dry_run=args.dry_run,
        )
        result = result + reverse

    print(
        f"scanned_files={result.scanned_files} changed_files={result.changed_files} "
        f"added_messages={result.added_messages}"
    )
    return 0


def _validate(args: argparse.Namespace) -> int:
    locale_root = args.repo_root / args.locale_root
    report = validate_locale(
        locale_root / args.source_culture,
        locale_root / args.target_culture,
    )

    print(
        "checked={checked} missing={missing} untranslated={untranslated} findings={findings}".format(
            checked=report.checked_messages,
            missing=report.missing_messages,
            untranslated=report.untranslated_messages,
            findings=len(report.findings),
        )
    )

    for finding in report.findings[:200]:
        print(f"{finding.level}: {finding.path}:{finding.message_id}: {finding.text}")

    if len(report.findings) > 200:
        print(f"... {len(report.findings) - 200} more findings omitted")

    return 1 if args.fail_on_errors and report.has_errors else 0


def _translate(args: argparse.Namespace) -> int:
    from .translate import build_translation_prompt, run_translate_files

    prompt_path = args.prompt
    if prompt_path is None:
        prompt_path = Path("Tools") / "Localization" / "prompts" / f"{args.target_culture}.md"

    prompt = build_translation_prompt(args.repo_root / prompt_path, args.repo_root / args.glossary)
    files = [args.repo_root / file for file in args.files]
    source_texts = _source_texts_for_translation(args, files)
    result = run_translate_files(
        files,
        prompt,
        args.chunk_size,
        source_texts,
        target_culture=args.target_culture,
        concurrency=args.concurrency,
        allow_partial=args.allow_partial,
        dry_run=args.dry_run,
    )
    print(
        f"translated_messages={result.translated_messages} changed_files={result.changed_files} "
        f"failed_files={len(result.failed_files)}"
    )

    for failed in result.failed_details:
        print(f"failed: {failed.path}: {failed.error}")

    if args.report_json is not None:
        report = {
            "translated_messages": result.translated_messages,
            "changed_files": result.changed_files,
            "failed_files": [
                {
                    "path": str(failed.path),
                    "translated_messages": failed.translated_messages,
                    "changed": failed.changed,
                    "error": failed.error,
                }
                for failed in result.failed_details
            ],
        }
        args.report_json.parent.mkdir(parents=True, exist_ok=True)
        args.report_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    return 0 if args.allow_partial or not result.failed_files else 1


def _source_texts_for_translation(args: argparse.Namespace, files: list[Path]) -> dict[Path, str]:
    locale_root = args.repo_root / args.locale_root
    source_root = locale_root / args.source_culture
    target_root = locale_root / args.target_culture
    result: dict[Path, str] = {}
    prototype_source_text: str | None = None

    for file in files:
        try:
            relative = file.relative_to(target_root)
        except ValueError:
            continue

        if relative == args.prototype_output:
            if prototype_source_text is None:
                entries = extract_entity_localizations(args.repo_root, args.prototypes_root)
                prototype_source_text = build_entity_ftl(entries)
            result[file] = prototype_source_text
            continue

        source_path = source_root / relative
        if source_path.exists():
            result[file] = read_text(source_path)

    return result


if __name__ == "__main__":
    sys.exit(main())
