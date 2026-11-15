# Source functionality and consolidation inventory

## Implementation checklist

- [x] Initial functionality and ownership inventory.
- [x] Shared permission vocabulary independent of provider options.
- [x] Managed agent session factory injected into workspace chats.
- [x] Git repository contract and injected factory for panels and previews.
- [x] Application-owned Smart host and workspace-owned embedding subscriptions.
- [x] CLI transcript reader factory registered in the composition root.
- [x] Instance-owned settings, database, shortcut caching and syntax selection with explicit UI/chat preference dependencies.
- [ ] Complete application/workspace service composition and remove remaining service-location calls.
- [ ] Consolidate host mode, approval and session orchestration workflows.
- [x] Consolidate Smart provider and helper workflows behind testable interfaces.
- [ ] Consolidate workspace indexing, memory and history ownership and persistence access.
- [ ] Refactor command discovery into focused detectors and a common execution workflow.
- [ ] Consolidate MCP configuration, approval and host integration workflows.
- [ ] Review editor, terminal, file explorer and Markdown responsibilities and shared flows.
- [ ] Complete project boundary redesign and architecture dependency checks.
- [ ] Add resource lifetime and failure/cancellation integration coverage.
- [x] Resolve the search retrieval evaluation failure without weakening thresholds.
- [ ] Complete full automated validation and manual Windows feature checks.

This inventory maps the current implementation to its owning source. It is a starting point for the architecture redesign; an entry identifies ownership, not a claim that all flows have already been unified.

| Functionality | Main implementation | Tests and integration |
| --- | --- | --- |
| Chat, sessions, streaming, approvals, modes, models, attachments, plans, handoff and recovery | `Codale.App/ViewModels/ChatViewModel*`, `Codale.Core/Agents`, `Codale.Agents/Claude`, `Codale.Agents/Codex` | `Codale.Agents.Tests`, `Codale.Core.Tests`; CLI protocols and WinUI |
| Account profiles, endpoint configuration, credentials and provider access | `Codale.Agents/CodaleAccountCli.cs`, `Codale.App/Services/CliEndpointSettings.cs`, `SecretProtector.cs`, `Codale.Agents/Providers` | Agent tests; CLI accounts and OpenAI/Anthropic-compatible services |
| CLI detection, installation/version checks, update commands and terminal launch/resume | `Codale.Agents/CliLocator.cs`, `CliVersionCheck.cs`, `Codex/CodexUpdater.cs`, `History/CliResume.cs`, `Codale.App/ViewModels/CliUpdateViewModel.cs` | Agent tests; package managers and installed CLIs |
| Smart routing, model roles, generation, decisions, completion, embeddings, summaries and activity | `Codale.Smart`, `Codale.App/Services/Smart*`, `FeatureService.cs` | `Codale.Smart.Tests`, `Codale.Smart.Smoke`, agent tests; local runtime and remote providers |
| Workspace index, search, discovery, context and knowledge | `Codale.Search`, `Codale.App/ViewModels/WorkspaceFileIndex.cs`, `SearchViewModel.cs`, `Services/FeatureService.cs` | `Codale.Search.Tests`; workspace files and Smart |
| Workspace memory, chat history, configuration, encrypted environment and database | `Codale.Workspace`, `Codale.Db`, `Codale.Agents/History` | `Codale.Workspace.Tests`, `Codale.Db.Tests`, agent tests; CLI transcript files and local database |
| Editor, syntax, completion, diff and previews including Markdown | `Codale.Editor`, `Codale.Core/Syntax`, `Codale.App/Controls/CodeEditor*`, `Codale.App/Views/EditorTab*`, `MarkdownView.cs`, `DiffTab*` | `Codale.Editor.Tests`, core tests; WinUI and grammar catalog |
| Terminal, shell input, tabs, transcript and attention | `Codale.Terminal`, `Codale.App/Views/Terminal*`, `CliTerminalTab*`, `Codale.Core/Tasks/TerminalTranscript.cs` | `Codale.Terminal.Tests`; Windows ConPTY |
| Git status, staging, commits, history, graph and diffs | `Codale.Git`, `Codale.App/ViewModels/GitViewModel.cs`, `DiffViewModel.cs`, `DiffViewerViewModel.cs` | `Codale.Git.Tests`; Git CLI |
| File explorer, project picker, workspace windows and folders | `Codale.App/ViewModels/FileTreeViewModel.cs`, `WorkspaceViewModel.cs`, `Codale.App/Views/WorkspacePage*`, `ProjectPickerPage*`, `Services/WorkspaceFolders.cs` | workspace and search tests; WinUI and filesystem |
| Project command discovery, saved commands, tasks and process execution | `Codale.Commands`, `Codale.Core/Tasks`, `Codale.Mcp.Tasks`, `Codale.App/Views/WorkspacePage.TerminalDock.cs` | `Codale.Commands.Tests`, `Codale.Mcp.Tests`; project configuration and shell |
| Browser, computer, phone and task MCP tools | `Codale.Mcp.Browser`, `Codale.Mcp.Computer`, `Codale.Mcp.Phone`, `Codale.Mcp.Tasks`, `Codale.Mcp.Common`, `Codale.App/Services/McpServerSettings.cs` | `Codale.Mcp.Tests`; Playwright, Windows desktop, adb, separate MCP processes |
| Application settings, activation, appearance, shortcuts and native integration | `Codale.Settings`, `Codale.App/App.xaml.cs`, `Program.cs`, `Services/ApplicationServices.cs`, `KeyboardShortcuts.cs`, `Controls/SettingsPanel*`, `Codale.ShellExt` | `Codale.Settings.Tests`; WinUI, Windows application lifecycle and shell extension |

