namespace TideCasa.Contracts;

public static class CustomerOrderStatus
{
    public static string Label(string status) => status switch
    {
        "new" => "Sent · waiting for the restaurant", "accepted" => "Accepted by the restaurant",
        "preparing" => "Cooking · your order is being prepared", "ready" => "Ready for pickup or handoff",
        "out_for_delivery" => "On the way", "delivered" => "Delivered · payment still outstanding",
        "completed" => "Complete", "cancelled" or "canceled" => "Cancelled",
        "awaiting_payment" => "Waiting for your payment", "payment_review" or "paid_needs_review" => "Staff are reviewing payment",
        _ => "Check with the restaurant"
    };
    public static bool Finished(string status) => status is "completed" or "cancelled" or "canceled" or "delivered";
}
