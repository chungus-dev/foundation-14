from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
import re

from .constants import ZERO_WIDTH_SPACE


MESSAGE_START_RE = re.compile(r"^(?P<id>-?[A-Za-z][A-Za-z0-9_-]*)\s*=")
VARIABLE_RE = re.compile(r"\{\s*\$([A-Za-z][A-Za-z0-9_-]*)")
ATTRIBUTE_RE = re.compile(r"^\s+\.([A-Za-z][A-Za-z0-9_-]*)\s*=", re.MULTILINE)
FUNCTION_RE = re.compile(r"\b([A-Z][A-Z0-9_-]*)\s*\(")
RICH_TAG_RE = re.compile(r"\[(\/?)([A-Za-z][A-Za-z0-9_-]*)(?:[^\]]*)\]")


@dataclass(frozen=True)
class FluentMessage:
    id: str
    start: int
    end: int
    lines: tuple[str, ...]

    @property
    def text(self) -> str:
        return "\n".join(self.lines)


def parse_messages(text: str) -> list[FluentMessage]:
    lines = text.replace("\r\n", "\n").split("\n")
    messages: list[FluentMessage] = []
    current_id: str | None = None
    current_start = 0

    for index, line in enumerate(lines):
        match = MESSAGE_START_RE.match(line)
        if match is None:
            continue

        if current_id is not None:
            messages.append(FluentMessage(current_id, current_start, index, tuple(lines[current_start:index])))

        current_id = match.group("id")
        current_start = index

    if current_id is not None:
        end = len(lines)
        while end > current_start and lines[end - 1] == "":
            end -= 1
        messages.append(FluentMessage(current_id, current_start, end, tuple(lines[current_start:end])))

    return messages


def message_map(text: str) -> dict[str, FluentMessage]:
    return {message.id: message for message in parse_messages(text)}


def normalize_fluent_text(text: str) -> str:
    stripped = text.replace("\r\n", "\n").strip("\n")
    if not stripped.strip():
        return ""

    return stripped + "\n"


def variables(text: str) -> set[str]:
    return set(VARIABLE_RE.findall(text))


def attributes(text: str) -> set[str]:
    return set(ATTRIBUTE_RE.findall(text))


def functions(text: str) -> set[str]:
    return set(FUNCTION_RE.findall(text))


def rich_tags(text: str) -> Counter[str]:
    tags: Counter[str] = Counter()
    for closing, tag in RICH_TAG_RE.findall(text):
        tags[f"/{tag}" if closing else tag] += 1
    return tags


def same_message_payload(left: FluentMessage, right: FluentMessage) -> bool:
    return _canonical_message(left.text) == _canonical_message(right.text)


def _canonical_message(text: str) -> str:
    return "\n".join(line.rstrip() for line in text.strip().splitlines())


def render_pattern(prefix: str, value: str) -> list[str]:
    prepared = escape_leading_multiline_markup(value)
    lines = prepared.splitlines() or [""]
    if len(lines) == 1:
        return [f"{prefix} {lines[0]}".rstrip()]

    return [prefix] + [f"    {line}" for line in lines]


def render_entity_message(message_id: str, name: str | None, description: str | None, suffix: str | None) -> str:
    lines: list[str] = []
    lines.extend(render_pattern(f"{message_id} =", name or ""))

    if description:
        lines.extend(render_pattern("  .desc =", description))

    if suffix:
        lines.extend(render_pattern("  .suffix =", suffix))

    return "\n".join(lines)


def escape_leading_multiline_markup(value: str) -> str:
    if "\n" not in value:
        return value

    stripped = value.lstrip()
    if not stripped.startswith("[") or stripped.startswith(ZERO_WIDTH_SPACE):
        return value

    leading_len = len(value) - len(stripped)
    return value[:leading_len] + ZERO_WIDTH_SPACE + stripped
