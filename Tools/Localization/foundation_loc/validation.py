from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

from .filesystem import iter_files, read_text
from .fluent import attributes, functions, message_map, rich_tags, same_message_payload, variables


@dataclass(frozen=True)
class ValidationFinding:
    level: str
    path: Path
    message_id: str
    text: str


@dataclass
class ValidationReport:
    findings: list[ValidationFinding] = field(default_factory=list)
    checked_messages: int = 0
    missing_messages: int = 0
    untranslated_messages: int = 0

    @property
    def has_errors(self) -> bool:
        return any(finding.level == "error" for finding in self.findings)

    def add(self, level: str, path: Path, message_id: str, text: str) -> None:
        self.findings.append(ValidationFinding(level, path, message_id, text))


def validate_locale(source_root: Path, target_root: Path) -> ValidationReport:
    report = ValidationReport()

    for source_path in iter_files(source_root, ".ftl"):
        relative = source_path.relative_to(source_root)
        target_path = target_root / relative
        source_messages = message_map(read_text(source_path))
        target_messages = message_map(read_text(target_path)) if target_path.exists() else {}

        for message_id, source_message in source_messages.items():
            report.checked_messages += 1
            target_message = target_messages.get(message_id)
            if target_message is None:
                report.missing_messages += 1
                report.add("warning", relative, message_id, "missing target message")
                continue

            if same_message_payload(source_message, target_message):
                report.untranslated_messages += 1
                report.add("warning", relative, message_id, "target message is identical to source")

            source_vars = variables(source_message.text)
            target_vars = variables(target_message.text)
            if source_vars != target_vars:
                report.add(
                    "error",
                    relative,
                    message_id,
                    f"variable mismatch: source={sorted(source_vars)} target={sorted(target_vars)}",
                )

            source_tags = rich_tags(source_message.text)
            target_tags = rich_tags(target_message.text)
            if source_tags != target_tags:
                report.add(
                    "error",
                    relative,
                    message_id,
                    f"rich-text tag mismatch: source={dict(source_tags)} target={dict(target_tags)}",
                )

            source_attributes = attributes(source_message.text)
            target_attributes = attributes(target_message.text)
            if source_attributes != target_attributes:
                report.add(
                    "error",
                    relative,
                    message_id,
                    f"attribute mismatch: source={sorted(source_attributes)} target={sorted(target_attributes)}",
                )

            source_functions = functions(source_message.text)
            target_functions = functions(target_message.text)
            if source_functions != target_functions:
                report.add(
                    "error",
                    relative,
                    message_id,
                    f"function mismatch: source={sorted(source_functions)} target={sorted(target_functions)}",
                )

    return report
