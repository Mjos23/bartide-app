namespace TideCasa.Api.Infrastructure;

/// <summary>A bounded exception to the default body limit, applied after authorization.</summary>
public sealed record ApiBodyLimit(long Bytes);
