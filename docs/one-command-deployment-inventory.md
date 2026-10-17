# ワンコマンドデプロイに必要なIaC構成の整理

調査・実装日: 2026-10-16（現行コード、AzureRM 5.10.0 の実 provider schema、および Microsoft / HashiCorp 公式ドキュメントに基づき構成を策定）

以下の最小構成を実装し、Azure への配置、RHEL 9 VM の初期設定、Private SCM 配置、12 Functions の登録と3 Task smoke（sum=3）を確認しました。標準6シナリオは single / three / classification / failure / conflict が Pass、admission20 が起動履歴取得の制限による NotRun で完了しました（summary 終了コード2）。host 復旧・期限停止・Spot 回収復旧は未実証です。
- `infra/batch.tf`（新規 `BatchService` Account と Windows Pool の作成、新規RG作成オプション、Gallery / Marketplace イメージ参照）
- `infra/bootstrap/`（Terraform state backend 構築用）
- `scripts/deploy.ps1`（ビルド → IaC適用 → ZIPデプロイの一括実行）
- `scripts/Test-Deployment.ps1`（リソースの準備完了確認および任意のスモークテスト）
- `environments/`（環境別の設定ファイル例）

本ドキュメントの一覧表の「現状」は初期検討時の記録を含みます。2026-10-17 の方針変更では Function 受信用 Private Endpoint・Private DNS・検証 VM・NAT Gateway を追加します。VM 経由の ZIP 配置と独立検証も対象です。Storage / Batch の全面閉域化、VPN / ExpressRoute 接続、`UserSubscription`、Gallery イメージのビルド工程、CI/OIDC は後続です。詳細は [接続設計](network-connectivity-design.md) を参照してください。なお、Terraform で Batch リソースの新規作成と既存参照を切り替える変数名は `batch_mode` としています。

## 目標と管理境界

環境設定ファイルをあらかじめ用意しておくことで、Azure認証済みの端末から1コマンドを実行するだけで、アプリケーションのビルド、Terraform によるインフラ構築、Function App へのコード配置、起動確認までを一貫して完了できることを目指します。なお、初期ログイン、各種権限の承認、クォータ上限の引き上げ申請、およびカスタムイメージの元データの準備は、コマンド実行前の前提条件とします。

実装済みの実行インターフェース（Azure認証および環境設定ファイル準備後）:

```powershell
./scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1
```

当初の設計では、既存の Batch Account / Pool / Gallery を参照のみ行う前提としていました。これに対し、環境を一から構築したいケースにも対応できるよう、既存リソースを参照する `existing` モードと、PoC専用の環境を新規構築する `new` モードを選択できる構成としています。事故防止のため、既存の Batch リソースを自動的に Terraform state へ import したり、同名で再作成したりする処理は行いません。また、モード間の切り替えも通常の更新としては扱わず、`terraform plan` の段階で置換・削除対象となることを明示します。

## IaC対象の一覧

「必須」は新規にPoC環境一式を構築するうえで管理が不可欠なリソース、「条件付き」は採用する機能やネットワーク要件に応じて追加が必要になるリソースです。

