# Source functionality and ownership inventory

Reviewed against merged `main` at `fa9981f` on November 16, 2026. This document describes current ownership and known limits. Earlier refactor progress and intermediate test counts are available in Git history.

## Current capabilities and owners

Paths below are relative to `src/` unless stated otherwise.

| Functionality | Owning implementation | Verification |
| --- | --- | --- |
| Claude/Codex sessions, normalized events, models, approvals and recovery | `Codale.Agents/Claude`, `Codale.Agents/Codex`, `Codale.Core/Agents`, `Codale.App/ViewModels/ChatViewModel*` | Agents and Core tests; live CLI behavior is opt-in |
| Account profiles, endpoint configuration and credentials | `Codale.Agents/CodaleAccountCli.cs`, `Codale.Agents/Providers`, `Codale.App/Services/CliEndpointSettings.cs`, `Codale.Settings/SecretProtector.cs` | Agents and Settings tests |
| CLI detection, version checks, installation/update commands and terminal resume | `Codale.Agents/CliLocator.cs`, `CliVersionCheck.cs`, `Codex/CodexUpdater.cs`, `History/CliResume.cs`, `Codale.App/ViewModels/CliUpdateViewModel.cs` | Agents tests; external installers and authenticated CLIs need live checks |
| Smart provider clients, roles, decisions and status | `Codale.Agents/Providers`, `Codale.App/Services/SmartExperienceHost.cs`, `SmartDecider.cs`, `Codale.Smart` | Agents and Smart tests; app uses provider-backed generation |
| Source index, project search and knowledge retrieval | `Codale.Search`, `Codale.App/ViewModels/WorkspaceFileIndex.cs`, `SearchViewModel.cs`, `Codale.App/Services/FeatureService.cs` | Search tests, including retrieval evaluation and index lifetime |
| Workspace data, memory, session records, configuration and secrets | `Codale.Workspace`, `Codale.Db`, `Codale.Agents/History` | Workspace, Db and Agents tests; CLI transcripts remain separate |
| Editor model, syntax selection, inline completion, diffs and Markdown | `Codale.Editor`, `Codale.Core/Syntax`, `Codale.Settings/SyntaxSelection.cs`, `Codale.App/Controls/CodeEditor*`, `Controls/Editing`, `Views/EditorTab*`, `MarkdownView.cs`, `DiffTab*` | Editor, Settings and Core tests; rendering requires Windows UI checks |
| Terminal emulation, ConPTY, terminal output and command execution | `Codale.Terminal`, `Codale.Core/Tasks/TaskService.cs`, `TerminalTranscript.cs`, `Codale.App/Views/Terminal*`, `CliTerminalTab*` | Terminal and MCP tests |
| Project command discovery, persistence and run resolution | `Codale.Commands/Detectors`, `CommandParsing.cs`, `ProjectCommandService.cs`, `ProjectCommandRun.cs` | Commands tests; both app and MCP use the service |
| Git operations, status, staging, graph, commits and diffs | `Codale.Git`, `Codale.App/ViewModels/GitViewModel.cs`, `DiffViewModel.cs`, `DiffViewerViewModel.cs` | Git tests using the Git CLI |
| Workspace windows, folders, file explorer and project picker | `Codale.App/ViewModels/WorkspaceViewModel.cs`, `FileTreeViewModel.cs`, `Views/WorkspacePage*`, `ProjectPickerPage*`, `Services/WorkspaceFolders.cs` | Workspace/Search tests and manual WinUI checks |
| Workspace MCP servers | `Codale.Mcp.Common/CodaleServers.cs`, `Codale.Mcp.Tasks/TaskTools.cs`, `Program.cs` | MCP tests; named-pipe transport to app terminals |
| Browser, desktop and Android automation | `Codale.Mcp.Browser`, `Codale.Mcp.Computer`, `Codale.Mcp.Phone`, `Codale.App/Services/McpServerSettings.cs` | MCP tests; real browser/desktop/device checks remain separate |
| Settings, persistence, shortcuts and recent projects | `Codale.Settings`, `Codale.App/Services/KeyboardShortcuts.cs`, `Controls/SettingsPanel*` | Settings tests and architecture guards |
| Composition, shutdown, activation and Explorer integration | `Codale.App/Services/ApplicationServices.cs`, `App.xaml.cs`, `MainWindow.xaml.cs`, `Program.cs`, `Codale.ShellExt` | Architecture/lifetime tests, app and native extension build; manual close/activation checks remain |

## Removed and replaced capabilities

