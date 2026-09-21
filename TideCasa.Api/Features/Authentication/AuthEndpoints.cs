using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Authentication;

public static class AuthEndpoints
{
    public static void AddTideCasaAuthentication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AuthStore>();
        services.AddScoped<AuthService>();
        services.AddHttpClient<SupabaseAuthClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = 65536;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
        services.AddAuthentication(RegisteredBearerHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, RegisteredBearerHandler>(RegisteredBearerHandler.SchemeName, _ => { });
        services.AddAuthorization();
    }

    private static string Address(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    public static void MapTideCasaAuthentication(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication").AddEndpointFilter<AuthRequestFilter>();
        group.MapPost("/signin", async (SignInRequest request, AuthService service, HttpContext context, CancellationToken ct) =>
            Results.Ok(await service.SignInAsync(request, Address(context), ct)));
        group.MapPost("/signup", async (SignUpRequest request, AuthService service, HttpContext context, CancellationToken ct) =>
            Results.Json(await service.SignUpAsync(request, Address(context), ct), statusCode: 202));
        group.MapPost("/verify", async (VerifyEmailRequest request, AuthService service, HttpContext context, CancellationToken ct) =>
            Results.Ok(await service.VerifyAsync(request, Address(context), ct)));
        group.MapPost("/forgot", async (EmailRequest request, AuthService service, HttpContext context, CancellationToken ct) =>
            Results.Json(await service.SendCodeAsync(request, "forgot", Address(context), ct), statusCode: 202));
        group.MapPost("/resend", async (EmailRequest request, AuthService service, HttpContext context, CancellationToken ct) =>
            Results.Json(await service.SendCodeAsync(request, "resend", Address(context), ct), statusCode: 202));
        group.MapPost("/reset", async (ResetPasswordRequest request, AuthService service, HttpContext context, CancellationToken ct) =>
            Results.Ok(await service.ResetAsync(request, Address(context), ct)));
        group.MapPost("/signout", async (AuthService service, HttpContext context, CancellationToken ct) =>
        {
            await service.SignOutAsync(RegisteredBearerHandler.ReadToken(context.Request), ct);
            return Results.NoContent();
        });
    }
}

public sealed class AuthRequestFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments.Where(a => a?.GetType().Namespace == typeof(SignInRequest).Namespace))
        {
            var errors = new List<ValidationResult>();
            Validator.TryValidateObject(argument!, new ValidationContext(argument!), errors, true);
            foreach (var property in argument!.GetType().GetProperties())
            {
                if (property.GetValue(argument) is string text && text.Any(char.IsControl))
                    errors.Add(new("Remove invalid control characters.", [property.Name]));
                if (property.Name == "Email" && property.GetValue(argument) is string email && !SupabaseAuthClient.ValidEmail(email.Trim()))
                    errors.Add(new("Enter a valid email address.", [property.Name]));
            }
            if (errors.Count > 0)
                return Results.ValidationProblem(errors.SelectMany(e => e.MemberNames.DefaultIfEmpty("form").Select(n => (n, message: e.ErrorMessage ?? "Check this field.")))
                    .GroupBy(e => e.n).ToDictionary(g => g.Key, g => g.Select(e => e.message).ToArray()));
        }
        try { return await next(context); }
        catch (AuthFailureException exception) { return Results.Problem(statusCode: exception.Status, title: exception.Message); }
    }
}
