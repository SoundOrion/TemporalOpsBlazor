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

## Workflow Motion View

Workflow Detail には `Execution Motion` を追加しています。これはTemporal標準UIのEvent Historyをそのまま複製するのではなく、運用管理者が判断しやすいように **Workflow / Run単位のMotionカード** として表示するビューです。

- Run motion cards: Continue-As-Newでつながる各Runを1枚ずつ表示
- Child workflow cards: Parentから起動されたChild Workflowを個別カード表示
- Activities: 各Runの中で発生したActivityを、そのRunカード内に表示
- Signals / timers: 必要に応じてLevel 3で表示
- Findings: 長時間実行、失敗、異常なContinue-As-New連鎖などを運用判断向けに表示

再生、停止、スクラブ、問題箇所へのジャンプ、表示粒度切り替えを備えています。全体を1本の巨大なタイムラインに押し込まず、各Workflow/Runで何が起きたかを読める構成にしています。

## Workflow一覧の軽量Run Chain表示

Workflow一覧では、Continue-As-Newで連なるRunをツリー状の軽量サマリとして表示します。一覧上ではMotionを描画せず、監視・比較・絞り込みを邪魔しない密度にしています。

- Run ID
- Status
- Start Time
- Close Time
- History
- Latency
- Run detailsリンク

各Runの `Run details` から `/workflows/{workflowId}/runs/{runId}` へ遷移し、そのRun単体のMotion Viewを表示します。Workflow行本体の `Details` は `/workflows/{workflowId}` に遷移し、Workflow ID全体のGrouped Detailを表示します。

## v13: Workflow Detail navigation policy

画面文言は英語で統一したまま、Workflow Detail の導線を以下の方針に整理しています。

- 一覧の `Details` は Workflow ID 単位の **Grouped Workflow Detail** を開きます。
- Continue-As-New の各 Run には `Run detail` リンクを表示します。
- Child Workflow には `Child detail` リンクを表示します。
- `/workflows/{workflowId}` はグループ全体の運用判断用ページです。
- `/workflows/{workflowId}/runs/{runId}` は個別 Run の Motion / History を確認するページです。

これにより、管理者はまずグループ全体の状態を見て、必要な Run や Child Workflow へ個別に掘り下げられます。

## v14: Workflow一覧とMotion詳細の導線

Workflow一覧の展開表示は、Continue-As-New run chainをツリー状に表示する軽量ビューに戻しています。
ここでは各Runの `Status`、`Start Time`、`Close Time`、`History`、`Latency` のみを確認できます。

各Runの `Details` を押すと `/workflows/{workflowId}/runs/{runId}` に遷移し、そのRun単体のMotion Viewを表示します。
一覧上ではモーションを大量に描画せず、運用者が必要なRunだけを選んで詳細確認できる構成です。

Workflow行本体の `Details` は `/workflows/{workflowId}` に遷移し、Workflow ID全体のGrouped Detailを表示します。
Grouped Detailでは全体概要とRun navigatorを表示し、各Runのモーション確認はRun Detail側に分離しています。


## v15: UIポリッシュ

画面思想はv14のまま、色・余白・文言・リンク導線を調整しています。

- 一覧は軽量Run Chain表示を維持
- Workflow行の `Details` はGrouped Detailへ遷移
- Run行の `Run details` は個別Run Motionへ遷移
- ボタンの意味が分かるようにラベルとtitleを整理
- Continue-As-Newツリーの行間、現在Runの強調、hover/focus状態を調整
- 背景グラデーションとカード影を少し抑え、管理画面として読みやすい配色に調整
- キーボード操作時のfocus-visibleを追加

## v16: リアルタイム監視向けWorkflow一覧

Workflowsページは、運用監視で使いやすいように以下を追加しています。

- `Auto refresh`: 10秒間隔で一覧を自動更新します。
- Continue-As-Newのツリーは初期状態では折りたたみです。ユーザーが展開したツリーは、Auto refresh後も展開状態を維持します。
- ソート順: 最新のCurrent Runを持つWorkflowが上位に来ます。
- Run chain表示: Current Runを先頭に出し、その後は新しいRunから古いRunへ並べます。

一覧では引き続きMotionを描画せず、Run ID / Status / Start Time / Close Time / History / Latency / Run details だけを表示します。リアルタイム監視では、一覧で最新状態を追い、必要なRunだけ `Run details` からMotion Viewへ遷移する構成です。

## v18: Workflow一覧のExecution Tree統一

Workflowsページでは、Continue-As-NewがあるWorkflowだけでなく、単一RunのWorkflowでも同じExecution Treeを展開できるようにしています。

- 初期状態ではすべて折りたたみです。
- ユーザーが展開したExecution Treeは、自動更新・手動更新後も展開状態を維持します。
- Continue-As-Newがある場合は複数Runを表示します。
- Continue-As-Newがない場合も、Current executionを1件のRun rowとして同じフォーマットで表示します。
- 一覧上の表示項目は Run ID / Status / Start Time / Close Time / History / Latency / Run details に統一しています。

これにより、Workflowの種類によって一覧の見え方や操作導線が変わらず、監視担当者は同じ目線でCurrent Runと詳細導線を確認できます。

## v19 Activity visibility improvement

Run Detail の Execution Motion では、Activities を 1 本の密集した横バーにまとめず、Activity ごとの行として表示するように変更しました。各行には Start / End / Duration / Event ID / Status と小さなローカルタイムラインを表示します。これにより、同一Run内でどのActivityが遅いか、失敗しているか、どのEvent範囲に対応するかを運用者が読み取りやすくなります。
