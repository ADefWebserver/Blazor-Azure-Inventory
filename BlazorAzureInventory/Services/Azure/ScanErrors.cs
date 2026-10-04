using Azure;
using BlazorAzureInventory.Models.Inventory;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace BlazorAzureInventory.Services.Azure;

/// <summary>Maps exceptions to scan issues. Messages contain status codes and error codes only, never payloads.</summary>
public static class ScanErrors
{
    public static (ScanIssueKind Kind, string Message) Classify(Exception ex, string? accessHint = null)
    {
        switch (ex)
        {
            case RequestFailedException { Status: 401 or 403 } rfe:
                return (ScanIssueKind.AccessDenied, accessHint is null ? $"{rfe.Status}: {rfe.ErrorCode ?? "Forbidden"}" : $"{rfe.Status}: {accessHint}");
            case RequestFailedException { Status: 404 } rfe:
                return (ScanIssueKind.NotFound, $"404: {rfe.ErrorCode ?? "NotFound"}");
            case RequestFailedException { Status: 429 } rfe:
                return (ScanIssueKind.Throttled, $"429: {rfe.ErrorCode ?? "Too many requests"}. Retries exhausted.");
            case RequestFailedException { Status: 408 or 504 } rfe:
                return (ScanIssueKind.Timeout, $"{rfe.Status}: {rfe.ErrorCode ?? "Timeout"}");
            case RequestFailedException rfe:
                return (ScanIssueKind.Unexpected, $"{rfe.Status}: {rfe.ErrorCode ?? "Request failed"}");
            case KuduException kudu:
                return (kudu.IssueKind, kudu.Message);
            case TimeoutException or TaskCanceledException:
                return (ScanIssueKind.Timeout, "The request timed out.");
        }

        if (IsConsentRequired(ex))
        {
            return (ScanIssueKind.ConsentRequired, "Additional consent, sign-in, or MFA is required for this tenant.");
        }

        return (ScanIssueKind.Unexpected, ex.GetType().Name);
    }

    public static bool IsConsentRequired(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is MicrosoftIdentityWebChallengeUserException or MsalUiRequiredException) return true;
        }
        return false;
    }
}
