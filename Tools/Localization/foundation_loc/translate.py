from __future__ import annotations

import asyncio
from dataclasses import dataclass
import json
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

        new_text = _replace_messages(text, translated_messages)
        changed = write_text_if_changed(path, normalize_fluent_text(new_text), dry_run=dry_run) or changed

    if not translated_messages:
        return 0, False

    return len(translated_messages), changed


def run_translate_files(
    files: list[Path],
    prompt: str,
    chunk_size: int,
    source_texts: dict[Path, str] | None = None,
    target_culture: str | None = None,
    concurrency: int = 4,
    dry_run: bool = False,
) -> TranslationRunResult:
    return asyncio.run(translate_files(
        files,
        prompt,
        chunk_size,
        source_texts=source_texts,
        target_culture=target_culture,
        concurrency=concurrency,
        dry_run=dry_run,
    ))


def _messages_to_translate(path: Path, source_text: str | None, target_culture: str | None) -> list[FluentMessage]:
    target_messages = message_map(read_text(path))
    if source_text is None:
        return [
            message
            for message in target_messages.values()
            if _should_translate_without_source(message, target_culture)
        ]

    source_messages = message_map(source_text)
    return [
        target
        for message_id, target in target_messages.items()
        if message_id in source_messages and target.text.strip() == source_messages[message_id].text.strip()
    ]


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
    attempts: int = 3,
) -> dict[str, str]:
    payload = [{"id": message.id, "text": message.text} for message in chunk]
    expected = {message.id: message for message in chunk}
    last_error: Exception | None = None

    for _ in range(attempts):
        response = await client.chat([
            {"role": "system", "content": prompt},
            {"role": "user", "content": json.dumps(payload, ensure_ascii=False)},
        ])

        try:
            return _parse_translation_response(response, expected)
        except ValueError as error:
            last_error = error

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


def _should_translate_without_source(message: FluentMessage, target_culture: str | None) -> bool:
    if target_culture is None:
        return False

    if target_culture.lower().startswith("ru"):
        return CYRILLIC_RE.search(message.text) is None

    return False


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
