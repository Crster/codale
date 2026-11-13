namespace Codale.App.Services;

/// <summary>Request outcomes per model role, independent of service availability and model loading.</summary>
public sealed class SmartStatusTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Role, string Task, long Generation, long Order)> _active = [];
    private long _order;
    private readonly Dictionary<string, string?> _errors = [];
    private readonly HashSet<string> _enabled = [];
    private long _generation;
    private DateTimeOffset _configuredAt;
    public event EventHandler? Changed;

    public void Configure(IEnumerable<(string Role, bool Enabled, string? Error)> roles)
    {
        lock (_gate)
        {
            _generation++;
            _configuredAt = DateTimeOffset.UtcNow;
            _active.Clear(); _errors.Clear(); _enabled.Clear();
            foreach (var role in roles)
            {
                if (role.Enabled) _enabled.Add(role.Role);
                if (role.Error is not null) _errors[role.Role] = role.Error;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Start(string id, string task, DateTimeOffset? startedAt = null)
    {
        lock (_gate)
        {
            // A "decide" request is the Decision model's; everything else runs on the first configured role that can write it.
            var role = task == SmartFeatureSettings.DecideTask
                ? _enabled.Contains(SmartFeatureSettings.Decision) ? SmartFeatureSettings.Decision : null
                : SmartFeatureSettings.RolesFor(task).FirstOrDefault(_enabled.Contains);
            if (role is null || startedAt < _configuredAt || _active.ContainsKey(id)) return;
            _active[id] = (role, SmartFeatureSettings.Normalize(task), _generation, ++_order);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Finish(string id, string outcome, string? error = null)
    {
        lock (_gate)
        {
            if (!_active.Remove(id, out var run) || run.Generation != _generation) return;
            if (outcome == "error") _errors[run.Role] = error ?? "The AI request failed.";
            else if (outcome == "completed") _errors.Remove(run.Role);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Working(string task) => SmartFeatureSettings.IsBackgroundTask(task) ? "Processing" : "Suggesting";

    public (string Text, string Detail, bool Visible) Snapshot()
    {
        lock (_gate)
        {
            var entries = SmartFeatureSettings.Roles.Where(r => _enabled.Contains(r.Id) || _errors.ContainsKey(r.Id))
                .Select(r =>
                {
                    var newest = _active.Values.Where(a => a.Role == r.Id).OrderByDescending(a => a.Order).FirstOrDefault();
                    var state = newest.Role is not null ? Working(newest.Task)
                        : _errors.ContainsKey(r.Id) ? "Error" : "Idle";
                    return (r.Label, State: state, Error: _errors.GetValueOrDefault(r.Id));
                }).ToArray();
            // The newest request owns the indicator; completing it reveals any remaining work.
            // Old errors must not crowd out the feature currently being used.
            var newestRun = _active.Values.OrderByDescending(a => a.Order).FirstOrDefault();
            var state = newestRun.Role is not null ? Working(newestRun.Task)
                : _errors.Count > 0 ? "Error" : null;
            var text = state is not null
                ? $"Smart: {state}"
                : entries.Length == 0 ? "Smart: Off" : "Smart: Idle";
            return (text, entries.Length == 0 ? "AI is not configured."
                : string.Join("\n", entries.Select(e => $"{e.Label}: {e.State}" + (e.Error is null ? "" : $" — {e.Error}"))),
                true);
        }
    }
}
