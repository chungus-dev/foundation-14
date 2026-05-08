#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from foundation_loc.cli import main


if __name__ == "__main__":
    raise SystemExit(main())
