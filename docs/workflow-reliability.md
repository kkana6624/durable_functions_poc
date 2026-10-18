# ワークフロー信頼性設計 — 受付制御・再試行・タイムアウト処理

更新日: 2026-10-18

本書は、公式ドキュメントおよび利用中SDKのソースコード解析に基づき設計・実装した、ワークフローの同時受付排他制御・一時障害リトライ・監視期限超過（タイムアウト）処理およびデプロイ制御の設計仕様書です。

> **注意:** 本書に記載された設計は、ローカル開発環境（Mock / Fake）に加え、実 Azure 環境（Azure Storage バックエンドおよび Durable Task Scheduler バックエンド）にて主要シナリオ（正常系・競合 `409`・監視期限超過 `V07`・Function App 再起動復旧 `V05`・Function ホスト停止中の Batch `MaxWallClockTime` 安全網 `V08`・0 台オートスケール Pool 先行投入 `V09`）の動作実証を完了しています。実機検証の到達点と残る確認事項は「4. 実Azureでの検証到達点と残る確認事項」および [信頼性の追加検証](reliability-validation.md) を参照してください。

---

## 1. 同一 `requestId` の受付制御

### 処理の流れ

1. **入力の照合と確定（HTTP Starter）**
   - HTTP Starter は、既存インスタンスの入力内容を照合した上で、Blob Storage の条件付き作成（アトミックな作成）を用いて `runs/poc-<requestId>/request.json` を確定します。
   - 同一の `requestId` に対して異なる入力内容が送信された場合は、リクエスト競合として `409 Conflict` を返却します。
   - 受付確認時（Blob や Entity との通信時）に一時的な障害が発生した場合は、`503 Service Unavailable` と `Retry-After` ヘッダーをクライアントへ返却します。
   - 入力内容をあらかじめ Blob Storage へ永続化することで、オーケストレーション起動前に Functions プロセスが停止したり HTTP 応答がネットワーク上で失われた場合でも、クライアントは同一内容で安全にリクエストを再送できます。

2. **Durable Entities による起動制御（`WorkflowAdmissionEntity`）**
   - リクエスト入力の同一性が確認できた場合、`requestId` と同一のキーを持つ `WorkflowAdmissionEntity` に対して `Start` 操作をシグナル送信（`SignalEntityAsync`）します。
   - HTTP Starter が返却する `202 Accepted` は、「起動シグナルが Durable の永続キューへ正常に投入され、リクエストが受理されたこと」を示します。そのため、Main オーケストレーションのインスタンスが実際に登録・開始される前に状態照会 API（GET）を実行すると、一時的に `404 Not Found` が返却される場合があります。
   - `WorkflowAdmissionEntity` の操作内では外部 I/O を一切行わず、未起動である場合にのみ Main オーケストレーションの起動 Action と「起動済み」内部状態を生成します。同一リクエストの再送シグナルを受信した場合は、保存済みの状態と照合して追加のアクションを起こさずに正常終了します。
   - Entity の状態更新と送信 Action は同一の Durable バッチ内で一括処理され、障害発生時のメッセージ再配信は基盤の Storage プロバイダーによって安全に制御されます（※Azure Storage の Queue と Table をまたぐ単一の物理分散トランザクションではない点に留意）。

### 設計上の判断とSDK仕様の確認

調査の過程で確認した Durable Functions SDK の仕様と、それに基づく設計方針は以下の通りです。

