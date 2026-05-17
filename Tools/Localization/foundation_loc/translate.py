from __future__ import annotations

import asyncio
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import sys

from .ai import AiConfig, OpenAICompatibleClient
from .filesystem import read_text, write_text_if_changed
from .fluent import FluentMessage, attributes, functions, message_map, normalize_entity_message_style, normalize_fluent_text, parse_messages, rich_tags, strip_rich_tags, variables


@dataclass(frozen=True)
class TranslationFailure:
    path: Path
    translated_messages: int
    changed: bool
    error: str


@dataclass(frozen=True)
class TranslationRunResult:
    translated_messages: int
    changed_files: int
    failed_files: tuple[Path, ...] = ()
    failed_details: tuple[TranslationFailure, ...] = ()


@dataclass
class TranslationProgress:
    total: int
    root: Path
    completed: int = 0
    active: tuple[Path, ...] = ()
    last_status_length: int = 0

    def started(self, path: Path) -> None:
        self.active = (*self.active, path)
        self.render()

    def finished(self, path: Path) -> None:
        self.completed += 1
        self.active = tuple(active_path for active_path in self.active if active_path != path)
        self.render()

    def render(self) -> None:
        status = _progress_status(self.completed, self.total, self.active, self.root)
        padding = " " * max(0, self.last_status_length - len(status))
        print(f"\r{status}{padding}", end="", file=sys.stderr, flush=True)
        self.last_status_length = len(status)

    def finish(self) -> None:
        print(file=sys.stderr, flush=True)


def build_translation_prompt(prompt_path: Path, glossary_path: Path | None) -> str:
    prompt = read_text(prompt_path)
    if glossary_path is None or not glossary_path.exists():
        return prompt

    return f"{prompt.rstrip()}\n\nProject glossary:\n{read_text(glossary_path).strip()}\n"


async def translate_files(
    files: list[Path],
    prompt: str,
    chunk_size: int,
    source_texts: dict[Path, str] | None = None,
    target_culture: str | None = None,
    concurrency: int = 2,
    *,
    allow_partial: bool = False,
    dry_run: bool = False,
) -> TranslationRunResult:
    pending_files: list[Path] = []
    preflight_errors: list[TranslationFileError] = []

    for path in files:
        try:
            text = read_text(path)
            source_text = source_texts.get(path) if source_texts else None
            if _messages_to_translate(text, source_text, target_culture):
                pending_files.append(path)
        except Exception as error:
            preflight_errors.append(TranslationFileError(path, 0, False, error))

    if not pending_files:
        return TranslationRunResult(
            0,
            0,
            tuple(error.path for error in preflight_errors),
            tuple(TranslationFailure(
                path=error.path,
                translated_messages=error.translated_messages,
                changed=error.changed,
                error=_format_translation_error(error.error),
            ) for error in preflight_errors),
        )

    skipped_files = len(files) - len(pending_files) - len(preflight_errors)
    if skipped_files > 0:
        print(f"skipped_translated_files={skipped_files}", file=sys.stderr, flush=True)

    client = OpenAICompatibleClient(AiConfig.from_env())
    semaphore = asyncio.Semaphore(max(1, concurrency))
    progress = TranslationProgress(total=len(pending_files), root=_common_path_root(pending_files))

    async def translate_one(path: Path) -> tuple[int, bool] | TranslationFileError:
        async with semaphore:
            progress.started(path)
            try:
                result = await translate_file(
                    path,
                    client,
                    prompt,
                    chunk_size,
                    source_text=source_texts.get(path) if source_texts else None,
                    target_culture=target_culture,
                    allow_partial=allow_partial,
                    dry_run=dry_run,
                )
                return result
            except TranslationFileError as error:
                return error
            except Exception as error:
                return TranslationFileError(path, 0, False, error)
            finally:
                progress.finished(path)

    translation_results = await asyncio.gather(*(translate_one(path) for path in pending_files))
    progress.finish()
    results = [*preflight_errors, *translation_results]
    translated = 0
    changed = 0
    failed: list[Path] = []
    failed_details: list[TranslationFailure] = []

    for result in results:
        if isinstance(result, TranslationFileError):
            translated += result.translated_messages
            changed += 1 if result.changed else 0
            failed.append(result.path)
            failed_details.append(TranslationFailure(
                path=result.path,
                translated_messages=result.translated_messages,
                changed=result.changed,
                error=_format_translation_error(result.error),
            ))
            continue

        count, did_change = result
        translated += count
        changed += 1 if did_change else 0

    return TranslationRunResult(translated, changed, tuple(failed), tuple(failed_details))


