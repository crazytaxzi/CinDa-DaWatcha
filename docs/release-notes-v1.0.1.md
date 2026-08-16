# CinDa-DaWatcha v1.0.1

Portability and Firefox-window correction.

## Changes

- `watch-config.json` now lives beside the executable by default.
- `geckoDriverPath`, `deliveryStatePath`, and every participant
  `executablePath` use `.\` paths resolved from the selected ledger directory.
- Absolute paths, missing `.\` prefixes, and `..` parent segments are rejected.
- Firefox is discovered from its standard Windows installation.
- Firefox opens as a visible non-private window without CinDa-DaWatcha adding
  `-no-remote`, private-window, or custom-profile switches.
- The exact UUID, idle-wait, deduplication, automatic Send, full-message
  verification, retry/refresh, and manual fallback rules are unchanged.

## Verification performed

- Release build with warnings treated as errors.
- 31 passing strict-JSON, schema, path-rooting, atomic-reload, state-machine,
  retry, persistence, integrity, and deduplication tests.
- Real visible Firefox DOM smoke test using the packaged GeckoDriver: stable-idle
  wait, exactly one Send click, full-message verification, duplicate suppression,
  unrelated-draft protection, and exact-route enforcement.
- Direct and transitive NuGet audit with no vulnerable or deprecated packages.