## Consolidation findings

- The shared `IAgentSession` and normalized events provide a provider boundary. `AgentSessionFactory` now centralizes managed chat session construction and is injected into workspace chats; `ChatViewModel` still coordinates many unrelated workflows, and the history reader factory has a separate provider switch.
- Permission modes are now normalized by `Codale.Core/Agents/AgentPermissionMode.cs`; provider protocol translation still belongs to each adapter. Host-mode and approval flows require further review before consolidation.
- The application now owns `SmartExperienceHost`, while each feature service owns and disposes its Smart embedder. Views and inline completion receive the host explicitly. Settings cache/watcher state and application database pin/timer state live in instance services; their application compatibility facades have been removed. Standalone workspace defaults still expose shared access for other process hosts.
- Workspace persistence, search indexes and CLI-owned histories are separate stores with different lifetimes and cross-process constraints. Any common interface must preserve those distinctions rather than assume a single database owner.
- Git view models now receive `IGitRepository` instances through a shared factory, including nested repositories and diff previews. Command discovery is a large static catalog, and MCP server configuration and host-specific transport remain distributed; these are candidates for focused adapters.
- Four MCP executables are separate deployment and process boundaries; consolidating their common contracts does not require merging their hosts.
- `ApplicationServices` now composes the agent and Git factories, Smart host and workspace view models. Extend this composition root with explicit lifetime and disposal tests before replacing the remaining static access.

The baseline retrieval evaluation failure was fixed by fitting excerpts to the remaining byte budget at line boundaries and only treating included ranges as covered. The original evaluation threshold and context byte budget are unchanged; all 333 search tests pass.

## Library boundary changes

Provider profiles, Smart API/CLI requests, role routing, decisions and status tracking now live in `Codale.Agents/Providers`. The application and tests reference the same compiled implementation; provider source is no longer linked into the test project. Architecture tests prohibit library references to the presentation project and regression to linked provider source.

Smart now references Core for process-job ownership rather than compiling a second copy of that implementation. Workspace memory, configuration, history and secrets are resolved through a workspace data context; the knowledge UI uses that context.

## Workflow extraction progress

Command formats and ecosystems now have focused detector classes under `Codale.Commands/Detectors`; `CommandParsing` owns shared tolerant parsing and quoting. `ProjectCommandService` owns detector ordering and saved-command access. The workspace and task MCP host use the same service interface. Existing command behavior and injected-detector ordering remain covered by the command tests.

`AgentModePolicy` in Core now owns host-mode recognition, CLI translation and provider-report compatibility. The chat view model delegates those decisions to it. Tests cover approval-preserving Ask/Automatic translation and Full access translation. Approval presentation and session orchestration remain separate open work.

### Application composition follow-up (2026-11-15)

Model-catalogue discovery now receives `IAgentSessionFactory` explicitly from its settings callers. Both catalogue probes and managed conversations therefore use the composed factory without resolving application services inside the chat view model. An architecture test checks every view-model source file for `App.Current` access. Presentation resource lookup through WinUI's `Application.Current.Resources` remains outside that service-composition rule.

