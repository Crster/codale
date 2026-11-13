# AI feature providers

General text features offer **Off**, **Claude** (installed CLI), **Codex** (installed CLI),
**OpenAI-compatible endpoints**, and **Anthropic endpoints**. The Auto Complete
and Embedding roles require OpenAI-compatible endpoints. Every option starts off. Enabling Smart no
longer downloads a model or loads a native inference runtime, and app packaging
no longer builds or includes the native inference DLLs.

Smart features are configured by **model role**, not by use. Each role has its own
provider and model (or Off), remembered per provider; OpenAI and Anthropic also take an
endpoint profile. CLI providers use the selected account's sign-in.

| Role | Model | Used for |
|---|---|---|
| **General** | text to text | commit messages, session names, background file summaries, context exploration |
| **Auto Complete** | fill-in-the-middle | editor inline completion, chat composer and shell completion |
| **Embedding** | text to vector | the project index and its `DbNode.Query` semantic search |
| **Decision** | decision model (OpenRouter "System One", e.g. `perplexity/pplx-decider-flash`) | auto mode Ask/Plan/Act, which session a prompt belongs to, command risk level |

General inherits the profile's Flash model when no model is selected. A decision model
only classifies: it is called through `POST /api/alpha/decisions` with choice, yes/no
("noul") and score questions, answers in well under a second, and never writes text.
Shell translation, search keywords and the risk card's description therefore stay on
General, and the classifications run on General's chat prompts while Decision is off.
Completions (editor, composer, shell) need the Auto Complete model; a chat model is too
slow for ghost text. Requests on different roles run side by side.

Auto mode routing picks Ask, Plan or Act for each prompt; the model is asked only when
the local rules are unsure, and a decision model's answer is taken as is when it is at
least 90% sure. With a decision model, every prompt in a session that names a subject is
also checked against the project's recent sessions (without one, only prompts the local
topic check flags go to General). If the prompt belongs to another session, or to a new
one, with at least 60% certainty, it is held back and the composer offers to move it
there, start a new session, or send it here anyway.

Auto Complete requires an explicit code model. OpenRouter instruct models use
`POST /chat/completions` with the code before and after the cursor and instructions
to return only the insertion. Other OpenAI-compatible endpoints require a
fill-in-the-middle (FIM) model. Those requests use
`POST /completions` with `prompt` (code before the cursor) and `suffix` (code after
the cursor), and insert the returned `choices[0].text` without trimming whitespace.
Model catalogues do not reliably declare FIM or embedding capabilities, so select a
model whose provider documents the required support.

Embedding requires an OpenAI-compatible endpoint and an explicit embedding model.
While it is off, the project index uses Codale's local hashed embedder. Vectors carry
the endpoint/model identity, so a changed setting re-embeds lazily without reopening
the project and vectors from different models are never mixed. The agents' memory
store stays on the local embedder: it is shared with the MCP tasks server, which does
not read app settings. Remote summaries and completions can send source code to the
selected provider. Background work (summaries, context) is offered only read tools,
never `run_shell`.

The far right of the status bar always displays the AI sparkle and a compact Smart
status, without a flyout, tooltip, or popup: Off when unconfigured, Idle when no
request is running, Suggesting for interactive requests, Processing for background
work (summaries, context, keywords, embeddings), and Error for an unresolved failure.
Only the latest active request determines the status. When it finishes, remaining
work takes over; old errors do not crowd out active requests.

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
