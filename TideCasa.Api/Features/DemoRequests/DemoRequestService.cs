using System.ComponentModel.DataAnnotations;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.DemoRequests;

public sealed class DemoRequestService(DemoRequestStore store)
{
    public async Task<DemoSaveResult> SubmitAsync(DemoRequest request, CancellationToken cancellationToken)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), errors, true);
        if (request.Id == Guid.Empty) errors.Add(new("Please refresh the form before sending.", [nameof(request.Id)]));
        if (!string.IsNullOrWhiteSpace(request.ContactWebsite)) errors.Add(new("We could not accept this submission.", [nameof(request.ContactWebsite)]));
        var fields = new[] { request.Name, request.Business, request.Email, request.Phone, request.City, request.BusinessType, request.PreferredTimes, request.TimeZone, request.Goals };
        if (fields.Any(v => v is null || v.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t')))
            errors.Add(new("Please remove invalid characters from the form.", ["form"]));
        if (errors.Count > 0)
            return new(null, false, errors.SelectMany(e => e.MemberNames.DefaultIfEmpty("form").Select(n => (n, e.ErrorMessage ?? "Please check this field.")))
                .GroupBy(e => e.n).ToDictionary(g => g.Key, g => g.Select(e => e.Item2).ToArray()));
        var normalized = request with
        {
            Name = request.Name.Trim(), Business = request.Business.Trim(), Email = request.Email.Trim().ToLowerInvariant(),
            Phone = request.Phone.Trim(), City = request.City.Trim(), PreferredTimes = request.PreferredTimes.Trim(),
            TimeZone = request.TimeZone.Trim(), Goals = request.Goals.Trim(), ContactWebsite = ""
        };
        return await store.SaveAsync(normalized, cancellationToken);
    }
}

public sealed record DemoSaveResult(DemoRequestReceipt? Receipt, bool Created, Dictionary<string, string[]>? Errors = null, bool Conflict = false, bool Limited = false);
