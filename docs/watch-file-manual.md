# Watched File Instruction Manual

This document is the normative specification for the JSON file watched by
CinDa-DaWatcha. It is written for both humans and AI systems that create,
inspect, or modify the file.

The words **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT**, and **MAY**
are requirements:

- **MUST / MUST NOT**: required for a valid and safe file.
- **SHOULD / SHOULD NOT**: strongly recommended unless a documented reason
  justifies an exception.
- **MAY**: optional behavior that remains compatible with this specification.

The machine-readable companion is
[watch-config.schema.json](watch-config.schema.json).

## 1. Purpose

The watched file is the control plane for CinDa-DaWatcha. It answers three
questions:

1. How should the monitor behave globally?
2. Which exact process runs should be watched?
3. Which ChatGPT conversation and passalong text belong to each process?

The file does not contain executable code. CinDa-DaWatcha does not evaluate
fields as PowerShell, command-line arguments, templates, or scripts.

## 2. Default location

The application creates and uses this path by default:

```text
%USERPROFILE%\Documents\CinDa-DaWatcha\watch-config.json
```

The desktop interface can select another JSON file. Only the selected file is
watched. Editing an example or schema file does nothing unless that file is
explicitly selected in the application.

## 3. JSON rules

The file MUST:

- Be valid JSON encoded as UTF-8.
- Contain exactly one top-level JSON object.
- Use camelCase property names exactly as documented.
- Contain a `settings` object and a `watches` array.
- Use doubled backslashes inside Windows paths.
- Use JSON escape sequences such as `\n` inside multiline strings.
- Contain no comments.
- Contain no trailing commas.
- Contain no duplicate property names.
- Contain no undocumented properties.

Property names are currently read case-insensitively by the application, but
an AI MUST NOT rely on that implementation detail. The application rewrites
the whole document when it replaces a conversation UUID. Unknown properties,
comments, formatting, and property order are not preserved.

An empty watch list is valid:

```json
{
  "settings": {
    "chatBaseUrl": "https://chatgpt.com",
    "conversationLimitBytes": 5242880,
    "pollIntervalMs": 1000,
    "completionStablePolls": 2,
    "handoffRetries": 2,
    "batchWindowMs": 3000,
    "firefoxBinary": "C:\\Program Files\\Mozilla Firefox\\firefox.exe",
    "firefoxProfileDirectory": "C:\\Users\\USER\\AppData\\Local\\CinDa-DaWatcha\\FirefoxProfile"
  },
  "watches": []
}
```

## 4. File lifecycle

### 4.1 Initial load

When monitoring starts, the application parses and validates the complete
file. If validation fails, monitoring does not start.

### 4.2 Hot reload

While monitoring is active, CinDa-DaWatcha watches the file name for creation,
rename, size, and last-write changes. It waits approximately 350 milliseconds
before reading, which combines rapid editor events into one reload attempt.

A successful reload replaces all in-memory settings and watch records.
A failed reload leaves the last valid in-memory configuration active and logs
the error.

### 4.3 Safe writing

An AI or external program MUST use an atomic replacement:

1. Read and parse the current file.
2. Modify the in-memory JSON object.
3. Validate the entire resulting object.
4. Write the result to a temporary file in the same directory.
5. Flush and close the temporary file.
6. Replace the watched file with the temporary file in one rename operation.
7. Read the watched file again and verify the intended values.

Do not stream a partially generated JSON document directly into the watched
path. The debounce reduces partial-read risk but does not eliminate it.

### 4.4 Application-owned writes

When a new ChatGPT conversation receives a UUID, the application replaces the
old UUID in every matching watch record. It writes through a same-directory
temporary file and suppresses its own resulting watcher event briefly.

An external AI SHOULD NOT edit the file during that replacement. If concurrent
editing is unavoidable, it MUST reread the file immediately before writing and
MUST verify that no UUID or process fingerprint changed.

## 5. Complete document shape

