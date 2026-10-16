# SolidWorks AI Chat (Windows)

Windows x64 SOLIDWORKS task-pane front end for the native **Codex / Claude / Gemini** agents.
The pane forwards the user's text and displays the agent's streamed response. It does
not interpret JSON operation proposals, require an extra “execute” message, or run a
custom planning/retry loop. The vendor agent owns tool use, recovery and completion.
No WSL component is used at runtime and no WSL setting is changed.

## Build

Prerequisites: Windows x64, .NET 8 SDK with Windows Desktop support, and SOLIDWORKS.
Run in Windows PowerShell from this repository:

```powershell
.\scripts\Build-ChatAddin.ps1
# If SOLIDWORKS is installed elsewhere:
.\scripts\Build-ChatAddin.ps1 -SolidWorksInteropDirectory 'C:\path\SOLIDWORKS\api\redist'
# Use a new directory when validating an update while an older package remains loaded:
.\scripts\Build-ChatAddin.ps1 -OutputDirectory 'C:\path\chat-addin-new'
```

Output: `artifacts\chat-addin\addin` and `artifacts\chat-addin\cli`.
Keep these directories together and do not move them after registration.
The package is framework-dependent and requires the **x64 .NET 8 Desktop Runtime**.
Build detects the conventional SOLIDWORKS and the local 3DEXPERIENCE 2026 SDK paths.
For other versions, supply the installed SDK directory explicitly.

## Register in SolidWorks

Open **64-bit Windows PowerShell as Administrator**:

```powershell
cd 'C:\Users\takea\Documents\Projects\Solidworks_AI\artifacts\chat-addin'
.\Register-ChatAddin.ps1
```

Registration uses the .NET-generated COM host and SOLIDWORKS's machine-wide
Addins registry key; Windows administrator rights are required for this step.
It does not close/restart SOLIDWORKS or modify CAD documents.
In SOLIDWORKS, open **Tools > Add-Ins**, enable **SolidWorks AI Chat**, then open
its right-hand task-pane tab. If it is absent in an existing session, restart
SOLIDWORKS after saving your work. The startup checkbox can also be disabled there.

Unregister after disabling the add-in:

```powershell
.\Register-ChatAddin.ps1 -Uninstall
```

The script removes only this add-in's registration; it preserves CAD files and settings.
Disable the add-in and close SOLIDWORKS before rebuilding an installed package
because the native COM host / managed assemblies can remain loaded in its process.

## AI connections

This implementation uses each vendor's **Windows CLI**, not the Codex desktop
chat or WSL installation, and not an API model renamed to “Codex”. Install and
authenticate the desired CLI using its official instructions:

