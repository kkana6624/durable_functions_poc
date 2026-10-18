# Azure Batch + Durable Functions PoC

Windows Batch Pool への Task 投入から、Durable Functions（子オーケストレーションおよび永続 Timer）による完了待機、Batch File API 経由での結果取得、Blob 保存・集約、最終 Manifest 公開までの一連の非同期ワークフローを検証する PoC 資材です。

設計の背景と実装要件については、[設計書](docs/azure-hpc-modernization-poc-design.md) および [実装指示書](docs/CODEX_IMPLEMENTATION_INSTRUCTIONS.md) を参照してください。

> [!IMPORTANT]
> **検証ステータスに関する注記**
> - **最新コード（15 Functions・V12 利用者キャンセル実装）**: `CancelWorkflow` を追加した計 15 個の Functions を登録するコードで、Release 単体テスト全 270 件（`Poc.Tests` 268/268 件、`DtsProbe` 2/2 件、失敗・スキップ 0 件）、Terraform Mock 37 件（`modes.tftest.hcl`）、PowerShell オフライン試験 8 組全件（cancellation シナリオ追加）が成功し、Release publish も成功しています。V12 利用者キャンセル（`POST /api/workflows/{requestId}/cancel`、Blob 先行永続化 `runs/poc-<requestId>/cancel.json`、Durable 外部イベント `CancelRequested`、Batch ジョブ有界停止 `TerminateBatchJobActivity`、成果物 `manifest.json` 公開抑止、所有者認可、冪等 200/202）、受付・所有者の単一レコード化（`admission.json` Version 2）、opt-in の `EntraEasyAuth` 認可アダプター、EasyAuth IaC（`workflow_auth_mode`）、および VM 認証 runner をローカル実装・テスト完了しています。
> - **Azure 実機検証境界（前回の 14 関数実機成功と今回の 15 関数未配置の区別）**: 前回の 14 関数コードについては、実 Azure 環境（`poc` 環境・Azure Storage バックエンド）への配置・スモークテスト（3 Tasks、`Sum=3`）および閉域検証 VM 経由での主要 5 シナリオ回帰検証（`single` / `three` / `classification` / `failure` / `conflict`、証跡: `artifacts/private-validation/20261018-083838-2879/`、終了コード 0）を完了しています。一方で、今回の `CancelWorkflow` を含む **15 関数コードの Azure 実機デプロイ・回帰検証は未実施** です。
> - **過去の Azure Storage バックエンド環境（`poc`）**: インフラ配置、RHEL 9 検証 VM の初期設定、Private SCM 配置、14 関数の登録・5 シナリオ回帰、Function App 再起動復旧（`V05`）、および専用 Pool（`batch_mode='new'`）での 0 台オートスケール先行投入・自動拡張完走・固定スケール復元（`V09`）を実機確認済みです（`admission20` は全世代の起動履歴取得制限により `NotRun`、summary 終了コード `2`）。
> - **過去の Durable Task Scheduler（DTS）バックエンド環境（`dts-poc`）**: 当時の 12 個の Functions 登録、3 Task スモークテスト（`Sum=3`）、標準 6 シナリオ、Function App 再起動復旧（`V05`）、Function ホスト停止中の Batch `MaxWallClockTime` による自動終了（`V08`）、および SDK（`tools/DtsProbe`）による履歴イベント集計を実機確認済みです（`admission20` は `NotRun`）。
> - **未実証・未完了の範囲**: 15 関数コード（`CancelWorkflow`）の Azure 実機回帰検証、Entra 登録・EasyAuth 配置と信頼できる owner/org 伝播の実証、実 C++ バイナリと入出力連携（`V10`）、大容量出力（`T09`）、全面閉域化（`V00`）、Spot 回収復旧が残っています。証跡は [検証結果](docs/test-results.md)、[実現性評価](docs/feasibility-matrix.md)、[信頼性の追加検証](docs/reliability-validation.md) を参照してください。

---

## 本資材でできること・できないこと（アプリ側との責務境界）

アプリケーション担当者および基盤担当者が本資材の範囲を共通認識できるよう、現在の資材で「実現できていること」と「含まれていないこと（アプリ側バイナリ組み込み時や後続フェーズで対応が必要な事項）」を整理します。

| 分類 | 本資材で「できること」（実装・検証済み） | 本資材単体では「できないこと」（スコープ外・後続課題） |
|---|---|---|
| **ワークフロー制御** | ・HTTP API（`POST /api/workflows`）によるジョブ受付<br>・子オーケストレーションによる複数 Task の並列投入と完了待ち合わせ（Fan-out / Fan-in）<br>・Durable Timer によるサーバーレスな状態ポーリング<br>・Batch Task 実行中の Function App 再起動からの無停止自動復旧（`V05`）<br>・1 Task でも失敗した場合の後続集約および `manifest.json` 公開の自動抑止<br>・**利用者キャンセル API（`POST /api/workflows/{requestId}/cancel`）による実行中ワークフローの有界停止（`V12`）**: 事前 Blob 永続化（`cancel.json`）、Durable 外部イベント（`CancelRequested`）待機、Batch ジョブ有界停止（`TerminateBatchJobActivity`）、成果物 `manifest.json` 公開の厳格な排他調停、所有者認可（他者 403 拒否）、冪等応答（実行中 202 / 完了・停止済み 200） | ・ミリ秒単位のリアルタイム応答<br>・15 関数コード（`CancelWorkflow` 追加）の Azure 実機回帰検証（前回 14 関数実機検証との境界） |
| **受付排他・再試行** | ・同一 `requestId` の二重起動防止（不変な `admission.json` Blob ＋ Durable Entity 直列化）<br>・同一 `requestId` で異なるパラメータが送られた際の `409 Conflict` 拒否<br>・一時的な通信障害に対する有界リトライ（最大 3 回）と監視期限超過時の Batch Job 強制停止（`V07`）および Function ホスト停止中の Batch `MaxWallClockTime` 安全網（`V08`） | ・履歴保持期限経過後や手動パージ後の `requestId` 再利用保証<br>・外部管理 API から全世代（全 `executionId`）の起動履歴を網羅取得して厳密な 1 回起動を証明すること |
| **計算処理・入出力** | ・Windows Batch Pool（専用ノードまたは Spot ノード、固定台数または 0 台 Autoscale）への Job / Task 投入と終了コード判定<br>・PoC 用ダミー処理（指定秒数スリープ後に `result.txt` へ整数 `1` を出力）の回収と合計値（`sum`）集計 | ・**本番 C++ バイナリ（`.exe` / `.dll`）の同梱・配布**（現在は PowerShell ダミーコマンドを実行）<br>・**大容量の入力シナリオファイル転送（`ResourceFiles`）および計算ノードから Blob への大容量結果直接保存（`OutputFiles`）**（※現行は 4 KB 以下・整数 `1` 専用の File API 回収のため、実バイナリ試験時は差し替えが必要）<br>・Spot ノードが結果回収前に Azure へ回収（Evict）された場合のノードローカル結果の保護 |
| **スケジューラ機能** | ・Azure Batch 標準のキューイング、空きノードへの Task 割り当て、Pool 準備完了待機、0 台待機からの `$PendingTasks` 自動スケールアウト（`V09`） | ・LSF / HPC Pack が持つ高度なクラスタスケジューラ機能（Fair Share、Backfilling、実行中タスクの追い越し、商用ソルバーライセンス連動）<br>・新規の業務向け Web フロントエンド画面（UI）の提供 |
| **ネットワーク・認証** | ・Function App の公開アクセス無効化と Private Endpoint / Private DNS 構成<br>・Public IP なし・SSH 拒否の RHEL 9 検証 VM（Run Command 経由）からの閉域デプロイおよび HTTP 検証<br>・Managed Identity による Function ↔ Storage / Batch / DTS 接続<br>・Function key によるAPI保護と、owner/orgヘッダー値を使った受付・照会・結果の照合 | ・Storage / Batch エンドポイントの全面 Private Endpoint 化および Function 送信用 VNet Integration<br>・オンプレミス ExpressRoute / VPN との実回線接続<br>・認証済み利用者への owner/org の安全な結び付け（任意ヘッダー値は本人性を証明しません）<br>・旧Starter混在中や管理API直接操作との競合保証 |

