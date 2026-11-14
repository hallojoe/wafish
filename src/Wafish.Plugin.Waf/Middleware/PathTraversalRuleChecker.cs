using System.Text.RegularExpressions;

namespace Wafish.Plugin.Waf;

/// <summary>
/// Detects path traversal attacks: raw ../ and URL-encoded variants (%2e%2e/, %252e%252e).
/// Checks both the raw request path and the decoded query string.
/// </summary>
public sealed class PathTraversalRuleChecker : IWafRuleChecker
{
    public string RuleId => "path-traversal";
    public static readonly PathTraversalRuleChecker Instance = new();

    /// <summary>
    /// Two raw or encoded dots followed by a raw or encoded path separator, or the end of the path.
    /// Fixed repetition covers mixed single/double encoding without treating 5, c, or % as separators.
    /// Retain the existing conservative rejection of two double-encoded dots with any suffix.
    /// </summary>
    private static readonly Regex Pattern = new(
        @"(?i)(?:\.|%2e|%252e){2}(?:/|\\|%2f|%5c|%252f|%255c|$)|%252e%252e",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    public WafCheckResult Check(WafCheckContext context)
    {
        if (!context.Options.EnablePathTraversalDetection)
        {
            return WafCheckResult.Passed;
        }

        // Check raw path (un-normalized — preserves ../ that ASP.NET might strip)
        if (Pattern.IsMatch(context.RawRequestPath))
        {
            return WafCheckResult.Blocked("PathTraversalBlocked", context.RawRequestPath);
        }

        // Check decoded query string
        if (!string.IsNullOrEmpty(context.DecodedQueryString) && Pattern.IsMatch(context.DecodedQueryString))
        {
            return WafCheckResult.Blocked("PathTraversalInQueryBlocked", context.DecodedQueryString);
        }

        return WafCheckResult.Passed;
    }
}
