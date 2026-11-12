# Native Smart experience

Local IBM Granite 4.2 inference with no OpenAI client, inference server, Python, or agent CLI.

## Model

- **File:** the official `granite-4.2-8b-Q4_K_M.gguf` at revision `93f3f6a8938ee922b784cf4e5b4203cd3428df8f`. The SHA-256 is verified, and the file is stored outside the app package.
- **License:** Apache 2.0 (https://huggingface.co/ibm-granite/granite-4.2-8b-GGUF).
- **Device:** loading uses one GPU, the discrete Vulkan GPU with the most memory. An integrated GPU is used only when it is the only GPU, because splitting layers onto an iGPU's shared memory makes every token wait on it. If the GPU can't load, the CPU is tried once.
- **Context:** 16K tokens (`SmartConversation.ContextTokens` and the shim's `context_tokens`). Flash attention and a q8_0 KV cache keep weights plus cache within an 8 GB GPU; without flash attention the cache falls back to f16.

## Prompt format

- **Chat template:** `SmartConversation` renders the chat template embedded in the GGUF byte for byte. `tests/fixtures/granite` holds goldens rendered by the template itself.
- **Tools:** the `# Tools … <tools>` block, `<tool_call><function=…><parameter=…>` XML calls, and `<tool_response>` turns. Reasoning in earlier turns is truncated before the last user turn, as the template does.
- **Sampling:** temperature 1.0 and top_p 0.95 for every task, IBM's recommendation, also stored in the GGUF. Evaluations pin `SmartExperience.Seed`.
- **Prefix reuse:** each call reuses the KV cache of the longest prompt prefix it shares with the previous call. A repeated system prompt or the next tool round only evaluates its new tail. On an RTX 5050, a repeated 4.5K-token prompt takes about 60 ms instead of 3 s.

## Reasoning per task

The single **Enable thinking** setting maps to one of Granite's three modes for each task (`SmartExperience.ReasoningFor`):

| Task | Thinking off | Thinking on |
|---|---|---|
| Routing, shell translation, completions | off | off (they answer in seconds) |
| Command risk, commit message | off | low effort (`{reasoning effort: low}`, up to 512 tokens) |
| Exploration, summaries, ranking, briefs | off | full |

When reasoning precedes a grammar-constrained answer, it runs as a free pass that stops at `</think>`. The grammar then constrains only the answer.

## Tasks

- **Routing, ranking, shell translation and command risk** run without tools and use output grammars, with strict validation afterwards.
- **Commit messages** read the staged diff directly:
  - the stat in full, then each file's patch
  - lock and generated files omitted
  - large files shortened so the patch fits about 24K characters
  - one grammar-constrained generation; the subject is at most 72 characters
- **Fill-in-the-middle** (`FillInMiddleAsync`):
  - prompt: `<|filename|>path\n<|fim_prefix|>…<|fim_suffix|>…<|fim_middle|>`
  - stops at the end of the line, never queues behind interactive work, and preempts background summaries
  - the editor shows it as ghost text (Settings → Inline code completions)

`native/build.ps1` downloads checksum-pinned llama.cpp b11960 DLLs and compiles the shim against headers at commit
`8642541b255bfb5dc39cd351a178be09013a55d6`. Windows x64 and ARM64 need the matching Visual Studio C++ build tools and
Windows SDK. The app build runs this step when the shim is missing or stale.

```powershell
./src/Codale.Smart/native/build.ps1 -Platform x64
dotnet test tests/Codale.Smart.Tests
dotnet run --project tests/Codale.Smart.Smoke -- src/Codale.Smart/native/bin/win-x64 <model.gguf> gpu            # protocol + features
dotnet run --project tests/Codale.Smart.Smoke -- src/Codale.Smart/native/bin/win-x64 <model.gguf> gpu bench      # speed, prefix reuse
dotnet run --project tests/Codale.Smart.Smoke -- src/Codale.Smart/native/bin/win-x64 <model.gguf> gpu eval       # accuracy/latency report
dotnet run --project tests/Codale.Smart.Smoke -- src/Codale.Smart/native/bin/win-x64 <model.gguf> gpu fim-spike  # FIM prompt formats
```

## Runtime behaviour

- **Lifecycle:** Smart experience starts disabled. Disabling it cancels work and unloads the weights. Basic search, indexing and project retrieval stay available.
- **Scheduling:** interactive inference preempts summary generation and cancels its running commands. Routing and discovery share a 120-second prompt budget that includes queue time; background summaries get 20 seconds. The last five seconds are reserved for the final answer. When time runs out, basic retrieval and local routing take over.
- **Commits:** a draft is discarded if the message or the staged tree changes during generation.

## Tools

- **Access:** exploring tasks can list directories, search accessible files, read text ranges and run noninteractive shell commands with the app user's permissions, including in background summaries. Commands default to the captured worktree but may choose another directory or change files. Cancellation cannot undo side effects a command has already had.
- **Limits:**
  - interactive requests get eight rounds and eight calls, 2,048 generated tokens per round, and 4 KiB tool responses
  - each command runs for at most 60 seconds
  - text reads are limited to 16 MiB; searches to 5,000 files and 1 MiB of text per file

## History

- **Status log:** the status bar beside index status opens a scrollable process log on hover, click or keyboard activation.
- **Storage:** history and reusable discoveries live in `smart/history` under the app data directory, capped at 200 runs or 10 MiB per project.
- **Reuse:** discoveries are scoped to a worktree. File hashes are rechecked; command output is timestamped historical evidence.