```json
{
  "settings": {
    "chatBaseUrl": "https://chatgpt.com",
    "conversationLimitBytes": 5242880,
    "pollIntervalMs": 1000,
    "completionStablePolls": 2,
    "handoffRetries": 2,
    "batchWindowMs": 3000,
    "firefoxBinary": "C:\\Program Files\\Mozilla Firefox\\firefox.exe",
    "firefoxProfileDirectory": "C:\\Users\\USER\\AppData\\Local\\CinDa-DaWatcha\\FirefoxProfile"
  },
  "watches": [
    {
      "id": "worker-01",
      "enabled": true,
      "process": {
        "pid": 12345,
        "name": "worker.exe",
        "executablePath": "C:\\Apps\\Worker\\worker.exe",
        "startTimeUtc": "2026-08-15T18:42:12.0000000Z"
      },
      "completion": {
        "method": "uia-text",
        "patterns": ["Completed", "Finished", "Done", "Success"],
        "windowTitlePattern": ""
      },
      "chat": {
        "uuid": "11111111-2222-3333-4444-555555555555"
      },
      "passalongMessage": "Review the completed task and continue."
    }
  ]
}
```

## 6. The `settings` block

`settings` contains global behavior. Every field applies to every watch
record.

| Field | Type | Required constraint | Recommended value |
|---|---:|---|---:|
| `chatBaseUrl` | string | Exact trusted ChatGPT origin | `https://chatgpt.com` |
| `conversationLimitBytes` | integer | At least 1 | `5242880` |
| `pollIntervalMs` | integer | At least 250 | `1000` |
| `completionStablePolls` | integer | At least 1 | `2` |
| `handoffRetries` | integer | At least 1 | `2` |
| `batchWindowMs` | integer | At least 0 | `3000` |
| `firefoxBinary` | string | Absolute path to Firefox | Standard Firefox path |
| `firefoxProfileDirectory` | string | Dedicated absolute directory | App default |

### 6.1 `chatBaseUrl`

Purpose: establishes the trusted origin used to build conversation URLs.

Required value:

```json
"chatBaseUrl": "https://chatgpt.com"
```

CinDa-DaWatcha appends `/c/{uuid}` for an existing conversation and `/`
for a new conversation.

An AI MUST use the exact HTTPS origin above. It MUST NOT insert paths, query
strings, fragments, credentials, an alternate deployment, or an unrelated
domain. A malicious origin
could receive passalong content.

A trailing slash is tolerated by the runtime but SHOULD be omitted.

### 6.2 `conversationLimitBytes`

Purpose: defines when the active conversation is considered too large and must
roll over to a new conversation.

The application:

1. Selects visible conversation-turn elements in the ChatGPT page.
2. Reads their visible `innerText`.
3. Joins the turns with blank lines.
4. Encodes that text as UTF-8.
5. Compares the byte count with this value.

Rollover occurs when:

```text
visibleConversationBytes >= conversationLimitBytes
```

The default is 5 MiB:

```json
"conversationLimitBytes": 5242880
```

This is not Firefox memory use, network traffic, HTML size, token count, or
the size of the JSON file.

Lower values produce more frequent conversation rollover. Higher values keep
more history but make the page heavier. An AI SHOULD preserve 5242880 unless
the operator explicitly requests another threshold.

### 6.3 `pollIntervalMs`

Purpose: controls how often process identity and completion state are checked.

Minimum valid value: `250`.

Recommended value: `1000`.

A smaller value detects completion faster but performs more process and
accessibility-tree work. A larger value reduces activity but adds latency.

Approximate time required to accept a visual completion state is:

```text
pollIntervalMs * completionStablePolls
```

Process exit detection is also bounded by the polling interval.

### 6.4 `completionStablePolls`

Purpose: rejects a completion label that appears only briefly.

The value is the number of consecutive polls for which completion must be
observed. Any non-matching poll resets the count to zero.

Recommended value: `2`.

Use a larger value for applications whose status control flickers. Do not use
`1` unless the completion signal is known to be stable.

### 6.5 `handoffRetries`

Purpose: sets the total number of normal handoff-preparation attempts.

Important: despite the field name, the current value is the total attempt
count, not “initial attempt plus this many retries.”

```json
"handoffRetries": 2
```

means:

- Attempt 1
- Attempt 2
- Then the diagnostic fallback

An AI SHOULD use `2`. Excessive retries can repeat expensive browser work and
make failures harder to diagnose.

### 6.6 `batchWindowMs`

Purpose: allows events that occur close together to be combined.

After the first event arrives, the batcher waits this many milliseconds, drains
all currently queued events, and groups them by conversation UUID.

- Events with the same UUID become one combined passalong message.
- Events with different UUIDs become separate handoffs.
- An event arriving after the queue is drained belongs to a later batch.
- A value of `0` disables the intentional wait.

Recommended value: `3000`.

Increase it when related jobs typically finish several seconds apart. Decrease
it when handoff latency matters more than combining messages.

### 6.7 `firefoxBinary`

