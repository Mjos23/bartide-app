using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Components.Pages;

public partial class RestaurantOrder
{
    [Parameter] public string Slug { get; set; } = "";
    [SupplyParameterFromQuery(Name = "table")] public string? TableToken { get; set; }
    [SupplyParameterFromQuery(Name = "payment_return")] public string? PaymentReturn { get; set; }
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private IWebHostEnvironment HostEnvironment { get; set; } = default!;
    private bool IsSampleOrdering => Slug == "gulf-lantern" && (PublicDemoWeb.Enabled(Configuration)
        || HostEnvironment.IsDevelopment() && Configuration["SampleBar:Enabled"] == "true");
    private sealed record SavedCheckout(int Version, string Slug, RestaurantOrderRequest Request, string? OrderId);
    private RestaurantPhoneCheckout? checkout;
    private RestaurantMenu? menu;
    private RestaurantQuote? quote;
    private RestaurantOrderReceipt? receipt;
    private RestaurantOrderRequest? pending;
    private readonly Dictionary<string, int> cart = new(StringComparer.Ordinal);
    private string fulfillment = "pickup", payment = "staff", tipChoice = "0", customTip = "", deliveryZip = "";
    private string customerName = "", phone = "", address = "", note = "", tableLabel = "";
    private string? error, loadedSlug, loadedTable;
    private bool loading, quoting, busy, uncertain, reviewing, connected;
    private bool savedToAccount;
    private async Task SaveToAccountAsync(string orderId)
    {
        if (pending is null) return;
        try { savedToAccount = await JS.InvokeAsync<bool>("tideCustomerOrders.save", Slug, orderId, pending.TrackingKey); }
        catch (JSException) { savedToAccount = false; }
    }
    private int quoteRevision, menuRevision;
    private bool Locked => !connected || busy || uncertain;
    private bool CanReview => !Locked && !quoting && quote is { CanSubmit: true } && menu?.Checkout.AcceptingOrders == true;
    private int CartItemCount => cart.Values.Sum();
    private int CartAmountCents => quote?.TotalCents ?? menu?.Items.Sum(item => (item.PriceCents ?? 0) * Quantity(item.Id)) ?? 0;
    private string CartAmountLabel => quote is null ? "Items subtotal" : "Total";
    private string SectionLink(string id) => Navigation.Uri.Split('#')[0] + "#" + id;
    private async Task GoToCheckoutAsync() => await JS.InvokeVoidAsync("tideOrderingCheckout.open");
    private static readonly (string Value, string Label)[] TipChoices = [("0", "No tip"), ("15", "15%"), ("20", "20%"), ("25", "25%"), ("custom", "Custom")];
    private IEnumerable<IGrouping<string, RestaurantMenuItem>> MenuGroups => menu?.Items.GroupBy(item => item.CategoryId) ?? Enumerable.Empty<IGrouping<string, RestaurantMenuItem>>();
    private string ReceiptStatus => receipt?.Status switch { "awaiting_payment" => "Complete payment before the restaurant can accept this order.", "paid_needs_review" or "payment_review" => "The restaurant is reviewing this payment. Please contact staff before ordering again.", "new" => "Awaiting the restaurant’s acceptance.", "accepted" => "The restaurant has accepted your order.", "preparing" => "Your order is being prepared.", "ready" => "Your order is ready.", "out_for_delivery" => "Your order is out for delivery.", "completed" => "Your order is complete.", "delivered" => "Your delivery has arrived. Payment is still outstanding.", "cancelled" or "canceled" => "Your order was cancelled.", _ => "Current order status: " + receipt?.Status };
    private string ReceiptPayment => receipt?.PaymentStatus switch { "paid_in_person" => "Paid to staff", "paid" => "Paid", "refunded" => "Refunded", "pending" => "Awaiting card payment", "partially_refunded" => "Partially refunded", "refund_pending" => "Refund in progress", _ => "Unpaid" };