async def translate_file(
    path: Path,
    client: OpenAICompatibleClient,
    prompt: str,
    chunk_size: int,
    source_text: str | None = None,
    target_culture: str | None = None,
    *,
    allow_partial: bool = False,
    dry_run: bool = False,
) -> tuple[int, bool]:
    text = read_text(path)
    messages = _messages_to_translate(text, source_text, target_culture)
    translated_messages: dict[str, str] = {}
    changed = False

    for chunk in _chunks(messages, chunk_size):
        try:
            translated_messages.update(await _translate_chunk(client, prompt, chunk, target_culture))
        except Exception as error:
            raise TranslationFileError(path, len(translated_messages), changed, error) from error

        if allow_partial:
            new_text = _replace_messages(text, translated_messages)
            changed = write_text_if_changed(path, normalize_fluent_text(new_text), dry_run=dry_run) or changed

    if not translated_messages:
        return 0, False

    if not allow_partial:
        new_text = _replace_messages(text, translated_messages)
        changed = write_text_if_changed(path, normalize_fluent_text(new_text), dry_run=dry_run) or changed

    return len(translated_messages), changed


def run_translate_files(
    files: list[Path],
    prompt: str,
    chunk_size: int,
    source_texts: dict[Path, str] | None = None,
    target_culture: str | None = None,
    concurrency: int = 2,
    *,
    allow_partial: bool = False,
    dry_run: bool = False,
) -> TranslationRunResult:
    return asyncio.run(translate_files(
        files,
        prompt,
        chunk_size,
        source_texts=source_texts,
        target_culture=target_culture,
        concurrency=concurrency,
        allow_partial=allow_partial,
        dry_run=dry_run,
    ))


def _messages_to_translate(text: str, source_text: str | None, target_culture: str | None) -> list[FluentMessage]:
    target_messages = message_map(text)
    if source_text is None:
        return [
            message
            for message in target_messages.values()
            if _should_translate_for_target_language(message, target_culture)
        ]

    messages: list[FluentMessage] = []
    for target in target_messages.values():
        if _should_translate_for_target_language(target, target_culture):
            messages.append(target)

    return messages


def _chunks(messages: list[FluentMessage], chunk_size: int) -> list[list[FluentMessage]]:
    chunks: list[list[FluentMessage]] = []
    current: list[FluentMessage] = []
    current_size = 0

    for message in messages:
        size = len(message.text)
        if current and current_size + size > chunk_size:
            chunks.append(current)
            current = []
            current_size = 0

        current.append(message)
        current_size += size

    if current:
        chunks.append(current)

    return chunks


async def _translate_chunk(
    client: OpenAICompatibleClient,
    prompt: str,
    chunk: list[FluentMessage],
    target_culture: str | None,
) -> dict[str, str]:
    payload = [{"id": message.id, "text": message.text} for message in chunk]
    expected = {message.id: message for message in chunk}
    last_error: Exception | None = None
    last_response: str | None = None
    attempts = 0
    max_attempts = int(os.environ.get("TRANSLATE_AI_RESPONSE_MAX_ATTEMPTS", "3"))
    cooldown_seconds = int(os.environ.get("TRANSLATE_AI_RESPONSE_COOLDOWN_SECONDS", "0"))

    # max_attempts==0 (via TRANSLATE_AI_RESPONSE_MAX_ATTEMPTS) means unlimited invalid-response retries.
    while max_attempts <= 0 or attempts < max_attempts:
        attempts += 1
        response = await client.chat([
            {"role": "system", "content": prompt},
            {"role": "user", "content": json.dumps(payload, ensure_ascii=False)},
        ])

        try:
            return _parse_translation_response(response, expected, target_culture)
        except ValueError as error:
            if isinstance(error, TranslationValidationError):
                error.ai_response = response
            last_error = error
            last_response = response
            _print_ai_response_error(response)
            if max_attempts <= 0 or attempts < max_attempts:
                max_attempts_text = "unlimited" if max_attempts <= 0 else str(max_attempts)
                print(
                    "AI translation response retry: "
                    f"attempt={attempts}/{max_attempts_text} "
                    f"cooldown_seconds={cooldown_seconds} "
                    f"reason={error}",
                    file=sys.stderr,
                    flush=True,
                )
                await asyncio.sleep(cooldown_seconds)

    raise TranslationValidationError(
        "AI returned invalid translation repeatedly.",
        ai_response=last_response,
    ) from last_error