- [Codex CLI](https://developers.openai.com/codex/cli/),
  [non-interactive execution](https://developers.openai.com/codex/noninteractive/).
- [Claude Code setup](https://code.claude.com/docs/en/setup),
  [CLI flags](https://code.claude.com/docs/en/cli-reference).
- [Gemini CLI](https://geminicli.com/docs/get-started/installation/),
  [headless mode](https://geminicli.com/docs/cli/headless/).

Use current CLI versions supporting the flags below. This application does not
install or sign in to vendor accounts automatically. Service availability and
billing follow the account used by each CLI. No credentials are stored by this app.

Open **設定** to set:

- 作業フォルダー: the existing SolidWorksAgent project (the build package supplies its default).
- swagent.exe: `artifacts\chat-addin\cli\swagent.exe`.
- AI CLI: an absolute `.exe`, `.js` or `.mjs` path, or an executable on Windows PATH.
  Standard npm `.cmd` shims resolve to known JavaScript entry points without a shell.
- Node.js: `node.exe` or its full path when using npm-installed CLIs.

Per-provider model names are editable under **モデル選択**. Empty uses the vendor default.
Codex uses a persistent app-server thread while the pane is open. Claude/Gemini use
native CLI session IDs for subsequent messages. Switching providers keeps their sessions
separate; the UI transcript is shared visually but another provider does not inherit a
private vendor session. **新しいチャット** starts fresh sessions. Closing SOLIDWORKS
ends the pane connection; automatic transcript restoration after restart is not implemented.
Settings remain in `%LOCALAPPDATA%\SolidWorksAgent\chat-settings.json`.
Native agents can read project files and use tools under their own permission policies;
the old “only title/path plus command JSON” data boundary no longer applies.

## Supported operations

- Inspect active document / open document list / part geometry / drawing dimensions.
- Create cylinders, stepped shafts and tubes.
- Build the existing housing/bearing/shaft assembly; analyze assembly interference.
- Create associative part/assembly drawings and PDFs with automatic paper/scale, or set isometric view.

`drawing create` uses the actual model without shaft-stage, diameter or length limits.
The default is one sheet with front/top/right/isometric views; `views=all` adds
back/left/bottom views. Paper A4–A0 and scale are selected automatically or can be
specified. `dimensions=auto` adds detectable visible outer, circular and shaft-shoulder
measurements on parts; assemblies import model dimensions.
`dimensions=model` imports model dimensions (construction dimensions can be dense and
require layout review), and `none` produces views only.
Native drawing dimensions are checked again after saving/reopening, before PDF export.
Dirty or unsaved source models are copied to a unique drawing reference file, preserving
the user's source and unsaved edits. Keep that reference file alongside the drawing.

This creates a general drawing, not a guarantee of a complete manufacturing specification.
Automatic section/detail views, GD&T, fits and design-specific tolerance selection are not
implemented by this command. Those requirements must be specified and completed explicitly.
The older specialized `drawing create-shaft` CLI remains for compatibility and is no longer
exposed to the chat.
## Direct agent connection (V6)

Codex uses the official [app-server protocol](https://developers.openai.com/codex/app-server/)
over stdio: initialize, thread/start, turn/start, streamed notifications and turn/interrupt.
Subsequent messages reuse the thread; input during work is forwarded with turn/steer.
There is no response schema, catalog-only prompt, 5-minute turn deadline, host summary
request, fixed retry count or host decision about the next operation.

A `solidworks_cli` dynamic tool forwards an argument array and optional stdin to the
existing Windows CLI, pinned to the hosting SOLIDWORKS process. It returns the actual
exit code, stdout and stderr directly to Codex. All CLI commands are available, including
API discovery/execution and other existing CLI workflows. The tool serializes CAD calls
and allows in-flight saves/cleanup to finish on stop. The usual rebuild, geometry validation,
file overwrite and document ownership checks remain in the CAD implementation.

Codex keeps its native file/shell tools and Windows configuration. The thread requests
workspace-write and on-request approvals with `approvalsReviewer=auto_review`.
Codex reviews eligible escalation requests itself, so routine CAD work does not wait
for repeated user approvals. The pane does not automatically accept raw approval
requests. Exceptional user-only approvals and missing design inputs still appear in chat.
The project location, CAD executable and host PID are supplied as integration context.
The normal Codex instructions are preserved; baseInstructions are not replaced.

Claude/Gemini use native stream-json CLI output and native session resume. Their tools,
plugins and permission settings are no longer disabled by the pane. They invoke the CAD
CLI using the supplied path/PID. Headless permission support depends on the installed CLI
and its existing configuration; the pane currently relays interactive native approvals
for Codex only. Claude/Gemini process stop terminates the agent, not its CAD descendants;
wait for any active CAD operation to finish before sending another request.

This uses the vendor's agent runtime; it does not guarantee identical model selection,
installed plugins, tools or quality to a separate desktop conversation. Codex real-runtime
checks and Claude/Gemini adapter checks are documented separately below.

## Verification

```powershell
dotnet build SolidWorksAgent.sln
dotnet run --project tests\UnitTests
dotnet run --project tests\ChatTests
dotnet run --project tests\ChatUiTests
```

ChatTests includes malformed/unsafe plans, vendor response fixtures, actual
Windows subprocess argument/UTF-8 round trips, cancellation, output bounds and
all three provider adapters using local fixture processes. These tests do not
contact paid AI services and do not prove real-account authentication.

Standalone UI preview: run `SolidWorksAgent.ChatUi.V7.exe` from the addin package.
It displays the actual control without attaching to CAD. The `--smoke-test <png>`
option renders the UI and exits for visual verification.
Runtime add-in errors: `%LOCALAPPDATA%\SolidWorksAgent\addin.log`.

## Architecture

- `SolidWorksAgent.Chat`: provider adapters, protocol, command catalog, process runner.
- `SolidWorksAgent.ChatUi`: Windows Forms chat pane and settings UI.
- `src/SolidWorksAgent.SwApi/Addin`: COM entry point (`ISwAddin`), task-pane hosting.
- Existing `SolidWorksAgent.Tools` / `SwApi`: CAD execution and lifecycle.

All SOLIDWORKS COM calls remain under `src/SolidWorksAgent.SwApi`. The add-in
project is excluded from the parent SwApi project's compile glob.
The UI process is Windows; WSL is only an optional development shell.

## Local verification (2026-10-15)

- Windows .NET 8 build/publish completed.
- 42 chat/protocol checks, 8 UI workflow checks and 17 existing unit checks passed.
- The Windows Forms control was rendered by the standalone preview.
- The final COM host was registered and loaded in the running SOLIDWORKS 2026
  instance. Its automation status reported `Connected: true` and a 393 x 1070
  task-pane control after the host's resize event.
- At that verification the package was `artifacts\chat-conversation`; V6 supersedes it.
- Windows Codex authentication and a real response were verified with ChatGPT login.
  Claude/Gemini adapter checks still use fixtures.
- V3 assemblies and COM identity allow replacing the old loaded chat pane without
  restarting SOLIDWORKS or closing CAD documents. Disable/unregister the old package
  when migrating; never replace files while their assemblies are loaded.

For diagnostics, the existing CLI also supports:

```powershell
.\artifacts\chat-conversation\cli\swagent.exe chat load-addin `
  --path '.\artifacts\chat-sketch\addin\SolidWorksAgent.ChatAddin.V3.comhost.dll' `
  --solidworks-pid <SOLIDWORKS-process-id> --json
```

This checks both the SOLIDWORKS load return code and the add-in's own task-pane
status. `chat unload-addin` accepts the same path and can disable this add-in;
it does not close CAD documents. A loaded .NET assembly may remain cached until
SOLIDWORKS exits, so an in-process update can still require a restart.

Enter sends a message; Shift+Enter inserts a newline. Provider selection stays in the
compact header. The **設定**, **モデル選択** and **新しいチャット** buttons are always visible above the conversation, beneath the provider selector.

The **モデル選択** button opens a list for Codex, fetched from the configured Windows
Codex CLI using [app-server `model/list`](https://learn.chatgpt.com/docs/app-server#list-models-modellist).
It follows pagination and excludes hidden entries; model names are not hard-coded.
The picker includes the AI's default setting, refresh, and optional manual entry. Failed
lookups keep the current selection and display an error instead of a blank dialog.
Discovery starts no conversation or inference turn and closes its temporary CLI process.
Selections apply to the next turn; returning to the default explicitly resolves the
Windows Codex config so the conversation does not retain the previous model override.
The catalog can be cached by Codex; inclusion is not a guarantee of account entitlement.
Claude/Gemini list discovery is not implemented; their picker explains this and retains
default/manual selection. No API key or WSL configuration change is required.

Output versioning follows `AGENTS.md`: use one version/path during a request, with
standing overwrite permission only for that request's own working files. After completion,
a new modification request starts the next unused version of the modified artifact, preserving
completed outputs and user originals. Model versions and drawing revisions are independent:
`Part_v002.sldprt` -> `Part_v002_d001.slddrw` -> drawing-only edit `Part_v002_d002.slddrw`.
Both drawings reference the unchanged model v002. A first drawing for model v003 starts at
`Part_v003_d001.slddrw`; if that model version already has drawings, use the next unused
revision. Verify actual model/configuration references, and give PDF exports the drawing's
stem. Preserve legacy drawings; when their revision history is unknown, save the edited
copy with the actual model version and the first unused d-number starting at d001.
Retries, clarification answers and read-only requests do not increment versions. Keep CAD references intact; clean up only unnecessary temporary files created
by the current request after verification. The agent applies this policy using its native
tools; the chat host does not add a versioning or deletion control loop.

User instructions appear in right-aligned, tinted bordered bubbles; AI messages appear on the left.
Speaker names are hidden; position and background color distinguish the messages.
Message text remains selectable, including while responses stream.
Windows notifications announce a completed AI response or a request for the user's reply.
Connection failures also notify; progress updates and user-cancelled turns do not.
Click the notification (or double-click the SolidWorks AI Chat tray icon) to return to SOLIDWORKS.
Notifications contain no CAD filenames or message contents. Windows notification settings and
Do Not Disturb can suppress them. Completion means the AI response has ended; inspect the
response for the actual CAD result. The notification icon is removed when the chat pane closes.

For an end-to-end connection check through the loaded pane, run
`swagent chat check-connection --solidworks-pid <pid> --json`. This submits a
connection-only message through the same UI handler; it refuses to replace a draft,
pending proposal or active request. `chat load-addin` reports the pane status and
conversation so completion can be verified without assuming that CLI-only success
proves the embedded UI works.

## Existing sketch dimensions and relations

In the part's chat, say `既存スケッチに寸法と拘束を追加して。形状を変えずに完全定義して`.
The agent can inspect the active part and apply/verify the requested sketch operation. Changes remain in the open document, unsaved, unless you explicitly
request a save path. Existing files require the separate overwrite instruction.

The implementation adds editable driving dimensions and geometric relations, rather than
fixing all entities. It supports 2D part sketches containing lines, circles, arcs and points.
3D sketches and splines are rejected. Individual dimensions and horizontal, vertical,
parallel, perpendicular, coincident, concentric, tangent, equal, midpoint and collinear
relations are also supported. An explicitly supplied dimension must match existing geometry;
this operation does not resize the part. Fully defined sketches need no additional constraints.

CLI: `sketch inspect` returns exact sketch names, entity IDs, existing dimensions/relations,
solver status and a fingerprint. `sketch fully-define --sketch <name> --expected <fingerprint>`
repairs an existing sketch. `sketch add-dimension` takes `--kind`, `--entities` and optional
`--value` in mm; `sketch add-relation` takes `--kind` and `--entities`. Both accept
`--expected`, `--output`, `--overwrite`, `--solidworks-pid` and `--json`.
Chat always requires the inspection fingerprint before changes; stale plans are rejected.

Opt-in real SolidWorks verification: `swagent sketch verify --output <unique.SLDPRT> --json`.
It creates its own test parts, verifies driving dimensions/solver state and unchanged geometry,
then closes intermediate documents and leaves the saved final test part open.

### Sketch implementation notes

COM calls remain in `SolidWorksAgent.SwApi/Parts/SketchEditor.cs`. Public inspection values
use millimetres (angles use degrees). The COM boundary converts lengths to metres.
FullyDefineSketch uses the actual origin feature name and datum selection mark 6.
Late-bound dispatch parameters require `DispatchWrapper`, including null datum objects
and arrays passed to AddRelation. Rollback removes only newly added dimensions/relations
and verifies restored geometry; it never blindly undoes a previous user operation.

API references: [FullyDefineSketch](https://help.solidworks.com/2015/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.ISketchManager~FullyDefineSketch.html),
[SetCoords](https://help.solidworks.com/2018/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISketchPoint~SetCoords.html).

## General drawing update (V4)

V4 replaces the restricted shaft-only chat command with `drawing create`. The new
managed/COM identity permits loading the update into the running SOLIDWORKS session.
Windows CLI authentication and WSL configuration are unchanged.

Validation: two-stage 30 mm shaft, imported curved/sheet-metal part with seven views,
and an assembly; native drawings and PDFs reopened/checked in SOLIDWORKS.

## Extensible API operations (V5)

The CLI supports discovery of installed API signatures and structured feature
operations using `api search`, `api describe` and `api execute`. The plate command
also supports corner fillets and ISO tapped-hole positions. See
[API automation, examples and coverage](api-automation.md). The old fixed catalog
is no longer the sole route for feature creation. V5 uses separate managed and COM
identities so it can be loaded while an older pane is resident.

## Native connection verification

The V6 test suite covers direct input, native session reuse, streamed text, steering,
user-input/approval forwarding, cancellation and a tool error followed by a tool success.
Native Codex integration is exercised with synthetic tool data, not the user's CAD model.
Claude/Gemini native adapters need the corresponding installed/authenticated Windows CLI;
real CAD execution through those vendors has not been verified.

When copying a portable Codex runtime, copy its matching helper executables too:

```powershell
.\scripts\Copy-CodexRuntime.ps1 -Destination '.\artifacts\chat-native\tools'
```

A lone `codex.exe` may answer text but fail to execute tools because
`codex-code-mode-host.exe` is absent. The script copies and hashes the complete matching
Windows executable set from the installed Codex app (or `-SourceDirectory`). It does
not change Codex account configuration, sandbox configuration or WSL settings.

Local V6 verification (2026-10-16): 29 core checks, 52 legacy protocol/process checks,
4 native-session transport checks and 7 UI checks passed (92 total). Real Codex
app-server made three synthetic tool calls: failed trial, inspection, corrected success.
The matching native runtime is in `artifacts\chat-native\tools`. The V6 COM pane was
registered and confirmed connected in the running SOLIDWORKS instance; V5 was unloaded.
No CAD document was modified by the native-agent synthetic test. WSL settings are unchanged.

## Automatic approval review (V7)

Both thread/start and turn/start explicitly select Codex's native auto-review reviewer.
The sandbox remains workspace-write, with on-request escalation. Approval policy is not
changed globally, and no Windows/WSL Codex configuration file is modified. A denied
review remains denied; the agent can diagnose and try an allowed alternative.
V7 has a separate COM/assembly identity for updating a running SOLIDWORKS session.

V7 verification (2026-10-16): 92 automated checks passed. A real Codex native-shell
connection check first failed to find SOLIDWORKS in the sandbox's Running Object Table,
then requested escalation, passed automatic review and returned exit code 0 with
`connected: true`. The client received zero manual approval requests. This test only
connected; it did not read or modify CAD documents. The updated package is
`artifacts\chat-auto-review`, reusing the complete runtime in `artifacts\chat-native\tools`.

## Settings controls and error handling (2026-10-16)

The popup menu has been removed. Three buttons at the top of the pane open settings,
select a model, or start a new chat. They stay inside the pane, avoiding the popup
placement problem on multiple monitors. All three are disabled during an active turn.
Settings callbacks catch and log failures in `addin.log` and report them in chat;
model selection is saved when the dialog is accepted. File-picker failures are
reported in the settings dialog.

The former menu disposed itself in `Closed`, while WinForms was still handling its
visibility/click events. An isolated build of that implementation reproduced
`ObjectDisposedException` from `ToolStripDropDown.SetVisibleCore` on closure.
Removing the popup also removes that lifecycle problem.

The 11 UI workflow checks pass under both `Application.Run` and a native Win32
message loop. They cover model selection, connection-settings cancellation, session
reset and recovery from an asynchronous settings error, alongside existing chat
workflows. The build completed without warnings/errors, and a rendered preview
confirmed the three buttons. Actual SOLIDWORKS clicks were not automated because
the desktop automation tool could not initialize with this WSL workspace path.

The UI update uses the existing V7 identity. Replace the UI DLL/PDB in the registered
`artifacts\chat-auto-review\addin` package while SOLIDWORKS is closed, keeping a backup
of the previous files. No COM re-registration is required. Restart SOLIDWORKS to load
the update.

## Enter and Shift+Enter in the native task pane (2026-10-16)

The chat input now explicitly claims Enter via `WM_GETDLGCODE` and enables
`AcceptsReturn`. Its key handler submits on Enter and inserts one newline at the
caret (replacing the selection) on Shift+Enter. IME composition/confirmation is
handled separately so confirming Japanese text does not submit the chat.
Only the chat input claims Enter; no global keyboard hook or SOLIDWORKS shortcut
configuration is changed.

The Windows test host now calls `IsDialogMessage` before dispatching native
messages, reproducing the parent dialog's handling of Enter. The previous input
passed both keyboard tests in a standalone WinForms loop but failed both in this
native dialog loop. The replacement passes all 15 UI checks in both loops,
including queued Enter/Shift+Enter messages, empty input, selection replacement,
and simulated IME composition messages. A real Japanese IME and physical keystrokes
inside SOLIDWORKS still need user verification. Build/publish completed with no
warnings/errors.

References: [WM_GETDLGCODE](https://learn.microsoft.com/en-us/windows/win32/dlgbox/wm-getdlgcode).

## Attach images and reference files (2026-10-16)

Drag one or more files from Windows Explorer onto the chat, or use **＋ 添付**.
The filenames appear above the input box; **×** removes an unsent attachment.
Then enter an instruction such as `この図面をモデル化して。寸法はmmです。` and send.
Dropping alone does not contact the AI. Attachments can be added while the agent is
idle. Sending just attachments asks the agent to inspect them. If sending fails,
the attachments and draft remain available for retry.

Codex receives PNG, JPEG and WebP files through native `localImage` inputs alongside
the user message. Other files (including PDFs, CAD, text, BMP and TIFF) are provided
as local references for the agent's native tools to read, render or inspect. Claude
and Gemini receive the reference paths through their existing CLI prompts and use
their own file tools; those providers' actual file-reading capability depends on
the installed CLI and model. No new API key is required. A drawing still needs enough
information to determine dimensions; the agent is instructed to ask about missing
sizes rather than claim it inspected unavailable contents.

Up to 20 files / 100 MB total can be attached per message. Folders and virtual mail
attachments are not accepted; save those files locally first. Files are copied into
unique folders under `workspace/chat-attachments`, so equal names cannot overwrite
each other and the originals are preserved. These copies are ignored by Git and
retained for subsequent turns; removing a chip or starting a new chat does not delete
referenced disk files. Unsaved attachment chips are not restored after restarting
the pane. Native CAD files that depend on sibling references may require those
references to be supplied separately.

Validation: full solution build and existing core/chat checks passed, plus two
attachment transport/storage checks and four UI attachment workflows. All 19 UI
checks passed under both managed and native dialog loops. A real logged-in Codex
session read the code printed only inside a synthetic image, described its two blue
squares and red circle, and independently read the code in an attached text file.
That check did not access SOLIDWORKS or modify CAD. Explorer-to-SOLIDWORKS drag input
and other providers were not tested on the live host.

Protocol reference: [Codex app-server](https://learn.chatgpt.com/docs/app-server).

### Task pane icon

The sidebar uses the supplied artwork in `src/SolidWorksAgent.SwApi/Addin/Assets/taskpane-source.png`.
The six indexed PNG sizes (20, 32, 40, 64, 96, 128) are included in build/publish output and
selected by SOLIDWORKS for the display scale through
[`CreateTaskpaneView3`](https://help.solidworks.com/2025/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISldWorks~CreateTaskpaneView3.html).
Run `scripts/Build-TaskPaneIcons.ps1` on Windows after replacing the source artwork.
The chat remains accessible with the default icon if the image files are missing.
