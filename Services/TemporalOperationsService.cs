using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Api.TaskQueue.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Converters;
using TemporalOpsBlazor.Models;
using ApiWorkflowExecution = Temporalio.Api.Common.V1.WorkflowExecution;
using ClientWorkflowExecution = Temporalio.Client.WorkflowExecution;
using ClientWorkflowStatus = Temporalio.Api.Enums.V1.WorkflowExecutionStatus;
using OpsWorkflowStatus = TemporalOpsBlazor.Models.WorkflowStatus;
using OpsWorkerStatus = TemporalOpsBlazor.Models.WorkerStatus;
using TemporalTaskQueue = Temporalio.Api.TaskQueue.V1.TaskQueue;

namespace TemporalOpsBlazor.Services;

public sealed class TemporalOperationsService : ITemporalOperationsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TemporalClientProvider _clientProvider;
    private readonly TemporalConnectionSettings _settings;
    private readonly ILogger<TemporalOperationsService> _logger;
    private readonly ConcurrentQueue<AuditRecord> _auditLog = new();

    public TemporalOperationsService(
        TemporalClientProvider clientProvider,
        IOptions<TemporalConnectionSettings> options,
        ILogger<TemporalOperationsService> logger)
    {
        _clientProvider = clientProvider;
        _settings = options.Value;
        _logger = logger;
    }

    public async Task<TemporalDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var client = await _clientProvider.GetClientAsync();
        var now = DateTimeOffset.UtcNow;
        var stuckCutoff = now.AddMinutes(-Math.Max(1, _settings.StuckWorkflowMinutes));
        var since24h = now.AddHours(-24);

        var runningCount = await SafeCountAsync(client, "ExecutionStatus = \"Running\"", cancellationToken);
        var failed24h = await SafeCountAsync(
            client,
            $"(ExecutionStatus = \"Failed\" OR ExecutionStatus = \"TimedOut\") AND CloseTime >= {FormatVisibilityTime(since24h)}",
            cancellationToken);
        var stuckCount = await SafeCountAsync(
            client,
            $"ExecutionStatus = \"Running\" AND StartTime <= {FormatVisibilityTime(stuckCutoff)}",
            cancellationToken);
        var completed24h = await SafeCountAsync(
            client,
            $"ExecutionStatus = \"Completed\" AND CloseTime >= {FormatVisibilityTime(since24h)}",
            cancellationToken);

        var hotWorkflows = await SearchWorkflowsAsync(new WorkflowSearchQuery(), cancellationToken);
        var limitedHotWorkflows = hotWorkflows
            .OrderByDescending(w => w.Risk)
            .ThenByDescending(w => w.StartedAt)
            .Take(8)
            .ToList();

        var workers = await GetWorkersAsync(cancellationToken);
        var schedules = await GetSchedulesAsync(cancellationToken);
        var audit = await GetAuditLogAsync(cancellationToken);

        var closed = completed24h + failed24h;
        var completionRate = closed == 0 ? 0 : Math.Round((decimal)completed24h / closed * 100, 2);
        var p95 = CalculateP95Seconds(hotWorkflows.Where(w => w.ClosedAt is not null).Select(w => (double)w.LatencySeconds));

        return new TemporalDashboardSnapshot
        {
            Namespace = _settings.Namespace,
            UpdatedAt = now,
            RunningWorkflows = ClampToInt(runningCount),
            FailedWorkflows24h = ClampToInt(failed24h),
            StuckWorkflows = ClampToInt(stuckCount),
            ActiveWorkers = workers.Count(w => w.Status != OpsWorkerStatus.Offline),
            CompletionRate = completionRate,
            P95LatencySeconds = (decimal)p95,
            HotWorkflows = limitedHotWorkflows,
            Workers = workers,
            Schedules = schedules.Take(6).ToList(),
            RecentAudit = audit.Take(6).ToList(),
            Throughput = BuildThroughputFallback(hotWorkflows),
        };
    }

    public async Task<IReadOnlyList<WorkflowExecutionSummary>> SearchWorkflowsAsync(WorkflowSearchQuery query, CancellationToken cancellationToken = default)
    {
        var client = await _clientProvider.GetClientAsync();
        var visibilityQuery = BuildVisibilityQuery(query);
        var workflows = new List<WorkflowExecutionSummary>();

        await foreach (var workflow in client.ListWorkflowsAsync(
            visibilityQuery,
            new WorkflowListOptions
            {
                Limit = Math.Max(1, _settings.WorkflowPageSize),
                Rpc = Rpc(cancellationToken),
            }).WithCancellation(cancellationToken))
        {
            workflows.Add(await MapWorkflowAsync(workflow, client.Options.Namespace));
        }

        workflows = await GroupContinueAsNewRunsAsync(client, workflows, cancellationToken);

        if (query.MinimumRisk is not null)
        {
            workflows = workflows.Where(w => w.Risk >= query.MinimumRisk).ToList();
        }

        return workflows
            .OrderByDescending(LatestCurrentRunStart)
            .ThenByDescending(w => w.Status == OpsWorkflowStatus.Running)
            .ThenByDescending(w => w.Risk)
            .ToList();
    }

    public async Task<TemporalOpsBlazor.Models.WorkflowDetail?> GetWorkflowAsync(string workflowId, string? runId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return null;
        }

        try
        {
            var client = await _clientProvider.GetClientAsync();
            var handle = client.GetWorkflowHandle(workflowId, EmptyToNull(runId));
            var description = await handle.DescribeAsync(new WorkflowDescribeOptions { Rpc = Rpc(cancellationToken) });
            var summary = await MapWorkflowAsync(description, client.Options.Namespace);
            var describedRunId = summary.RunId;

            var rawHistory = new List<HistoryEvent>();
            var history = new List<WorkflowHistoryEvent>();
            string inputJson = "{}";
            var count = 0;

            await foreach (var ev in handle.FetchHistoryEventsAsync(
                new WorkflowHistoryEventFetchOptions { Rpc = Rpc(cancellationToken) }).WithCancellation(cancellationToken))
            {
                count++;
                if (count > Math.Max(1, _settings.HistoryEventLimit))
                {
                    break;
                }

                rawHistory.Add(ev);

                if (ev.EventType == EventType.WorkflowExecutionStarted && ev.WorkflowExecutionStartedEventAttributes?.Input is not null)
                {
                    inputJson = FormatMessage(ev.WorkflowExecutionStartedEventAttributes.Input);
                }

                history.Add(MapHistoryEvent(ev));
            }

            var continuationRuns = await LoadContinuationRunsAsync(client, workflowId, cancellationToken);
            if (continuationRuns.Count == 0)
            {
                continuationRuns = [ToRunSummary(summary, true)];
            }

            var groupedSummary = BuildContinuationGroup(summary, continuationRuns);
            var detailSummary = groupedSummary;
            var motionRuns = groupedSummary.ContinuationRuns;

            if (!string.IsNullOrWhiteSpace(runId))
            {
                var selectedRun = continuationRuns.FirstOrDefault(r => string.Equals(r.RunId, describedRunId, StringComparison.OrdinalIgnoreCase))
                    ?? ToRunSummary(summary, true);

                detailSummary = CloneForSingleRun(summary, selectedRun);
                motionRuns = detailSummary.ContinuationRuns;
            }

            var motion = await BuildWorkflowMotionAsync(
                client,
                detailSummary,
                motionRuns,
                rawHistory,
                describedRunId,
                cancellationToken);

            return new TemporalOpsBlazor.Models.WorkflowDetail
            {
                Summary = detailSummary,
                History = history,
                ContinuationRuns = groupedSummary.ContinuationRuns,
                Motion = motion,
                OpenSignals = GuessSignals(history),
                InputJson = inputJson,
                MemoJson = await FormatMemoAsync(description.Memo),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load workflow detail for {WorkflowId}/{RunId}", workflowId, runId);
            return null;
        }
    }

    public async Task<IReadOnlyList<WorkerSummary>> GetWorkersAsync(CancellationToken cancellationToken = default)
    {
        var client = await _clientProvider.GetClientAsync();
        var taskQueues = await DiscoverTaskQueuesAsync(client, cancellationToken);
        var workers = new List<WorkerSummary>();

        foreach (var taskQueue in taskQueues)
        {
            try
            {
                var response = await client.WorkflowService.DescribeTaskQueueAsync(
                    new DescribeTaskQueueRequest
                    {
                        Namespace = client.Options.Namespace,
                        TaskQueue = new TemporalTaskQueue { Name = taskQueue },
                        TaskQueueType = TaskQueueType.Workflow,
                        ReportStats = true,
                    },
                    Rpc(cancellationToken));

                var lastPoll = response.Pollers
                    .Select(p => ToDateTimeOffset(p.LastAccessTime?.ToDateTime()))
                    .Where(d => d != DateTimeOffset.MinValue)
                    .DefaultIfEmpty(DateTimeOffset.MinValue)
                    .Max();

                var stats = response.Stats;
                var backlog = stats?.ApproximateBacklogCount ?? 0;
                var dispatchRate = stats?.TasksDispatchRate ?? 0;
                var addRate = stats?.TasksAddRate ?? 0;
                var status = response.Pollers.Count == 0
                    ? OpsWorkerStatus.Offline
                    : backlog > 0 && dispatchRate <= 0
                        ? OpsWorkerStatus.Degraded
                        : OpsWorkerStatus.Healthy;

                workers.Add(new WorkerSummary
                {
                    WorkerId = response.Pollers.Count == 0 ? "no active poller" : string.Join(", ", response.Pollers.Select(p => p.Identity).Distinct().Take(3)),
                    TaskQueue = taskQueue,
                    Status = status,
                    Pollers = response.Pollers.Count,
                    Backlog = ClampToInt(backlog),
                    SlotsUsed = ClampToInt(dispatchRate),
                    SlotsCapacity = Math.Max(1, ClampToInt(addRate + dispatchRate + 1)),
                    LastHeartbeat = lastPoll == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow.AddDays(-1) : lastPoll,
                    Version = ExtractWorkerVersion(response.Pollers.FirstOrDefault()),
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to describe task queue {TaskQueue}", taskQueue);
                workers.Add(new WorkerSummary
                {
                    WorkerId = "describe failed",
                    TaskQueue = taskQueue,
                    Status = OpsWorkerStatus.Offline,
                    Pollers = 0,
                    Backlog = 0,
                    SlotsUsed = 0,
                    SlotsCapacity = 1,
                    LastHeartbeat = DateTimeOffset.UtcNow.AddDays(-1),
                    Version = "unknown",
                });
            }
        }

        return workers
            .OrderByDescending(w => w.Status)
            .ThenByDescending(w => w.Backlog)
            .ThenBy(w => w.TaskQueue)
            .ToList();
    }

    public async Task<IReadOnlyList<ScheduleSummary>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var client = await _clientProvider.GetClientAsync();
        var schedules = new List<ScheduleSummary>();

        await foreach (var schedule in client.ListSchedulesAsync(
            new ScheduleListOptions { Rpc = Rpc(cancellationToken) }).WithCancellation(cancellationToken))
        {
            schedules.Add(MapSchedule(schedule));
            if (schedules.Count >= 100)
            {
                break;
            }
        }

        return schedules
            .OrderBy(s => s.IsPaused)
            .ThenBy(s => s.NextRunAt)
            .ToList();
    }

    public Task<IReadOnlyList<AuditRecord>> GetAuditLogAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AuditRecord> items = _auditLog
            .Reverse()
            .Take(200)
            .ToList();

        return Task.FromResult(items);
    }

    public async Task<OperationResult> RequestCancellationAsync(string workflowId, string runId, string reason, CancellationToken cancellationToken = default)
    {
        return await RunAuditedWorkflowOperationAsync(
            "Cancel",
            workflowId,
            runId,
            RiskLevel.Medium,
            reason,
            async client =>
            {
                var handle = client.GetWorkflowHandle(workflowId, EmptyToNull(runId), EmptyToNull(runId));
                await handle.CancelAsync(new WorkflowCancelOptions { Rpc = Rpc(cancellationToken) });
                return "Cancellation request accepted.";
            });
    }

    public async Task<OperationResult> TerminateAsync(string workflowId, string runId, string reason, CancellationToken cancellationToken = default)
    {
        return await RunAuditedWorkflowOperationAsync(
            "Terminate",
            workflowId,
            runId,
            RiskLevel.Critical,
            reason,
            async client =>
            {
                var handle = client.GetWorkflowHandle(workflowId, EmptyToNull(runId), EmptyToNull(runId));
                await handle.TerminateAsync(reason, new WorkflowTerminateOptions
                {
                    Details = new object?[] { new { reason, operatedBy = _settings.Identity, at = DateTimeOffset.UtcNow } },
                    Rpc = Rpc(cancellationToken),
                });
                return "Workflow terminated.";
            });
    }

    public async Task<OperationResult> SendSignalAsync(string workflowId, string runId, string signalName, string payloadJson, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signalName))
        {
            return OperationResult.Fail("Enter a signal name.");
        }

        IReadOnlyCollection<object?> args;
        try
        {
            args = ParseSignalArgs(payloadJson);
        }
        catch (JsonException ex)
        {
            return OperationResult.Fail($"Invalid payload JSON: {ex.Message}");
        }

        return await RunAuditedWorkflowOperationAsync(
            $"Signal:{signalName}",
            workflowId,
            runId,
            RiskLevel.Low,
            reason,
            async client =>
            {
                var handle = client.GetWorkflowHandle(workflowId, EmptyToNull(runId));
                await handle.SignalAsync(signalName, args, new WorkflowSignalOptions { Rpc = Rpc(cancellationToken) });
                return "Signal accepted.";
            });
    }

    public async Task<OperationResult> ResetAsync(string workflowId, string runId, long eventId, string reason, CancellationToken cancellationToken = default)
    {
        if (eventId <= 0)
        {
            return OperationResult.Fail("Reset Event ID must be greater than or equal to 1.");
        }

        return await RunAuditedWorkflowOperationAsync(
            "Reset",
            workflowId,
            runId,
            RiskLevel.Critical,
            reason,
            async client =>
            {
                var response = await client.WorkflowService.ResetWorkflowExecutionAsync(
                    new ResetWorkflowExecutionRequest
                    {
                        Namespace = client.Options.Namespace,
                        WorkflowExecution = new ApiWorkflowExecution
                        {
                            WorkflowId = workflowId,
                            RunId = runId,
                        },
                        Reason = reason,
                        WorkflowTaskFinishEventId = eventId,
                        RequestId = Guid.NewGuid().ToString("N"),
                        Identity = _settings.Identity,
                    },
                    Rpc(cancellationToken));

                return $"Workflow reset accepted. New RunId: {response.RunId}";
            });
    }

    public async Task<OperationResult> PauseScheduleAsync(string scheduleId, string reason, CancellationToken cancellationToken = default)
    {
        return await RunAuditedScheduleOperationAsync(
            "PauseSchedule",
            scheduleId,
            RiskLevel.Medium,
            reason,
            async client =>
            {
                await client.GetScheduleHandle(scheduleId).PauseAsync(reason, Rpc(cancellationToken));
                return "Schedule paused.";
            });
    }

    public async Task<OperationResult> UnpauseScheduleAsync(string scheduleId, string reason, CancellationToken cancellationToken = default)
    {
        return await RunAuditedScheduleOperationAsync(
            "UnpauseSchedule",
            scheduleId,
            RiskLevel.Low,
            reason,
            async client =>
            {
                await client.GetScheduleHandle(scheduleId).UnpauseAsync(reason, Rpc(cancellationToken));
                return "Schedule unpaused.";
            });
    }

    private async Task<long> SafeCountAsync(TemporalClient client, string query, CancellationToken cancellationToken)
    {
        try
        {
            var count = await client.CountWorkflowsAsync(query, new WorkflowCountOptions { Rpc = Rpc(cancellationToken) });
            return count.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Workflow count failed for query: {Query}", query);
            return 0;
        }
    }

    private async Task<List<WorkflowExecutionSummary>> GroupContinueAsNewRunsAsync(
        TemporalClient client,
        IReadOnlyList<WorkflowExecutionSummary> workflows,
        CancellationToken cancellationToken)
    {
        if (workflows.Count == 0)
        {
            return [];
        }

        var grouped = new List<WorkflowExecutionSummary>();
        var seenWorkflowIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in workflows.OrderByDescending(w => w.StartedAt))
        {
            if (!seenWorkflowIds.Add(seed.WorkflowId))
            {
                continue;
            }

            var runs = await LoadContinuationRunsAsync(client, seed.WorkflowId, cancellationToken);
            if (runs.Count == 0)
            {
                runs = [ToRunSummary(seed, true)];
            }

            grouped.Add(BuildContinuationGroup(seed, runs));
        }

        return grouped;
    }

    private async Task<IReadOnlyList<WorkflowRunSummary>> LoadContinuationRunsAsync(
        TemporalClient client,
        string workflowId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return [];
        }

        var runs = new List<WorkflowRunSummary>();
        var query = $"WorkflowId = \"{EscapeVisibilityValue(workflowId)}\"";
        var limit = Math.Max(10, Math.Min(Math.Max(1, _settings.WorkflowPageSize), 250));

        try
        {
            await foreach (var workflow in client.ListWorkflowsAsync(
                query,
                new WorkflowListOptions
                {
                    Limit = limit,
                    Rpc = Rpc(cancellationToken),
                }).WithCancellation(cancellationToken))
            {
                var summary = await MapWorkflowAsync(workflow, client.Options.Namespace);
                runs.Add(ToRunSummary(summary, false));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to hydrate Continue-As-New chain for {WorkflowId}", workflowId);
        }

        var ordered = runs
            .GroupBy(r => r.RunId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.StartedAt).First())
            .OrderByDescending(r => r.StartedAt)
            .ToList();

        if (ordered.Count > 0)
        {
            var current = ChooseCurrentRun(ordered);
            foreach (var run in ordered)
            {
                run.IsCurrent = string.Equals(run.RunId, current.RunId, StringComparison.OrdinalIgnoreCase);
            }
        }

        return ordered;
    }

    private static WorkflowExecutionSummary BuildContinuationGroup(
        WorkflowExecutionSummary seed,
        IReadOnlyList<WorkflowRunSummary> runs)
    {
        var ordered = runs.Count == 0
            ? new List<WorkflowRunSummary> { ToRunSummary(seed, true) }
            : runs.OrderByDescending(r => r.StartedAt).ToList();

        var current = ChooseCurrentRun(ordered);
        foreach (var run in ordered)
        {
            run.IsCurrent = string.Equals(run.RunId, current.RunId, StringComparison.OrdinalIgnoreCase);
        }

        var groupStartedAt = ordered.Min(r => r.StartedAt);
        DateTimeOffset? groupClosedAt = ordered.Any(r => r.ClosedAt is null)
            ? (DateTimeOffset?)null
            : ordered.Max(r => r.ClosedAt);
        var groupLatency = groupClosedAt is null
            ? DateTimeOffset.UtcNow - groupStartedAt
            : groupClosedAt.Value - groupStartedAt;

        return new WorkflowExecutionSummary
        {
            WorkflowId = seed.WorkflowId,
            RunId = current.RunId,
            WorkflowType = seed.WorkflowType,
            Status = current.Status,
            TaskQueue = string.IsNullOrWhiteSpace(current.TaskQueue) ? seed.TaskQueue : current.TaskQueue,
            Namespace = seed.Namespace,
            StartedAt = groupStartedAt,
            ClosedAt = groupClosedAt,
            Attempt = ordered.Count,
            PendingActivities = seed.PendingActivities,
            HistoryLength = ordered.Sum(r => r.HistoryLength),
            LatencySeconds = (decimal)Math.Max(0, groupLatency.TotalSeconds),
            Risk = ordered.Select(r => CalculateRisk(r.Status, r.StartedAt, r.HistoryLength)).Append(seed.Risk).Max(),
            Owner = seed.Owner,
            Memo = seed.Memo,
            SearchAttributes = seed.SearchAttributes,
            ContinuationRunCount = ordered.Count,
            ContinuationRuns = ordered,
        };
    }


    private static DateTimeOffset LatestCurrentRunStart(WorkflowExecutionSummary workflow) =>
        workflow.ContinuationRuns.FirstOrDefault(r => r.IsCurrent)?.StartedAt
        ?? workflow.ContinuationRuns.OrderByDescending(r => r.StartedAt).FirstOrDefault()?.StartedAt
        ?? workflow.StartedAt;

    private static WorkflowRunSummary ChooseCurrentRun(IReadOnlyList<WorkflowRunSummary> runs)
    {
        return runs
            .OrderByDescending(r => r.Status == OpsWorkflowStatus.Running)
            .ThenByDescending(r => r.Status != OpsWorkflowStatus.ContinuedAsNew)
            .ThenByDescending(r => r.StartedAt)
            .First();
    }

    private static WorkflowRunSummary ToRunSummary(WorkflowExecutionSummary summary, bool isCurrent) => new()
    {
        WorkflowId = summary.WorkflowId,
        RunId = summary.RunId,
        Status = summary.Status,
        TaskQueue = summary.TaskQueue,
        StartedAt = summary.StartedAt,
        ClosedAt = summary.ClosedAt,
        HistoryLength = summary.HistoryLength,
        LatencySeconds = summary.LatencySeconds,
        IsCurrent = isCurrent,
    };

    private static WorkflowExecutionSummary CloneForSingleRun(WorkflowExecutionSummary source, WorkflowRunSummary selectedRun)
    {
        return new WorkflowExecutionSummary
        {
            WorkflowId = source.WorkflowId,
            RunId = selectedRun.RunId,
            WorkflowType = source.WorkflowType,
            Status = selectedRun.Status,
            TaskQueue = string.IsNullOrWhiteSpace(selectedRun.TaskQueue) ? source.TaskQueue : selectedRun.TaskQueue,
            Namespace = source.Namespace,
            StartedAt = selectedRun.StartedAt,
            ClosedAt = selectedRun.ClosedAt,
            Attempt = source.Attempt,
            PendingActivities = source.PendingActivities,
            HistoryLength = selectedRun.HistoryLength,
            LatencySeconds = selectedRun.LatencySeconds,
            Risk = source.Risk,
            Owner = source.Owner,
            Memo = source.Memo,
            SearchAttributes = source.SearchAttributes,
            ContinuationRunCount = 1,
            ContinuationRuns = [selectedRun],
        };
    }

    private async Task<WorkflowExecutionSummary> MapWorkflowAsync(ClientWorkflowExecution workflow, string ns)
    {
        var status = MapStatus(workflow.Status);
        var startedAt = ToDateTimeOffset(workflow.StartTime);
        DateTimeOffset? closedAt = workflow.CloseTime.HasValue ? ToDateTimeOffset(workflow.CloseTime.Value) : null;
        var latency = closedAt is null ? DateTimeOffset.UtcNow - startedAt : closedAt.Value - startedAt;
        var memoJson = await FormatMemoAsync(workflow.Memo);
        var searchAttrs = workflow.TypedSearchAttributes?.ToString() ?? string.Empty;

        var summary = new WorkflowExecutionSummary
        {
            WorkflowId = workflow.Id,
            RunId = workflow.RunId,
            WorkflowType = workflow.WorkflowType,
            Status = status,
            TaskQueue = workflow.TaskQueue,
            Namespace = ns,
            StartedAt = startedAt,
            ClosedAt = closedAt,
            Attempt = 1,
            PendingActivities = 0,
            HistoryLength = workflow.HistoryLength,
            LatencySeconds = (decimal)Math.Max(0, latency.TotalSeconds),
            Risk = CalculateRisk(status, startedAt, workflow.HistoryLength),
            Owner = ExtractOwner(searchAttrs, memoJson),
            Memo = memoJson,
            SearchAttributes = string.IsNullOrWhiteSpace(searchAttrs) ? "-" : searchAttrs,
            ContinuationRunCount = 1,
        };

        summary.ContinuationRuns = [ToRunSummary(summary, true)];
        return summary;
    }

    private WorkflowHistoryEvent MapHistoryEvent(HistoryEvent ev)
    {
        var eventType = ev.EventType.ToString();
        var details = eventType switch
        {
            nameof(EventType.WorkflowExecutionStarted) => FormatStartedEvent(ev),
            nameof(EventType.ActivityTaskFailed) => FormatMessage(ev.ActivityTaskFailedEventAttributes),
            nameof(EventType.ActivityTaskTimedOut) => FormatMessage(ev.ActivityTaskTimedOutEventAttributes),
            nameof(EventType.WorkflowExecutionFailed) => FormatMessage(ev.WorkflowExecutionFailedEventAttributes),
            nameof(EventType.WorkflowTaskFailed) => FormatMessage(ev.WorkflowTaskFailedEventAttributes),
            nameof(EventType.WorkflowExecutionSignaled) => FormatMessage(ev.WorkflowExecutionSignaledEventAttributes),
            nameof(EventType.WorkflowExecutionTerminated) => FormatMessage(ev.WorkflowExecutionTerminatedEventAttributes),
            _ => CompactEventDetails(ev),
        };

        return new WorkflowHistoryEvent
        {
            EventId = ev.EventId,
            Timestamp = ToDateTimeOffset(ev.EventTime?.ToDateTime()),
            EventType = eventType,
            Details = details,
            IsProblem = eventType.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("TimedOut", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("Terminated", StringComparison.OrdinalIgnoreCase),
        };
    }


    private async Task<WorkflowMotion> BuildWorkflowMotionAsync(
        TemporalClient client,
        WorkflowExecutionSummary summary,
        IReadOnlyList<WorkflowRunSummary> continuationRuns,
        IReadOnlyList<HistoryEvent> currentRunHistory,
        string? currentRunHistoryRunId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var runs = continuationRuns.Count > 0
            ? continuationRuns.OrderBy(r => r.StartedAt).ToList()
            : new List<WorkflowRunSummary> { ToRunSummary(summary, true) };

        if (runs.Count == 0)
        {
            runs = new List<WorkflowRunSummary> { ToRunSummary(summary, true) };
        }

        var runHistories = new Dictionary<string, IReadOnlyList<HistoryEvent>>(StringComparer.OrdinalIgnoreCase);
        var currentHistoryRunId = string.IsNullOrWhiteSpace(currentRunHistoryRunId) ? summary.RunId : currentRunHistoryRunId;
        if (currentRunHistory.Count > 0 && !string.IsNullOrWhiteSpace(currentHistoryRunId))
        {
            runHistories[currentHistoryRunId] = currentRunHistory;
        }

        foreach (var run in runs.Take(50))
        {
            if (string.IsNullOrWhiteSpace(run.RunId) || runHistories.ContainsKey(run.RunId))
            {
                continue;
            }

            runHistories[run.RunId] = await LoadHistoryEventsAsync(
                client,
                run.WorkflowId,
                run.RunId,
                Math.Max(1, _settings.HistoryEventLimit),
                cancellationToken);
        }

        var timelineStart = runs.Min(r => r.StartedAt);
        var timelineEnd = runs
            .Select(r => r.ClosedAt ?? now)
            .DefaultIfEmpty(now)
            .Max();

        if (timelineEnd <= timelineStart)
        {
            timelineEnd = timelineStart.AddSeconds(1);
        }

        var runSegments = runs.Select(run => new WorkflowMotionSegment
        {
            Id = $"run-{SafeId(run.RunId)}",
            LaneId = "run-chain",
            Kind = "run",
            Label = ShortId(run.RunId),
            Details = $"{run.Status} / {run.HistoryLength:N0} events / {run.LatencySeconds:N1}s",
            Status = run.Status,
            StartTime = run.StartedAt,
            EndTime = run.ClosedAt ?? now,
            WorkflowId = run.WorkflowId,
            RunId = run.RunId,
            IsCurrent = run.IsCurrent,
            IsProblem = IsProblemStatus(run.Status),
            IsWaiting = run.Status == OpsWorkflowStatus.Running,
            DisplayLevel = 1,
        }).ToList();

        var rootSegment = new WorkflowMotionSegment
        {
            Id = $"workflow-{SafeId(summary.WorkflowId)}",
            LaneId = "workflow",
            Kind = "workflow",
            Label = summary.WorkflowType,
            Details = $"{summary.WorkflowId} / {summary.Status}",
            Status = summary.Status,
            StartTime = timelineStart,
            EndTime = summary.ClosedAt ?? timelineEnd,
            WorkflowId = summary.WorkflowId,
            RunId = summary.RunId,
            IsCurrent = summary.Status == OpsWorkflowStatus.Running,
            IsProblem = IsProblemStatus(summary.Status),
            IsWaiting = summary.Status == OpsWorkflowStatus.Running,
            DisplayLevel = 1,
        };

        var childSegments = new List<WorkflowMotionSegment>();
        var activitySegments = new List<WorkflowMotionSegment>();
        var operationalMarkers = new List<WorkflowMotionMarker>();
        var allMarkers = new List<WorkflowMotionMarker>();

        foreach (var run in runs)
        {
            if (!runHistories.TryGetValue(run.RunId, out var events) || events.Count == 0)
            {
                continue;
            }

            childSegments.AddRange(ExtractChildWorkflowSegments(summary.WorkflowId, run.RunId, events, now));
            activitySegments.AddRange(ExtractActivitySegments(summary.WorkflowId, run.RunId, events, now));
            allMarkers.AddRange(ExtractWorkflowMarkers("run-chain", events));
            operationalMarkers.AddRange(ExtractOperationalMarkers(events));
        }

        var childLaneMarkers = allMarkers
            .Where(m => m.Kind.Contains("child", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var activityLaneMarkers = allMarkers
            .Where(m => m.Kind.Contains("activity", StringComparison.OrdinalIgnoreCase) || m.Kind.Contains("timer", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var runLaneMarkers = allMarkers
            .Where(m => !childLaneMarkers.Contains(m) && !activityLaneMarkers.Contains(m))
            .ToList();

        var lanes = new List<WorkflowMotionLane>
        {
            new()
            {
                Id = "workflow",
                Title = "Overall execution",
                Subtitle = "Business-level execution window across all related runs.",
                Kind = "workflow",
                DisplayLevel = 1,
                Segments = [rootSegment],
            },
            new()
            {
                Id = "run-chain",
                Title = "Continue-As-New run chain",
                Subtitle = "Same Workflow ID, ordered from the first run to the current run.",
                Kind = "run",
                DisplayLevel = 1,
                Segments = runSegments,
                Markers = runLaneMarkers,
            }
        };

        if (childSegments.Count > 0 || childLaneMarkers.Count > 0)
        {
            lanes.Add(new WorkflowMotionLane
            {
                Id = "children",
                Title = "Child workflows",
                Subtitle = "Downstream workflow executions started by the parent.",
                Kind = "child",
                DisplayLevel = 1,
                Segments = childSegments
                    .OrderBy(s => s.StartTime)
                    .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
                    .Take(120)
                    .ToList(),
                Markers = childLaneMarkers,
            });
        }

        if (activitySegments.Count > 0 || activityLaneMarkers.Count > 0)
        {
            lanes.Add(new WorkflowMotionLane
            {
                Id = "activities",
                Title = "Major activities",
                Subtitle = "Activities collapsed into operationally useful segments.",
                Kind = "activity",
                DisplayLevel = 2,
                Segments = activitySegments
                    .OrderBy(s => s.StartTime)
                    .ThenByDescending(s => s.IsProblem)
                    .Take(160)
                    .ToList(),
                Markers = activityLaneMarkers,
            });
        }

        if (operationalMarkers.Count > 0)
        {
            lanes.Add(new WorkflowMotionLane
            {
                Id = "operator-events",
                Title = "Signals, timers, and operator-relevant events",
                Subtitle = "Events that typically explain why the workflow moved or waited.",
                Kind = "marker",
                DisplayLevel = 3,
                Segments = [],
                Markers = operationalMarkers
                    .OrderBy(m => m.Timestamp)
                    .Take(180)
                    .ToList(),
            });
        }

        NormalizeMotionLayout(lanes, timelineStart, timelineEnd);

        var problemSegments = lanes.SelectMany(l => l.Segments).Where(s => s.IsProblem).ToList();
        var problemMarkers = lanes.SelectMany(l => l.Markers).Where(m => m.IsProblem).ToList();
        var findings = BuildMotionFindings(summary, runs, childSegments, activitySegments, problemMarkers, now);
        var recommendedAction = BuildRecommendedAction(summary, findings, childSegments, activitySegments, runs);

        return new WorkflowMotion
        {
            WorkflowId = summary.WorkflowId,
            CurrentRunId = summary.RunId,
            OverallStatus = summary.Status,
            Risk = summary.Risk,
            TimelineStart = timelineStart,
            TimelineEnd = timelineEnd,
            TotalDurationSeconds = (decimal)Math.Max(0, (timelineEnd - timelineStart).TotalSeconds),
            RunCount = runs.Count,
            ChildWorkflowCount = childSegments
                .Select(s => string.IsNullOrWhiteSpace(s.WorkflowId) ? s.Label : s.WorkflowId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            ActivityCount = activitySegments.Count,
            ProblemCount = problemSegments.Count + problemMarkers.Count,
            CurrentState = BuildCurrentState(summary, runs, activitySegments, childSegments, now),
            BusinessImpact = BuildBusinessImpact(summary, childSegments, activitySegments),
            RecommendedAction = recommendedAction,
            Lanes = lanes,
            Markers = lanes.SelectMany(l => l.Markers).OrderBy(m => m.Timestamp).ToList(),
            Findings = findings,
        };
    }

    private async Task<IReadOnlyList<HistoryEvent>> LoadHistoryEventsAsync(
        TemporalClient client,
        string workflowId,
        string? runId,
        int limit,
        CancellationToken cancellationToken)
    {
        var events = new List<HistoryEvent>();
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return events;
        }

        try
        {
            var handle = client.GetWorkflowHandle(workflowId, EmptyToNull(runId));
            await foreach (var ev in handle.FetchHistoryEventsAsync(
                new WorkflowHistoryEventFetchOptions { Rpc = Rpc(cancellationToken) }).WithCancellation(cancellationToken))
            {
                events.Add(ev);
                if (events.Count >= limit)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch history for motion view {WorkflowId}/{RunId}", workflowId, runId);
        }

        return events;
    }

    private static IReadOnlyList<WorkflowMotionSegment> ExtractActivitySegments(
        string workflowId,
        string runId,
        IReadOnlyList<HistoryEvent> events,
        DateTimeOffset now)
    {
        var activities = new Dictionary<long, ActivityMotionBuilder>();

        foreach (var ev in events.OrderBy(e => e.EventId))
        {
            var timestamp = ToDateTimeOffset(ev.EventTime?.ToDateTime());
            if (timestamp == DateTimeOffset.MinValue)
            {
                timestamp = now;
            }

            switch (ev.EventType)
            {
                case EventType.ActivityTaskScheduled:
                {
                    var attrs = ev.ActivityTaskScheduledEventAttributes;
                    var activityId = ReadString(attrs, "ActivityId");
                    var activityType = ReadString(attrs, "ActivityType", "Name");
                    activities[ev.EventId] = new ActivityMotionBuilder
                    {
                        ScheduledEventId = ev.EventId,
                        ActivityId = string.IsNullOrWhiteSpace(activityId) ? $"activity-{ev.EventId}" : activityId,
                        ActivityType = string.IsNullOrWhiteSpace(activityType) ? "Activity" : activityType,
                        ScheduledAt = timestamp,
                        StartEventId = ev.EventId,
                        Status = OpsWorkflowStatus.Running,
                    };
                    break;
                }
                case EventType.ActivityTaskStarted:
                {
                    var scheduledId = ReadLong(ev.ActivityTaskStartedEventAttributes, "ScheduledEventId");
                    if (activities.TryGetValue(scheduledId, out var activity))
                    {
                        activity.StartedAt ??= timestamp;
                    }
                    break;
                }
                case EventType.ActivityTaskCompleted:
                case EventType.ActivityTaskFailed:
                case EventType.ActivityTaskTimedOut:
                case EventType.ActivityTaskCanceled:
                {
                    var attrs = GetEventAttributes(ev);
                    var scheduledId = ReadLong(attrs, "ScheduledEventId");
                    if (activities.TryGetValue(scheduledId, out var activity))
                    {
                        activity.EndedAt = timestamp;
                        activity.EndEventId = ev.EventId;
                        activity.Status = ev.EventType switch
                        {
                            EventType.ActivityTaskCompleted => OpsWorkflowStatus.Completed,
                            EventType.ActivityTaskFailed => OpsWorkflowStatus.Failed,
                            EventType.ActivityTaskTimedOut => OpsWorkflowStatus.TimedOut,
                            EventType.ActivityTaskCanceled => OpsWorkflowStatus.Cancelled,
                            _ => OpsWorkflowStatus.Running,
                        };
                        activity.Details = CompactEventDetails(attrs);
                    }
                    break;
                }
            }
        }

        return activities.Values
            .Select(a => new WorkflowMotionSegment
            {
                Id = $"activity-{a.ScheduledEventId}",
                LaneId = "activities",
                Kind = "activity",
                Label = a.ActivityType,
                Details = string.IsNullOrWhiteSpace(a.Details)
                    ? $"Activity ID: {a.ActivityId}"
                    : $"Activity ID: {a.ActivityId} / {a.Details}",
                Status = a.Status,
                StartTime = a.StartedAt ?? a.ScheduledAt,
                EndTime = a.EndedAt ?? now,
                StartEventId = a.StartEventId,
                EndEventId = a.EndEventId,
                WorkflowId = workflowId,
                RunId = runId,
                IsCurrent = a.EndedAt is null,
                IsProblem = IsProblemStatus(a.Status),
                IsWaiting = a.StartedAt is null && a.EndedAt is null,
                DisplayLevel = 2,
            })
            .Where(s => s.EndTime >= s.StartTime)
            .ToList();
    }

    private static IReadOnlyList<WorkflowMotionSegment> ExtractChildWorkflowSegments(
        string parentWorkflowId,
        string parentRunId,
        IReadOnlyList<HistoryEvent> events,
        DateTimeOffset now)
    {
        var childrenByInitiatedId = new Dictionary<long, ChildWorkflowMotionBuilder>();
        var childrenByWorkflowId = new Dictionary<string, ChildWorkflowMotionBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in events.OrderBy(e => e.EventId))
        {
            var timestamp = ToDateTimeOffset(ev.EventTime?.ToDateTime());
            if (timestamp == DateTimeOffset.MinValue)
            {
                timestamp = now;
            }

            switch (ev.EventType)
            {
                case EventType.StartChildWorkflowExecutionInitiated:
                {
                    var attrs = ev.StartChildWorkflowExecutionInitiatedEventAttributes;
                    var childWorkflowId = ReadString(attrs, "WorkflowId");
                    var childType = ReadString(attrs, "WorkflowType", "Name");
                    var child = new ChildWorkflowMotionBuilder
                    {
                        InitiatedEventId = ev.EventId,
                        WorkflowId = childWorkflowId,
                        WorkflowType = string.IsNullOrWhiteSpace(childType) ? "Child workflow" : childType,
                        StartedAt = timestamp,
                        StartEventId = ev.EventId,
                        Status = OpsWorkflowStatus.Running,
                    };
                    childrenByInitiatedId[ev.EventId] = child;
                    if (!string.IsNullOrWhiteSpace(childWorkflowId))
                    {
                        childrenByWorkflowId[childWorkflowId] = child;
                    }
                    break;
                }
                case EventType.ChildWorkflowExecutionStarted:
                {
                    var attrs = ev.ChildWorkflowExecutionStartedEventAttributes;
                    var initiatedId = ReadLong(attrs, "InitiatedEventId");
                    var childWorkflowId = ReadString(attrs, "WorkflowExecution", "WorkflowId");
                    var childRunId = ReadString(attrs, "WorkflowExecution", "RunId");
                    var childType = ReadString(attrs, "WorkflowType", "Name");
                    var child = GetOrCreateChildBuilder(childrenByInitiatedId, childrenByWorkflowId, initiatedId, childWorkflowId);
                    child.WorkflowId = string.IsNullOrWhiteSpace(child.WorkflowId) ? childWorkflowId : child.WorkflowId;
                    child.RunId = childRunId;
                    child.WorkflowType = string.IsNullOrWhiteSpace(child.WorkflowType) ? childType : child.WorkflowType;
                    child.StartedAt ??= timestamp;
                    child.StartEventId = child.StartEventId == 0 ? ev.EventId : child.StartEventId;
                    child.Status = OpsWorkflowStatus.Running;
                    break;
                }
                case EventType.ChildWorkflowExecutionCompleted:
                case EventType.ChildWorkflowExecutionFailed:
                case EventType.ChildWorkflowExecutionTimedOut:
                case EventType.ChildWorkflowExecutionCanceled:
                case EventType.ChildWorkflowExecutionTerminated:
                {
                    var attrs = GetEventAttributes(ev);
                    var initiatedId = ReadLong(attrs, "InitiatedEventId");
                    var childWorkflowId = ReadString(attrs, "WorkflowExecution", "WorkflowId");
                    var childRunId = ReadString(attrs, "WorkflowExecution", "RunId");
                    var child = GetOrCreateChildBuilder(childrenByInitiatedId, childrenByWorkflowId, initiatedId, childWorkflowId);
                    child.WorkflowId = string.IsNullOrWhiteSpace(child.WorkflowId) ? childWorkflowId : child.WorkflowId;
                    child.RunId = string.IsNullOrWhiteSpace(child.RunId) ? childRunId : child.RunId;
                    child.EndedAt = timestamp;
                    child.EndEventId = ev.EventId;
                    child.Status = ev.EventType switch
                    {
                        EventType.ChildWorkflowExecutionCompleted => OpsWorkflowStatus.Completed,
                        EventType.ChildWorkflowExecutionFailed => OpsWorkflowStatus.Failed,
                        EventType.ChildWorkflowExecutionTimedOut => OpsWorkflowStatus.TimedOut,
                        EventType.ChildWorkflowExecutionCanceled => OpsWorkflowStatus.Cancelled,
                        EventType.ChildWorkflowExecutionTerminated => OpsWorkflowStatus.Terminated,
                        _ => OpsWorkflowStatus.Running,
                    };
                    child.Details = CompactEventDetails(attrs);
                    break;
                }
            }
        }

        return childrenByInitiatedId.Values
            .Concat(childrenByWorkflowId.Values)
            .GroupBy(c => !string.IsNullOrWhiteSpace(c.WorkflowId) ? c.WorkflowId : c.InitiatedEventId.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.StartedAt ?? DateTimeOffset.MinValue).First())
            .Select(c => new WorkflowMotionSegment
            {
                Id = $"child-{SafeId(c.WorkflowId)}-{c.InitiatedEventId}",
                LaneId = "children",
                Kind = "child",
                Label = string.IsNullOrWhiteSpace(c.WorkflowId) ? c.WorkflowType : c.WorkflowId,
                Details = string.IsNullOrWhiteSpace(c.Details)
                    ? $"{c.WorkflowType} / parent {parentWorkflowId}/{ShortId(parentRunId)}"
                    : $"{c.WorkflowType} / {c.Details}",
                Status = c.Status,
                StartTime = c.StartedAt ?? now,
                EndTime = c.EndedAt ?? now,
                StartEventId = c.StartEventId,
                EndEventId = c.EndEventId,
                WorkflowId = c.WorkflowId,
                RunId = c.RunId,
                IsCurrent = c.EndedAt is null,
                IsProblem = IsProblemStatus(c.Status),
                IsWaiting = c.EndedAt is null,
                DisplayLevel = 1,
            })
            .Where(s => s.EndTime >= s.StartTime)
            .ToList();
    }

    private static IReadOnlyList<WorkflowMotionMarker> ExtractWorkflowMarkers(string laneId, IReadOnlyList<HistoryEvent> events)
    {
        var markers = new List<WorkflowMotionMarker>();
        foreach (var ev in events)
        {
            var eventType = ev.EventType.ToString();
            if (!IsMotionMarkerEvent(eventType))
            {
                continue;
            }

            var timestamp = ToDateTimeOffset(ev.EventTime?.ToDateTime());
            if (timestamp == DateTimeOffset.MinValue)
            {
                continue;
            }

            var markerLane = laneId;
            if (eventType.Contains("ChildWorkflow", StringComparison.OrdinalIgnoreCase))
            {
                markerLane = "children";
            }
            else if (eventType.Contains("Activity", StringComparison.OrdinalIgnoreCase) || eventType.Contains("Timer", StringComparison.OrdinalIgnoreCase))
            {
                markerLane = "activities";
            }

            markers.Add(new WorkflowMotionMarker
            {
                Id = $"marker-{ev.EventId}",
                LaneId = markerLane,
                Kind = MarkerKind(eventType),
                Label = MotionEventLabel(eventType),
                Details = CompactEventDetails(GetEventAttributes(ev)),
                Timestamp = timestamp,
                EventId = ev.EventId,
                IsProblem = eventType.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                    || eventType.Contains("TimedOut", StringComparison.OrdinalIgnoreCase)
                    || eventType.Contains("Terminated", StringComparison.OrdinalIgnoreCase),
                DisplayLevel = eventType.Contains("WorkflowExecution", StringComparison.OrdinalIgnoreCase) ? 1 : 2,
            });
        }

        return markers;
    }

    private static IReadOnlyList<WorkflowMotionMarker> ExtractOperationalMarkers(IReadOnlyList<HistoryEvent> events)
    {
        return events
            .Where(ev => IsOperationalMarkerEvent(ev.EventType.ToString()))
            .Select(ev => new WorkflowMotionMarker
            {
                Id = $"operator-{ev.EventId}",
                LaneId = "operator-events",
                Kind = MarkerKind(ev.EventType.ToString()),
                Label = MotionEventLabel(ev.EventType.ToString()),
                Details = CompactEventDetails(GetEventAttributes(ev)),
                Timestamp = ToDateTimeOffset(ev.EventTime?.ToDateTime()),
                EventId = ev.EventId,
                IsProblem = false,
                DisplayLevel = 3,
            })
            .Where(m => m.Timestamp != DateTimeOffset.MinValue)
            .ToList();
    }

    private static void NormalizeMotionLayout(IReadOnlyList<WorkflowMotionLane> lanes, DateTimeOffset start, DateTimeOffset end)
    {
        var totalMs = Math.Max(1, (end - start).TotalMilliseconds);
        foreach (var segment in lanes.SelectMany(l => l.Segments))
        {
            var segmentStart = segment.StartTime < start ? start : segment.StartTime;
            var segmentEnd = segment.EndTime <= segment.StartTime ? segment.StartTime.AddMilliseconds(1) : segment.EndTime;
            segmentEnd = segmentEnd > end ? end : segmentEnd;
            segment.OffsetPercent = ClampPercent((decimal)((segmentStart - start).TotalMilliseconds / totalMs * 100));
            segment.WidthPercent = Math.Max(0.65m, ClampPercent((decimal)((segmentEnd - segmentStart).TotalMilliseconds / totalMs * 100)));
        }

        foreach (var marker in lanes.SelectMany(l => l.Markers))
        {
            marker.OffsetPercent = ClampPercent((decimal)((marker.Timestamp - start).TotalMilliseconds / totalMs * 100));
        }
    }

    private static IReadOnlyList<WorkflowMotionFinding> BuildMotionFindings(
        WorkflowExecutionSummary summary,
        IReadOnlyList<WorkflowRunSummary> runs,
        IReadOnlyList<WorkflowMotionSegment> childSegments,
        IReadOnlyList<WorkflowMotionSegment> activitySegments,
        IReadOnlyList<WorkflowMotionMarker> problemMarkers,
        DateTimeOffset now)
    {
        var findings = new List<WorkflowMotionFinding>();
        var failedChildren = childSegments.Where(s => s.IsProblem).ToList();
        var failedActivities = activitySegments.Where(s => s.IsProblem).ToList();
        var oldestRunning = runs
            .Where(r => r.Status == OpsWorkflowStatus.Running)
            .OrderBy(r => r.StartedAt)
            .FirstOrDefault();

        if (IsProblemStatus(summary.Status))
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.Critical,
                Title = "Workflow closed abnormally",
                Description = $"The current grouped workflow status is {summary.Status}.",
                RecommendedAction = "Open the first failing segment, confirm business impact, then decide between reset, cancellation, or manual recovery.",
            });
        }

        if (failedChildren.Count > 0)
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.High,
                Title = "Child workflow failure detected",
                Description = $"{failedChildren.Count} child workflow segment(s) are failed, timed out, cancelled, or terminated.",
                RecommendedAction = "Review the child workflow lane first; downstream failures usually define the real business impact.",
                EventId = failedChildren.First().EndEventId == 0 ? null : failedChildren.First().EndEventId,
            });
        }

        if (failedActivities.Count > 0)
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.High,
                Title = "Activity failure or timeout detected",
                Description = $"{failedActivities.Count} activity segment(s) require attention.",
                RecommendedAction = "Check worker health and external dependencies before retrying or resetting the workflow.",
                EventId = failedActivities.First().EndEventId == 0 ? null : failedActivities.First().EndEventId,
            });
        }

        if (oldestRunning is not null && now - oldestRunning.StartedAt > TimeSpan.FromMinutes(30))
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = summary.Risk >= RiskLevel.High ? RiskLevel.High : RiskLevel.Medium,
                Title = "Long-running execution",
                Description = $"The current run has been open for {(now - oldestRunning.StartedAt).TotalMinutes:N0} minutes.",
                RecommendedAction = "Verify this duration is expected for the business process. If not, check pending activities and worker backlog.",
            });
        }

        if (runs.Count >= 10)
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.Medium,
                Title = "Large Continue-As-New chain",
                Description = $"This workflow has {runs.Count} runs in the same Workflow ID chain.",
                RecommendedAction = "Confirm the chain is an expected compaction pattern, not an unintended loop.",
            });
        }

        if (problemMarkers.Count > 0 && findings.Count == 0)
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.Medium,
                Title = "Problem marker detected",
                Description = "One or more problem events appear in the history.",
                RecommendedAction = "Inspect the highlighted marker and compare it with the activity/child lanes.",
                EventId = problemMarkers.First().EventId,
            });
        }

        if (findings.Count == 0)
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.Low,
                Title = "No urgent anomaly detected",
                Description = "The grouped execution appears operationally healthy based on status, child workflows, and major activities.",
                RecommendedAction = "Continue monitoring. Use operator actions only when there is a business-approved reason.",
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static string BuildCurrentState(
        WorkflowExecutionSummary summary,
        IReadOnlyList<WorkflowRunSummary> runs,
        IReadOnlyList<WorkflowMotionSegment> activities,
        IReadOnlyList<WorkflowMotionSegment> children,
        DateTimeOffset now)
    {
        if (IsProblemStatus(summary.Status))
        {
            return $"Closed abnormally as {summary.Status}.";
        }

        var currentRun = runs.FirstOrDefault(r => r.IsCurrent) ?? runs.OrderByDescending(r => r.StartedAt).FirstOrDefault();
        var runningActivity = activities
            .Where(a => a.IsCurrent)
            .OrderByDescending(a => a.StartTime)
            .FirstOrDefault();
        var runningChild = children
            .Where(c => c.IsCurrent)
            .OrderByDescending(c => c.StartTime)
            .FirstOrDefault();

        if (runningChild is not null)
        {
            return $"Waiting on child workflow {runningChild.Label}.";
        }

        if (runningActivity is not null)
        {
            var minutes = Math.Max(0, (now - runningActivity.StartTime).TotalMinutes);
            return $"Running activity {runningActivity.Label} for {minutes:N0} minutes.";
        }

        if (currentRun is not null && currentRun.Status == OpsWorkflowStatus.Running)
        {
            return $"Current run {ShortId(currentRun.RunId)} is running.";
        }

        return $"Latest status is {summary.Status}.";
    }

    private static string BuildBusinessImpact(
        WorkflowExecutionSummary summary,
        IReadOnlyList<WorkflowMotionSegment> childSegments,
        IReadOnlyList<WorkflowMotionSegment> activitySegments)
    {
        if (summary.Risk >= RiskLevel.Critical)
        {
            return "Critical operational impact. Management attention and documented recovery decision are recommended.";
        }

        if (summary.Risk == RiskLevel.High)
        {
            return "Potential business impact. Review failed or delayed downstream work before taking action.";
        }

        if (childSegments.Any(s => s.IsProblem))
        {
            return "Downstream impact is possible because one or more child workflows ended abnormally.";
        }

        if (activitySegments.Any(s => s.IsProblem))
        {
            return "Limited impact may exist around a failing activity or external dependency.";
        }

        return "No immediate business impact is visible from the grouped execution.";
    }

    private static string BuildRecommendedAction(
        WorkflowExecutionSummary summary,
        IReadOnlyList<WorkflowMotionFinding> findings,
        IReadOnlyList<WorkflowMotionSegment> childSegments,
        IReadOnlyList<WorkflowMotionSegment> activitySegments,
        IReadOnlyList<WorkflowRunSummary> runs)
    {
        var top = findings.OrderByDescending(f => f.Severity).FirstOrDefault();
        if (top is not null && top.Severity >= RiskLevel.High)
        {
            return top.RecommendedAction;
        }

        if (summary.Status == OpsWorkflowStatus.Running && runs.Count > 1)
        {
            return "Confirm the current run is progressing. Continue-As-New history is normal only when each run advances business state.";
        }

        if (summary.Status == OpsWorkflowStatus.Running)
        {
            return "Monitor pending activities and worker backlog. Escalate only if the workflow stops making progress.";
        }

        return top?.RecommendedAction ?? "No immediate operator action is required.";
    }

    private static bool IsOperationalMarkerEvent(string eventType)
    {
        return eventType.Contains("Signaled", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Timer", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Update", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMotionMarkerEvent(string eventType)
    {
        if (eventType.Contains("WorkflowTask", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (eventType.Contains("ChildWorkflow", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("StartChildWorkflow", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (eventType.Contains("Activity", StringComparison.OrdinalIgnoreCase))
        {
            return eventType.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("TimedOut", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("Canceled", StringComparison.OrdinalIgnoreCase);
        }

        if (eventType.Contains("WorkflowExecution", StringComparison.OrdinalIgnoreCase))
        {
            return eventType.Contains("ContinuedAsNew", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("Completed", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("TimedOut", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("Terminated", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("Canceled", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("Signaled", StringComparison.OrdinalIgnoreCase);
        }

        return eventType.Contains("Timer", StringComparison.OrdinalIgnoreCase);
    }

    private static string MarkerKind(string eventType)
    {
        if (eventType.Contains("ContinuedAsNew", StringComparison.OrdinalIgnoreCase)) return "continue";
        if (eventType.Contains("ChildWorkflow", StringComparison.OrdinalIgnoreCase)) return "child";
        if (eventType.Contains("Activity", StringComparison.OrdinalIgnoreCase)) return "activity";
        if (eventType.Contains("Timer", StringComparison.OrdinalIgnoreCase)) return "timer";
        if (eventType.Contains("Signal", StringComparison.OrdinalIgnoreCase)) return "signal";
        if (eventType.Contains("Failed", StringComparison.OrdinalIgnoreCase) || eventType.Contains("TimedOut", StringComparison.OrdinalIgnoreCase)) return "problem";
        return "workflow";
    }

    private static string MotionEventLabel(string eventType)
    {
        return eventType
            .Replace("StartChildWorkflowExecution", "Child ", StringComparison.OrdinalIgnoreCase)
            .Replace("ChildWorkflowExecution", "Child ", StringComparison.OrdinalIgnoreCase)
            .Replace("WorkflowExecution", "Workflow ", StringComparison.OrdinalIgnoreCase)
            .Replace("ActivityTask", "Activity ", StringComparison.OrdinalIgnoreCase)
            .Replace("ContinuedAsNew", "Continued As New", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static ChildWorkflowMotionBuilder GetOrCreateChildBuilder(
        Dictionary<long, ChildWorkflowMotionBuilder> byInitiatedId,
        Dictionary<string, ChildWorkflowMotionBuilder> byWorkflowId,
        long initiatedId,
        string workflowId)
    {
        if (initiatedId > 0 && byInitiatedId.TryGetValue(initiatedId, out var byEvent))
        {
            return byEvent;
        }

        if (!string.IsNullOrWhiteSpace(workflowId) && byWorkflowId.TryGetValue(workflowId, out var byWorkflow))
        {
            if (initiatedId > 0)
            {
                byInitiatedId[initiatedId] = byWorkflow;
            }
            return byWorkflow;
        }

        var child = new ChildWorkflowMotionBuilder
        {
            InitiatedEventId = initiatedId,
            WorkflowId = workflowId,
            WorkflowType = "Child workflow",
            Status = OpsWorkflowStatus.Running,
        };

        if (initiatedId > 0)
        {
            byInitiatedId[initiatedId] = child;
        }

        if (!string.IsNullOrWhiteSpace(workflowId))
        {
            byWorkflowId[workflowId] = child;
        }

        return child;
    }

    private static object? GetEventAttributes(HistoryEvent ev)
    {
        var eventType = ev.EventType.ToString();
        var propertyName = eventType + "EventAttributes";
        var prop = ev.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop?.GetValue(ev);
    }

    private static string ReadString(object? value, params string[] path)
    {
        var result = ReadNested(value, path);
        return result?.ToString() ?? string.Empty;
    }

    private static long ReadLong(object? value, params string[] path)
    {
        var result = ReadNested(value, path);
        return result switch
        {
            long l => l,
            int i => i,
            uint ui => ui,
            ulong ul when ul <= long.MaxValue => (long)ul,
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static object? ReadNested(object? value, params string[] path)
    {
        var current = value;
        foreach (var name in path)
        {
            if (current is null)
            {
                return null;
            }

            var prop = current.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            current = prop?.GetValue(current);
        }

        return current;
    }

    private static bool IsProblemStatus(OpsWorkflowStatus status) => status is
        OpsWorkflowStatus.Failed or
        OpsWorkflowStatus.TimedOut or
        OpsWorkflowStatus.Terminated or
        OpsWorkflowStatus.Cancelled;

    private static decimal ClampPercent(decimal value) => Math.Clamp(value, 0m, 100m);

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Length <= 8 ? value : value[..8];
    }

    private static string SafeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Guid.NewGuid().ToString("N")[..8];
        }

        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private sealed class ActivityMotionBuilder
    {
        public long ScheduledEventId { get; set; }
        public string ActivityId { get; set; } = string.Empty;
        public string ActivityType { get; set; } = string.Empty;
        public DateTimeOffset ScheduledAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public long StartEventId { get; set; }
        public long EndEventId { get; set; }
        public OpsWorkflowStatus Status { get; set; } = OpsWorkflowStatus.Running;
        public string Details { get; set; } = string.Empty;
    }

    private sealed class ChildWorkflowMotionBuilder
    {
        public long InitiatedEventId { get; set; }
        public string WorkflowId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string WorkflowType { get; set; } = string.Empty;
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public long StartEventId { get; set; }
        public long EndEventId { get; set; }
        public OpsWorkflowStatus Status { get; set; } = OpsWorkflowStatus.Running;
        public string Details { get; set; } = string.Empty;
    }

    private async Task<IReadOnlyList<string>> DiscoverTaskQueuesAsync(TemporalClient client, CancellationToken cancellationToken)
    {
        var taskQueues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var taskQueue in _settings.MonitoredTaskQueues.Where(q => !string.IsNullOrWhiteSpace(q)))
        {
            taskQueues.Add(taskQueue.Trim());
        }

        await foreach (var workflow in client.ListWorkflowsAsync(
            "ExecutionStatus = \"Running\"",
            new WorkflowListOptions { Limit = Math.Min(100, Math.Max(10, _settings.DashboardPageSize)), Rpc = Rpc(cancellationToken) })
            .WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(workflow.TaskQueue))
            {
                taskQueues.Add(workflow.TaskQueue);
            }
        }

        return taskQueues.Count == 0 ? ["default"] : taskQueues.OrderBy(q => q).ToList();
    }

    private ScheduleSummary MapSchedule(ScheduleListDescription schedule)
    {
        var action = schedule.Schedule?.Action;
        var startWorkflow = action as ScheduleListActionStartWorkflow;
        var next = schedule.Info?.NextActionTimes.FirstOrDefault();
        var state = schedule.Schedule?.State;

        return new ScheduleSummary
        {
            ScheduleId = schedule.Id,
            WorkflowType = startWorkflow?.Workflow ?? action?.GetType().Name ?? "unknown",
            Cron = schedule.Schedule?.Spec?.ToString() ?? "schedule spec",
            IsPaused = state?.Paused ?? false,
            NextRunAt = next.HasValue ? ToDateTimeOffset(next.Value) : DateTimeOffset.MinValue,
            TaskQueue = "-",
            OverlapSkipped24h = schedule.Info?.RecentActions.Count(a => a.GetType().Name.Contains("Skip", StringComparison.OrdinalIgnoreCase)) ?? 0,
        };
    }

    private async Task<OperationResult> RunAuditedWorkflowOperationAsync(
        string action,
        string workflowId,
        string runId,
        RiskLevel risk,
        string reason,
        Func<TemporalClient, Task<string>> operation)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult.Fail("Enter an action reason.");
        }

        var target = string.IsNullOrWhiteSpace(runId) ? workflowId : $"{workflowId}/{runId}";
        return await RunAuditedOperationAsync(action, target, risk, reason, operation);
    }

    private async Task<OperationResult> RunAuditedScheduleOperationAsync(
        string action,
        string scheduleId,
        RiskLevel risk,
        string reason,
        Func<TemporalClient, Task<string>> operation)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult.Fail("Enter an action reason.");
        }

        return await RunAuditedOperationAsync(action, scheduleId, risk, reason, operation);
    }

    private async Task<OperationResult> RunAuditedOperationAsync(
        string action,
        string target,
        RiskLevel risk,
        string reason,
        Func<TemporalClient, Task<string>> operation)
    {
        var audit = new AuditRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            Actor = _settings.Identity,
            Action = action,
            Target = target,
            Risk = risk,
            Reason = reason,
            Succeeded = false,
        };

        try
        {
            var client = await _clientProvider.GetClientAsync();
            var message = await operation(client);
            audit.Succeeded = true;
            EnqueueAudit(audit);
            return OperationResult.Ok(message, audit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Temporal operation failed. Action={Action}, Target={Target}", action, target);
            audit.Reason = $"{reason}\nError: {ex.Message}";
            EnqueueAudit(audit);
            return OperationResult.Fail($"Temporal operation failed: {ex.Message}");
        }
    }

    private void EnqueueAudit(AuditRecord audit)
    {
        _auditLog.Enqueue(audit);
        while (_auditLog.Count > 500 && _auditLog.TryDequeue(out _))
        {
        }
    }

    private static string BuildVisibilityQuery(WorkflowSearchQuery query)
    {
        var clauses = new List<string>();

        if (query.Status is not null)
        {
            clauses.Add($"ExecutionStatus = \"{ToTemporalStatusText(query.Status.Value)}\"");
        }

        if (!string.IsNullOrWhiteSpace(query.TaskQueue))
        {
            clauses.Add($"TaskQueue = \"{EscapeVisibilityValue(query.TaskQueue)}\"");
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = EscapeVisibilityValue(query.Keyword.Trim());
            clauses.Add($"(WorkflowId STARTS_WITH \"{keyword}\" OR WorkflowType = \"{keyword}\")");
        }

        return clauses.Count == 0 ? string.Empty : string.Join(" AND ", clauses);
    }

    private static IReadOnlyCollection<object?> ParseSignalArgs(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return Array.Empty<object?>();
        }

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement.Clone();
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Select(ConvertJsonElement).ToArray();
        }

        return [ConvertJsonElement(root)];
    }

    private static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), JsonOptions),
        JsonValueKind.Array => JsonSerializer.Deserialize<List<object?>>(element.GetRawText(), JsonOptions),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number when element.TryGetDouble(out var d) => d,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    private static OpsWorkflowStatus MapStatus(ClientWorkflowStatus status) => status switch
    {
        ClientWorkflowStatus.Running => OpsWorkflowStatus.Running,
        ClientWorkflowStatus.Completed => OpsWorkflowStatus.Completed,
        ClientWorkflowStatus.Failed => OpsWorkflowStatus.Failed,
        ClientWorkflowStatus.TimedOut => OpsWorkflowStatus.TimedOut,
        ClientWorkflowStatus.Canceled => OpsWorkflowStatus.Cancelled,
        ClientWorkflowStatus.Terminated => OpsWorkflowStatus.Terminated,
        ClientWorkflowStatus.ContinuedAsNew => OpsWorkflowStatus.ContinuedAsNew,
        ClientWorkflowStatus.Paused => OpsWorkflowStatus.Paused,
        _ => OpsWorkflowStatus.Running,
    };

    private static string ToTemporalStatusText(OpsWorkflowStatus status) => status switch
    {
        OpsWorkflowStatus.Cancelled => "Canceled",
        OpsWorkflowStatus.ContinuedAsNew => "ContinuedAsNew",
        OpsWorkflowStatus.TimedOut => "TimedOut",
        _ => status.ToString(),
    };

    private static RiskLevel CalculateRisk(OpsWorkflowStatus status, DateTimeOffset startedAt, int historyLength)
    {
        if (status is OpsWorkflowStatus.Failed or OpsWorkflowStatus.TimedOut or OpsWorkflowStatus.Terminated)
        {
            return RiskLevel.High;
        }

        if (status == OpsWorkflowStatus.Running && DateTimeOffset.UtcNow - startedAt > TimeSpan.FromHours(1))
        {
            return RiskLevel.Medium;
        }

        return historyLength > 5_000 ? RiskLevel.Medium : RiskLevel.Low;
    }

    private static async Task<string> FormatMemoAsync(IReadOnlyDictionary<string, IEncodedRawValue> memo)
    {
        if (memo.Count == 0)
        {
            return "{}";
        }

        var decoded = new Dictionary<string, object?>();
        foreach (var (key, value) in memo)
        {
            try
            {
                decoded[key] = await value.ToValueAsync<object>();
            }
            catch
            {
                decoded[key] = FormatMessage(value.Payload);
            }
        }

        return JsonSerializer.Serialize(decoded, JsonOptions);
    }

    private static string ExtractOwner(string searchAttrs, string memoJson)
    {
        foreach (var source in new[] { searchAttrs, memoJson })
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            foreach (var key in new[] { "Owner", "owner", "Team", "team" })
            {
                var index = source.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    return key;
                }
            }
        }

        return "-";
    }

    private static IReadOnlyList<string> GuessSignals(IReadOnlyList<WorkflowHistoryEvent> history)
    {
        var signals = history
            .Where(h => h.EventType.Contains("Signaled", StringComparison.OrdinalIgnoreCase))
            .Select(h => ExtractSignalName(h.Details))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (signals.Count == 0)
        {
            signals.AddRange(["operatorOverride", "retryNow", "skipStep"]);
        }

        return signals;
    }

    private static string ExtractSignalName(string details)
    {
        const string field = "signalName";
        var index = details.IndexOf(field, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        var colon = details.IndexOf(':', index);
        if (colon < 0)
        {
            return string.Empty;
        }

        var after = details[(colon + 1)..].Trim().Trim('"', ' ', ',', '}');
        var end = after.IndexOfAny(['"', ',', '}']);
        return end > 0 ? after[..end].Trim() : after.Trim();
    }

    private static string FormatStartedEvent(HistoryEvent ev)
    {
        var attr = ev.WorkflowExecutionStartedEventAttributes;
        if (attr is null)
        {
            return CompactEventDetails(ev);
        }

        return $"taskQueue={attr.TaskQueue?.Name ?? "-"}, workflowType={attr.WorkflowType?.Name ?? "-"}, input={FormatMessage(attr.Input)}";
    }

    private static string CompactEventDetails(object? value)
    {
        if (value is null)
        {
            return "-";
        }

        if (value is IMessage message)
        {
            return CompactEventDetails(message);
        }

        var text = value.ToString() ?? "-";
        return text.Length <= 600 ? text : text[..600] + " ...";
    }

    private static string CompactEventDetails(IMessage? message)
    {
        if (message is null)
        {
            return "-";
        }

        var text = FormatMessage(message);
        return text.Length <= 600 ? text : text[..600] + " ...";
    }

    private static string FormatMessage(IMessage? message)
    {
        if (message is null)
        {
            return "{}";
        }

        try
        {
            return JsonFormatter.Default.Format(message);
        }
        catch
        {
            return message.ToString();
        }
    }

    private static RpcOptions Rpc(CancellationToken cancellationToken) => new()
    {
        CancellationToken = cancellationToken,
        Timeout = TimeSpan.FromSeconds(30),
        Retry = true,
    };

    private static DateTimeOffset ToDateTimeOffset(DateTime? value)
    {
        if (value is null)
        {
            return DateTimeOffset.MinValue;
        }

        return ToDateTimeOffset(value.Value);
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return new DateTimeOffset(utc);
    }

    private static string FormatVisibilityTime(DateTimeOffset value) => $"\"{value.UtcDateTime:O}\"";

    private static string EscapeVisibilityValue(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int ClampToInt(long value) => value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    private static int ClampToInt(float value) => (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);

    private static double CalculateP95Seconds(IEnumerable<double> values)
    {
        var ordered = values.Where(v => v >= 0).OrderBy(v => v).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(ordered.Length * 0.95) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static IReadOnlyList<MetricPoint> BuildThroughputFallback(IEnumerable<WorkflowExecutionSummary> workflows)
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, 8)
            .Select(i => now.AddHours(i - 7))
            .Select(hour => new MetricPoint
            {
                Label = hour.ToLocalTime().ToString("HH"),
                Value = workflows.Count(w => w.ClosedAt is not null
                    && w.ClosedAt.Value >= hour
                    && w.ClosedAt.Value < hour.AddHours(1)),
            })
            .ToList();
    }

    private static string ExtractWorkerVersion(PollerInfo? poller)
    {
        if (poller?.DeploymentOptions is not null && !string.IsNullOrWhiteSpace(poller.DeploymentOptions.BuildId))
        {
            return poller.DeploymentOptions.BuildId;
        }

        return "unversioned";
    }
}
