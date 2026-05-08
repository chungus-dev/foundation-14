from __future__ import annotations

from pathlib import Path


DEFAULT_SOURCE_CULTURE = "en-US"
DEFAULT_TARGET_CULTURE = "ru-RU"
DEFAULT_LOCALE_ROOT = Path("Resources") / "Locale"
DEFAULT_PROTOTYPES_ROOT = Path("Resources") / "Prototypes" / "Entities"
DEFAULT_PROTOTYPE_OUTPUT = Path("_prototypes") / "entities.ftl"
DEFAULT_PROTOTYPE_STATE = Path("_prototypes") / "entities.sources.json"
ZERO_WIDTH_SPACE = "\u200b"
