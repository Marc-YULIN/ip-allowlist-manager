namespace Allowlist.Core;

/// <summary>The desired behavior of an IP rule.</summary>
public enum EntryKind { Allow, Block }
/// <summary>The lifecycle of a local rule.</summary>
public enum EntryState { PendingAdd, Active, PendingRemove, Removed, Failed }
/// <summary>The mutation sent to the remote list.</summary>
public enum OperationAction { Add, Remove }
/// <summary>Uncertain operations require observation before another dispatch.</summary>
public enum OperationState { Pending, InFlight, Uncertain, Succeeded, Failed, DeadLetter }
/// <summary>The durable evidence recorded around a remote call.</summary>
public enum DispatchPhase { Dispatching, Accepted, Completed, Failed, Unknown }
/// <summary>The source of an audited change.</summary>
public enum ActorType { Admin, Integration, System }
/// <summary>The administrator's permitted level of access.</summary>
public enum AdminRole { Admin, Viewer }
/// <summary>The provider's asynchronous operation status.</summary>
public enum BulkState { Pending, Running, Completed, Failed }

public sealed class ManagedEnvironment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string AllowListId { get; set; } = "";
    public string AllowListName { get; set; } = "";
    public string BlockListId { get; set; } = "";
    public string BlockListName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class IpEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnvironmentId { get; set; }
    public string Cidr { get; set; } = "";
    public EntryKind Kind { get; set; }
    public string Description { get; set; } = "";
    public string Owner { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }
    public EntryState State { get; set; }
    public string? RemoteItemId { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class RemoteOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnvironmentId { get; set; }
    public string ListId { get; set; } = "";
    public Guid? EntryId { get; set; }
    public OperationAction Action { get; set; }
    public OperationState State { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? RemoteOperationId { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? RemoteItemId { get; set; }
}

public sealed class RemoteDispatchLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public int Attempt { get; set; }
    public DispatchPhase Phase { get; set; }
    public string Detail { get; set; } = "";
    public DateTime At { get; set; }
}

public sealed class DriftSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnvironmentId { get; set; }
    public EntryKind ListKind { get; set; }
    public DateTime TakenAt { get; set; }
    public int LocalCount { get; set; }
    public int RemoteCount { get; set; }
    public string MissingRemoteJson { get; set; } = "[]";
    public string ExtraRemoteJson { get; set; } = "[]";
    public bool AutoFixed { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Actor { get; set; } = "";
    public ActorType ActorType { get; set; }
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string TraceId { get; set; } = "";
    public DateTime At { get; set; }
}

public sealed class IntegrationClient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string Scopes { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public AdminRole Role { get; set; }
}

public sealed record Actor(string Name, ActorType Type, string TraceId);
public sealed class DomainException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
