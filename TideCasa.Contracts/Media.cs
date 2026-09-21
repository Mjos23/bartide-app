namespace TideCasa.Contracts;

public sealed record MediaFile(string Id, string Kind, string Name, string ContentType, long ByteSize, string Status, bool Referenced, string CreatedAt);
public sealed record MediaWorkspace(string TenantId, string Name, bool StorageReady, string StorageLabel, IReadOnlyList<MediaFile> Files);
public sealed record MediaUploadResult(MediaFile File);
