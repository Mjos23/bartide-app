using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace TideCasa.Contracts;

public sealed record DemoRequest : IValidatableObject
{
    public Guid Id { get; init; }
    [Required, StringLength(120)] public string Name { get; init; } = "";
    [Required, StringLength(150)] public string Business { get; init; } = "";
    [Required, EmailAddress, StringLength(254)] public string Email { get; init; } = "";
    [StringLength(40)] public string Phone { get; init; } = "";
    [StringLength(120)] public string City { get; init; } = "";
    [Required, RegularExpression("^(tide-casa|bartide)$")] public string BusinessType { get; init; } = "tide-casa";
    [Required, StringLength(240)] public string PreferredTimes { get; init; } = "";
    [Required, StringLength(80)] public string TimeZone { get; init; } = "Eastern Time";
    [StringLength(1600)] public string Goals { get; init; } = "";
    [StringLength(200)] public string ContactWebsite { get; init; } = "";
    // Older payload hashes omit this property. Keep false/omitted requests byte-compatible.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool CanText { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CanText && !DemoTextPreference.IsUsablePhone(Phone))
            yield return new(DemoTextPreference.PhoneError, [nameof(Phone)]);
    }
}

public static class DemoTextPreference
{
    public const string PhoneError = "Enter a phone number we can text, or leave the texting option unchecked.";

    public static bool IsUsablePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var phone = value.Trim();
        var digits = 0;
        for (var index = 0; index < phone.Length; index++)
        {
            var character = phone[index];
            if (char.IsAsciiDigit(character)) digits++;
            else if (character is ' ' or '(' or ')' or '-' or '.') continue;
            else if (character != '+' || index != 0) return false;
        }
        return digits is >= 7 and <= 15;
    }
}

public sealed record DemoRequestReceipt(Guid Id, string Status, string Message);
