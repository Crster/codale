using System.Text.Json;
using Codale.Agents.Codex;
using Codale.Agents.Claude;
using Codale.App.ViewModels;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public sealed class PlanningApprovalTests
{
    private const string Script = """
        $ranges = @(@('src/Codale.Smart/SmartConversation.cs',39,71), @('src/Codale.Smart/SmartExperience.cs',114,134), @('src/Codale.Smart/ModelInstaller.cs',1,16), @('src/Codale.Smart/SmartTools.cs',32,56), @('src/Codale.App/ViewModels/ChatViewModel.cs',3712,3737)); foreach ($range in $ranges) { $lines = Get-Content -LiteralPath $range[0]; Write-Output ('FILE ' + $range[0]); for ($i = $range[1]; $i -le $range[2]; $i++) { Write-Output ('{0}: {1}' -f $i, $lines[$i-1]) } }
        """;

    [Theory]
    [InlineData("plan", false)]
    [InlineData("auto", true)]
    [InlineData("ask", false)]
    public async Task Screenshot_read_receives_codex_accept_via_the_app_fast_path(string mode, bool automatic)
    {
        var command = "\"C:\\Users\\Crster\\AppData\\Local\\Microsoft\\WindowsApps\\pwsh.exe\" -Command '" + Script.Replace("'", "''") + "'";
        var frames = new List<JsonElement>();
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = ".", PermissionMode = mode == "ask" ? "manual" : mode })
        {
            MessageWriter = (frame, _) => { frames.Add(JsonSerializer.SerializeToElement(frame)); return Task.CompletedTask; },
        };
        await session.HandleServerRequestAsync(JsonSerializer.SerializeToElement(7), "item/commandExecution/requestApproval",
            JsonSerializer.SerializeToElement(new { @params = new { threadId = "main", itemId = "read", command } }));
        Assert.True(session.Events.TryRead(out var requested));
        var approval = Assert.IsType<ApprovalRequested>(requested);

        // This is the exact application fast path, before interactive-card creation.
        // It deliberately has no dependency on AutoApproveSafeShell.
        var handled = await PlanningApproval.TryApproveInspectionAsync(mode, automatic, approval.ToolName, command,
            () => session.RespondToApprovalAsync(approval.RequestId, ApprovalDecision.Allow()));

        Assert.True(handled);
        var response = Assert.Single(frames);
        Assert.Equal(7, response.GetProperty("id").GetInt32());
        Assert.Equal("accept", response.GetProperty("result").GetProperty("decision").GetString());
    }

    [Theory]
    [InlineData("plan", false, "Bash", "foreach ($p in @('a')) { Set-Content $p changed }")]
    [InlineData("auto", true, "Bash", "Write-Output \"$(Remove-Item a)\"")]
    [InlineData("plan", false, "Bash", "dotnet build")]
    [InlineData("plan", false, "Bash", "unknown-program")]
    [InlineData("plan", false, "Write", "Get-Content a")]
    [InlineData("plan", false, "ExitPlanMode", "Get-Content a")]
    [InlineData("auto", true, "ExitPlanMode", "Get-Content a")]
    [InlineData("manual", false, "Bash", "Get-Content a")]
    [InlineData("acceptEdits", false, "Bash", "Get-Content a")]
    [InlineData("ask", false, "Bash", "foreach ($p in @('a')) { Set-Content $p changed }")]
    [InlineData("ask", false, "PowerShell", "Write-Output \"$(Remove-Item a)\"")]
    [InlineData("ask", false, "Bash", "dotnet build")]
    [InlineData("ask", false, "Bash", "unknown-program")]
    [InlineData("ask", false, "Write", "Get-Content a")]
    [InlineData("ask", false, "Edit", "Get-Content a")]
    [InlineData("ask", false, "Permissions", "Get-Content a")]
    [InlineData("ask", false, "AskUserQuestion", "Get-Content a")]
    [InlineData("ask", false, "ExitPlanMode", "Get-Content a")]
    public async Task Other_requests_do_not_use_the_planning_fast_path(string mode, bool automatic, string tool, string command)
    {
        var replies = 0;
        Assert.False(await PlanningApproval.TryApproveInspectionAsync(mode, automatic, tool, command,
            () => { replies++; return Task.CompletedTask; }));
        Assert.Equal(0, replies);
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    [InlineData("Read")]
    public async Task Shared_fast_path_accepts_both_providers_read_tools(string tool)
    {
        var replies = 0;
        Assert.True(await PlanningApproval.TryApproveInspectionAsync("plan", false, tool, Script,
            () => { replies++; return Task.CompletedTask; }));
        Assert.Equal(1, replies);
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    [InlineData("Read")]
    [InlineData("Glob")]
    [InlineData("Grep")]
    [InlineData("LS")]
    [InlineData("WebSearch")]
    [InlineData("WebFetch")]
    public async Task Ask_reads_receive_claude_allow_without_interactive_approval(string tool)
    {
        var replies = new List<JsonElement>();
        Assert.True(await PlanningApproval.TryApproveInspectionAsync("ask", false, tool, Script, () =>
        {
            var frame = ClaudeAgentSession.BuildApprovalResponse("ask-read", ApprovalDecision.Allow());
            replies.Add(JsonDocument.Parse(ClaudeAgentSession.SerializeFrame(frame)).RootElement.Clone());
            return Task.CompletedTask;
        }));
        var response = Assert.Single(replies).GetProperty("response");
        Assert.Equal("ask-read", response.GetProperty("request_id").GetString());
        Assert.Equal("allow", response.GetProperty("response").GetProperty("behavior").GetString());
    }
}
