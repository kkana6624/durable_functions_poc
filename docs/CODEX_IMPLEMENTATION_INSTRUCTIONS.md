# Codex 実装指示書 — Azure Batch + Durable Functions フィージビリティPoC

更新日: 2026-10-16

> **歴史的文書および現行仕様に関する注記（2026-10-18更新）:** 本文は初版（P0）実装時の歴史的スコープ指示書です。本文における「非要件」や「P1」の記載は初版当時の境界を示しています。
> その後追加された「同時受付時の排他制御」「一時障害に対するリトライ」「タイムアウト時の Batch リソース停止処理」、およびワンコマンドデプロイの「新規 Batch リソース作成オプション（`batch_mode`）」は [信頼性改善の設計](workflow-reliability.md) および [ワンコマンドデプロイ構成整理](one-command-deployment-inventory.md) を参照してください。
> さらに現行コード（コミット `5a45b17`）では、計14関数構成への拡張、独自状態照会・結果照会URL契約（`/api/workflows/{requestId}`、`Retry-After: 10`）、HTTP/ログ/状態のエラー遮蔽、公開成果物検証、Activity例外のDurable境界での安全化、およびgRPC再試行の3コード限定（`Unavailable` / `DeadlineExceeded` / `ResourceExhausted`）へ発展しています。

> **はじめに:** 作業を開始する前に、同じディレクトリにある `azure-hpc-modernization-poc-design.md` を通読し、そこで定義されている P0 スコープを最短経路で動作させることを優先してください。
> 本PoCの目的は **Durable Functions を用いた非同期ワークフロー制御の技術的実現性を確認すること** であり、HPC の数値計算処理そのものや LSF 互換スケジューラを開発することではありません。

## 1. 前提条件（確定事項）

- 計算ノードの OS は **Windows Server** であり、各ジョブ（Task）は原則として **単一ノード内で完結** します。
- Azure Compute Gallery（ACG）のイメージを用いた Azure Batch Pool の構築実績がすでにあり、Pool 構成および必要権限の調査は完了しています。
- 既存の Batch Account / Pool / Gallery を本PoCのなかで作り直したり、設定を変更したりしないでください。
- 処理の軽量（Light）・重量（Heavy）を判定するロジックは既存システム側に別途存在しますが、**本PoCではシンプルな Stub 実装とします**（将来的に実ロジックへ差し替えやすいようインターフェースは分離しておきます）。
- 計算ノードで実行するダミー Task は、Windows 標準の **PowerShell 5.1 コマンド** で数秒間スリープしたのちに `result.txt` へ `1` を書き出すだけの処理とします。実際の科学技術計算、CPU 負荷の再現、MPI 通信、HPC 固有のライセンス処理などは実装不要です。
- 初期検証の対象は、1つの Pool・3つの Task・数KB以下の小さな結果ファイルのみとします。

## 2. 技術スタック

- Azure Functions v4、**.NET 10 / isolated worker** を採用します。Microsoft の最新ガイドおよび NuGet でサポートされているパッケージの組み合わせを確認し、現行の Project SDK を使用してください（万一プラットフォーム上の制約が判明した場合のみ .NET 8 等へ変更し、その理由をドキュメントに記録してください）。
- Durable Functions isolated 拡張パッケージとして `Microsoft.Azure.Functions.Worker.Extensions.DurableTask` を使用します。
- Azure SDK は `Azure.Compute.Batch`（原則として現行の 1.x 系 SDK）、`Azure.Identity`、`Azure.Storage.Blobs` を使用します。
- Terraform（`azurerm` プロバイダー）を用いて、**本PoC専用の** Function App、Storage Account、Blob Container、監視リソース、Managed Identity、および RBAC を構成します。
- Batch および Storage への接続認証には `DefaultAzureCredential`（クラウド上では Managed Identity）を使用し、接続文字列やキーなどの資格情報をコードや設定ファイルにハードコードしないでください。

## 3. P0 で実装する必須要件

