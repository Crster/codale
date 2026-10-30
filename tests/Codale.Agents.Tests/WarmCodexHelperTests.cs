using System.Diagnostics;
using System.Text.Json;

using Codale.Core.Agents;
using Codale.Core.Helper;

namespace Codale.Agents.Tests;

public sealed class WarmCodexHelperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prewarming_initializes_without_inference_and_preserves_early_turn_events(bool earlyEvents)
    {
        using var server = new FakeServer(earlyEvents ? "early" : "success");
        using var helper = server.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal("helper-thread", await helper.Ready.WaitAsync(deadline.Token));
        var before = server.Requests();
        Assert.Equal(new[] { "initialize", "initialized", "thread/start" }, before.Select(Method));
        var thread = before[^1].GetProperty("params");
        Assert.True(thread.GetProperty("ephemeral").GetBoolean());
        Assert.Equal("read-only", thread.GetProperty("sandbox").GetString());
        Assert.Equal("Write a title.", thread.GetProperty("developerInstructions").GetString());
        Assert.Equal(HelperModel.DefaultCodexModel, thread.GetProperty("model").GetString());

        UsageSnapshot? usage = null;
        Assert.Equal("A short title", await helper.AskAsync("My request", deadline.Token, TimeSpan.FromSeconds(10), u => usage = u));
        Assert.Equal(10, usage!.Value.InputTokens);
        Assert.Equal(3, usage.Value.OutputTokens);
        var turn = server.Requests().Single(r => Method(r) == "turn/start").GetProperty("params");
        Assert.Equal("low", turn.GetProperty("effort").GetString());
        Assert.Equal("My request", turn.GetProperty("input")[0].GetProperty("text").GetString());
        Assert.False(helper.IsAlive);
    }

    [Fact]
    public async Task Cancellation_stops_a_waiting_helper()
    {
        using var server = new FakeServer("stall");
        using var helper = server.Start();
        await helper.Ready.WaitAsync(TimeSpan.FromSeconds(15));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => helper.AskAsync("request", cancel.Token, TimeSpan.FromSeconds(10)));
        Assert.False(helper.IsAlive);
    }

    [Fact]
    public async Task Protocol_errors_are_reported_and_the_helper_is_disposed()
    {
        using var server = new FakeServer("error");
        using var helper = server.Start();
        var error = await Assert.ThrowsAsync<HelperModelException>(() => helper.AskAsync("request", default, TimeSpan.FromSeconds(15)));
        Assert.Contains("Not logged in", error.Message);
        Assert.False(helper.IsAlive);
    }

    private static string? Method(JsonElement frame) => frame.GetProperty("method").GetString();

    private sealed class FakeServer : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "codale-helper-test-" + Guid.NewGuid().ToString("N"));
        private readonly string _mode;

        public FakeServer(string mode)
        {
            _mode = mode;
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "server.ps1"), """
                param($Log, $Mode)
                function Send($value) { [Console]::Out.WriteLine(($value | ConvertTo-Json -Depth 20 -Compress)) }
                while ($null -ne ($line = [Console]::ReadLine())) {
                    $r = $line | ConvertFrom-Json
                    [IO.File]::AppendAllText($Log, $line + "`n")
                    switch ($r.method) {
                        'initialize' { Send @{ id=$r.id; result=@{} } }
                        'thread/start' { Send @{ id=$r.id; result=@{ thread=@{ id='helper-thread' } } } }
                        'turn/start' {
                            if ($Mode -eq 'stall') { continue }
                            if ($Mode -eq 'error') { Send @{ id=$r.id; error=@{ code=-1; message='Not logged in' } }; continue }
                            if ($Mode -ne 'early') { Send @{ id=$r.id; result=@{ turn=@{ id='turn' } } } }
                            Send @{ method='item/completed'; params=@{ item=@{ type='agentMessage'; text='A short title' } } }
                            Send @{ method='thread/tokenUsage/updated'; params=@{ tokenUsage=@{ total=@{ inputTokens=10; outputTokens=3 } } } }
                            Send @{ method='turn/completed'; params=@{ turn=@{ status='completed' } } }
                            if ($Mode -eq 'early') { Send @{ id=$r.id; result=@{ turn=@{ id='turn' } } } }
                        }
                    }
                }
                """);
        }

        public HelperModel.WarmCodexProcess Start()
        {
            // PowerShell is built into Windows; pwsh is also the supported development shell on Unix.
            var info = HelperModel.BuildStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(_folder, "server.ps1"), Path.Combine(_folder, "requests.jsonl"), _mode })
            {
                info.ArgumentList.Add(arg);
            }
            return HelperModel.WarmCodexProcess.TryStart(info, "Write a title.", HelperModel.DefaultCodexModel)!;
        }

        public JsonElement[] Requests() => File.ReadAllLines(Path.Combine(_folder, "requests.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }
}
