# Architecture

## Components

### CinDa.DaWatcha.Core

Owns configuration validation, atomic UUID updates, file watching,
process identity verification, polling, event deduplication, batching,
and passalong-message construction. It contains no browser or WPF code.

### CinDa.DaWatcha.App

Provides the WPF dashboard, Windows UI Automation completion detector,
handoff coordinator, and Selenium-controlled Firefox session.

### CinDa.DaWatcha.BrowserSmoke

Launches the browser controller with a disposable profile to verify
Selenium, GeckoDriver resolution, Firefox startup, and clean shutdown.

## Browser invariant

The application owns one Firefox process, one window, one tab, and one
persistent profile. Every conversation change navigates that same tab.
Unexpected tabs are closed before each browser operation.

## State progression

```text
Watching -> Completion/Exit -> Batched by UUID -> Preparing
Preparing -> Awaiting manual Send -> Browser verification
Browser verification -> User confirmation -> Completed
```

A conversation at or above the byte limit changes the preparation target
to a new chat. The JSON UUID is not changed until the manually sent
message produces a valid `/c/{uuid}` URL.

## Safety invariants

- A PID is actionable only after its complete fingerprint matches.
- A process run produces no more than one handoff.
- Configuration writes use a same-directory temporary file and atomic
  replacement.
- Self-generated UUID writes are suppressed from hot-reload recursion.
- The application never invokes ChatGPT's Send control.
- Different conversation UUIDs are never mixed into one message.
