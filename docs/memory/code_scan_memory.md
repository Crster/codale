# Codebase Scan & Refactoring Memory Ledger

This file tracks file-by-file inspection findings, architectural observations, optimization opportunities, risky/unsafe patterns, obsolete compatibility code, and formatting issues.

## Scan Index & Status

| Project | Clean Source Files | Scan Status | Remediation Status | Notes |
| :--- | :--- | :--- | :--- | :--- |
| **Codale.Mcp.Common** | 4 | **Completed** | **Clean** | Protocol & tools foundation |
| **Codale.Mcp.Tasks** | 4 | **Completed** | **Refactored** | Removed POSIX shell fallback |
| **Codale.Mcp.Computer** | 4 | **Completed** | **Optimized** | Process name caching in Desktop.Windows |
| **Codale.Mcp.Browser** | 3 | **Completed** | **Hardened** | Added 24-hr download evictions on session disposal |
| **Codale.Commands** | 2 | **Completed** | **Refactored** | Removed POSIX quote escaping |
| **Codale.Core** | 49 | **Completed** | **Clean** | Domain, processes & syntax |
| **Codale.Storage** | 2 | **Completed** | **Clean** | SQLite persistence & FTS5 search |
| **Codale.Git** | 9 | **Completed** | **Clean** | Git CLI wrappers, diff & status |
| **Codale.Terminal** | 5 | **Completed** | **Refactored** | Removed unused ct in WriteAsync |
| **Codale.Search** | 12 | **Completed** | **Refactored** | Discarded unused ct in ConnectModelAsync |
| **Codale.Agents** | 23 | **Completed** | **Clean** | LLM sessions (Claude/Codex/OpenAI BYOK) |
| **Codale.App** | 108 | **Completed** | **Decomposed & Refactored** | Stripped LegacyAgentKey, sorted imports, resolved IDE0060/IDE0062, extracted ChatViewModel.Turns.cs |
| **Tests & Harnesses** | 68 | **Completed** | **Clean** | All 676 unit & integration tests passing |

---

## Detailed Findings & Remediation Log

### 1. Codale.Mcp (Common, Tasks, Computer, Browser)
- **`src/Codale.Mcp.Tasks/TaskTools.cs`**:
  - *Obsolete Compatibility*: `InDirectory` method contained `OperatingSystem.IsWindows() ? ... : ...` branch with fallback `cd '{dir}' && {command}`.
  - *Remediation*: **Removed**. Standardized on Windows PowerShell `Set-Location` syntax.
- **`src/Codale.Mcp.Computer/Desktop.cs`**:
  - *Performance Optimization*: Window enumeration queried `Process.GetProcessById((int)pid)` for every visible window.
  - *Remediation*: **Cached**. Added transient `Dictionary<uint, string>` cache to reuse process name lookups per enumeration run, minimizing OS handle overhead.
- **`src/Codale.Mcp.Browser/BrowserSession.cs`**:
  - *Resource Management*: Stale temporary files accumulated in `codale-downloads`.
  - *Remediation*: **Added `CleanupDownloads()`** invoked during `DisposeAsync()` to evict temporary downloads older than 24 hours.

### 2. Codale.Commands
- **`src/Codale.Commands/CommandCatalog.cs`**:
  - *Obsolete Compatibility*: `Quote` method contained non-Windows POSIX `/bin/sh` single-quote escaping branch (`trimmed.Replace("'", @"'\''")`).
  - *Remediation*: **Removed**. Standardized on Windows single-quote escape (`''`).

### 3. Codale.Terminal
- **`src/Codale.Terminal/TerminalSession.cs`**:
  - *Analyzer / Redundancy (`IDE0060`)*: `WriteAsync(string text, CancellationToken ct = default)` had an unused parameter `ct`.
  - *Remediation*: **Removed** `ct` parameter and streamlined call sites.

### 4. Codale.Search
- **`src/Codale.App/ViewModels/SearchViewModel.cs`**:
  - *Analyzer (`IDE0060`)*: `ConnectModelAsync(CancellationToken ct)` had unused `ct`.
  - *Remediation*: **Replaced** with discard `_` to fulfill the `Func<CancellationToken, Task<ISearchModel?>>` delegate contract.

### 5. Codale.App
- **`src/Codale.App/Services/AppSettings.cs`**:
  - *Obsolete Compatibility*: `LegacyAgentKey = "chat.defaultAgent"` and `_droppedLegacyKey` tracking on load and save.
  - *Remediation*: **Removed** `LegacyAgentKey` constant, `_droppedLegacyKey` flag, and legacy key-filtering branch from `LoadFile()` and `OnFileChanged()`.
- **`src/Codale.App/ViewModels/ChatViewModel.cs` & `ChatViewModel.Turns.cs`**:
  - *Architectural Decomposition*: `ChatViewModel.cs` was massive (`>4,300 lines`). Extracted turn lifecycle (`EndTurn`, `LiftOutDeliverables`, `ToggleTurn`) and plan/ask mode transitions (`EnterPlanModeAsync`, `EnterAskTurnAsync`, `RestorePlanEffort`, `RestoreAfterPlanTurn`) into a dedicated partial class [`ChatViewModel.Turns.cs`](file:///X:/CrsterSite/codale/src/Codale.App/ViewModels/ChatViewModel.Turns.cs).
  - *Redundancy / Analyzer (`IDE0060`)*: Removed unused parameter `end` from `EndTurn`.
- **`src/Codale.App/Controls/HexViewer.xaml.cs`**:
  - *Analyzer (`IDE0060`)*: Removed unused parameter `row` from `ByteInRow(double x, bool ascii)`.
- **`src/Codale.App/Views/MarkdownView.cs`**:
  - *Analyzer (`IDE0060`)*: Removed unused parameter `size` from `AddWithImages`.
- **`src/Codale.App/Views/ChatTab.xaml.cs`**:
  - *Analyzer (`IDE0062`)*: Converted local function `Focus` inside `FocusDraft()` to `static void Focus(TextBox box)` to avoid closure allocations.
- **`src/Codale.App/Views/EditorTab.xaml.cs`**:
  - *Analyzer (`IDE0060`)*: Refactored `OnOpenExternallyClick` to zero-argument `OpenExternally()` and discarded sender parameter in `OnEditorTextChanged`.
- **`src/Codale.App/MainWindow.xaml.cs`**:
  - *Analyzer (`IDE0060`)*: Discarded unused sender parameters in key handlers.
- **Imports Sorting & Whitespace**:
  - Formatted all `using` directives alphabetically.
  - Normalised whitespace and final newlines across all files.

---

## Final Verification Summary
- **Formatting**: `dotnet format whitespace --verify-no-changes` -> **Passed** (Exit Code 0).
- **Unit & Integration Tests**: `dotnet test` -> **All 676 tests passed** (0 failed, 0 skipped across all test suites).
