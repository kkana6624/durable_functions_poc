# 受付・再試行・期限超過の改善

更新日: 2026-10-17

本書は、公式ドキュメントおよび利用中のSDKソースコードの調査結果に基づき実装した、ワークフローの受付制御・再試行・期限超過処理およびデプロイ制御の設計書です。

> ※本書に記載の検証内容はローカル環境（Mock / Fake）で確認したものであり、Azure実環境での動作実証については末尾の「実Azureで残る検証」にて今後実施します。

---

## 1. 同一 `requestId` の受付制御

### 処理の流れ

1. **入力の照合と確定（HTTP Starter）**
   - HTTP starterは既存インスタンスの入力内容を照合したうえで、`runs/poc-<requestId>/request.json` をBlobの条件付き作成によって確定します。
   - 同一の `requestId` に対して異なる入力が送信された場合は `409 Conflict` を返します。
   - 受付確認時に一時的な障害が発生した場合は、`503 Service Unavailable` と `Retry-After` ヘッダーを返します。
   - 入力内容をBlobに永続化するため、起動前にプロセスが停止したりHTTP応答が失われたりした場合でも、クライアントは同じ入力で安全にリクエストを再送できます。

2. **Entityによる起動制御（`WorkflowAdmissionEntity`）**
   - 同一入力であることが確認できた場合、同じキーを持つ `WorkflowAdmissionEntity` に対して `Start` をsignalします。
   - HTTP starterが返す `202 Accepted` は「signalを永続キューへ投入し受理したこと」を示します。そのため、Mainオーケストレーションが登録される前に状態照会（GET）を行うと、一時的に `404 Not Found` が返る場合があります。
   - Entityの操作内では外部I/Oを行わず、未起動の場合にのみMainオーケストレーションの起動Actionと「起動済み」の状態を生成します。再送されたsignalを受信した場合は、保存済みの状態を照合してそのまま終了します。
   - Entityの状態更新と送信Actionは同一のdurable batchで処理され、障害発生時のメッセージ再配信はStorageプロバイダーによって制御されます（※Azure StorageのQueueとTableをまたぐ単一の物理トランザクションではありません）。

### 設計上の判断とSDK仕様の確認

調査の過程で確認したSDKの仕様と、それに基づく設計方針は以下の通りです。