def _parse_translation_response(
    response: str,
    expected: dict[str, FluentMessage],
    target_culture: str | None,
) -> dict[str, str]:
    data = json.loads(_strip_json_fence(response))
    if not isinstance(data, list):
        raise TranslationValidationError("AI translation response must be a JSON list.")

    result: dict[str, str] = {}
    for item in data:
        if not isinstance(item, dict) or not isinstance(item.get("id"), str) or not isinstance(item.get("text"), str):
            raise TranslationValidationError("AI translation response contains an invalid item.")

        message_id = item["id"]
        if message_id not in expected:
            raise TranslationValidationError(f"AI returned unexpected message id: {message_id}")

        text = normalize_entity_message_style(item["text"].strip("\n"))
        _validate_translated_message(expected[message_id], text)
        _validate_target_language(message_id, text, target_culture)
        result[message_id] = text

    missing = set(expected) - set(result)
    if missing:
        raise TranslationValidationError(f"AI response is missing message ids: {sorted(missing)}")

    return result


def _validate_translated_message(source: FluentMessage, translated: str) -> None:
    parsed = parse_messages(translated)
    if len(parsed) != 1 or parsed[0].id != source.id:
        raise TranslationValidationError(f"AI changed Fluent message structure for {source.id}.")

    if variables(source.text) != variables(translated):
        raise TranslationValidationError(f"AI changed Fluent variables for {source.id}.")

    if rich_tags(source.text) != rich_tags(translated):
        raise TranslationValidationError(f"AI changed rich-text tags for {source.id}.")

    if attributes(source.text) != attributes(translated):
        raise TranslationValidationError(f"AI changed Fluent attributes for {source.id}.")

    if functions(source.text) != functions(translated):
        raise TranslationValidationError(f"AI changed Fluent functions for {source.id}.")

    if _comments(source.text) != _comments(translated):
        raise TranslationValidationError(f"AI changed comments for {source.id}.")


def _validate_target_language(message_id: str, text: str, target_culture: str | None) -> None:
    if target_culture is None:
        return

    visible = _user_visible_text(text)

    if target_culture.lower().startswith("en") and CYRILLIC_RE.search(visible) is not None:
        raise TranslationValidationError(f"AI left Cyrillic text in English translation for {message_id}.")

    if target_culture.lower().startswith("ru") and _contains_untranslated_english(visible):
        raise TranslationValidationError(f"AI left English text in Russian translation for {message_id}.")


def _comments(text: str) -> list[str]:
    return [line for line in text.splitlines() if line.lstrip().startswith("#")]


CYRILLIC_RE = re.compile(r"[\u0400-\u04ff]")
LATIN_WORD_RE = re.compile(r"[A-Za-z][A-Za-z'-]*")
PROTECTED_LATIN_PHRASE_RE = re.compile(
    r"\b(?:Foundation\s*14|Space\s+Station\s*14|D-?Class|Class-D|SCP-\d+)\b",
    re.IGNORECASE,
)
ANGLE_TAG_RE = re.compile(r"</?[^>\s]+(?:\s+[^>]*)?>")
PRESERVED_LATIN_TERMS = {
    "ai",
    "apc",
    "dna",
    "gps",
    "hud",
    "id",
    "ids",
    "nt",
    "pda",
    "scp",
    "ui",
}


