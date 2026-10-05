using BlazorAzureInventory.Components.Resources;
using BlazorAzureInventory.Models.Tags;
using Radzen;

namespace BlazorAzureInventory.Services.Resources;

/// <summary>Opens the Resource Properties popup with standard options.</summary>
public sealed class ResourceDialogLauncher(DialogService dialogs)
{
    public const string TagsTab = "Tags";

    public Task OpenAsync(string resourceId, string? title = null, string? initialTab = null, ResourceTagDiff? preload = null) =>
        dialogs.OpenAsync<ResourcePropertiesDialog>(
            title ?? "Resource details",
            new Dictionary<string, object?>
            {
                [nameof(ResourcePropertiesDialog.ResourceId)] = resourceId,
                [nameof(ResourcePropertiesDialog.InitialTab)] = initialTab,
                [nameof(ResourcePropertiesDialog.Preload)] = preload
            },
            new DialogOptions
            {
                Width = "900px",
                Resizable = true,
                Draggable = true,
                CloseDialogOnEsc = true,
                CloseDialogOnOverlayClick = false,
                CssClass = "resource-dialog"
            });
}