Purpose: identifies the Firefox executable launched by Selenium.

Typical value:

```json
"firefoxBinary": "C:\\Program Files\\Mozilla Firefox\\firefox.exe"
```

The path MUST:

- Be absolute.
- Identify `firefox.exe`.
- Exist on the machine running CinDa-DaWatcha.
- Use doubled backslashes in JSON.

Changing this value does not replace a Firefox instance that is already
running. Pause and restart monitoring, or restart CinDa-DaWatcha, to guarantee
the new executable is used.

### 6.8 `firefoxProfileDirectory`

Purpose: supplies persistent login and browser state to the Firefox instance
owned by CinDa-DaWatcha.

The directory MUST be dedicated to CinDa-DaWatcha. It MUST NOT be the profile
used by an ordinary Firefox session. Two Firefox processes must not open the
same profile simultaneously.

The application creates the directory when needed. Spaces are permitted.
Changing the value takes effect on the next managed Firefox launch, not in an
already running instance.

## 7. The `watches` array

`watches` is an ordered array of process-specific records.

The array MAY be empty. Each entry MUST contain:

- `id`
- `enabled`
- `process`
- `completion`
- `chat`
- `passalongMessage`

Every record, including a disabled record, MUST be complete and valid.

Multiple records MAY use the same ChatGPT UUID. That is the intended method
for batching several related processes into one conversation.

## 8. Watch field: `id`

Purpose: provides the stable logical identity of the watch record.

Recommended format:

```text
lowercase letters, digits, dots, underscores, and hyphens
```

Examples:

```json
"id": "tax-worker-01"
"id": "browser-export"
"id": "nightly_job.2"
```

Requirements:

- MUST be non-empty.
- MUST be unique without regard to letter case.
- SHOULD be 1 to 64 characters.
- SHOULD remain unchanged for the logical watcher.

Changing only `id` makes the monitor treat the record as a different runtime
watch and can cause another handoff for the same process. An AI MUST NOT rename
an ID merely for cosmetic reasons while monitoring is active.

## 9. Watch field: `enabled`

Purpose: enables or pauses monitoring for one record.

```json
"enabled": true
```

- `true`: inspect the process and allow a trigger.
- `false`: show the record as disabled and skip monitoring.

Disabling a record does not permit incomplete placeholder data. All other
fields must remain valid because the entire document is validated before use.

Best practice: create a new record with `enabled: false`, validate all values,
and change it to `true` only after verifying the live process fingerprint and
conversation UUID.

## 10. The `process` block

The process block identifies one process run, not every future process with the
same executable name.

```json
"process": {
  "pid": 12345,
  "name": "worker.exe",
  "executablePath": "C:\\Apps\\Worker\\worker.exe",
  "startTimeUtc": "2026-08-15T18:42:12.0000000Z"
}
```

All four values form the fingerprint. The PID alone is never trusted because
Windows can reuse a PID after a process exits.

### 10.1 `pid`

Type: positive integer.

The value MUST be obtained from the live target process. An AI MUST NOT guess,
reuse from an old record, or infer a PID from a window title.

A watch added after its process has already exited will not emit an exit event.
The process must first be observed alive with a matching fingerprint.

### 10.2 `name`

Type: non-empty string.

This is the executable process name. Matching is case-insensitive and ignores
the `.exe` extension. Therefore `worker` and `worker.exe` identify the same
name, but the AI SHOULD preserve the name returned by the operating system.

The name is not sufficient by itself; path and start time must also match.

### 10.3 `executablePath`

Type: non-empty absolute Windows path.

The application converts both configured and observed paths to full paths and
compares them case-insensitively.

The path MUST point to the executable for the live PID. Do not use:

- A shortcut path.
- A working directory.
- A command-line argument.
- A script being interpreted by another executable.
- A relative path.

Access to some protected processes may prevent the application from reading
the executable path. Such a record remains in a stale-PID state and does not
trigger.

### 10.4 `startTimeUtc`

Type: RFC 3339 / ISO 8601 timestamp with a UTC `Z` suffix.

Preferred representation:

```json
"startTimeUtc": "2026-08-15T18:42:12.1234567Z"
```

The observed start time must be within two seconds of the configured value.
The tolerance accommodates operating-system timestamp precision; it is not
permission to reuse an old timestamp.

Together, `pid`, `name`, `executablePath`, and `startTimeUtc` prevent a
recycled PID from causing a handoff for an unrelated process.

### 10.5 Capturing a correct fingerprint

