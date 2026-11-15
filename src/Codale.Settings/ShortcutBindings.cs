namespace Codale.Settings;

public sealed record ShortcutAction(string Id, string Label, string Description, string Default);

/// <summary>Owns the settings subscription and normalized shortcut map for one application.</summary>
public sealed class ShortcutBindings : IDisposable
{
    private readonly AppSettingsStore _settings;
    private readonly IReadOnlyList<ShortcutAction> _actions;
    private readonly Func<string, string?> _normalize;
    private Dictionary<string, string>? _map;
    private bool _disposed;

    public ShortcutBindings(AppSettingsStore settings, IReadOnlyList<ShortcutAction> actions, Func<string, string?> normalize)
    {
        _settings = settings;
        _actions = [.. actions];
        _normalize = normalize;
        settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _map = null;

    public string Get(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fallback = _actions.First(action => action.Id == id).Default;
        return _settings.GetShortcut(id) switch
        {
            null => fallback,
            var stored when stored.Equals("none", StringComparison.OrdinalIgnoreCase) => "",
            var stored => _normalize(stored) ?? fallback,
        };
    }

    public void Set(string id, string chord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fallback = _actions.First(action => action.Id == id).Default;
        _settings.SetShortcut(id, chord == fallback ? null : chord.Length == 0 ? "none" : chord);
    }

    public string? Find(string chord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_map is null)
        {
            _map = [with(StringComparer.Ordinal)];
            foreach (var action in _actions)
                if (Get(action.Id) is { Length: > 0 } value && _normalize(value) is { } key)
                    _map.TryAdd(key, action.Id);
        }
        return _map.GetValueOrDefault(chord);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _map = null;
    }
}