1. **`StartWorkflow` (HTTP Trigger)**: リクエスト JSON を検証して Durable オーケストレーションを起動し、`202 Accepted` とともに `instanceId` および状態照会用 URL を返します。
2. **`MainOrchestrator`**: `ClassifyActivity`（Stub による軽重判定）→ `EnsureBatchJobActivity`（Batch Job の作成・確認）→ 3つの子オーケストレーターを並列起動して `Task.WhenAll` で待機 → `FetchAndPersistActivity` → `AggregateActivity` → `PublishManifestActivity` の順に制御します。
3. **`MonitorBatchTaskOrchestrator`**: 1 Task 分の `EnsureBatchTaskActivity`、`GetBatchTaskStatusActivity`、および `Durable Timer` によるポーリングループを実装します。Task が完了したら親オーケストレーターへ結果を返し、失敗またはタイムアウト時は明示的なエラーとして伝播させます。
4. **Windows Task コマンド**: 追加ランタイム不要の標準コマンドで `result.txt` に整数 `1` を出力して終了コード `0` で正常終了させます（失敗注入対象の Task は意図的に非ゼロで終了させます）。**Azure Batch の Task コマンドラインは暗黙的にシェルを介さないため、先頭に `cmd /c` を明示し、クォートやエスケープ処理が正しく動作することを検証してください。**
5. **結果ファイルの収集（P0 方式）**: P0 では **Batch の `OutputFiles` 機能は使用しません**。正常終了した各 Task の `result.txt` を Batch Task File API（`GetTaskFile` 系 API。Task 作業ディレクトリ配下のパス、通常は `wd/result.txt` 相当をファイル一覧 API 等で確認）を用いて `FetchAndPersistActivity` で取得し、Functions 側から Blob Storage へ小さな JSON ファイルとして保存します。計算ノード側に Blob 書き込み用の Managed Identity や SAS トークンを配布する実装は不要です。
6. **`AggregateActivity` / `PublishManifestActivity`**: `AggregateActivity` で3つの結果 JSON の `value` を合算して `final/aggregate.json` へ保存し、`PublishManifestActivity` で全 Task の成功と集計結果の整合性を確認したうえで `final/manifest.json` を出力します。
7. **ID の決定論的生成と冪等性**: 同一の `requestId` からは常に同じ Batch Job ID / Task ID が導出されるように実装します。すでに同一 ID が存在する場合は入力構成を照合し、同一構成であれば処理を継続、異なる構成であれば Conflict として扱います。これにより、Activity のリトライ時に重複して Job や Task が作成されないようにします。
8. **Task 成否の正確な判定**: Batch 上の Task 状態が `Completed` であることだけをもって成功と判定せず、実行結果（`executionInfo`）、`exitCode`、`failureInfo` を確認して成否を判定してください。`MaxTaskRetryCount` は原則 `0` としますが、計算ノードの障害復旧時に Batch 側で Task が再実行される可能性がある点は README に明記してください。
9. **オーケストレーション履歴の軽量化**: Orchestrator の入出力には ID、ステータス、URI 等の小さなメタデータのみを渡し、結果ファイルの本文データをそのままオーケストレーション履歴に含めないでください。
10. **決定性（Determinism）の維持**: Orchestrator 内のコードは決定論的に保ちます。外部 I/O はすべて Activity で実行し、待機には `Task.Delay` ではなく Durable Timer を使用し、ログ出力にはリプレイ安全なロガーを使用してください。

## 4. 実装をシンプルに保つための非要件（スコープ外）

以下の項目は初版 P0 のスコープ外とし、実装を行いません。

- 実際の HPC 数値計算処理、および LSF や HPC Pack の詳細なジョブ定義構文との互換性
- Windows 計算ノードへの新規アプリケーションの導入、カスタムイメージの再作成、Startup Task の改修
- Light / Heavy 判定に応じた別 Pool への振り分けや、`RequiredSlots` の細かなチューニング
- Batch `OutputFiles` を用いた計算ノードから Blob Storage への直接アップロード（P1 候補）
- 同一 `requestId` が同時にリクエストされた場合の厳密な排他制御、本番環境向けのリクエスト認可、失敗時のキャンセル連鎖
- 大規模な耐障害性試験、数百ノード規模での性能検証、Fair Share、Backfilling、MPI
- 独自の状態管理 DB、独自のメッセージキュー、常駐ポーリング用の別アプリケーションの構築

**過度な抽象化や汎用フレームワーク化は避けてください。** 実装時に設けるインターフェースは、テスト容易性と将来の差し替えに必要な `IBatchGateway`、`IResultRepository`、`IWorkloadClassifier` 程度にとどめます。

## 5. 最小 API 仕様と期待される出力

リクエスト JSON:

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

正常完了後に生成される Blob 一覧（PoC専用の `poc-` プレフィックスが付与されます）:

```text
runs/poc-feasibility-001/request.json
runs/poc-feasibility-001/tasks/task-00/result.json
runs/poc-feasibility-001/tasks/task-01/result.json
runs/poc-feasibility-001/tasks/task-02/result.json
runs/poc-feasibility-001/final/aggregate.json
runs/poc-feasibility-001/final/manifest.json
```

`aggregate.json` には `sum=3` および `taskCount=3` が記録されます。最終の `manifest.json` は Blob の条件付きアップロードを用いて出力し、重複公開を防ぎます。

## 6. 実装・検証の進め方（推奨順序）

**A. SDK・環境の確認と Windows Batch スモークテスト**

- 既存の Batch Pool ID と Endpoint を環境変数から取得します（この段階で空の Windows Pool を新規作成することはしません）。
- 既存 Pool に対して PoC 用 ID を付けた単一の Task を投入し、PowerShell ワンライナーで作成した `result.txt` を Batch File API 経由で読み出せることを確認します。なお、現行 SDK のメソッドシグネチャが古いサンプルコードと異なる場合は、公式ドキュメントの最新仕様に合わせて実装してください。

**B. Durable ワークフローの構築**

