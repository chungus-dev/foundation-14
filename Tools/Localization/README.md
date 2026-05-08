# Foundation 14 Localization Tools

This directory contains the fork-owned localization pipeline. It is intentionally separate from game runtime code.

Foundation 14 is an SCP Foundation build on top of Space Station 14 technology and upstream content.
Fork-owned strings should use Foundation/SCP terminology. Inherited upstream strings may still describe a
station, crew, cargo, or station departments; those strings should keep their original context.

Common commands:

```bash
python Tools/Localization/run.py normalize
python Tools/Localization/run.py sync-strings
python Tools/Localization/run.py extract-prototypes
python Tools/Localization/run.py validate
```

`extract-prototypes` writes generated entity strings to `Resources/Locale/<culture>/_prototypes/entities.ftl`
and records source hashes in `entities.sources.json`. Existing translations are preserved while the source
prototype text is unchanged; changed source text is reset to English so the next translation run can update it.

AI translation uses an OpenAI-compatible `/chat/completions` endpoint and reads secrets only from environment variables:

- `F14_AI_BASE_URL`
- `F14_AI_MODEL`
- `F14_AI_KEYS`
- `F14_AI_PROXIES` optional, comma- or newline-separated
- `F14_AI_MAX_ATTEMPTS` optional; set `0` for unlimited provider/key retry attempts

GitHub Actions defaults to the free, rate-limited GitHub Models endpoint:

```text
F14_AI_BASE_URL=https://models.github.ai/inference
F14_AI_MODEL=openai/gpt-4o-mini
F14_AI_KEYS=${{ secrets.GITHUB_TOKEN }}
```

For local free testing, create a GitHub PAT with the `models` scope and use it as `F14_AI_KEYS`.
OpenRouter free routing can also be used with `F14_AI_BASE_URL=https://openrouter.ai/api/v1`,
`F14_AI_MODEL=openrouter/free`, and an OpenRouter API key in `F14_AI_KEYS`.
For fully local inference, run Ollama or LM Studio and point `F14_AI_BASE_URL` at their local `/v1` endpoint.

The AI provider is allowed to translate values only. File structure, Fluent message IDs, attributes, comments, placeholders, and final formatting are owned by these tools.
The translator only rewrites messages that still match the source text, including generated prototype messages.
When `--allow-partial` is used, successful chunks are written immediately and failed files are left partially translated.
The next run skips already translated messages and continues from messages that still match the source.