def _should_translate_for_target_language(message: FluentMessage, target_culture: str | None) -> bool:
    if target_culture is None:
        return False

    text = _user_visible_text(message.text)

    if target_culture.lower().startswith("ru"):
        return _contains_untranslated_english(text)

    if target_culture.lower().startswith("en"):
        return CYRILLIC_RE.search(text) is not None

    return False


def _contains_untranslated_english(text: str) -> bool:
    tokens = _english_residue_tokens(text)
    if not tokens:
        return False

    if CYRILLIC_RE.search(text) is None:
        return True

    return True


def _english_residue_tokens(text: str) -> list[str]:
    text = PROTECTED_LATIN_PHRASE_RE.sub(" ", text)
    result: list[str] = []

    for match in LATIN_WORD_RE.finditer(text):
        token = match.group(0).strip("'-")
        if not token:
            continue

        normalized = token.lower()
        if len(normalized) < 3 or normalized in PRESERVED_LATIN_TERMS or token.isupper():
            continue

        result.append(normalized)

    return result


def _user_visible_text(text: str) -> str:
    values: list[str] = []
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue

        values.append(line.split("=", 1)[1] if "=" in line else line)

    visible = "\n".join(values)
    visible = re.sub(r"\{[^}]*\}", " ", visible)
    visible = strip_rich_tags(visible)
    return ANGLE_TAG_RE.sub(" ", visible)


def _strip_json_fence(response: str) -> str:
    stripped = response.strip()
    if not stripped.startswith("```"):
        return stripped

    lines = stripped.splitlines()
    if len(lines) >= 3 and lines[-1].strip() == "```":
        return "\n".join(lines[1:-1]).strip()

    return stripped


def _replace_messages(text: str, replacements: dict[str, str]) -> str:
    lines = text.replace("\r\n", "\n").split("\n")
    messages = message_map(text)
    output: list[str] = []
    cursor = 0

    for message in sorted(messages.values(), key=lambda item: item.start):
        output.extend(lines[cursor:message.start])
        replacement = replacements.get(message.id)
        if replacement is None:
            output.extend(message.text.splitlines())
        else:
            output.extend(replacement.splitlines())
            output.extend(_trailing_blank_lines(message.lines))
        cursor = message.end

    output.extend(lines[cursor:])
    return "\n".join(output)


def _trailing_blank_lines(lines: tuple[str, ...]) -> list[str]:
    result: list[str] = []
    for line in reversed(lines):
        if line.strip():
            break

        result.append(line)

    result.reverse()
    return result


def _progress_status(completed: int, total: int, active: tuple[Path, ...], root: Path) -> str:
    width = 24
    ratio = 1 if total <= 0 else completed / total
    filled = int(width * ratio)
    bar = "#" * filled + "-" * (width - filled)
    percent = int(ratio * 100)
    active_text = _format_paths(active, root)
    return f"Translation {completed}/{total} [{bar}] {percent:3d}% | Active: {active_text}"


def _format_paths(paths: tuple[Path, ...], root: Path) -> str:
    if not paths:
        return "idle"

    return " | ".join(_short_path(path, root) for path in paths)


def _short_path(path: Path, root: Path) -> str:
    try:
        return str(path.relative_to(root))
    except ValueError:
        return str(path)


def _common_path_root(paths: list[Path]) -> Path:
    try:
        return Path(os.path.commonpath([str(path.parent) for path in paths]))
    except ValueError:
        return Path.cwd()


def _print_ai_response_error(response: str) -> None:
    print("\nai_response_begin", file=sys.stderr, flush=True)
    print(response, file=sys.stderr, flush=True)
    print("ai_response_end", file=sys.stderr, flush=True)


def _format_translation_error(error: Exception) -> str:
    message = str(error)
    if isinstance(error, TranslationValidationError) and error.ai_response is not None:
        return f"{message}\nAI response:\n{error.ai_response}"

    return message


class TranslationValidationError(ValueError):
    def __init__(self, message: str, ai_response: str | None = None):
        super().__init__(message)
        self.ai_response = ai_response


class TranslationFileError(RuntimeError):
    def __init__(self, path: Path, translated_messages: int, changed: bool, error: Exception):
        super().__init__(f"{path}: {error}")
        self.path = path
        self.translated_messages = translated_messages
        self.changed = changed
        self.error = error
