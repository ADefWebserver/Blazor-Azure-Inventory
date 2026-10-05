using System.Text.RegularExpressions;
using Azure;
using BlazorAzureInventory.Models.Tags;
using BlazorAzureInventory.Services.Azure;

namespace BlazorAzureInventory.Services.Tags;

/// <summary>Maps ARM tag write failures to outcomes and user messages. Messages never contain tag values.</summary>
public static partial class TagErrorMapper
{
    [GeneratedRegex("\"policyAssignment\"\\s*:\\s*\\{\\s*\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyAssignmentName();

    [GeneratedRegex("policy assignment '([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyAssignmentQuoted();

    public static (TagWriteOutcome Outcome, string Message) Map(Exception ex)
    {
        switch (ex)
        {
            case RequestFailedException { Status: 403, ErrorCode: "RequestDisallowedByPolicy" } rfe:
                var policy = PolicyName(rfe.Message);
                return (TagWriteOutcome.PolicyDenied, policy is null ? "Blocked by Azure Policy." : $"Blocked by policy '{policy}'.");
            case RequestFailedException { Status: 401 or 403 }:
                return (TagWriteOutcome.Forbidden, "You need Tag Contributor (or Contributor) on this resource to edit tags.");
            case RequestFailedException { Status: 404 }:
                return (TagWriteOutcome.NotFound, "This resource no longer exists. Rescan to refresh.");
            case RequestFailedException { Status: 429 }:
                return (TagWriteOutcome.Throttled, "Azure is throttling requests. Try again in a minute.");
            case RequestFailedException { Status: 409, ErrorCode: "ScopeLocked" }:
                return (TagWriteOutcome.Failed, "The resource is locked (ReadOnly lock).");
            case RequestFailedException { Status: 400 } rfe:
                return (TagWriteOutcome.ValidationFailed, $"Azure rejected the tags ({rfe.ErrorCode ?? "BadRequest"}).");
            case RequestFailedException rfe:
                return (TagWriteOutcome.Failed, $"Azure returned an error ({rfe.Status}: {rfe.ErrorCode ?? "Request failed"}).");
            case TimeoutException or TaskCanceledException:
                return (TagWriteOutcome.Failed, "The request timed out.");
        }

        if (ScanErrors.IsConsentRequired(ex))
        {
            return (TagWriteOutcome.Failed, "Additional consent, sign-in, or MFA is required. Sign out and sign in again.");
        }
        return (TagWriteOutcome.Failed, $"The tag update failed ({ex.GetType().Name}).");
    }

    public static string? PolicyName(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = PolicyAssignmentName().Match(message);
        if (m.Success) return m.Groups[1].Value;
        m = PolicyAssignmentQuoted().Match(message);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Seconds from a Retry-After header, if present.</summary>
    public static TimeSpan? RetryAfter(RequestFailedException ex)
    {
        var raw = ex.GetRawResponse();
        if (raw is not null && raw.Headers.TryGetValue("Retry-After", out var value) && int.TryParse(value, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
        }
        return null;
    }
}
