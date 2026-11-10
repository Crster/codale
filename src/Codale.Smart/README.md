# Native Smart experience

Independent LFM3 inference: no OpenAI client, inference server, Python, or agent CLI.

The app downloads the official Q4_K_M GGUF at revision
`cd0c74c575b5c416f3011b5b4c298077f26ca8c2`, verifies SHA-256, and stores it outside the
app package. Inference uses 8K context, greedy sampling, and the official ChatML format
with thinking off by default. Smart Experience's persistent **Enable thinking** setting
applies to the next request without reloading weights. Routing, ranking and commit
messages use native grammars only after exploration, retaining strict final validation.
Model license: https://huggingface.co/LiquidAI/LFM3-1.7B-GGUF.

`native/build.ps1` downloads checksum-pinned llama.cpp b11960 DLLs and compiles the
shim against headers at commit `8642541b255bfb5dc39cd351a178be09013a55d6`.
Windows x64 and ARM64 require the corresponding Visual Studio C++ build tools and
Windows SDK. The app build invokes this step when the shim is missing or stale.
The Vulkan release includes CPU backends; loading tries GPU then CPU once.

```powershell
./src/Codale.Smart/native/build.ps1 -Platform x64
dotnet test tests/Codale.Smart.Tests
dotnet run --project tests/Codale.Smart.Smoke -- src/Codale.Smart/native/bin/win-x64 <model.gguf> gpu
dotnet run --project tests/Codale.Smart.Smoke -- src/Codale.Smart/native/bin/win-x64 <model.gguf> cpu
```

Smart experience starts disabled. Disabling cancels work and unloads weights. Basic
search, indexing, and project retrieval remain available. Interactive inference
preempts summary generation and cancels its running commands. Routing and discovery
share a 120-second prompt budget, including queue time; background summaries and commit
drafts each have 120 seconds. The final five seconds are reserved for finalization.
Timeouts retain basic retrieval and local routing. Commit drafts use staged changes
and are discarded if the message or staged tree changes during generation.

Every Smart task can list directories, search accessible files, read text ranges and run
arbitrary noninteractive shell commands with the app user's permissions, including in
background summaries. Commands default to the captured worktree and may choose another
directory or change files. Cancellation cannot undo completed command side effects.
Requests permit eight rounds and eight calls, with 2,048 generated tokens per exploration
round, 4 KiB tool responses, and at most 60 seconds per command. Native token counting
keeps prompt plus generation within 8K by dropping old complete exchanges; text reads
are limited to 16 MiB, searches to 5,000 files and 1 MiB of text per file.

The status bar beside index status opens a scrollable process log on hover, click or
keyboard activation. History and reusable discoveries live in `smart/history` under
the app data directory, capped at 200 runs or 10 MiB per project. Discoveries are scoped
to a worktree; file hashes are rechecked, while command output is timestamped historical
evidence. Relevant current and background evidence share a 12 KiB chat-context budget;
basic fallback retains its 3,600-byte budget. Project Knowledge still controls automatic
context injection. Source summaries also track inspected file dependencies and thinking
mode so stale cache entries are not reused.
