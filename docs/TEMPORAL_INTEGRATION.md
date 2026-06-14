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

## Workflow一覧の軽量Run Chain表示

Workflow一覧の展開行では、Continue-As-Newで連なるRunをツリー状の軽量サマリとして表示します。一覧ではMotionを描画せず、Status / Start Time / Close Time / History / Latency と `Run details` 導線だけを見せます。

Run単体のMotion確認は `/workflows/{workflowId}/runs/{runId}` に分離しています。これにより、一覧画面は監視・比較・絞り込みに集中し、詳細画面は個別Executionの解析に集中できます。

## v13: 詳細画面の粒度

運用画面では、Continue-As-New でつながる Workflow ID をひとつの業務単位として扱います。ただし、調査や説明責任では個別 Run 単位の確認も必要になるため、詳細画面を2段構成にしています。

- Grouped Detail: `/workflows/{workflowId}`
  - Workflow ID 全体の運用判断、Current Run、Run chain、Child Workflow を確認します。
- Run Detail: `/workflows/{workflowId}/runs/{runId}`
  - 特定 Run の Motion View と Event History を確認します。

一覧の展開行には、各Runの `Run details` 導線を表示しています。

## v14: Continue-As-NewとRun Detailの表示方針

運用一覧では、Continue-As-Newで連なるRunをツリー状の軽量サマリとして表示します。
一覧ではモーション表示を行わず、Status / Start Time / Close Time / History / Latency を確認できるようにしています。

個別Runの `Details` リンクからRun Detailへ遷移すると、そのRun単体のHistoryを元にMotion Viewを表示します。
これにより、一覧は監視・絞り込み・状況把握に集中し、詳細画面は個別Executionの解析に集中する構成になります。


## v15: UIポリッシュ

画面思想はv14のまま、色・余白・文言・リンク導線を調整しています。

- 一覧は軽量Run Chain表示を維持
- Workflow行の `Details` はGrouped Detailへ遷移
- Run行の `Run details` は個別Run Motionへ遷移
- ボタンの意味が分かるようにラベルとtitleを整理
- Continue-As-Newツリーの行間、現在Runの強調、hover/focus状態を調整
- 背景グラデーションとカード影を少し抑え、管理画面として読みやすい配色に調整
- キーボード操作時のfocus-visibleを追加

## v16: 監視中の展開状態とソート順

Workflowsページでは、リアルタイム監視時の視認性を優先し、ユーザーが開いたContinue-As-New chainの展開状態を更新後も保持します。

- Continue-As-New chainは初期状態では折りたたみです。ユーザーが開いたchainは更新後も開いたまま維持されます。
- 自動更新や手動更新後も、画面の展開状態を保ったまま再描画します。
- Workflow一覧のソートは、Workflow全体の最古Start Timeではなく、Current RunのStart Timeを基準にします。
- chain内のRunもCurrent Runを先頭に表示し、その後は新しいRunから古いRunの順で表示します。

これにより、Continue-As-Newが多いWorkflowでも、監視者は現在動いているRunや直近Runをスクロールせず確認できます。

## v18: Execution Treeの表示統一

Workflowsページの展開行は、Continue-As-New専用の表示ではなく、すべてのWorkflowで使えるExecution Treeとして扱います。

- Continue-As-Newあり: Current Runを先頭に、複数Runをツリー状に表示します。
- Continue-As-Newなし: Current executionを1件だけ同じツリー形式で表示します。
- 展開状態は `WorkflowId` をキーに保持するため、Auto refresh後もユーザーが開いたツリーは閉じません。
- Run単位の詳細確認はすべて `Run details` から `/workflows/{workflowId}/runs/{runId}` に遷移します。

一覧ではHistoryやMotionを直接描画せず、監視に必要な最小限のExecution metadataだけを統一フォーマットで表示します。
