# Claude Code ↔ Visual Studio

**English** · [Français](README.fr.md)

Drive Visual Studio from Claude Code: build, read the errors, set breakpoints, step through the
debugger and inspect variables, all without leaving your terminal.

The extension hosts an [MCP](https://modelcontextprotocol.io) server inside Visual Studio and exposes
the IDE as a set of tools that Claude Code can call.

**Visual Studio 2022 (17.x) and 2026 (18.x)** · **C# / .NET** projects

---

## What it changes

Without the extension, Claude Code edits files and runs `dotnet build` blindly. With it, Claude
works inside your IDE:

- "Build and fix the errors" → it builds, reads the structured error list with code
  (`CS0103`…), file, line and column, fixes the errors, and builds again.
- "Set a breakpoint on line 42 and tell me why `total` is negative" → it sets the breakpoint,
  starts debugging, waits for the break, then reads the locals and the call stack.
- "Trace the value of `i` on every pass without stopping the program" → it sets a tracepoint.
- "Where does this `InvalidOperationException` swallowed by a `catch` come from?" → it makes the
  debugger break when the exception is thrown and reads it: type, message, source file and line.
- "Who calls `Apply`?" → it asks Roslyn, which has already analyzed the solution, for the
  references: same-name symbols are excluded and overloads told apart, which a text search cannot do.
- "Rename `Total` to `Amount` everywhere" or "add the missing using" → it goes through Roslyn
  (`rename_symbol`, `apply_code_fix`) instead of editing text, and nothing is applied if the change
  breaks the build.
- "Run the `OrderService` tests and tell me why they fail" → it runs them in Test Explorer and
  reads, test by test, the error message and the stack; if needed, it reruns the failing test
  under the debugger.
- "The program is frozen, take a look" → it pauses it and reads the stacks of all threads,
  grouped: a deadlock stands out at once.
- "Look at what I selected" → it reads your selection in the editor or the console.

Control of the debugger is what makes the difference: no command-line tool can replace it.

---

## Installation

**1. Get the extension**

Install **Claude Code MCP Bridge** from the
[Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=MMedard.claude-vs-mcp-bridge),
or download `ClaudeCodeVsMcp.vsix` from the
[latest release](https://github.com/R3C-0N/Visual-Studio-Claude-MCP/releases/latest).

**2. Install**

Close Visual Studio, then double-click the `.vsix`.

**3. Register the server with Claude Code**

Restart Visual Studio and open a solution. In the **Output** window, choose **Claude MCP** in the
"Show output from" list. The command to copy is there, token included:

```
claude mcp add --transport http visual-studio http://127.0.0.1:5230/mcp --header "Authorization: Bearer <your-token>"
```

The token is specific to your machine and generated on first launch. Add `--scope user` to make
the server available from any directory, not only the one where you run the command.

**4. Check**

```powershell
claude mcp list        # visual-studio ... ✔ Connected
```

That's it. Ask Claude to build your solution to confirm.

---

## Available tools

| Area | Tools |
|---|---|
| Solution | `solution_info`, `list_projects`, `open_file`, `set_startup_project`, `set_configuration` |
| Navigation | `find_symbol`, `go_to_definition`, `find_references` |
| Refactoring | `rename_symbol`, `apply_code_fix` |
| Documents | `get_dirty_documents`, `save_documents` |
| Build | `build`, `build_status`, `cancel_build`, `get_errors` |
| Tests | `list_tests`, `run_tests`, `test_status`, `test_results`, `cancel_tests`, `debug_tests` |
| Debugger | `debug_start`, `debug_stop`, `debug_pause`, `debug_continue`, `debug_step`, `debug_state`, `run_to_line`, `set_next_statement`, `debug_attach`, `debug_detach` |
| Threads | `list_threads`, `switch_thread`, `get_all_stacks` |
| Breakpoints | `set_breakpoint`, `list_breakpoints`, `update_breakpoint`, `clear_breakpoints` |
| Inspection | `evaluate`, `get_locals`, `get_stack` |
| Exceptions | `break_on_exception`, `get_exception` |
| Output | `list_output_panes`, `get_output_pane` |
| Selection | `get_selection`, `take_pending_context` |
| UI | `ui_windows`, `ui_snapshot`, `ui_action`, `execute_command` |
| Dialogs | `get_dialog`, `answer_dialog` |
| Instances | `list_instances`, `use_instance`, `server_info` |

Long operations (build, run) accept a `wait_ms`. Past it, the response carries the status
`running`: this is not an error but a state to poll again.

### A single target for multi-targeted projects

A build started by `build` compiles only one target (TargetFramework) of multi-targeted projects,
for example `net10.0-windows10.0.19041.0` without `net10.0-android`. By default, this is the IDE's
active target, chosen in the toolbar drop-down. `set_configuration` changes it for the session:

| `framework` | Effect |
|---|---|
| a TFM, e.g. `net10.0-windows10.0.19041.0` | Only this target is built, in every project that declares it |
| `ide` | Back to the IDE's active target (default behavior) |
| `all` | All targets, like Visual Studio's *Build* command |

The `build` response repeats the target in its `framework` field. Projects that do not declare this
target, for example a `net8.0` library, build normally. Nothing is written to disk: no `.csproj`, no
`.csproj.user`, no change to the IDE drop-down. Builds started by hand in Visual Studio are not
affected.

Visual Studio already does the same with F5, but only for the startup project. The bridge supplies
the `TargetFramework` build property to the project system during its own builds, through the public
`ExportBuildGlobalPropertiesProvider` extension point.

`debug_stop` terminates the debugged processes directly instead of issuing Visual Studio's *Stop
Debugging* command, which, depending on the project, opens a modal confirmation. Pass
`force: false` to get the original command back, which is useful if the session is attached to a
process that must not be killed. If Visual Studio then asks for confirmation, `debug_stop` answers
**Yes** and reports it in `confirmedDialogs`; with `confirm: false`, it returns with the status
`dialog_open` and the content of the dialog.

### Modal dialogs

While a modal dialog is open, Visual Studio processes no commands at all. The extension detects
them without going through the UI thread they block:

- the debugging tools (`debug_start`, `debug_continue`, `debug_step`, `debug_pause`,
  `debug_stop`) return as soon as a dialog appears, with the status `dialog_open`, instead of
  waiting for their timeout;
- any other tool whose command is blocked by a dialog (a build started while debugging opens "Do
  you want to stop debugging?") returns in under a second with the error `DIALOG_OPEN`, which
  describes the dialog. The command resumes once the dialog is answered;
- `get_dialog` reads the open dialogs (title, message, buttons) and `answer_dialog` clicks a
  button by its label, its `id`, or a language-independent standard answer (`yes`, `no`, `ok`,
  `cancel`…).

These two tools run even while another call is in progress on the instance. Native dialogs
(MessageBox) are read through Win32, WPF dialogs through UI Automation.

### Tests

The test tools go through Visual Studio's Test Explorer, whatever the framework (MSTest, xUnit,
NUnit…), and return structured results instead of the window's text:

```
list_tests   filter: "OrderService"                        → full names, state, project, target
run_tests    tests: ["MyApp.Tests.OrderServiceTests.Total"] → summary: passed/failed/skipped/notRun
test_results outcome: "failed"                             → error message and stack of each failure
debug_tests  tests: ["MyApp.Tests.OrderServiceTests.Total"] → break on the breakpoint
```

- `run_tests` targets full names (`tests`), a text (`filter`, `project`), the failures to rerun
  (`failed_only`) or, with no argument, all tests. The rows of a theory share the same full name:
  `targets` counts names, `total` counts Test Explorer entries.
- Test Explorer first builds what has changed. If the build fails, Visual Studio asks whether to
  run the last successful build: the response then carries the status `dialog_open` and the dialog.
  Answer with `answer_dialog`: `No` gives the status `not_started`, to be read with `get_errors`;
  `Yes` runs the old binaries.
- As with `build`, past `wait_ms` the response carries the status `running`; `test_status` gives
  the phase (`building`, `discovering`, `running`…) then the summary. It also sees runs started by
  hand from Test Explorer, but only once they start: during the build that precedes them, it still
  shows the previous summary with a note.
- `cancel_tests` stops a run at any time, build included (status `cancelled`).
- A targeted test that did not run appears as `notRun` rather than with its old result.
- `debug_tests` requires that no debugging session is in progress. At the `break` status,
  `get_stack`, `get_locals` and `evaluate` work as usual; a failing assertion also breaks the
  debugger, as in Test Explorer.
- A test's file and line are given only if Visual Studio knows them, which is rare before a first
  run: `find_symbol` on the full name finds them. The stack of a failure always carries them.

These tools rely on internal Test Explorer services that Microsoft does not document (see *Known
limitations*). The approach owes a lot to the exploration published by
[VsDebugMcp](https://github.com/wuxinmao2008/VsDebugMcp) and
[VS-MCPServer](https://github.com/CodingWithCalvin/VS-MCPServer).

### Driving any window

Windows without an automation API are driven through their UI. `ui_snapshot` returns the tree of
WPF controls as JSON: each node carries an `id`, its type, its name, its value, its state
(`toggle`, `expanded`, `selected`) and the list of actions it accepts. `ui_action` applies an
action to an `id`: `click`, `invoke`, `toggle`, `expand`, `collapse`, `select`, `set_value`,
`scroll_into_view`, `focus`.

```
ui_snapshot  window: "Solution Explorer"        → tree: toolbar, solution hierarchy
ui_action    id: 412, action: "expand"           → expands a project
ui_snapshot  root_id: 412                        → its items, now materialized
ui_action    id: 418, action: "select"           → selects a file
execute_command command: "Edit.OpenWith"
```

`ui_windows` lists the captions of the open windows, so you know what to pass to `window`. By
default `interactive_only` keeps only named, valued or actionable nodes and flattens silent
containers; `filter` further reduces the tree to nodes containing a text. The `id`s stay valid as
long as the control exists: after an action that rebuilds the window, take a new snapshot.
Virtualized lists expose only the displayed items: expand or scroll, then take another snapshot.

`execute_command` runs a named Visual Studio command (`View.TestExplorer`,
`TestExplorer.RunAllTests`, `Edit.FormatDocument`…), which often avoids going through the UI.

---

## Breakpoints

`set_breakpoint` covers five variants, which can be combined:

| Variant | Parameter | Behavior |
|---|---|---|
| Ordinary | — | breaks execution |
| Conditional | `condition` | triggers if the expression is true, or when its value changes |
| Tracepoint | `message` | logs **without** breaking |
| Temporary | `temporary` | deletes itself after its first hit |
| Dependent | `depends_on` | stays disabled until another breakpoint is hit |

On top of that, `hit_count` with its mode (`equal`, `greater_or_equal`, `multiple`) and `filter`
to restrict it to a thread.

A tracepoint's message accepts Visual Studio's syntax, with expressions in braces and
pseudo-variables:

```
i = {i}, called by $CALLER on thread $TID
```

Breakpoints are identified by `path:line`, the format expected by `depends_on` and
`update_breakpoint`.

---

## Exceptions

`break_on_exception` sets the *Exception Settings* window:

| `break_when` | Behavior |
|---|---|
| `thrown` (default) | breaks as soon as it is thrown, even if a `catch` then handles it |
| `user_unhandled` | breaks when it leaves user code unhandled (*Just My Code*) |
| `never` | no longer breaks |

`exception` accepts a full name (`System.InvalidOperationException`), a short name if
unambiguous (`NullReferenceException`), or several comma-separated names. An unknown full name (an
exception from the project) is added to the group. Without `exception`, the tool lists the ones
that break when thrown. `group` targets a group other than .NET (`C++`, `Win32`…), `reset_all`
restores the default settings. The setting applies to the current session as well as the next ones.

When the debugger breaks on an exception, `debug_start` and `debug_continue` report it, and
`get_exception` describes it:

```json
{
  "stoppedAt": "thrown",
  "type": "System.InvalidOperationException",
  "message": "Sequence contains no elements",
  "hresult": "0x80131509",
  "origin": { "method": "MyApp.Orders.Last(...)", "file": "C:\src\MyApp\Orders.cs", "line": 42 },
  "stackTrace": [ ... ],
  "innerExceptions": [ ... ]
}
```

The stack comes from the exception's `StackTrace`, with file and line when symbols are available.
`AggregateException`s detail all their inner exceptions (`aggregated`). For a native or SEH
exception, with no object to inspect, `get_exception` returns the code and description reported
by the debugger.

---

## Code navigation

`find_symbol`, `go_to_definition` and `find_references` query Roslyn on the solution Visual
Studio has already loaded: no second load, and the result is exact where a text search confuses
same-name symbols, overloads and interface implementations.

A symbol can be designated in two ways:

- **by an occurrence**: `file`, `line` and `symbol`, the identifier as written on that line. This
  is the safest, since Claude has usually just read the file;
- **by its name**: `name`, simple or qualified (`ExceptionBreaks.Apply`). If it is ambiguous, the
  error lists the candidates.

```json
{ "file": "src/MyApp/Orders.cs", "line": 42, "symbol": "Last" }
```

Each result gives file, line, column, line text and project. `find_symbol` accepts a pattern
(`ExBr` finds `ExceptionBreaks`), `go_to_definition` returns all parts of a `partial` type and can
open the editor (`open`). A symbol from the framework or a NuGet package returns its assembly.

### Unsaved changes

A file modified in Visual Studio but not saved differs from what Claude reads on disk.
`get_dirty_documents` (optionally on a single `path`) lists them before a change,
`save_documents` saves them. Navigation results carry `unsaved: true` when their lines come from
such a buffer.

---

## Refactorings

`rename_symbol` renames a symbol across the whole solution, like F2; it is designated as for
navigation (`file` + `line` + `symbol`, or `name`). `apply_code_fix` reproduces the light bulb:
without `fix`, it lists the diagnostics of a line and the proposed fixes; with `fix` (index or part
of the title), it applies the chosen one.

```json
{ "file": "src/MyApp/Orders.cs", "line": 12, "code": "CS0246", "fix": "using System.Text" }
```

Both tools follow the same protocol:

- `preview: true` describes each affected file, line by line (before / after), without writing
  anything;
- the build errors the change **would add** are listed in `newErrors`. If there are any, nothing
  is applied, unless `force: true` is set: this is what stops a rename to a name already in use;
- a file shared by several targets of a multi-targeted project receives the same change everywhere.

The fixes come from Visual Studio (add a `using`, implement an interface, `IDE*` rules…) and from
the project's analyzer packages.

---

## Threads and attach

When the program is paused, `get_all_stacks` gives each thread's stack, grouping identical stacks
like the *Parallel Stacks* window: fifty pool threads blocked at the same spot fit in one entry, and
two threads waiting on each other stand out at once. `list_threads` summarizes each thread by its
first frame in your code, `switch_thread` makes it the current thread for `get_stack`, `get_locals`
and `evaluate`.

`run_to_line` runs to a line without a breakpoint; `set_next_statement` moves the next statement
within the current function, for example to replay a block after fixing a variable.
`debug_attach` attaches to an already running process, by PID or by name (`w3wp`, a service…),
and `debug_detach` lets it continue without the debugger.

---

## Sending a selection from Visual Studio

In the editor (`Ctrl+Alt+Shift+C` or right-click) and in the Output window's context menu:
**"Envoyer a Claude Code"** (Send to Claude Code). The selection is captured at the moment of the
click, so Claude does not read something else while you are writing your request.

- If Claude Code is connected to the IDE (`/ide` in the terminal), the selection is pushed
  directly into the prompt.
- Otherwise it is queued, and Claude retrieves it with `take_pending_context`.

`get_selection` also reads the current selection on demand, without sending anything.

---

## Several Visual Studio instances

Each instance registers itself, and Claude Code keeps a single URL: `http://127.0.0.1:5230/mcp`.

- A single instance open → nothing to do.
- Several → `list_instances` to see them, `use_instance` to choose, or the `instance` parameter
  on each call.
- When it is ambiguous, the tools **refuse to guess** and return the list of choices: a build
  started in the wrong solution costs more than a question.

If the instance acting as the entry point closes, another one takes over the role in less than ten
seconds, with no reconfiguration.

---

## Never reconnect again: the stdio relay

Claude Code retries an HTTP connection only briefly: 3 attempts when the session opens, about 30
seconds after a disconnection. After that, the server is marked as failed until a manual `/mcp` →
**Reconnect**. This happens if Claude Code starts before Visual Studio, or if Visual Studio is
closed for a while.

`tools\vs-mcp-relay.mjs` solves this. Claude Code launches it as a **stdio** server, so it is
always connected, and the relay forwards each call to the hub when it arrives:

- without Visual Studio, a tool call waits up to 30 s, which covers a hub takeover, then answers
  "Visual Studio is not reachable" instead of failing the server;
- the tool list is cached and served even when Visual Studio is closed;
- as soon as Visual Studio comes back, the relay notifies Claude Code (`tools/list_changed`):
  there is nothing to do.

The token is reread from `%APPDATA%\claude-vs-mcp\token` on each request, so it no longer appears in
the configuration. Node 18 or later, no dependencies.

```powershell
claude mcp remove visual-studio --scope user
claude mcp add visual-studio --scope user -- node "<path>\tools\vs-mcp-relay.mjs"
```

| Variable | Role | Default |
|---|---|---|
| `CLAUDE_VS_MCP_URL` | Hub URL | `http://127.0.0.1:<port>/mcp` |
| `CLAUDE_VS_MCP_HUB_PORT` | Hub port, if the URL is not given | `5230` |
| `CLAUDE_VS_MCP_TOKEN` | Token, instead of the file | reread from `%APPDATA%` |
| `CLAUDE_VS_MCP_WAIT_MS` | Maximum wait for Visual Studio per tool call | `30000` |

The relay's log goes to stderr, into Claude Code's MCP logs.

---

## Security

- Listens on `127.0.0.1` only, never on an external interface.
- Mandatory token, compared in constant time, stored in `%APPDATA%\claude-vs-mcp\token`.
- `Origin` and `Host` headers validated: without this, a web page open in your browser could drive
  Visual Studio through DNS rebinding.

**Keep in mind**: this server effectively grants arbitrary code execution. A build runs the MSBuild
targets and pre/post-build scripts of the open repository, and the debugger starts processes. It
runs with your privileges. Do not use it on a machine where you open code you do not trust.

---

## Known limitations

- **Navigation covers only C# and VB**: Roslyn does not analyze C++. On a modified, unsaved file,
  positions follow the editor buffer, not the disk.
- **A frame's file and line require symbols** (`get_stack`, `debug_state`…): without a PDB, a
  frame has only its function and module.
- **Visual Studio comes to the foreground** at each debugger break and when a tool places the
  cursor (`run_to_line`, `set_next_statement`): the tools immediately give focus back to the window
  you were using, but a keystroke made in that interval can land in the editor.
- **The stacks are thread stacks**, not logical `async` stacks: an awaiting task with no thread
  does not appear in `get_all_stacks`.
- **`set_next_statement` stays within the current function**, and what is skipped or replayed is
  not undone: a replayed side effect runs twice.
- **`apply_code_fix` only offers diagnostic fixes**: light-bulb refactorings without a diagnostic
  (extract method…) are not included. `rename_symbol` does not rename the file of a renamed type.
- **`get_exception` evaluates `Message`, `StackTrace`…** on the exception, and so runs code from
  the debugged program: a custom exception that overrides `Message` executes it.
- **`evaluate`, `get_locals` and `get_stack` require a break** on a breakpoint: the expression
  evaluator does not exist while the program is running. Evaluating can have side effects, as a
  called property runs its code.
- **A breakpoint's condition cannot be modified** after creation. `update_breakpoint` recreates it
  in that case, which resets its hit count to zero.
- **Temporary and dependent breakpoints are emulated** by the extension, since Visual Studio does
  not expose them: they do not survive a solution reload and do not appear as such in the
  Breakpoints window.
- **The output of a console application launched in an external console** does not appear in
  `get_output_pane`.
- **A modal dialog freezes automation** until it is answered (`answer_dialog`). Task dialogs
  (*TaskDialog*) let neither their text nor their buttons be read: `answer_dialog` tries the
  standard answers blindly. Remember to enable automatic reloading of files modified outside the
  editor, otherwise every change made by Claude opens a dialog.
- **The test tools rely on internal APIs** of Test Explorer (`ITestsService`, `OperationBroker`),
  read through reflection: a Visual Studio update can break them. They then answer `UNSUPPORTED`,
  naming the missing member; until a fix, `execute_command` (`TestExplorer.RunAllTests`…) and
  `ui_snapshot` remain available.
- **Tests are known only after their discovery**, which follows the build. On a freshly opened
  solution, Test Explorer can take several minutes to return its list: `list_tests` then answers
  `loading` (or `discovering`) after 15 s instead of blocking the instance; just call it again.
- **The test tools query the whole Test Explorer list** on each call, which Visual Studio flags as
  expensive: around a second for a few hundred tests, more on a large solution. Prefer
  `test_status` to follow a run.
- **`ui_snapshot` only sees the WPF UI**: hosted Win32 or WinForms areas and native dialogs do not
  appear in it. Nor do the hidden items of a virtualized list.
- Only one tool runs at a time per instance: Visual Studio automation handles reentrancy poorly.

---

## Troubleshooting

| Symptom | Lead |
|---|---|
| Nothing in the "Claude MCP" pane | `devenv /log`, then read `%APPDATA%\Microsoft\VisualStudio\<version>\ActivityLog.xml` |
| `claude mcp list` fails | Check first with `tools\Smoke-Test.ps1`: it isolates the transport from the client configuration |
| Port 5230 already in use | `Get-NetTCPConnection -LocalPort 5230`, or set `CLAUDE_VS_MCP_HUB_PORT` |
| Wrong instance driven | `list_instances` then `use_instance` |

The extension's log is written to the **Claude MCP** pane and duplicated in
`%TEMP%\claude-vs-mcp\extension-<pid>.log`.

---

## Development

```powershell
pwsh tools\Build.ps1              # builds, produces the .vsix
pwsh tools\Build.ps1 -Install     # installs (Visual Studio closed, elevated terminal)
pwsh tools\Smoke-Test.ps1         # tests the server over raw HTTP, without Claude Code
pwsh tools\New-Release.ps1        # tags, pushes and publishes a release
pwsh tools\Publish-Marketplace.ps1  # publishes the Release .vsix to the Visual Studio Marketplace
```

`F5` deploys to the experimental hive (`/rootsuffix Exp`) without touching your main
installation.

The Marketplace page uses `src\ClaudeCodeVsMcp\marketplace-overview.md`, a short English overview,
not this README.

**Git hooks**: enable them once per clone, since `core.hooksPath` is a local setting:

```powershell
git config core.hooksPath .githooks
```

`pre-commit` and `pre-push` reject any non-public e-mail address. The pattern can be overridden
with `git config hooks.allowedEmail`.

**Versioning**: the version lives in a single place, the `Version` attribute of
`source.extension.vsixmanifest`; `AssemblyVersion` and the version announced by the server are
derived from it at build time. Every change increments it: patch for a fix, minor for a new
capability, major for a breaking change. Visual Studio's update mechanism depends on it, since
`VSIXInstaller` only replaces an installed extension in place if the version increases.

### Three pitfalls to know before contributing

1. **COM event objects must be kept in fields.** `dte.Events.BuildEvents` returns a new object on
   each access; without a strong reference, the garbage collector collects it and events stop
   arriving without any error.
2. **Never use a blocking overload** (`Build(true)`, `Go(true)`): the UI thread is the one the
   build and the debugger need to make progress. Always `false` then wait for the event, and never
   `.Result` on the UI thread.
3. **Output panes are localized** ("Générer" on a French IDE). They are resolved by GUID, never by
   name.

---

## License

[Apache License 2.0 with Commons Clause](LICENSE).

Use, modification, redistribution and forking are **free**, including in companies and for
professional use. The only thing forbidden is **selling** the software in the sense of the Commons
Clause: providing third parties, for a fee, with a product or service whose value derives entirely
or substantially from its functionality, which covers resale, paid hosting and paid support based
on it.

This combination is not an open-source license in the OSI sense, because of the commercial
restriction. For use outside this scope, contact the copyright holder.