The remaining `App.Current.Services` lookups have now been removed. The application passes its composition root to the main window, which initializes the XAML-created settings panel before loading it and creates workspaces through that root. Workspaces supply the shared Smart host to editor and terminal constructors and expose their composed CLI history factory to the terminal session panel. Inline completion is an instance owned by its terminal view and receives the same host. Workspace status subscriptions use that workspace's host for both subscription and unsubscription. An architecture check covers application source files to prevent direct `App.Current.Services` lookup from returning.

The application/workspace composition checklist item remains open for static settings/database ownership and application/window lifetime responsibilities. Window UI access through `App.Current.MainWindow` also remains; this is separate from application service lookup. Resource lifetime and failure/cancellation integration coverage is still outstanding.

### Database ownership and shutdown follow-up (2026-11-15)

`WorkspaceDatabasePool` now owns database pins and idle timers as instance state. `WorkspaceDataFactory` owns a disposable pool and supplies it to memory, history, configuration and secret stores. Disposing the factory releases its pins and rejects new operations; in-flight operation leases can finish independently. Idle callbacks check pin identity so an old callback cannot release a replacement pin. The static `WorkspaceDb` facade remains for standalone stores and existing callers; those callers have not all migrated to composed ownership.

`ApplicationServices` now owns asynchronous shutdown of Smart and its workspace data factory, followed by settings watcher/debounce cleanup and release of the application database idle pin and timer. The window intercepts close even without an open workspace, waits for the current workspace and retiring workspaces from project switches, and then awaits application shutdown before the final close. Redirected activation is detached when the window closes. Smart disposal no longer runs from a separate async `Closed` handler.

Settings still expose a static API and cache, and `AppDb` still exposes static access. Their resources now have explicit shutdown, but replacing those APIs with injected stores remains open. Manual close/cancel, launcher-only close, project-switch shutdown and external settings-edit checks remain to be performed on Windows. This follow-up adds automated coverage for factory disposal, data persistence across owners, rejection after disposal and active-lease completion during disposal.

Validation for this follow-up: all 378 agent tests pass, including the architecture checks. The x64 Debug Windows application builds successfully with zero warnings and zero errors, including the native shell extension. Manual Windows feature checks and the complete solution test suite have not been run for this follow-up.

Database/shutdown follow-up validation: all 8 workspace tests and all 378 agent tests pass; the x64 Debug Windows application builds with zero warnings and errors. Full solution validation remains open.

### Service composition guard (2026-11-15)

`ApplicationDoesNotUseStaticWorkspaceDatabaseFallbacks` fails if any application source uses `WorkspaceDb.`, `CommandCatalog.` or a direct `new HistoryStore/MemoryStore/ConfigStore/EnvStore(` constructor, so workspace data stays on the composed context. The remaining static fallbacks (`WorkspaceDb.Shared`, `CommandCatalog`) are kept for MCP process hosts and compatibility callers, as described above. Window access is now explicit. `MainWindow` implements `IWindowOwner` and passes itself through navigation (`WorkspaceNavigation`, `ProjectPickerNavigation`); the workspace page keeps it as `_window`, and child views (`ChatTab`, `EditorTab`, `TerminalTab`, `AgentSessionPanel`) and `SettingsPanel` receive an `Owner`/owner value before use. `App` no longer exposes `MainWindow`, `MainWindowHandle` or `OpenProjectInPlace`; the architecture test rejects their return. The checklist item stays unchecked because the settings and database compatibility facades and the static `WorkspaceDb.Shared` fallback remain for process hosts, as noted above.

Validation: the architecture tests pass; all 380 agent tests and 8 settings tests pass; the x64 Debug Windows application builds with zero warnings and errors.

### Approval routing extraction (2026-11-15)

The order of the first approval checks now lives in `Codale.Core/Agents/ApprovalRouting.cs`. It answers the attached-file read, Codale's own unasked tools, and the accept-edits file edits, and sends governed modes to `ApprovalPolicy` (`ApprovalStep.ConsultPolicy`). The chat view model performs the answer and keeps the policy evaluation, shell assessment, planning inspection and reader prompts. Two branches of the previous cascade were unreachable because `ApprovalPolicy.Governs` already covers Ask, Plan, Manual, Default and Automatic (the Ask-mode web lookup and the read/edit branches for those modes); they are not carried over. `ApprovalRoutingTests` (26 cases) pin the order for each mode.

