# AI feature providers

General text features offer **Off**, **Claude** (installed CLI), **Codex** (installed CLI),
**OpenAI-compatible endpoints**, and **Anthropic endpoints**. Specialized completion
and embedding features require OpenAI-compatible endpoints. Every option starts off. Enabling Smart no
longer downloads a model or loads a native inference runtime, and app packaging
no longer builds or includes the native inference DLLs.

There is no master provider or toggle: all four options remain visible and are
configured independently. Choose a provider and model for each option, or Off to
disable it. OpenAI and Anthropic also have an endpoint choice for each option.
CLI providers use the selected account's sign-in. The options are
**Developer Tools** (commit messages, commands, routing, and briefs),
**Codebase Context** (background summaries and search), **Code Completion**, and
**Search Embeddings**. Developer Tools and Codebase Context inherit the selected
profile's Flash model when no feature-specific model is selected.
Reasoning follows the selected model. Each option remembers its model per provider.

Code Completion requires an explicit fill-in-the-middle (FIM) model. Requests use
`POST /completions` with `prompt` (code before the cursor) and `suffix` (code after
the cursor), and insert the returned `choices[0].text` without trimming whitespace.
The endpoint must support prefix/suffix infilling; chat-only APIs and CLI accounts
are not offered for this feature. Model catalogues do not reliably declare FIM or
embedding capabilities, so select a model whose provider documents the required support.

Embeddings require OpenAI and an explicit embedding model. They are disabled for
CLI and Anthropic providers. Reopen the project to apply a changed embedding
configuration. Vectors carry the endpoint/model identity so vectors from different
models are not mixed. Turning Smart off stops further remote embedding requests.
Remote summaries and completions can send source code to the selected provider;
background summaries remain opt-in.

Remote requests retain validated output parsing, bounded tool exploration, fresh
file evidence, cancellation, and per-project activity history. CLI generation
runs in an isolated temporary working directory. Claude's native tools and MCP
servers are disabled; Codex runs with a read-only sandbox. Codale's own bounded
tools provide project context for features that request exploration.

The library's legacy native runtime and its evaluation tests remain available for
development; the app does not expose or load them. See [native runtime notes](native-runtime.md).

Protocol references: [OpenAI Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create),
[prefix/suffix completions](https://docs.ollama.com/api/openai-compatibility),
[OpenAI embeddings](https://developers.openai.com/api/reference/resources/embeddings/methods/create),
[Codex CLI](https://learn.chatgpt.com/docs/developer-commands), and
[Anthropic Messages](https://platform.claude.com/docs/en/api/messages/create).
