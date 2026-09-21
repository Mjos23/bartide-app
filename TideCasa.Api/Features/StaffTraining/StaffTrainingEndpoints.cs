using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Features.Authentication;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.StaffTraining;

public static class StaffTrainingEndpoints
{
    public static void MapStaffTraining(this WebApplication app)
    {
        var team = app.MapGroup("/api/v1/tenants/{id}/team").WithTags("Staff and training")
            .RequireAuthorization().WithMetadata(new TideCasa.Api.Infrastructure.ApiBodyLimit(64 * 1024))
            .AddEndpointFilter<StaffTrainingFilter>();
        team.MapGet("", async (string id, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.WorkspaceAsync(id, User(context), ct)));
        team.MapPost("/members", async (string id, AddTeamMemberRequest request, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.AddMemberAsync(id, User(context), request, ct)));
        team.MapPost("/members/{memberId}", async (string id, string memberId, SetTeamMemberStateRequest request, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.SetMemberAsync(id, memberId, User(context), request, ct)));
        team.MapPost("/shifts", async (string id, AddTeamShiftRequest request, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.AddShiftAsync(id, User(context), request, ct)));
        team.MapDelete("/shifts/{shiftId}", async (string id, string shiftId, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.DeleteShiftAsync(id, shiftId, User(context), ct)));
        team.MapPost("/messages", async (string id, SendTeamMessageRequest request, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.MessageAsync(id, User(context), request, ct)));
        team.MapPost("/courses", async (string id, SaveTrainingCourseRequest request, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.SaveCourseAsync(id, null, User(context), request, ct)));
        team.MapPost("/courses/{courseId}", async (string id, string courseId, SaveTrainingCourseRequest request, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.SaveCourseAsync(id, courseId, User(context), request, ct)));
        team.MapPost("/lessons", async (string id, SaveTrainingLessonRequest request, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.SaveLessonAsync(id, null, User(context), request, ct)));
        team.MapPost("/lessons/{lessonId}", async (string id, string lessonId, SaveTrainingLessonRequest request, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.SaveLessonAsync(id, lessonId, User(context), request, ct)));
        team.MapDelete("/lessons/{lessonId}", async (string id, string lessonId, int expectedVersion, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.DeleteLessonAsync(id, lessonId, expectedVersion, User(context), ct)));
        team.MapPost("/courses/{courseId}/assignments", async (string id, string courseId, SetStaffCourseAssignmentRequest request, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.AssignAsync(id, courseId, User(context), request, ct)));
        team.MapPost("/lessons/{lessonId}/progress", async (string id, string lessonId, SetStaffLessonProgressRequest request, HttpContext context, StaffTrainingStore store, CancellationToken ct) =>
            Results.Ok(await store.ProgressAsync(id, lessonId, User(context), request, ct)));
    }
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}

public sealed class StaffTrainingFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (TeamException error)
        {
            return Results.Problem(statusCode: error.Status, title: error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = error.Code });
        }
        catch (Exception error) when (error is DbException or JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return Results.Problem(statusCode: 503, title: "The team workspace is temporarily unavailable. Please try again.",
                extensions: new Dictionary<string, object?> { ["code"] = "team_unavailable" });
        }
    }
}
