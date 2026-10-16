# 受付・再試行・期限超過の改善

更新日: 2026-10-17。公式資料と利用中のSDKソースを調査専用エージェントで確認し、配置制御とワークフローを並列に実装した。以下は設計とローカル検証の説明であり、Azure実環境の成功を示さない。

## 同一requestIdの受付

HTTP starterは既存インスタンスの入力を照合し、`runs/poc-<requestId>/request.json`をBlobの条件付き作成で確定する。同じIDの異なる入力は409。同じ入力は同じキーの`WorkflowAdmissionEntity`へStartをsignalする。202はsignalの永続キュー投入の受理であり、Main登録前の状態GETは一時404になり得る。受付確認の一時障害は503とRetry-Afterを返す。入力Blobを保持するため、起動前停止・応答喪失後も同じ入力で再送できる。

Entity操作は外部I/Oを行わず、未起動の場合だけMainの起動Actionと起動済み状態を生成する。再送されたsignalは保存状態を照合して終了する。Entity状態と送信Actionは同じdurable batchで処理され、Storageプロバイダーが障害時のメッセージ再配信を扱う。これはAzure Storageのqueueとtableにまたがる単一物理トランザクションではない。

調査中に次の点で当初方針を修正した。

- `GetInstance`の後に`ScheduleNew`するだけでは同時受付が競合する。[公式singleton説明](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-singletons)
- SDKの`DedupeStatuses`は利用できるが、Azure Storageでの状態照会と起動キュー投入は原子的ではない。Blob leaseも外部起動APIへのfencingにはならず、応答喪失時の完全な解決にならない。[Storageプロバイダー実装](https://github.com/Azure/durabletask/blob/main/src/DurableTask.AzureStorage/AzureStorageOrchestrationService.cs)
- SDK 1.24.1のEntity起動はActionをバッチへ追加し、例外時は状態とActionをrollbackする。この経路は`DedupeStatuses`を転送しないため、本実装では設定せず、Entityの保存状態によって起動を制御する。[利用版のEntity shim](https://github.com/microsoft/durabletask-dotnet/blob/v1.24.1/src/Worker/Core/Shims/TaskEntityShim.cs)
- Entityの直列処理とruntimeの起動メッセージ重複抑止を使用する。[公式Entity説明](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-entities)、[Core Entity dispatcher](https://github.com/Azure/durabletask/blob/main/src/DurableTask.Core/TaskEntityDispatcher.cs)、[Core起動重複処理](https://github.com/Azure/durabletask/blob/main/src/DurableTask.Core/TaskOrchestrationDispatcher.cs)

入力Blob・Entity状態・Main履歴を保持し、本HTTP APIを使うことが前提。個別purge、同IDを管理APIから直接起動する操作、旧starterとの同時稼働は対象外。旧版からの更新は既存処理の終了後に行う。Entity状態を削除してrequestIdを再利用する清掃手順は提供しない。Entity利用に追加の同時実行数制限は不要。[host設定](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-host-json-settings)

## 一時障害と期限

Activityは一時障害だけを`TransientWorkflowException`へ変換し、Durable retry handlerが最大3試行と共有deadlineを確認する。待機はDurable Timerで5秒・10秒。入力不正、認証・権限エラー、競合、結果不整合は再試行しない。SDK側は再試行1回、通信試行のNetworkTimeoutは10秒とする。SDKによる再試行とDurableによる再試行の両方があるため、総通信試行数が3回とは限らない。

isolated workerの`TaskFailedException`で元の例外型を確認するには`FailureDetails.IsCausedBy<T>()`を使う。[公式エラー処理](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-error-handling)

Azure.Coreは通信再試行を使い切ると`AggregateException`を返す場合があるため、内部例外の全件が一時障害の場合だけ再試行する。NetworkTimeoutはlinked tokenによる`OperationCanceledException`として返る。呼出元tokenをHTTP受付・Activity・SDK操作まで渡し、呼出元がcancel済みなら再試行せず、それ以外のSDK通信cancelを一時障害として扱う。エラーメッセージの文字列判定は使わない。[SDK RetryPolicy](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.54.0/sdk/core/Azure.Core/src/Pipeline/RetryPolicy.cs)、[利用系列のResponseBodyPolicy](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.54.0/sdk/core/Azure.Core/src/Pipeline/Internal/ResponseBodyPolicy.cs)

監視期限は分類完了後に親で確定し、子へ渡す。Task作成・状態照会Activityとdeadline Timerを競合させ、期限到達で親へTimedOutを返す。TimerはActivityを物理キャンセルしないため、Job終端化で遅れて実行されるTask追加を拒否する。Job作成を放棄すると停止後にJobが新規作成される窓が生じるので、Job作成Activityだけは完了を待つ。[公式Timer説明](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-timers)

期限超過時は当該JobのID・Pool・fingerprintを照合してTerminateを要求し、5秒間隔・最大2分でJob Completedを確認する。完了済み・停止中の再要求は冪等に扱う。停止確認に失敗しても状態を成功にせず、`customStatus.termination`と`terminationError`を残し、Manifestを公開しない。全TaskのCompleted待ちは、未開始TaskがActiveのまま残るため停止確認に使わない。[Terminate Job API](https://learn.microsoft.com/en-us/rest/api/batchservice/jobs/terminate-job?view=rest-batchservice-2025-06-01)、[Batchエラーコード](https://learn.microsoft.com/en-us/rest/api/batchservice/batch-status-and-error-codes)

Batch Jobに`MaxWallClockTime`を設定し、Functions停止中の安全網とする。これはJob作成時起算で、orchestration側の共有期限とは異なる。Job release等を含む停止処理の時間もあるため、期限時刻ちょうどの物理停止やワークフロー全体の厳密な実時間上限は保証しない。利用者によるDurableキャンセルのBatch連鎖は今回の範囲外。

## 配置制御

- `Backend.Create=true`のPlanOnlyは、成功した存在確認でbackend作成済みと分かればbootstrapを適用せずmain計画へ進む。未作成ならbootstrap計画のみ。存在確認のエラーを不存在として握りつぶさない。[AzureRM backend](https://developer.hashicorp.com/terraform/language/backend/azurerm)、[container exists](https://learn.microsoft.com/en-us/cli/azure/storage/container?view=azure-cli-latest)
- 既存Poolは0台でも配置可能。ノードの存在をTask投入の前提にせず、SmokeはTaskを投入して完了を待つ。自動拡張設定は変更しない。[Batch自動拡張](https://learn.microsoft.com/en-us/azure/batch/batch-automatic-scaling)
- 新規Poolは設定台数分の利用可能ノードを待つ。`odata.nextLink`で全ページを集計し、他ホストや循環する継続URLを拒否する。[Node一覧API](https://learn.microsoft.com/en-us/rest/api/batchservice/nodes/list-nodes?view=rest-batchservice-2025-06-01)
- Functions検出は12件。状態GETの404は起動待ち、401/403は認証・権限失敗として扱う。[Durable HTTP API](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-http-api)

## 実Azureで残る検証

1. 同入力の同時POST、異入力の同時POSTで、Mainが1件・入力競合が409となること。
2. 入力Blob作成後・Entity受理前後・Entityバッチ確定前後にhostを停止して、再送と復旧を確認すること。
3. Task投入・状態照会中の期限到達でJobがCompletedとなり、Manifestがないこと。停止権限不足も確認すること。
4. host停止中のMaxWallClockTimeと、0台autoscale PoolへのSmokeを確認すること。
5. 一時的な障害で再試行され、権限・競合・結果不整合では再試行されないこと。

Mockではアプリケーションの分岐と呼び出しを確認する。実Blobの同時条件付き書き込み、Entityバッチのクラッシュ復旧、実Batchの停止・権限伝播は実Azure試験で別途実証する。
