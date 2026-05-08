# Foundation 14 Licensing

Foundation 14 is distributed as a mixed-license project.

Unless a file, directory notice, asset metadata file, or third-party notice
states otherwise, original Foundation 14 contributions are licensed under the
Reciprocal Public License 1.5 (`RPL-1.5`). See `LICENSES/RPL-1.5.txt`.

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
Per-file SPDX headers are not required for these fork-owned directories.

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
