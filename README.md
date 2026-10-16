# Azure Batch + Durable Functions PoC

既存の Windows Batch Pool に小さな PowerShell Task を投入し、Durable の子オーケストレーションと永続 Timer で待機、Batch File API → Blob → 集約 → Manifest 公開までを検証する PoC です。実装の根拠は [設計書](docs/azure-hpc-modernization-poc-design.md) と [実装指示書](docs/CODEX_IMPLEMENTATION_INSTRUCTIONS.md) です。

Azure 実接続の成否は [検証結果](docs/test-results.md) と [実現性評価](docs/feasibility-matrix.md) に記載します。ローカル Fake の成功を実 Pool の成功として扱いません。

Batch基盤を含むワンコマンド配置を実装しています。新規PoC用Batch Account / Windows Poolの作成、既存Batch参照、state backendの準備、Functionコード配置、準備完了確認を一つのスクリプトで実行できます。[必要なIaCの洗い出し](docs/one-command-deployment-inventory.md) に実装範囲と条件付き構成を記載しています。

## ワンコマンド配置

Windows PowerShell 5.1以降、.NET 10、Terraform 1.9以降、Azure CLI 2.48.1以降を用意し、`az login`を実行します。配置は下記の設定ファイルに指定したsubscriptionへ行い、CLIの既定subscriptionは変更しません。

新しい基盤を作る場合は`environments/poc.example.psd1`を`environments/poc.psd1`へコピーし、subscription、リージョン、グローバルに一意な名前suffix、既存Galleryの正確なversion ID、state backend名、配置実行者のobject IDを設定します。新規Galleryを製造する処理は含みません。Marketplace Windows imageを使う場合は`batch_image_source='marketplace'`と正確なimage versionを指定し、Gallery IDを省略します。

```powershell
./scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1
```

実行順は、事前確認 → build/test/publish → backend作成または参照 → Terraform plan検査/apply → Pool確認 → Entra認証ZIP配置 → 12 Functions検出です。applyはコマンド内で実行します。`-PlanOnly`はapply・コード配置を行いません。`Backend.Create=true`でもbackend作成済みならmain planまで進み、未作成ならbootstrap planのみを保存します。backend参照の権限不足・通信失敗は不存在と区別して失敗します。

新規Poolは指定全台の準備完了を待ち、ノード一覧は全ページを確認します。既存Poolは存在・参照権限だけを確認するため、0台でもコードを配置できます。`-Smoke`ではTaskを先に投入して自動拡張と完了を待ちます。自動拡張設定の変更は行いません。単一Task smokeの期限は配置設定の`TimeoutSeconds`を使います。

```powershell
# 保存planの確認のみ
./scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -PlanOnly
# 配置後に単一Task smokeと3 Task workflowを実行し、sum=3まで検証
./scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -Smoke
```

既存Batchを使う場合は`environments/existing.example.psd1`をコピーして使用します。既存Account、Pool、Galleryは参照専用です。新規RGを作るか既存RGを使うかは`create_resource_group`で選択します。新規Batchは`BatchService`方式、Entra認証のみ、公開endpoint、`Simplified`通信のWindows Poolです。1台・1slotなら3 Taskは順次実行され、3本の監視は並行して進みます。新規ノード数は1〜3台です。

新規作成には対象RGでのリソース作成権限、RBAC割当権限が必要です。実行者にはBatchデータプレーンのPool/Node参照権限、`-Smoke`にはJob/Task操作、Function key取得、結果Blob参照権限も必要です。Function identityへの権限付与は実装済みですが、実行者自身の既存環境権限を自動で拡張しません。Gallery利用時はPool作成者のイメージ利用権限とリージョンreplicaを準備してください。

ログ、publish出力、ZIP、保存plan、配置結果は`.deploy/<environment>/<実行日時>/`に保存します。bootstrapのlocal stateは`.deploy/<environment>/bootstrap/terraform.tfstate`、アプリのremote stateはbackend containerの`<environment>.tfstate`です。bootstrap stateはbackendの管理に必要なので保管してください。state backendのRG/Storage/Containerには`prevent_destroy`を設定しています。環境名とbackend/対象RG/Batchモード等の対応を記録し、同じ環境名で異なるstateへ誤接続する変更を拒否します。

