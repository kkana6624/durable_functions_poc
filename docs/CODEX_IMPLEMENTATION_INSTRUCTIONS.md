# Codex 実装指示書 — Azure Batch + Durable Functions フィージビリティPoC

更新日: 2026-10-16

2026-10-17追記: 本文は初版P0の実装指示です。追加で承認された同時受付制御・一時障害再試行・期限超過時のBatch停止は[信頼性改善の設計](workflow-reliability.md)を現行仕様とします。本文の非要件・P1指定は初版の範囲を示します。

> **最初に同じディレクトリにある `azure-hpc-modernization-poc-design.md` を読み、そこにあるP0を最短で動かすこと。**
> 本PoCの目的は **Durable Functionsによる非同期ワークフロー制御の実現性確認** であり、HPC計算本体やLSF互換スケジューラの開発ではない。

## 1. 確定済みの情報

- 計算ノードは **Windows Server**。ジョブは通常 **単一ノード完結**。
- Azure Compute Galleryベースの Azure Batch Pool 構築実績があり、Poolと権限の調査は済んでいる。
- Batch Account / Pool / GalleryをPoCで作り直したり変更したりしない。
- 軽重判定ロジックは別途既存システムに存在するが、**このPoCではStubでよい**。実装はAdapter差し替え可能な形にする。
- ダミーTaskはWindowsの **PowerShell 5.1コマンド**で数秒待ち、`result.txt`に`1`を書き込むだけでよい。実計算、CPU負荷再現、MPI、HPC固有ライセンス処理は不要。
- 最初は1Pool・3 Task・数KB以下の結果のみ。

## 2. 技術スタック

- Azure Functions v4、**.NET 10 / isolated worker**。Microsoftの最新ガイドとNuGetからサポートされるパッケージ組み合わせを確認し、現行のProject SDKを使用する。プラットフォーム制約が判明した場合のみ.NET 8等へ変更し、理由を記録。
- Durable Functions isolated拡張 `Microsoft.Azure.Functions.Worker.Extensions.DurableTask`。
- `Azure.Compute.Batch`（原則現行の1.x SDK）、`Azure.Identity`、`Azure.Storage.Blobs`。
- Terraform azurerm providerで**PoC専用**Function App、Storage、Blob、監視、Managed Identity、RBACを構成。
- `DefaultAzureCredential`（クラウド上はManaged Identity）でBatch/Storageに接続。資格情報のハードコードは禁止。

## 3. P0で必ず実装すること

1. `StartWorkflow` HTTP Trigger: JSON要求を検証し、Durableを起動。202 + instanceId + 状態URLを返す。
2. `MainOrchestrator`: `ClassifyActivity`でStub軽重判定 → `EnsureBatchJobActivity`でBatch Job確保 → 3つの子Orchestratorを並列起動し`Task.WhenAll` → `FetchAndPersistActivity` → `AggregateActivity` → `PublishManifestActivity`。
3. `MonitorBatchTaskOrchestrator`: 1 Task分の`EnsureBatchTaskActivity`、`GetBatchTaskStatusActivity`、`Durable Timer`ループを実装。全Taskが完了すれば親へ結果を返す。失敗/期限超過は明示的なエラーへ。
4. Windows Taskは追加ランタイム不要のコマンドで `result.txt`に整数1を出力し、正常終了0。失敗注入対象Taskは意図的に非ゼロ終了。**Batch Taskコマンドは暗黙にShellを使用しないため `cmd /c` を用い、Quote/escapingを検証する**。
5. P0では **Batch OutputFilesは使わない**。成功した各Taskの`result.txt`をBatch Task File API（`GetTaskFile`系。Task作業ディレクトリ内のパス、通常`wd/result.txt`相当をAPIのファイル一覧で確認）から `FetchAndPersistActivity` で取得し、Functions側からBlobへ小さなJSONとして保存。BatchノードにBlob書込IDやSASを付与する実装は不要。
6. `AggregateActivity`: 3つのJSONの`value`を合計して`final/aggregate.json`へ保存。`PublishManifestActivity`: 全件成功と結果整合性をチェックして`final/manifest.json`を公開。
7. 同じrequestIdのBatch Job/Task IDは決定的に導出。既存IDとの衝突を検出し、同一構成なら継続・異なる構成ならConflict。Activity再実行で不要な重複Job/Taskを作らない基本設計を実装。
8. Batch `Completed`だけで成功としない。実行結果、exit code、failure infoを調べて判定。MaxTaskRetryCountは原則0、ただしノード復旧時の再実行可能性はREADMEに明記。
9. Orchestratorの入出力はID、状態、URI等の小データのみ。結果ファイルの本文はオーケストレーション履歴に載せない。
10. Orchestratorは決定的に保つ。I/OはActivityで実施、通常の `Task.Delay` を使わずDurable Timer、リプレイ安全なロギングを使用する。

## 4. 実装を軽くするための明確な非要件

- 本物のHPC計算、LSFやHPC Packの詳細なジョブ構文互換
- Windowsノードへの新しいアプリケーション導入、イメージ再作成、Startup Task改修
- Light/Heavyの別Poolへの配置、RequiredSlotsの細粒度チューニング
- Batch OutputFilesによるノードからBlobへの直送（P1候補）
- 同時requestIdの厳密な排他保証、リクエスト認可の本番対応、キャンセル連鎖
- 大規模耐障害性試験、数百ノードでの性能検証、Fair Share、Backfilling、MPI
- 独自DB、独自メッセージキュー、独自ポーラの別アプリ構築