### Mode restore extraction (2026-11-15)

`TemporaryModeRestore<TSession>` in `Codale.Core/Agents` owns the pending restore for temporary turns: the session it applies to, the mode to hand back, and the Ask-from-Automatic flag. `Take` returns the restore only for the session the turn started on and drops a stale one. `ChatViewModel` and `ChatViewModel.Turns.cs` perform the CLI switches and UI updates through it; the previous three fields are gone. `TemporaryModeRestoreTests` (6 cases) cover it.

### Plan effort extraction (2026-11-15)

`PlanEffortOverride` in `Codale.Core/Agents` owns the planning effort: the starting effort recorded on the first plan, Lite promotion to Flash's effort, the effort the tier label shows, and the restore. Planning twice keeps the first start. `ChatViewModel` applies the effort to the session and performs the live switch or restart. `PlanEffortOverrideTests` (6 cases) cover it.

Still in the view model: the live effort and restart calls in `EnterPlanModeAsync` and `RestorePlanEffort`, and the session orchestration in `ChatViewModel`. Validation: Core tests and agent tests pass; the x64 Debug Windows application builds with zero warnings and errors.

### Turn resolution extraction (2026-11-15)

`TurnResolution.Resolve` in `Codale.Core/Agents/TurnResolution.cs` decides how a finished turn ended (completed, stopped or failed), which notice it leaves (interrupted, auto mode paused, or error), and the notice text. `ChatViewModel` maps the result to its `TurnOutcome` and performs the transcript, composer and attention side effects. The paused and failed attention requests now read the same resolved notice. `TurnResolutionTests` (7 cases) cover the table.

This is one slice of session orchestration. Still in `ChatViewModel`: connection and spawn (`ConnectCoreAsync`), event dispatch (`Handle`), send and steer flows (`SendTurnAsync`, `SteerTurnAsync`), and restart. Item 2 stays unchecked.

### Connection preflight extraction (2026-11-15)

`ConnectPreflight` in `Codale.Core/Agents/ConnectPreflight.cs` decides whether the signed-in account must be checked before a CLI starts (`NeedsAccountCheck`), and whether a failed start means a resumed conversation was lost (`IsResumeLost`). `ChatViewModel.ConnectCoreAsync` uses both; the account lookup stays lazy, as before. `ConnectPreflightTests` (9 cases) cover them.

Still in `ConnectCoreAsync`: endpoint model and effort resolution, MCP and task-server setup, and the spawn request. The request's system prompt and tool lists depend on App-side MCP directive code, so moving it into Core needs that code moved first. Item 2 stays unchecked.

### Smart request workflows (2026-11-15)

Two request shapes that the Smart helpers repeated now live in `Codale.Smart/SmartRequestWorkflow.cs`: `RunTrackedAsync` reports a request on the status (completed, cancelled, or error with its message) through `ISmartRequestStatus`, and `RunWithDeadlineAsync` gives null when a helper's deadline passes without a caller cancel. `SmartDecider` and `SmartHelperModel` use them; the app implements the status interface in `SmartStatusSink`. The pure completion text rules (`WithoutRepeat`, `Clean`) moved from `SmartInlineCompletion` to `Codale.Smart/SmartCompletionText.cs`. Tests: `SmartRequestWorkflowTests` (6) and `SmartCompletionTextTests` (9).

Still open for item 3: `SmartEmbedder` keeps its own tracked request because it is synchronous and rethrows, and the app helpers still depend on the concrete `SmartExperienceHost` rather than an interface, so they cannot be exercised without the host. The provider routing (`SmartFeatureRouter`) and helper workflows in `Codale.Agents` are not yet consolidated. Item 3 stays unchecked.

### Smart task host interface (2026-11-15)

`ISmartTaskHost` in `Codale.Smart/SmartTaskHost.cs` is the surface the task helpers need: feature enablement, readiness, and one generation. `SmartExperienceHost` implements it explicitly. `SmartHelperRunner` (availability and deadline-bounded answers) and `SmartInlineSuggester` (typing-pause continuation) in `Codale.Smart/SmartTaskRunners.cs` now hold the logic that `SmartHelperModel` and `SmartInlineCompletion` used to carry; those app classes are thin adapters. `SmartTaskRunnersTests` (12 cases) drive both runners through a fake host.

