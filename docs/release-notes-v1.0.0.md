# CinDa-DaWatcha v1.0.0

First production release of the local Windows training-run handoff monitor.

## What is included

- Strict, hot-reloaded JSON job ledger for application groups.
- Full PID/name/path/start-time fingerprint verification.
- Pending, running, blocked, succeeded, and failed participant protocol.
- One locked-app warning per participant process run.
- Final success/failure barrier only after every expected application is
  terminal and every matching process is closed.
- Immutable delivery routing to the ChatGPT UUID that initiated the job.
- Durable delivery IDs, route binding, content hashes, restart recovery, and
  conflict quarantine.
- Dedicated Firefox profile with exact-origin and exact-UUID enforcement.
- Stable-idle detection, automatic Send, complete-message verification, normal
  retries, one refresh recovery attempt, and a clearly labeled manual fallback.
- Self-contained Windows x64 runtime and pinned GeckoDriver v0.37.1. No `winget`,
  .NET installation, Selenium Manager download, OpenAI API, email, webhook, or
  runtime CLI checking/sending is used.

## Install

1. Download `CinDa-DaWatcha-v1.0.0-win-x64.zip` and its `.sha256` file.
2. Verify the ZIP SHA-256, then extract the complete directory to a writable
   location such as `%LOCALAPPDATA%\Programs\CinDa-DaWatcha`.
3. Run `CinDa.DaWatcha.App.exe`.
4. Start monitoring, choose **Open Firefox / Sign in**, and sign in to ChatGPT in
   the dedicated profile.
5. Populate the created job ledger using `watch-config.example.json` and the
   bundled manual.

Mozilla Firefox itself must already be installed at the configured path.

## Verification performed

- Release build with warnings treated as errors.
- 26 unit, state-machine, strict-JSON, schema, atomic-reload, retry, persistence,
  integrity, and deduplication tests.
- Real Firefox DOM smoke test against a local fixture: generation wait, exact
  composer verification, one Send click, complete user-bubble verification,
  already-delivered suppression, unrelated-draft protection, exact-route
  enforcement, and clean shutdown.
- Direct and transitive NuGet vulnerability/deprecation audit.

The test count above is the intended release gate; release automation must fail
if any test or browser assertion fails.
