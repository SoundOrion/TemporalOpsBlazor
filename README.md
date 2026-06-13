# TemporalOpsBlazor

Temporal Workflowを運用管理者が扱うための、Blazor Serverベースの管理UIです。  
この版ではMock serviceだけでなく、Temporal .NET SDKを使った実Temporal接続に対応しています。

## 主な機能

- Dashboard: Running / Failed / Stuck / Worker / P95 latencyなどの運用KPI
- Workflows: Workflow ID、Run ID、Type、Task Queue、Status、Riskで検索
- Workflow Detail: Metadata、Signals、Event history、Input、Memo/Runbook
- Operator Actions: Signal、Cancel、Reset、Terminate
- Safety Guard: 危険操作の二段確認、操作理由必須、Audit log記録
- Workers: Task QueueごとのPoller、Backlog、Dispatch rate、最終Poll時刻
- Schedules: ScheduleのPause / Unpause
- Audit: 運用操作の履歴確認

## 起動方法

```bash
cd TemporalOpsBlazor
dotnet restore
dotnet run
```

ブラウザで表示されたローカルURLを開いてください。

## Temporal接続設定

`appsettings.json` の `Temporal` セクションで設定します。初期値はローカルTemporal dev server向けです。

```json
{
  "Temporal": {
    "UseMock": false,
    "TargetHost": "localhost:7233",
    "Namespace": "default",
    "Identity": "temporal-ops-blazor",
    "ApiKey": "",
    "WorkflowPageSize": 100,
    "DashboardPageSize": 200,
    "HistoryEventLimit": 120,
    "StuckWorkflowMinutes": 60,
    "MonitoredTaskQueues": ["default"],
    "Tls": {
      "Enabled": false,
      "Disabled": false,
      "Domain": "",
      "ServerRootCaCertPath": "",
      "ClientCertPath": "",
      "ClientPrivateKeyPath": ""
    }
  }
}
```

Mockに戻したい場合:

```json
"UseMock": true
```

環境変数でも上書きできます。

```bash
export Temporal__UseMock=false
export Temporal__TargetHost=localhost:7233
export Temporal__Namespace=default
export Temporal__MonitoredTaskQueues__0=default
```

## Temporal Cloud API Key例

```bash
export Temporal__UseMock=false
export Temporal__TargetHost='your-namespace.account.tmprl.cloud:7233'
export Temporal__Namespace='your-namespace.account'
export Temporal__ApiKey='your-api-key'
```

API Keyを指定した場合はTLS接続前提です。必要に応じて `Temporal:Tls:Domain` も設定してください。

## mTLS例

```bash
export Temporal__UseMock=false
export Temporal__TargetHost='your-namespace.account.tmprl.cloud:7233'
export Temporal__Namespace='your-namespace.account'
export Temporal__Tls__Enabled=true
export Temporal__Tls__ClientCertPath='/secure/client.pem'
export Temporal__Tls__ClientPrivateKeyPath='/secure/client.key'
```

## 実データ対応の構成

- `TemporalClientProvider`: appsettings / 環境変数から `TemporalClient` を生成し、Singletonとして再利用
- `TemporalOperationsService`: `ITemporalOperationsService` の実Temporal実装
- `Program.cs`: `Temporal:UseMock` でMock / Realを切り替え
- `Services/MockTemporalOperationsService.cs`: UI確認用のデモ実装として残置

## 実装済みAPIの対応

- Workflow一覧: `ListWorkflowsAsync` + Visibility query
- Workflow件数: `CountWorkflowsAsync`
- Workflow詳細: `GetWorkflowHandle(...).DescribeAsync()` + `FetchHistoryEventsAsync()`
- Signal: `SignalAsync`
- Cancel: `CancelAsync`
- Terminate: `TerminateAsync`
- Reset: `ResetWorkflowExecutionAsync`
- Task Queue / Worker: `DescribeTaskQueueAsync`
- Schedules: `ListSchedulesAsync` / `PauseAsync` / `UnpauseAsync`

## 本番運用で追加したいもの

- OIDC / Entra ID / Oktaなどの認証認可
- Namespace単位、Task Queue単位、Action単位のRBAC
- Terminate / Resetの承認ワークフロー
- Signal payloadのJSON Schema検証
- Temporal visibility queryの保存検索
- Audit logのDB永続化
- OpenTelemetry / Prometheusメトリクス連携
- Runbook URL、Incident ticket、PagerDutyなどの外部連携

本番ではUIからTemporalへ直接接続せず、RBAC・監査・承認・Rate limitを備えたBackend APIを挟む構成も検討してください。

## v5: Continue-As-New grouping

Workflow一覧では、同じ `WorkflowId` を持つ複数Runを Continue-As-New chain として1行に集約します。
行の `ContinueAsNew × N` を開くと古いRunから現在Runまでのタイムラインを表示し、詳細画面にもRun chainを表示します。
運用操作は、集約行の current run に対して実行されます。

## v7 Operations / Management Brush-up

This version adds a management-oriented operations layer on top of the workflow execution views.

- Dashboard is now an Operations Control Tower: health score, operating mode, business impact summary, and next actions.
- Added `/ops-review` for manager-friendly review: current statement, KPI evidence, high-attention workflows, and audit trail.
- Prioritization is phrased around business impact and operational decisions, not only developer/debugging details.
- Existing workflow actions remain guarded by reason input and audit records.
- Continue-As-New chains remain grouped as a single logical workflow with per-run status/start/close metadata.

