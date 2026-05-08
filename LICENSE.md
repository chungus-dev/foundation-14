# Foundation 14 Licensing

Foundation 14 is distributed as a mixed-license project.

Unless a file, directory notice, asset metadata file, or third-party notice
states otherwise, original Foundation 14 contributions are licensed under the
Reciprocal Public License 1.5 (`RPL-1.5`). See `LICENSES/RPL-1.5.txt`.
The Foundation 14 RPL notice is provided in `LICENSE-NOTICE.md`.

This repository also contains upstream and third-party material that remains
under its original license. See `THIRD-PARTY-NOTICES.md`.

## Foundation 14 Original Work

The RPL-1.5 default applies to original Foundation 14 work, including code,
tools, scripts, prototypes, localization, configuration, and other required
game data created for this fork.

Foundation 14 fork-owned code and data should normally live under `_Scp`
directories, for example:

- `Content.Client/_Scp/`
- `Content.Server/_Scp/`
- `Content.Shared/_Scp/`
- `Resources/Prototypes/_Scp/`
- `Resources/Locale/*/_Scp/`
- `Resources/Textures/_Scp/`

Files under these directories are treated as Foundation 14 original work and
are licensed under RPL-1.5 unless the file or asset metadata states otherwise.
Per-file SPDX headers are not required by project convention for these
fork-owned directories. This convention does not waive any RPL notice
obligations for downstream distributors.

The `_Scp` directory convention is an ownership convention, not a loophole.
Moving, renaming, copying, vendoring, or embedding Foundation 14 original work
outside `_Scp` directories does not change its license.

For purposes of RPL-1.5 Required Components, Foundation 14 original prototypes,
maps, localization, configuration, scripts, schemas, and control files required
to install, build, host, or run Foundation 14 modifications are part of the
corresponding Foundation 14 Extension.

Deploying a modified Foundation 14 build, server, packaged client, or hosted
game service triggers the RPL-1.5 source-availability obligations for the
Foundation 14 Extensions that were Deployed.

If Foundation 14 original source is copied or distributed outside this
repository layout, preserve `LICENSE.md`, `LICENSE-NOTICE.md`,
`LICENSES/RPL-1.5.txt`, and `THIRD-PARTY-NOTICES.md`, or provide equivalent
notices where recipients are likely to find them.

Distributors of standalone Foundation 14 source files should include the RPL
license notice in or with those files as required by RPL-1.5.

## Contributions

By submitting a contribution to Foundation 14, the contributor represents that
they have the right to submit it and agrees that the contribution is licensed
under the license that applies to the material they add or modify.

Original Foundation 14 contributions are licensed under RPL-1.5 unless a file,
asset metadata file, or notice states otherwise. Contributions to upstream or
third-party material must preserve the applicable upstream license and
attribution notices.

## Upstream and Modified Upstream Files

Unmodified upstream Space Station 14, RobustToolbox, and third-party files
retain their original licenses.

When Foundation 14 changes an upstream file, only the Foundation 14
modifications are intended to be licensed under RPL-1.5 where permitted by the
upstream license. Such changes should be marked near the changed block when the
file format allows comments.

Use lowercase marker text for upstream files:

- `scp edit start` / `scp edit end` for modified upstream blocks.
- `scp added start` / `scp added end` for new Foundation 14 blocks added to an
  upstream file.
- `scp edit:` for a small single-line change where a start/end block would add
  noise.

Removing an `scp edit` or `scp added` marker does not remove the RPL-1.5
license from the underlying Foundation 14 modification.

Do not remove or replace existing upstream copyright, attribution, or license
notices.

## Assets

Asset metadata controls asset licensing.

For `.rsi` sprites, the authoritative license and attribution fields are in the
directory's `meta.json`. Existing upstream and third-party assets keep the
license declared in their metadata.

Important: the current RobustToolbox RSI schema restricts `meta.json` license
values to known asset licenses such as Creative Commons variants and `CC0-1.0`.
Do not write `RPL-1.5` into an `.rsi/meta.json` unless the validator schema is
intentionally updated to allow it.

For original Foundation 14 RSI assets, prefer a validator-compatible asset
license such as `CC-BY-SA-4.0` unless the project deliberately updates the RSI
schema and tooling to accept `RPL-1.5`.

Assets derived from Creative Commons ShareAlike material must retain the
appropriate ShareAlike license and attribution.

## SCP Foundation Material

Foundation 14 is an unofficial SCP-inspired project. This repository license
does not grant trademark rights or rights to third-party SCP Foundation
material.

Any direct SCP Wiki text, imagery, or other material incorporated into this
project must retain the applicable source license and attribution separately.
SCP-derived material must not be treated as Foundation 14-owned RPL material
unless the contributor separately owns the relevant rights and clearly licenses
that original contribution under RPL-1.5.

Keep SCP-derived material in separately identifiable files or metadata where
practical. If a file contains SCP-derived material, that file must clearly state
the applicable SCP source license and attribution instead of relying on the
repository's RPL default.