    protected override Task OnParametersSetAsync()
    {
        if (loadedSlug == Slug && loadedTable == TableToken) return Task.CompletedTask;
        return LoadMenuAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (showItemDialog)
        {
            showItemDialog = false;
            await JS.InvokeVoidAsync("tideOrderingDetails.open", "ordering-item-dialog");
        }
        await SyncDeliveryPollingAsync();
        if (!firstRender) return;
        connected = true;
        try
        {
            var saved = await JS.InvokeAsync<SavedCheckout?>("tideMerchantCheckout.read", Slug);
            if (saved is { Version: 1 } && saved.Slug == Slug && saved.Request?.Order?.PaymentMethod is "phone" or "staff"
                && saved.Request.TrackingKey is { Length: 64 } tracking && tracking.All(char.IsAsciiHexDigit) && Guid.TryParse(saved.Request.RequestKey, out _))
            {
                pending = saved.Request;
                if (saved.OrderId is not null)
                {
                    if (pending.Order.PaymentMethod == "staff")
                    {
                        var tracked = await Api.TrackAsync(Slug, new(saved.OrderId, pending.TrackingKey));
                        if (tracked.Succeeded) receipt = tracked.Value;
                        else { uncertain = true; error = "Your saved order could not be checked. Retry safely or ask the restaurant."; }
                    }
                    else
                    {
                    var response = await Api.TrackCheckoutAsync(Slug, new(saved.OrderId, pending.TrackingKey));
                    if (response.Succeeded && response.Value is { } paymentValue) ApplyCheckout(paymentValue);
                    else { uncertain = true; error = "Your payment result is not confirmed yet. Retry this saved order safely."; }
                    }
                }
                else { uncertain = true; error = "You have a saved order request. Retry it to confirm the result safely."; }
            }
            else if (PaymentReturn is not null) error = "This browser tab no longer has your private receipt. Ask the restaurant for help; do not pay again until your payment is checked.";
        }
        catch (Exception error) when (error is JSException or System.Text.Json.JsonException)
        { if (PaymentReturn is not null) this.error = "Your saved payment receipt could not be opened. Ask the restaurant to check your payment before ordering again."; }
        StateHasChanged();
    }

