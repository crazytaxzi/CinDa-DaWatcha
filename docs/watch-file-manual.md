# Job Ledger Instruction Manual

This is the normative contract for the JSON file watched by CinDa-DaWatcha.
`MUST` and `MUST NOT` are requirements. The companion
[JSON Schema](watch-config.schema.json) describes the same document shape.

## Purpose and ownership

The ledger is shared local state between a training launcher, each participating
application, and CinDa-DaWatcha. It records which ChatGPT conversation started a
job, the exact processes in its application group, their heartbeats and outcomes,
and the handoff content they produced.

Any authorized local participant may edit the ledger. CinDa-DaWatcha watches and
reads it but never rewrites it. Its own durable delivery history is stored at
`settings.deliveryStatePath`, not in this file.

No value is executed as a command, script, template, URL, or API call. The only
remote destination is hard-coded in the application as
`https://chatgpt.com/c/{initiatingChatUuid}` and is opened by Firefox.

## File rules

The document MUST:

- Be UTF-8 JSON containing one top-level object.
- Contain exactly `settings` and `jobs` at the top level.
- Use the documented camelCase spelling; names are case-sensitive.
- Contain no comments, trailing commas, duplicate names, or unknown properties.
- Double backslashes in Windows paths.
- Express every file path from the ledger root as `.\\required-item`; absolute
  paths and `..` parent-directory escapes are forbidden.
- Use canonical hyphenated UUIDs and UTC timestamps ending in `Z`.

CinDa-DaWatcha strictly validates every reload. A rejected edit is displayed and
the most recent valid in-memory copy stays active.

## Safe concurrent updates

Writers MUST use atomic replacement:

1. Read and parse the current complete document.
2. Preserve settings, jobs, participants, and fields they do not own.
3. Modify the intended fields in memory.
4. Update the participant and job `updatedAtUtc` values as applicable.
5. Validate and serialize to a temporary file in the same directory.
6. Flush and close the temporary file.
7. Replace the selected ledger with one rename.
8. Read it back and verify the intended update survived.

Do not write a partial document directly to the watched path. Concurrent writers
need a shared lock or compare-and-retry protocol; atomic replacement prevents
partial reads but cannot merge two simultaneous edits.

## Settings

Every setting is required.

| Field | Valid range or rule | Default |
|---|---|---:|
| `pollIntervalMs` | 250–60000 | 1000 |
| `heartbeatStaleMs` | 1000–86400000 | 120000 |
| `conversationIdlePollMs` | 250–10000 | 1000 |
| `conversationIdleStablePolls` | 2–30 | 3 |
| `conversationIdleTimeoutMs` | 10000–3600000 | 900000 |
| `sendVerificationTimeoutMs` | 5000–300000 | 45000 |
| `automaticSendAttempts` | 1–5 | 3 |
| `geckoDriverPath` | Ledger-root-relative path ending `geckodriver.exe` | `.\geckodriver.exe` |
| `deliveryStatePath` | Ledger-root-relative file path | `.\delivery-state.json` |

Firefox is discovered from its normal Windows installation and is not configured
in the ledger. CinDa-DaWatcha launches a visible, non-private Firefox window and
does not pass private-window, `-no-remote`, or custom-profile switches.

`automaticSendAttempts` is the number of ordinary attempts. After those fail,
the program refreshes the same conversation once and performs one final automatic
attempt. It then prepares a manual fallback.

The idle settings require the exact conversation to remain non-generating and
structurally stable for several polls. If unrelated text is already in the
composer, delivery stops instead of overwriting it.

## Jobs

Each object in `jobs` has these fields:

| Field | Meaning |
|---|---|
| `id` | Stable, nonblank, case-insensitively unique job ID |
| `enabled` | Whether CinDa-DaWatcha evaluates the job |
| `initiatingChatUuid` | UUID supplied by the ChatGPT conversation that launched the job |
| `recoveryChatUuid` | Optional UUID an operator can open for recovery help; never a delivery target |
| `createdAtUtc` | Job creation time in UTC |
| `updatedAtUtc` | Most recent job update time in UTC |
| `summary` | Human-readable job purpose |
| `participants` | One or more applications in the completion barrier |

The launcher MUST record `initiatingChatUuid` when it creates the job. It MUST be
copied from the initiating conversation's `/c/{uuid}` URL. It MUST NOT be guessed,
reused from another job, or changed to redirect a pending handoff.

At the first queued warning or final message, CinDa-DaWatcha durably binds
`job.id` to that initiating UUID. A later conflicting route is quarantined and
shown in the dashboard. `recoveryChatUuid` only enables an operator-facing
**Open recovery UUID** button; no warning or handoff is sent there.

Job IDs MUST remain stable for the life of the job. Reusing an old ID for a new
run retains its delivery binding and deduplication history and is therefore
forbidden.

## Participants

Each participant has:

- `id`: stable, nonblank, case-insensitively unique within the job.
- `process`: the exact process fingerprint described below.
- `state`: `Pending`, `Running`, `Blocked`, `Succeeded`, or `Failed`.
- `updatedAtUtc`: UTC timestamp for the participant record.
- `heartbeatUtc`: required for `Pending` and `Running`; refresh it while alive.
- `finishedAtUtc`: required for `Succeeded` and `Failed`.
- `exitCode`: optional process exit code when the writer can obtain it.
- `detail`: current status or concise outcome description.
- `handoffMessage`: required and nonblank for `Succeeded` and `Failed`.

The participant writer owns its state, heartbeat, detail, and handoff. It SHOULD
refresh `heartbeatUtc` substantially faster than `heartbeatStaleMs`. A heartbeat
at or beyond that age produces one locked-app warning even if the process is
still alive.

