namespace Codale.Commands.Tests;

/// <summary>
/// Discovery over config files Codale does not control. Every parser is tolerant by
/// contract: missing files, missing fields and malformed JSON contribute nothing
/// rather than throwing.
/// </summary>
public sealed class CommandCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-commands-").FullName;

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

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Codale_commands_json_reads_name_command_and_cwd()
    {
        WriteFile(@".codale\commands.json", """
            {
              "commands": [
                { "name": "dev server", "command": "npm run dev", "cwd": "web" },
                { "command": "dotnet build" }
              ]
            }
            """);

        var commands = CommandCatalog.Discover(_root);

        Assert.Equal(2, commands.Count);

        Assert.Equal("dev server", commands[0].Name);
        Assert.Equal("npm run dev", commands[0].Command);
        Assert.Equal("web", commands[0].WorkingDirectory);
        Assert.Equal(CommandSource.Codale, commands[0].Source);
        Assert.EndsWith(Path.Combine(".codale", "commands.json"), commands[0].SourcePath);

        // A missing name falls back to the command itself rather than dropping the entry.
        Assert.Equal("dotnet build", commands[1].Name);
        Assert.Null(commands[1].WorkingDirectory);
    }

    [Fact]
    public void Codale_commands_json_accepts_a_bare_root_array()
    {
        WriteFile(@".codale\commands.json", """[ { "name": "build", "command": "dotnet build" } ]""");

        var commands = CommandCatalog.Discover(_root);

        var command = Assert.Single(commands);
        Assert.Equal("build", command.Name);
        Assert.Equal(CommandSource.Codale, command.Source);
    }

    [Fact]
    public void Claude_launch_json_reads_commands_array()
    {
        WriteFile(@".claude\launch.json", """
            {
              "commands": [
                { "name": "dev", "command": "npm run dev", "cwd": "web" },
                { "name": "broken", "command": "" }
              ]
            }
            """);

        var commands = CommandCatalog.Discover(_root);

        var command = Assert.Single(commands);
        Assert.Equal("dev", command.Name);
        Assert.Equal("npm run dev", command.Command);
        Assert.Equal("web", command.WorkingDirectory);
        Assert.Equal(CommandSource.Claude, command.Source);
    }

    [Fact]
    public void Claude_launch_json_reads_configurations_shape_and_argv_commands()
    {
        WriteFile(@".claude\launch.json", """
            {
              "configurations": [
                { "label": "serve", "run": ["npm", "run", "dev"] }
              ]
            }
            """);

        var commands = CommandCatalog.Discover(_root);

        var command = Assert.Single(commands);
        Assert.Equal("serve", command.Name);
        Assert.Equal("npm run dev", command.Command);
    }

    [Fact]
    public void Claude_launch_json_accepts_a_bare_root_array()
    {
        WriteFile(@".claude\launch.json", """[ { "name": "dev", "command": "npm run dev" } ]""");

        var commands = CommandCatalog.Discover(_root);

        Assert.Equal("dev", Assert.Single(commands).Name);
    }

    [Fact]
    public void Vscode_tasks_json_reads_label_command_args_and_cwd()
    {
        WriteFile(@".vscode\tasks.json", """
            {
              "tasks": [
                {
                  "label": "build",
                  "command": "dotnet",
                  "args": [ "build", "web app.csproj" ],
                  "options": { "cwd": "src" }
                },
                { "label": "no command" },
                {
                  "label": "serve windows",
                  "command": "echo posix",
                  "windows": { "command": "npm", "args": [ "run", "dev" ] }
                }
              ]
            }
            """);

        var commands = CommandCatalog.Discover(_root);

        Assert.Equal(2, commands.Count);

        Assert.Equal("build", commands[0].Name);
        // Arguments containing a space are quoted so the shell keeps them together.
        Assert.Equal("dotnet build \"web app.csproj\"", commands[0].Command);
        Assert.Equal("src", commands[0].WorkingDirectory);
        Assert.Equal(CommandSource.VscodeTasks, commands[0].Source);

        // The windows override replaces both the command and its arguments.
        Assert.Equal("npm run dev", commands[1].Command);
    }

    [Fact]
    public void Vscode_launch_json_derives_a_command_from_executable_and_args()
    {
        WriteFile(@".vscode\launch.json", """
            {
              "configurations": [
                {
                  "name": "Run app",
                  "type": "node",
                  "runtimeExecutable": "node",
                  "runtimeArgs": [ "--inspect" ],
                  "args": [ "server.js", "--port 3000" ],
                  "cwd": "web"
                },
                {
                  "name": "Program only",
                  "type": "coreclr",
                  "program": "dotnet",
                  "args": [ "run" ]
                },
                {
                  "name": "Attach",
                  "type": "node",
                  "request": "attach",
                  "port": 9229
                }
              ]
            }
            """);

        var commands = CommandCatalog.Discover(_root);

        Assert.Equal(2, commands.Count);

        Assert.Equal("Run app", commands[0].Name);
        Assert.Equal("node --inspect server.js \"--port 3000\"", commands[0].Command);
        Assert.Equal("web", commands[0].WorkingDirectory);
        Assert.Equal(CommandSource.VscodeLaunch, commands[0].Source);

        // A program without a runtime executable still derives a command.
        Assert.Equal("dotnet run", commands[1].Command);

        // Attach configurations derive nothing and are skipped.
        Assert.DoesNotContain(commands, c => c.Name == "Attach");
    }

    [Fact]
    public void Vscode_launch_json_tolerates_jsonc_comments_and_trailing_commas()
    {
        WriteFile(@".vscode\launch.json", """
            // VS Code configs are JSONC in the wild.
            {
              "version": "0.2.0",
              "configurations": [
                { "name": "Run", "program": "node", "args": ["app.js"], },
              ],
            }
            """);

        var commands = CommandCatalog.Discover(_root);

        Assert.Equal("node app.js", Assert.Single(commands).Command);
    }

    [Fact]
    public void Package_json_scripts_become_npm_run_commands()
    {
        WriteFile("package.json", """
            {
              "name": "web",
              "scripts": {
                "dev": "vite",
                "build": "vite build"
              }
            }
            """);

        var commands = CommandCatalog.Discover(_root);

        Assert.Equal(2, commands.Count);
        Assert.Equal("dev", commands[0].Name);
        Assert.Equal("npm run dev", commands[0].Command);
        Assert.Equal(CommandSource.PackageJson, commands[0].Source);
        Assert.Null(commands[0].WorkingDirectory);
    }

    [Theory]
    [InlineData("bun.lockb", "bun run dev")]
    [InlineData("pnpm-lock.yaml", "pnpm run dev")]
    [InlineData("yarn.lock", "yarn run dev")]
    public void Package_json_uses_detected_lockfile_package_manager(string lockfile, string expectedCommand)
    {
        WriteFile(lockfile, "");
        WriteFile("package.json", """{ "scripts": { "dev": "vite" } }""");

        var command = Assert.Single(CommandCatalog.Discover(_root));

        Assert.Equal(expectedCommand, command.Command);
    }

    [Theory]
    [InlineData("dev; calc")]
    [InlineData("dev && rm -rf /")]
    [InlineData("dev|more")]
    [InlineData("a b")]
    [InlineData("$(whoami)")]
    [InlineData("`x`")]
    [InlineData("--prefix")]
    [InlineData("\\\"q\\\"")]
    [InlineData("")]
    public void Package_json_scripts_with_unsafe_names_are_skipped(string name)
    {
        var scripts = new System.Text.Json.Nodes.JsonObject { [name] = "echo hi", ["dev"] = "vite" };
        WriteFile("package.json", new System.Text.Json.Nodes.JsonObject { ["scripts"] = scripts }.ToJsonString());

        var command = Assert.Single(CommandCatalog.Discover(_root));

        Assert.Equal("npm run dev", command.Command);
    }

    [Theory]
    [InlineData("test:unit")]
    [InlineData("build.prod")]
    [InlineData("@scope/pkg-build")]
    [InlineData("lint_fix")]
    public void Package_json_script_names_in_the_usual_shapes_are_kept(string name)
    {
        var scripts = new System.Text.Json.Nodes.JsonObject { [name] = "echo hi" };
        WriteFile("package.json", new System.Text.Json.Nodes.JsonObject { ["scripts"] = scripts }.ToJsonString());

        Assert.Equal("npm run " + name, Assert.Single(CommandCatalog.Discover(_root)).Command);
    }

    [Fact]
    public void An_argument_with_an_embedded_quote_cannot_break_out_of_its_quoting()
    {
        WriteFile(@".vscode\launch.json", """
            { "configurations": [ { "name": "x", "program": "node", "args": [ "say \"hi\" now", "plain arg" ] } ] }
            """);

        var command = Assert.Single(CommandCatalog.Discover(_root));

        Assert.Equal("""node 'say "hi" now' "plain arg" """.TrimEnd(), command.Command);
    }

    [Fact]
    public void A_project_with_no_config_files_discovers_nothing()
    {
        Assert.Empty(CommandCatalog.Discover(_root));
    }

    [Fact]
    public void Malformed_files_are_skipped_without_throwing()
    {
        WriteFile(@".codale\commands.json", "{ not json ");
        WriteFile(@".claude\launch.json", "]]]");
        WriteFile(@".vscode\tasks.json", "null");
        WriteFile("package.json", "3");

        Assert.Empty(CommandCatalog.Discover(_root));
    }

    [Fact]
    public void Sources_are_ordered_codale_claude_tasks_launch_npm()
    {
        WriteFile(@".codale\commands.json", """{ "commands": [ { "name": "c", "command": "c" } ] }""");
        WriteFile(@".claude\launch.json", """[ { "name": "l", "command": "l" } ]""");
        WriteFile(@".vscode\tasks.json", """{ "tasks": [ { "label": "t", "command": "t" } ] }""");
        WriteFile(@".vscode\launch.json", """{ "configurations": [ { "name": "d", "program": "d" } ] }""");
        WriteFile("package.json", """{ "scripts": { "p": "x" } }""");

        var sources = CommandCatalog.Discover(_root).Select(c => c.Source).ToList();

        Assert.Equal(
            [CommandSource.Codale, CommandSource.Claude, CommandSource.VscodeTasks, CommandSource.VscodeLaunch, CommandSource.PackageJson],
            sources);
    }

    [Fact]
    public void Deno_tasks_become_deno_task_commands()
    {
        WriteFile("deno.jsonc", """
            {
              // Deno tasks
              "tasks": {
                "dev": "deno run --watch main.ts",
                "serve": { "command": "deno serve server.ts" }
              }
            }
            """);

        var commands = CommandCatalog.Discover(_root);

        Assert.Equal(2, commands.Count);
        Assert.Equal("dev", commands[0].Name);
        Assert.Equal("deno task dev", commands[0].Command);
        Assert.Equal(CommandSource.Deno, commands[0].Source);
        Assert.Equal("deno task serve", commands[1].Command);
    }

    [Fact]
    public void Dotnet_project_and_launch_settings_discover_run_watch_build_and_profiles()
    {
        WriteFile(@"src\App\App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        WriteFile(@"src\App\Properties\launchSettings.json", """
            {
              "profiles": {
                "https": { "commandName": "Project", "applicationUrl": "https://localhost:5001" },
                "IIS Express": { "commandName": "IISExpress" }
              }
            }
            """);

        var commands = CommandCatalog.Discover(_root);

        Assert.All(commands, c => Assert.Equal(CommandSource.Dotnet, c.Source));
        Assert.Contains(commands, c => c.Name == "run" && c.Command == @"dotnet run --project src\App\App.csproj");
        Assert.Contains(commands, c => c.Name == "watch" && c.Command == @"dotnet watch --project src\App\App.csproj");
        Assert.Contains(commands, c => c.Name == "run: https" && c.Command == @"dotnet run --project src\App\App.csproj --launch-profile https");
        Assert.DoesNotContain(commands, c => c.Name.Contains("IIS"));
        Assert.Contains(commands, c => c.Name == "build" && c.Command == "dotnet build");
        Assert.Contains(commands, c => c.Name == "test" && c.Command == "dotnet test");
    }

    [Fact]
    public void Gradle_discovers_wrapper_spring_boot_and_custom_tasks()
    {
        WriteFile("gradlew.bat", "@echo off");
        WriteFile("build.gradle.kts", """
            plugins {
                id("org.springframework.boot") version "3.2.0"
            }
            tasks.register("migrateDb") {
                doLast { println("migrating") }
            }
            """);

        var commands = CommandCatalog.Discover(_root);

        Assert.All(commands, c => Assert.Equal(CommandSource.Gradle, c.Source));
        Assert.Contains(commands, c => c.Name == "bootRun" && c.Command == @".\gradlew bootRun");
        Assert.Contains(commands, c => c.Name == "build" && c.Command == @".\gradlew build");
        Assert.Contains(commands, c => c.Name == "test" && c.Command == @".\gradlew test");
        Assert.Contains(commands, c => c.Name == "migrateDb" && c.Command == @".\gradlew migrateDb");
    }

    [Fact]
    public void Maven_pom_discovers_wrapper_and_spring_boot()
    {
        WriteFile("mvnw.cmd", "@echo off");
        WriteFile("pom.xml", "<project><build><plugins><plugin><artifactId>spring-boot-maven-plugin</artifactId></plugin></plugins></build></project>");

        var commands = CommandCatalog.Discover(_root);

        Assert.All(commands, c => Assert.Equal(CommandSource.Maven, c.Source));
        Assert.Contains(commands, c => c.Name == "spring-boot:run" && c.Command == @".\mvnw spring-boot:run");
        Assert.Contains(commands, c => c.Name == "test" && c.Command == @".\mvnw test");
        Assert.Contains(commands, c => c.Name == "package" && c.Command == @".\mvnw package");
    }

    [Fact]
    public void Python_discovers_django_poetry_scripts_poe_and_pytest()
    {
        WriteFile("manage.py", "#!/usr/bin/env python");
        WriteFile("poetry.lock", "");
        WriteFile("pyproject.toml", """
            [tool.poetry.scripts]
            serve = "app:main"

            [tool.poe.tasks]
            lint = "flake8"

            [tool.pytest.ini_options]
            testpaths = ["tests"]
            """);

        var commands = CommandCatalog.Discover(_root);

        Assert.All(commands, c => Assert.Equal(CommandSource.Python, c.Source));
        Assert.Contains(commands, c => c.Name == "django: runserver" && c.Command == "python manage.py runserver");
        Assert.Contains(commands, c => c.Name == "serve" && c.Command == "poetry run serve");
        Assert.Contains(commands, c => c.Name == "lint" && c.Command == "poe lint");
        Assert.Contains(commands, c => c.Name == "pytest" && c.Command == "pytest");
    }

    [Fact]
    public void Cargo_and_go_discover_standard_commands()
    {
        WriteFile("Cargo.toml", "[package]\nname = \"demo\"");
        WriteFile("go.mod", "module example.com/demo");

        var commands = CommandCatalog.Discover(_root);

        Assert.Contains(commands, c => c.Source == CommandSource.Cargo && c.Name == "run" && c.Command == "cargo run");
        Assert.Contains(commands, c => c.Source == CommandSource.Cargo && c.Name == "test" && c.Command == "cargo test");
        Assert.Contains(commands, c => c.Source == CommandSource.Go && c.Name == "run" && c.Command == "go run .");
        Assert.Contains(commands, c => c.Source == CommandSource.Go && c.Name == "test" && c.Command == "go test ./...");
    }

    [Fact]
    public void Composer_and_laravel_artisan_discover_scripts_and_artisan_commands()
    {
        WriteFile("composer.json", """
            {
              "scripts": {
                "post-autoload-dump": "Illuminate\\Foundation\\ComposerScripts::postAutoloadDump",
                "test": "@php artisan test",
                "analyse": "phpstan analyse"
              }
            }
            """);
        WriteFile("artisan", "#!/usr/bin/env php");

        var commands = CommandCatalog.Discover(_root);

        // Lifecycle hooks like post-autoload-dump are skipped.
        Assert.DoesNotContain(commands, c => c.Name == "post-autoload-dump");
        Assert.Contains(commands, c => c.Source == CommandSource.Composer && c.Name == "test" && c.Command == "composer run-script test");
        Assert.Contains(commands, c => c.Source == CommandSource.Composer && c.Name == "analyse" && c.Command == "composer run-script analyse");
        Assert.Contains(commands, c => c.Source == CommandSource.Laravel && c.Name == "serve" && c.Command == "php artisan serve");
        Assert.Contains(commands, c => c.Source == CommandSource.Laravel && c.Name == "migrate" && c.Command == "php artisan migrate");
    }

    [Fact]
    public void Makefile_justfile_and_taskfile_discover_targets_and_recipes()
    {
        WriteFile("Makefile", """
            .PHONY: build test
            build:
            	go build
            test: build
            	go test
            """);
        WriteFile("justfile", """
            set dotenv-load := true
            @dev port="3000":
                npm run dev
            """);
        WriteFile("Taskfile.yml", """
            version: '3'
            tasks:
              lint:
                cmds:
                  - golangci-lint run
              deploy:
                cmds:
                  - ./deploy.sh
            """);

        var commands = CommandCatalog.Discover(_root);

        Assert.Contains(commands, c => c.Source == CommandSource.Makefile && c.Name == "build" && c.Command == "make build");
        Assert.Contains(commands, c => c.Source == CommandSource.Makefile && c.Name == "test" && c.Command == "make test");
        Assert.DoesNotContain(commands, c => c.Name == ".PHONY");
        Assert.Contains(commands, c => c.Source == CommandSource.Justfile && c.Name == "dev" && c.Command == "just dev");
        Assert.Contains(commands, c => c.Source == CommandSource.Taskfile && c.Name == "lint" && c.Command == "task lint");
        Assert.Contains(commands, c => c.Source == CommandSource.Taskfile && c.Name == "deploy" && c.Command == "task deploy");
    }

    [Fact]
    public void Prisma_docker_compose_and_shell_scripts_are_discovered()
    {
        WriteFile(@"prisma\schema.prisma", "datasource db { provider = \"postgresql\" }");
        WriteFile("compose.yaml", "services:\n  db:\n    image: postgres");
        WriteFile("dev.ps1", "Write-Host 'starting'");

        var commands = CommandCatalog.Discover(_root);

        Assert.Contains(commands, c => c.Source == CommandSource.DockerCompose && c.Name == "up" && c.Command == "docker compose up");
        Assert.Contains(commands, c => c.Source == CommandSource.Prisma && c.Name == "migrate dev" && c.Command == "npx prisma migrate dev");
        Assert.Contains(commands, c => c.Source == CommandSource.Prisma && c.Name == "studio" && c.Command == "npx prisma studio");
        Assert.Contains(commands, c => c.Source == CommandSource.ShellScript && c.Name == "dev.ps1" && c.Command == @"powershell -ExecutionPolicy Bypass -File .\dev.ps1");
    }
}