削除・置換を含むplan、既存Batch更新、Gallery変更を検出した場合はapply前に停止します。構成変更で置換が必要な場合は個別の移行として扱います。途中失敗時はリソースを残して原因を示し、同じ設定で再実行できます。Function key、Durable管理URL、認証情報を配置ログには出しません。

Resource Provider登録、quota増枠、VM容量、Marketplace利用条件、Galleryの元イメージ準備は事前条件です。スクリプトはProvider登録状況を確認し、Pool resizeエラーと準備期限超過を検出します。実Azure上の権限・quota・配置・復旧は未検証です。閉域化、UserSubscription方式、Galleryイメージ製造、CI/OIDCは現在の配置スクリプトには含みません。

## 構成と管理境界

| 対象 | 管理方法 |
|---|---|
| Resource Group | 既定は既存`data`参照。`create_resource_group=true`ではPoC専用RGを作成 |
| Batch Account / Windows Pool | `batch_mode=existing`は参照のみ。`new`はPoC専用基盤を作成 |
| 既存 Azure Compute Gallery image version | 新規Gallery利用Poolではdata参照してimageへ設定。Gallery自体は変更しない |
| PoC Linux Function App / B1 Dedicated plan | Terraform で新規作成。Always On 有効 |
| PoC Storage / `poc-results` container | Terraform で新規作成。Storage 名は Azure の制約により `poc` + suffix（ハイフンなし） |
| PoC Application Insights / Log Analytics | Terraform で新規作成 |
| Function system-assigned identity / RBAC | Terraform で作成。PoC Storage と既存 Batch account に限定 |
| state backend | 独立bootstrap rootでPoC専用RG/Storage/Containerと実行者Blob権限を作成、または既存backend参照 |
| PoC Batch Jobs / Tasks / 結果 Blob | Activity / smoke tool で実行時に作成。Terraform 管理外 |

共有 Pool の利用可能ノード、Windows Server / PowerShell 5.1、同時実行の影響を利用者が確認してから実接続試験を実行してください。計算ノードへのアプリ配布、Pool resize、イメージ更新は行いません。

## 必要な環境

- .NET 10 SDK、Azure Functions Core Tools v4、Azure CLI、Terraform 1.9 以降。
- Azure へのログインと既存 Batch の Microsoft Entra 認証、既存 Pool 参照権限。
- デプロイ実行者には PoC リソース作成権限と指定 scope の RBAC 作成権限。Owner を自動付与しません。Resource Provider は事前登録済みを前提とし、自動登録を無効にしています。
- ローカル実接続実行者には Batch account の `Azure Batch Job Submitter`、結果 container の Blob データ読書き権限。クラウド host storage をローカル host に使う場合は後述の host 権限も必要です。

