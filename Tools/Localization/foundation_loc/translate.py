from __future__ import annotations

import asyncio
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re

from .ai import AiConfig, OpenAICompatibleClient
from .filesystem import read_text, write_text_if_changed
from .fluent import FluentMessage, attributes, functions, message_map, normalize_fluent_text, parse_messages, rich_tags, variables


@dataclass(frozen=True)
class TranslationRunResult:
    translated_messages: int
    changed_files: int
    failed_files: tuple[Path, ...] = ()


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
    concurrency: int = 4,
    *,
    allow_partial: bool = False,
    dry_run: bool = False,
) -> TranslationRunResult:
    client = OpenAICompatibleClient(AiConfig.from_env())
    semaphore = asyncio.Semaphore(max(1, concurrency))

    async def translate_one(path: Path) -> tuple[int, bool] | TranslationFileError:
        async with semaphore:
            try:
                return await translate_file(
                    path,
                    client,
                    prompt,
                    chunk_size,
                    source_text=source_texts.get(path) if source_texts else None,
                    target_culture=target_culture,
                    allow_partial=allow_partial,
                    dry_run=dry_run,
                )
            except TranslationFileError as error:
                return error
            except Exception as error:
                return TranslationFileError(path, 0, False, error)

    results = await asyncio.gather(*(translate_one(path) for path in files))
    translated = 0
    changed = 0
    failed: list[Path] = []

    for result in results:
        if isinstance(result, TranslationFileError):
            translated += result.translated_messages
            changed += 1 if result.changed else 0
            failed.append(result.path)
            continue

        count, did_change = result
        translated += count
        changed += 1 if did_change else 0

    return TranslationRunResult(translated, changed, tuple(failed))


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
    messages = _messages_to_translate(path, source_text, target_culture)
    translated_messages: dict[str, str] = {}
    changed = False

    for chunk in _chunks(messages, chunk_size):
        try:
            translated_messages.update(await _translate_chunk(client, prompt, chunk))
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
    concurrency: int = 4,
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


def _messages_to_translate(path: Path, source_text: str | None, target_culture: str | None) -> list[FluentMessage]:
    target_messages = message_map(read_text(path))
    if source_text is None:
        return [
            message
            for message in target_messages.values()
            if _should_translate_for_target_language(message, target_culture)
        ]

    source_messages = message_map(source_text)
    messages: list[FluentMessage] = []

    for message_id, target in target_messages.items():
        source = source_messages.get(message_id)
        if source is not None and target.text.strip() == source.text.strip():
            messages.append(target)
            continue

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
) -> dict[str, str]:
    payload = [{"id": message.id, "text": message.text} for message in chunk]
    expected = {message.id: message for message in chunk}
    last_error: Exception | None = None
    attempts = 0
    max_attempts = int(os.environ.get("TRANSLATE_AI_RESPONSE_MAX_ATTEMPTS", "0"))
    cooldown_seconds = int(os.environ.get("TRANSLATE_AI_RESPONSE_COOLDOWN_SECONDS", "60"))

    while max_attempts <= 0 or attempts < max_attempts:
        attempts += 1
        response = await client.chat([
            {"role": "system", "content": prompt},
            {"role": "user", "content": json.dumps(payload, ensure_ascii=False)},
        ])

        try:
            return _parse_translation_response(response, expected)
        except ValueError as error:
            last_error = error
            await asyncio.sleep(cooldown_seconds)

    raise TranslationValidationError("AI returned invalid translation repeatedly.") from last_error


def _parse_translation_response(response: str, expected: dict[str, FluentMessage]) -> dict[str, str]:
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

        text = item["text"].strip("\n")
        _validate_translated_message(expected[message_id], text)
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


def _should_translate_without_source(message: FluentMessage, target_culture: str | None) -> bool:
    return _should_translate_for_target_language(message, target_culture)


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
    visible = re.sub(r"\[[^\]]+\]", " ", visible)
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
        output.extend((replacements.get(message.id) or message.text).splitlines())
        cursor = message.end

    output.extend(lines[cursor:])
    return "\n".join(output)


class TranslationValidationError(ValueError):
    pass


class TranslationFileError(RuntimeError):
    def __init__(self, path: Path, translated_messages: int, changed: bool, error: Exception):
        super().__init__(f"{path}: {error}")
        self.path = path
        self.translated_messages = translated_messages
        self.changed = changed
        self.error = error
