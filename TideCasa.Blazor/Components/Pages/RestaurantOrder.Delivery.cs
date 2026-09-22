using Microsoft.JSInterop;

namespace TideCasa.Blazor.Components.Pages;

public partial class RestaurantOrder : IAsyncDisposable
{
    private DotNetObjectReference<RestaurantOrder>? deliveryReference;
    private int? deliveryPollId;
    private bool deliveryPolling, disposed;
    private int deliveryFailures;
    private string? deliveryRefreshError;
    private DateTimeOffset? deliveryCheckedAt;
    private bool ShouldPollDelivery => !disposed && receipt?.Delivery is not null
        && receipt.Status is not ("completed" or "cancelled" or "delivered") && pending is not null;

    private async Task SyncDeliveryPollingAsync()
    {
        try
        {
            if (ShouldPollDelivery && deliveryPollId is null)
            {
                deliveryReference ??= DotNetObjectReference.Create(this);
                deliveryPollId = await JS.InvokeAsync<int>("tideDeliveryStatus.start", deliveryReference);
            }
            else if (!ShouldPollDelivery && deliveryPollId is { } id)
            { deliveryPollId = null; await JS.InvokeVoidAsync("tideDeliveryStatus.stop", id); }
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException) { }
    }

    [JSInvokable]
    public async Task<int> PollDeliveryAsync()
    {
        if (!ShouldPollDelivery) return 0;
        if (busy || deliveryPolling) return 15000;
        deliveryPolling = true;
        var saved = pending!; var order = receipt!.OrderId;
        try
        {
            var response = await Api.TrackAsync(Slug, new(order, saved.TrackingKey));
            if (disposed || pending != saved || receipt?.OrderId != order) return 0;
            if (response.Succeeded && response.Value is { } current)
            { receipt = current; deliveryCheckedAt = DateTimeOffset.UtcNow; deliveryFailures = 0; deliveryRefreshError = null; }
            else
            { deliveryFailures++; deliveryRefreshError = "Updates are interrupted. Your last saved status is shown. Use Check order status or contact the restaurant."; }
            await InvokeAsync(StateHasChanged);
            return !ShouldPollDelivery ? 0 : deliveryFailures == 0 ? 15000 : deliveryFailures == 1 ? 30000 : 60000;
        }
        finally { deliveryPolling = false; }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        try { if (deliveryPollId is { } id) await JS.InvokeVoidAsync("tideDeliveryStatus.stop", id); }
        catch (Exception ex) when (ex is JSException or TaskCanceledException) { }
        deliveryReference?.Dispose();
    }
}
