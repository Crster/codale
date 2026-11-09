# Native Smart experience

Independent LFM3 inference: no OpenAI client, inference server, Python, or agent CLI.

The app downloads the official Q4_K_M GGUF at revision
`cd0c74c575b5c416f3011b5b4c298077f26ca8c2`, verifies SHA-256, and stores it outside the
app package. Inference uses 8K context, greedy sampling, and the official ChatML format
with thinking disabled. Routing and ranking also use native output grammars.
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
preempts summary generation. Callers impose the shared five-second prompt budget;
timeouts retain basic retrieval and local routing. Commit drafts use staged changes
and are discarded if the message or staged tree changes during generation.
