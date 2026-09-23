using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Components.Pages;

public partial class RestaurantOrder
{
    private sealed record ItemCustomization(IReadOnlyList<string>? RemovedIngredients, string? SpecialRequest);
    private readonly Dictionary<string, ItemCustomization> customizations = new(StringComparer.Ordinal);
    private RestaurantMenuItem? detailItem;
    private HashSet<string> draftRemoved = new(StringComparer.Ordinal);
    private string draftRequest = "";
    private int draftQuantity = 1;
    private bool showItemDialog;
    private string? detailError;
    private int DetailMaximum => detailItem is null ? 1 : Math.Min(20, 50 - CartItemCount + Quantity(detailItem.Id));
    private bool CanSaveItem => !Locked && detailItem is { Available: true, PriceCents: >= 0 }
        && draftQuantity >= 1 && draftQuantity <= DetailMaximum && (Quantity(detailItem.Id) > 0 || cart.Count < 20);

    private void OpenItem(RestaurantMenuItem item)
    {
        if (!connected) return;
        detailItem = item;
        var saved = customizations.GetValueOrDefault(item.Id);
        draftRemoved = new(saved?.RemovedIngredients ?? [], StringComparer.Ordinal);
        draftRequest = saved?.SpecialRequest ?? "";
        draftQuantity = Math.Max(1, Quantity(item.Id));
        detailError = null;
        showItemDialog = true;
    }

    private async Task CloseItemAsync()
    {
        await JS.InvokeVoidAsync("tideOrderingDetails.close", "ordering-item-dialog");
        detailItem = null;
    }

    private async Task ItemKeyAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Escape") await CloseItemAsync();
    }

    private void ToggleIngredient(string ingredient, ChangeEventArgs args)
    {
        if (Locked) return;
        if (args.Value is true) draftRemoved.Add(ingredient); else draftRemoved.Remove(ingredient);
    }

    private async Task SaveItemChoiceAsync()
    {
        if (!CanSaveItem || detailItem is null) return;
        if (draftRequest.Length > 240 || draftRequest.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        { detailError = "Keep your item request to 240 characters or fewer."; return; }
        var removed = (detailItem.Ingredients ?? []).Where(draftRemoved.Contains).ToArray();
        var request = draftRequest.Trim();
        cart[detailItem.Id] = draftQuantity;
        customizations[detailItem.Id] = new(removed.Length == 0 ? null : removed, request.Length == 0 ? null : request);
        await CloseItemAsync();
        await RefreshQuoteAsync();
    }

    private Task JumpCategoryAsync(string id) => JS.InvokeVoidAsync("tideOrderingDetails.jump", "category-" + id).AsTask();
    private string CategoryJumpLabel(string id) => IsSampleOrdering ? id switch
    {
        "share" => "Appetizers", "kitchen" => "Food", "bar" => "Drinks", "soft" => "Alcohol-free", _ => CategoryName(id)
    } : CategoryName(id);
}