Use this PowerShell pattern while the process is running:

```powershell
$p = Get-Process -Id 12345 -ErrorAction Stop

[pscustomobject]@{
  pid            = $p.Id
  name           = $p.ProcessName
  executablePath = $p.Path
  startTimeUtc   = $p.StartTime.ToUniversalTime().ToString("O")
} | ConvertTo-Json
```

An AI using a machine-control tool MUST obtain all four values in the same
inspection session. It MUST verify that the PID remains alive after capture.

## 11. The `completion` block

The completion block defines how a still-running process can be considered
finished.

```json
"completion": {
  "method": "uia-text",
  "patterns": ["Completed", "Finished", "Done", "Success"],
  "windowTitlePattern": ""
}
```

Process exit is always a trigger after the matching process has been observed
alive. The completion block adds a trigger that can fire before exit.

Only these method values are permitted:

- `uia-text`
- `title`

### 11.1 Method `uia-text`

Use `uia-text` when the target application exposes completion status through
the Windows accessibility tree.

For every visible top-level window owned by the configured PID, the detector:

1. Checks `windowTitlePattern` when it is non-empty.
2. Walks up to 4,000 visible controls in the UI Automation control view.
3. Reads each control's accessible `Name`.
4. Performs a case-insensitive substring comparison against every pattern.
5. Reports completion when any pattern matches.
6. Requires the result to remain true for `completionStablePolls` polls.

Patterns are literal substrings, not regular expressions.

Good patterns are specific:

```json
"patterns": ["Export completed successfully", "All tasks finished"]
```

Weak patterns are dangerous:

```json
"patterns": ["Done", "Success"]
```

A broad word may appear in help text, history, or an unrelated button. An AI
SHOULD use the longest stable status phrase exposed by the actual application.

At least one pattern is required for `uia-text`.

### 11.2 Method `title`

Use `title` when completion is reliably represented in the top-level window
title.

```json
"completion": {
  "method": "title",
  "patterns": [],
  "windowTitlePattern": "^Export complete - .+$"
}
```

`windowTitlePattern` is evaluated as a case-insensitive .NET regular
expression with a 250 millisecond match timeout.

If the pattern is not valid regular-expression syntax, the implementation
falls back to a case-insensitive substring match. An AI MUST still provide a
valid regular expression; it MUST NOT depend on the fallback.

Use anchors when the entire title structure is known. Escape literal regex
characters. Avoid catastrophic or needlessly complex expressions.

For `title`, `windowTitlePattern` MUST be non-empty.

### 11.3 Title fallback with `uia-text`

A non-empty `windowTitlePattern` is checked even when the method is
`uia-text`. This provides an intentional fallback:

```json
"completion": {
  "method": "uia-text",
  "patterns": ["Export completed successfully"],
  "windowTitlePattern": "\\bComplete\\b"
}
```

Leave it as an empty string when title matching is not wanted.

### 11.4 Console and browser caveats

A console child process may be displayed inside a Windows Terminal window
owned by a different PID. Its UI Automation tree will not be found under the
child PID. Exit detection still works.

Firefox uses multiple processes. A content-process PID may not own the visible
top-level browser window. For browser watches, prefer process exit or target a
PID that owns the relevant window and verify it experimentally.

## 12. The `chat` block

```json
"chat": {
  "uuid": "11111111-2222-3333-4444-555555555555"
}
```

`uuid` routes the handoff to:

```text
https://chatgpt.com/c/{uuid}
```

Requirements:

- MUST be a syntactically valid UUID.
- MUST come from an actual intended ChatGPT conversation URL.
- MUST NOT be invented, truncated, or copied from an unrelated URL.
- SHOULD use lowercase hexadecimal for consistency.

The all-zero UUID is syntactically valid but is not a usable ChatGPT
conversation and MUST NOT be used in an enabled production record.

### 12.1 Shared UUID behavior

Several watch records MAY intentionally share one UUID. Events inside the
batch window are combined into one message for that UUID.

When the conversation exceeds the configured byte limit and rolls over, the
application replaces the old UUID in every record that shares it. This keeps
the related group together.

An AI changing a shared UUID manually MUST first identify every record using
the old UUID and MUST update the group consistently unless the operator
explicitly requests the group to be split.

## 13. Watch field: `passalongMessage`

Purpose: supplies the process-specific instruction included in a handoff.

```json
"passalongMessage": "Review the output, summarize failures, and continue."
```

The value:

