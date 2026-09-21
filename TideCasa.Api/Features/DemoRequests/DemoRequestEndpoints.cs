using TideCasa.Contracts;

namespace TideCasa.Api.Features.DemoRequests;

public static class DemoRequestEndpoints
{
    public static void MapDemoRequests(this WebApplication app)
    {
        app.MapPost("/api/v1/demo-requests", async (DemoRequest request, DemoRequestService service, CancellationToken cancellationToken) =>
        {
            var result = await service.SubmitAsync(request, cancellationToken);
            if (result.Errors is not null) return Results.ValidationProblem(result.Errors);
            if (result.Conflict) return Results.Problem(statusCode: 409, title: "This request was already submitted with different details. Refresh the form to start a new request.");
            if (result.Limited) return Results.Problem(statusCode: 429, title: "Please wait before sending another request, or email hello@tide.casa.");
            return Results.Json(result.Receipt, statusCode: result.Created ? 201 : 200);
        }).RequireRateLimiting("public-form").WithName("RequestDemo").WithTags("Demo requests")
          .Produces<DemoRequestReceipt>(201).Produces<DemoRequestReceipt>().ProducesValidationProblem().ProducesProblem(409).ProducesProblem(429);
    }
}