- **単純な状態確認＋起動における競合リスク**: `GetInstanceAsync` で状態を確認した直後に `ScheduleNewOrchestrationInstanceAsync` を呼び出すナイーブな実装では、同時リクエスト受信時に TOCTOU（Time-of-check to time-of-use）の競合が発生し、重複起動を完全に防ぐことができません（参考: [公式singleton説明](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-singletons)）。
- **`DedupeStatuses` と Blob Lease の限界**: SDK には `DedupeStatuses` オプションが存在しますが、Azure Storage プロバイダーの実装においてインスタンス状態の照会と起動キューへのメッセージ投入はアトミック（原子的）には行われません。また、Blob Lease による排他制御を導入しても、外部からの起動 API 呼び出しに対する完全なフェンシング（Fencing）としては機能せず、HTTP 応答喪失時の再送処理に対する根本解決にはなりません（参考: [Storageプロバイダー実装](https://github.com/Azure/durabletask/blob/main/src/DurableTask.AzureStorage/AzureStorageOrchestrationService.cs)）。
- **SDK 1.24.1 における Entity からのオーケストレーション起動挙動**: 本プロジェクトで利用している Durable Task .NET SDK 1.24.1 では、Entity からのオーケストレーション起動は内部的に Action をバッチへ追加する仕組みとなっており、処理中に例外が発生した場合は内部状態と Action の双方がロールバックされます。ただし、この経路では `DedupeStatuses` 設定が伝播されない仕様であるため、本設計では `DedupeStatuses` に依存せず、Entity 自身が保持する内部状態によって起動の冪等性を制御しています（参考: [利用版のEntity shim](https://github.com/microsoft/durabletask-dotnet/blob/v1.24.1/src/Worker/Core/Shims/TaskEntityShim.cs)）。
- **Entity による直列化とランタイム重複抑止の併用**: 同一キーの Entity の直列処理と、ランタイムの起動メッセージ重複抑止を併用する設計です。入力 Blob・Entity 状態・実行履歴を保持し、本 API を経由することを前提とします。ただし、実 Azure で全世代の起動履歴を網羅した観測はできておらず、厳密な起動一意性を実証済みとはしません。Azure Storage 向けの実装ソースだけを DTS の内部トランザクション保証の根拠にもしません（参考: [公式Entity説明](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-entities)、[Core Entity dispatcher](https://github.com/Azure/durabletask/blob/main/src/DurableTask.Core/TaskEntityDispatcher.cs)、[Core起動重複処理](https://github.com/Azure/durabletask/blob/main/src/DurableTask.Core/TaskOrchestrationDispatcher.cs)）。

### 前提条件と運用上の制約

- 本設計は、入力 Blob・Entity 内部状態・Main オーケストレーション実行履歴を適切に保持し、専用の HTTP API を経由してリクエストを受け付ける運用を前提とします。
- 以下の運用操作や構成はサポート対象外（非推奨）とします。
  - 個別のオーケストレーションインスタンスや Entity 状態の任意 purge（パージ）
  - Durable 管理 API 等から同一インスタンス ID を直接指定した強制起動
  - 旧バージョンの Starter と新バージョンの Starter の同時稼働（新バージョンへの切り替えは、先行する実行中ワークフローがすべて完了した後に実施）
  - Entity 内部状態を手動削除して同一の `requestId` を再利用するクリーンアップ運用
- なお、Entity の利用にあたって追加の同時実行数制限を `host.json` に設定する必要はありません（参考: [host設定](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-host-json-settings)）。

---

## 2. 一時障害と監視期限（タイムアウト）

### 再試行（Retry）の設計

- **Durableレイヤーの再試行**: Activity 関数は既存の一時障害分類（ネットワーク瞬断、503、スロットリング等）を `TransientWorkflowException` へ変換し、Durable の再試行ハンドラーが「最大3回の試行」かつ「ワークフロー共有の deadline（監視期限）内であること」を確認した上でリトライします。待機時間は Durable Timer により5秒、10秒と設定されます。入力バリデーション不正、HTTP/Azure SDKの401/403、リクエスト競合（409）、結果データ不整合などは恒久エラーとして再試行しません。ただし、Activity側の既存分類は全 `Grpc.Core.RpcException` を一時障害とします。この分類の見直しは別の残課題であり、独自HTTP API側の限定されたgRPC再試行分類とは異なります。
- **SDKレイヤーの再試行とタイムアウト**: Azure SDK 側のクライアント再試行は1回、1回の通信試行あたりの `NetworkTimeout` は10秒に設定します。SDK レイヤーでの再試行と Durable レイヤーでの再試行が階層的に実行されるため、総通信試行数が単純に3回とはならない点に留意します。
- **例外型の判定とキャンセルトークンの伝播**:
  - .NET Isolated Worker の `TaskFailedException` から基底の例外型を判定する際は、`FailureDetails.IsCausedBy<T>()` を使用します（参考: [公式エラー処理](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-error-handling)）。
  - 利用SDK 1.24.1では、この判定は `ErrorType` の型解決に基づき、messageやinnerの内容を使いません。Activity wrapperは既存条件で分類後、固定message・innerなしの `TransientWorkflowException` / `TimeoutException` / `PermanentWorkflowException` へ変換します。caller取消はcaller tokenを維持した安全な `OperationCanceledException` を渡します。元のSDK例外・Data・独自プロパティをコピーしません（参考: [利用版TaskFailureDetails](https://github.com/microsoft/durabletask-dotnet/blob/v1.24.1/src/Abstractions/TaskFailureDetails.cs)）。この範囲はwrapperから外へ渡す例外であり、全ランタイムログ、Activity入出力や別のDurable経路の非露出保証ではありません。
  - `Azure.Core` は通信リトライを使い切った際に `AggregateException` をラップして返す場合があるため、含まれる内部例外の全件が一時障害に該当する場合にのみ再試行対象と判定します。
  - `NetworkTimeout` はリンクされたトークンによる `OperationCanceledException` として発生します。呼び出し元の `CancellationToken` を HTTP 受付から Activity、SDK 操作まで正しく引き渡し、呼び出し元自身がキャンセルされた場合は再試行を中止し、それ以外の SDK 通信タイムアウトによるキャンセルのみを一時障害として扱います。エラーメッセージの文字列マッチングによる判定は使用しません（参考: [SDK RetryPolicy](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.54.0/sdk/core/Azure.Core/src/Pipeline/RetryPolicy.cs)、[利用系列のResponseBodyPolicy](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.54.0/sdk/core/Azure.Core/src/Pipeline/Internal/ResponseBodyPolicy.cs)）。

### 監視期限とBatch Jobの停止処理

- **期限の確定と監視**: 監視期限（deadline）は、ワークロード分類処理の完了後に親オーケストレーターで確定し、子オーケストレーターへ伝播します。各子オーケストレーターでは Task 投入・状態照会 Activity と deadline Timer を競合させ、期限到達時点で親オーケストレーターへ `TimedOut` ステータスを返却します。
- **Activity実行中の競合対策**: Durable Timer は実行中の Activity を物理的に中断・キャンセルできないため、期限超過時は Batch Job を終端化（Terminate）することで、遅れて完了した Activity からの追加 Task 投入を Batch サービス側で拒否させます。ただし、Job 作成 Activity の待機を途中で打ち切ると、停止処理の直後に新規 Job が作成されてしまう時間窓（レースコンディション）が生じるため、Job 作成 Activity だけは必ず完了を待機します（参考: [公式Timer説明](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-timers)）。
- **Terminate要求と停止確認**: 期限超過時は対象 Job の ID・Pool・fingerprint を照合した上で Terminate を要求し、5秒間隔・最大2分間のポーリングで Job が `Completed` 状態へ遷移したことを確認します。すでに完了済みまたは停止処理中の Job に対する再要求は冪等に処理されます。万一停止確認がタイムアウト等で失敗した場合も、ワークフローの状態を成功とは見なさず、`customStatus.termination` と `terminationError` に異常を記録し、最終成果物（manifest.json）の公開を阻止します。なお、未開始の Task は Job 停止後も `Active` のまま残存するため、全 Task の `Completed` 待機を Job 停止の確認条件には使用しません（参考: [Terminate Job API](https://learn.microsoft.com/en-us/rest/api/batchservice/jobs/terminate-job?view=rest-batchservice-2025-06-01)、[Batchエラーコード](https://learn.microsoft.com/en-us/rest/api/batchservice/batch-status-and-error-codes)）。
- **Batch側の安全網（`MaxWallClockTime`）**: Functions ホストが突然停止した場合の最終安全網として、Batch Job 自身に `MaxWallClockTime` を設定します。これは Job 作成時点からの起算であり、Functions オーケストレーション側の共有 deadline とは起点が異なります。また、Job Release タスク等の停止シーケンスにも時間を要するため、期限時刻ちょうどでの物理停止やワークフロー全体の実時間上限を厳密に保証するものではありません。なお、ユーザーによる Durable ワークフローの手動キャンセル操作を Batch 側へ連鎖させる処理は本フェーズの実装範囲外です。

---

## 3. 配置制御（デプロイ・Smokeテスト）

- **Terraform BackendのPlanOnly制御**: `Backend.Create=true` での `PlanOnly` 実行時は、リモートステート用 Storage の存在確認が成功して作成済みと判明すれば、bootstrap は適用せず main の計画（plan）へ進みます。未作成であれば bootstrap の計画のみを実行します。存在確認時のエラーを単なる「リソース不存在」として握りつぶすことはしません（参考: [AzureRM backend](https://developer.hashicorp.com/terraform/language/backend/azurerm)、[container exists](https://learn.microsoft.com/en-us/cli/azure/storage/container?view=azure-cli-latest)）。
- **既存Batch Poolおよび新規0台Autoscale Poolへの対応**: 既存 Pool または `batch_scale_mode='autoscale'` の新規 Pool は、稼働ノードが 0 台であってもデプロイおよび Task 投入が可能です。ノードの存在を Task 投入の前提条件とはせず、Smoke テストや `Invoke-PocAutoscaleValidation.ps1` では Task を投入した上でノードの起動および Task の完了を待機します。既存 Pool に設定された自動拡張（autoscale）ルールは変更しません（参考: [Batch自動拡張](https://learn.microsoft.com/en-us/azure/batch/batch-automatic-scaling)）。
- **新規固定スケールBatch Poolのノード待機**: 新規 `fixed` Pool を作成する構成では、設定台数分の利用可能ノードが正常にプロビジョニングされるまで待機します。ノード一覧取得時は `odata.nextLink` を辿って全ページを集計し、別ホスト宛てや循環参照となる不正な継続 URL が返された場合は処理を拒否します（参考: [Node一覧API](https://learn.microsoft.com/en-us/rest/api/batchservice/nodes/list-nodes?view=rest-batchservice-2025-06-01)）。
- **Functionsの検出と状態判定**: デプロイ後に正常稼働を確認すべき Functions の検出対象は計12件です。状態照会 API（GET）における `404` は「インスタンス起動待ち」として上限付きでリトライし、`401` / `403` は「認証・権限エラー」として即座に失敗判定とします（参考: [Durable HTTP API](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-http-api)）。

---

## 4. 実Azureでの検証到達点と残る確認事項

ローカル環境のテスト（Mock / Fake）に加え、実 Azure 環境（Azure Storage バックエンドおよび Durable Task Scheduler バックエンド）にて以下の検証を完了しています（詳細は [信頼性の追加検証](reliability-validation.md) および [検証結果](test-results.md) を参照）。

1. **同時受付と入力競合**:
   - 異なる入力の同時・連続 POST に対して正しく `409 Conflict` が返却されること（`conflict` シナリオ：両バックエンドで実機 **Pass**）。
   - 同一入力の 20 並列 POST に対して同一インスタンス ID へ集約され完了すること、および `tools/DtsProbe --history true` で返却履歴の `executionStartedCount=1` を確認済み（ただし全世代の `executionId` を網羅列挙する公開 API がないため、`admission20` シナリオは引き続き `NotRun` 扱い）。
2. **プロセス中断からの復旧（`V05`）**:
   - Batch Task 実行中に Function App を ARM 経由で `restart` しても、再 POST なしで同一インスタンス・同一 Task のまま完走し、成果物と `manifest.json` が整合すること（両バックエンドで実機 **Pass**）。※Entity チェックポイント確定前後のミリ秒単位の強制クラッシュ注入は未実施。
3. **監視期限超過と停止処理（`V07`）**:
   - 監視期限（deadline）到達時に `TimedOut` となり、対象 Batch Job の `maxWallClockTime` 制約一致と `Completed`（強制終了）遷移、および `manifest.json` 非公開を確認済み（両バックエンドで実機 **Pass**）。※停止権限不足時の異常系注入は別途。
4. **安全網とオートスケール（`V08` / `V09`）**:
   - Function App を `Stopped` にした状態で Batch の `MaxWallClockTime` により Job が `MaxWallClockTimeExpiry` で自動終了すること（`V08`：実機 **Pass**）。
   - 0 台オートスケール Pool に対してノード 0 台状態から Task を先行投入し、自動拡張により Spot ノードが起動して Task が完走すること（`V09`：実機 **Pass**）。
5. **リトライポリシーの峻別**:
   - ローカルの Fake / Mock 単体テスト（`ResilienceTests` 42 件）で一時障害リトライと恒久エラー非再試行・キャンセルトークン伝播を確認済み。実 Azure 上での人為的な通信障害注入試験は後続課題。
