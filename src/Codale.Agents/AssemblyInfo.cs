using System.Runtime.CompilerServices;

// Session internals (the live model/effort) are worth
// pinning with tests without standing up the real runtime.
[assembly: InternalsVisibleTo("Codale.Agents.Tests")]