- MUST be a non-empty JSON string.
- Is trimmed at its beginning and end.
- Is included verbatim beneath automatically generated process metadata.
- Is not a template and performs no variable substitution.
- Is not executed as a command or script.

For multiline content, encode line breaks with `\n`:

```json
"passalongMessage": "Review the output.\nThen continue with the next queued task."
```

A good message states:

1. What completed.
2. Where the resulting artifact or output can be found.
3. What the receiving conversation should do next.
4. Any constraint that must remain true.

Do not place secrets, access tokens, passwords, private keys, or unrelated
personal data in this field. The content is destined for a ChatGPT
conversation.

The application adds metadata such as watch ID, PID, process name, trigger
type, timestamp, and a unique handoff marker. Do not duplicate those fields
unless the receiving workflow requires them.

## 14. Trigger and re-arm semantics

For each enabled record, the monitor maintains runtime state keyed by:

```text
id + pid + name + executablePath + startTimeUtc
```

A matching process is first marked as observed alive. The first stable
completion or later exit produces one event. Additional completion and exit
observations for that same runtime identity do not produce another event.

Changing only these fields does not re-arm a handled process:

- `enabled`
- `completion`
- `chat.uuid`
- `passalongMessage`
- Global settings

Changing any process fingerprint value creates a new runtime identity.
Changing the watch `id` creates a separate runtime state and can also cause
another trigger.

Runtime deduplication is currently held in memory. Restarting the application
clears it. If a completed process is still running and still displays its
completion state after restart, it can trigger again. An AI SHOULD refresh the
record for the next live process run and SHOULD disable obsolete completed
records.

## 15. What reloads immediately

The following values affect subsequent polling or handoffs after a successful
hot reload:

- All watch records
- `conversationLimitBytes`
- `pollIntervalMs`
- `completionStablePolls`
- `handoffRetries`
- `batchWindowMs`
- `chatBaseUrl`

`firefoxBinary` and `firefoxProfileDirectory` are used when the managed
Firefox driver is created. If Firefox is already running, restart the managed
browser to guarantee those changes take effect.

An event already placed into a batch contains the watch record as it existed
when the event fired. Editing its message or UUID afterward does not rewrite
that already queued event.

## 16. AI authoring protocol

An AI instructed to create or modify the watched file MUST follow this exact
sequence.

### Phase A: Establish authority and inputs

1. Identify the exact watched-file path.
2. Determine whether the task permits reading only or also permits writing.
3. Obtain every live PID and fingerprint from the machine; never invent them.
4. Obtain each ChatGPT UUID from an actual conversation URL; never invent it.
5. Obtain the intended passalong text from the operator or an authoritative
   task specification.

### Phase B: Read and understand

6. Read the entire current file immediately before editing.
7. Parse it as JSON.
8. Reject duplicate property names.
9. Validate it against `watch-config.schema.json`.
10. Preserve every unrelated setting and watch record.
11. Select an existing watch by exact `id`, not by array position.

### Phase C: Modify

12. Make only the explicitly required semantic changes.
13. Use canonical camelCase property names.
14. Preserve shared-UUID groups unless explicitly splitting them.
15. Do not add comments, metadata, timestamps, notes, or unknown properties.
16. Do not reorder the `watches` array unless requested.
17. Do not change `id` to re-arm a record.
18. Do not update a process fingerprint from stale historical information.
19. Keep a new record disabled until all values are verified.

### Phase D: Validate

20. Confirm every ID is non-empty and case-insensitively unique.
21. Confirm every PID is a positive integer.
22. Confirm every process path is absolute and belongs to its live PID.
23. Confirm every start time is UTC and belongs to that process run.
24. Confirm every enabled UUID belongs to an intended ChatGPT URL.
25. Confirm `method` is exactly `uia-text` or `title`.
26. Confirm the method-specific completion fields are valid.
27. Confirm every passalong message is non-empty and contains no secret.
28. Confirm all global numeric limits meet their minimums.
29. Serialize and parse the result again before writing.

### Phase E: Commit atomically

30. Write UTF-8 JSON to a same-directory temporary file.
31. Flush and close it.
32. Atomically replace the watched file.
33. Reread and parse the final watched file.
34. Verify the intended fields and all preserved fields.
35. Report the exact IDs and fields changed.
36. Report any reload error shown by CinDa-DaWatcha.

If any required input cannot be verified, the AI MUST stop and ask for that
input. It MUST NOT substitute a plausible value.

## 17. Common recipes

