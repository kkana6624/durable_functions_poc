# ワンコマンド配置に必要なIaCの洗い出し

調査・実装日: 2026-10-16。現行コードと AzureRM 5.10.0 の実 provider schema、Microsoft / HashiCorp 公式資料を確認した構成案。

現在は最小構成を実装済み: `infra/batch.tf`（新規BatchService AccountとWindows Pool）、新規RGオプション、Gallery/Marketplace参照、`infra/bootstrap/`（state backend）、`scripts/deploy.ps1`（build→IaC→ZIP配置）、`scripts/Test-Deployment.ps1`（準備完了・任意smoke）。設定例は`environments/`にある。Azure実配置は未実施。

以下の洗い出し表は元の構成案を残している。閉域化、UserSubscription、Galleryイメージ製造、CI/OIDCの条件付き資材は未実装。Terraformの新規Batch/既存参照を切り替える変数名は`batch_mode`。

## 目標と管理境界

設定ファイルを一度用意し、Azure認証済みの環境から一つのコマンドで、ビルド・Terraform・Functionコード配置・起動確認まで行う。初期ログイン、権限の承認、quota増枠の承認、カスタムイメージの元データ準備は前提条件として扱う。

実装したインターフェース（Azure認証と環境設定後）:

```powershell
./scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1
```

既存設計は Batch Account / Pool / Gallery を参照専用としている。新しい要望に対応するため、既存資材を維持する `existing` モードと、PoC専用基盤を作る `new` モードを追加する案とする。既存Batchを自動importしたり、同じ名前で作り直したりしない。モード間の変更も通常更新として扱わず、planで置換・削除を明示する。

## IaC対象の一覧

「必須」は新しいPoC一式を作るために管理が必要なもの。「条件付き」は利用する機能・ネットワーク方式で必要になるもの。

| 分類 | 対象 / 主なTerraform資材 | 現状 | 追加・変更内容 |
|---|---|---|---|
| 必須 | Resource Group / `azurerm_resource_group` | 既存RGのみ参照 | PoC専用RG新規作成と既存RG参照を選択可能にする。locationを明示 |
| 必須 | Batch Account / `azurerm_batch_account` | ID/endpoint変数のみ | 新規Account、割当方式、Entra認証、公開アクセス設定。endpoint/IDをFunctionsへ自動接続 |
| 必須 | Windows Pool / `azurerm_batch_pool` | Pool ID変数のみ | VM size、node-agent SKU、イメージ、専用ノード数、slots、通信方式、resize期限を管理 |
| 必須 | Windowsイメージ参照 / Poolの`storage_image_reference` | Gallery IDをoutputするだけ | 実際にPoolのimage IDへ接続。Gallery版はversion固定、Marketplace版はpublisher/offer/SKU/versionを指定 |
| 必須 | Function App / Service Plan | 実装済み | 新規/既存Batchから解決した値をapp settingsへ渡す。設定ファイルとの値重複をなくす |
| 必須 | Durable・結果Storage / Container | 実装済み | host Blob/Queue/Table、結果containerを維持。実行主体の初期データアクセス権限を確認 |
| 必須 | Managed Identity / RBAC | Function権限のみ実装済み | Function→Batch/Storageに加え、IaC実行主体→Pool作成/イメージ利用を整理 |
| 必須 | Insights / Log Analytics | 実装済み | Batch診断設定を追加しログ集約。利用可能な診断カテゴリを確認して指定 |
| 必須 | Terraform state / bootstrap | local state | ワンコマンドで再利用できるstateの場所を固定。共有実行ならremote backendのStorage/Containerと実行主体RBACを別stateで管理 |
| 必須 | アプリ成果物の配置 | README手動手順 | build/publish、ZIP、Entra認証でのコード配置、配置結果検出をスクリプト化。Terraformとは別工程 |
| 条件付き | VNet / Subnet / NSG / UDR / NAT | 未実装 | VNetを使うPoolの通信経路を管理。NAT等は選択した外向き接続方式に応じて追加 |
| 条件付き | Private Endpoint / Private DNS / Function VNet Integration | 未実装 | 閉域化時、Batch `batchAccount`と`nodeManagement`を区別。Storage blob/queue/table、Function/SCMへの到達性も設計 |
| 条件付き | Gallery / Image Definition / Image Version | 既存参照のみ | イメージも新規作成する場合は`azurerm_shared_image_gallery` / `azurerm_shared_image` / `azurerm_shared_image_version`。元イメージ取得・構築工程も必要 |
| 条件付き | Key Vault / Batchサービス権限 | 未実装 | `UserSubscription`割当方式を採用する場合に追加。標準`BatchService`構成に一律追加しない |
| 条件付き | Pool User Assigned Identity / AutoStorage | 未実装 | resource files、application packages、OutputFiles、ノードからStorageへ直接アクセスする場合に追加。現在のダミーTaskでは不要 |
| 条件付き | CI/CD実行Identity / OIDC | 未実装 | GitHub Actions等から実行する場合に追加。ローカルCLI実行とは権限主体を分離 |

