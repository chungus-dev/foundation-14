# Third-Party Notices

This project incorporates code, data, engine components, and assets from
third-party and upstream projects under their respective licenses.

These notices do not replace file-level license metadata. If a file, asset
metadata file, or third-party dependency declares a more specific license, that
specific license controls that material.

Foundation 14 original contributions are intended to be licensed under the
Reciprocal Public License 1.5 (`RPL-1.5`) unless a file or asset metadata file
states otherwise. See `LICENSE-NOTICE.md` and `LICENSES/RPL-1.5.txt`.

The `_Scp` directory convention is used to identify Foundation 14 fork-owned
code and data, but it is not the sole boundary of the license. Moving,
renaming, copying, vendoring, or embedding Foundation 14 original work outside
those directories does not change its license.

---

## Space Station 14 Content

This repository is derived from Space Station 14 content.

Copyright (c) 2017-2026 Space Wizards Federation

Upstream Space Station 14 content code is licensed under the MIT License:

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

Foundation 14 modifications and additions to upstream content files may be
licensed under `RPL-1.5` where permitted by the upstream license and where
marked as Foundation 14 work. The original upstream material remains under its
original license.

---

## RobustToolbox Engine

RobustToolbox is included as the game engine and keeps its own license notices.
See the license files and third-party notices inside `RobustToolbox/`,
including but not limited to:

- `RobustToolbox/LICENSE-MIT.TXT`
- `RobustToolbox/LICENSE-GPLv3.TXT`
- `RobustToolbox/LICENSE-ASSETS.TXT`

Bundled engine subprojects and third-party libraries inside `RobustToolbox/`
retain their own licenses and copyright notices.

---

## Game Assets

Game assets are mixed-license material. Sprite, sound, font, and other asset
licenses are declared by the asset metadata or adjacent license files. In
particular, `.rsi` sprite directories use `meta.json` fields such as
`license` and `copyright`.

Common asset licenses in this repository include, without limitation:

- `CC-BY-SA-3.0`
- `CC-BY-SA-4.0`
- `CC-BY-NC-SA-3.0`
- `CC0-1.0`
- `OFL-1.1`

The current RobustToolbox RSI schema restricts `.rsi/meta.json` license values
to known asset licenses such as Creative Commons variants and `CC0-1.0`. Do not
write `RPL-1.5` into an `.rsi/meta.json` unless the validator schema is
intentionally updated to allow it.

Original Foundation 14 RSI assets should use a validator-compatible asset
license such as `CC-BY-SA-4.0` unless the project deliberately updates the RSI
schema and tooling to accept `RPL-1.5`.

Assets derived from upstream or third-party Creative Commons ShareAlike
material must retain the appropriate ShareAlike license and attribution in
their metadata.

---

## SCP Foundation Material

Foundation 14 is an unofficial SCP-inspired project. This repository license
does not grant trademark rights or rights to third-party SCP Foundation
material.

Any direct SCP Wiki text, imagery, or other material incorporated into this
project must retain the applicable source license and attribution separately.
SCP Wiki material is commonly licensed under Creative Commons
Attribution-ShareAlike 3.0 (`CC-BY-SA-3.0`) and must be attributed to the wiki
and applicable authors where used. See the SCP Foundation licensing guide:
`https://scp-wiki.wikidot.com/licensing-guide`.

Do not treat SCP-derived material as Foundation 14-owned RPL material unless the
contributor separately owns the relevant rights and clearly licenses that
original contribution under RPL-1.5.

Keep SCP-derived material separately identifiable where practical. If a file or
asset metadata entry contains SCP-derived material, that file or metadata entry
must state the applicable source license and attribution.

---

## Other Third-Party Material

Some files may be derived from or reference other third-party projects such as
TGStation, Paradise, Citadel, CEV Eris, fonts, audio packs, or public-domain
resources. Those files retain the licenses and attribution stated in their
metadata, adjacent license files, or source comments.
