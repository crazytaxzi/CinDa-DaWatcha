# CinDa-DaWatcha

CinDa-DaWatcha is a Windows handoff monitor for a group of local training
applications. Those applications maintain a JSON job ledger. When every member
has finished and released its process, CinDa-DaWatcha delivers one verified
summary to the ChatGPT conversation that started the job.

The runtime uses local Windows process inspection and browser DOM automation in
an isolated Firefox profile. It does not call the OpenAI API, any other web API,
email, webhooks, or command-line tools. Network interaction is limited to what
the managed Firefox browser itself does while displaying ChatGPT.

## Delivery contract

- The initiating `/c/{uuid}` is recorded at job startup and is immutable after
  the first delivery for that job is queued.
- Every participant is identified by PID, name, executable path, and start time.
- A stale heartbeat, explicit `Blocked` state, or non-responsive GUI raises one
  locked-app warning to the initiating UUID.
- The final result waits until every participant is terminal and no matching
  process remains alive.
- A process that disappears without a terminal handoff is reported as failed.
- The complete final message is deduplicated in a durable delivery ledger.
- Delivery waits for a stable, idle conversation, sends automatically, and
  verifies the complete user bubble. It makes the configured attempts, refreshes
  once for one last attempt, then exposes an unmistakable manual-send fallback.

## Requirements

- Windows 10 or 11
- Mozilla Firefox
- The packaged release (recommended), or the .NET 8 SDK for source builds

The release contains `geckodriver.exe`; the program never downloads a driver at
runtime and does not require `winget`.

## Run from source

```powershell
dotnet build .\CinDa-DaWatcha.sln -c Release
dotnet run --project .\src\CinDa.DaWatcha.App -c Release
```

At first launch, the program creates:

```text
%USERPROFILE%\Documents\CinDa-DaWatcha\watch-config.json
```

Open the dedicated Firefox window from the dashboard and sign in to ChatGPT.
The profile and tab are owned only by CinDa-DaWatcha; normal Firefox sessions
are not attached to or altered.

Copy [watch-config.example.json](watch-config.example.json), then follow the
normative [job-ledger manual](docs/watch-file-manual.md) and
[JSON Schema](docs/watch-config.schema.json). Programs may atomically edit the
selected ledger at any time; CinDa-DaWatcha only reads it.

## Verify from source

```powershell
dotnet test .\CinDa-DaWatcha.sln -c Release
dotnet run --project .\tools\CinDa.DaWatcha.BrowserSmoke -c Release -- --driver C:\path\to\geckodriver.exe
```

The browser smoke test uses a local fixture. It exercises idle detection,
composer preparation, the Send click, full-message verification, and clean
shutdown without contacting ChatGPT or sending a real message.

## Important limits

ChatGPT can change its page structure without notice, so every send is verified
against the complete outgoing text and failures fall back visibly instead of
being assumed successful. A computer crash between the browser accepting a
message and rendering it can never provide mathematical exactly-once delivery;
on restart the program searches the target conversation for the complete message
before clicking Send again.