- **単純な状態確認＋起動の競合**: `GetInstance` の後に `ScheduleNew` を呼ぶだけの実装では、同時受付時に競合が発生します（参考: [公式singleton説明](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-singletons)）。
- **`DedupeStatuses` とBlob Leaseの限界**: SDKの `DedupeStatuses` は利用可能ですが、Azure Storageプロバイダーにおける状態照会と起動キュー投入は原子的ではありません。また、Blob leaseを用いたとしても外部の起動APIに対するfencingにはならず、応答喪失時の完全な対策にはなりません（参考: [Storageプロバイダー実装](https://github.com/Azure/durabletask/blob/main/src/DurableTask.AzureStorage/AzureStorageOrchestrationService.cs)）。
- **SDK 1.24.1におけるEntityからの起動挙動**: 利用中のSDK 1.24.1では、Entityからのオーケストレーション起動はActionをバッチへ追加する仕組みであり、例外発生時は状態とActionがともにロールバックされます。この経路は `DedupeStatuses` を転送しないため、本実装では `DedupeStatuses` を設定せず、Entity自身の保存状態によって起動を制御しています（参考: [利用版のEntity shim](https://github.com/microsoft/durabletask-dotnet/blob/v1.24.1/src/Worker/Core/Shims/TaskEntityShim.cs)）。
- **直列処理と重複抑止の組み合わせ**: 上記を踏まえ、Entityの直列処理とランタイム側の起動メッセージ重複抑止を組み合わせて一意性を担保しています（参考: [公式Entity説明](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-entities)、[Core Entity dispatcher](https://github.com/Azure/durabletask/blob/main/src/DurableTask.Core/TaskEntityDispatcher.cs)、[Core起動重複処理](https://github.com/Azure/durabletask/blob/main/src/DurableTask.Core/TaskOrchestrationDispatcher.cs)）。

### 前提条件と運用上の制約

- 入力Blob・Entity状態・Main履歴を保持し、本HTTP API経由でリクエストを受け付けることを前提とします。
- 以下の操作や構成はサポート対象外です。
  - 個別のインスタンスや状態のpurge
  - 管理API等からの同一IDでの直接起動
  - 旧バージョンのstarterとの同時稼働（旧版からの更新は、既存の処理がすべて終了した後に実施してください）
  - Entity状態を削除して同一の `requestId` を再利用する清掃手順
- なお、Entityの利用にあたって追加の同時実行数制限を `host.json` に設定する必要はありません（参考: [host設定](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-host-json-settings)）。

---

## 2. 一時障害と監視期限（タイムアウト）

### 再試行（Retry）の設計

- **Durableレイヤーの再試行**: Activityは一時障害のみを `TransientWorkflowException` へ変換し、Durable retry handlerが「最大3試行」および「共有deadline（監視期限）内であること」を確認します。待機時間はDurable Timerで5秒・10秒です。入力不正、認証・権限エラー、競合、結果不整合などの恒久エラーは再試行しません。
- **SDKレイヤーの再試行とタイムアウト**: SDK側は再試行1回、1回の通信試行あたりの `NetworkTimeout` は10秒とします。SDKによる再試行とDurableによる再試行の両方が行われるため、総通信試行数が3回とは限りません。
- **例外型の判定とキャンセルトークンの伝播**:
  - isolated workerの `TaskFailedException` で元の例外型を確認するには、`FailureDetails.IsCausedBy<T>()` を使用します（参考: [公式エラー処理](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-error-handling)）。
  - `Azure.Core` は通信再試行を使い切ると `AggregateException` を返す場合があるため、内部例外の全件が一時障害の場合のみ再試行します。
  - `NetworkTimeout` はlinked tokenによる `OperationCanceledException` として返ります。呼び出し元tokenをHTTP受付・Activity・SDK操作まで引き渡し、呼び出し元がcancel済みであれば再試行せず、それ以外のSDK通信cancelを一時障害として扱います。エラーメッセージの文字列判定は使用しません（参考: [SDK RetryPolicy](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.54.0/sdk/core/Azure.Core/src/Pipeline/RetryPolicy.cs)、[利用系列のResponseBodyPolicy](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.54.0/sdk/core/Azure.Core/src/Pipeline/Internal/ResponseBodyPolicy.cs)）。

### 監視期限とBatch Jobの停止処理

- **期限の確定と監視**: 監視期限は分類処理の完了後に親オーケストレーターで確定し、子オーケストレーターへ渡します。Task作成・状態照会Activityとdeadline Timerを競合させ、期限到達時点で親へ `TimedOut` を返します。
- **Activity実行中の競合対策**: Durable Timerは実行中のActivityを物理キャンセルしないため、Jobを終端化（Terminate）することで、遅れて実行されるTask追加をBatch側で拒否させます。ただし、Job作成Activityの待機を途中で放棄すると、停止処理の後にJobが新規作成される時間窓が生じるため、Job作成Activityだけは完了を待ちます（参考: [公式Timer説明](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-timers)）。
- **Terminate要求と停止確認**: 期限超過時は対象JobのID・Pool・fingerprintを照合してTerminateを要求し、5秒間隔・最大2分でJobが `Completed` 状態になることを確認します。すでに完了済み・停止中のJobに対する再要求は冪等に扱います。万が一停止確認に失敗した場合も、ワークフローの状態を成功にはせず、`customStatus.termination` と `terminationError` を残し、Manifestを公開しません。なお、未開始のTaskはJob停止後も `Active` のまま残るため、全Taskの `Completed` 待ちは停止確認には使用しません（参考: [Terminate Job API](https://learn.microsoft.com/en-us/rest/api/batchservice/jobs/terminate-job?view=rest-batchservice-2025-06-01)、[Batchエラーコード](https://learn.microsoft.com/en-us/rest/api/batchservice/batch-status-and-error-codes)）。
- **Batch側の安全網（`MaxWallClockTime`）**: Functionsホスト停止中の安全網として、Batch Job自体に `MaxWallClockTime` を設定します。これはJob作成時点からの起算であり、オーケストレーション側の共有期限とは異なります。また、Job release等を含む停止処理の時間も要するため、期限時刻ちょうどでの物理停止やワークフロー全体の厳密な実時間上限を保証するものではありません。なお、利用者によるDurableキャンセル操作のBatchへの連鎖は今回の実装範囲外です。

---

## 3. 配置制御（デプロイ・Smokeテスト）

- **Terraform BackendのPlanOnly制御**: `Backend.Create=true` での `PlanOnly` 実行時は、存在確認が成功してbackend作成済みと判明すれば、bootstrapを適用せずmainの計画（plan）へ進みます。未作成であればbootstrapの計画のみを実行します。存在確認時のエラーを「不存在」として握りつぶすことはしません（参考: [AzureRM backend](https://developer.hashicorp.com/terraform/language/backend/azurerm)、[container exists](https://learn.microsoft.com/en-us/cli/azure/storage/container?view=azure-cli-latest)）。
- **既存Batch Pool（0台構成）への対応**: 既存Poolは稼働ノードが0台でも配置可能です。ノードの存在をTask投入の前提条件にはせず、SmokeテストではTaskを投入して完了を待ちます。既存Poolの自動拡張（autoscale）設定は変更しません（参考: [Batch自動拡張](https://learn.microsoft.com/en-us/azure/batch/batch-automatic-scaling)）。
- **新規Batch Poolのノード待機**: 新規Pool作成時は、設定台数分の利用可能ノードが揃うのを待ちます。`odata.nextLink` を辿って全ページを集計し、他ホストや循環する継続URLが返された場合は拒否します（参考: [Node一覧API](https://learn.microsoft.com/en-us/rest/api/batchservice/nodes/list-nodes?view=rest-batchservice-2025-06-01)）。
- **Functionsの検出と状態判定**: デプロイ後のFunctions検出対象は12件です。状態GETの `404` は「起動待ち」、`401` / `403` は「認証・権限失敗」として扱います（参考: [Durable HTTP API](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-http-api)）。

---

## 4. 実Azureで残る検証

ローカルのMockテストでは、アプリケーションの分岐ロジックとAPI呼び出しの組み立てを確認しています。実Blobにおける同時条件付き書き込み、Entityバッチのクラッシュ復旧、実Batchの停止処理や権限エラーの伝播については、以下の実Azure試験にて別途実証します。

1. 同一入力の同時POSTおよび異なる入力の同時POSTにおいて、Mainオーケストレーションが1件のみ起動し、入力競合が `409` となること。
2. 入力Blob作成後・Entity受理前後・Entityバッチ確定前後の各タイミングでhostを停止し、再送と復旧が正常に機能すること。
3. Task投入・状態照会中の期限到達でJobが `Completed` となり、Manifestが公開されないこと。あわせて停止権限不足時の挙動も確認すること。
4. host停止中の `MaxWallClockTime` によるJob終端化と、0台autoscale PoolへのSmokeテストが完了すること。
5. 一時的な障害で再試行が行われ、権限エラー・競合・結果不整合では再試行されないこと。
