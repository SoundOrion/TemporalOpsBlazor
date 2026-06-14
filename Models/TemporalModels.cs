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

    // Runs connected by Continue-As-New share the same WorkflowId and are shown as one logical workflow in the list.
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
    public WorkflowMotion Motion { get; set; } = new();
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

public sealed class WorkflowMotion
{
    public string WorkflowId { get; set; } = string.Empty;
    public string CurrentRunId { get; set; } = string.Empty;
    public WorkflowStatus OverallStatus { get; set; }
    public RiskLevel Risk { get; set; }
    public DateTimeOffset TimelineStart { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset TimelineEnd { get; set; } = DateTimeOffset.UtcNow;
    public decimal TotalDurationSeconds { get; set; }
    public int RunCount { get; set; }
    public int ChildWorkflowCount { get; set; }
    public int ActivityCount { get; set; }
    public int ProblemCount { get; set; }
    public string CurrentState { get; set; } = "Unknown";
    public string BusinessImpact { get; set; } = "No impact analysis available.";
    public string RecommendedAction { get; set; } = "Review the workflow history before taking operator action.";
    public IReadOnlyList<WorkflowMotionLane> Lanes { get; set; } = [];
    public IReadOnlyList<WorkflowMotionMarker> Markers { get; set; } = [];
    public IReadOnlyList<WorkflowMotionFinding> Findings { get; set; } = [];
}

public sealed class WorkflowMotionLane
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Kind { get; set; } = "default";
    public int DisplayLevel { get; set; } = 1;
    public IReadOnlyList<WorkflowMotionSegment> Segments { get; set; } = [];
    public IReadOnlyList<WorkflowMotionMarker> Markers { get; set; } = [];
}

public sealed class WorkflowMotionSegment
{
    public string Id { get; set; } = string.Empty;
    public string LaneId { get; set; } = string.Empty;
    public string Kind { get; set; } = "segment";
    public string Label { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public WorkflowStatus Status { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public long StartEventId { get; set; }
    public long EndEventId { get; set; }
    public string WorkflowId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public bool IsProblem { get; set; }
    public bool IsWaiting { get; set; }
    public int DisplayLevel { get; set; } = 1;
    public decimal OffsetPercent { get; set; }
    public decimal WidthPercent { get; set; }
}

public sealed class WorkflowMotionMarker
{
    public string Id { get; set; } = string.Empty;
    public string LaneId { get; set; } = string.Empty;
    public string Kind { get; set; } = "marker";
    public string Label { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public long EventId { get; set; }
    public bool IsProblem { get; set; }
    public int DisplayLevel { get; set; } = 2;
    public decimal OffsetPercent { get; set; }
}

public sealed class WorkflowMotionFinding
{
    public RiskLevel Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RecommendedAction { get; set; } = string.Empty;
    public long? EventId { get; set; }
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
