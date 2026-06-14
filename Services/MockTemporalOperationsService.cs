using TemporalOpsBlazor.Models;

namespace TemporalOpsBlazor.Services;

public sealed class MockTemporalOperationsService : ITemporalOperationsService
{
    private readonly List<WorkflowExecutionSummary> _workflows;
    private readonly List<WorkerSummary> _workers;
    private readonly List<ScheduleSummary> _schedules;
    private readonly List<AuditRecord> _audit;
    private readonly object _sync = new();

    public MockTemporalOperationsService()
    {
        var now = DateTimeOffset.Now;

        _workflows =
        [
            new() { WorkflowId = "order-20260613-10425", RunId = "2f4d9a91-7ba5-4d52", WorkflowType = "OrderFulfillmentWorkflow", Status = WorkflowStatus.Running, TaskQueue = "orders-critical", StartedAt = now.AddMinutes(-46), Attempt = 1, PendingActivities = 2, HistoryLength = 824, LatencySeconds = 192.4m, Risk = RiskLevel.High, Owner = "commerce", Memo = "VIP order, inventory reservation pending", SearchAttributes = "customerTier=VIP, region=JP" },
            new() { WorkflowId = "order-20260613-10425", RunId = "b43d11d0-44bb-4f09", WorkflowType = "OrderFulfillmentWorkflow", Status = WorkflowStatus.ContinuedAsNew, TaskQueue = "orders-critical", StartedAt = now.AddHours(-3), ClosedAt = now.AddMinutes(-47), Attempt = 1, PendingActivities = 0, HistoryLength = 4981, LatencySeconds = 7998.0m, Risk = RiskLevel.Medium, Owner = "commerce", Memo = "ContinueAsNew: history compaction", SearchAttributes = "customerTier=VIP, region=JP" },
            new() { WorkflowId = "order-20260613-10425", RunId = "0c6c5988-0252-4dd3", WorkflowType = "OrderFulfillmentWorkflow", Status = WorkflowStatus.ContinuedAsNew, TaskQueue = "orders-critical", StartedAt = now.AddHours(-5), ClosedAt = now.AddHours(-3).AddMinutes(-1), Attempt = 1, PendingActivities = 0, HistoryLength = 5022, LatencySeconds = 7140.0m, Risk = RiskLevel.Medium, Owner = "commerce", Memo = "ContinueAsNew: daily order slice", SearchAttributes = "customerTier=VIP, region=JP" },
            new() { WorkflowId = "payment-reconcile-nightly", RunId = "98fc9231-0cb8-4bb7", WorkflowType = "PaymentReconciliationWorkflow", Status = WorkflowStatus.Failed, TaskQueue = "payments-batch", StartedAt = now.AddHours(-3), ClosedAt = now.AddHours(-2).AddMinutes(-44), Attempt = 5, PendingActivities = 0, HistoryLength = 1412, LatencySeconds = 587.1m, Risk = RiskLevel.Critical, Owner = "finance", Memo = "Provider timeout after retry exhaustion", SearchAttributes = "provider=AcmePay, severity=critical" },
            new() { WorkflowId = "invoice-archiver-2026-06-13", RunId = "a531255d-8f1f-447b", WorkflowType = "InvoiceArchivalWorkflow", Status = WorkflowStatus.Running, TaskQueue = "backoffice", StartedAt = now.AddHours(-1).AddMinutes(-18), Attempt = 2, PendingActivities = 1, HistoryLength = 411, LatencySeconds = 71.8m, Risk = RiskLevel.Medium, Owner = "billing", Memo = "Storage upload throttling observed", SearchAttributes = "tenant=global" },
            new() { WorkflowId = "subscription-renewal-jp-0613", RunId = "f71f54f8-6715-4594", WorkflowType = "SubscriptionRenewalWorkflow", Status = WorkflowStatus.Running, TaskQueue = "subscriptions", StartedAt = now.AddMinutes(-24), Attempt = 1, PendingActivities = 0, HistoryLength = 182, LatencySeconds = 24.5m, Risk = RiskLevel.Low, Owner = "growth", Memo = "Healthy", SearchAttributes = "market=JP" },
            new() { WorkflowId = "shipment-label-retry-88012", RunId = "fdb47f99-283e-4609", WorkflowType = "ShipmentLabelWorkflow", Status = WorkflowStatus.TimedOut, TaskQueue = "logistics", StartedAt = now.AddHours(-6), ClosedAt = now.AddHours(-5).AddMinutes(-39), Attempt = 3, PendingActivities = 0, HistoryLength = 526, LatencySeconds = 420.0m, Risk = RiskLevel.High, Owner = "logistics", Memo = "Carrier API unstable", SearchAttributes = "carrier=NekoLine" },
            new() { WorkflowId = "customer-export-gdpr-447", RunId = "7c1a1448-24d9-4a8e", WorkflowType = "CustomerDataExportWorkflow", Status = WorkflowStatus.Completed, TaskQueue = "privacy", StartedAt = now.AddHours(-9), ClosedAt = now.AddHours(-8).AddMinutes(-47), Attempt = 1, PendingActivities = 0, HistoryLength = 244, LatencySeconds = 106.3m, Risk = RiskLevel.Medium, Owner = "privacy", Memo = "Completed within SLA", SearchAttributes = "requestType=gdpr" },
            new() { WorkflowId = "warehouse-resync-kansai", RunId = "1e934d78-23f5-4d0d", WorkflowType = "WarehouseResyncWorkflow", Status = WorkflowStatus.Running, TaskQueue = "warehouse", StartedAt = now.AddHours(-2), Attempt = 1, PendingActivities = 4, HistoryLength = 996, LatencySeconds = 321.8m, Risk = RiskLevel.High, Owner = "supply-chain", Memo = "Backlog increasing", SearchAttributes = "site=Kansai" }
        ];

        _workers =
        [
            new() { WorkerId = "orders-worker-a01", TaskQueue = "orders-critical", Status = WorkerStatus.Healthy, Pollers = 8, Backlog = 12, SlotsUsed = 42, SlotsCapacity = 64, LastHeartbeat = now.AddSeconds(-7), Version = "2026.06.13.1" },
            new() { WorkerId = "payments-worker-b02", TaskQueue = "payments-batch", Status = WorkerStatus.Degraded, Pollers = 3, Backlog = 184, SlotsUsed = 31, SlotsCapacity = 32, LastHeartbeat = now.AddMinutes(-2), Version = "2026.06.12.4" },
            new() { WorkerId = "logistics-worker-c01", TaskQueue = "logistics", Status = WorkerStatus.Degraded, Pollers = 2, Backlog = 71, SlotsUsed = 15, SlotsCapacity = 16, LastHeartbeat = now.AddMinutes(-1), Version = "2026.06.10.8" },
            new() { WorkerId = "warehouse-worker-w07", TaskQueue = "warehouse", Status = WorkerStatus.Healthy, Pollers = 5, Backlog = 39, SlotsUsed = 22, SlotsCapacity = 48, LastHeartbeat = now.AddSeconds(-11), Version = "2026.06.13.1" },
            new() { WorkerId = "privacy-worker-p01", TaskQueue = "privacy", Status = WorkerStatus.Healthy, Pollers = 2, Backlog = 0, SlotsUsed = 1, SlotsCapacity = 8, LastHeartbeat = now.AddSeconds(-5), Version = "2026.05.28.2" }
        ];

        _schedules =
        [
            new() { ScheduleId = "daily-payment-reconcile", WorkflowType = "PaymentReconciliationWorkflow", Cron = "0 2 * * *", IsPaused = false, NextRunAt = now.Date.AddDays(1).AddHours(2), TaskQueue = "payments-batch", OverlapSkipped24h = 0 },
            new() { ScheduleId = "invoice-archiver-hourly", WorkflowType = "InvoiceArchivalWorkflow", Cron = "0 * * * *", IsPaused = false, NextRunAt = now.AddHours(1).AddMinutes(-now.Minute).AddSeconds(-now.Second), TaskQueue = "backoffice", OverlapSkipped24h = 1 },
            new() { ScheduleId = "warehouse-resync-kansai", WorkflowType = "WarehouseResyncWorkflow", Cron = "*/30 * * * *", IsPaused = true, NextRunAt = now.AddMinutes(30), TaskQueue = "warehouse", OverlapSkipped24h = 3 },
            new() { ScheduleId = "privacy-export-cleanup", WorkflowType = "PrivacyExportCleanupWorkflow", Cron = "30 3 * * 0", IsPaused = false, NextRunAt = now.Date.AddDays(1).AddHours(3).AddMinutes(30), TaskQueue = "privacy", OverlapSkipped24h = 0 }
        ];

        _audit =
        [
            new() { Timestamp = now.AddMinutes(-12), Actor = "ops.lead@example.com", Action = "Signal", Target = "order-20260613-10425", Risk = RiskLevel.Medium, Reason = "Force inventory refresh", Succeeded = true },
            new() { Timestamp = now.AddMinutes(-31), Actor = "sre@example.com", Action = "Pause schedule", Target = "warehouse-resync-kansai", Risk = RiskLevel.High, Reason = "Backlog pressure control", Succeeded = true },
            new() { Timestamp = now.AddHours(-1), Actor = "finance.ops@example.com", Action = "Reset", Target = "payment-reconcile-nightly", Risk = RiskLevel.Critical, Reason = "Replay from provider callback received", Succeeded = true }
        ];
    }