---

## Durable Task Scheduler（DTS）の PoC

`durable_backend='durableTaskScheduler'` を指定すると、Durable Functions の状態管理バックエンドとして Consumption SKU の Durable Task Scheduler・Task Hub・30 日保持ポリシー、および Function App の Managed Identity 接続を構成します（既定は従来の `azureStorage` です）。DTS 環境は別 Environment・Function App・Storage・Terraform state として分離し、既存の実行履歴や Entity 状態の移行は行いません。設定例は `environments/durable-task-scheduler.example.psd1`、詳細な構成・検証手順・制限事項は [DTS PoC](docs/durable-task-scheduler-poc.md) を参照してください。

`existing_service_plan_id`（既定 `null`）に同一サブスクリプション・リージョン内の既存 Linux App Service Plan の ARM ID を指定すると、別リソースグループの Plan であっても読み取り専用で参照・共用でき、追加の Plan を作成しません（DTS 実機検証では既存 B1 Plan を共用し、Function App・Storage・Task Hub・state を分離して実証しました）。なお、Plan 内の CPU・メモリは共有されるため、既存 Function App への性能影響がないことまでは保証しません（参考: [Dedicated Plan の仕様](https://learn.microsoft.com/en-us/azure/azure-functions/dedicated-plan)）。

---

## ワンコマンド配置

Batch 基盤を含む環境構築からアプリケーション配置までを 1 つのスクリプト（`scripts/deploy.ps1`）で一貫して実行できます。実装範囲と条件付き構成の詳細は [必要なIaCの洗い出し](docs/one-command-deployment-inventory.md) を参照してください。

- 新規 PoC 用 Batch Account / Windows Pool の作成、または既存 Batch 環境の読み取り専用参照
- Terraform state backend の準備（新規作成または既存参照）
- Function App のビルド・単体テスト・パッケージングと、検証 VM から Private SCM への Entra 認証 ZIP デプロイ
- デプロイ後の準備完了確認（Pool ノード状態および 15 個の Functions 登録検出）

### 1. 事前準備

以下のツールを用意し、`az login` を実行します。デプロイ先は設定ファイル（`.psd1`）に指定したサブスクリプションに固定され、ローカル Azure CLI の既定サブスクリプションは変更されません。

- Windows PowerShell 5.1 以降
- .NET 10 SDK
- Terraform 1.9 以降
- Azure CLI 2.48.1 以降

**前提条件とスコープ外項目**
- **事前準備が必要な項目**: Resource Provider（`Microsoft.DurableTask` 等を含む）の登録、クォータ増枠、VM 容量の確保、Marketplace 利用条件の有効化、Azure Compute Gallery の元イメージ準備（スクリプトでは Provider 登録状況の確認、および Pool の resize エラー・準備期限超過の検出を行います）。
- **現スクリプトの対象外**: Storage / Batch の全面閉域化、VPN / ExpressRoute 回線接続、`UserSubscription` 方式、Gallery 内のイメージバージョン自体のビルド・作成処理、CI/OIDC 連携。なお、Function App の受信は Private Endpoint のみとし、公開ネットワークアクセスを無効化します。

### 2. 設定ファイルの作成

#### 新規 Batch 基盤を作成する場合 (`batch_mode='new'`)
`environments/poc.example.psd1` を `environments/poc.psd1` にコピーし、以下の項目を設定します。

- サブスクリプション ID、リージョン、グローバルに一意な名前 suffix
- 既存 Gallery の正確な image version ID（新規の Compute Gallery やイメージバージョン自体を作成する処理は含みません）
  - Marketplace の Windows イメージを使用する場合は、`batch_image_source='marketplace'` と正確な image version を指定し、Gallery ID は省略します。
- Terraform state backend 名、デプロイ実行者の Object ID、および検証 VM 用 SSH 公開鍵（`validation_vm_ssh_public_key`）。公開鍵のサンプル値は必ず実際の公開鍵へ置換してください（秘密鍵は設定ファイルへ保存しません）。

新規作成される Batch 環境の仕様は以下のとおりです。
- **構成**: `BatchService` 方式、Entra 認証のみ、公開エンドポイント、`Simplified` 通信の Windows Pool（新規ノード数は 1〜3 台）
- **計算ノードの種別とスケール方式**: `batch_node_type='spot'` または `'dedicated'` を指定します（省略時は従来互換の `dedicated`）。PoC サンプルは `spot`・`batch_node_count=1` とし、Spot 1 台・専用ノード 0 台を要求します。`batch_scale_mode` は `'fixed'`（既定、固定台数）または `'autoscale'`（0 台待機から `$PendingTasks` に応じて最大 `batch_node_count` 台へ自動拡張、評価間隔 `batch_autoscale_evaluation_interval='PT5M'`）を選択できます。RHEL 検証 VM は通常 VM のまま作成されます。
- **並行動作**: 1 台・1 slot 構成の場合、3 つの Task の計算自体は順次実行され、Durable Functions 側の 3 本の監視オーケストレーションは並行して動作します。

#### Spot 利用時の条件と再開手順

Spot ノードは空き容量が保証されず、実行中に Azure 側へ回収（Evict）される可能性があります。クォータ承認後であってもノードを確保できず、Pool 準備待ちやワークフローが期限超過に達する場合があります。また、Batch によるノード回収時の Task 再キューは、非ゼロ終了に対する `MaxTaskRetryCount=0` とは別の動作です。現行 Task は結果をノードローカルへ出力し、後から Activity が回収する方式のため、回収前にノードが失われると結果を取得できない場合があります（Task から Blob への直接永続化は未実装です。参考: [Batch Spot の仕様](https://learn.microsoft.com/en-us/azure/batch/batch-spot-vms)）。

今後の配置変更時も、同じ設定・remote state で再 `plan` を実行してから `apply` し、state やノード種別の変更前に保存した古い `plan` は適用しないでください（参考: [クォータの確認](https://learn.microsoft.com/en-us/azure/batch/batch-quota-limit)）。

#### 既存の Batch 基盤を使用する場合 (`batch_mode='existing'`)
`environments/existing.example.psd1` をコピーして使用します。

- 既存の Batch Account、Pool、Gallery はすべて参照専用（読み取りのみ）として扱います。
- リソースグループを新規作成するか既存のものを使用するかは `create_resource_group` で選択します。

### 3. デプロイの実行

```powershell
# 通常デプロイ（インフラ構築 + コード配置）
./scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1

# 保存 plan の確認のみ（apply およびコード配置を行わない）
./scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -PlanOnly

# 配置後、検証 VM から 3 Task HTTP workflow を実行し、sum=3 まで検証
./scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -Smoke
```

#### 実行フローとオプションの挙動
1. **処理順序**: 事前確認 → `build` / `test` / `publish` → backend 作成または参照 → Terraform `plan` 検査 / `apply` → Pool 確認 → VM Run Command による Private SCM への Entra 認証 ZIP 配置 → 現行15個の Functions のインデックス登録（ロード）検出（`-PlanOnly` を指定しない限り、Terraform の `apply` もスクリプト内で自動実行されます）。
2. **`-PlanOnly` 指定時**: `apply` とコード配置は行いません。`Backend.Create=true` の場合、backend が作成済みであれば main plan の作成まで進み、未作成であれば bootstrap plan のみを保存します。なお、backend 参照時の権限不足や通信失敗は「未作成（不存在）」とは明確に区別してエラー停止します。
3. **Pool 確認と `-Smoke` 指定時**:
   - **新規 Pool（`fixed`）**: 指定した全台数が準備完了になるまで待機します（ノード一覧取得時のページネーションに対応し、全ノードの状態と種別を確認します）。
   - **新規 Pool（`autoscale`）または既存 Pool**: 新規 `autoscale` Pool では `enableAutoScale=true` かつ `autoScaleRun.error` がないことを確認し、既存 Pool では存在と参照権限のみを確認するため、稼働ノード数が 0 台の状態でもコードを配置できます。
   - **`-Smoke` 実行時**: 自動拡張（autoscale）設定自体は変更せず、先に Task を投入して自動拡張と完了を待機します。配置設定の `TimeoutSeconds` を用いて検証 VM 上から 3 Task HTTP workflow を検証します（単一 Task の検証には独立ランナーの `-Scenarios single` を使用します）。

### 4. 必要な権限

Function App の Managed Identity に対する権限付与は Terraform 内で実装済みですが、デプロイ実行者自身の権限は、明示した `validation_operator_principal_id` への転送専用コンテナの Blob 権限を除き自動拡張されません。実行者には事前に以下の権限が必要です。

- **インフラ作成時**: 対象リソースグループでのリソース作成権限、RBAC 割当権限（Owner の自動付与は行いません）
- **Gallery 利用時**: Pool 作成者のイメージ利用権限、および対象リージョンへの replica 配置
- **Batch 操作**: Batch データプレーンの Pool / Node 参照権限
- **VM 経由の配置・検証**: 実行者には VM Run Command 操作権限、およびカスタムロール定義の作成権限が事前に必要です。転送専用 `poc-vm-control` コンテナの `Storage Blob Data Contributor` は、任意の `validation_operator_principal_id` に実行者の Object ID を指定した場合のみ Terraform で付与します（`Backend.deployer_principal_id` と同じ ID を指定可能です）。未指定の場合は実行者が Blob データ権限を事前に用意してください。検証 VM の Managed Identity には、Function App に限定した 6 操作のカスタムロール（参照・関数一覧・function key 取得・host key 取得・設定参照・ZIP 配置）、Batch Account の `Azure Batch Account Reader`、Storage の `Storage Blob Data Reader`、および転送専用コンテナの `Storage Blob Data Contributor` を Terraform で付与します。Function の HTTP 呼び出しには既存の Function key 認証を継続します。

### 5. 出力ファイル・State 管理・安全機構

- **出力先**: ログ、`publish` 出力、ZIP、保存 plan、配置結果は `.deploy/<environment>/<実行日時>/` に保存されます。Function key、Durable 管理 URL、認証情報などの秘密情報は配置ログに出力されません。
- **Terraform State**:
  - bootstrap の local state: `.deploy/<environment>/bootstrap/terraform.tfstate`（backend の管理に必要なため保管してください）
  - アプリ環境の remote state: backend container 内の `<environment>.tfstate`
  - state backend の RG / Storage / Container には `prevent_destroy` を設定しています。また、環境名と backend・対象 RG・Batch モード・Durable バックエンド種別等の対応を記録し、同一環境名で異なる state や異なるバックエンドへ誤接続する設定変更を拒否します。
- **変更保護と再実行**:
  - 削除・置換を含む plan、既存 Batch の更新、Gallery の変更を検出した場合は `apply` 前に停止します（構成変更でリソース置換が必要な場合は個別の移行作業として扱います）。
  - 途中で失敗した場合は作成済みリソースを残して原因を表示するため、修正後に同じ設定で再実行できます。

---

## IaC（Terraform）で作成・参照されるリソース一覧

`infra/`（本体）および `infra/bootstrap/`（State 管理用）によって管理される Azure リソースの一覧です。

| カテゴリ | リソース種別 / Terraform 定義 | 作成・参照の動作 | 用途・備考 |
|---|---|---|---|
| **State 管理**<br>(`infra/bootstrap/`) | Resource Group / Storage Account / Blob Container (`tfstate`) / 実行者 RBAC | `Backend.Create=true` 時に新規作成（既存時は参照） | Terraform の remote state 保存専用。誤削除防止のため `prevent_destroy` を設定 |
| **基盤グループ**<br>(`infra/main.tf`) | Resource Group (`azurerm_resource_group`) | `create_resource_group=true` で新規作成、`false` で既存 RG を参照 | PoC リソース一式の格納先 |
| **ストレージ**<br>(`infra/main.tf`, `validation-vm.tf`) | Storage Account (`poc<suffix>`) + Container `poc-results` / `poc-vm-control` | 新規作成（共有キー無効・OAuth/MI 強制・TLS 1.2） | ・`poc-results`: ワークフロー入力 `request.json`、各 Task 結果、`aggregate.json`、`manifest.json` の保存先<br>・`poc-vm-control`: 検証 VM とのバンドル・証跡受け渡し専用<br>・Functions ホスト領域（および `azureStorage` モード時の Durable History/Queue）としても使用 |
| **ワークフロー実行**<br>(`infra/main.tf`) | App Service Plan (`B1` Linux) + Linux Function App (`.NET 10 isolated`) | Function App は新規作成。Plan は既定で新規作成、`existing_service_plan_id` 指定時は既存 Linux Plan を読み取り専用参照 | Durable Functions（HTTP 受付・状態・結果照会・キャンセル、Orchestrator・Activity・Entity 計 15 関数）をホスト。Always On 有効、公開アクセス無効（`public_network_access_enabled=false`） |
| **Durable バックエンド（選択時）**<br>(`infra/durable-task-scheduler.tf`) | Durable Task Scheduler (`Microsoft.DurableTask/schedulers`) + Task Hub + 30日保持ポリシー | `durable_backend='durableTaskScheduler'` の場合のみ AzAPI で新規作成 | Durable Functions の状態・メッセージを高速 gRPC で管理するマネージドバックエンド（Consumption SKU）。既定の `azureStorage` 選択時は作成しない |
| **計算基盤（Batch）**<br>(`infra/batch.tf`) | Batch Account (`BatchService`) + Windows Server 2022 Pool (`Standard_D2s_v5` 等) | `batch_mode='new'` で新規作成、`existing` で既存環境を読み取り専用参照 | 計算 Task のキューイングと Windows 計算ノード（`spot` または `dedicated`、1〜3 台、`fixed` または 0 台 `autoscale`）の割り当て。Gallery イメージまたは Marketplace イメージを参照 |
| **閉域ネットワーク・検証 VM**<br>(`infra/validation-vm.tf`) | VNet + Subnets (`validation-vm`, `private-endpoints`) + NSG (`DenyAllInbound`) + Public IP + NAT Gateway + Private Endpoint (`sites`) + Private DNS Zone (`privatelink.azurewebsites.net`) + RHEL 9 VM (`Standard_F1as_v7`) | 新規作成 | 公開アクセスを遮断した Function App / Private SCM に対し、VNet 内部からデプロイと HTTP 検証を行うための RHEL 9 検証 VM（Public IP なし・SSH 受信拒否・Run Command 操作・cloud-init で CLI / PowerShell 自動導入） |
| **監視・ログ**<br>(`infra/main.tf`) | Log Analytics Workspace + Application Insights | 新規作成 | Function App の実行トレースおよびテレメトリの収集（30 日保持） |
| **権限（RBAC）**<br>(`infra/*.tf`) | Built-in Role Assignments + Custom Role Definition (`validation-function`) | 新規作成（最小権限スコープ） | ・Function MI → Storage（Blob Owner / Queue・Table Contributor）、Batch（Job Submitter）、DTS Hub（Data Contributor）<br>・検証 VM MI → Function（専用 6 操作）、Storage（Blob Reader + `poc-vm-control` Contributor）、Batch（Account Reader）<br>・任意指定時のみ操作者へ `poc-vm-control` Contributor / DTS Hub Reader を付与 |

> [!NOTE]
> Batch の個別 Job / Task および結果 Blob データは、ワークフロー実行時にアプリケーションから動的に作成されます（Terraform の管理対象外）。また、既存の共有 Pool を参照する場合は、計算ノードへのアプリ配布、Pool の resize、イメージ更新は一切行いません。

---

## スクリプト・補助ツールの役割一覧

リポジトリ内の PowerShell スクリプト（`scripts/`）および .NET 補助ツール（`tools/`）の役割は以下のとおりです（各スクリプトの詳細な入出力・引数仕様・依存関係は [PowerShell スクリプト一覧・役割整理](docs/powershell-scripts-inventory.md) を参照してください）。

| スクリプト / ツール | 主な用途・実行タイミング | 具体的な処理内容 |
|---|---|---|
| [`scripts/deploy.ps1`](file:///D:/durable_functions_poc/scripts/deploy.ps1) | **ワンコマンド構築・デプロイ**<br>（初回構築・構成変更時） | 事前チェック、.NET ビルド・単体テスト・publish（POSIX 形式 ZIP 作成）、Terraform bootstrap / main の `plan`・安全性検査・`apply`、Pool ノード準備待機、および検証 VM 経由での Private SCM ZIP デプロイ・15 関数の登録確認までを一括実行します（`-PlanOnly` で計画確認のみ、`-Smoke` で 3 Task 疎通確認まで実行）。 |
| [`scripts/Test-Deployment.ps1`](file:///D:/durable_functions_poc/scripts/Test-Deployment.ps1) | **デプロイ後の準備確認・Smoke 試験**<br>（`deploy.ps1` 内部または単体呼び出し） | Function App の稼働・15 関数のロード状態、および Batch Pool の目標ノード準備完了（または 0 台 Autoscale 設定正常性）を確認します。`-Smoke` 指定時は 3 Task のワークフローを投入し、`manifest.json` の生成と `Sum=3` を検証します。 |
| [`scripts/Invoke-PrivatePocValidation.ps1`](file:///D:/durable_functions_poc/scripts/Invoke-PrivatePocValidation.ps1) | **閉域環境向け 独立シナリオ検証（入口）**<br>（デプロイ完了後のシナリオ試験時） | ローカル端末から呼び出す検証のメイン入口です。検証資材をバンドル化して検証 VM の Run Command を起動し、VM 内で `Invoke-PocValidation.ps1` を実行させた後、機密情報を除いた検証結果（`summary.json` / `summary.txt`）をローカルへ回収します。 |
| [`scripts/Invoke-PocValidation.ps1`](file:///D:/durable_functions_poc/scripts/Invoke-PocValidation.ps1) | **シナリオ検証ランナー本体**<br>（検証 VM 内または直接到達可能な環境で実行） | インフラの再配置を行わず、HTTP API 経由で各検証シナリオ（`single`, `three`, `classification`, `failure`［exit 42］, `admission20`［20並列受付］, `conflict`［409競合］, `timeout`［監視期限停止・`MaxWallClockTime` 照合］, `cancellation`［利用者キャンセル・有界停止］）を実行し、Durable・Batch・Blob の三者を突合して合否（終了コード `0`/`1`/`2`）を判定します。 |
| [`scripts/Invoke-PocRecoveryValidation.ps1`](file:///D:/durable_functions_poc/scripts/Invoke-PocRecoveryValidation.ps1) | **Function App 再起動復旧 / ホスト停止安全網の検証**<br>（耐障害性試験 `V05` / `V08` 時） | 既定（`-ValidationMode Restart`）では長時間待機 Task の `Running` 確認後に Function App を `restart` し、再送なしで同一インスタンス・Task が完走し成果物が整合することを検証します。`-ValidationMode HostStoppedBatchWallClock` 指定時は Function App を `stop` した状態で Batch 側の `MaxWallClockTime` により Job が自動終了することを検証します（いずれも `finally` で `start` 復元を実行）。 |
| [`scripts/Invoke-PocAutoscaleValidation.ps1`](file:///D:/durable_functions_poc/scripts/Invoke-PocAutoscaleValidation.ps1) | **0 台オートスケール Pool 先行投入の検証**<br>（自動拡張試験 `V09` 時） | 専用 Pool（`batch_mode='new'`）を 0 台 `steady` へ縮小して `$PendingTasks` ベースの Autoscale 式（`PT5M`）を有効化し、ノード 0 台状態から `single` シナリオを投入して 1 台への自動スケールアウトと Task 完走を確認後、`finally` で元の固定スケール構成へ復元します（`existing` 共有 Pool は拒否）。 |
| [`scripts/Invoke-WorkflowAuthValidation.ps1`](file:///D:/durable_functions_poc/scripts/Invoke-WorkflowAuthValidation.ps1) | **独自 API ワークフロー認証・認可の検証**<br>（認証境界試験時） | 検証 VM 内または直接到達可能な環境で、VM Managed Identity / IMDS から取得した Bearer トークンおよび Function key を用いて、`EntraEasyAuth` モードの独自開始・状態・結果 API への認可、401/403 拒否、所有者照合、および機密非露出を検証するランナーです（内部診断用の EasyAuth 除外パスは設けず、直接独自 API を検証）。 |
| [`scripts/Invoke-PrivateVmOperation.ps1`](file:///D:/durable_functions_poc/scripts/Invoke-PrivateVmOperation.ps1)<br>[`scripts/PrivateVm.Worker.ps1`](file:///D:/durable_functions_poc/scripts/PrivateVm.Worker.ps1) | **検証 VM 転送・ゲスト実行基盤**<br>（各スクリプトから内部呼び出し） | ローカル端末と RHEL 9 検証 VM の間で、`poc-vm-control` Blob コンテナと Azure VM Run Command を用いてバンドル転送・cloud-init 待機・ゲスト内処理実行・証跡回収を安全に仲介する共通機構です。 |
| [`scripts/Deployment.Common.ps1`](file:///D:/durable_functions_poc/scripts/Deployment.Common.ps1)<br>[`scripts/Validation.Assertions.ps1`](file:///D:/durable_functions_poc/scripts/Validation.Assertions.ps1)<br>[`scripts/Recovery.Context.ps1`](file:///D:/durable_functions_poc/scripts/Recovery.Context.ps1) | **共通関数ライブラリ**<br>（内部モジュール） | 設定ファイル（`.psd1`）の検証、POSIX 互換 ZIP の生成、Terraform plan の破壊的変更検査、シークレット秘匿フィルタリング、および状態照合アサーションを提供する内部モジュール群です。 |
| [`tools/DtsProbe`](file:///D:/durable_functions_poc/tools/DtsProbe/Program.cs) | **DTS データプレーン直接照会ツール**<br>（DTS 環境の独立証跡確認時） | Durable Task Scheduler のエンドポイントへ読み取り専用で接続し、対象インスタンスの状態（`runtimeStatus`）や保持履歴のイベント件数集計（`--history true`）をペイロード非表示で安全に出力する .NET CLI ツールです。 |
| [`tools/BatchSmoke`](file:///D:/durable_functions_poc/tools/BatchSmoke/Program.cs) | **Batch 単体疎通の補助診断ツール**<br>（開発端末からの直接診断用） | Functions を介さずに開発端末から Batch エンドポイントへ直接ダミー Task を 1 件投入して動作を切り分けるための補助 CLI ツールです（通常の検証では上記の `Invoke-PrivatePocValidation.ps1` を使用します）。 |
| `scripts/tests/*.Tests.ps1` | **スクリプトのオフライン単体試験**<br>（ローカル CI / 回帰テスト用） | Azure へ一切接続せずに、Fake / Mock を用いてデプロイ制御、シナリオ判定、VM 転送、再起動復旧、0 台オートスケール、およびワークフロー認証検証スクリプトのロジックとエラーハンドリングを検証するテストスイート（8 組）です。 |

---

## 必要な環境とホスティング構成

### 必須ツール・権限
- **ツール**: .NET 10 SDK、Azure Functions Core Tools v4、Azure CLI、Terraform 1.9 以降
- **認証・参照権限**: Azure へのログイン、既存 Batch の Microsoft Entra 認証、既存 Pool の参照権限
- **デプロイ実行者権限**: PoC リソース作成権限、指定スコープの RBAC 作成権限（Resource Provider は事前登録済みを前提とし、Terraform での自動登録は無効化しています）
- **ローカル実接続の実行者権限**: Batch Account の `Azure Batch Job Submitter`、結果コンテナの Blob データ読み書き権限（クラウド上の host storage をローカル Functions host から使用する場合は、後述の host storage 権限も必要です）

### Function App のホスティング選定理由
本構成では .NET 10 isolated worker と Azure Functions v4 を使用します。[公式の isolated worker ガイド](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide) にあるとおり Linux Consumption プランでは .NET 10 を利用できないため、Always On を有効にした **Linux Dedicated (App Service Plan B1)** を選択しています。詳細は [ホスティングガイド](https://learn.microsoft.com/en-us/azure/azure-functions/functions-scale) および [Terraform AzureRM `linux_function_app` リソース仕様](https://github.com/hashicorp/terraform-provider-azurerm/blob/main/website/docs/r/linux_function_app.html.markdown) を参照してください。

---

## 1. ローカル検証

リポジトリルートで以下のコマンドを実行し、Azure に接続せずにビルドとテストを検証できます。

```powershell
dotnet restore src/Poc.Functions/Poc.Functions.csproj
dotnet restore DurableBatchPoc.slnx
dotnet build DurableBatchPoc.slnx --no-restore
dotnet test --project tests/Poc.Tests/Poc.Tests.csproj --no-restore
dotnet test --project tests/DtsProbe.Tests/DtsProbe.Tests.csproj --no-restore

# 配置・検証制御のオフライン試験（Azureへ接続しない）
./scripts/tests/Deployment.Common.Tests.ps1
./scripts/tests/Test-Deployment.Tests.ps1
./scripts/tests/Deploy.Workflow.Tests.ps1
./scripts/tests/PocValidation.Tests.ps1
./scripts/tests/PrivateVm.Tests.ps1
./scripts/tests/RecoveryValidation.Tests.ps1
./scripts/tests/AutoscaleValidation.Tests.ps1
./scripts/tests/WorkflowAuthValidation.Tests.ps1

# Terraform mock_provider試験（providerの取得後、Azureへ接続しない）
terraform -chdir=infra test
terraform -chdir=infra/bootstrap test
```

### 配置済み環境を独立して検証する

`Invoke-PrivatePocValidation.ps1` は VM Run Command 経由で検証を開始する入口です。検証 VM 上では `az login --identity` 後に `Invoke-PocValidation.ps1` を実行します。`Invoke-PocValidation.ps1` は、デプロイ済みの環境に対して独立してシナリオ検証を実行するためのテスト実行スクリプト（ランナー）です。デプロイ処理や Pool 設定の変更は行わず、HTTP 経由でテスト用 Job / Task を投入して動作を検証します。実行には `az login` が完了していることに加え、デプロイ結果ファイルに記録されたサブスクリプションにおける Function キー取得・設定参照権限、Batch Job / Task の参照権限、および結果 Blob の読み取り権限が必要です。

```powershell
# インフラの再デプロイを行わず、正常系・分類・意図的な Task 失敗シナリオを検証
./scripts/Invoke-PrivatePocValidation.ps1 `
  -DeploymentResultPath .deploy/poc/<run>/deployment-result.json `
  -Scenarios single,three,classification,failure `
  -EvidenceDirectory artifacts/validation/first-run

# 同一入力の 20 並列投入・完了後再送、および異なる入力パラメータによる 409 競合を検証
./scripts/Invoke-PrivatePocValidation.ps1 `
  -DeploymentResultPath .deploy/poc/<run>/deployment-result.json `
  -Scenarios admission20,conflict

# 監視期限試験: 短い WORKFLOW_TIMEOUT_MIN を設定した専用環境のみ
./scripts/Invoke-PrivatePocValidation.ps1 `
  -DeploymentResultPath .deploy/timeout-poc/<run>/deployment-result.json `
  -Scenarios timeout -DedicatedEnvironment -TimeoutSeconds 900
```

デフォルトのシナリオは `single,three,classification,failure,admission20,conflict` です。`TimeoutSeconds` はシナリオ内の待機に適用され、既定 900 秒、指定範囲 60〜3600 秒、ポーリング間隔は 10 秒です。Run Command では cloud-init の準備待ちを最大 900 秒、以後のゲスト操作全体を最大 4200 秒（70 分）に制限し、Action Run Command の 90 分制限内に収めます。長い検証は `-Scenarios` を指定して複数回に分けてください。タイムアウト試験（`timeout`）では実際のデプロイ設定値および Batch Job の `maxWallClockTime` 制約を確認し、設定された Task 遅延時間とランナーの待機上限の組み合わせでタイムアウトを安全に観測できない構成の場合は `NotRun` と判定します。なお、ランナー側の待機期限が超過した場合でも、Azure 上の計算 Job が正常に停止完了したとはみなしません。

`-DedicatedEnvironment` は試験専用の環境を指定するスイッチであり、専用 Batch ノードを指定するものではありません（Spot ノードを使用する試験専用環境でも指定します）。

検証結果は `summary.json` および `summary.txt` に出力され、シナリオごとの合否判定（`Pass` / `Fail` / `NotRun`）、UTC 実行時刻、Git コミット情報、デプロイパッケージのハッシュ値、各種観測値が保存されます。VM 経由ラッパーの既定の回収先は `artifacts/private-validation/<run>/` です。VM 内で直接使用するランナーの既定保存先は `artifacts/validation/<run>/` であり、プロセスの終了コードは「全選択試験が Pass」で `0`、「Fail が 1 件以上あり」で `1`、「Fail はないが NotRun が含まれる」で `2` となります。認証情報、キー、シークレットを含む URL、生の例外スタックトレースは証跡ファイルへ出力されません。なお、同一の出力先を指定すると前回の結果要約が上書きされるため、実行ごとに個別のディレクトリを指定してください。

`admission20` シナリオでは、同一インスタンスへの集約、投入 Task 数、完了後の再送動作を観測します。ただし、現在の Azure Functions / Durable 管理 API のみでは全世代にわたる Main オーケストレーションの起動履歴を完全には証明できないため、観測値を記録したうえでステータスを `NotRun`（未実証）とし、厳密な「1 度限りの起動（exactly-once schedule）」が実環境で成立したとは断定しません。また、検証ランナー自身はリクエスト入力、Entity 状態、Main オーケストレーション履歴を清掃しません。DTS を選択した場合は Scheduler の 30 日保持ポリシーが別途適用され、期限後の履歴保持は保証しません。

個人の検証環境では VNet 内の検証 VM から Function の Private Endpoint を呼び出します。ローカル端末から Function への直接到達性は不要です。転送用 bundle を専用の転送 Blob コンテナにアップロードし、VM の Managed Identity で取得して実行し、安全な証跡のみ Blob 経由で回収します。NAT Gateway を通る外部通信と Storage / Batch の公開データエンドポイントは今回維持します。VPN / ExpressRoute 接続と Storage / Batch の全面閉域化は [共通ネットワーク設計](docs/network-connectivity-design.md) に記載した後続フェーズです。なお、ローカル環境での Fake / Mock によるテスト成功と、Azure 実環境での接続試験の結果は明確に区別して評価してください。

ランナーと Azure 側の処理順序、生成される成果物、およびシナリオ別の詳細な判定条件については [検証ランナーの処理フロー](docs/validation-runner-flow.md) を参照してください。

- **リストア順序**: 現行の `Azure.Functions.Sdk` の生成ターゲットを準備するため、先に Function プロジェクト（`Poc.Functions.csproj`）を直接 restore してからソリューション全体を restore します。
- **テスト実行**: `global.json` で `Microsoft.Testing.Platform` runner を指定しているため、`dotnet test` 実行時は `--project` オプションを使用します。
- **モック構成**: 単体テストは `FakeGateway` とインメモリの結果保存を使用し、Azure への通信は発生しません。最新コードの Release 検証では全 270 件（`Poc.Tests` 268/268 件、`DtsProbe` 2/2 件、失敗・スキップ 0 件）が合格し、Release publish も成功しました。前回の 14 関数実機検証済みコードから、V12 利用者キャンセル（`CancelWorkflow`）が追加され計 15 関数となっています。前回の 14 関数コードでの Azure 実機成功（スモークおよび閉域 5 シナリオ）に対し、15 関数コードの Azure 実機回帰は未実施である境界に注意してください。詳細は [検証結果](docs/test-results.md) を参照してください。

---

## 2. Terraform の確認と適用（手動実行手順）

通常は前述の `./scripts/deploy.ps1` を使用しますが、既存の backend へ手動で接続して Terraform を実行する場合は以下の手順に従います。

### 事前準備と State 移行の注意点
- `infra/` は AzureRM remote backend を使用するため、事前に state 用の Storage Account / Container と実行者の Blob データ権限を用意してください。
- `backend.hcl` には `subscription_id`、`resource_group_name`、`storage_account_name`、`container_name`、環境固有の `key`、`use_azuread_auth=true` を記載します（`deploy.ps1` の実行で生成された `.deploy/<environment>/<実行日時>/backend.hcl` も流用可能です）。
- 以前の local state を使用している場合は、state をバックアップしたうえで、同一リソースを対象とする backend へ `terraform init -migrate-state` で明示的に移行してください。空の remote state に接続して再 `apply` すると既存リソースとの競合が発生します（配置スクリプトによる自動 `import` や暗黙の state 移行は行われません）。

### Plan と Apply の実行

```powershell
az login
az account set --subscription <subscription-id>
Copy-Item infra/terraform.tfvars.example infra/terraform.tfvars
# terraform.tfvars の既存リソース ID と一意な suffix を編集
terraform -chdir=infra init -backend-config=<backend.hclの絶対パス>
terraform -chdir=infra fmt -check
terraform -chdir=infra validate
terraform -chdir=infra plan -out=poc.tfplan
terraform -chdir=infra show poc.tfplan
```

plan の内容を以下のように確認したうえで、`apply` を実行します。
- **`batch_mode=existing` の場合**: 既存の Batch Account / Pool / Gallery / RG に対する作成・更新・削除が含まれておらず、既存 Batch への変更が Function Identity に対する `Azure Batch Job Submitter` ロール割り当てのみであることを確認します。
- **`batch_mode=new` の場合**: PoC 専用の Batch Account / Pool も新規作成対象に含まれます（設定例は `infra/terraform.new.tfvars.example` を参照）。

```powershell
terraform -chdir=infra apply poc.tfplan
terraform -chdir=infra output
```

### プロバイダー管理・セキュリティ・RBAC 仕様
- **Provider バージョン**: AzureRM provider は `5.10.x` に固定しており、検証時の `5.10.0` の `.terraform.lock.hcl` を同梱しています。provider 更新時は lock ファイルの差分をレビューしてください。
- **機密情報の保護**: `terraform.tfvars`、state、plan、認証情報、Function キーや Durable 管理 URL は Git にコミットしないでください。state ファイルはアクセスを制限し、本番利用時は暗号化とロックを備えた remote backend を別途構成してください。
- **Storage の Managed Identity 接続**: Function の host storage は共有キー接続を使用せず（Storage 側でも共有キーは無効化）、`storage_uses_managed_identity` と `AzureWebJobsStorage__accountName` / `__credential=managedidentity` を設定します。PoC Storage スコープに対して、[host storage の必要権限](https://learn.microsoft.com/en-us/azure/azure-functions/manage-connections?tabs=host%2Cidentity)（`Storage Blob Data Owner`）と [Durable Functions の必要権限](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-configure-managed-identity)（`Storage Queue Data Contributor` / `Storage Table Data Contributor`）を付与します。実行結果も同じ PoC Storage に保存するため、追加の Blob 権限は不要です。デプロイ後は RBAC の反映後に Functions host が正常起動することを確認してください。
- **Batch ロールのスコープ**: [`Azure Batch Job Submitter`](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/compute#azure-batch-job-submitter) は Pool の変更権限を持ちませんが、アカウント内の全 Job にアクセス可能であり、RBAC レベルでの Job ID prefix による制限はできません。PoC 用の `poc-` prefix 制限はアプリケーション側で実施します。共有 Batch Account へ適用する場合は、このスコープの権限付与が認められる環境で実施してください。

---

## 3. 設定

| 設定 | 意味 / 初期値 |
|---|---|
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `AzureWebJobsStorage__accountName` | PoC host storage 名 |
| `AzureWebJobsStorage__credential` | Azure 環境では `managedidentity`。ローカルで CLI 資格情報を使う場合は設定しない |
| `BATCH_ENDPOINT` | 既存 account の `https://...batch.azure.com` |
| `BATCH_POOL_ID` | 既存 Windows Pool ID |
| `BLOB_ENDPOINT` | PoC Blob service URI |
| `BLOB_CONTAINER` | `poc-results` |
| `BATCH_POLL_SECONDS` | `10` |
| `WORKFLOW_TIMEOUT_MIN` | `15` |

- **認証方式**: SDK の認証には `DefaultAzureCredential` を使用します（Azure 上では System-assigned Managed Identity、ローカル実行時は `az login` の開発者資格情報などを使用）。キーや SAS トークンはソースコードに埋め込みません。
- **ローカル実行設定**: ローカル実行時は `src/Poc.Functions/local.settings.json.example` を `local.settings.json` にコピーして上記項目を設定します。ローカルの host storage に Azurite を使用する場合は、Azurite を起動して `AzureWebJobsStorage=UseDevelopmentStorage=true` を設定し、`AzureWebJobsStorage__*` の項目を削除してください（結果保存用の `BLOB_ENDPOINT` は実 PoC Storage のままにします）。

---

## 4. Step A: 既存 Windows Pool の smoke テスト

通常の単一 Task 検証は `Invoke-PrivatePocValidation.ps1 -Scenarios single` で VM から HTTP workflow を起動します。以下の BatchSmoke は、Batch エンドポイントへ直接接続できる開発端末で使う補助診断ツールです。ワンコマンド配置の `-Smoke` からは呼び出しません。

ワークフロー認証は既定`PocFunctionKey`を維持し、明示的な`EntraEasyAuth`設定時だけEasyAuthとアプリ内App Role検証を有効にします。各独自APIにはBearerとFunction key、owner/orgが必要です。設定契約・Entra登録の手順と制約は[認証境界](docs/workflow-authentication-design.md)を参照してください。Entra登録、App Role割当、`idtyp` optional claimは自動変更しません。Azure配置・両Durableバックエンドの認証実証は未完です。

厳格モードの`deploy.ps1 -Smoke`は事前検証で拒否します。既存のSmoke・Private/通常シナリオ・Recovery/Autoscale runnerは、内部Durable診断を含めBearer未対応です。新しい`scripts/Invoke-WorkflowAuthValidation.ps1`はVM Managed IdentityのIMDSを用い、独自開始・状態・結果APIを検証する限定runnerです。SCM配置の既存Entra/ARM token認証とは別経路で、診断用EasyAuth除外パスは追加しません。

```powershell
$env:BATCH_ENDPOINT = 'https://<account>.<region>.batch.azure.com'
$env:BATCH_POOL_ID = '<existing-windows-pool>'
dotnet run --project tools/BatchSmoke
```

- **確認内容**: ログに Job ID、Task ID、状態、ファイル名、取得値が出力されることを確認し、`artifacts/` 配下にログを保存します。この smoke テストが成功するまでは、実現性評価の `F01` を Pass とは扱いません。
- **使用 SDK**: [Azure.Compute.Batch の現行ガイド](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/compute.batch-readme) に従い、`BatchClient` と Entra 認証を使用します（旧 `Microsoft.Azure.Batch` のコードは混在させません）。
- **コマンド仕様**: 暗黙のシェル解釈に依存しないよう `cmd /c powershell.exe -NoProfile -NonInteractive -Command "..."` を明示し、整数の待機時間と固定出力のみを埋め込みます（[Batch コマンド仕様](https://learn.microsoft.com/en-us/azure/batch/jobs-and-tasks) および [Task File API](https://learn.microsoft.com/en-us/rest/api/batchservice/tasks/get-task-file) を参照）。なお、ローカル Windows 上でのクォーティング確認だけでなく、実 Pool 上の PowerShell 実行ポリシー等で正常に動作するかをこのステップで確認します。

---

## 5. Step B: 起動、状態照会、Blob 確認

### 1. Functions の起動またはデプロイ

**ローカルから実 Azure リソースへ接続して起動する場合:**

```powershell
Push-Location src/Poc.Functions
func start
Pop-Location
```

**Azure Function App へ配置する場合:**
Azure への配置は冒頭の `deploy.ps1` を使用します。ローカル端末でビルドした ZIP を検証 VM へ渡し、VM 内で Managed Identity のトークンを取得して Private SCM の ZIP API へ配置します。VM に .NET SDK や Terraform を追加する必要はありません。

```powershell
./scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1
```

### 2. ワークフローの開始と状態照会

以下はローカルエンドポイントへのリクエスト例です。所有者ヘッダーは受付・照会時の照合に使いますが、呼出元が任意に送信できるため利用者認証にはなりません。Azure 上では各 API に有効な Function key を `x-functions-key` に指定してください。開始専用の Function key は別 Function の状態照会には使えません。

```powershell
$body = @{
  requestId = 'feasibility-001'
  classification = 'auto'
  taskCount = 3
  failTaskIndex = $null
  lightDelaySeconds = 5
  heavyDelaySeconds = 15
} | ConvertTo-Json
$headers = @{ 'X-Poc-Owner-Id' = 'local-example-owner' }
$started = Invoke-RestMethod -Method Post -Uri 'http://localhost:7071/api/workflows' -ContentType 'application/json' -Headers $headers -Body $body
$started
# 応答中の statusQueryGetUri を使用して状態を照会
Invoke-RestMethod -Uri $started.statusQueryGetUri -Headers $headers
```

開始202の `statusQueryGetUri` と `Location` は独自の `/api/workflows/{requestId}` を指し、キーを含むDurable管理URLを返しません。`Retry-After` は10秒です。内部検証runnerは開始URL契約を検証した後、system keyから管理URIをメモリ内で生成して詳細診断を続けます。キー・管理URIはログや証跡へ保存しません。この変更はV11の一部であり、Entra認証・App Role検証・所有者伝播の確定および最新コードのAzure実証は未完です。

独自状態照会の `resultAvailable` は、公開manifest・aggregate・全Task結果の存在と現行PoC契約の整合性を確認した時点の値です。DurableがCompletedでも成果物欠損時はfalseとなり、結果取得は409を返します。不整合時は固定メッセージの500、一時障害時は503と `Retry-After: 10` を返します。結果取得時にも再検証するため、状態照会後の削除を含め取得成功を保証する値ではありません。

独自HTTP応答、custom statusのエラー表示、明示的な失敗ログには固定の説明またはコードを使用します。Activity wrapperも分類後に固定message・innerなしの安全な例外（SafeActivityException）を渡し、再試行・timeout・caller取消の分類を維持します。また、gRPC通信の一時障害再試行対象は Unavailable, DeadlineExceeded, ResourceExhausted の3ステータスコードに限定しています。対象はwrapperから外へ渡す例外情報であり、Activity入出力や別経路を含む全ログ・全履歴での機密情報非露出を保証するものではありません。

#### パラメータと受付制御（冪等性・重複抑止）の仕様
- **入力パラメータ**: `taskCount` は `1`〜`3`、`classification` は `light` / `heavy` / `auto` を指定できます。
- **受付の確定と競合検出**: `runs/poc-<requestId>/admission.json` の単一不変レコード（Version 2）へ入力・instanceId・所有者/組織を条件付き作成します。所有者なしも固定し、同一所有者・入力だけ再送できます。所有者・組織・所有者の有無が異なる場合は `HTTP 403`、所有者一致で入力が異なる場合は `HTTP 409` を返します。任意のowner/orgヘッダーは本人性を証明しません。
- **同時受付・再送時の動作**: 同一 `requestId` かつ同一所有者・入力の同時リクエストや再送は、同じ Durable Entity へ通知されます。Entity が「起動済み状態」と「Main オーケストレーションの起動指示」を永続化して重複を抑止します。受付Blobの作成とDurable通知は非原子的で、通知失敗時も受付を保持し同一内容の再送で回復します。
- **旧受付の互換**: 旧 `request.json` / `owner.json` を書き換えず、整合する旧受付は再送時に新レコードへ正規化します。所有者なし旧受付への所有者追加、所有者のみ残る旧受付、新旧の矛盾は拒否します。切替時は旧Starterを停止してください。詳細は [信頼性設計](docs/workflow-reliability.md) を参照してください。今回の変更はローカル実装で、Azure配置・実機検証は未実施です。
- **ステータスコードと再試行**:
  - `HTTP 202 (Accepted)` は Durable Entity への通知受理を表します。所有者付きで受け付けたジョブの独自状態照会には、受付時と同じ所有者情報が必要です。内部診断のDurable管理APIは、Main実インスタンスの登録前に一時的な `404` を返す場合があります。
  - 受付確認中の一時的な障害時は `HTTP 503` と `Retry-After` ヘッダーを返します。その場合は同じ `requestId` と入力内容で再送できます。

#### 利用者キャンセル仕様（V12）
- **エンドポイント**: `POST /api/workflows/{requestId}/cancel`
- **所有者認可**: 受付時に記録された所有者（`owner` / `organization`）とキャンセル要求元の呼出元情報（`X-Poc-Owner-Id` / `X-Poc-Org-Id` または Entra 認証トークン）を照合します。他者または別組織からの要求は `HTTP 403 Forbidden` で拒否され、認可情報不正時は `HTTP 400 Bad Request` を返します。
- **先行永続化**: Durable オーケストレーションへのイベント通知前に、Blob Storage 上の `runs/poc-<requestId>/cancel.json` へキャンセル要求レコード（`WorkflowCancelRecord`）を条件付き作成（先行永続化）します。これにより、Orchestrator が起動前・初期化中であってもキャンセル要求が確実に記録・補足されます。
- **Durable 外部イベント待機**: `MainOrchestrator` は実行中、外部イベント `CancelRequested`（イベント名: `CancelRequested`）を待機します。子オーケストレーション（`MonitorBatchTaskOrchestrator`）の並列監視完了待ち（`Task.WhenAll`）と `Task.WhenAny` により競合を調停します。
- **Batch ジョブの有界停止**: キャンセル検知時、直ちに `TerminateBatchJobActivity` を呼び出し、実行中の Batch ジョブを有界（最大 2 分、5 秒間隔ポーリング）に安全停止します。
- **成果物公開の排他調停**: キャンセルが成立した場合は後続の集約処理および `manifest.json` の公開を厳格に抑止します。カスタムステータスを `state = "Cancelled"`, `errorCode = "workflow_cancelled"` に確定し、ワークフローを例外（`WorkflowCancelledException`）により安全に終了します。
- **ステータスコードと冪等性**:
  - 実行中のワークフローに対するキャンセル要求には `HTTP 202 Accepted` を返却します。
  - すでに完了・失敗・キャンセル済みの終端状態にあるワークフローに対する再度のキャンセル要求には、冪等に `HTTP 200 OK`（`status: "Cancelled"` 等）を返却します。
  - 存在しない `requestId` の場合は `HTTP 404 Not Found` を返却します。

### 3. 実行結果 Blob の確認

ワークフローが正常完了すると、PoC コンテナ（`poc-results`）内の `runs/poc-<requestId>/` 配下に各 Task の結果、`final/aggregate.json`、および `final/manifest.json` が作成されます。

- 3 つの Task がそれぞれ値 `1` を出力した場合、`sum=3` / `taskCount=3` となります。
- Batch の Job ID は `requestId` のハッシュから決定的に導出されます（後の清掃作業では、実際のログや Durable 出力に記録された ID と prefix を確認して使用します）。

```powershell
$storageName = terraform -chdir=infra output -raw storage_account_name
az storage blob list --account-name $storageName --container-name poc-results --auth-mode login --query '[].name' -o table
# 出力で確認した完全な Blob 名を指定してダウンロード
az storage blob download --account-name $storageName --container-name poc-results --name '<run-prefix>/final/manifest.json' --file artifacts/manifest.json --auth-mode login
```

---

## 6. Step C: 故障と再開の試験

再起動復旧ランナー（`V05`）、タイムアウト停止（`V07`）、Function ホスト停止中の Batch 側 `MaxWallClockTime` 安全網（`V08`）、0 台オートスケール Pool 先行投入（`V09`）、および DTS 履歴集計の実行手順と実機検証結果は [信頼性の追加検証](docs/reliability-validation.md) を参照してください（これらはローカル試験および実 Azure 環境の双方で Pass を確認済みです）。

各異常系・再開シナリオの検証手順は以下のとおりです。

- **非ゼロ終了（Task 失敗）**: 新しい `requestId` と `failTaskIndex=1` を指定して実行します。他の兄弟 Task の自然終了を待ったうえで親オーケストレーションが失敗（Failed）となり、`aggregate.json` / `manifest.json` が公開されないことを確認します。
- **永続 Timer 待機**: `classification=heavy` と長い待機時間を指定して実行し、`HTTP 202` が返ること、および実行継続中のステータスを照会できることを確認します。
- **Function App 再起動からの再開**: 上記の Timer 待機中に `az functionapp restart --resource-group <poc-resource-group> --name <poc-function-name>` を実行します（再起動対象は PoC 専用 Function App のみに限定し、共有 Pool や計算ノードは再起動しません）。再開後の instance ID、Activity 履歴、最終的な `manifest.json` を保存します。
- **結果ファイルの欠損**: `FakeGateway` で Task ファイル取得時の例外を模擬し、成功と誤認しないことを検証します（実ノード上のファイルを直接削除する操作は既定手順には含めません）。
- **監視期限超過（タイムアウト）**: `WORKFLOW_TIMEOUT_MIN` を Task の待機時間より短く設定した専用試験を実行し、オーケストレーションが `TimedOut` となり結果が未公開であることを確認します。
  - 期限超過時、親オーケストレーションは対象 PoC Job の ID・Pool・fingerprint を照合して Job を終端化（Terminate）し、最大 2 分間、停止完了を確認します。
  - 停止要求の送信成功と実際の停止完了は区別して扱われます。ステータス照会の `customStatus.termination` および `terminationError` で停止確認の結果を確認してください。

Application Insights では、`instanceId`、`Job ID`、`Task ID` を手掛かりに Activity の実行ログを追跡できます。状態照会結果、Batch 状態、Blob 一覧、試験時刻、およびログの保存場所を [検証結果](docs/test-results.md) に記録してください。

---

## 7. 清掃（クリーンアップ）

清掃を行う前に、対象の Durable オーケストレーションが終端状態（Completed / Failed / Terminated 等）に到達していることを確認してください。期限超過試験の後は Job の停止完了も確認し、自動での停止確認に失敗していた場合は対象の PoC Job の状態を手動で確認します。

### 1. Batch Job の個別削除（既存 Account 利用時）
確認済みの PoC Job（`poc-` prefix）のみを 1 件ずつ削除します。既存の共有 Pool や計算ノードは停止しません。

```powershell
# Azure CLI Batch data-plane login（アカウント共有鍵ではなく Entra token を使用）
az batch account login --resource-group <existing-batch-rg> --name <existing-batch-account> --auth-mode aad
$pocJobId = '<実行ログに記録された完全なpoc-Job-ID>'
if (-not $pocJobId.StartsWith('poc-', [StringComparison]::Ordinal)) { throw 'PoC Job ID のみ削除可能' }
az batch job show --job-id $pocJobId
# 表示内容を確認後、対象の 1 件のみを削除
az batch job delete --job-id $pocJobId --yes
```

### 2. 結果 Blob の個別清掃
Blob を個別に削除する場合は、PoC 専用コンテナ `poc-results` 内で、実行出力に記録された完全な run prefix（`poc-` 対象）に一致するものだけに限定します。共有コンテナに対するワイルドカード削除は行いません。

### 3. PoC リソース全体の破棄（Terraform Destroy）
PoC 環境全体を破棄する場合は、事前に必要な証跡（PoC Storage 内の状態履歴や結果 Blob）を保存したうえで、以下の手順で destroy plan を作成・確認してから適用します。

```powershell
terraform -chdir=infra plan -destroy -out=poc-destroy.tfplan
terraform -chdir=infra show poc-destroy.tfplan
# PoC Storage の状態履歴と結果も消えるため、必要な証跡を先に保存してから実行
terraform -chdir=infra apply poc-destroy.tfplan
```

- **削除対象の範囲**:
  - `batch_mode=existing`（既存参照モード）では、既存の Resource Group、Batch Account、Pool、Gallery は destroy 対象外となり、PoC で作成したリソースと RBAC のみが削除されます。
  - `batch_mode=new`（新規作成モード）では、PoC 専用 RG と Batch Account / Pool も destroy 対象に含まれます（Batch Job 自体は Terraform 管理外ですが、新規 Batch Account を削除すれば内部の Job もあわせて削除されます）。
  - 実行時は対象環境の backend 設定と `tfvars` を指定した destroy plan で削除対象を必ず確認してください。
- **State Backend の保護**: state backend は独立した bootstrap root で管理されており、`prevent_destroy` が設定されているため、アプリ環境の destroy には含まれません。

---

## 制限事項と信頼性設計の前提

本 PoC におけるリトライ、タイムアウト、重複抑止の設計根拠と公式ドキュメントの確認結果は [信頼性改善の設計](docs/workflow-reliability.md) にまとめています。運用・評価時は以下の仕様と制限事項に留意してください。

### 1. 再試行（リトライ）と実行保証
- **Activity の再実行耐性**: Durable Activity は at-least-once（最低 1 回）実行です。決定的な Job / Task ID、入力内容の照合、Blob の条件付き公開（If-None-Match 等）を組み合わせることで再実行時の安全性を確保しています。
- **リトライ対象と回数**: 一時的な通信障害および HTTP `408` / `429` / `500` / `502` / `503` / `504` のみを対象として、Durable 側で最大 3 回試行します（待機間隔は 5 秒・10 秒。監視 Activity は親子共有の監視期限を超えて再試行しません）。SDK 側のリトライは 1 回、各通信試行の `NetworkTimeout` は 10 秒に設定しています。
- **リトライ非対象**: 入力不正、権限不足、構成競合、結果不整合などの恒久的エラーは Durable では再試行しません。
- **Task の実行保証**: Batch Task 自体の retry 回数は `0` を基本としていますが、計算ノード障害からの復旧に伴う再実行は発生し得るため、計算処理の厳密な **exactly-once（1 回限り実行）は保証しません**。

### 2. タイムアウト制御とキャンセルの制限
- **監視期限の共有と競合制御**: ワークフローの監視期限は、分類（classification）完了後から親オーケストレーションと子オーケストレーションの間で共有されます。Durable Timer は実行中の Activity を物理的に中断できないため、期限到達時の Task 作成と Job 停止の競合は、Batch 側の Job 終端状態（Terminating / Completed）によって制御します。ただし、**Job 作成 Activity だけは、停止処理の後に遅れて Job が作成され停止を逃れる事態を防ぐため、完了を待ってから停止処理へ進みます**。
- **`MaxWallClockTime` の位置づけ**: Functions host が停止している間の安全網として Batch Job にも `MaxWallClockTime` を設定していますが、こちらは「Job 作成時点」からの起算であり、オーケストレーションの監視期限や通信・Activity 実行時間を含めた厳密な実時間上限とは異なります。
- **利用者キャンセル連鎖（V12）の実装**: 利用者キャンセル要求（`POST /api/workflows/{requestId}/cancel`）により、先行 Blob 永続化（`cancel.json`）と Durable 外部イベント（`CancelRequested`）を通じて実行中ワークフローを検知し、Batch Job の有界停止（`TerminateBatchJobActivity`）と `manifest.json` 公開抑止を連動させます。所有者認可（他者 403 拒否）および完了後の 200 冪等応答をローカル実装・テスト完了済みです（単体テスト全 270 件・PS オフライン全 8 組 Pass。15 関数コードの Azure 実機回帰は未実施）。通常の Task 失敗時は他の兄弟 Task の自然終了を待機します。

### 3. 受付の重複抑止の前提条件
- **状態保持の前提**: 同一 `requestId` に対する受付の重複抑止は、受付 Blob（`admission.json`、旧受付の `request.json`）、Durable Entity の状態、および Main オーケストレーションのインスタンス履歴が保持された状態で本 API を経由することを前提としています。これらを個別に削除（purge）して同じ ID を再利用する運用や、Durable の管理 API から同じインスタンス ID を直接起動する操作は保証対象外です。
- **デプロイ時の注意**: 既存のインスタンスが存在する場合は入力内容を照合して再利用しますが、旧バージョンの starter との同時稼働は避け、実行中の処理がすべて終了してから本バージョンをデプロイしてください。

### 4. ファイル転送サイズと本 PoC の評価スコープ外項目
- **結果ファイルのサイズ制限**: Batch の Task File API を用いた結果取得は、数 KB 以下の小さな結果データ専用です。また、計算ノードのローカルファイルはノード障害や保持期限切れによって失われる可能性があります。本番環境で大容量の出力を扱う場合は、Batch の `OutputFiles` 機能や計算ノードからの直接 Blob 永続化、および失敗時の修復機構を別途検討する必要があります。
- **未検証の領域**: LSF / HPC Pack の完全代替可否、300 ノード規模での性能検証、Fair Share スケジューリング、Backfilling、MPI ジョブ、商用業務アプリケーションのライセンス管理については本 PoC の検証対象外です。
