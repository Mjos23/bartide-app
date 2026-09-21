using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Accounts;

public sealed class WorkspaceRegistrationStore(ApplicationDatabase database)
{
    public async Task<WorkspaceRegistration> RegisterAsync(AuthUser user, RegisterWorkspaceRequest request, CancellationToken ct)
    {
        if (request is null || !Guid.TryParseExact(request.RequestId, "D", out _) || !request.Acknowledged
            || request.Plan is not ("restaurant" or "business")) throw new AuthFailureException("Review the package and business type before continuing.", 400);
        var name = Field(request.BusinessName, "Business name", 120);
        var contact = Field(request.ContactName, "Your name", 120);
        var area = Field(request.Area, "Location", 120);
        var phone = Field(request.Phone, "Phone", 50, true);
        var website = Field(request.Website, "Website", 2048, true);
        var notes = Field(request.Notes, "Project notes", 2000, true, true);
        if (website.Length > 0 && (!Uri.TryCreate(website, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0))
            throw new AuthFailureException("Use an HTTPS website address.", 400);
        var vertical = request.Plan == "restaurant" ? "bartide" : "tide-casa";
        var id = request.RequestId.ToLowerInvariant();
        var stem = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (stem.Length == 0) stem = "business";
        var slug = stem[..Math.Min(40, stem.Length)].TrimEnd('-') + "-" + id;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var menu = JsonSerializer.Serialize(new
        {
            schema = "bartide-menu/1",
            venue = new { name, vertical, currency = "USD", area, website_url = website, tagline = "", hours_text = "", service_note = "" },
            categories = new[] { new { id = vertical == "bartide" ? "food" : "services", label = vertical == "bartide" ? "Food" : "Services" } },
            items = Array.Empty<object>()
        });
        try
        {
            await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
            // Preserve the existing one-workspace-per-account rule. Never rebind an existing owner by email.
            await using (var existing = db.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = db.Sql("SELECT id,slug,user_id FROM bartide_customers WHERE user_id=@user OR email=@email COLLATE NOCASE ORDER BY created_at LIMIT 2",
            "SELECT id,slug,user_id FROM bartide_customers WHERE user_id=@user OR translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\" ORDER BY created_at LIMIT 2");
                existing.Parameters.AddWithValue("@user", user.UserId); existing.Parameters.AddWithValue("@email", user.Email.ToLowerInvariant());
                await using var reader = await existing.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    if (reader.IsDBNull(2) || reader.GetString(2) != user.UserId) throw new AuthFailureException("This account needs a workspace review. Return to your account or contact Tide Casa.", 409);
                    var found = new WorkspaceRegistration(reader.GetString(0), reader.GetString(1), false);
                    if (await reader.ReadAsync(ct)) throw new AuthFailureException("This account has multiple workspaces. Choose one from your account.", 409);
                    return found;
                }
            }
            await using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO bartide_customers(id,slug,email,user_id,name,menu_json,version,status,enrollment_note,vertical,
                  requested_plan,contact_name,contact_phone,setup_notes,created_at,updated_at)
                VALUES(@id,@slug,@email,@user,@name,@menu,0,'draft','',@vertical,'enhanced',@contact,@phone,@notes,@now,@now)
                """;
            foreach (var (key, value) in new[] { ("@id", id), ("@slug", slug), ("@email", user.Email.ToLowerInvariant()), ("@user", user.UserId),
                ("@name", name), ("@menu", menu), ("@vertical", vertical), ("@contact", contact), ("@phone", phone), ("@notes", notes), ("@now", now) })
                insert.Parameters.AddWithValue(key, value);
            await insert.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
            return new(id, slug, true);
        }
        catch (DbException e) when (DatabaseExtensions.IsConstraintViolation(e)) { throw new AuthFailureException("This registration could not be confirmed. Return to your account before trying again.", 409); }
        catch (DbException) { throw new AuthFailureException("Your workspace could not be saved. Please try again.", 503); }
    }
    private static string Field(string? value, string label, int max, bool optional = false, bool multiline = false)
    {
        value ??= "";
        if (value.Length > max || !optional && string.IsNullOrWhiteSpace(value)
            || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')))
            throw new AuthFailureException($"{label}: enter {(optional ? "up to" : "1 to")} {max} characters.", 400);
        return value.Trim();
    }
}
