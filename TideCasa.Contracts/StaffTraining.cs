namespace TideCasa.Contracts;

public sealed record TeamMember(string Id, string Name, string? Email, string Role, bool Active, bool AccountLinked);
public sealed record TeamShift(string Id, string MemberId, string StartsAt, string EndsAt, string Label);
public sealed record TeamMessage(string Id, string Author, string Text, string CreatedAt);
public sealed record TrainingCourse(string Id, string Title, string Description, bool Published, int Version);
public sealed record TrainingLesson(string Id, string CourseId, string Title, string Description, string VideoKind, string VideoSource, int Position, int Version, string? UploadedVideoId = null);
public sealed record TrainingVideoOption(string Id, string Name);
public sealed record StaffCourseAssignment(string MemberId, string CourseId, bool Active, int Version);
public sealed record StaffLessonProgress(string MemberId, string LessonId, bool Completed, string UpdatedAt);
public sealed record StaffTrainingWorkspace(string TenantId, string Name, bool CanManage, string? MemberId,
    IReadOnlyList<TeamMember> Members, IReadOnlyList<TeamShift> Shifts, IReadOnlyList<TeamMessage> Messages,
    IReadOnlyList<TrainingCourse> Courses, IReadOnlyList<TrainingLesson> Lessons,
    IReadOnlyList<StaffCourseAssignment> Assignments, IReadOnlyList<StaffLessonProgress> Progress,
    IReadOnlyList<TrainingVideoOption>? UploadedVideos = null);
public sealed record AddTeamMemberRequest(string Name, string Email, string Role);
public sealed record SetTeamMemberStateRequest(bool ExpectedActive, bool Active);
public sealed record AddTeamShiftRequest(string MemberId, string StartsAt, string EndsAt, string Label);
public sealed record SendTeamMessageRequest(string RequestId, string Text);
public sealed record SaveTrainingCourseRequest(string Title, string Description, bool Published = false, int ExpectedVersion = 0);
public sealed record SaveTrainingLessonRequest(string CourseId, string Title, string Description, string VideoUrl, int Position, int ExpectedVersion = 0, string? UploadedVideoId = null);
public sealed record SetStaffCourseAssignmentRequest(string MemberId, bool Active, int ExpectedVersion = -1);
public sealed record SetStaffLessonProgressRequest(bool Completed);
