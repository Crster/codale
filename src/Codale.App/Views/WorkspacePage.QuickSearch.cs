using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Views;

/// <summary>
/// The workspace's answers to the global search palette: files on disk, the tabs that are
/// open, past conversations, git changes, project commands and the everyday actions -
/// ranked by how well their names match - with the project-wide content search (and the
/// agentic ask) always offered last.
/// </summary>
public sealed partial class WorkspacePage : IQuickSearchSource
{
    private const int MaxFiles = 12;
    private const int MaxPerGroup = 6;

    public async Task<IReadOnlyList<QuickSearchItem>> SearchAsync(string query, string? fileScope, CancellationToken ct)
    {
        var results = new List<QuickSearchItem>();

        if (fileScope is not null)
        {
            if (query.Length > 0)
            {
                results.Add(new QuickSearchItem
                {
                    Group = "This file",
                    Glyph = "",
                    Title = $"Find “{query}”",
                    Detail = fileScope,
                    Hint = "Enter",
                    Invoke = () => OnTitleBarSearchSubmitted(App.Current.MainWindow, new SearchSubmission(query, InFile: true)),
                });
                AddContentSearch(results, query);
            }

            return results;
        }

        if (query.Length == 0)
        {
            results.AddRange(OpenTabItems("").Take(8));
            results.AddRange(HistoryItems("").Take(5));
            results.AddRange(ActionItems(""));
            return results;
        }

        IReadOnlyList<(string Path, string Directory)> files = [];
        try
        {
            files = await ViewModel.Files.FindFilesAsync(query, MaxFiles, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Unreadable folders: the other groups still answer.
        }

        ct.ThrowIfCancellationRequested();

        results.AddRange(OpenTabItems(query).Take(MaxPerGroup));
        results.AddRange(files.Select(file => new QuickSearchItem
        {
            Group = "Files",
            Glyph = FileIcons.NodeGlyph(false, Path.GetFileName(file.Path)),
            Title = Path.GetFileName(file.Path),
            Detail = file.Directory,
            Invoke = () => ShowFile(file.Path),
        }));
        results.AddRange(ChangeItems(query).Take(MaxPerGroup));
        results.AddRange(HistoryItems(query).Take(MaxPerGroup));
        results.AddRange(CommandItems(query).Take(MaxPerGroup));
        results.AddRange(ActionItems(query));
        AddContentSearch(results, query);
        return results;
    }

    /// <summary>The project-wide search (and the agent that answers questions) for anything the names did not find.</summary>
    private void AddContentSearch(List<QuickSearchItem> results, string query) =>
        results.Add(new QuickSearchItem
        {
            Group = "Search the project",
            Glyph = "",
            Title = $"Search contents for “{query}”",
            Detail = "or ask a question about the code",
            Hint = "Search",
            Invoke = () => OnTitleBarSearchSubmitted(App.Current.MainWindow, new SearchSubmission(query, InFile: false)),
        });

    /// <summary>
    /// How well <paramref name="text"/> answers the query: 0 for a prefix, 1 for a word
    /// start, 2 for anywhere, 3 for the letters in order; -1 for no match. Empty matches all.
    /// </summary>
    private static int MatchRank(string text, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (string.IsNullOrEmpty(text))
        {
            return -1;
        }

        var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at == 0)
        {
            return 0;
        }

        if (at > 0)
        {
            return char.IsLetterOrDigit(text[at - 1]) ? 2 : 1;
        }

        // Letters in order only mean something in a short name; across a sentence they match nearly anything.
        if (text.Length > 48)
        {
            return -1;
        }

        var position = 0;
        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            position = text.IndexOf(c.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (position < 0)
            {
                return -1;
            }

            position++;
        }

        return 3;
    }

    private static IEnumerable<QuickSearchItem> Ranked(IEnumerable<(QuickSearchItem Item, string Text)> rows, string query) =>
        rows.Select(row => (row.Item, Rank: MatchRank(row.Text, query)))
            .Where(row => row.Rank >= 0)
            .OrderBy(row => row.Rank)
            .Select(row => row.Item);

