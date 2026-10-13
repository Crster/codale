using System.Diagnostics;

using Codale.Core.Tasks;

namespace Codale.Core.Tests;

public sealed class HostedTaskTests
{
    private static async Task<T> Eventually<T>(Func<T> read, Func<T, bool> done, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var value = read();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            value = read();
        }

        return value;
    }

    private static string Echo(string text) => OperatingSystem.IsWindows() ? $"Write-Output '{text}'" : $"echo {text}";

    [Fact]
    public async Task Captures_output_and_exit_code()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        var task = manager.Start(Echo("hello-tasks"));

        await Eventually(() => task.State, s => s != HostedTaskState.Running);

        Assert.Equal(HostedTaskState.Exited, task.State);
        Assert.Equal(0, task.ExitCode);
        Assert.Contains("hello-tasks", task.Read().Text);
    }

    [Fact]
    public async Task Read_since_an_offset_returns_only_new_output()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        var task = manager.Start(Echo("first"));
        await Eventually(() => task.State, s => s != HostedTaskState.Running);

        var (_, offset) = task.Read();
        Assert.Equal("", task.Read(offset).Text);
    }

    [Fact]
    public async Task Stop_kills_a_running_command()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        var task = manager.Start(OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 120" : "sleep 120");

        var pid = (await Eventually(() => task.Pid, p => p is not null))!.Value;
        Assert.True(task.Stop());

        Assert.Equal(HostedTaskState.Stopped, task.State);
        Assert.False(task.Stop());

        var gone = await Eventually(() =>
        {
            try { return Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { return true; }
        }, exited => exited);
        Assert.True(gone);
    }

    [Fact]
    public async Task Stop_also_kills_a_process_orphaned_by_its_dead_parent()
    {
        // npm -> cmd -> node: the wrapper dies, node lives on, and a parent-link walk
        // (Process.Kill(entireProcessTree)) never finds it. The job object must.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("codale-orphan").FullName;
        var pidFile = Path.Combine(dir, "pid.txt");
        var child = Path.Combine(dir, "child.ps1");
        await File.WriteAllTextAsync(child,
            "$p = Start-Process ping -PassThru -WindowStyle Hidden -ArgumentList '-n','300','127.0.0.1'\r\n" +
            $"Set-Content -Path '{pidFile}' -Value $p.Id\r\n");

        using var manager = new HostedTaskManager(dir);
        var task = manager.Start(
            $"Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile','-File','{child}'; Start-Sleep -Seconds 120");

        try
        {
            var text = await Eventually(() => File.Exists(pidFile) ? File.ReadAllText(pidFile).Trim() : "", t => t.Length > 0);
            var orphan = int.Parse(text);
            Assert.False(Process.GetProcessById(orphan).HasExited);

            task.Stop();

            var gone = await Eventually(() =>
            {
                try { return Process.GetProcessById(orphan).HasExited; }
                catch (ArgumentException) { return true; }
            }, exited => exited);
            Assert.True(gone, "the orphaned grandchild survived Stop");
        }
        finally
        {
            task.Stop();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // The killed processes may still hold it as their working directory for a moment.
            }
        }
    }

    [Fact]
    public void Blank_command_is_rejected()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        Assert.Throws<ArgumentException>(() => manager.Start("  "));
    }

    [Fact]
    public async Task Pipe_round_trip_starts_reads_and_stops()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        using var server = new TaskPipeServer(new LocalTaskService(manager));
        server.Start();
        var client = new TaskPipeClient(server.PipeName, server.Token);

        var started = await client.StartAsync(Echo("over-the-pipe"), "greeter", waitSeconds: 15, CancellationToken.None);
        Assert.Equal("greeter", started.Name);
        Assert.Contains("over-the-pipe", started.Output);
        Assert.Equal("exited", started.State);

        var listed = await client.ListAsync(CancellationToken.None);
        Assert.Contains(listed, t => t.Id == started.Id);

        var read = await client.ReadAsync(started.Id, since: started.NextOffset, tailChars: null, CancellationToken.None);
        Assert.Equal("", read.Output);

        await Assert.ThrowsAsync<TaskServiceException>(() => client.StopAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task Read_since_an_offset_past_the_end_returns_nothing_instead_of_throwing()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        var task = manager.Start(Echo("tail"));
        await Eventually(() => task.State, s => s != HostedTaskState.Running);

        var (text, next) = task.Read(since: 1_000_000);

        Assert.Equal("", text);
        Assert.Equal(task.Read().NextOffset, next);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_break_the_task()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        var task = manager.Start(Echo("boom"));
        task.OutputReceived += (_, _) => throw new InvalidOperationException("subscriber bug");
        task.Finished += _ => throw new InvalidOperationException("subscriber bug");

        await Eventually(() => task.State, s => s != HostedTaskState.Running);

        Assert.Equal(0, task.ExitCode);
        Assert.Contains("boom", task.Read().Text);
    }

    [Fact]
    public async Task Old_finished_tasks_are_dropped_but_recent_ones_stay()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        var first = manager.Start(Echo("one"));
        await Eventually(() => first.State, s => s != HostedTaskState.Running);

        HostedTask? last = null;
        for (var i = 0; i < 24; i++)
        {
            last = manager.Start(Echo("n" + i));
            await Eventually(() => last.State, s => s != HostedTaskState.Running);
        }

        // The next start is what purges.
        var running = manager.Start(Echo("final"));

        Assert.Null(manager.Get(first.Id));
        Assert.NotNull(manager.Get(last!.Id));
        Assert.NotNull(manager.Get(running.Id));
        Assert.True(manager.List().Count <= 21);
    }

    [Fact]
    public async Task Pipe_panel_op_hands_the_report_to_the_host()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        using var server = new TaskPipeServer(new LocalTaskService(manager));
        string? kind = null;
        string? first = null;
        server.PanelPublished = (k, input) =>
        {
            kind = k;
            first = input.GetProperty("todos")[0].GetProperty("content").GetString();
        };

        var response = await server.HandleAsync(
            $$$"""{"token":"{{{server.Token}}}","op":"panel","kind":"todos","input":{"todos":[{"content":"Do it","status":"pending"}]}}""");

        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.Equal("todos", kind);
        Assert.Equal("Do it", first);
    }

    [Fact]
    public async Task Pipe_panel_op_refuses_a_wrong_token()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        using var server = new TaskPipeServer(new LocalTaskService(manager));
        var called = false;
        server.PanelPublished = (_, _) => called = true;

        var response = await server.HandleAsync("""{"token":"wrong","op":"panel","kind":"todos","input":{}}""");

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.False(called);
    }

    [Fact]
    public async Task Pipe_refuses_a_wrong_token()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        using var server = new TaskPipeServer(new LocalTaskService(manager));

        var response = await server.HandleAsync("""{"token":"wrong","op":"list"}""");

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal("Unauthorized.", response["error"]!.GetValue<string>());
        Assert.Empty(manager.List());
    }

    [Fact]
    public void Start_reuses_already_running_task()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        var cmd = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 120" : "sleep 120";
        var first = manager.Start(cmd, "sleeper");
        try
        {
            Assert.Equal(HostedTaskState.Running, first.State);

            var second = manager.Start(cmd, "sleeper");
            Assert.Same(first, second);

            // Reuses even if name is null on subsequent start
            var third = manager.Start(cmd);
            Assert.Same(first, third);

            // Reuses if name matches an existing running task's name
            var fourth = manager.Start(cmd + " ", "sleeper");
            Assert.Same(first, fourth);

            Assert.Single(manager.List());
        }
        finally
        {
            first.Stop();
        }
    }

    [Fact]
    public void TaskStarted_is_not_raised_when_reusing_running_task()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        var starts = 0;
        manager.TaskStarted += _ => starts++;

        var cmd = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 120" : "sleep 120";
        var first = manager.Start(cmd, "sleeper");
        try
        {
            Assert.Equal(1, starts);

            var second = manager.Start(cmd, "sleeper");
            Assert.Same(first, second);
            Assert.Equal(1, starts);
        }
        finally
        {
            first.Stop();
        }
    }

    [Fact]
    public async Task Start_spawns_new_task_if_previous_has_exited()
    {
        using var manager = new HostedTaskManager(Path.GetTempPath());
        var cmd = Echo("once");
        var first = manager.Start(cmd);
        await Eventually(() => first.State, s => s != HostedTaskState.Running);

        var second = manager.Start(cmd);
        try
        {
            Assert.NotSame(first, second);
        }
        finally
        {
            second.Stop();
        }
    }
}