| 分類 | 対象 / 主なTerraform資材 | 現状 | 追加・変更内容 |
|---|---|---|---|
| 必須 | Resource Group / `azurerm_resource_group` | 既存RGのみ参照 | PoC専用RGの新規作成と既存RGの参照を選択可能にし、`location` を明示する |
| 必須 | Batch Account / `azurerm_batch_account` | ID/endpoint変数のみ | 新規 Account 作成、割当方式、Entra認証、公開アクセス設定を構成し、endpoint/IDをFunctionsへ自動連携する |
| 必須 | Windows Pool / `azurerm_batch_pool` | Pool ID変数のみ | VM サイズ、node-agent SKU、イメージ、専用または Spot ノード数、slots、通信方式、resize期限を管理する |
| 必須 | Windowsイメージ参照 / Poolの`storage_image_reference` | Gallery IDの参照出力のみ | Poolのimage IDへ実際に接続する。Gallery版はversion固定、Marketplace版はpublisher/offer/SKU/versionを指定する |
| 必須 | Function App / Service Plan | 実装済み | 新規または既存Batchから解決した値を App Settings へ引き渡し、設定ファイルとの二重定義をなくす |
| 必須 | Durable・結果Storage / Container | 実装済み | host用のBlob/Queue/Tableと結果格納用 Container を維持し、実行主体の初期データアクセス権限を確認する |
| 必須 | Managed Identity / RBAC | Function権限のみ実装済み | FunctionからBatch/Storageへの権限に加え、IaC実行主体によるPool作成・イメージ参照の権限を整理する |
| 必須 | Insights / Log Analytics | 実装済み | Batchの診断設定を追加してログを集約する。利用可能な診断カテゴリを確認のうえ指定する |
| 必須 | Terraform state / bootstrap | local state | ワンコマンド実行で再利用できるようstateの保存先を固定する。複数人で共有する場合はremote backendのStorage/Containerと実行主体RBACを別stateで管理する |
| 必須 | アプリ成果物の配置 | README手動手順 | build/publish、ZIP化、Entra認証を用いたコード配置、配置結果の確認までをスクリプト化する（Terraformとは別工程として実行） |
| 必須（今回追加） | VNet / Subnet / NSG / NAT / Linux VM | Azure 配置・RHEL 初期設定・Run Command 実行確認済み | Public IP なし RHEL 検証 VM と NAT 送信を構成。CLI / PowerShell から3 Task smoke成功。Pool 用 VNet / UDR は後続 |
| 必須（受信）/ 後続（送信） | Private Endpoint / Private DNS / Function VNet Integration | Function / SCM 配置・疎通、公開アクセス Disabled、接続 Approved 確認済み | Function の送信 VNet Integration、Storage と Batch の Private Endpoint は後続。外部からの HTTP 拒否試験は別途確認 |
| 条件付き | Gallery / Image Definition / Image Version | 既存参照のみ | イメージ自体も新規作成する場合は `azurerm_shared_image_gallery` / `azurerm_shared_image` / `azurerm_shared_image_version` を追加する（元イメージの取得・ビルド工程も別途必要） |
| 条件付き | Key Vault / Batchサービス権限 | 未実装 | `UserSubscription` 割当方式を採用する場合に追加する。標準の `BatchService` 構成には含めない |
| 条件付き | Pool User Assigned Identity / AutoStorage | 未実装 | resource files、application packages、OutputFiles、または計算ノードからStorageへ直接アクセスする場合に追加する（現在のダミーTask構成では不要） |
| 条件付き | CI/CD実行Identity / OIDC | 未実装 | GitHub Actions等から自動実行する場合に追加し、ローカルCLI実行時とは権限主体を分離する |

