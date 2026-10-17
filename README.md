# Azure Batch + Durable Functions PoC

Windows Batch Pool への PowerShell Task 投入から、Durable Functions（子オーケストレーションおよび永続 Timer）による完了待機、Batch File API 経由での結果取得、Blob 保存・集約、Manifest 公開までの一連のワークフローを検証する PoC です。

設計の背景と実装要件については、[設計書](docs/azure-hpc-modernization-poc-design.md) および [実装指示書](docs/CODEX_IMPLEMENTATION_INSTRUCTIONS.md) を参照してください。

> [!IMPORTANT]
> **検証ステータスに関する注記**
> Azure Storage バックエンドでのインフラ配置と RHEL 9 検証 VM の初期設定が完了し、Private SCM 配置、12 Functions の登録、VM から Private Endpoint 経由で投入した3 Task の実行・結果回収・sum=3 の集約が成功しました。Function の公開アクセス無効化と Private Endpoint 接続 Approved を確認しています。標準6シナリオは HTTP 202 対応修正後に5件 Pass、admission20 は起動履歴取得の制限による NotRun で完了しました（summary 終了コード2）。host 再起動復旧、期限停止、Spot 回収復旧は未実証です。実環境の証跡とローカル試験は [検証結果](docs/test-results.md) と [実現性評価](docs/feasibility-matrix.md) に分けて記録します。

---

## Durable Task Scheduler の PoC

`durable_backend='durableTaskScheduler'` を指定すると、Consumption Scheduler・task hub・30日保持ポリシーと Function Managed Identity の接続を構成します。既定は従来の `azureStorage` です。DTS 用は別 Environment・Function App・Storage・state とし、既存の実行履歴・Entity 状態の移行は行いません。設定例は `environments/durable-task-scheduler.example.psd1`、構成・配置・検証手順と制限は [DTS PoC](docs/durable-task-scheduler-poc.md) を参照してください。

DTS 用の別環境を Azure へ配置し、Managed Identity 接続、12 Functions の登録、3 Task smoke（sum=3）と標準検証の5件 Pass を確認しました。`admission20` は全世代の起動履歴を取得できないため NotRun、summary 終了コードは2です。SDK で DTS データプレーンを直接照会し、同じ smoke インスタンスの `MainOrchestrator / Completed` も確認しました。上記の Azure Storage の検証記録とは別の結果です。Functions の受信閉域化は継続し、DTS への送信は公開エンドポイントと Entra 認証を使います。証跡は [DTS PoC](docs/durable-task-scheduler-poc.md) に記録しています。