.NET 10 isolated worker と Functions v4 を使用します。[公式 isolated guide](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide) では Linux Consumption で .NET 10 を使えないため、この構成は Always On を有効にした Linux Dedicated B1 を選択しています。[hosting guide](https://learn.microsoft.com/en-us/azure/azure-functions/functions-scale) と [AzureRM Function resource](https://github.com/hashicorp/terraform-provider-azurerm/blob/main/website/docs/r/linux_function_app.html.markdown) を参照してください。リージョンでの実デプロイは未検証です。

## 1. ローカル検証

リポジトリルートで実行します。

```powershell
dotnet restore src/Poc.Functions/Poc.Functions.csproj
dotnet restore DurableBatchPoc.slnx
dotnet build DurableBatchPoc.slnx --no-restore
dotnet test --project tests/Poc.Tests/Poc.Tests.csproj --no-restore
# 配置制御のオフライン試験（Azureへ接続しない）
./scripts/tests/Deployment.Common.Tests.ps1
./scripts/tests/Test-Deployment.Tests.ps1
./scripts/tests/Deploy.Workflow.Tests.ps1
# Terraform mock_provider試験（providerの取得後、Azureへ接続しない）
terraform -chdir=infra test
terraform -chdir=infra/bootstrap test
```

現行 `Azure.Functions.Sdk` の生成ターゲットを準備するため、Function project を先に直接 restore してから solution を restore します。`global.json` は Microsoft.Testing.Platform runner を選択するため、テスト実行時は `--project` を指定します。単体テストは FakeGateway とメモリ上の結果保存を使用し、Azure にアクセスしません。現在の実測結果は `docs/test-results.md` を参照してください。

## 2. Terraform の確認と適用

通常は上記の`deploy.ps1`を使用します。以下は既存backendへ手動で接続する場合の手順です。`infra/`はAzureRM remote backendを使うため、先にstate用Storage/Containerと実行者のBlobデータ権限を用意します。backend.hclには`subscription_id`、`resource_group_name`、`storage_account_name`、`container_name`、環境固有の`key`、`use_azuread_auth=true`を記載します。ワンコマンド実行で生成した`.deploy/<environment>/<実行日時>/backend.hcl`も利用できます。

以前のlocal stateを使っている場合は、stateを保存してから同じリソースを対象とするbackendへ`terraform init -migrate-state`で明示移行してください。空のremote stateに接続して再applyすると既存リソースとの衝突が起きます。配置スクリプトは自動importや暗黙のstate移行を行いません。

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

`batch_mode=existing`では、Planに既存Batch Account / Pool / Gallery / RGの作成・更新・削除がないことを確認します。既存Batchに対して作るものはFunction identityの`Azure Batch Job Submitter` role assignmentだけです。`batch_mode=new`ではPoC専用Account/Poolも作成されます。新規設定例は`infra/terraform.new.tfvars.example`です。その後、確認済みplanを適用します。

```powershell
terraform -chdir=infra apply poc.tfplan
terraform -chdir=infra output
```

AzureRM provider を 5.10.x に固定し、検証時の 5.10.0 の `.terraform.lock.hcl` を含めています。provider 更新時は lock の差分をレビューしてください。`terraform.tfvars`、state、plan、資格情報、Function 管理 URL のキーを Git に含めません。state はアクセスを制限し、本番利用では暗号化とロック付き remote backend を別途構成してください。

Function の host storage は鍵接続を使わず `storage_uses_managed_identity` と `AzureWebJobsStorage__accountName` / `__credential=managedidentity` を設定します。共有キーは Storage 側でも無効です。[host storage の権限](https://learn.microsoft.com/en-us/azure/azure-functions/manage-connections?tabs=host%2Cidentity) に従う Blob Data Owner と、[Durable の権限](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-configure-managed-identity) に従う Queue / Table Data Contributor を PoC Storage scope に付与します。結果も同じ PoC Storage に保存するため追加 Blob 権限は不要です。RBAC の反映後に host が正常起動することを確認してください。

[Azure Batch Job Submitter](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/compute#azure-batch-job-submitter) は Pool 変更権限を持ちませんが、account 内の jobs にアクセスでき、ID prefix による認可制限はありません。PoC prefix はアプリで制限します。共有 account への適用はこの scope の権限を認められる環境で行ってください。

## 3. 設定

| 設定 | 意味 / 初期値 |
|---|---|
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `AzureWebJobsStorage__accountName` | PoC host storage 名 |
| `AzureWebJobsStorage__credential` | Azure は `managedidentity`。ローカル CLI 資格情報では設定しない |
| `BATCH_ENDPOINT` | 既存 account の `https://...batch.azure.com` |
| `BATCH_POOL_ID` | 既存 Windows Pool ID |
| `BLOB_ENDPOINT` | PoC Blob service URI |
| `BLOB_CONTAINER` | `poc-results` |
| `BATCH_POLL_SECONDS` | `10` |
| `WORKFLOW_TIMEOUT_MIN` | `15` |

SDK 認証は `DefaultAzureCredential` です。Azure では system-assigned Managed Identity、ローカルは `az login` の開発者資格情報等を使います。キーや SAS をソースに埋め込みません。

実行時は `src/Poc.Functions/local.settings.json.example` を `local.settings.json` にコピーし、上記を設定します。ローカル host storage に Azurite を使う場合は Azurite を起動し、`AzureWebJobsStorage=UseDevelopmentStorage=true` を設定します。その場合は `AzureWebJobsStorage__*` を削除し、結果用 `BLOB_ENDPOINT` は実 PoC Storage のままにします。

## 4. Step A: 既存 Windows Pool の smoke

最初に endpoint と Pool ID を設定し、単一 Task のコマンド実行と `wd/result.txt` の取得を確認します。

```powershell
$env:BATCH_ENDPOINT = 'https://<account>.<region>.batch.azure.com'
$env:BATCH_POOL_ID = '<existing-windows-pool>'
dotnet run --project tools/BatchSmoke
```

ログに Job ID、Task ID、状態、ファイル名、取得値が出ることを確認し、`artifacts/` 以下のログへ保存します。smoke の成功前に F01 を Pass としません。SDK は [Azure.Compute.Batch 現行ガイド](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/compute.batch-readme) の `BatchClient` と Entra 認証を使用し、旧 `Microsoft.Azure.Batch` サンプルは混在させません。

コマンドは暗黙 shell に依存せず `cmd /c powershell.exe -NoProfile -NonInteractive -Command "..."` を明示し、整数の待機時間と固定出力のみを挿入します。[Batch コマンド仕様](https://learn.microsoft.com/en-us/azure/batch/jobs-and-tasks) と [Task File API](https://learn.microsoft.com/en-us/rest/api/batchservice/tasks/get-task-file) を参照してください。ローカル Windows での quoting 成功だけでは実 Pool の実行ポリシーまで保証しません。

## 5. Step B: 起動、照会、Blob 確認

ローカルで実接続する場合:

```powershell
Push-Location src/Poc.Functions
func start
Pop-Location
```

Azure にデプロイする場合は Terraform 適用後、publish ディレクトリの中身を zip にして Azure CLI の Entra 認証で配布します。host.json が zip 直下にある必要があります。

```powershell
dotnet publish src/Poc.Functions/Poc.Functions.csproj -c Release -o artifacts/publish
Compress-Archive -Path artifacts/publish/* -DestinationPath artifacts/functions.zip -Force
$functionName = terraform -chdir=infra output -raw function_app_name
az functionapp deployment source config-zip --resource-group <poc-resource-group> --name $functionName --src artifacts/functions.zip
```

要求例。ローカルの URL を Azure の `workflow_url` に変更する際は Function key を `x-functions-key` header へ渡します。key と返却される管理 URL はログへ公開しないでください。

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
# 応答中の statusQueryGetUri を使用
Invoke-RestMethod -Uri $started.statusQueryGetUri
```

`taskCount` は 1〜3、`classification` は `light` / `heavy` / `auto` です。`runs/poc-<requestId>/request.json`を条件付き作成して入力を確定し、異なる入力はHTTP 409にします。同じ入力の同時受付・再送は同じDurable Entityへ通知し、Entityが起動済み状態と起動指示を永続化します。HTTP 202は通知の受理を表し、実インスタンス登録まで状態URLが一時404になる場合は再照会してください。受付確認中の一時障害は503と`Retry-After`を返します。同じrequestIdと入力で再送できます。

成功時は PoC container 内の `runs/poc-<requestId>/` に Task 結果、`final/aggregate.json`、`final/manifest.json` が存在します。3 Task で各 value が 1 なら `sum=3` / `taskCount=3`。Job ID は requestId のハッシュから決定的に導出します。清掃時は実際のログ / Durable 出力の ID と prefix を確認します。

```powershell
$storageName = terraform -chdir=infra output -raw storage_account_name
az storage blob list --account-name $storageName --container-name poc-results --auth-mode login --query '[].name' -o table
# 出力で確認した完全な Blob 名を指定する
az storage blob download --account-name $storageName --container-name poc-results --name '<run-prefix>/final/manifest.json' --file artifacts/manifest.json --auth-mode login
```

## 6. Step C: 故障と再開の試験

- 非ゼロ終了: 新しい requestId と `failTaskIndex=1` で実行。ほかの Task は自然終了を待ち、親が失敗し aggregate / manifest を公開しないことを確認。
- Timer: `classification=heavy` と長い待機時間を指定。HTTP が 202 で返り、継続中の状態を照会できることを確認。
- 再起動: 上の待機中に `az functionapp restart --resource-group <poc-resource-group> --name <poc-function-name>` を実行。PoC 専用 Function のみに限定し、共有 Pool / node は再起動しない。再開後の instance ID、Activity 履歴、manifest を保存。
- 結果欠損: FakeGateway で Task ファイル取得例外を模擬し成功誤認しないことを検証。実ノードのファイル削除は既定手順に含めない。
- 期限超過: `WORKFLOW_TIMEOUT_MIN` を待機時間より短くした専用試験で TimedOut と未公開を確認。親は当該PoC JobのID・Pool・fingerprintを照合してJobを終端化し、最大2分間、完了を確認します。`customStatus.termination`と`terminationError`で停止確認結果を確認してください。停止要求の成功と停止完了は区別します。

Application Insights では instance ID、Job ID、Task ID を手掛かりに Activity ログを取得します。状態照会結果、Batch 状態、Blob 一覧、試験時間とログの場所を `docs/test-results.md` に記録してください。

## 7. 清掃

先に Durable の対象試験が終端へ到達したことを確認します。期限超過時はJob停止完了も確認し、停止確認に失敗した場合は当該PoC Jobを手動確認します。確認済みのPoC Jobだけを削除し、既存Poolやnodeは停止しません。

```powershell
# Azure CLI Batch data-plane login。アカウント鍵ではなく Entra token を使用
az batch account login --resource-group <existing-batch-rg> --name <existing-batch-account> --auth-mode aad
$pocJobId = '<実行ログに記録された完全なpoc-Job-ID>'
if (-not $pocJobId.StartsWith('poc-', [StringComparison]::Ordinal)) { throw 'PoC Job ID のみ削除可能' }
az batch job show --job-id $pocJobId
# 表示を確認後、対象の一件だけ削除
az batch job delete --job-id $pocJobId --yes
```

Blob の個別清掃は PoC 専用 `poc-results` 内で、実行出力の完全な run prefix と一致する `poc-` 対象に限定します。共有コンテナへのワイルドカード削除は行いません。PoC 全体を破棄する場合は次の destroy plan で PoC 作成リソースとその RBAC のみが対象であることを確認します。

```powershell
terraform -chdir=infra plan -destroy -out=poc-destroy.tfplan
terraform -chdir=infra show poc-destroy.tfplan
# PoC Storage の状態履歴と結果も消えるため、必要な証跡を先に保存
terraform -chdir=infra apply poc-destroy.tfplan
```

既存参照のResource Group、Batch Account、Pool、Galleryはdestroy対象外です。新規作成モードではPoC専用RGとBatch Account/Poolもdestroy対象になります。環境のbackend設定とtfvarsを指定したdestroy planで対象を確認してください。Batch JobはTerraform管理外ですが、新規Accountを削除すればその中のJobも失われます。既存Accountでは上記の一件削除で清掃します。state backendは独立rootで`prevent_destroy`を設定しており、アプリ環境のdestroyに含めません。

## 制限と次の検証

Activityはat-least-once実行です。安定したJob / Task ID、入力照合、Blobの条件付き公開で再実行に対応し、一時的な通信障害・408/429/500/502/503/504だけを最大3回試行します。待機は5秒・10秒で、監視Activityは共有期限を超えて再試行しません。SDK側は再試行1回、通信試行のNetworkTimeoutは10秒です。入力不正・権限不足・構成競合・結果不整合はDurableで再試行しません。Batch Task自体のretryは0を基本としますが、ノード復旧時の再実行は残るため、計算のexactly-onceは保証しません。

監視期限は分類完了後から親子で共有します。Durable Timerは実行中Activityを取り消せないため、期限到達時のTask作成とJob停止の競合はBatchのJob終端状態で制御します。Job作成だけは、後からJobが作られて停止を逃れることを防ぐため完了を待ちます。Batchの`MaxWallClockTime`も設定してhost停止中の安全網にしますが、こちらはJob作成時起算であり監視期限とは異なります。通信・Activityの実行を含む厳密な実時間上限ではありません。通常のTask失敗は兄弟の終了を待ち、利用者によるキャンセルのBatch連鎖は未実装です。

受付の重複抑止は入力Blob、Entity状態、Mainインスタンス履歴を保持して本APIを使うことが前提です。これらを個別に削除してIDを再利用する運用や、管理APIから同じIDを直接起動する操作は対象外です。既存インスタンスは入力照合して再利用しますが、旧starterとの同時稼働は避け、既存処理が終了してからこの版を配置してください。現在のMock試験は実hostの永続化・復旧を実証するものではありません。

設計根拠と公式資料の確認結果は[信頼性改善の設計](docs/workflow-reliability.md)に記録しています。

Task File API による転送は数 KB 以下の小結果専用です。ノードローカルファイルはノード障害や保持期限で失われます。本番大容量出力は Batch OutputFiles / ノードからの直接永続化 / 修復を別途検討します。LSF/HPC Pack の完全代替、300 ノード性能、Fair Share、Backfilling、MPI、業務ライセンスは検証していません。
