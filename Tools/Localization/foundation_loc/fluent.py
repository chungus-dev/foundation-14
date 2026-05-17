from __future__ import annotations

from collections import Counter
from collections.abc import Callable
from dataclasses import dataclass
import re

from .constants import ZERO_WIDTH_SPACE


MESSAGE_START_RE = re.compile(r"^(?P<id>-?[A-Za-z][A-Za-z0-9_-]*)\s*=")
VARIABLE_RE = re.compile(r"\{\s*\$([A-Za-z][A-Za-z0-9_-]*)")
ATTRIBUTE_RE = re.compile(r"^\s+\.([A-Za-z][A-Za-z0-9_-]*)\s*=", re.MULTILINE)
FUNCTION_RE = re.compile(r"\{\s*([A-Z][A-Z0-9_-]*)\s*\(")
RICH_TAG_RE = re.compile(r"(?<!\\)\[(\/?)([A-Za-z][A-Za-z0-9_-]*)(?:[^\]]*)\]")
RICH_TAG_NAMES = {
    "bold",
    "bolditalic",
    "bullet",
    "center",
    "cmdlink",
    "color",
    "emoji",
    "font",
    "head",
    "italic",
    "keybind",
    "mono",
    "protodata",
    "scramble",
    "textlink",
}


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
    normalized = text.replace("\r\n", "\n")
    lines = normalized.split("\n")
    if normalized.endswith("\n"):
        lines = lines[:-1]

    lines = _trim_outer_blank_lines(lines)
    if not any(line.strip() for line in lines):
        return ""

    return "\n".join(escape_leading_multiline_markup_lines(lines)) + "\n"


def _trim_outer_blank_lines(lines: list[str]) -> list[str]:
    start = 0
    while start < len(lines) and not lines[start].strip():
        start += 1

    end = len(lines)
    while end > start and not lines[end - 1].strip():
        end -= 1

    leading = [""] if start > 0 else []
    trailing = [""] if end < len(lines) else []
    return leading + lines[start:end] + trailing


def escape_leading_multiline_markup_lines(lines: list[str]) -> list[str]:
    output = list(lines)
    in_multiline_pattern = False

    for index, line in enumerate(output):
        if _is_pattern_assignment(line):
            in_multiline_pattern = True
            continue

        if not in_multiline_pattern:
            continue

        if not line.strip():
            continue

        output[index] = escape_leading_markup_line(line)

    return output


def escape_leading_markup_line(line: str) -> str:
    stripped = line.lstrip()
    if stripped.startswith(ZERO_WIDTH_SPACE):
        return line

    match = RICH_TAG_RE.match(stripped)
    if match is None or match.group(2).lower() not in RICH_TAG_NAMES:
        return line

    leading_len = len(line) - len(stripped)
    return line[:leading_len] + ZERO_WIDTH_SPACE + stripped


def _is_pattern_assignment(line: str) -> bool:
    return MESSAGE_START_RE.match(line) is not None or ATTRIBUTE_RE.match(line) is not None


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


def strip_rich_tags(text: str) -> str:
    return RICH_TAG_RE.sub(" ", text)


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
    lines.extend(render_pattern(f"{message_id} =", _entity_name(name or "")))

    if description:
        lines.extend(render_pattern("  .desc =", _capitalize_value(description)))

    if suffix:
        lines.extend(render_pattern("  .suffix =", _capitalize_value(suffix)))

    return "\n".join(lines)


def escape_leading_multiline_markup(value: str) -> str:
    if "\n" not in value:
        return value

    return "\n".join(escape_leading_markup_line(line) for line in value.split("\n"))


def normalize_entity_message_style(text: str) -> str:
    parsed = parse_messages(text)
    if len(parsed) != 1 or not parsed[0].id.startswith("ent-"):
        return text

    lines = list(parsed[0].lines)
    if lines:
        lines[0] = _replace_assignment_value(lines[0], _entity_name)

    for index, line in enumerate(lines):
        if re.match(r"^\s+\.(?:desc|suffix)\s*=", line):
            lines[index] = _replace_assignment_value(line, _capitalize_value)

    return "\n".join(lines)


def _replace_assignment_value(line: str, transform: Callable[[str], str]) -> str:
    if "=" not in line:
        return line

    prefix, value = line.split("=", 1)
    separator = " " if value.startswith(" ") else ""
    stripped = value[1:] if value.startswith(" ") else value
    return f"{prefix}={separator}{transform(stripped)}".rstrip()


def _entity_name(value: str) -> str:
    if _starts_with_fluent_syntax(value):
        return value

    return value.lower()


def _capitalize_value(value: str) -> str:
    if _starts_with_fluent_syntax(value):
        return value

    for index, char in enumerate(value):
        if char.isalpha():
            return value[:index] + char.upper() + value[index + 1:]

    return value


def _starts_with_fluent_syntax(value: str) -> bool:
    return value.lstrip().startswith(("{", "["))
