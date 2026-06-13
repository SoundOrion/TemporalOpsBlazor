using TemporalOpsBlazor.Models;

namespace TemporalOpsBlazor.Services;

public interface ITemporalOperationsService
{
    Task<TemporalDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowExecutionSummary>> SearchWorkflowsAsync(WorkflowSearchQuery query, CancellationToken cancellationToken = default);
    Task<WorkflowDetail?> GetWorkflowAsync(string workflowId, string? runId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkerSummary>> GetWorkersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduleSummary>> GetSchedulesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditRecord>> GetAuditLogAsync(CancellationToken cancellationToken = default);

    Task<OperationResult> RequestCancellationAsync(string workflowId, string runId, string reason, CancellationToken cancellationToken = default);
    Task<OperationResult> TerminateAsync(string workflowId, string runId, string reason, CancellationToken cancellationToken = default);
    Task<OperationResult> SendSignalAsync(string workflowId, string runId, string signalName, string payloadJson, string reason, CancellationToken cancellationToken = default);
    Task<OperationResult> ResetAsync(string workflowId, string runId, long eventId, string reason, CancellationToken cancellationToken = default);
    Task<OperationResult> PauseScheduleAsync(string scheduleId, string reason, CancellationToken cancellationToken = default);
    Task<OperationResult> UnpauseScheduleAsync(string scheduleId, string reason, CancellationToken cancellationToken = default);
}
