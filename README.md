# CinDa-DaWatcha

CinDa-DaWatcha is a Windows desktop monitor that watches fingerprinted
processes and prepares manual ChatGPT handoffs when a process completes
or exits.

It launches its own Firefox instance with one window and one tab. It
never attaches to normal Firefox sessions, never installs an extension,
and never clicks **Send**.

## MVP behavior

- Hot-reloads a structured JSON watch file.
- Verifies PID, process name, executable path, and start time.
- Detects process exit or a stable Windows UI Automation completion state.
- Emits only one handoff per process run.
- Batches simultaneous events by ChatGPT conversation UUID.
- Measures visible conversation text as UTF-8 bytes.
- Rolls over to a new chat at 5 MiB and atomically replaces the UUID.
- Tries a handoff twice, then prepares a diagnostic message in a new chat.
- Requires manual Send plus four-part delivery confirmation.

## Requirements

- Windows 10 or 11
- .NET 8 Desktop Runtime or SDK
- Mozilla Firefox
- Internet access on first browser run so Selenium Manager can resolve
  GeckoDriver

## Build and run

```powershell
git clone https://github.com/crazytaxzi/CinDa-DaWatcha.git
cd CinDa-DaWatcha
dotnet build .\CinDa-DaWatcha.sln -c Release
dotnet run --project .\src\CinDa.DaWatcha.App -c Release
```

On first launch, the app creates:

```text
%USERPROFILE%\Documents\CinDa-DaWatcha\watch-config.json
```

Start monitoring, choose **Open managed Firefox**, and sign in to
ChatGPT inside that dedicated browser profile. Normal Firefox windows
and profiles remain untouched.

## Configure a watch

Copy the shape in [watch-config.example.json](watch-config.example.json).
To capture a process fingerprint:

```powershell
$p = Get-Process -Id 12345
[pscustomobject]@{
  pid = $p.Id
  name = $p.ProcessName
  executablePath = $p.Path
  startTimeUtc = $p.StartTime.ToUniversalTime().ToString("O")
} | ConvertTo-Json
```

The UUID is the identifier after `/c/` in a ChatGPT conversation URL.
The passalong message is stored directly in each watch record.

### Completion rules

Use `"method": "uia-text"` to inspect visible accessible controls owned
by the PID. A configured pattern must remain visible for
`completionStablePolls` consecutive polls.

Use `"method": "title"` with `windowTitlePattern` when the application
reports completion in its title. The title value is treated as a
case-insensitive regular expression, with substring matching as a
fallback for an invalid expression.

Console applications without an accessible window still trigger when
their process exits.

## Manual-send confirmation

CinDa-DaWatcha prepares the message and waits. After sending it yourself,
select **Confirm message sent**. Completion requires:

1. The composer is empty.
2. The Send button is no longer ready.
3. The expected user-message bubble is visible.
4. You explicitly confirm in the desktop app.

For a rollover chat, the new UUID is captured only after the manual send
creates the conversation URL.

## Validation

```powershell
dotnet test .\CinDa-DaWatcha.sln -c Release
dotnet run --project .\tools\CinDa.DaWatcha.BrowserSmoke -c Release
```

The browser smoke test briefly opens and closes an isolated Firefox
profile. It does not navigate to ChatGPT.

## Current limitations

- Firefox and ChatGPT DOM changes may require selector maintenance.
- UI Automation cannot see completion text that an application does not
  expose through its accessibility tree.
- A Windows Terminal tab can be hosted by a different PID than its child
  console process; process-exit detection remains reliable.
- Only one handoff can await manual confirmation at a time. Later batches
  stay queued.
