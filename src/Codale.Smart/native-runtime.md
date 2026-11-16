# Native inference development harness

The Windows app uses provider-backed Smart roles. It does not expose a local model picker, download model weights, load this runtime, build the shim during an app build, or package inference DLLs. See [current Smart features](README.md) for the application path.

`Codale.Smart` retains native inference classes, template tests and the `Codale.Smart.Smoke` console harness for development and evaluation. These require a separate native build and model file.

## Native payload and model

`native/build.ps1` downloads checksum-pinned llama.cpp b11960 Vulkan DLLs and compiles `smart.cpp` against headers at revision `8642541b255bfb5dc39cd351a178be09013a55d6`. Build for x64 or ARM64 with the matching Visual Studio C++ tools and Windows SDK:

```powershell
./src/Codale.Smart/native/build.ps1 -Platform x64
```

The current `SmartModels` catalog contains Granite 4.2 8B Q4_K_M. `ModelInstaller` defines its pinned revision, size and checksum; the smoke harness verifies the supplied model file. Keep model files outside the app package.

`SmartConversation` implements the Granite chat template, including tool turns and Off/Low/Full reasoning. The native context is 16,384 tokens. Template goldens live in [tests/fixtures/granite](../../tests/fixtures/granite/README.md).

## Run the harness

Run these commands from the repository root, replacing `<model.gguf>` with a local model path:

```powershell
dotnet test tests/Codale.Smart.Tests
dotnet run --project tests/Codale.Smart.Smoke -- src/Codale.Smart/native/bin/win-x64 <model.gguf> gpu model=granite-4.2-8b-q4
dotnet run --project tests/Codale.Smart.Smoke -- src/Codale.Smart/native/bin/win-x64 <model.gguf> gpu model=granite-4.2-8b-q4 bench
dotnet run --project tests/Codale.Smart.Smoke -- src/Codale.Smart/native/bin/win-x64 <model.gguf> gpu model=granite-4.2-8b-q4 eval
dotnet run --project tests/Codale.Smart.Smoke -- src/Codale.Smart/native/bin/win-x64 <model.gguf> gpu model=granite-4.2-8b-q4 fim-spike
```

Use `cpu` instead of `gpu` to request CPU execution. `protocol-only` and `features-only` select individual smoke stages; `budget-seconds=120` sets the task budget. The current model ID is `granite-4.2-8b-q4`; use `model=granite-4.2-8b-q4` when supplying it explicitly.

The first run above checks readiness and protocol/features. `bench` measures prompt/prefix reuse and generation; `eval` writes accuracy/latency results; `fim-spike` compares completion prompt formats. These runs load real weights and are separate from the ordinary unit-test suite.

## Scope and tool access

The retained runtime supports prefix reuse, constrained output and bounded tool rounds. Runtime behavior depends on the harness configuration; the application's provider roles and status UI are described in the Smart README.

Background exploration uses the read-only tool set. Interactive development requests can include `run_shell` when their caller supplies that tool set; commands may change files and cancellation cannot undo completed side effects. Consult `SmartTools` and the smoke cases before running a tool-enabled evaluation.

Historical native inference timing and quality measurements are not guarantees for the current application or another device. The automated post-merge build/test verification did not load model weights or run this harness.
