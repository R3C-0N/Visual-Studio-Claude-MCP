# Claude Code MCP Bridge

Let [Claude Code](https://claude.com/claude-code) work inside Visual Studio instead of around it.
The extension hosts an [MCP](https://modelcontextprotocol.io) server in the IDE, so Claude can build,
debug, run tests and refactor your solution the way you would.

**Visual Studio 2022 (17.x) and 2026 (18.x)** · **C# / .NET** projects

## What you can ask

- *"Build and fix the errors"*: Claude builds, reads the structured error list (code, file,
  line, column), fixes the errors and builds again.
- *"Break on line 42 and tell me why `total` is negative"*: Claude sets the breakpoint, starts
  debugging, then reads the locals and the call stack.
- *"Where does this swallowed `InvalidOperationException` come from?"*: Claude breaks when the
  exception is thrown and reports its type, message and origin.
- *"Run the `OrderService` tests and explain the failures"*: Claude runs them in Test Explorer, reads
  each failure's message and stack, and can debug the failing test.
- *"Rename `Total` to `Amount` everywhere"*: Claude uses Roslyn, with a preview, and applies
  nothing that would break the build.
- *"The app is frozen, take a look"*: Claude pauses it and groups every thread's stack, so a
  deadlock is easy to spot.

No command-line tool can drive the debugger. This extension lets Claude do it.

## Getting started

1. Install the extension and restart Visual Studio with a solution open.
2. In the **Output** window, select **Claude MCP** under *Show output from*. It shows the command
   to run, including your machine's token:

   ```
   claude mcp add --transport http visual-studio http://127.0.0.1:5230/mcp --header "Authorization: Bearer <your-token>"
   ```

   Add `--scope user` to make the server available from any directory.
3. Check that the server is connected with `claude mcp list`, then ask Claude to build your solution.

## Tools

| Area | Tools |
|---|---|
| Build | `build`, `build_status`, `cancel_build`, `get_errors`, `set_configuration` |
| Debugger | start, stop, pause, continue, step, run to line, set next statement, attach, detach |
| Breakpoints | conditional, hit count, tracepoints, temporary, dependent |
| Inspection | `evaluate`, `get_locals`, `get_stack`, `get_exception`, `break_on_exception` |
| Threads | `list_threads`, `switch_thread`, `get_all_stacks` (grouped like *Parallel Stacks*) |
| Tests | list, run, debug and cancel tests through Test Explorer (MSTest, xUnit, NUnit…) |
| Code | `find_symbol`, `go_to_definition`, `find_references`, `rename_symbol`, `apply_code_fix` |
| IDE | output panes, editor selection, unsaved documents, modal dialogs, named commands, UI Automation of any tool window |

On multi-targeted projects, `set_configuration` can build a single target framework. Several
Visual Studio instances can run at once: Claude lists them and asks which one to use if the target
is ambiguous.

## Security

The server listens on `127.0.0.1` only and requires a per-machine token. It checks the `Origin` and
`Host` headers to block DNS rebinding.

Building and debugging run code from the open repository with your privileges. Do not use the
extension on code you do not trust.

## Documentation

The [full documentation](https://github.com/R3C-0N/Visual-Studio-Claude-MCP#readme)
([français](https://github.com/R3C-0N/Visual-Studio-Claude-MCP/blob/master/README.fr.md)) covers every tool, a stdio relay that keeps Claude Code connected while Visual Studio is closed,
known limitations and troubleshooting. Report issues on
[GitHub](https://github.com/R3C-0N/Visual-Studio-Claude-MCP/issues).

Licensed under [Apache 2.0 with Commons Clause](https://github.com/R3C-0N/Visual-Studio-Claude-MCP/blob/master/LICENSE):
free to use, modify and redistribute, including at work. Selling it is not allowed.
