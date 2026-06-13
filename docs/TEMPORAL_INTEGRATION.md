# Temporal integration guide

この版では `ITemporalOperationsService` の実装を `TemporalOperationsService` に切り替えることで、Temporalの実データを表示・操作できます。

## 接続方式

`Program.cs` で `Temporal:UseMock` を読み、以下のようにDIを切り替えています。

```csharp
var useMock = builder.Configuration.GetValue("Temporal:UseMock", false);
if (useMock)
{
    builder.Services.AddSingleton<ITemporalOperationsService, MockTemporalOperationsService>();
}
else
{
    builder.Services.AddSingleton<ITemporalOperationsService, TemporalOperationsService>();
}
```

## 実Temporal実装の責務

`TemporalOperationsService` は以下を担当します。

- Visibility queryを組み立ててWorkflow一覧を取得
- Workflow handleからDescribe / History / Signal / Cancel / Terminateを実行
- raw gRPC serviceからResetWorkflowExecution / DescribeTaskQueueを実行
- Schedule一覧取得とPause / Unpauseを実行
- UI操作結果をインメモリAudit logに記録

## 設定例: local dev server

```json
"Temporal": {
  "UseMock": false,
  "TargetHost": "localhost:7233",
  "Namespace": "default",
  "MonitoredTaskQueues": ["default"]
}
```

## 設定例: Temporal Cloud API Key

```json
"Temporal": {
  "UseMock": false,
  "TargetHost": "your-namespace.account.tmprl.cloud:7233",
  "Namespace": "your-namespace.account",
  "ApiKey": "<secret>",
  "Tls": {
    "Enabled": true
  }
}
```

## 設定例: mTLS

```json
"Temporal": {
  "UseMock": false,
  "TargetHost": "your-namespace.account.tmprl.cloud:7233",
  "Namespace": "your-namespace.account",
  "Tls": {
    "Enabled": true,
    "ClientCertPath": "/secure/client.pem",
    "ClientPrivateKeyPath": "/secure/client.key"
  }
}
```

## 注意点

- `HistoryEventLimit` を大きくしすぎると詳細画面が重くなります。
- `Reset` は `WorkflowTaskFinishEventId` を指定します。誤操作防止のためUI側ではWorkflow ID確認入力を必須にしています。
- `DescribeTaskQueue` のWorkflow Task Queue統計はsticky queueを含まない場合があるため、Backlog/Dispatch rateは運用判断の補助指標として扱ってください。
- このサンプルのAudit logはインメモリです。本番ではDBや監査基盤へ永続化してください。