- The hosted background-task manager was removed. `ITaskService` now operates on workspace terminal tabs. `run_command` and `run_in_terminal` start visible terminals; `read_terminal` reads output; `stop_terminal` interrupts only a terminal the agent opened. There are no exposed `start_task`, `read_task`, `list_tasks`, or `stop_task` tools.
- `Codale.Mcp.Tasks` is the retained executable/project name for five logical servers: `codale-panel`, `codale-command`, `codale-terminal`, `codale-memory`, and `codale-secret`. `codale-tasks` and the MCP `explore` tool are no longer exposed.
- The app no longer downloads or loads native model weights, builds the native inference shim, or packages inference DLLs. General, Auto Complete, Embedding and Decision roles use configured provider clients. `Codale.Smart` native runtime classes and `Codale.Smart.Smoke` remain development tools.
- The search panel uses `SourceExplorer` to produce local results and optional provider refinement. `SearchAgentLoop` remains library code, but the old autonomous Ask answer loop is not the search panel's entry point.
- The dead chat context-retrieval path was deleted. Topic changes are marked by `MarkNewTopic`; retained knowledge/summary APIs do not imply automatic context injection on every chat turn.
- The application `AppSettings` and `AppDb` compatibility facades were removed. Settings and application database services are composed instances. The unused SQLite dependency was removed; current storage is implemented by `Codale.Db`.

## Composition and persistence boundaries

`ApplicationServices` creates the settings store, application database, recent projects, command history, shortcut adapter, syntax selection, Smart host, agent session factory, Git repository factory, CLI history factory, decision service, workspace data factory, and project command service.

Workspaces receive those dependencies explicitly. `WorkspaceDataFactory` owns an instance `WorkspaceDatabasePool`; memory/history/configuration/secret stores are opened through workspace data contexts. Its disposal releases pins and timers, rejects new operations, and lets existing operation leases finish. Standalone stores and MCP processes retain static defaults where they require their own process boundary. Application source is guarded against calling those fallbacks.

Settings own their value/provider caches, watcher, debounce timer and UI notification dispatch. `ShortcutBindings` owns its normalized cache and settings subscription. `SyntaxSelection` owns per-settings override decisions; Core's syntax catalog/tokenizer services still have process-wide state.

The window close workflow waits for active and retiring workspaces and application shutdown. `ApplicationServices` disposes Smart, workspace data, shortcuts, settings and application database resources. Automated lifetime coverage does not replace interactive close/cancel checks.

## Shared workflows and contracts

- `AgentSessionFactory`, `IGitRepositoryFactory` and `ICliHistoryReaderFactory` centralize provider/repository construction.
- `AgentPermissionMode`, `AgentModePolicy`, `ApprovalRouting`, `TemporaryModeRestore`, `PlanEffortOverride` and `TurnResolution` hold shared policy and state rules. `ChatViewModel` still owns session connection, event handling, send/steer/restart and approval presentation.
- `SmartFeatureRouter`, provider clients and `SmartDecider` route generation/classification. Completion and embedding require explicit compatible models; local hashed embeddings remain available.
- `ProjectCommandService` composes focused detectors and a configuration resolver. `ProjectCommandRun` shares name resolution, working-directory fallback and duplicate-running-command checks. Terminal creation remains host-specific.
- `McpToolPolicy`, `McpConfigText`, `McpFileMerge` and `McpHostExecutables` hold policy, text, merge and host lookup rules. `McpServerSettings` performs app-side file staging and integration.
- `WorkspacePaths`, `PickerOwner` and `MarkdownLinks` share path, picker-owner and Markdown link/text rules. WinUI Markdown rendering remains in `MarkdownView`.
- `tests/Codale.Agents.Tests/ProjectLayeringTests.cs` pins allowed project references. Other architecture guards prohibit application service lookup, static workspace/settings access in presentation code, and linked provider implementations in tests.

The four MCP executable projects are deployment/process boundaries: one workspace host executable and three automation hosts. The workspace executable advertises five servers; executable count and server count differ.

## Verification and remaining work

The November 16, 2026 post-merge verification on `main` at `fa9981f` completed:

- `dotnet build Codale.slnx -c Debug`: zero warnings and zero errors, including the x64 app, MCP hosts and native Explorer extension.
- `dotnet test Codale.slnx -c Debug --no-build --no-restore`: 2,372 passed, zero failed, zero skipped.
- Suite counts: Core 1,004; Agents 406; Smart 163; Search 340; Terminal 78; Git 73; Db 53; Commands 55; MCP 52; Editor 32; Settings 8; Workspace 8.
- Clean working tree and `git diff --check` before documentation edits.

Remaining manual checks include live chat scaling, shortcut recording, grammar changes, close/cancel, launcher-only close, project-switch shutdown, external settings edits, live agent/provider requests, CLI updates/sign-in, and real browser/desktop/Android workflows. Native model inference requires the separate harness and model assets.

The refactor does not claim that all orchestration is unified: chat session/UI coordination, host-specific terminal launch, Core's global syntax services, and standalone process storage defaults remain explicit boundaries for future work. Analyzer suggestions reported by `dotnet format` are a separate concern from a warning-free build; old suggestion counts are not a current audit.
