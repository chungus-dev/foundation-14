# Foundation 14 Localization Tools

This directory contains the fork-owned localization pipeline. It is intentionally separate from game runtime code.

Foundation 14 is an SCP Foundation build on top of Space Station 14 technology and upstream content.
Fork-owned strings should use Foundation/SCP terminology. Inherited upstream strings may still describe a
station, crew, cargo, or station departments; those strings should keep their original context.

Common commands:

```bash
python Tools/Localization/run.py normalize
python Tools/Localization/run.py sync-strings --bidirectional
python Tools/Localization/run.py extract-prototypes
python Tools/Localization/run.py validate
```

`extract-prototypes` writes generated entity strings to
`Resources/Locale/<culture>/_Scp/Prototypes/entities.ftl` and records source hashes in
`entities.sources.json`. Existing translations are preserved while the source prototype text is unchanged;
changed source text is reset to English so the next translation run can update it. Generated prototype
messages that still match the source text are reset before extraction so the translator can pick them up.

`sync-strings --bidirectional` first copies missing source messages into the target culture, then copies
target-only messages back into the source culture. This keeps fork-owned string additions from existing in
only one locale.

Fork-owned localization files live under `Resources/Locale/<culture>/_Scp/`.
Single-line upstream edits may use `Scp edit`, and single-line additions may use `Scp added`.
Use `Scp edit start` / `Scp edit end` and `Scp added start` / `Scp added end` for multi-line edits/additions.

AI translation uses an OpenAI-compatible `/chat/completions` endpoint and reads secrets only from environment variables:

- `TRANSLATE_AI_BASE_URL`, comma- or newline-separated for provider rotation
- `TRANSLATE_AI_MODEL`, comma- or newline-separated for provider/model rotation
- `TRANSLATE_AI_KEYS`
- `TRANSLATE_AI_PROXIES` optional, comma- or newline-separated
- `TRANSLATE_AI_MAX_ATTEMPTS` optional, defaults to `0` for unlimited provider/key retry attempts
- `TRANSLATE_AI_MAX_OUTPUT_TOKENS` optional, defaults to `8192`
- `TRANSLATE_AI_RESPONSE_MAX_ATTEMPTS` optional, defaults to `0` for unlimited invalid-response retries
- `TRANSLATE_AI_RESPONSE_COOLDOWN_SECONDS` optional, defaults to `60`
- `LOCALIZATION_CHECKPOINT_FILE_BATCH_SIZE` optional in GitHub Actions, defaults to `10`

GitHub Actions defaults to the free, rate-limited GitHub Models endpoint:

```text
TRANSLATE_AI_BASE_URL=https://models.github.ai/inference
TRANSLATE_AI_MODEL=openai/gpt-4o-mini
TRANSLATE_AI_KEYS=${{ github.token }}
TRANSLATE_AI_MAX_ATTEMPTS=5
TRANSLATE_AI_RESPONSE_MAX_ATTEMPTS=2
```

The workflow uses `github.token` only with the GitHub Models endpoint. External providers require an explicit
`TRANSLATE_AI_KEYS` secret. For local free testing, create a GitHub PAT with the `models` scope and use it as
`TRANSLATE_AI_KEYS`.
OpenRouter free routing can also be used with `TRANSLATE_AI_BASE_URL=https://openrouter.ai/api/v1`,
the chosen OpenRouter free model ID in `TRANSLATE_AI_MODEL`, and an OpenRouter API key in
`TRANSLATE_AI_KEYS`.
For fully local inference, run Ollama or LM Studio and point `TRANSLATE_AI_BASE_URL` at their local `/v1` endpoint.

The AI provider is allowed to translate values only. File structure, Fluent message IDs, attributes, comments, placeholders, and final formatting are owned by these tools.
The translator rewrites messages that still match the source text and messages that are visibly in the wrong language for the target culture, including generated prototype messages.
Before new strings are synced and new prototype strings are extracted, the workflow translates already-existing
untranslated target/source locale messages so old untranslated content is handled first.
When `--allow-partial` is used, successful chunks are written immediately and failed files are left partially translated.
The next run skips already translated messages and continues from messages that still match the source.
The GitHub Actions workflow translates files in checkpointed batches and pushes each changed batch to the
automation branch. If a batch fails without making progress, the workflow stops later AI translation steps for
that run, keeps the pushed checkpoints, and the next run retries the remaining untranslated messages.
Checkpoint commits are split by final locale directory. Their subject uses
`Auto translate <directory> into <language>`, and the commit body lists the changed files under `Translated`.
