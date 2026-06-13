# TemporalOpsBlazor

TemporalOpsBlazor は、Temporal Workflow を運用管理者・管理職の視点で監視、確認、操作するための Blazor Server ベースの管理コンソールです。
UI は英語で統一し、README / docs は日本語で運用・導入時に読みやすいように整理しています。

このバージョンは、デモ用のモックサービスと、Temporal .NET SDK を使った実Temporal接続の両方に対応しています。

## 主な機能

- Dashboard: Running、Failed、Stuck、Workers、Completion Rate、P95 latency などの管理向けKPIを表示
- Operations Review: 朝会、障害報告、週次レビューで使いやすい1ページ要約
- Workflows: Workflow ID、Run ID、Workflow Type、Task Queue、Status、Riskで検索・絞り込み
- Continue-As-New grouping: 同じ Workflow ID に連なる複数Runを1つの論理Workflowとして表示
- Run chain timeline: Continue-As-New の各Runに Status、Start Time、Close Time、History、Latency を表示
- Workflow Detail: メタデータ、Open Signals、Event History、Input、Memo/Runbook、Run Chainを表示
- Operator Actions: Signal、Cancel、Reset、Terminateを実行可能
- Safety Guard: 影響の大きい操作に対する二段確認、理由入力、監査ログ記録
- Workers: Task QueueごとのPoller、Backlog、Dispatch Rate、Last Heartbeatを表示
- Schedules: ScheduleのPause / Unpause
- Audit: オペレーター操作の履歴確認

## 画面と言語方針

- アプリケーションUI: 英語で統一
- README / docs: 日本語
- 運用担当者や管理職が状況判断しやすいように、UIでは「業務影響」「復旧判断」「次アクション」「監査証跡」に相当する情報を前面に出しています。

## ローカル起動

```bash
cd TemporalOpsBlazor
dotnet restore
dotnet run
```

`dotnet run` の出力に表示されるローカルURLをブラウザで開いてください。

## Temporal接続設定

`appsettings.json` の `Temporal` セクションで設定します。初期値はローカルのTemporal開発サーバーを想定しています。

```json
"Temporal": {
  "UseMock": false,
  "TargetHost": "localhost:7233",
  "Namespace": "default",
  "Identity": "temporal-ops-blazor",
  "ApiKey": "",
  "MonitoredTaskQueues": ["default"],
  "WorkflowPageSize": 50,
  "DashboardPageSize": 8,
  "HistoryEventLimit": 80,
  "StuckWorkflowMinutes": 30,
  "Tls": {
    "Enabled": false,
    "Disabled": false,
    "Domain": "",
    "ClientCertPath": "",
    "ClientKeyPath": ""
  }
}
```

モックサービスへ戻す場合は以下のようにします。

```json
"UseMock": true
```

環境変数でも同じ値を上書きできます。

```bash
export Temporal__UseMock=false
export Temporal__TargetHost=localhost:7233
export Temporal__Namespace=default
```

## Temporal Cloud API Key の設定例

```bash
export Temporal__UseMock=false
export Temporal__TargetHost='your-namespace.account.tmprl.cloud:7233'
export Temporal__Namespace='your-namespace.account'
export Temporal__ApiKey='your-api-key'
export Temporal__Tls__Enabled=true
```

API Keyを指定する場合はTLS利用が前提です。環境によって明示的なTLSドメインが必要な場合は、`Temporal:Tls:Domain` を設定してください。

## mTLS の設定例

```bash
export Temporal__UseMock=false
export Temporal__TargetHost='your-namespace.account.tmprl.cloud:7233'
export Temporal__Namespace='your-namespace.account'
export Temporal__Tls__Enabled=true
export Temporal__Tls__ClientCertPath='/secure/client.pem'
export Temporal__Tls__ClientKeyPath='/secure/client.key'
```

## 実データ対応の構成

- `TemporalClientProvider`: `appsettings.json` / 環境変数から `TemporalClient` を生成し、再利用します。
- `TemporalOperationsService`: `ITemporalOperationsService` の実Temporal接続実装です。
- `Program.cs`: `Temporal:UseMock` に応じてMock/Real実装を切り替えます。
- `Services/MockTemporalOperationsService.cs`: UIデモ、オフライン確認、画面調整用に残しています。

## 実装済みのTemporal操作

- Workflow一覧: `ListWorkflowsAsync` とVisibility Query
- Workflow件数: `CountWorkflowsAsync`
- Workflow詳細: `GetWorkflowHandle(...).DescribeAsync()` と `FetchHistoryEventsAsync()`
- Signal: `SignalAsync`
- Cancel: `CancelAsync`
- Terminate: `TerminateAsync`
- Reset: raw workflow service の `ResetWorkflowExecutionAsync`
- Workers: raw workflow service の `DescribeTaskQueueAsync`
- Schedules: `ListSchedulesAsync`、`PauseAsync`、`UnpauseAsync`

## Continue-As-New grouping

Workflow一覧では、同じ `WorkflowId` を持つ複数のRunを1つの論理Workflow行としてまとめます。

`ContinueAsNew × N` バッジを開くと、古いRunからCurrent Runまでのタイムラインを確認できます。各Runには Status、Start Time、Close Time、History、Latency が表示されます。

Detail画面でも同じRun chainを確認できます。Signal、Cancel、Terminate、Resetなどの操作対象は、集約行のCurrent Runに統一しています。

## 本番運用に向けた強化案

- OIDC / Entra ID / Okta などによる認証
- Namespace、Task Queue、操作種別ごとのRBAC
- Terminate / Reset に対する承認フロー
- Signal payload のJSON Schema検証
- 保存済みTemporal Visibility Query
- 監査ログの永続化
- OpenTelemetry / Prometheus 連携
- Runbook URL、インシデントチケット、PagerDutyなどとの連携

本番利用では、このUIからTemporalへ直接接続するのではなく、バックエンドAPIを挟む構成を推奨します。RBAC、監査、承認、レート制御、操作ポリシーをサーバー側で強制できるためです。
