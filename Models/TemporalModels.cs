namespace TemporalOpsBlazor.Models;

public enum WorkflowStatus
{
    Running,
    Completed,
    Failed,
    TimedOut,
    Cancelled,
    Terminated,
    ContinuedAsNew,
    Paused
}

public enum WorkerStatus
{
    Healthy,
    Degraded,
    Offline
}

public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public sealed class TemporalDashboardSnapshot
{
    public string Namespace { get; set; } = "default";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public int RunningWorkflows { get; set; }
    public int FailedWorkflows24h { get; set; }
    public int StuckWorkflows { get; set; }
    public int ActiveWorkers { get; set; }
    public decimal CompletionRate { get; set; }
    public decimal P95LatencySeconds { get; set; }
    public IReadOnlyList<WorkflowExecutionSummary> HotWorkflows { get; set; } = [];
    public IReadOnlyList<WorkerSummary> Workers { get; set; } = [];
    public IReadOnlyList<ScheduleSummary> Schedules { get; set; } = [];
    public IReadOnlyList<AuditRecord> RecentAudit { get; set; } = [];
    public IReadOnlyList<MetricPoint> Throughput { get; set; } = [];
}

public sealed class MetricPoint
{
    public string Label { get; set; } = string.Empty;
    public int Value { get; set; }
}

public sealed class WorkflowSearchQuery
{
    public string? Keyword { get; set; }
    public WorkflowStatus? Status { get; set; }
    public string? TaskQueue { get; set; }
    public RiskLevel? MinimumRisk { get; set; }
}

public sealed class WorkflowExecutionSummary
{
    public string WorkflowId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string WorkflowType { get; set; } = string.Empty;
    public WorkflowStatus Status { get; set; }
    public string TaskQueue { get; set; } = string.Empty;
    public string Namespace { get; set; } = "default";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public int Attempt { get; set; }
    public int PendingActivities { get; set; }
    public int HistoryLength { get; set; }
    public decimal LatencySeconds { get; set; }
    public RiskLevel Risk { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public string SearchAttributes { get; set; } = string.Empty;

    // Continue-As-New で同じ WorkflowId に連なる Run を、一覧では1つの論理Workflowとして扱う。
    public int ContinuationRunCount { get; set; } = 1;
    public IReadOnlyList<WorkflowRunSummary> ContinuationRuns { get; set; } = [];
    public bool HasContinuationChain => ContinuationRunCount > 1;
}

public sealed class WorkflowRunSummary
{
    public string WorkflowId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public WorkflowStatus Status { get; set; }
    public string TaskQueue { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public int HistoryLength { get; set; }
    public decimal LatencySeconds { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed class WorkflowDetail
{
    public WorkflowExecutionSummary Summary { get; set; } = new();
    public IReadOnlyList<WorkflowHistoryEvent> History { get; set; } = [];
    public IReadOnlyList<WorkflowRunSummary> ContinuationRuns { get; set; } = [];
    public IReadOnlyList<string> OpenSignals { get; set; } = [];
    public string InputJson { get; set; } = "{}";
    public string MemoJson { get; set; } = "{}";
}

public sealed class WorkflowHistoryEvent
{
    public long EventId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public bool IsProblem { get; set; }
}

public sealed class WorkerSummary
{
    public string WorkerId { get; set; } = string.Empty;
    public string TaskQueue { get; set; } = string.Empty;
    public WorkerStatus Status { get; set; }
    public int Pollers { get; set; }
    public int Backlog { get; set; }
    public int SlotsUsed { get; set; }
    public int SlotsCapacity { get; set; }
    public DateTimeOffset LastHeartbeat { get; set; }
    public string Version { get; set; } = string.Empty;
}

public sealed class ScheduleSummary
{
    public string ScheduleId { get; set; } = string.Empty;
    public string WorkflowType { get; set; } = string.Empty;
    public string Cron { get; set; } = string.Empty;
    public bool IsPaused { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    public string TaskQueue { get; set; } = string.Empty;
    public int OverlapSkipped24h { get; set; }
}

public sealed class AuditRecord
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Actor { get; set; } = "operator@example.com";
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool Succeeded { get; set; } = true;
}

public sealed class OperationResult
{
    public bool Succeeded { get; set; }
    public string Message { get; set; } = string.Empty;
    public AuditRecord? Audit { get; set; }

    public static OperationResult Ok(string message, AuditRecord audit) => new()
    {
        Succeeded = true,
        Message = message,
        Audit = audit
    };

    public static OperationResult Fail(string message) => new()
    {
        Succeeded = false,
        Message = message
    };
}
