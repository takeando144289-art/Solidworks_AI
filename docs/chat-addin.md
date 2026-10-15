# SolidWorks AI Chat (Windows)

Windows x64 SOLIDWORKS task-pane add-in. Choose **Codex / Claude / Gemini**,
enter a request, review proposed changes in the conversation, and reply **実行して**.
Read-only inspections run automatically and report their results in the conversation.
Reply **やめて** to cancel a proposal or stop after the current CAD operation.
The existing `swagent.exe` performs CAD work. No WSL component is used at runtime.

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

Open **⋯ > 接続設定…** to set:

- 作業フォルダー: the existing SolidWorksAgent project (the build package supplies its default).
- swagent.exe: `artifacts\chat-addin\cli\swagent.exe`.
- AI CLI: an absolute `.exe`, `.js` or `.mjs` path, or an executable on Windows PATH.
  Standard npm `.cmd` shims resolve to known JavaScript entry points without a shell.
- Node.js: `node.exe` or its full path when using npm-installed CLIs.

Per-provider model names are editable under **⋯ > モデル…**. Empty means the CLI
default; availability depends on the authenticated account. Conversation context
and execution results are retained when switching providers, but vendor-native
conversation sessions are not shared. The last 24 messages are sent as context.
**⋯ > 新しい会話** clears the in-memory history. Conversations are not persisted by this UI.
Settings are stored in `%LOCALAPPDATA%\SolidWorksAgent\chat-settings.json`.
The model sees conversation text, the active document's title/path/type and
the JSON results of operations you execute. It is not sent entire CAD files by this app.
Vendor CLIs may retain their own logs according to their settings.

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
The chat does not yet expose the experimental sheet-metal, knee-linkage, or
document-discard CLI commands. Unsupported requests are answered or clarified.

## Execution behavior

AI replies contain a validated JSON operation plan. The app validates the entire
plan before presenting it; it never executes AI-generated shell text or code.
Missing dimensions, unknown commands/arguments, duplicate options and non-finite
dimensions are rejected. Every `swagent` invocation uses an argument array and
the process ID of the SOLIDWORKS instance hosting the pane. Active document
title/path/type are rechecked immediately before execution; users should leave
the CAD document unchanged while an operation is running.

Existing outputs are protected by default. Reply **上書きを許可して実行** to
explicitly pass `--overwrite` for the currently reviewed generation batch only.
AI cannot grant this option itself. Negations, quotations and revised requests do not approve a pending batch.
A revised request cancels the old proposal. Approval is consumed once, including when execution fails. The existing rebuild, validation, save and
document cleanup logic stays in the SwApi layer. A failed or partially completed
plan is consumed and cannot accidentally be replayed by sending approval again.

AI calls are asynchronous, cancellable, and limited to five minutes. CAD commands
finish their current rebuild/save/cleanup before stopping the remaining batch;
they are not forcibly terminated midway. Closing the pane cancels an AI request
and stops the batch after its current CAD command.

Provider modes:

- Codex: `exec`, read-only sandbox, shell tools disabled, schema-constrained final output.
  It uses the user's existing Windows Codex account/configuration; installed custom
  integrations remain subject to that CLI's configuration. The provider is a trusted
  local agent, not an isolation boundary for arbitrary third-party plugins.
- Claude: print mode, built-in and MCP tools disabled, empty settings sources,
  no session persistence, JSON schema.
- Gemini: headless JSON, extensions disabled, child-only system settings overlay
  preserving existing system settings and adding admin deny rules for all tools.
  No user/global Gemini settings file is modified.

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

Standalone UI preview: run `SolidWorksAgent.ChatUi.V5.exe` from the addin package.
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
- This machine's registered package is `artifacts\chat-conversation`.
  Earlier development packages under `artifacts` are not the registered final product.
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
compact header. Connection settings, model and new conversation are under the ⋯ menu.

For an end-to-end connection check through the loaded pane, run
`swagent chat check-connection --solidworks-pid <pid> --json`. This submits a
connection-only message through the same UI handler; it refuses to replace a draft,
pending proposal or active request. `chat load-addin` reports the pane status and
conversation so completion can be verified without assuming that CLI-only success
proves the embedded UI works.

## Existing sketch dimensions and relations

In the part's chat, say `既存スケッチに寸法と拘束を追加して。形状を変えずに完全定義して`.
The assistant first inspects the active part, then proposes the named sketch operation. Reply
`実行して` to apply it. Changes remain in the open document, unsaved, unless you explicitly
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

The chat now discovers installed API signatures and can propose structured feature
operations using `api search`, `api describe` and `api execute`. The plate command
also supports corner fillets and ISO tapped-hole positions. See
[API automation, examples and coverage](api-automation.md). The old fixed catalog
is no longer the sole route for feature creation. V5 uses separate managed and COM
identities so it can be loaded while an older pane is resident.
