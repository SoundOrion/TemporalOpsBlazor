using System.Collections.Concurrent;
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

        if (query.MinimumRisk is not null)
        {
            workflows = workflows.Where(w => w.Risk >= query.MinimumRisk).ToList();
        }

        return workflows
            .OrderByDescending(w => w.Risk)
            .ThenByDescending(w => w.StartedAt)
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

                if (ev.EventType == EventType.WorkflowExecutionStarted && ev.WorkflowExecutionStartedEventAttributes?.Input is not null)
                {
                    inputJson = FormatMessage(ev.WorkflowExecutionStartedEventAttributes.Input);
                }

                history.Add(MapHistoryEvent(ev));
            }

            return new TemporalOpsBlazor.Models.WorkflowDetail
            {
                Summary = summary,
                History = history,
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
            return OperationResult.Fail("Signal名を入力してください。");
        }

        IReadOnlyCollection<object?> args;
        try
        {
            args = ParseSignalArgs(payloadJson);
        }
        catch (JsonException ex)
        {
            return OperationResult.Fail($"Payload JSONが不正です: {ex.Message}");
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
            return OperationResult.Fail("Reset Event IDは1以上を指定してください。");
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

    private async Task<WorkflowExecutionSummary> MapWorkflowAsync(ClientWorkflowExecution workflow, string ns)
    {
        var status = MapStatus(workflow.Status);
        var startedAt = ToDateTimeOffset(workflow.StartTime);
        DateTimeOffset? closedAt = workflow.CloseTime.HasValue ? ToDateTimeOffset(workflow.CloseTime.Value) : null;
        var latency = closedAt is null ? DateTimeOffset.UtcNow - startedAt : closedAt.Value - startedAt;
        var memoJson = await FormatMemoAsync(workflow.Memo);
        var searchAttrs = workflow.TypedSearchAttributes?.ToString() ?? string.Empty;

        return new WorkflowExecutionSummary
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
        };
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
            return OperationResult.Fail("操作理由を入力してください。");
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
            return OperationResult.Fail("操作理由を入力してください。");
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