Still open for item 3: `SmartDecider` and `SmartEmbedder` depend on `SmartProviderClient` in `Codale.Agents`, so they need a provider-client interface in the same way; and provider routing (`SmartFeatureRouter`) is not yet consolidated. Item 3 stays unchecked.

### Smart provider interface and layering (2026-11-15)

Layering change: `ISmartRequestStatus` and `SmartRequestWorkflow` moved from `Codale.Smart` to `Codale.Core/Agents`, because both reference no Smart types and Smart, Agents and the app all reference Core. Their tests moved to `Codale.Core.Tests`.

`ISmartProviderClient` in `Codale.Agents/Providers` names the members the host uses from a Smart role (`Role`, `IsEnabled`, `ConfigurationError`, `EmbeddingIdentity`, `EmbedAsync`, `DecideAsync`); `SmartProviderClient` implements it. The decision and embedding workflows now live in `SmartDecisionService` and `SmartEmbeddingService` (`Codale.Agents/Providers/SmartServices.cs`); `SmartDecider` and `SmartEmbedder` in the app are thin forwarders. `SmartServicesTests` (7 cases) cover the no-decider path, completion, failure, the hashed fallback when the model is off, timeout (with a short injected timeout), and error propagation.

Provider routing: `SmartFeatureRouter` now depends on `ISmartProviderClient` (with `ConfigurationKey`, `FeatureIdentity` and `CompleteAsync` added to the interface) and `SmartFeatureRouterTests` (14 cases) cover the role mapping, fallbacks, the Decision role, `HasGeneration` and routing of completions. Item 3 is complete.

### Workspace index ownership (2026-11-15)

`ISourceIndexes` in `Codale.Search` supplies the source index for a project root. `ProjectKnowledgeIndex` takes it as an optional constructor argument and defaults to `SharedSourceIndexes`, the existing per-root cache, so behaviour is unchanged. `KnowledgeSourceSeamTests` (2 cases) prove that refresh reads through the injected provider and that the default is the shared cache. The Search suite (335) and the app build pass.

Still open for item 4, and needs decisions:
- The source index is a process-wide static cache (`SourceIndex.For`). The editor, search, composer prediction and health views still call it directly.
- The index model is persisted as `workspace-model.json` in the project's knowledge folder, outside the workspace database. Deciding whether it moves into the database is a design choice.
- `FeatureLog` still has a path-based constructor that opens its own `HistoryStore`. It is used by tests only; removing it means updating about twenty test call sites.

### Command run rules (2026-11-15)

`ProjectCommandRun` in `Codale.Commands` holds the rules that both ways of running a saved command share: name resolution (exact name, then a unique substring, with the same error messages the agent always got), the absolute working directory (falling back to the root for an invalid path), and whether a live run already covers the command (same name or same command line). `TaskTools` (agent tools) and `WorkspaceViewModel.RunProjectCommand` (commands menu) both use it. `ProjectCommandRunTests` adds 12 cases.

Behaviour change: an invalid working directory in an agent-run command now runs in the run directory instead of failing. Still open for item 5: the two paths still open their own terminals (`TaskTools` through the task service, the app through `TerminalViewModel`); a single launch workflow would need that unified too. Item 5 stays unchecked.

### MCP tool policy (2026-11-15)

`AgentToolMode` moved to `Codale.Core/Agents`, and `McpToolPolicy` there holds the decisions that follow from the browser, desktop and phone modes: parsing and formatting the stored modes, the CLI's own tool setting (`Native`, `ClaudeChrome`), Codex feature flags, Claude's disallowed tools, and the modes key a session uses to notice a change. `McpServerSettings` delegates to it. `McpToolPolicyTests` (13 cases) pin each decision, including the case where Codale's server is missing. Core (960), MCP (52) and agent (400) suites pass; the app builds clean.

Still open for item 6: the directive text and per-CLI configuration writers (`Directive`, `MergeServers`, `CodexOverrides`, `StageSettings`) and the host executable lookup (`RunnablePath`, `StageRunnable`) still live in `McpServerSettings`. Those need the same treatment: pure configuration builders in Core and an injectable host locator. Item 6 stays unchecked.

### Store caller migration (2026-11-15)

`FeatureService` now receives the workspace data context and creates `FeatureLog` instances from its history store. `ProjectCommandService` accepts a configuration-store resolver; the application supplies one backed by its workspace data factory. Feature log and saved-command operations therefore use the composed pool instead of creating stores through the static workspace fallback. Tests verify that both workflows persist normally and reject access after their owner is disposed. Standalone and MCP callers retain their existing defaults and process boundaries.

