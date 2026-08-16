# Architecture

## Trust boundary

The job ledger and delivery-state ledger are local files. Process checks use
Windows APIs through .NET. ChatGPT interaction occurs only through Selenium in
a normal visible Firefox window. Production navigation is hard-coded to
`https://chatgpt.com/c/{canonical-uuid}` with no query or fragment; the watched
file cannot substitute a host, path, query, script, or command.

There is no OpenAI API client, generic HTTP client, CLI integration, email, or
webhook in the runtime.

## Components

`CinDa.DaWatcha.Core` strictly parses the ledger, resolves every configured file
path inside the ledger's directory, validates the contract,
fingerprints processes, evaluates group state, constructs deterministic
messages, persists delivery state atomically, and coordinates retries.

`CinDa.DaWatcha.App` is the single-instance WPF dashboard and Firefox DOM
controller. The controller owns one automation tab in a non-private window,
verifies the exact
conversation route, waits for stable idle state, refuses to overwrite unrelated
composer text, clicks Send once per attempt, and verifies the entire user bubble.

`CinDa.DaWatcha.BrowserSmoke` drives the real Firefox controller against a local
test page. The alternate origin is internal and test-assembly-only; production
code always uses ChatGPT.

## State flow

```text
job ledger -> strict reload -> group/process evaluation
                             | blocked/stale/unresponsive
                             +--------------------------> warning
                             | all terminal + all exited
                             +--------------------------> final summary

message -> durable queue -> exact UUID -> wait idle -> prepare -> send -> verify
                              ^              retry <= configured attempts |
                              | refresh once + one final attempt           |
                              +---------- manual fallback <----------------+
```

## Invariants

- A job route is bound durably on its first queued message. Later UUID changes
  for that job are quarantined as conflicts.
- A process matches only when PID, normalized name, ledger-root-resolved
  executable path, and start time match. PID reuse is not accepted.
- Ledger file paths must be relative and remain inside the directory containing
  `watch-config.json`; absolute and parent-directory paths are rejected.
- A final message is produced only after every participant is terminal and no
  matching participant process remains.
- Delivery IDs and message hashes are deterministic. Restarting does not erase
  delivered state.
- Before every Send click, the controller checks whether the complete message
  already exists in the exact target conversation.
- A marker alone is not proof of delivery; the whole normalized message must be
  present in a user-authored bubble.
- The application never writes the job ledger. External writers may replace it
  atomically without a suppression window that could hide their edits.