Batch Account と Pool は AzureRM プロバイダーで管理できます。計算ノードの実体である VM・NIC・OSディスク等は Pool の設定に従って Batch サービス側が動的に払い出すため、Terraform 側で個別 VM リソースとして重複管理する必要はありません。なお、provider schema にて `storage_image_reference.id`、`fixed_scale`、`target_node_communication_mode`、`network_configuration` が利用できることを確認済みです（参照: [Batch Pool定義](https://github.com/hashicorp/terraform-provider-azurerm/blob/main/website/docs/r/batch_pool.html.markdown)、[Batch Account定義](https://github.com/hashicorp/terraform-provider-azurerm/blob/main/website/docs/r/batch_account.html.markdown)）。

## Batch構成で確定が必要な事項

最小PoC構成では、PoC 専用の Batch Account に Windows Pool を1つ作成し、Spot ノード1台・専用ノード0台・1ノードあたり1 slot・固定スケール（fixed scale）・`Simplified` 通信モードを採用します。`batch_node_type` は `dedicated` または `spot` を受け取り、省略時は既存設定との互換性のため `dedicated` です。PoC サンプルと実設定で `spot` を明示します。`batch_node_count` は選択した種別のノード総数（1〜3台）であり、混在構成は対象外です。検証用 Linux VM は通常 VM のままです。

ワークフロー側では3つの Task を並行して監視しますが、1台1 slot では計算処理自体は順次実行されます。3つを同時に実行するにはノード数を3台にするか、1ノードあたり3 slots へ変更します。VM SKU については、イメージ、対象 Batch account の該当種別のコアクォータ、およびリージョン容量を確認します。`Standard_D2s_v5` の Spot 1台には Spot コア（API 名 `lowPriorityCoreQuota`）が最低2必要です。専用コアのクォータや検証 VM 用 Compute クォータとは別枠です。[Batch クォータ](https://learn.microsoft.com/en-us/azure/batch/batch-quota-limit)

### Spot の容量・中断と結果回収

クォータ承認は Spot の空き容量を保証しません。確保できなければ Pool 準備待ちが期限に達し、実行中の回収でもワークフローが期限に達する場合があります。回収されたノード上で中断した Task の再キューは、Task の非ゼロ終了に対する `MaxTaskRetryCount=0` と別の動作です。現在は Task がノードローカルへ出した結果を後から Activity が取得するため、回収前にノードを失うと結果を取得できない場合があります。今回の変更は IaC・配置確認・文書のみで、C# や結果保存方式は変更しません。Task から Blob への直接永続化は後続課題です。[Batch Spot](https://learn.microsoft.com/en-us/azure/batch/batch-spot-vms)

新規モードの Terraform output は `batch_node_type`、`batch_dedicated_node_count`、`batch_spot_node_count` を返し、`batch_node_count` は従来どおり総台数を維持します。既存参照モードのこれら新規作成用 output は `null` とし、既存 Pool の種別・台数は変更しません。配置確認では新規 Pool の目標台数だけでなく、準備できたノードが指定した種別であることも確認します。

### 割当方式

- `BatchService`: 最小PoCにおける第一候補です。Batch サービスが管理するサブスクリプション側で計算ノードが割り当てられます。
- `UserSubscription`: 計算ノードの VM 等をユーザー自身のサブスクリプション内に配置する要件がある場合に採用します。Key Vault の準備や、Batch サービスプリンシパルに対する `Azure Batch Service Orchestration Role` の付与などが追加で必要になるため、サブスクリプションの初期セットアップ（bootstrap）はアプリ配置工程から切り分けます。

前提条件の詳細は [Account作成とUserSubscription前提](https://learn.microsoft.com/en-us/azure/batch/batch-account-create-portal) を参照してください。なお、Function App に付与するジョブ投入権限（Job Submitter 相当）と、Terraform の実行主体が Pool を作成するための権限は別物であるため、それぞれ適切に付与する必要があります。

### Windowsイメージ

既存の Azure Compute Gallery イメージバージョンを利用する場合は、一般化（generalized）済みであること、Batch 対応 OS と node-agent SKU の組み合わせが正しいこと、Batch と同一リージョンへレプリケーションされていること、および Pool 作成主体の権限を事前に確認します。Microsoft の公式ドキュメントに記載されているとおり、Gallery イメージから Batch Pool を作成する際は通常の `Reader` ロールだけでは権限が不足し、`Microsoft.Compute/disks/beginGetAccess/action` 権限が必要となります。そのため、IaC で `Reader` のみを自動付与して完了とするのではなく、必要なカスタムロールまたは適切な組み込みロールが確実に割り当てられていることを確認します（参照: [GalleryイメージによるPool作成](https://learn.microsoft.com/en-us/azure/batch/batch-sig-images)）。

もしカスタムイメージ自体も新規に作成する場合は、Terraform で Gallery の枠組みを用意するだけでは完結しません。元となる managed image / snapshot / VHD の準備や、Azure VM Image Builder 等を用いた Windows イメージのビルド工程を別途構築する必要があります。一方、PowerShell のダミー Task のみを動かす最小 PoC であれば、Batch 対応の Marketplace Windows イメージを直接指定する構成も有力な選択肢となります。既存のカスタム HPC イメージの元データが存在しない場合は Terraform だけで環境を再現することはできないため、どのイメージソースを利用するかを事前に明確にしておきます。

### ネットワーク

最小構成であっても、Function App から Batch API、Function App から Storage、および Pool から Batch node management への通信経路が確保されていることを確認します。新規作成する Pool には `Simplified` 通信モードを明示的に指定します（参照: [Simplified通信要件](https://learn.microsoft.com/en-us/azure/batch/simplified-compute-node-communication)）。

閉域網構成とする場合は、VNet 内に Pool 用、Function VNet 統合用、Private Endpoint 用の各 Subnet を分割して配置します。そのうえで、Batch API 用の `batchAccount` とノード管理用の `nodeManagement` の両方の Private Endpoint、および対応する Private DNS ゾーンと VNet リンクを管理します。Storage の blob / queue / table についても同様にプライベート経路を用意し、デプロイ実行端末や CI runner から Function App の SCM エンドポイントおよび Terraform が操作する各データプレーンへ到達できるよう設計します。

なお、Pool の Public IP を無効化するだけでは計算ノードの通信は成立しません。`nodeManagement` 用の Private Endpoint と名前解決（DNS）を構成し、さらに外部へのアウトバウンド通信が必要な場合は NAT Gateway や Azure Firewall 等の経路もあわせて設計する必要があります。Public IP なし Pool に関する Microsoft 公式ドキュメントでは、NAT 等によるアウトバウンド経路の説明と Private Endpoint 必須の注記が混在しているため、閉域網を採用する際は `nodeManagement` の Private Endpoint を含めて実機で疎通を検証します（参照: [Public IPなしPool](https://learn.microsoft.com/en-us/azure/batch/simplified-node-communication-pool-no-public-ip)）。

## ワンコマンド実行でまとめる工程

1. .NET SDK / Terraform / Azure CLI のバージョン、Azure ログイン状態、対象サブスクリプション、および設定ファイルの内容を確認する。
2. 必要な Resource Provider の登録状況、RBAC、Gallery イメージ、VM / Batch クォータ、ネットワーク到達性を事前チェックし、不足がある場合は理由を表示して処理を停止する。
3. アプリケーションの `restore` / `build` / `test` / `publish` を実行してデプロイ用 ZIP を作成し、ZIP 直下に `host.json` と拡張機能ディレクトリが含まれていることを確認する。
4. Terraform の state backend を準備または参照し、`init` / `validate` / `plan` を実行する。既存参照モード（`existing`）の場合は、共有 Batch リソースへの変更が含まれていないことを機械的にチェックする。
5. 保存済みの plan ファイルを `apply` し、Batch Account endpoint、Pool ID、Function App 名、Blob URI 等を Terraform output から取得する。
6. RBAC の反映を待機したのち、新規 Pool モードでは指定台数のノードが準備完了になるまでタイムアウト・リトライ・resize error 検出付きで待機する。既存 Pool モードでは Pool の存在と参照権限のみを確認し、稼働ノード数が 0 台でもアプリ配置へ進む（スモークテスト実行時は Task 投入後のオートスケールおよび完了を待機する）。
7. 検証 VM の cloud-init 準備完了後、Managed Identity でログインし、Private SCM へ Entra 認証 ZIP 配置を実行する。Run Command と転送 Blob を用いてデプロイ完了と各 Function の認識状態を確認する。
8. 検証オプション（`-Smoke`）指定時は、VM 上の HTTP トリガーによるワークフロー起動（3 Task）のみを実行し、 `manifest.json` の生成および `sum=3` の集約結果確認までを実行し、スモークテスト結果をデプロイ結果とは区別して記録する。ローカルの BatchSmoke による単一 Task 投入は行わず、単一 Task は独立ランナーの `-Scenarios single` で検証する。
9. 最後に環境名、state key、成果物ハッシュ、デプロイ先リソースID、実行結果、およびログの出力先を表示する（Function key や管理用URLに含まれるシークレット等は画面表示・ファイル保存ともに抑止する）。

ワンコマンドデプロイであっても、事前確認を行う場合は `-PlanOnly` オプションにより `terraform plan` の差分を精査したうえで適用へ進む運用が可能です。途中でエラーが発生した場合は非ゼロの終了コードで停止し、原因調査や再実行を容易にするため作成済みリソースはそのまま保持します。また、既存 Pool に対して破壊的なロールバック処理は行いません。

Function App への ZIP 配置は IaC（Terraform）とは別工程のアプリケーションデプロイ処理ですが、同一のデプロイスクリプト内から連続して呼び出せるようにしています。セキュリティ維持のため現在の Basic 認証無効化設定はそのまま維持し、Entra 認証に対応した Azure CLI コマンドまたは SCM ZIP API を使用してデプロイします（参照: [Functions ZIP配置とEntra認証](https://learn.microsoft.com/en-us/azure/azure-functions/deployment-zip-push)）。

なお、クォータ上限の引き上げ承認、組織ポリシーの変更、Marketplace イメージの利用規約同意、および未提供のカスタムイメージ元データの用意については、スクリプトの自動処理だけでは完結できません。特に新規作成した Batch Account では初期のコア数クォータが 0 に設定されているケースもあるため、Batch Account の作成成功と Pool の実稼働可否は切り分けて判定します（参照: [Batchのquota](https://learn.microsoft.com/en-us/azure/batch/batch-quota-limit)）。

## ディレクトリ構成案と入力パラメータ

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

設定ファイルで受け取る主な入力パラメータは、subscription / tenant、RGの新規作成・既存利用の別、location、リソース名 suffix、Batchの新規作成・既存利用の別（`batch_mode`）、allocation mode、VM SKU、ノード台数 / slots、image source / version / node-agent SKU、公開・閉域の別、Subnet / DNS 設定、state backend 設定、実行主体、およびスモークテスト実行の有無です。Batch Account ID や Pool ID 等の直接指定は既存参照モード（`existing`）のときのみ必須とし、新規作成モード（`new`）では Terraform の output から自動的に引き継ぎます。

実装の優先順位は以下の4段階とします。要件が確定していない条件付きの構成要素（Storage / Batch の全面閉域化や `UserSubscription` 用リソースなど）は、コストや管理負荷の増大を避けるため初期構成には含めず、要件確定後に順次追加する方針とします。
1. 専用 `BatchService` Account + Windows Pool + 新規 RG 作成オプション
2. デプロイスクリプト（`scripts/deploy.ps1`）と設定値の自動連携
3. リモート state 用の bootstrap 構成
4. 要件に応じた閉域網対応・Gallery イメージ構築・`UserSubscription` モードへの対応

なお、本PoCにおける Batch Job および Task は、従来どおり HTTP リクエスト実行時にアプリケーション側から動的に作成します。インフラとアプリのデプロイ処理だけで業務ワークフローが自動実行されることはなく、スモークテストのオプションを明示的に有効にした場合のみ検証用の PoC Job を投入します。