**不要な抽象化や汎用フレームワークを作らないこと。** 実際の実装に必要なインターフェースは`IBatchGateway`、`IResultRepository`、`IWorkloadClassifier`程度にとどめる。

## 5. 最小APIと期待結果

リクエスト:

```json
{
  "requestId": "feasibility-001",
  "classification": "auto",
  "taskCount": 3,
  "failTaskIndex": null,
  "lightDelaySeconds": 5,
  "heavyDelaySeconds": 15
}
```

成功後のBlob:

```text
runs/feasibility-001/tasks/task-00/result.json
runs/feasibility-001/tasks/task-01/result.json
runs/feasibility-001/tasks/task-02/result.json
runs/feasibility-001/final/aggregate.json
runs/feasibility-001/final/manifest.json
```

`aggregate.json` には`sum=3`・`taskCount=3`を記録。Blobの条件付きアップロードで最終Manifestを重複公開しない。

## 6. 実装手順（必ず順番を守る）

**A. SDK・環境とWindows Batchスモークテスト**

- 既存Batch Pool IDとEndpointを環境変数から取得。空のWindows Poolの作成はしない。
- 既存PoolにPoC ID付き単一Taskを投入。PowerShellワンライナーで`result.txt`を作り、Batch File APIから読み出せることを確認。SDKの署名が古いサンプルと異なる場合は公式の現行実装に合わせる。

**B. Durable ワークフローの構築**

- Parent/Childオーケストレーション、Activity、Durable Timer、3 Task並列待機、Blob結果集約、最終Manifestを通す。
- 1 HTTP要求から状態照会、Blobに最終結果が残るまで再現可能な手順をREADMEに記す。

**C. フィージビリティ試験**

- 正常系: 3 Task成功でsum=3。
- 分岐: light/heavyの違いがログと結果に残る。
- 失敗系: 1 Task非ゼロ終了 → 失敗が伝播し、Manifestは公開しない。
- 永続待機: Task待機中に**このPoC専用**Function Appのみ再起動し、Orchestratorが復旧することを確認。共有Batch Pool/計算ノードを停止しない。
- 結果欠損: Taskファイルが読めない状態をMock等で模擬し、成功誤認しないことを確認。
- 必要なら同一requestIdでの再投入も確認し、**厳密な同時受付保証は未検証と報告**する。

**D. 検証結果のドキュメント化**

- `docs/test-results.md`: 手順、期待値、実測、スクリーンショットまたはログの場所、結果(Pass/Fail/Not tested)を記載。
- `docs/feasibility-matrix.md`: Durableによるワークフロー制御、Batchによる計算実行、Blobの集約について、実際に確認できたものだけPass。
- LSF/HPC Pack全体の代替性、性能、Fair Shareなどは**Not tested/Gap**に留める。

## 7. Terraformルール

- 既存Batch Account、Pool、ACGは`resource`で宣言しない。`data`参照または変数からの明示受領のみ。
- RG/Storageも既存利用可。PoCが作成するものと参照するだけのものをREADMEに表で列挙する。
- `terraform plan`で既存Batchリソースに対する変更・削除がないことを確認。
- `terraform.tfvars.example`に必要な値と取得方法を記す。秘密情報やstateをGitに含めない。
- PoC専用の`poc-`プレフィックス以外のBatch Job/Blobを削除・改変しない。
- Azure上の実行に必要なRBACが不足している場合は権限不足を明示し、広範なOwner権限の付与を自動実施しない。

## 8. 納品物

- コンパイル可能なFunction App、最小限の単体テスト、Terraform、Windows Taskコマンド作成部
- `README.md`（セットアップ、環境変数、Terraform Plan/Apply、起動/照会、Blob確認、清掃）
- `docs/test-results.md`、`docs/feasibility-matrix.md`
- 既存Poolと切り離してローカル単体テストできるFakeGateway（実接続試験と混同しない）
- SDK/API検証結果、残る課題と別フェーズの候補

## 9. 注意事項

- **Azureへアクセスできない状態なら、アクセスしたかのような結果を作らず、ビルド/Mock試験と実環境未実施試験を分けて報告する。**
- BatchのTaskファイルはノードローカルで永続ではない。P0での取得成功は大容量・本番のBlob出力の実現性証明ではない。P1ではBatch `OutputFiles`やノードからの直接アップロードを検証する。
- Durable ActivityはAt-least-once実行。`Singleton`スタート前の状態照会は排他の完全保証ではない。
- 検証の中心は **どれだけの非同期制御、状態DB、待機制御、復旧制御の独自実装が不要になるか** の実証である。

## 参考（実装時に再確認）

- https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-dotnet-isolated-overview
- https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide
- https://learn.microsoft.com/en-us/dotnet/api/overview/azure/compute.batch-readme
- https://learn.microsoft.com/en-us/rest/api/batchservice/tasks/get-task-file
- https://learn.microsoft.com/en-us/azure/batch/jobs-and-tasks