    private IEnumerable<QuickSearchItem> OpenTabItems(string query)
    {
        var rows = new List<(QuickSearchItem, string)>();
        foreach (var group in new[] { CentreTabs, SplitTabs })
        {
            foreach (var item in group.TabItems.OfType<TabViewItem>())
            {
                var title = TabTitle(item.Header);
                if (title.Length == 0)
                {
                    continue;
                }

                var glyph = item.IconSource is FontIconSource icon ? icon.Glyph : TabGlyph(item);
                var target = item;
                rows.Add((new QuickSearchItem
                {
                    Group = "Open tabs",
                    Glyph = glyph,
                    Title = title,
                    Hint = "Switch",
                    Invoke = () => SelectTab(target),
                }, title));
            }
        }

        return Ranked(rows, query);
    }

    private string TabGlyph(TabViewItem item)
    {
        if (_chatTabs.Any(t => ReferenceEquals(t.Item, item)))
        {
            return "";
        }

        if (_terminalTabs.Any(t => ReferenceEquals(t.Item, item)))
        {
            return "";
        }

        if (_editorTabs.FirstOrDefault(t => ReferenceEquals(t.Item, item)) is { } editor)
        {
            return FileIcons.NodeGlyph(false, editor.Tab.ViewModel.FileName ?? "");
        }

        return "";
    }

    /// <summary>The words on a tab: a plain header, or the first text inside a composed one.</summary>
    private static string TabTitle(object? header) => header switch
    {
        string text => text,
        TextBlock block => block.Text,
        Panel panel => panel.Children.Select(TabTitle).FirstOrDefault(t => t.Length > 0) ?? "",
        ContentControl content => TabTitle(content.Content),
        _ => "",
    };

    private IEnumerable<QuickSearchItem> HistoryItems(string query) =>
        Ranked(ViewModel.Sessions.Sessions.Select(session => (new QuickSearchItem
        {
            Group = "History",
            Glyph = session.IsOpen ? "" : "",
            Title = session.Title,
            Detail = session.TurnsText,
            Hint = session.IsWorking ? "running" : session.IsOpen ? "open" : "",
            Invoke = () =>
            {
                if (ViewModel.Sessions.OpenCommand.CanExecute(session))
                {
                    ViewModel.Sessions.OpenCommand.Execute(session);
                }
            },
        }, session.Title + " " + session.Summary.FirstPrompt)), query);

    private IEnumerable<QuickSearchItem> ChangeItems(string query) =>
        Ranked(ViewModel.Git.Changes.Select(change => (new QuickSearchItem
        {
            Group = "Changes",
            Glyph = "",
            Title = Path.GetFileName(change.Path),
            Detail = Path.GetDirectoryName(change.Path)?.Replace('\\', '/') ?? "",
            Hint = "Diff",
            Invoke = () => _ = ShowChangeDiffAsync(change),
        }, change.Path)), query);

    private IEnumerable<QuickSearchItem> CommandItems(string query)
    {
        IReadOnlyList<Codale.Commands.ProjectCommand> commands;
        try
        {
            commands = ViewModel.DiscoverCommands();
        }
        catch (Exception)
        {
            return [];
        }

        return Ranked(commands.Select(command => (new QuickSearchItem
        {
            Group = "Commands",
            Glyph = "",
            Title = command.Name,
            Detail = command.Command,
            Hint = "Run",
            Invoke = () => OpenCommandTab(command),
        }, command.Name + " " + command.Command)), query);
    }

    private IEnumerable<QuickSearchItem> ActionItems(string query)
    {
        var actions = new (string Glyph, string Title, string Keywords, Action Run)[]
        {
            ("", "New chat", "chat conversation agent ask", () => _ = OpenChatOrOfferInstallAsync()),
            ("", "New terminal", "terminal shell console powershell", OpenTerminalTab),
            ("", "New file", "file untitled create", OpenNewFileTab),
            ("", "Show Git", "git changes commit branch", ShowGitPanel),
            ("", "Toggle side panel", "files sidebar left panel", ToggleLeftPanel),
            ("", "Settings", "settings preferences options theme", () => App.Current.MainWindow?.SetSettingsOpen(true)),
            ("", "Open browser", "browser web preview", () => ViewModel.McpTools.OpenBrowserCommand.Execute(null)),
        };

        return Ranked(actions.Select(action => (new QuickSearchItem
        {
            Group = "Actions",
            Glyph = action.Glyph,
            Title = action.Title,
            Invoke = action.Run,
        }, action.Title + " " + action.Keywords)), query).Take(query.Length == 0 ? 4 : MaxPerGroup);
    }
}
