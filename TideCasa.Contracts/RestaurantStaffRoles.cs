namespace TideCasa.Contracts;

/// <summary>Tenant staff roles. None of these roles grants business ownership.</summary>
public static class RestaurantStaffRoles
{
    public static bool IsStaff(string? role) => role is "manager" or "bartender" or "server" or "kitchen" or "driver";
    public static string Label(string role) => role switch
    {
        "manager" => "General manager", "bartender" => "Bartender", "server" => "Server",
        "kitchen" => "Kitchen", "driver" => "Driver", _ => role
    };
}
