/// <summary>
/// Fill-in-the-middle cases: code before and after a caret, and what belongs there. A proposal passes when its first
/// line, normalised for whitespace, equals one of the accepted answers or starts the expected line.
/// </summary>
static class FimCases
{
    public sealed record Case(string File, string Prefix, string Suffix, string[] Accepted);

    public static readonly Case[] All =
    [
        new("src/MathHelpers.cs", "public static class MathHelpers\n{\n    public static int Add(int a, int b) => ", ";\n\n    public static int Subtract(int a, int b) => a - b;\n}\n", ["a + b"]),
        new("src/MathHelpers.cs", "public static class MathHelpers\n{\n    public static int Add(int a, int b) => a + b;\n\n    public static int Multiply(int a, int b) => ", ";\n}\n", ["a * b"]),
        new("src/User.cs", "public sealed class User\n{\n    public string FirstName { get; set; } = \"\";\n    public string LastName { get; set; } = \"\";\n    public string FullName => ", ";\n}\n", ["$\"{FirstName} {LastName}\"", "FirstName + \" \" + LastName", "$\"{FirstName} {LastName}\".Trim()"]),
        new("src/Guard.cs", "public static void NotNull(object? value, string name)\n{\n    if (value is null) ", "\n}\n", ["throw new ArgumentNullException(name);"]),
        new("src/Loop.cs", "var total = 0;\nfor (var i = 0; i < items.Count; i++)\n{\n    ", "\n}\nreturn total;\n", ["total += items[i];"]),
        new("src/Program.cs", "using System;\n\nclass Program\n{\n    static void Main()\n    {\n        ", "\n    }\n}\n", ["Console.WriteLine(\"Hello, World!\");", "Console.WriteLine(\"Hello World!\");", "Console.WriteLine(\"Hello world\");", "Console.WriteLine(\"Hello World\");", "Console.WriteLine(\"Hello, world!\");"]),
        new("src/Repo.cs", "public async Task<User?> FindAsync(int id, CancellationToken ct)\n{\n    return await _db.Users.FirstOrDefaultAsync(", ", ct);\n}\n", ["u => u.Id == id", "x => x.Id == id", "user => user.Id == id"]),
        new("src/Temperature.cs", "public static double CelsiusToFahrenheit(double celsius) => ", ";\n", ["celsius * 9 / 5 + 32", "(celsius * 9 / 5) + 32", "celsius * 9.0 / 5.0 + 32", "celsius * 1.8 + 32", "celsius * 9.0 / 5.0 + 32.0", "(celsius * 9.0 / 5.0) + 32"]),
        new("src/math.ts", "export function clamp(value: number, min: number, max: number): number {\n  return ", ";\n}\n", ["Math.min(Math.max(value, min), max)", "Math.max(min, Math.min(value, max))", "Math.min(max, Math.max(value, min))", "Math.max(min, Math.min(max, value))"]),
        new("src/api.ts", "export async function getJson<T>(url: string): Promise<T> {\n  const response = await fetch(url);\n  if (!response.ok) throw new Error(`HTTP ${response.status}`);\n  return ", ";\n}\n", ["response.json()", "await response.json()", "(await response.json()) as T", "response.json() as Promise<T>"]),
        new("src/list.ts", "const names = users.map(", ");\n", ["user => user.name", "u => u.name", "(user) => user.name", "(u) => u.name"]),
        new("src/sum.ts", "export const sum = (values: number[]): number => values.reduce(", ");\n", ["(a, b) => a + b, 0", "(total, value) => total + value, 0", "(acc, v) => acc + v, 0", "(sum, value) => sum + value, 0", "(acc, value) => acc + value, 0"]),
        new("app/util.py", "def is_even(n: int) -> bool:\n    return ", "\n\n\ndef is_odd(n: int) -> bool:\n    return n % 2 == 1\n", ["n % 2 == 0"]),
        new("app/files.py", "def read_text(path: str) -> str:\n    with open(path, encoding=\"utf-8\") as f:\n        ", "\n", ["return f.read()"]),
        new("app/squares.py", "squares = [", " for x in range(10)]\n", ["x * x", "x ** 2", "x**2", "x*x"]),
        new("app/greet.py", "def greet(name: str) -> str:\n    return ", "\n", ["f\"Hello, {name}!\"", "f\"Hello {name}\"", "f\"Hello, {name}\"", "\"Hello, \" + name", "f\"Hello {name}!\""]),
        new("src/Point.cs", "public readonly record struct Point(double X, double Y)\n{\n    public double DistanceTo(Point other) => Math.Sqrt(", ");\n}\n", ["Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2)", "(X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y)", "Math.Pow(other.X - X, 2) + Math.Pow(other.Y - Y, 2)", "(other.X - X) * (other.X - X) + (other.Y - Y) * (other.Y - Y)"]),
        new("src/Strings.cs", "public static bool IsBlank(string? text) => ", ";\n", ["string.IsNullOrWhiteSpace(text)", "String.IsNullOrWhiteSpace(text)"]),
        new("src/Max.cs", "public static int Max(int[] values)\n{\n    var best = values[0];\n    foreach (var value in values)\n        if (value > best) ", "\n    return best;\n}\n", ["best = value;"]),
        new("src/Disposable.cs", "public void Dispose()\n{\n    _stream?.Dispose();\n    ", "\n}\n", ["GC.SuppressFinalize(this);", "_stream = null;", "_disposed = true;"]),
    ];

    public static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("( ", "(").Replace(" )", ")");

    /// <summary>What the editor shows for a raw middle: its first line, without a repeat of the rest of the caret line.</summary>
    public static string Shown(Case c, string raw)
    {
        var line = Codale.Core.Text.FimText.FirstLine(raw, c.Suffix, c.Prefix);
        return Codale.Core.Text.FimText.LooksLikeProse(line) ? "" : line;
    }

    public static bool Passes(Case c, string raw)
    {
        var line = Normalize(Shown(c, raw));
        if (line.Length == 0) return false;
        return c.Accepted.Any(a => line == Normalize(a) || line.TrimEnd(';') == Normalize(a).TrimEnd(';'));
    }
}