- 親・子オーケストレーション、各 Activity、Durable Timer、3 Task の並列待機、Blob への結果集約、および最終 `manifest.json` の出力までを一通り実装します。
- 1回の HTTP リクエスト送信から状態照会、Blob 上の最終成果物の確認に至るまでを再現できる手順を README に記載します。

**C. フィージビリティ検証の実施**

- **正常系**: 3つの Task がすべて成功し、`sum=3` の集約結果が得られること。
- **処理分岐**: `light` / `heavy` の指定に応じて、ログや成果物に分岐結果と待機時間の差が反映されること。
- **異常系（失敗伝播）**: 1つの Task を非ゼロ終了させた際、親オーケストレーションにエラーが伝播し、`manifest.json` が公開されないこと。
- **永続待機と再起動復旧**: Task の完了待機中に **本PoC専用の Function App のみ** を再起動しても、オーケストレーションが履歴から再開して完了できること（共有の Batch Pool や計算ノード自体は停止させないでください）。
- **結果ファイル欠損**: Task は完了したものの `result.txt` が読み取れない状態を Mock 等で再現し、誤って成功扱いにならず明示的な失敗となること。
- 必要に応じて同一 `requestId` での再投入も確認し、**同時リクエスト時の厳密な排他保証については P0 時点では未検証（P1 課題）である旨を明記**してください。

**D. 検証結果のドキュメント化**

- `docs/test-results.md`: テスト手順、期待値、実測結果、ログまたは証跡の保存場所、および判定結果（Pass / Fail / Not tested）を記録します。
- `docs/feasibility-matrix.md`: Durable Functions によるワークフロー制御、Batch による計算実行、Blob を用いた結果集約の各観点について、実際に動作確認できた項目のみを Pass として記録します。
- LSF / HPC Pack 全体の完全代替性、大規模性能、Fair Share 等については **Not tested / Gap** として明確に切り分けて記載してください。

## 7. Terraform 実装上のルール

- 既存の Batch Account、Pool、ACG は Terraform の `resource` として定義せず、`data` ソースによる参照または変数からの受け渡しのみとしてください。
- Resource Group や Storage Account も既存リソースの利用を可能とし、本PoCで新規作成するものと既存参照のみのものを README の表で明確に整理してください。
- `terraform plan` 実行時に、既存の Batch リソースに対する変更や削除が含まれていないことを必ず確認してください。
- `terraform.tfvars.example` に必要な変数項目と値の確認方法を記載し、シークレットや `.tfstate` ファイルは Git リポジトリに含めないでください。
- PoC 専用の `poc-` プレフィックスが付いたもの以外の Batch Job や Blob データを削除・変更しないよう制御してください。
- Azure 環境での実行に必要な RBAC 権限が不足している場合は、不足している権限をエラーメッセージやドキュメントで明示し、安易に広範な `Owner` 権限等を自動付与する実装は行わないでください。

## 8. 成果物一覧

- ビルド・実行可能な Function App 一式、最小限の単体テスト、Terraform コード、および Windows Task コマンド生成ロジック
- `README.md`（環境セットアップ、環境変数一覧、Terraform Plan/Apply 手順、ワークフローの起動・状態照会手順、Blob 成果物の確認方法、クリーンアップ手順）
- `docs/test-results.md` および `docs/feasibility-matrix.md`
- 既存 Pool に接続せずローカル環境で単体テストを実行できる FakeGateway 実装（実環境での接続テスト結果とは明確に区別すること）
- SDK / API の検証結果、残課題、および次フェーズ（P1 以降）の検討候補の整理

## 9. 実装・報告時の注意事項

- **実行環境から Azure へアクセスできない場合は、実機検証を行ったかのような架空の結果を作成せず、「ローカルでのビルド・Mock テスト結果」と「Azure 実環境での未実施項目」を明確に区別して報告してください。**
- Batch 計算ノード上の Task ファイルはノードローカルの一時領域に置かれるため、永続性は保証されません。P0 における Task File API での結果取得成功は、あくまで小規模データでの疎通確認であり、本番の大容量データをそのままこの方式で扱えることを示すものではありません。大容量出力に向けた Batch `OutputFiles` や計算ノードからの直接 Blob アップロードは P1 で検証します。
- Durable Functions の Activity は At-least-once での実行となります。また、オーケストレーション起動前の単純なステータス照会だけでは、同時リクエストに対する完全な排他制御にはならない点に留意してください。
- 本PoCにおける評価の主眼は、**非同期ポーリング、状態管理用 DB、待機タイマー制御、およびプロセス再起動時のリカバリ処理をどれだけ自前実装せずに済むか** を具体的に示すことにあります。

## 参考ドキュメント（実装時に再確認すること）

- https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-dotnet-isolated-overview
- https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide
- https://learn.microsoft.com/en-us/dotnet/api/overview/azure/compute.batch-readme
- https://learn.microsoft.com/en-us/rest/api/batchservice/tasks/get-task-file
- https://learn.microsoft.com/en-us/azure/batch/jobs-and-tasks
