namespace Codale.Search.Tests;

/// <summary>The ghost-text header: the project's own types the file uses, with their members, so "_board." is not a guess.</summary>
public sealed class CompletionContextTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-completion-").FullName;

    public CompletionContextTests()
    {
        Write("Board.cs", """
            public sealed class Board
            {
                private readonly Random _random = new();

                public int Width { get; }

                /// <summary>Puts food on a free cell.</summary>
                public bool PlaceFood(Snake snake)
                {
                    return true;
                }

                private void Reset() { }
            }
            """);
        Write("Geometry.cs", """
            public enum Direction
            {
                Up,
                Down,
            }
            """);
        Write("Game.cs", """
            public sealed class Game
            {
                private readonly Board _board = new();

                public void Run()
                {
                    var heading = Direction.Up;
                    _board.
                }
            }
            """);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_header_outlines_the_members_of_project_types_used_in_the_file()
    {
        using var index = new SourceIndex(_root, new SourceIndexOptions { ModelPath = Path.Combine(_root, "model.json") });
        var snapshot = await index.GetSnapshotAsync();

        var header = CompletionContext.Build(snapshot, _root, Path.Combine(_root, "Game.cs"), "csharp", "var heading = Direction.Up;\n_board.");

        Assert.Contains("public sealed class Board   [Board.cs]", header);
        Assert.Contains("public bool PlaceFood(Snake snake)", header);
        Assert.Contains("public int Width { get; }", header);
        Assert.DoesNotContain("Reset", header);
        Assert.Contains("public enum Direction { Up, Down }", header);
        // The field "_board" names the type the caret needs, so Board is listed before Direction.
        Assert.True(header.IndexOf("class Board", StringComparison.Ordinal) < header.IndexOf("enum Direction", StringComparison.Ordinal));
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