Application database pin/timer state now lives in disposable `ApplicationDatabase`, with `AppDb` serving as a compatibility facade over the shared instance. `ApplicationServices` owns disposal of that instance and passes it through workspaces to MCP preferences, workspace folder history and todo persistence. View-model architecture checks prohibit `AppDb.Open` calls. The instance retains the original idle-close and lock-wait behavior.

Remaining migration targets include static settings/cache access, recent projects, command history and application data-path consumers. The compatibility facade still supplies the shared application database instance; composition does not yet create an isolated settings/database environment. Manual Windows lifetime checks and full solution validation remain open.

Validation: all 43 command tests and all 379 agent tests pass; the x64 Debug Windows application builds with zero warnings and errors.

### Settings, recent projects and command history (2026-11-15)

`Codale.Settings` now contains the production `AppSettingsStore`, `ApplicationDatabase`, `RecentProjects` and `CommandHistory` implementations, database node extensions and secret protection. It has no presentation or WinUI dependency. `AppSettingsStore` owns its parsed values, provider cache, synchronization gate, file watcher, debounce timer and notification dispatch. Its folder is supplied at construction. UI dispatch is supplied by the application as delegates; disposal clears cached values, stops reload resources, rejects subsequent stored-value access and suppresses queued change notifications.

The application composition root owns the default settings store and creates recent-project and command-history services against its database. The project picker receives its recent-project service through navigation. Terminal view models receive settings and command history explicitly; terminal views use that view model's history. Settings, main-window preferences, Smart hosting/helpers/embeddings, feature retrieval and CLI endpoint settings now read their composed settings store. The settings and database compatibility facades still refer to the process defaults; remaining UI/chat callers, shortcut-map caching and syntax selection have not all migrated away from those facades. The complete composition checklist item remains open.

`Codale.Settings.Tests` references the compiled production library, without linked source or Windows App SDK startup. Five tests cover cache isolation and persistence across owners, external edits invalidating only the affected cache, queued notification suppression after disposal, instance-owned settings state, and injected recent-project/command-history storage. Recent-project deduplication/clear and command-history recall/suggestions remain covered by the storage test. The five settings tests and all 379 agent tests pass; the x64 Debug application builds with zero warnings and errors. Manual Windows interaction checks and full solution validation remain open.

### UI/chat settings, shortcut caching and syntax selection (2026-11-15)

The application composition root now constructs its settings store and application database directly. All remaining presentation and chat settings callers receive their store through their owning workspace, chat, terminal or control. The unused `AppSettings` and `AppDb` facades have been removed. The application-source architecture check rejects direct `AppSettings` access as well as application service lookup.

Chat defaults, routing, approvals, account/profile choices, Git and search preferences use the composed store. XAML-created editor and terminal controls receive preferences from their owner before use instead of reading process settings in their constructors. Terminal appearance helpers take settings explicitly. Chat typography is a pure scale calculation; each transcript row receives its chat's settings, propagated into turn steps, explore groups and approval questions/options, including later additions.

`ShortcutBindings` in `Codale.Settings` owns the normalized chord cache and a removable settings subscription. The application-owned `KeyboardShortcuts` adapter supplies Windows key parsing and owns recording state; settings, window and chat callers use that instance. Disposal clears the cache and detaches notifications. Immutable action identifiers and metadata remain shared.

`SyntaxSelection` in `Codale.Settings` owns override decisions against its settings and an injected catalog/store. The application supplies the shared Core syntax catalog initialized in the composition root; standalone selection instances can own independent catalogs without replacing Core's global services. Editor and search snippets receive that selection explicitly. Settings uses its store/catalog and removes its named grammar-change subscription when unloaded. Markdown uses the Core grammar services already initialized by the host. Core's process-wide tokenizer/rendering boundary remains a separate architecture review item.

Validation: all 8 settings tests and all 379 agent tests pass; the x64 Debug Windows app builds with zero warnings and errors. New tests cover shortcut cache invalidation/isolation, duplicate precedence, disabled and invalid bindings, disposal, per-owner syntax overrides and installed-grammar catalog isolation. Manual checks for live chat scaling, shortcut recording, grammar changes and close/cancel remain open, along with full solution validation and broader window/lifetime integration coverage.