    private void ApplyCheckout(RestaurantPhoneCheckout value)
    { checkout = value; receipt = value.Receipt; uncertain = false; reviewing = false; }
    private async Task<bool> SaveCheckoutAsync(string? orderId)
    {
        try { return await JS.InvokeAsync<bool>("tideMerchantCheckout.save", Slug, pending, orderId); }
        catch (JSException) { return false; }
    }
    private void ResumePayment()
    {
        if (checkout?.CheckoutUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.IdnHost == "checkout.stripe.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0)
            Navigation.NavigateTo(url, forceLoad: true);
    }
    private async Task CancelPaymentAsync()
    {
        if (busy || receipt is null || pending is null) return;
        busy = true; error = null;
        var response = await Api.CancelCheckoutAsync(Slug, new(receipt.OrderId, pending.TrackingKey));
        busy = false;
        if (response.Succeeded && response.Value is { } paymentValue) ApplyCheckout(paymentValue);
        else error = "We could not confirm cancellation. Your order stays reserved until Stripe confirms its payment status.";
    }

    private async Task LoadMenuAsync()
    {
        var revision = ++menuRevision;
        ++quoteRevision;
        loadedSlug = Slug; loadedTable = TableToken;
        loading = true; error = null; menu = null; quote = null; receipt = null; pending = null;
        checkout = null; savedToAccount = false; cart.Clear(); customizations.Clear(); detailItem = null; reviewing = false; uncertain = false; busy = false; quoting = false;
        tipChoice = "0"; customTip = ""; payment = "staff"; deliveryZip = ""; address = ""; tableLabel = "";
        if (Slug.Length > 128 || (TableToken?.Length ?? 0) > 128)
        { loading = false; error = "This restaurant or table link could not be found."; return; }
        var result = await Api.GetMenuAsync(Slug, TableToken);
        if (revision != menuRevision) return;
        loading = false;
        if (!result.Succeeded || result.Value is not { } value)
        {
            error = (int)result.Status == 404 ? "This restaurant or table link is unavailable. Ask staff for a current QR code." : "The menu could not be loaded. Please try again shortly.";
            return;
        }
        menu = value;
        fulfillment = menu.Table is not null && menu.Checkout.DineInEnabled ? "dine-in" : menu.Checkout.PickupEnabled ? "pickup" : menu.Checkout.DeliveryEnabled ? "delivery" : menu.Checkout.DineInEnabled ? "dine-in" : "";
    }

    private int Quantity(string id) => cart.GetValueOrDefault(id);
    private string CategoryName(string id) => menu?.Categories.FirstOrDefault(category => category.Id == id)?.Name ?? "Menu";
    private string Money(int cents) => (cents / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US")) + (menu?.Currency.Equals("usd", StringComparison.OrdinalIgnoreCase) == false ? " " + menu.Currency.ToUpperInvariant() : "");
    private static string FulfillmentLabel(string value) => value switch { "dine-in" => "Dine in", "delivery" => "Delivery", _ => "Pickup" };

    private async Task ChangeQuantityAsync(string id, int delta)
    {
        if (Locked || menu?.Items.FirstOrDefault(item => item.Id == id) is not { Available: true, PriceCents: >= 0 }) return;
        if (delta > 0 && ((!cart.ContainsKey(id) && cart.Count >= 20) || cart.Values.Sum() >= 50)) return;
        var quantity = Math.Clamp(Quantity(id) + delta, 0, 20);
        if (quantity == 0) { cart.Remove(id); customizations.Remove(id); } else cart[id] = quantity;
        await RefreshQuoteAsync();
    }

    private async Task SetFulfillmentAsync(string value)
    {
        if (Locked || menu is null || (value == "dine-in" && !menu.Checkout.DineInEnabled)
            || (value == "pickup" && !menu.Checkout.PickupEnabled) || (value == "delivery" && !menu.Checkout.DeliveryEnabled)) return;
        fulfillment = value;
        if (value != "delivery") { deliveryZip = ""; address = ""; }
        await RefreshQuoteAsync();
    }
    private async Task SetPaymentAsync(string value)
    {
        if (Locked || menu is null || value is not ("staff" or "phone")
            || (value == "staff" && !menu.Checkout.PayStaffEnabled)
            || (value == "phone" && !menu.Checkout.PhonePaymentAvailable)) return;
        payment = value; await RefreshQuoteAsync();
    }
    private async Task SetTipAsync(string value) { if (Locked) return; tipChoice = value; await RefreshQuoteAsync(); }
    private async Task SetZipAsync(ChangeEventArgs args) { if (Locked) return; deliveryZip = args.Value?.ToString()?.Trim() ?? ""; await RefreshQuoteAsync(); }
    private async Task SetTableLabelAsync(ChangeEventArgs args) { if (Locked) return; tableLabel = args.Value?.ToString()?.Trim() ?? ""; await RefreshQuoteAsync(); }
    private async Task SetCustomTipAsync(ChangeEventArgs args) { if (Locked) return; customTip = args.Value?.ToString()?.Trim() ?? ""; await RefreshQuoteAsync(); }

    private RestaurantQuoteRequest? MakeQuoteRequest()
    {
        if (cart.Count == 0 || menu is null) return null;
        if (fulfillment.Length == 0) { error = "The restaurant has no available ordering method right now."; return null; }
        if (fulfillment == "dine-in" && menu.Table is null && string.IsNullOrWhiteSpace(tableLabel))
        { error = "Enter your table number or name before reviewing your order."; return null; }
        if (fulfillment == "delivery" && (deliveryZip.Length != 5 || !deliveryZip.All(char.IsAsciiDigit)))
        { error = "Enter a five-digit delivery ZIP code to confirm your total."; return null; }
        int? customCents = null;
        var percent = 0;
        if (menu.Checkout.TipsEnabled && tipChoice == "custom")
        {
            if (!decimal.TryParse(customTip, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount < 0 || amount > 500 || decimal.Truncate(amount * 100) != amount * 100)
            { error = "Enter a tip from $0.00 to $500.00 with no more than two decimal places."; return null; }
            customCents = (int)(amount * 100);
        }
        else if (menu.Checkout.TipsEnabled && tipChoice is "15" or "20" or "25") percent = int.Parse(tipChoice, CultureInfo.InvariantCulture);
        return new(cart.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new OrderItemSelection(pair.Key, pair.Value,
            customizations.GetValueOrDefault(pair.Key)?.RemovedIngredients, customizations.GetValueOrDefault(pair.Key)?.SpecialRequest)).ToArray(), fulfillment, payment,
            fulfillment == "dine-in" ? menu.Table?.Token : null, fulfillment == "delivery" ? deliveryZip : null, percent, customCents,
            fulfillment == "dine-in" && menu.Table is null ? tableLabel : null);
    }

    private async Task RefreshQuoteAsync()
    {
        var revision = ++quoteRevision;
        quote = null; reviewing = false; error = null; quoting = false;
        var request = MakeQuoteRequest();
        if (request is null) return;
        quoting = true;
        var result = await Api.QuoteAsync(Slug, request);
        if (revision != quoteRevision) return;
        quoting = false;
        if (result.Succeeded && result.Value is { } value) quote = value;
        else error = FriendlyError(result.Code, (int)result.Status);
    }

    private Task ReviewAsync()
    {
        if (!CanReview) return Task.CompletedTask;
        if (!ValidContact()) return Task.CompletedTask;
        error = null; reviewing = true;
        return Task.CompletedTask;
    }

    private void EditDetails() { if (!Locked) { reviewing = false; error = null; } }

    private bool ValidContact()
    {
        if (string.IsNullOrWhiteSpace(customerName) || customerName.Trim().Length > 80 || customerName.Any(char.IsControl))
        { error = "Enter your name before sending your order."; return false; }
        var trimmedPhone = phone.Trim();
        var digits = trimmedPhone.Count(char.IsAsciiDigit);
        var phoneBody = trimmedPhone.StartsWith('+') ? trimmedPhone[1..] : trimmedPhone;
        if ((fulfillment != "dine-in" && trimmedPhone.Length == 0) || (trimmedPhone.Length > 0 && (phone.Length > 30 || digits is < 7 or > 15
            || phoneBody.Any(character => !char.IsAsciiDigit(character) && character is not ('-' or '(' or ')' or ' ' or '.')))))
        { error = "Enter a phone number the restaurant can reach you on."; return false; }
        if (fulfillment == "delivery" && (string.IsNullOrWhiteSpace(address) || address.Length > 250 || address.Any(char.IsControl)))
        { error = "Enter a delivery address."; return false; }
        if (note.Length > 500 || note.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        { error = "Keep your order note to 500 characters or fewer."; return false; }
        return true;
    }

    private async Task SubmitAsync()
    {
        if (busy || receipt is not null) return;
        if (pending is null)
        {
            if (!CanReview || !reviewing || !ValidContact() || quote is null || MakeQuoteRequest() is not { } selection) return;
            pending = new(Guid.NewGuid().ToString("D"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), selection,
                quote.Fingerprint, customerName.Trim(), phone.Trim(), fulfillment == "delivery" ? address.Trim() : null, note.Trim());
        }
        // Keep the entire immutable request, including both keys, through an uncertain response.
        busy = true; error = null;
        if (pending.Order.PaymentMethod == "phone")
        {
            if (!await SaveCheckoutAsync(null))
            { busy = false; error = "Allow this tab to save your private receipt before paying on your phone."; return; }
            var phoneResult = await Api.CheckoutAsync(Slug, pending);
            busy = false;
            if (phoneResult.Succeeded && phoneResult.Value is { } phoneValue)
            {
                ApplyCheckout(phoneValue);
                if (!await SaveCheckoutAsync(phoneValue.Receipt.OrderId))
                { error = "Keep this page open. Your order is reserved, but its receipt could not be saved for return from payment."; return; }
                await SaveToAccountAsync(phoneValue.Receipt.OrderId);
                ResumePayment(); return;
            }
            if (phoneResult.Uncertain || phoneResult.Code is "request_conflict" or "merchant_busy" or "merchant_review" or "merchant_pending")
            { uncertain = true; error = "Your payment request could not be confirmed. Retry this same saved order safely."; return; }
            pending = null; uncertain = false; reviewing = false;
            try { await JS.InvokeVoidAsync("tideMerchantCheckout.clear", Slug); } catch (JSException) { }
            await RefreshQuoteAsync(); error = FriendlyError(phoneResult.Code, (int)phoneResult.Status); return;
        }
        if (!await SaveCheckoutAsync(null))
        { busy = false; if (!uncertain) pending = null; error = "Allow this tab to save your private receipt before placing the order."; return; }
        var result = await Api.OrderAsync(Slug, pending);
        busy = false;
        if (result.Succeeded && result.Value is { } value)
        { receipt = value; uncertain = false; reviewing = false; await SaveCheckoutAsync(value.OrderId); await SaveToAccountAsync(value.OrderId); return; }
        if (result.Uncertain || result.Code == "request_conflict")
        { uncertain = true; error = "We couldn’t confirm whether the restaurant received your order. Retry this same order to confirm it safely."; return; }
        pending = null; uncertain = false; reviewing = false;
        try { await JS.InvokeVoidAsync("tideMerchantCheckout.clear", Slug); } catch (JSException) { }
        if (result.Code == "stale_quote")
        {
            await RefreshQuoteAsync();
            error = "The menu or your total changed. Review the updated order before sending it again.";
            return;
        }
        quote = null;
        error = FriendlyError(result.Code, (int)result.Status);
    }

    private async Task RefreshReceiptAsync()
    {
        if (busy || deliveryPolling || receipt is null || pending is null) return;
        busy = true; error = null;
        if (pending.Order.PaymentMethod == "phone")
        {
            var response = await Api.TrackCheckoutAsync(Slug, new(receipt.OrderId, pending.TrackingKey));
            busy = false;
            if (response.Succeeded && response.Value is { } paymentValue) ApplyCheckout(paymentValue);
            else error = "We could not refresh your payment. Keep this receipt and try again shortly.";
            return;
        }
        var result = await Api.TrackAsync(Slug, new(receipt.OrderId, pending.TrackingKey));
        busy = false;
        if (result.Succeeded && result.Value is { } value) { receipt = value; deliveryCheckedAt = DateTimeOffset.UtcNow; deliveryRefreshError = null; deliveryFailures = 0; }
        else error = "We couldn’t refresh the order’s status. Your saved receipt is still shown. Please try again or ask staff.";
    }

    private async Task StartAnotherOrderAsync()
    {
        if (busy || receipt is null || checkout?.State is "reserved" or "creating" or "open" or "review") return;
        try { await JS.InvokeVoidAsync("tideMerchantCheckout.clear", Slug); } catch (JSException) { }
        customerName = ""; phone = ""; note = "";
        await LoadMenuAsync();
    }

    private static string FriendlyError(string? code, int status) => code switch
    {
        "invalid_customization" => "Review this item's listed ingredients and special request, then try again.",
        "table_required" => "Enter your table number or scan the QR code at your table.",
        "invalid_table" => "Use the table number or name shown at your table.",
        "table_unavailable" => "That table is unavailable. Check its number or ask a staff member.",
        "table_mismatch" => "The entered table does not match your QR code. Scan your table again or ask staff.",
        "client_context_unavailable" => "This ordering session needs to be refreshed. Reload the page before placing an order.",
        "merchant_busy" => "The saved payment is being checked. Try again shortly.",
        "stale_quote" => "Your total changed. Review the updated order before paying.",
        "phone_unavailable" => "Phone payments are not available yet. Choose Pay staff if the restaurant offers it.",
        "ordering_closed" => "The restaurant is not accepting new orders right now.",
        "queue_full" => "The restaurant’s order queue is full. Please try again later or speak to staff.",
        "delivery_full" => "Delivery is at capacity right now. Choose another available method or try later.",
        "invalid_order" => "Check item availability, your delivery ZIP and minimum, and the tip amount, then review your order again.",
        "not_found" => "This menu or table link is no longer available. Ask staff for a current link.",
        _ when status == 429 => "Please wait a moment before trying again.",
        _ => "Your order total could not be confirmed. Please try again or speak to staff."
    };
}
