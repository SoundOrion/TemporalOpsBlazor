# Temporal連携メモ

このプロジェクトは、`ITemporalOperationsService` の実装をモックから `TemporalOperationsService` へ切り替えることで、実Temporal環境のデータ表示と操作に対応します。

アプリケーションUIは英語で統一し、このドキュメントは導入・保守担当者向けに日本語で記載しています。

## DIの切り替え

`Program.cs` では `Temporal:UseMock` を読み取り、モックサービスまたは実Temporal接続サービスを登録します。

```csharp
if (temporalSettings.UseMock)
{
    builder.Services.AddSingleton<ITemporalOperationsService, MockTemporalOperationsService>();
}
else
{
    builder.Services.AddSingleton<TemporalClientProvider>();
    builder.Services.AddSingleton<ITemporalOperationsService, TemporalOperationsService>();
}
```

## 実Temporalサービスの責務

`TemporalOperationsService` は主に以下を担当します。

- Workflow検索用のVisibility Query生成
- Workflow summary、description、history の取得
- Continue-As-New chain の取得と集約
- Signal、Cancel、Terminate、Reset の実行
- ResetWorkflowExecution と DescribeTaskQueue のための raw gRPC service 呼び出し
- Schedule一覧取得とPause / Unpause
- UI上の操作結果を監査ログへ記録

## ローカル開発サーバーの設定例

```json
"Temporal": {
  "UseMock": false,
  "TargetHost": "localhost:7233",
  "Namespace": "default",
  "MonitoredTaskQueues": ["default"]
}
```

## Temporal Cloud API Key の設定例

```bash
export Temporal__UseMock=false
export Temporal__TargetHost='your-namespace.account.tmprl.cloud:7233'
export Temporal__Namespace='your-namespace.account'
export Temporal__ApiKey='your-api-key'
export Temporal__Tls__Enabled=true
```

## mTLS の設定例

```bash
export Temporal__UseMock=false
export Temporal__TargetHost='your-namespace.account.tmprl.cloud:7233'
export Temporal__Namespace='your-namespace.account'
export Temporal__Tls__Enabled=true
export Temporal__Tls__ClientCertPath='/secure/client.pem'
export Temporal__Tls__ClientKeyPath='/secure/client.key'
```

## Continue-As-New の扱い

Temporalでは Continue-As-New により、同じWorkflow IDのまま新しいRunへ継続されるケースがあります。

このUIでは、同一 `WorkflowId` のRunを1つの論理Workflowとして集約します。Workflow一覧では `ContinueAsNew × N` として表示し、展開すると各Runの Status、Start Time、Close Time、History、Latency を確認できます。

運用上の操作対象はCurrent Runに統一しています。これにより、古いRunに対して誤ってSignalやTerminateを行うリスクを下げます。

## 運用上の注意

- `HistoryEventLimit` を大きくしすぎると、詳細画面が重くなる可能性があります。
- Resetでは `WorkflowTaskFinishEventId` を利用します。誤操作防止のため、UIではWorkflow IDの確認入力を必須にしています。
- `DescribeTaskQueue` の統計値はsticky queueを含まない場合があるため、BacklogやDispatch Rateは絶対値ではなく運用判断の目安として扱ってください。
- サンプルの監査ログはインメモリです。本番ではDBや監査基盤へ永続化してください。
- 本番ではUIからTemporalへ直接接続するより、バックエンドAPIを挟んでRBAC、承認、監査、レート制限を強制する構成を推奨します。

## 本番導入時に追加検討したい項目

- OIDC / Entra ID / Okta などによるログイン統合
- Namespace、Task Queue、Workflow Type、操作種別ごとの権限制御
- Terminate / Reset の承認ワークフロー
- Signal payload のJSON Schema検証
- 操作ログの長期保存と検索
- OpenTelemetry / Prometheus による外部監視連携
- Runbook、インシデント管理、オンコール通知との連携

## Motion View の実装方針

`WorkflowMotionView.razor` は、Workflow Historyを直接そのまま表示するのではなく、`WorkflowMotion` DTOへ変換した結果を表示します。

実Temporal接続では `FetchHistoryEventsAsync` でHistory Eventを取得し、以下のカテゴリに正規化します。

- Run segment: Continue-As-Newでつながる各Run
- Child workflow segment: ChildWorkflowExecutionStarted / Completed / Failed / TimedOut など
- Activity segment: ActivityTaskScheduled / Started / Completed / Failed / TimedOut など
- Marker: Continue-As-New、Signal、Timer、異常終了など
- Finding: 運用判断向けの異常ハイライト

表示は1本の巨大なタイムラインではなく、RunごとのMotionカードを中心にしています。各Runカードの中に、そのRunで発生したActivity、Child Workflow、Signal/Timerを重ねるため、Continue-As-Newが多いWorkflowでも粒が潰れにくくなります。Child Workflowも個別カードとして表示し、必要に応じて詳細画面へ遷移できます。

Motion Viewは「デバッグ用の完全なイベント羅列」ではなく、「現在どこで止まっているか」「どこが業務影響の起点か」「次に何を確認すべきか」を見せるためのビューです。詳細な全イベントは従来のEvent timelineで確認できます。

## Workflow一覧のMotion Preview

Workflow一覧の展開行では、単なるContinue-As-NewのRun一覧ではなく、対象WorkflowごとのMotion Previewを表示します。

- 各Continue-As-New Runは独立したWorkflow executionカードとして表示します。
- ActivityはRunカード内の主要セグメントとして表示します。
- Child Workflowは親Runの下に別カードとして表示し、クリックで該当Workflowの詳細へ遷移します。
- 一覧表示時点では詳細Historyを取得せず、行を展開したタイミングで遅延取得します。

この構成により、運用管理者は「同じWorkflow IDに属するRun全体」ではなく、「各Workflow executionで実際に何が起きたか」を確認できます。

## v13: 詳細画面の粒度

運用画面では、Continue-As-New でつながる Workflow ID をひとつの業務単位として扱います。ただし、調査や説明責任では個別 Run 単位の確認も必要になるため、詳細画面を2段構成にしています。

- Grouped Detail: `/workflows/{workflowId}`
  - Workflow ID 全体の運用判断、Current Run、Run chain、Child Workflow を確認します。
- Run Detail: `/workflows/{workflowId}/runs/{runId}`
  - 特定 Run の Motion View と Event History を確認します。

一覧の展開プレビューにも Run detail / Child detail の導線を表示しています。

## v14: Continue-As-NewとRun Detailの表示方針

運用一覧では、Continue-As-Newで連なるRunをツリー状の軽量サマリとして表示します。
一覧ではモーション表示を行わず、Status / Start Time / Close Time / History / Latency を確認できるようにしています。

個別Runの `Details` リンクからRun Detailへ遷移すると、そのRun単体のHistoryを元にMotion Viewを表示します。
これにより、一覧は監視・絞り込み・状況把握に集中し、詳細画面は個別Executionの解析に集中する構成になります。
