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