### 17.1 Add a new watch safely

1. Capture the live fingerprint.
2. Obtain the target ChatGPT UUID.
3. Create a unique stable ID.
4. Choose and verify a completion method.
5. Write a specific passalong message.
6. Add the complete record with `enabled: false`.
7. Validate and atomically save.
8. Verify the application loaded the record.
9. Change only `enabled` to `true`.
10. Validate and atomically save again.

### 17.2 Point a watch at a restarted process

A restarted process is a new process run. Update together:

- `pid`
- `name`
- `executablePath`
- `startTimeUtc`

Capture all four from the same live process. Do not update only the PID.

### 17.3 Pause one watch

Change only:

```json
"enabled": false
```

Do not delete or blank the other blocks. A disabled record still must validate.

### 17.4 Route several processes to one conversation

Give each record a unique `id` and fingerprint, but use the same
`chat.uuid`. Keep `batchWindowMs` large enough to include their expected
completion spread.

### 17.5 Split one watch into another conversation

Change only that record's UUID after verifying the new conversation. Do not
change other records sharing the old UUID. This intentionally removes the
record from the shared routing group.

### 17.6 Reuse a logical watch for the next run

Keep the same `id`, completion rule, chat block, and passalong intent. Replace
the complete process fingerprint with values captured from the new live run.

## 18. Invalid patterns

### 18.1 PID without a matching fingerprint

```json
"process": {
  "pid": 12345,
  "name": "some-process",
  "executablePath": "C:\\guessed\\path.exe",
  "startTimeUtc": "2020-01-01T00:00:00Z"
}
```

Why invalid: the fields were not captured from the same live process. The
record will be stale or, worse, misrepresent operator intent.

### 18.2 Placeholder UUID on an enabled record

```json
"enabled": true,
"chat": {
  "uuid": "00000000-0000-0000-0000-000000000000"
}
```

Why invalid: the value passes UUID syntax checks but does not identify a real
conversation.

### 18.3 Broad completion text

```json
"patterns": ["OK", "Done"]
```

Why unsafe: common words can exist in unrelated controls and cause a premature
handoff.

### 18.4 Raw Windows path

```json
"firefoxBinary": "C:\Program Files\Mozilla Firefox\firefox.exe"
```

Why invalid JSON: backslashes begin escape sequences. Each backslash must be
doubled in the JSON source.

### 18.5 JSON comments

```json
{
  // This is not valid JSON.
  "watches": []
}
```

Why invalid: the parser does not accept comments, and application rewrites
would not preserve them.

## 19. Troubleshooting by displayed state

| State | Meaning | Correct response |
|---|---|---|
| `Disabled` | Record is intentionally skipped | Set `enabled` true only after validation |
| `Not running` | PID was not found | Capture the next live process fingerprint |
| `Stale PID` | PID exists but fingerprint differs or cannot be read | Recapture all four process fields |
| `Watching` | Fingerprint matches; no stable completion yet | No action unless completion rule is wrong |
| `Detector error` | UI Automation inspection failed | Inspect access rights and completion method |
| `Queued` | One event was accepted for this process run | Do not re-arm or rename the record |
| `Handled` | Handoff already prepared for this runtime identity | Update fingerprint for the next run |
| Reload error | New JSON was rejected | Fix the file; last valid configuration remains active |

## 20. Final AI validation checklist

Before writing, an AI MUST be able to answer **yes** to every applicable item:

- [ ] I know the exact watched-file path.
- [ ] The document is valid UTF-8 JSON with no comments or trailing commas.
- [ ] Only documented camelCase properties are present.
- [ ] Every global setting is present and valid.
- [ ] Every watch ID is stable and case-insensitively unique.
- [ ] Every enabled PID is currently alive.
- [ ] Every process fingerprint was captured from one live process.
- [ ] Every executable path is absolute.
- [ ] Every start time is UTC.
- [ ] Every enabled UUID came from the intended ChatGPT conversation.
- [ ] Every completion method is exact and method-specific fields are valid.
- [ ] Every completion pattern was verified against the target application.
- [ ] Every passalong message is non-empty and contains no secret.
- [ ] Shared UUID relationships were preserved intentionally.
- [ ] Unrelated settings and records were preserved.
- [ ] The result was validated before writing.
- [ ] The watched file was replaced atomically.
- [ ] The final file was reread and verified.
- [ ] CinDa-DaWatcha accepted the reload.

If any box cannot be checked, the AI MUST stop rather than guess.
