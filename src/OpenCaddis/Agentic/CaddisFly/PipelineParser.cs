using System.Text.RegularExpressions;

namespace OpenCaddis.Agentic.CaddisFly;

public static partial class PipelineParser
{
    /// <summary>
    /// Parses a pipe-delimited DSL string into a pipeline.
    /// Format: "command1 --key value | approve --prompt 'Continue?' | command2 --key value"
    /// </summary>
    public static CaddisFlyPipeline Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Pipeline string cannot be empty.", nameof(input));

        var segments = SplitPipeSegments(input);
        var steps = new List<PipelineStep>();

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i].Trim();
            if (string.IsNullOrEmpty(segment))
                throw new FormatException($"Empty pipeline segment at position {i + 1}.");

            steps.Add(ParseStep(segment, i));
        }

        return new CaddisFlyPipeline { Steps = steps };
    }

    private static PipelineStep ParseStep(string segment, int index)
    {
        var tokens = Tokenize(segment);
        if (tokens.Count == 0)
            throw new FormatException($"Empty pipeline segment at position {index + 1}.");

        var command = tokens[0];
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? approvalPrompt = null;
        int? timeout = null;
        int retries = 0;
        int retryDelay = 2;

        var j = 1;
        while (j < tokens.Count)
        {
            var token = tokens[j];
            if (token.StartsWith("--"))
            {
                var key = token[2..];
                if (j + 1 < tokens.Count && !tokens[j + 1].StartsWith("--"))
                {
                    var value = tokens[j + 1];
                    args[key] = value;

                    if (key.Equals("prompt", StringComparison.OrdinalIgnoreCase) && command.Equals("approve", StringComparison.OrdinalIgnoreCase))
                        approvalPrompt = value;
                    if (key.Equals("timeout", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var t))
                        timeout = t;
                    if (key.Equals("retries", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var r))
                        retries = r;
                    if (key.Equals("retry-delay", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var rd))
                        retryDelay = rd;

                    j += 2;
                }
                else
                {
                    // Flag-style argument with no value
                    args[key] = "true";
                    j++;
                }
            }
            else
            {
                // Positional argument stored with index key
                args[$"arg{j}"] = token;
                j++;
            }
        }

        var stepName = args.TryGetValue("name", out var n) ? n : $"step-{index + 1}";

        return new PipelineStep
        {
            Name = stepName,
            Command = command,
            Args = args,
            TimeoutSeconds = timeout,
            Retries = retries,
            RetryDelaySeconds = retryDelay,
            ApprovalPrompt = approvalPrompt ?? (command.Equals("approve", StringComparison.OrdinalIgnoreCase) ? "Approval required to continue." : null)
        };
    }

    /// <summary>
    /// Splits input on unquoted pipe characters.
    /// </summary>
    private static List<string> SplitPipeSegments(string input)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                current.Append(c);
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                current.Append(c);
            }
            else if (c == '|' && !inSingle && !inDouble)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        segments.Add(current.ToString());
        return segments;
    }

    /// <summary>
    /// Tokenizes a segment, respecting single and double quotes.
    /// Quotes are stripped from the returned token values.
    /// </summary>
    private static List<string> Tokenize(string segment)
    {
        var tokens = new List<string>();
        var matches = TokenRegex().Matches(segment);

        foreach (Match match in matches)
        {
            if (match.Groups["dq"].Success)
                tokens.Add(match.Groups["dq"].Value);
            else if (match.Groups["sq"].Success)
                tokens.Add(match.Groups["sq"].Value);
            else
                tokens.Add(match.Value);
        }

        return tokens;
    }

    /// <summary>
    /// Applies variable substitution to a pipeline.
    /// Replaces {{variable}} placeholders in step args with provided values.
    /// </summary>
    public static void ApplyVariables(CaddisFlyPipeline pipeline, Dictionary<string, string>? overrides = null)
    {
        var vars = new Dictionary<string, string>(pipeline.Variables, StringComparer.OrdinalIgnoreCase);
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
                vars[key] = value;
        }

        if (vars.Count == 0)
            return;

        foreach (var step in pipeline.Steps)
        {
            var keys = step.Args.Keys.ToList();
            foreach (var key in keys)
            {
                step.Args[key] = SubstituteVariables(step.Args[key], vars);
            }
        }
    }

    private static string SubstituteVariables(string input, Dictionary<string, string> vars)
    {
        return VariableRegex().Replace(input, match =>
        {
            var varName = match.Groups[1].Value;
            return vars.TryGetValue(varName, out var value) ? value : match.Value;
        });
    }

    [GeneratedRegex("""\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|[^\s]+""")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex VariableRegex();
}