Batch Account / PoolはAzureRMで管理可能。VM・NIC・OSディスク等の実体はPoolに従ってBatchが払い出すため、個別VMをTerraformで二重管理しない。provider schemaで `storage_image_reference.id`、`fixed_scale`、`target_node_communication_mode`、`network_configuration` を確認済み。[Batch Pool定義](https://github.com/hashicorp/terraform-provider-azurerm/blob/main/website/docs/r/batch_pool.html.markdown)、[Batch Account定義](https://github.com/hashicorp/terraform-provider-azurerm/blob/main/website/docs/r/batch_account.html.markdown)

## Batchで確定が必要な事項

最小PoC案は、専用Batch Account、Windows Pool 1個、専用ノード1台、1ノード1slot、固定scale、`Simplified`通信方式とする。3 Taskは並行に監視するが、この設定では計算実行は順番になる。同時に3 Taskを実行したい場合は3台または3slotsへ変更する。VM SKUはリージョンの対応イメージ・quota・容量を確認して選定する。

### 割当方式

- `BatchService`: 最小PoCの第一候補。Batch管理サブスクリプション側で計算リソースを割り当てる。
- `UserSubscription`: VM等を利用者のサブスクリプションに置く要件がある場合に採用。Key Vault、BatchサービスprincipalへのAzure Batch Service Orchestration Role等が増える。subscription bootstrapをアプリ配置から切り分ける。

詳細は [Account作成とUserSubscription前提](https://learn.microsoft.com/en-us/azure/batch/batch-account-create-portal) を参照。FunctionのJob Submitter権限は、Terraform実行主体のPool作成権限の代わりにはならない。

### Windowsイメージ

既存Gallery versionを利用する場合、generalized image、Batch対応OS/node-agentの組合せ、Batchリージョンへのreplica、作成主体の権限を検証する。Batch専用公式資料では通常のReaderだけでは不十分で、`Microsoft.Compute/disks/beginGetAccess/action`が必要とされているため、Readerだけを自動設定して完了扱いしない。[GalleryイメージによるPool作成](https://learn.microsoft.com/en-us/azure/batch/batch-sig-images)

イメージまで新規作成するなら、Galleryの箱だけでは不十分。元のmanaged image / snapshot / VHD、またはAzure VM Image Builder等によるWindowsイメージ作成工程をIaC/ビルド資材に追加する。PowerShellダミーTaskのみのPoCには、Batch対応Marketplace Windows imageを直接使う構成も候補とする。元データのない既存HPCイメージをTerraformだけで再現できるとは扱わない。

### ネットワーク

最小構成でも、Function→Batch API、Function→Storage、Pool→Batch node managementの経路を確認する。新規Poolには`Simplified`を明示する。[Simplified通信要件](https://learn.microsoft.com/en-us/azure/batch/simplified-compute-node-communication)

閉域構成ではVNet内にPool用、Function統合用、Private Endpoint用のSubnetを分ける。Batch API用`batchAccount`とノード管理用`nodeManagement`のPrivate Endpoint、Private DNSとVNet linkを管理する。Storageのblob/queue/tableも必要な経路を用意する。配置マシン/CI runnerからFunction SCMとTerraformが操作するデータプレーンへ到達できるようにする。

Poolのpublic IPを無効にするだけでは通信は完成しない。nodeManagement Private EndpointとDNSを構成し、追加の外部通信が必要ならNAT/Firewall等を設計する。Microsoftのno-public-IP資料にはNAT代替の説明とPrivate Endpoint必須の注記が混在するため、閉域案はnodeManagement PEを含めて実機検証する。[Public IPなしPool](https://learn.microsoft.com/en-us/azure/batch/simplified-node-communication-pool-no-public-ip)

## ワンコマンド実行でまとめる工程

1. .NET / Terraform / Azure CLIのバージョン、ログイン、対象subscription、設定ファイルを確認。
2. 必要Resource Providerの登録状況、RBAC、Gallery、VM/Batch quota、ネットワーク到達性を事前確認。不足時は理由を示して停止。
3. restore / build / test / publishとZIP作成。ZIP直下のhost.jsonと拡張機能ディレクトリを確認。
4. state backendを準備または参照し、Terraform init / validate / plan。既存モードでは共有Batch資材への変更がないことを機械的に確認。
5. 保存済みplanをapplyし、Account endpoint、Pool ID、Function名、Blob URIをoutputから取得。
6. RBAC反映を待ち、新規Poolは指定全台の準備完了を期限・リトライ・resize error検出付きで待つ。既存Poolは存在・参照権限を確認し、0台でも配置を進める。SmokeはTask投入後に自動拡張・完了を待つ。
7. FunctionコードをEntra認証でZIP配置し、配置完了・Functions検出を確認。
8. 検証オプション有効時は単一Batch smoke→HTTP workflow→manifest/sum=3確認。試験結果を配置結果と区別して保存。
9. 環境名、state key、成果物hash、配置先ID、結果、ログの場所を表示。Function keyや管理URLの秘密は表示・保存しない。

一つのコマンドでも、標準ではplan確認後にapplyへ進む。無人CIは事前レビューされた変更・設定に対する明示オプションで実行する。途中失敗は非ゼロ終了し、作成済みリソースは調査・再実行できる状態で保持する。既存Poolには破壊的なロールバックを行わない。

ZIP配置はIaCとは異なる成果物配置工程だが、同じスクリプトから呼べる。現在のBasic認証無効設定を維持し、Entra対応のAzure CLIまたはSCM ZIP APIを使う。[Functions ZIP配置とEntra認証](https://learn.microsoft.com/en-us/azure/azure-functions/deployment-zip-push)

Quota増枠の承認、組織ポリシーの変更、利用規約同意、未提供のカスタムイメージ生成元はワンコマンド内で無条件に解決できない。新規Batchではcore quotaが0の場合もあるため、Account作成成功とPool実行可能を分けて判定する。[Batchのquota](https://learn.microsoft.com/en-us/azure/batch/batch-quota-limit)

## 追加する資材案と入力

```text
infra/bootstrap/           # 共有実行時のstate backend
infra/modules/batch/       # 新規Account、Windows Pool、条件付きIdentity等
infra/modules/network/     # 選択した通信方式のネットワーク
infra/modules/gallery/     # カスタムイメージも管理する場合のみ
infra/                     # 現行Function/Storage/監視との結合、既存参照モード
environments/poc.example.psd1
scripts/deploy.ps1          # build→IaC→コード配置→確認
scripts/Test-Deployment.ps1 # readiness、smoke、結果照合
```

入力はsubscription/tenant、RG新規・既存、location、名前suffix、Batch新規・既存、allocation mode、VM SKU、台数/slots、image source/version/node-agent SKU、公開・閉域、Subnet/DNS、state backend、実行主体、smoke有無。既存モードだけAccount/Pool ID等を要求し、新規モードではTerraform outputを使う。

実装順は、(1)専用BatchService Account + Windows Pool + 新規RGオプション、(2)deployment scriptと自動設定接続、(3)state bootstrap、(4)選択された閉域化・Gallery構築・UserSubscription対応とする。設定が確定していない条件付き構成を、必須の高コスト資材として一律作成しない。

現行PoCのBatch Job / Taskは引き続きHTTP実行時にアプリが作成する。配置だけで業務ワークフローを開始せず、smokeを選択した場合のみPoC用Jobを作成する。