    public Task<TemporalDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var groupedWorkflows = GroupContinuationRuns(_workflows);

            var snapshot = new TemporalDashboardSnapshot
            {
                Namespace = "default",
                UpdatedAt = DateTimeOffset.Now,
                RunningWorkflows = groupedWorkflows.Count(x => x.Status == WorkflowStatus.Running),
                FailedWorkflows24h = groupedWorkflows.Count(x => x.Status is WorkflowStatus.Failed or WorkflowStatus.TimedOut),
                StuckWorkflows = groupedWorkflows.Count(x => x.Status == WorkflowStatus.Running && x.LatencySeconds > 180),
                ActiveWorkers = _workers.Count(x => x.Status != WorkerStatus.Offline),
                CompletionRate = 98.72m,
                P95LatencySeconds = 87.4m,
                HotWorkflows = groupedWorkflows.OrderByDescending(x => x.Risk).ThenByDescending(x => x.LatencySeconds).Take(5).ToList(),
                Workers = _workers.ToList(),
                Schedules = _schedules.ToList(),
                RecentAudit = _audit.OrderByDescending(x => x.Timestamp).Take(5).ToList(),
                Throughput = BuildThroughput()
            };

            return Task.FromResult(snapshot);
        }
    }

    public Task<IReadOnlyList<WorkflowExecutionSummary>> SearchWorkflowsAsync(WorkflowSearchQuery query, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IEnumerable<WorkflowExecutionSummary> result = _workflows;

            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                result = result.Where(x =>
                    x.WorkflowId.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase) ||
                    x.RunId.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase) ||
                    x.WorkflowType.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase) ||
                    x.Owner.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase) ||
                    x.SearchAttributes.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase));
            }

            if (query.Status is not null)
            {
                result = result.Where(x => x.Status == query.Status.Value);
            }

            if (!string.IsNullOrWhiteSpace(query.TaskQueue))
            {
                result = result.Where(x => x.TaskQueue.Equals(query.TaskQueue, StringComparison.OrdinalIgnoreCase));
            }

            if (query.MinimumRisk is not null)
            {
                result = result.Where(x => x.Risk >= query.MinimumRisk.Value);
            }

            var grouped = GroupContinuationRuns(result);

            return Task.FromResult<IReadOnlyList<WorkflowExecutionSummary>>(grouped
                .OrderByDescending(LatestCurrentRunStart)
                .ThenByDescending(x => x.Status == WorkflowStatus.Running)
                .ThenByDescending(x => x.Risk)
                .ToList());
        }
    }

    public Task<WorkflowDetail?> GetWorkflowAsync(string workflowId, string? runId = null, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var summary = _workflows.FirstOrDefault(x =>
                x.WorkflowId.Equals(workflowId, StringComparison.OrdinalIgnoreCase) &&
                (runId is null || x.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase)));

            if (summary is null)
            {
                return Task.FromResult<WorkflowDetail?>(null);
            }

            var groupedSummary = GroupContinuationRuns(_workflows
                .Where(x => x.WorkflowId.Equals(summary.WorkflowId, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault() ?? summary;

            var detailSummary = groupedSummary;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                var selectedRun = groupedSummary.ContinuationRuns.FirstOrDefault(r =>
                    string.Equals(r.RunId, summary.RunId, StringComparison.OrdinalIgnoreCase))
                    ?? ToRunSummary(summary, false);
                detailSummary = CloneForSingleRun(summary, selectedRun);
            }

            var detail = new WorkflowDetail
            {
                Summary = detailSummary,
                OpenSignals = ["refreshInventory", "changePriority", "operatorOverride"],
                InputJson = """
                {
                  "tenant": "jp-commerce",
                  "workflowSource": "operator-console",
                  "priority": "high",
                  "traceId": "trc_82fb1290"
                }
                """,
                MemoJson = """
                {
                  "owner": "operations",
                  "runbook": "https://runbooks.example.local/temporal/high-risk-workflow",
                  "slaMinutes": 60
                }
                """,
                History = BuildHistory(detailSummary),
                ContinuationRuns = groupedSummary.ContinuationRuns,
                Motion = BuildMotion(detailSummary)
            };

            return Task.FromResult<WorkflowDetail?>(detail);
        }
    }

    public Task<IReadOnlyList<WorkerSummary>> GetWorkersAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<WorkerSummary>>(_workers.OrderByDescending(x => x.Status).ThenByDescending(x => x.Backlog).ToList());
        }
    }

    public Task<IReadOnlyList<ScheduleSummary>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<ScheduleSummary>>(_schedules.OrderBy(x => x.NextRunAt).ToList());
        }
    }

    public Task<IReadOnlyList<AuditRecord>> GetAuditLogAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<AuditRecord>>(_audit.OrderByDescending(x => x.Timestamp).ToList());
        }
    }

    public Task<OperationResult> RequestCancellationAsync(string workflowId, string runId, string reason, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var workflow = FindWorkflow(workflowId, runId);
            if (workflow is null) return Task.FromResult(OperationResult.Fail("Target workflow was not found."));
            workflow.Status = WorkflowStatus.Cancelled;
            workflow.ClosedAt = DateTimeOffset.Now;
            return Task.FromResult(Audit("Request cancellation", workflowId, RiskLevel.High, reason));
        }
    }

    public Task<OperationResult> TerminateAsync(string workflowId, string runId, string reason, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var workflow = FindWorkflow(workflowId, runId);
            if (workflow is null) return Task.FromResult(OperationResult.Fail("Target workflow was not found."));
            workflow.Status = WorkflowStatus.Terminated;
            workflow.ClosedAt = DateTimeOffset.Now;
            return Task.FromResult(Audit("Terminate", workflowId, RiskLevel.Critical, reason));
        }
    }

    public Task<OperationResult> SendSignalAsync(string workflowId, string runId, string signalName, string payloadJson, string reason, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var workflow = FindWorkflow(workflowId, runId);
            if (workflow is null) return Task.FromResult(OperationResult.Fail("Target workflow was not found."));
            workflow.Memo = $"Signal '{signalName}' sent. {workflow.Memo}";
            return Task.FromResult(Audit($"Signal: {signalName}", workflowId, RiskLevel.Medium, reason));
        }
    }

    public Task<OperationResult> ResetAsync(string workflowId, string runId, long eventId, string reason, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var workflow = FindWorkflow(workflowId, runId);
            if (workflow is null) return Task.FromResult(OperationResult.Fail("Target workflow was not found."));
            workflow.Status = WorkflowStatus.Running;
            workflow.Attempt += 1;
            workflow.PendingActivities = Math.Max(1, workflow.PendingActivities);
            workflow.ClosedAt = null;
            workflow.Memo = $"Reset from event {eventId}. {workflow.Memo}";
            return Task.FromResult(Audit($"Reset from event {eventId}", workflowId, RiskLevel.Critical, reason));
        }
    }

    public Task<OperationResult> PauseScheduleAsync(string scheduleId, string reason, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var schedule = _schedules.FirstOrDefault(x => x.ScheduleId.Equals(scheduleId, StringComparison.OrdinalIgnoreCase));
            if (schedule is null) return Task.FromResult(OperationResult.Fail("Target schedule was not found."));
            schedule.IsPaused = true;
            return Task.FromResult(Audit("Pause schedule", scheduleId, RiskLevel.High, reason));
        }
    }

    public Task<OperationResult> UnpauseScheduleAsync(string scheduleId, string reason, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var schedule = _schedules.FirstOrDefault(x => x.ScheduleId.Equals(scheduleId, StringComparison.OrdinalIgnoreCase));
            if (schedule is null) return Task.FromResult(OperationResult.Fail("Target schedule was not found."));
            schedule.IsPaused = false;
            return Task.FromResult(Audit("Unpause schedule", scheduleId, RiskLevel.Medium, reason));
        }
    }

    private static IReadOnlyList<WorkflowExecutionSummary> GroupContinuationRuns(IEnumerable<WorkflowExecutionSummary> workflows)
    {
        return workflows
            .GroupBy(x => x.WorkflowId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var runs = g
                    .OrderByDescending(x => x.StartedAt)
                    .Select(x => ToRunSummary(x, false))
                    .ToList();

                var current = runs
                    .OrderByDescending(x => x.Status == WorkflowStatus.Running)
                    .ThenByDescending(x => x.Status != WorkflowStatus.ContinuedAsNew)
                    .ThenByDescending(x => x.StartedAt)
                    .First();

                foreach (var run in runs)
                {
                    run.IsCurrent = run.RunId.Equals(current.RunId, StringComparison.OrdinalIgnoreCase);
                }

                var representative = g.First(x => x.RunId.Equals(current.RunId, StringComparison.OrdinalIgnoreCase));
                var started = runs.Min(x => x.StartedAt);
                DateTimeOffset? closed = runs.Any(x => x.ClosedAt is null) ? (DateTimeOffset?)null : runs.Max(x => x.ClosedAt);
                var latency = closed is null ? DateTimeOffset.Now - started : closed.Value - started;

                return new WorkflowExecutionSummary
                {
                    WorkflowId = representative.WorkflowId,
                    RunId = current.RunId,
                    WorkflowType = representative.WorkflowType,
                    Status = current.Status,
                    TaskQueue = representative.TaskQueue,
                    Namespace = representative.Namespace,
                    StartedAt = started,
                    ClosedAt = closed,
                    Attempt = runs.Count,
                    PendingActivities = representative.PendingActivities,
                    HistoryLength = runs.Sum(x => x.HistoryLength),
                    LatencySeconds = (decimal)Math.Max(0, latency.TotalSeconds),
                    Risk = g.Max(x => x.Risk),
                    Owner = representative.Owner,
                    Memo = representative.Memo,
                    SearchAttributes = representative.SearchAttributes,
                    ContinuationRunCount = runs.Count,
                    ContinuationRuns = runs
                };
            })
            .ToList();
    }


    private static DateTimeOffset LatestCurrentRunStart(WorkflowExecutionSummary workflow) =>
        workflow.ContinuationRuns.FirstOrDefault(r => r.IsCurrent)?.StartedAt
        ?? workflow.ContinuationRuns.OrderByDescending(r => r.StartedAt).FirstOrDefault()?.StartedAt
        ?? workflow.StartedAt;

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
        IsCurrent = isCurrent
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

    private WorkflowExecutionSummary? FindWorkflow(string workflowId, string runId) =>
        _workflows.FirstOrDefault(x =>
            x.WorkflowId.Equals(workflowId, StringComparison.OrdinalIgnoreCase) &&
            x.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase));

    private OperationResult Audit(string action, string target, RiskLevel risk, string reason)
    {
        var audit = new AuditRecord
        {
            Timestamp = DateTimeOffset.Now,
            Actor = "current.operator@example.com",
            Action = action,
            Target = target,
            Risk = risk,
            Reason = string.IsNullOrWhiteSpace(reason) ? "No reason supplied" : reason,
            Succeeded = true
        };
        _audit.Add(audit);
        return OperationResult.Ok($"{action} accepted.", audit);
    }

    private static IReadOnlyList<MetricPoint> BuildThroughput() =>
    [
        new() { Label = "08:00", Value = 44 },
        new() { Label = "09:00", Value = 62 },
        new() { Label = "10:00", Value = 58 },
        new() { Label = "11:00", Value = 73 },
        new() { Label = "12:00", Value = 69 },
        new() { Label = "13:00", Value = 81 },
        new() { Label = "14:00", Value = 77 },
        new() { Label = "15:00", Value = 94 }
    ];


    private static WorkflowMotion BuildMotion(WorkflowExecutionSummary summary)
    {
        var now = DateTimeOffset.Now;
        var runs = summary.ContinuationRuns.Count > 0
            ? summary.ContinuationRuns.OrderBy(r => r.StartedAt).ToList()
            : new List<WorkflowRunSummary> { ToRunSummary(summary, true) };

        var start = runs.Min(r => r.StartedAt);
        var end = runs.Select(r => r.ClosedAt ?? now).Max();
        if (end <= start)
        {
            end = start.AddSeconds(1);
        }

        var root = new WorkflowMotionSegment
        {
            Id = "mock-root",
            LaneId = "workflow",
            Kind = "workflow",
            Label = summary.WorkflowType,
            Details = $"{summary.WorkflowId} / {summary.Status}",
            Status = summary.Status,
            StartTime = start,
            EndTime = end,
            WorkflowId = summary.WorkflowId,
            RunId = summary.RunId,
            IsCurrent = summary.Status == WorkflowStatus.Running,
            IsProblem = IsProblemStatus(summary.Status),
            IsWaiting = summary.Status == WorkflowStatus.Running,
            DisplayLevel = 1
        };

        var runSegments = runs.Select(run => new WorkflowMotionSegment
        {
            Id = $"mock-run-{run.RunId}",
            LaneId = "run-chain",
            Kind = "run",
            Label = ShortRunId(run.RunId),
            Details = $"{run.Status} / {run.HistoryLength:N0} events / {run.LatencySeconds:N1}s",
            Status = run.Status,
            StartTime = run.StartedAt,
            EndTime = run.ClosedAt ?? now,
            WorkflowId = run.WorkflowId,
            RunId = run.RunId,
            IsCurrent = run.IsCurrent,
            IsProblem = IsProblemStatus(run.Status),
            IsWaiting = run.Status == WorkflowStatus.Running,
            DisplayLevel = 1
        }).ToList();

        var activitySegments = new List<WorkflowMotionSegment>
        {
            new()
            {
                Id = "mock-activity-validate",
                LaneId = "activities",
                Kind = "activity",
                Label = "ValidateInput",
                Details = "Input validation and routing decision.",
                Status = WorkflowStatus.Completed,
                StartTime = start.AddSeconds(12),
                EndTime = start.AddSeconds(35),
                StartEventId = 8,
                EndEventId = 16,
                WorkflowId = summary.WorkflowId,
                RunId = summary.RunId,
                DisplayLevel = 2
            },
            new()
            {
                Id = "mock-activity-provider",
                LaneId = "activities",
                Kind = "activity",
                Label = summary.Risk >= RiskLevel.High ? "ExternalProviderRequest" : "FinalizeOutput",
                Details = summary.Risk >= RiskLevel.High ? "Retry policy applied after provider error." : "Output persisted successfully.",
                Status = summary.Risk >= RiskLevel.High ? WorkflowStatus.Failed : WorkflowStatus.Completed,
                StartTime = start.AddMinutes(2),
                EndTime = start.AddMinutes(4),
                StartEventId = 22,
                EndEventId = 43,
                WorkflowId = summary.WorkflowId,
                RunId = summary.RunId,
                IsProblem = summary.Risk >= RiskLevel.High,
                DisplayLevel = 2
            }
        };

        var childSegments = summary.WorkflowId.Contains("order", StringComparison.OrdinalIgnoreCase)
            ? new List<WorkflowMotionSegment>
            {
                new()
                {
                    Id = "mock-child-inventory",
                    LaneId = "children",
                    Kind = "child",
                    Label = "inventory-reservation-child",
                    Details = "Inventory reservation child workflow.",
                    Status = WorkflowStatus.Running,
                    StartTime = now.AddMinutes(-35),
                    EndTime = now,
                    WorkflowId = "inventory-reservation-child",
                    RunId = "child-4b1c9fd0",
                    IsCurrent = true,
                    IsWaiting = true,
                    DisplayLevel = 1
                }
            }
            : new List<WorkflowMotionSegment>();

        var markers = new List<WorkflowMotionMarker>();
        foreach (var run in runs.Where(r => r.Status == WorkflowStatus.ContinuedAsNew))
        {
            markers.Add(new WorkflowMotionMarker
            {
                Id = $"mock-continue-{run.RunId}",
                LaneId = "run-chain",
                Kind = "continue",
                Label = "Continued As New",
                Details = "History compacted into a fresh run.",
                Timestamp = run.ClosedAt ?? run.StartedAt,
                IsProblem = false,
                DisplayLevel = 1
            });
        }

        var lanes = new List<WorkflowMotionLane>
        {
            new() { Id = "workflow", Title = "Overall execution", Subtitle = "Business-level execution window across all related runs.", Kind = "workflow", DisplayLevel = 1, Segments = [root] },
            new() { Id = "run-chain", Title = "Continue-As-New run chain", Subtitle = "Same Workflow ID, oldest to newest.", Kind = "run", DisplayLevel = 1, Segments = runSegments, Markers = markers },
        };

        if (childSegments.Count > 0)
        {
            lanes.Add(new WorkflowMotionLane { Id = "children", Title = "Child workflows", Subtitle = "Downstream child workflows started by the parent.", Kind = "child", DisplayLevel = 1, Segments = childSegments });
        }

        lanes.Add(new WorkflowMotionLane { Id = "activities", Title = "Major activities", Subtitle = "Activities collapsed into operational segments.", Kind = "activity", DisplayLevel = 2, Segments = activitySegments });

        NormalizeMotionLayout(lanes, start, end);

        var findings = BuildMockFindings(summary, childSegments, activitySegments, runs);
        var problemCount = lanes.SelectMany(l => l.Segments).Count(s => s.IsProblem) + markers.Count(m => m.IsProblem);

        return new WorkflowMotion
        {
            WorkflowId = summary.WorkflowId,
            CurrentRunId = summary.RunId,
            OverallStatus = summary.Status,
            Risk = summary.Risk,
            TimelineStart = start,
            TimelineEnd = end,
            TotalDurationSeconds = (decimal)Math.Max(0, (end - start).TotalSeconds),
            RunCount = runs.Count,
            ChildWorkflowCount = childSegments.Count,
            ActivityCount = activitySegments.Count,
            ProblemCount = problemCount,
            CurrentState = summary.Status == WorkflowStatus.Running ? "Current run is active." : $"Latest status is {summary.Status}.",
            BusinessImpact = summary.Risk >= RiskLevel.High ? "Potential business impact. Review highlighted segments before taking action." : "No immediate business impact is visible from the grouped execution.",
            RecommendedAction = findings.OrderByDescending(f => f.Severity).First().RecommendedAction,
            Lanes = lanes,
            Markers = markers,
            Findings = findings
        };
    }

    private static IReadOnlyList<WorkflowMotionFinding> BuildMockFindings(
        WorkflowExecutionSummary summary,
        IReadOnlyList<WorkflowMotionSegment> children,
        IReadOnlyList<WorkflowMotionSegment> activities,
        IReadOnlyList<WorkflowRunSummary> runs)
    {
        var findings = new List<WorkflowMotionFinding>();

        if (IsProblemStatus(summary.Status))
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.Critical,
                Title = "Workflow closed abnormally",
                Description = $"The grouped workflow status is {summary.Status}.",
                RecommendedAction = "Confirm business impact, then decide whether reset or manual recovery is appropriate."
            });
        }

        if (activities.Any(a => a.IsProblem))
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.High,
                Title = "Activity failure detected",
                Description = "A major activity segment ended abnormally.",
                RecommendedAction = "Check worker health and external dependencies before retrying."
            });
        }

        if (runs.Count >= 3)
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.Medium,
                Title = "Continue-As-New chain",
                Description = $"{runs.Count} runs are grouped under the same Workflow ID.",
                RecommendedAction = "Confirm this is an expected compaction pattern, not an unintended loop."
            });
        }

        if (children.Any(c => c.IsCurrent))
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.Medium,
                Title = "Waiting on child workflow",
                Description = "A child workflow is still running.",
                RecommendedAction = "Review the child workflow lane before acting on the parent."
            });
        }

        if (findings.Count == 0)
        {
            findings.Add(new WorkflowMotionFinding
            {
                Severity = RiskLevel.Low,
                Title = "No urgent anomaly detected",
                Description = "The grouped execution appears operationally healthy.",
                RecommendedAction = "Continue monitoring."
            });
        }

        return findings.OrderByDescending(f => f.Severity).ToList();
    }

    private static void NormalizeMotionLayout(IReadOnlyList<WorkflowMotionLane> lanes, DateTimeOffset start, DateTimeOffset end)
    {
        var totalMs = Math.Max(1, (end - start).TotalMilliseconds);
        foreach (var segment in lanes.SelectMany(l => l.Segments))
        {
            var segmentStart = segment.StartTime < start ? start : segment.StartTime;
            var segmentEnd = segment.EndTime <= segment.StartTime ? segment.StartTime.AddMilliseconds(1) : segment.EndTime;
            segmentEnd = segmentEnd > end ? end : segmentEnd;
            segment.OffsetPercent = Math.Clamp((decimal)((segmentStart - start).TotalMilliseconds / totalMs * 100), 0m, 100m);
            segment.WidthPercent = Math.Max(0.65m, Math.Clamp((decimal)((segmentEnd - segmentStart).TotalMilliseconds / totalMs * 100), 0m, 100m));
        }

        foreach (var marker in lanes.SelectMany(l => l.Markers))
        {
            marker.OffsetPercent = Math.Clamp((decimal)((marker.Timestamp - start).TotalMilliseconds / totalMs * 100), 0m, 100m);
        }
    }

    private static bool IsProblemStatus(WorkflowStatus status) => status is WorkflowStatus.Failed or WorkflowStatus.TimedOut or WorkflowStatus.Terminated or WorkflowStatus.Cancelled;

    private static string ShortRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return "-";
        }

        return runId.Length <= 8 ? runId : runId[..8];
    }

    private static IReadOnlyList<WorkflowHistoryEvent> BuildHistory(WorkflowExecutionSummary summary)
    {
        var start = summary.StartedAt;
        return
        [
            new() { EventId = 1, Timestamp = start, EventType = "WorkflowExecutionStarted", Details = $"{summary.WorkflowType} started on {summary.TaskQueue}", IsProblem = false },
            new() { EventId = 5, Timestamp = start.AddSeconds(9), EventType = "WorkflowTaskScheduled", Details = "First workflow task scheduled", IsProblem = false },
            new() { EventId = 8, Timestamp = start.AddSeconds(12), EventType = "ActivityTaskScheduled", Details = "ValidateInput activity", IsProblem = false },
            new() { EventId = 16, Timestamp = start.AddSeconds(31), EventType = "ActivityTaskCompleted", Details = "ValidateInput completed", IsProblem = false },
            new() { EventId = 22, Timestamp = start.AddMinutes(2), EventType = "ActivityTaskScheduled", Details = "External provider request", IsProblem = false },
            new() { EventId = 43, Timestamp = start.AddMinutes(4), EventType = summary.Risk >= RiskLevel.High ? "ActivityTaskFailed" : "ActivityTaskCompleted", Details = summary.Risk >= RiskLevel.High ? "Transient provider error; retry policy applied" : "Provider request completed", IsProblem = summary.Risk >= RiskLevel.High },
            new() { EventId = 71, Timestamp = DateTimeOffset.Now.AddMinutes(-3), EventType = "WorkflowTaskCompleted", Details = "Latest workflow task completed", IsProblem = false }
        ];
    }
}
