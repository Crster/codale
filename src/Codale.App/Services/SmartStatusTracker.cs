namespace Codale.App.Services;

/// <summary>Request outcomes, independent of service availability and model loading.</summary>
public sealed class SmartStatusTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Group, long Generation, long Order)> _active = [];
    private long _order;
    private readonly Dictionary<string, string?> _errors = [];
    private readonly HashSet<string> _enabled = [];
    private long _generation;
    private DateTimeOffset _configuredAt;
    public event EventHandler? Changed;

    public void Configure(IEnumerable<(string Group, bool Enabled, string? Error)> features)
    {
        lock (_gate)
        {
            _generation++;
            _configuredAt = DateTimeOffset.UtcNow;
            _active.Clear(); _errors.Clear(); _enabled.Clear();
            foreach (var feature in features)
            {
                if (feature.Enabled) _enabled.Add(feature.Group);
                if (feature.Error is not null) _errors[feature.Group] = feature.Error;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Start(string id, string feature, DateTimeOffset? startedAt = null)
    {
        lock (_gate)
        {
            var group = SmartFeatureSettings.GroupFor(feature);
            if (startedAt < _configuredAt || !_enabled.Contains(group) || _active.ContainsKey(id)) return;
            _active[id] = (group, _generation, ++_order);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Finish(string id, string outcome, string? error = null)
    {
        lock (_gate)
        {
            if (!_active.Remove(id, out var run) || run.Generation != _generation) return;
            if (outcome == "error") _errors[run.Group] = error ?? "The AI request failed.";
            else if (outcome == "completed") _errors.Remove(run.Group);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public (string Text, string Detail, bool Visible) Snapshot()
    {
        lock (_gate)
        {
            var entries = SmartFeatureSettings.Groups.Where(g => _enabled.Contains(g.Id) || _errors.ContainsKey(g.Id))
                .Select(g =>
                {
                    var active = _active.Values.Any(a => a.Group == g.Id);
                    var state = active ? (g.Id is "context" or "embedding" ? "Processing" : "Suggesting")
                        : _errors.ContainsKey(g.Id) ? "Error" : "Idle";
                    return (g.Label, State: state, Error: _errors.GetValueOrDefault(g.Id));
                }).ToArray();
            // The newest request owns the indicator; completing it reveals any remaining work.
            // Old errors must not crowd out the feature currently being used.
            var currentGroup = _active.Values.OrderByDescending(a => a.Order).FirstOrDefault().Group;
            currentGroup ??= _errors.Keys.LastOrDefault();
            var current = entries.FirstOrDefault(e => e.Label ==
                SmartFeatureSettings.Groups.FirstOrDefault(g => g.Id == currentGroup).Label);
            var visible = currentGroup is not null;
            var text = visible
                ? $"Smart: {current.State}"
                : entries.Length == 0 ? "Smart: Off" : "Smart: Idle";
            return (text, entries.Length == 0 ? "AI is not configured."
                : string.Join("\n", entries.Select(e => $"{e.Label}: {e.State}" + (e.Error is null ? "" : $" — {e.Error}"))),
                true);
        }
    }
}
