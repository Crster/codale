using System.Runtime.CompilerServices;

// The hooks' file housekeeping (output cap, sweeps, refusal memory) is worth pinning with tests.
[assembly: InternalsVisibleTo("Codale.Mcp.Tests")]