`Blocked` explicitly requests a locked-app warning. A matching GUI process whose
main window is not responding is also treated as blocked. The warning asks the
initiating UUID to unstick the application without launching a duplicate run.

## Process fingerprint

`process` contains all four required fields:

```json
{
  "pid": 12345,
  "name": "trainer.exe",
  "executablePath": ".\\Apps\\Trainer\\trainer.exe",
  "startTimeUtc": "2026-08-15T18:42:12Z"
}
```

Capture these values from the same live process. PID alone is never trusted,
because Windows reuses PIDs. Name matching ignores the optional `.exe` suffix.
Each configured path is resolved from the directory containing the selected
`watch-config.json`, then the resolved Windows paths are compared. Start times
have a one-second precision tolerance.

Process inspection is performed in-process with .NET/Windows APIs. The runtime
does not invoke PowerShell, `tasklist`, WMI command-line tools, or any other CLI.

## State protocol

A typical application lifecycle is:

```text
Pending -> Running -> Succeeded
                   -> Failed
                   -> Blocked -> Running
```

The group completion barrier opens only when every participant is `Succeeded` or
`Failed` and every matching fingerprinted process has exited. Merely changing a
state while its process remains open does not complete the job.

If a matching process disappears before its writer records a terminal state and
handoff, CinDa-DaWatcha converts that observation into an effective failure in
the final summary. A changed fingerprint at the same PID is also a failure; it is
not mistaken for the original process.

The final job result is success only when all participants succeeded. Otherwise
it is failure. The summary includes each participant's effective result, detail,
exit code when available, and handoff message.

## Delivery protocol

For every warning and final handoff, CinDa-DaWatcha:

1. Creates a deterministic delivery ID and saves the complete message plus SHA-256
   in the private delivery ledger.
2. Navigates the controlled Firefox tab to the exact initiating UUID.
3. Rejects redirects away from `https://chatgpt.com/c/{uuid}`.
4. Searches user-authored bubbles for the complete normalized message. If found,
   it records success without clicking Send.
5. Waits until generation has stopped, the page is stable for the configured
   polls, and the composer contains no unrelated draft.
6. Writes and reads back the full composer text.
7. Clicks Send once.
8. Accepts success only when the complete message appears in a user bubble.
9. Retries ordinary attempts, then refreshes once for one final attempt.
10. If still unverified, leaves the exact message prepared and displays clearly
    labeled **SEND NOW** and **VERIFY SENT MESSAGE** controls.

Warnings and the final handoff have different deterministic IDs, so one blocked
warning does not suppress the later result. Each individual ID is delivered only
once as far as browser-observable state permits. After a crash at the narrow
point between remote acceptance and DOM rendering, restart recovery searches for
the complete message before another Send click.

Do not edit `delivery-state.json`. Corrupt hashes, duplicate IDs, altered routes,
and unknown fields cause startup rejection rather than silent replay.

## Authoring checklist

Before enabling a new job, verify all of the following:

- The initiating UUID came from the conversation that started this exact job.
- The job ID has never been used for another run.
- Every participant ID is unique within the job.
- Every fingerprint was captured from one currently live process.
- Every timestamp is UTC and ends in `Z`.
- Pending/running participants have fresh heartbeats.
- The file passes the schema and contains no unknown or duplicate fields.
- Every configured file path starts with `.\\` and remains inside the ledger
  directory.
- The standard Firefox window opened by the dashboard is signed into ChatGPT.

For a terminal update, verify:

- `state` is `Succeeded` or `Failed`.
- `finishedAtUtc` and `updatedAtUtc` reflect that update.
- `handoffMessage` says what happened and what the initiating conversation should
  do next.
- The application has released files/resources and is exiting; final delivery
  will wait for the process itself to stop.

## Example terminal participant

```json
{
  "id": "trainer",
  "process": {
    "pid": 12345,
    "name": "trainer.exe",
    "executablePath": ".\\Apps\\Trainer\\trainer.exe",
    "startTimeUtc": "2026-08-15T18:42:12Z"
  },
  "state": "Succeeded",
  "updatedAtUtc": "2026-08-15T20:12:09Z",
  "heartbeatUtc": "2026-08-15T20:12:01Z",
  "finishedAtUtc": "2026-08-15T20:12:09Z",
  "exitCode": 0,
  "detail": "Training and checkpoint validation completed.",
  "handoffMessage": "Checkpoint D:\\Models\\run-01 passed validation. Continue with evaluation."
}
```

## Troubleshooting

| Dashboard state | Meaning | Response |
|---|---|---|
| `Invalid configuration` | Strict parsing or validation failed | Correct the ledger; the last valid copy remains active |
| `NotStarted` | Pending process has not matched yet | Verify launcher timing and the full fingerprint |
| `Running` | Matching process is active with a fresh heartbeat | No action |
| `Blocked` | Explicit block, stale heartbeat, or unresponsive GUI | Let the warning reach the initiating UUID; avoid duplicate launches |
| `WaitingForExit` | Terminal state recorded but process is still alive | Let the application close and release its resources |
| `Failed` | Early exit or terminal failure | Inspect detail and final summary |
| `Manual send required` | Automatic attempts and refresh were exhausted | Check the exact target label, then use **SEND NOW** or send in Firefox and choose **VERIFY SENT MESSAGE** |
| Route conflict | An existing job ID was given a different initiating UUID | Restore the original UUID or create a genuinely new unique job ID |

When the dashboard reports a route, payload, or delivery-ledger integrity
conflict, do not delete history to force a resend. Establish why the source data
changed and correct the producer.
