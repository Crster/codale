using System.Text.RegularExpressions;
using System.Management.Automation.Language;

namespace Codale.Core.Shell;

/// <summary>A conservative subset of shell syntax that permits file inspection during planning.</summary>
public static class PlanningShellPolicy
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get-Content", "Get-ChildItem", "Get-Item", "Get-Location", "Test-Path", "Resolve-Path",
        "Select-String", "Select-Object", "Measure-Object", "Get-FileHash",
        "Format-Table", "Format-List", "Out-String", "Write-Output",
        "Get-Date", "Get-Process", "Get-Service", "Get-Command", "Get-Help", "Get-Member", "Get-Alias", "Get-PSDrive",
        "Get-Host", "Get-Acl", "Get-ItemProperty", "Get-ItemPropertyValue", "Get-Variable", "Get-Random", "Get-Culture",
        "Compare-Object", "Split-Path", "Join-Path", "Convert-Path", "ConvertFrom-Json", "ConvertTo-Json", "ConvertTo-Csv",
        "ConvertFrom-Csv", "Write-Host", "Out-Host", "Sort-Object", "Group-Object", "Format-Wide", "Select-Xml",
        "rg", "grep", "cat", "head", "tail", "wc", "ls", "pwd", "findstr", "dir", "type", "echo", "whoami", "hostname",
    };

    /// <summary>Reads may proceed during planning; writes and plan review still need approval.</summary>
    public static bool AllowsToolInspection(string toolName, string? command = null) => toolName switch
    {
        "Read" or "Glob" or "Grep" or "LS" or "WebSearch" or "WebFetch" => true,
        "Bash" or "PowerShell" => AllowsInspection(command),
        _ => false,
    };

    /// <summary>
    /// Recognizes inspection commands and a restricted PowerShell syntax tree. Parsing
    /// never executes the script; every nested operation must be recognized.
    /// </summary>
    public static bool AllowsInspection(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        // Codex reports PowerShell's transport wrapper rather than just its script.
        var wrapper = Regex.Match(command.Trim(),
            "^(?:\"(?<exe>[^\"]+)\"|(?<exe>\\S+))\\s+-(?:Command|c)\\s+(?<body>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (wrapper.Success)
        {
            var executable = wrapper.Groups["exe"].Value.Replace('\\', '/').Split('/').Last();
            if (executable.ToLowerInvariant() is not ("pwsh" or "pwsh.exe" or "powershell" or "powershell.exe"))
                return false;
            command = wrapper.Groups["body"].Value.Trim();
            if (command.Length < 2 || command[0] is not ('\'' or '"') || command[^1] != command[0])
                return false;
            // Decode PowerShell's literal quoting rather than stripping quotes: Codex
            // doubles embedded single quotes in its transport representation.
            var quoted = Parser.ParseInput(command, out _, out var quoteErrors);
            if (quoteErrors.Length != 0 || quoted.EndBlock?.Statements.Count != 1 ||
                quoted.EndBlock.Statements[0] is not PipelineAst { PipelineElements.Count: 1 } literal ||
                literal.PipelineElements[0] is not CommandExpressionAst expression ||
                expression.Expression is not StringConstantExpressionAst body)
                return false;
            command = body.Value;
        }

        if (command.Contains('\0')) return false;
        var script = Parser.ParseInput(command.ReplaceLineEndings("\n"), out _, out var errors);
        return errors.Length == 0 && script.EndBlock?.Statements.Count > 0 &&
            new InspectionValidator().Allows(script);
    }

    private sealed class InspectionValidator
    {
        private readonly HashSet<string> _locals = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "true", "false", "null", "args", "input", "this", "_", "foreach", "switch",
            "Host", "HOME", "PID", "Error", "ExecutionContext", "ShellId", "StackTrace",
            "LASTEXITCODE", "NestedPromptLevel", "Matches", "MyInvocation", "OFS",
            "OutputEncoding", "EnabledExperimentalFeatures", "IsWindows", "IsLinux", "IsMacOS", "IsCoreCLR",
        };

        public bool Allows(Ast ast) => ast switch
        {
            ScriptBlockAst s => s.ParamBlock is null && s.BeginBlock is null && s.ProcessBlock is null &&
                s.CleanBlock is null && s.DynamicParamBlock is null && s.Attributes.Count == 0 &&
                s.UsingStatements.Count == 0 && s.ScriptRequirements is null && s.EndBlock is not null && Allows(s.EndBlock),
            NamedBlockAst b => b.Unnamed && (b.Traps is null || b.Traps.Count == 0) && b.Statements.All(Allows),
            StatementBlockAst b => (b.Traps is null || b.Traps.Count == 0) && b.Statements.All(Allows),
            PipelineAst p => !p.Background && p.PipelineElements.All(Allows),
            PipelineChainAst p => !p.Background && Allows(p.LhsPipelineChain) && Allows(p.RhsPipeline),
            CommandExpressionAst e => e.Redirections.Count == 0 && Allows(e.Expression),
            CommandAst c => AllowsCommand(c),
            AssignmentStatementAst a => a.Operator == TokenKind.Equals && a.Left is VariableExpressionAst v &&
                IsLocalName(v) && Allows(a.Right) && Declare(v),
            ForEachStatementAst f => f.Flags == ForEachFlags.None && f.ThrottleLimit is null &&
                f.Label is null && Allows(f.Condition) && IsLocalName(f.Variable) &&
                Declare(f.Variable) && Allows(f.Body),
            ForStatementAst f => f.Label is null && f.Initializer is not null && f.Condition is not null &&
                f.Iterator is not null && Allows(f.Initializer) && Allows(f.Condition) && Allows(f.Iterator) && Allows(f.Body),
            ParenExpressionAst p => Allows(p.Pipeline),
            ArrayExpressionAst a => Allows(a.SubExpression),
            SubExpressionAst s => Allows(s.SubExpression),
            ArrayLiteralAst a => a.Elements.All(Allows),
            IndexExpressionAst i => Allows(i.Target) && Allows(i.Index),
            // Only array/string size properties are needed for line-range inspection.
            MemberExpressionAst m when m is not InvokeMemberExpressionAst => !m.Static && Allows(m.Expression) &&
                m.Member is StringConstantExpressionAst name && (name.Value.Equals("Count", StringComparison.OrdinalIgnoreCase) ||
                    name.Value.Equals("Length", StringComparison.OrdinalIgnoreCase)),
            ExpandableStringExpressionAst s => s.NestedExpressions.All(Allows),
            StringConstantExpressionAst => true,
            ConstantExpressionAst c => c.Value is null or bool or int or long or double or decimal,
            VariableExpressionAst v => !v.Splatted && v.VariablePath.IsUnqualified &&
                (_locals.Contains(v.VariablePath.UserPath) || v.VariablePath.UserPath.ToLowerInvariant() is "true" or "false" or "null"),
            BinaryExpressionAst b => b.Operator is (TokenKind.Plus or TokenKind.Minus or TokenKind.Multiply or
                TokenKind.Divide or TokenKind.Rem or TokenKind.Format or TokenKind.DotDot or TokenKind.Ieq or
                TokenKind.Ine or TokenKind.Ilt or TokenKind.Ile or TokenKind.Igt or TokenKind.Ige or
                TokenKind.And or TokenKind.Or) && Allows(b.Left) && Allows(b.Right),
            UnaryExpressionAst u => AllowsUnary(u),
            // Script blocks, functions, types, invocation, redirection and unknown nodes fail closed.
            _ => false,
        };

        private static bool IsLocalName(VariableExpressionAst v)
        {
            var name = v.VariablePath.UserPath;
            return !v.Splatted && v.VariablePath.IsUnqualified && Regex.IsMatch(name, @"^[a-zA-Z][a-zA-Z0-9_]*$") &&
                !Reserved.Contains(name) && !name.StartsWith("PS", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith("Preference", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("Maximum", StringComparison.OrdinalIgnoreCase);
        }

        private bool Declare(VariableExpressionAst v)
        {
            _locals.Add(v.VariablePath.UserPath);
            return true;
        }

        private bool AllowsUnary(UnaryExpressionAst u) => u.TokenKind switch
        {
            TokenKind.PlusPlus or TokenKind.MinusMinus or TokenKind.PostfixPlusPlus or TokenKind.PostfixMinusMinus =>
                u.Child is VariableExpressionAst v && IsLocalName(v) && _locals.Contains(v.VariablePath.UserPath),
            TokenKind.Plus or TokenKind.Minus or TokenKind.Not or TokenKind.Exclaim => Allows(u.Child),
            _ => false,
        };

        private bool AllowsCommand(CommandAst c)
        {
            if (c.InvocationOperator != TokenKind.Unknown || c.Redirections.Count != 0 || c.CommandElements.Count == 0 ||
                c.CommandElements[0] is not StringConstantExpressionAst name)
                return false;

            if (name.Value.Equals("git", StringComparison.OrdinalIgnoreCase))
            {
                if (c.CommandElements.Count < 2 || c.CommandElements[1] is not StringConstantExpressionAst subcommand ||
                    subcommand.Value.ToLowerInvariant() is not ("status" or "ls-files")) return false;
            }
            else if (!Commands.Contains(name.Value)) return false;

            foreach (var arg in c.CommandElements.Skip(1))
            {
                if (arg is CommandParameterAst parameter)
                {
                    // Common parameters can mutate variables or emit transcripts to disk.
                    if (new[] { "ov", "pv", "ev", "wv", "iv", "ob", "OutVariable", "PipelineVariable",
                        "ErrorVariable", "WarningVariable", "InformationVariable", "OutBuffer" }.Any(p =>
                            p.StartsWith(parameter.ParameterName, StringComparison.OrdinalIgnoreCase))) return false;
                    if (parameter.Argument is not null && !Allows(parameter.Argument)) return false;
                }
                else if (!Allows(arg)) return false;
            }

            // Keep executable ripgrep options literal so they cannot be assembled from variables.
            if (name.Value.Equals("rg", StringComparison.OrdinalIgnoreCase))
                return c.CommandElements.Skip(1).All(a => a is StringConstantExpressionAst or CommandParameterAst) &&
                    !c.CommandElements.Skip(1).Any(a => Regex.IsMatch(
                        a is StringConstantExpressionAst s ? s.Value : a.Extent.Text,
                        @"^--(?:pre|hostname-bin)(?:=|$)", RegexOptions.IgnoreCase));
            return true;
        }
    }
}
