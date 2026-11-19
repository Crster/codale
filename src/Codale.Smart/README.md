# Smart feature providers

The application uses provider-backed Smart features. It no longer downloads a local model, loads native inference weights, or builds/packages native inference DLLs. The retained [native runtime harness](native-runtime.md) is for development and evaluation.

## Configure model roles

In Settings, select a provider, account/endpoint profile and model for each role. All roles start **Off**. Roles can be enabled independently; there is no master provider selector.

| Role | Supported providers | Used for |
| --- | --- | --- |
| General | Claude CLI, Codex CLI, OpenAI-compatible API, Anthropic API | Commit messages, session names, shell translation, summaries, context exploration, search refinement, and classification fallback |
| Auto Complete | OpenAI-compatible API with an explicit completion model | Editor, composer and shell insertions |
| Embedding | OpenAI-compatible API with an explicit embedding model | Project index vectors and semantic retrieval |
| Decision | OpenAI-compatible configuration with a supported decision endpoint and explicit model | Auto mode, session routing and command-risk classification |

General can use the selected profile's Flash model when its model is left at the default. Completion, embedding and decision roles require dedicated models. CLI providers use the selected account's credential home. API profiles supply the endpoint and credentials. Model choices are remembered for each provider; a missing configured model can remain visible so it can be corrected.

A listed model is not proof that an endpoint supports FIM, embeddings or decisions. Select models and endpoints that implement the required operation. Decision uses `POST /api/alpha/decisions`, with choice, yes/no (`noul`) and score questions; it does not generate prose. General supplies text tasks and classification fallback when available.

## Routing and completion

Auto mode chooses Ask, Plan or Act. Local rules handle clear cases; configured classification can resolve uncertain cases. Session routing can hold a prompt and offer to move it to an existing conversation, start a new conversation, or send it in the current one. Routing tests are offline by default; the live corpus has a separate `CODALE_ROUTING_LIVE_TESTS` opt-in.

Auto Complete uses the explicitly selected code model. OpenRouter instruct models receive an insertion request through `POST /chat/completions`, with a raw document and `<CURSOR>` marker, capped at 96 generated tokens. Editor requests can include project types and member signatures. Editor ghost text is restricted to the remaining line or one statement on an empty line.

Other OpenAI-compatible completion endpoints use `POST /completions`, with `prompt` and `suffix`, and read `choices[0].text`. These need a FIM-compatible model. Completion roles do not fall back to General. Next-prompt prediction after an agent response is a separate agent-session feature.

## Embeddings and source access

Embedding uses an explicit model through an OpenAI-compatible endpoint. When it is Off, project indexing uses a local hashed embedder. Vector identity includes the endpoint/model configuration, allowing lazy re-embedding when configuration changes without mixing model identities. Workspace memory keeps its local embedder because its stores are shared with MCP processes that do not read app settings.

Remote summaries, exploration, embeddings and completion may send source code to the selected provider. Background exploration receives read-only tools and cannot call `run_shell`. CLI generation runs from an isolated temporary working directory: Claude native tools/MCP are disabled, and Codex uses a read-only sandbox. Codale supplies bounded project context for features that request exploration.

Search UI uses `SourceExplorer` for local retrieval and optional refinement. The removed MCP `explore` tool and old search Ask UI are not required by these provider features. Chat turns do not receive automatic project source retrieval.

## Status and history

The far right of the status bar shows the sparkle and a compact status: Off, Idle, Suggesting, Processing or Error. Hovering shows status details in a tooltip. The status represents active work and unresolved failures; it does not open the former process-log flyout.

`SmartExperienceHost` owns the provider router and status tracker. Provider work retains cancellation, validated output parsing and bounded exploration. Activity history is stored per project under `smart/history` in the application data folder, capped at 10 runs or 1 MiB per project. History storage is separate from an interactive history viewer.

## Implementation and verification

- `Codale.Agents/Providers`: settings rules, API/CLI clients, role routing, decisions and status tracking.
- `Codale.App/Services/SmartExperienceHost.cs`: application-owned provider composition and settings updates.
- `Codale.Smart`: generation/exploration machinery, history, summaries and retained native development classes.
- `Codale.Smart.Tests` and `Codale.Agents.Tests`: offline protocol, routing and feature coverage.
- `Codale.Smart.Smoke`: model-dependent native development evaluation, run separately.

The November 16, 2026 post-merge automated run passed all 163 Smart tests and 406 Agents tests as part of the full 2,372-test suite. It did not call live providers or load native model weights.
