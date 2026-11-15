using Codale.Agents.Providers;
using Codale.Core.Agents;

namespace Codale.App.Services;

/// <summary>Reports Smart request progress on the host's status tracker.</summary>
internal sealed class SmartStatusSink(SmartStatusTracker status) : ISmartRequestStatus
{
    public void Start(string id, string task) => status.Start(id, task);

    public void Finish(string id, string outcome, string? error = null) => status.Finish(id, outcome, error);
}