`existing_service_plan_id` は既定 `null`（新規 Plan 作成）です。同じサブスクリプション・リージョンの既存 Linux Plan の完全な ARM ID を指定すると、別 RG からも読み取り専用で参照し、追加 Plan を作成しません。今回の DTS 実証では既存 B1 を共用し、Function App・Storage・hub・state を分離しました。既存 Azure Storage 環境の Function 設定・保存先と Plan 設定は変更していません。Plan の CPU・メモリは共有するため性能影響がないことは保証しません。[Plan 共有の仕様](https://learn.microsoft.com/en-us/azure/azure-functions/dedicated-plan) を参照してください。

## ワンコマンド配置

Batch 基盤を含む環境構築からアプリケーション配置までを 1 つのスクリプト（`scripts/deploy.ps1`）で実行できます。実装範囲と条件付き構成の詳細は [必要なIaCの洗い出し](docs/one-command-deployment-inventory.md) を参照してください。

- 新規 PoC 用 Batch Account / Windows Pool の作成、または既存 Batch 環境の参照
- Terraform state backend の準備（新規作成または既存参照）
- Function App のビルド・テスト・パッケージングと、検証 VM から Private SCM への Entra 認証 ZIP デプロイ
- デプロイ後の準備完了確認（Pool ノード状態および Functions 検出）

### 1. 事前準備

以下のツールを用意し、`az login` を実行します。デプロイ先は設定ファイル（`.psd1`）に指定したサブスクリプションとなり、Azure CLI の既定サブスクリプションは変更されません。

- Windows PowerShell 5.1 以降
- .NET 10 SDK
- Terraform 1.9 以降
- Azure CLI 2.48.1 以降

**前提条件とスコープ外項目**
- **事前準備が必要な項目**: Resource Provider の登録、クォータ増枠、VM 容量の確保、Marketplace 利用条件の有効化、Azure Compute Gallery の元イメージ準備（スクリプトでは Provider 登録状況の確認、および Pool の resize エラー・準備期限超過の検出を行います）。
- **現スクリプトの対象外**: Storage / Batch の全面閉域化、VPN / ExpressRoute 回線接続、UserSubscription 方式、Gallery 内のイメージバージョン自体の作成処理、CI/OIDC 連携。Function の受信は Private Endpoint のみとし、公開ネットワークアクセスを無効化します。

### 2. 設定ファイルの作成

#### 新規 Batch 基盤を作成する場合 (`batch_mode='new'`)
`environments/poc.example.psd1` を `environments/poc.psd1` にコピーし、以下の項目を設定します。

- サブスクリプション ID、リージョン、グローバルに一意な名前 suffix
- 既存 Gallery の正確な image version ID（新規の Compute Gallery やイメージバージョン自体を作成する処理は含みません）
  - Marketplace の Windows イメージを使用する場合は、`batch_image_source='marketplace'` と正確な image version を指定し、Gallery ID は省略します。
- Terraform state backend 名、デプロイ実行者の Object ID、および検証 VM 用 SSH 公開鍵（validation_vm_ssh_public_key）。公開鍵のサンプル値は必ず実際の公開鍵へ置換してください。秘密鍵は設定ファイルへ保存しません。

新規作成される Batch 環境の仕様は以下のとおりです。
- **構成**: `BatchService` 方式、Entra 認証のみ、公開エンドポイント、`Simplified` 通信の Windows Pool（新規ノード数は 1〜3 台）
- **計算ノードの種別**: `batch_node_type='spot'` または `'dedicated'` を指定します。省略時は従来互換の `dedicated` です。PoC サンプルは `spot`・`batch_node_count=1` とし、Spot 1 台・専用ノード 0 台を要求します。`batch_node_count` は選択した種別の総台数で、混在構成は対象外です。RHEL 検証 VM は通常 VM のままです。
- **並行動作**: 1 台・1 slot 構成の場合、3 つの Task は順次実行され、3 本の監視オーケストレーションは並行して進みます。

#### Spot 利用時の条件と再開手順

Spot は容量が保証されず、実行中に回収される可能性があります。クォータ承認後もノードを確保できず、Pool 準備待ちやワークフローが期限に達する場合があります。Batch による回収時の Task 再キューは、非ゼロ終了に対する `MaxTaskRetryCount=0` と別の動作です。現行 Task は結果をノードローカルへ出力し、後から Activity が回収するため、回収前にノードを失うと結果を取得できない場合があります。Task から Blob への直接永続化は未実装であり、今回の Spot 対応でも C# の処理は変更しません。[Batch Spot の仕様](https://learn.microsoft.com/en-us/azure/batch/batch-spot-vms)

2026-10-17 に Spot コア枠2への増枠を確認し、`Standard_D2s_v5` の Spot 1台を配置して3 Task smoke を実証しました。今後の配置変更も同じ設定・state で再 plan してから apply し、state やノード種別の変更前に保存した古い plan は適用しません。新しい環境では必要なクォータと容量を個別確認します。[クォータの確認](https://learn.microsoft.com/en-us/azure/batch/batch-quota-limit)

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

# 配置後、VM から 3 Task HTTP workflow を実行し、sum=3 まで検証
./scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -Smoke
```

#### 実行フローとオプションの挙動
1. **処理順序**: 事前確認 → `build` / `test` / `publish` → backend 作成または参照 → Terraform `plan` 検査 / `apply` → Pool 確認 → VM Run Command による Private SCM への Entra 認証 ZIP 配置 → 12 個の Functions のインデックス登録（ロード）検出（`-PlanOnly` を指定しない限り、Terraform の `apply` もスクリプト内で自動実行されます）。
2. **`-PlanOnly` 指定時**: `apply` とコード配置は行いません。`Backend.Create=true` の場合、backend が作成済みであれば main plan の作成まで進み、未作成であれば bootstrap plan のみを保存します。なお、backend 参照時の権限不足や通信失敗は「未作成（不存在）」とは区別してエラー停止します。
3. **Pool 確認と `-Smoke` 指定時**:
   - **新規 Pool**: 指定した全台数が準備完了になるまで待機します（ノード一覧取得時のページネーションに対応し、全ノードの状態を確認します）。
   - **既存 Pool**: 存在と参照権限のみを確認するため、ノード数が 0 台の状態でもコードを配置できます。
   - **`-Smoke` 実行時**: 自動拡張（autoscale）設定自体は変更せず、先に Task を投入して自動拡張と完了を待機します。配置設定の `TimeoutSeconds` を用いて VM 上で 3 Task HTTP workflow を検証します。単一 Task の検証には独立ランナーの `-Scenarios single` を使用します。

### 4. 必要な権限

Function の Managed Identity に対する権限付与は Terraform 内で実装済みですが、デプロイ実行者自身の権限は、明示した `validation_operator_principal_id` への転送専用コンテナの Blob 権限を除き自動拡張しません。実行者には事前に以下の権限が必要です。

- **インフラ作成時**: 対象リソースグループでのリソース作成権限、RBAC 割当権限（Owner の自動付与は行いません）
- **Gallery 利用時**: Pool 作成者のイメージ利用権限、および対象リージョンへの replica 配置
- **Batch 操作**: Batch データプレーンの Pool / Node 参照権限
- **VM 経由の配置・検証**: 実行者には VM Run Command 操作権限、カスタムロール定義の作成権限が事前に必要です。転送専用 `poc-vm-control` コンテナの Storage Blob Data Contributor は、任意の `validation_operator_principal_id` に実行者の Object ID を指定した場合のみ Terraform で付与します（Backend.deployer_principal_id と同じ ID を指定できます）。未指定の場合は実行者が Blob データ権限を事前に用意してください。VM の Managed Identity には、Function アプリに限定した5操作のカスタムロール（参照・key取得・配置）、Batch account の Azure Batch Account Reader、Storage の Storage Blob Data Reader、および転送専用コンテナの Storage Blob Data Contributor を Terraform で付与します。Function HTTP 呼び出しは既存の Function key 認証を継続します。

### 5. 出力ファイル・State 管理・安全機構

- **出力先**: ログ、`publish` 出力、ZIP、保存 plan、配置結果は `.deploy/<environment>/<実行日時>/` に保存されます。Function key、Durable 管理 URL、認証情報などの秘密情報は配置ログに出力されません。
- **Terraform State**:
  - bootstrap の local state: `.deploy/<environment>/bootstrap/terraform.tfstate`（backend の管理に必要なため保管してください）
  - アプリ環境の remote state: backend container 内の `<environment>.tfstate`
  - state backend の RG / Storage / Container には `prevent_destroy` を設定しています。また、環境名と backend・対象 RG・Batch モード等の対応を記録し、同一環境名で異なる state へ誤接続する設定変更を拒否します。
- **変更保護と再実行**:
  - 削除・置換を含む plan、既存 Batch の更新、Gallery の変更を検出した場合は `apply` 前に停止します（構成変更でリソース置換が必要な場合は個別の移行作業として扱います）。
  - 途中で失敗した場合は作成済みリソースを残して原因を表示するため、修正後に同じ設定で再実行できます。

---

## 構成と管理境界

| 対象 | 管理方法 |
|---|---|
| Resource Group | 既定は既存 `data` 参照。`create_resource_group=true` では PoC 専用 RG を作成 |
| Batch Account / Windows Pool | `batch_mode=existing` は参照のみ。`new` は PoC 専用基盤を作成 |
| 既存 Azure Compute Gallery image version | 新規 Gallery 利用 Pool では `data` 参照してイメージへ設定。Gallery 自体は変更しない |
| PoC Linux Function App / B1 Dedicated plan | Terraform で新規作成。Always On 有効 |
| PoC Storage / `poc-results` container | Terraform で新規作成。Storage 名は Azure の制約により `poc` + suffix（ハイフンなし） |
| PoC Application Insights / Log Analytics | Terraform で新規作成 |
| VNet / VM・Private Endpoint 用 subnet / NSG / NAT Gateway | Terraform で新規作成。検証 VM の NIC に Public IP を付けず、SSH 受信を拒否 |
| Function Private Endpoint / Private DNS | Function・SCM を Private IP へ解決し、Function の公開ネットワークアクセスを無効化 |
| RHEL 9 検証 VM / Managed Identity | cloud-init で Azure CLI・PowerShell を準備。Run Command から配置・投入・検証を実行 |
| Function system-assigned identity / RBAC | Terraform で作成。PoC Storage と既存 Batch account に限定 |
| state backend | 独立 bootstrap root で PoC 専用 RG / Storage / Container と実行者 Blob 権限を作成、または既存 backend 参照 |
| PoC Batch Jobs / Tasks / 結果 Blob | Activity / smoke tool で実行時に作成。Terraform 管理外 |

> [!NOTE]
> 共有 Pool を使用して実接続試験を行う際は、利用可能なノード数、OS 環境（Windows Server / PowerShell 5.1）、同時実行による他ジョブへの影響を事前に確認してください。計算ノードへのアプリ配布、Pool の resize、イメージ更新は行いません。

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

# 配置制御のオフライン試験（Azureへ接続しない）
./scripts/tests/Deployment.Common.Tests.ps1
./scripts/tests/Test-Deployment.Tests.ps1
./scripts/tests/Deploy.Workflow.Tests.ps1
./scripts/tests/PocValidation.Tests.ps1
./scripts/tests/PrivateVm.Tests.ps1

# Terraform mock_provider試験（providerの取得後、Azureへ接続しない）
terraform -chdir=infra test
terraform -chdir=infra/bootstrap test
```

### 配置済み環境を独立して検証する

`Invoke-PrivatePocValidation.ps1` は VM Run Command 経由で検証を開始する入口です。VM 上では `az login --identity` 後に `Invoke-PocValidation.ps1` を実行します。`Invoke-PocValidation.ps1` は、デプロイ済みの環境に対して独立してシナリオ検証を実行するためのテスト実行スクリプト（ランナー）です。デプロイ処理や Pool 設定の変更は行わず、HTTP 経由でテスト用 Job / Task を投入して動作を検証します。実行には `az login` が完了していることに加え、デプロイ結果ファイルに記録されたサブスクリプションにおける Function キー取得・設定参照権限、Batch Job / Task の参照権限、および結果 Blob の読み取り権限が必要です。

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

デフォルトのシナリオは `single,three,classification,failure,admission20,conflict` です。`TimeoutSeconds` はシナリオ内の待機に適用され、既定 900 秒、指定範囲 60〜3600 秒、ポーリング間隔は 10 秒です。Run Command では cloud-init の準備待ちを最大 900 秒、以後のゲスト操作全体を最大 4200 秒（70 分）に制限し、Action Run Command の90分制限内に収めます。長い検証は `-Scenarios` を指定して複数回に分けてください。タイムアウト試験（`timeout`）では実際のデプロイ設定値を確認し、設定された Task 遅延時間とランナーの待機上限の組み合わせでタイムアウトを安全に観測できない構成の場合は `NotRun` と判定します。なお、ランナー側の待機期限が超過した場合でも、Azure 上の計算 Job が正常に停止完了したとはみなしません。

`-DedicatedEnvironment` は試験専用の環境を指定するスイッチであり、専用 Batch ノードを指定するものではありません。Spot ノードを使用する試験専用環境でも指定します。

検証結果は `summary.json` および `summary.txt` に出力され、シナリオごとの合否判定（`Pass` / `Fail` / `NotRun`）、UTC 実行時刻、Git コミット情報、デプロイパッケージのハッシュ値、各種観測値が保存されます。VM 経由ラッパーの既定の回収先は `artifacts/private-validation/<run>/` です。VM 内で直接使用するランナーの既定保存先は `artifacts/validation/<run>/` であり、プロセスの終了コードは「全選択試験が Pass」で `0`、「Fail が 1 件以上あり」で `1`、「Fail はないが NotRun が含まれる」で `2` となります。認証情報、キー、シークレットを含む URL、生の例外スタックトレースは証跡ファイルへ出力されません。なお、同一の出力先を指定すると前回の結果要約が上書きされるため、実行ごとに個別のディレクトリを指定してください。

`admission20` シナリオでは、同一インスタンスへの集約、投入 Task 数、完了後の再送動作を観測します。ただし、現在の Azure Functions / Durable 管理 API のみでは全世代にわたる Main オーケストレーションの起動履歴を完全には証明できないため、観測値を記録したうえでステータスを `NotRun`（未実証）とし、厳密な「1 度限りの起動（exactly-once schedule）」が実環境で成立したとは断定しません。また、検証ランナー自身はリクエスト入力、Entity 状態、Main オーケストレーション履歴を清掃しません。DTS を選択した場合は Scheduler の30日保持ポリシーが別途適用され、期限後の履歴保持は保証しません。

個人の検証環境では VNet 内の検証 VM から Function の Private Endpoint を呼び出します。ローカル端末から Function への直接到達性は不要です。転送用 bundle を専用の転送 Blob コンテナにアップロードし、VM の Managed Identity で取得して実行し、安全な証跡のみ Blob 経由で回収します。NAT Gateway を通る外部通信と Storage / Batch の公開データエンドポイントは今回維持します。VPN / ExpressRoute 接続と Storage / Batch の全面閉域化は [共通ネットワーク設計](docs/network-connectivity-design.md) に記載した後続フェーズです。なお、ローカル環境での Fake / Mock によるテスト成功と、Azure 実環境での接続試験の結果は明確に区別して評価してください。

ランナーと Azure 側の処理順序、生成される成果物、およびシナリオ別の詳細な判定条件については [検証ランナーの処理フロー](docs/validation-runner-flow.md) を参照してください。

- **リストア順序**: 現行の `Azure.Functions.Sdk` の生成ターゲットを準備するため、先に Function プロジェクト（`Poc.Functions.csproj`）を直接 restore してからソリューション全体を restore します。
- **テスト実行**: `global.json` で `Microsoft.Testing.Platform` runner を指定しているため、`dotnet test` 実行時は `--project` オプションを使用します。
- **モック構成**: 単体テストは `FakeGateway` とインメモリの結果保存を使用し、Azure への通信は発生しません（現在 7 つのテストクラス・全 91 件の単体テストがすべて合格）。現在のテスト実行結果は [検証結果](docs/test-results.md) を参照してください。

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

以下はローカルエンドポイントへのリクエスト例です。Azure 上の `workflow_url` に対して実行する際は、Function key を `x-functions-key` ヘッダーに指定してください（Function key やレスポンスに含まれる Durable 管理 URL はログに記録・公開しないでください）。

```powershell
$body = @{
  requestId = 'feasibility-001'
  classification = 'auto'
  taskCount = 3
  failTaskIndex = $null
  lightDelaySeconds = 5
  heavyDelaySeconds = 15
} | ConvertTo-Json
$started = Invoke-RestMethod -Method Post -Uri 'http://localhost:7071/api/workflows' -ContentType 'application/json' -Body $body
$started
# 応答中の statusQueryGetUri を使用して状態を照会
Invoke-RestMethod -Uri $started.statusQueryGetUri
```

#### パラメータと受付制御（冪等性・重複抑止）の仕様
- **入力パラメータ**: `taskCount` は `1`〜`3`、`classification` は `light` / `heavy` / `auto` を指定できます。
- **入力の確定と競合検出**: リクエスト受付時に `runs/poc-<requestId>/request.json` を条件付き作成して入力内容を確定します。同一 `requestId` で異なる入力パラメータが送信された場合は `HTTP 409 (Conflict)` を返します。
- **同時受付・再送時の動作**: 同一 `requestId` かつ同一入力の同時リクエストや再送は、同じ Durable Entity へ通知されます。Entity が「起動済み状態」と「Main オーケストレーションの起動指示」を永続化することで重複起動を防ぎます。
- **ステータスコードと再試行**:
  - `HTTP 202 (Accepted)` は Durable Entity への通知受理を表します。Main オーケストレーションの実インスタンスが登録されるまでの間、状態照会 URL（`statusQueryGetUri`）が一時的に `404` を返す場合は、少し待ってから再照会してください。
  - 受付確認中の一時的な障害時は `HTTP 503` と `Retry-After` ヘッダーを返します。その場合は同じ `requestId` と入力内容で再送できます。

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
- **キャンセル連鎖の未実装**: 通常の Task 失敗時は他の兄弟 Task の終了を待機します。また、利用者からのキャンセル要求を Batch Job / Task の停止へ連鎖させる機能は現在未実装です。

### 3. 受付の重複抑止の前提条件
- **状態保持の前提**: 同一 `requestId` に対する受付の重複抑止は、入力 Blob（`request.json`）、Durable Entity の状態、および Main オーケストレーションのインスタンス履歴が保持された状態で本 API を経由することを前提としています。これらを個別に削除（purge）して同じ ID を再利用する運用や、Durable の管理 API から同じインスタンス ID を直接起動する操作は保証対象外です。
- **デプロイ時の注意**: 既存のインスタンスが存在する場合は入力内容を照合して再利用しますが、旧バージョンの starter との同時稼働は避け、実行中の処理がすべて終了してから本バージョンをデプロイしてください。

### 4. ファイル転送サイズと本 PoC の評価スコープ外項目
- **結果ファイルのサイズ制限**: Batch の Task File API を用いた結果取得は、数 KB 以下の小さな結果データ専用です。また、計算ノードのローカルファイルはノード障害や保持期限切れによって失われる可能性があります。本番環境で大容量の出力を扱う場合は、Batch の `OutputFiles` 機能や計算ノードからの直接 Blob 永続化、および失敗時の修復機構を別途検討する必要があります。
- **未検証の領域**: LSF / HPC Pack の完全代替可否、300 ノード規模での性能検証、Fair Share スケジューリング、Backfilling、MPI ジョブ、商用業務アプリケーションのライセンス管理については本 PoC の検証対象外です。
