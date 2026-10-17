# プロジェクトメモリ

## 2026-10-17 信頼性検証ランナーの追加（ローカル実装）

- **範囲**: 再起動復旧 runner、DTS の現行履歴集計、timeout の Batch 制約照合を追加しました。今回 Azure の設定変更・再配置・Function 再起動・故障試験は行っていません。直下の DTS 基本動作の Azure 成功記録と区別します。変更は未コミットです。
- **復旧 runner**: `scripts/Invoke-PocRecoveryValidation.ps1` が非公開 Function の1 Task の Running を VM で確認し、操作者の認証で指定 Function を restart、再送なしで同じ instance / Task の完了・作成時刻・fingerprint・Blob 成果物を照合します。対象の明示、専用試験の明示、観測時刻の鮮度確認、finally での start を実装しました。Entity checkpoint の強制中断は証明しません。
- **権限差分**: 状態確認用の system key をメモリ内で再取得するため、VM の Function スコープのロールに `Microsoft.Web/sites/host/listkeys/action` を追加しました。host key の読み取りを含むため、次の実機試験前に IaC の差分を確認して反映します。VM への restart / start 権限は追加していません。管理 URL・キーをバンドルや Blob に保存しません。
- **timeout**: 実際の Job の maxWallClockTime が設定と一致することを必須にし、欠落・不正・不一致を Fail とします。既知の停止理由と制約秒数だけを証跡へ追加しました。Function 停止中の Batch 安全網の実証は別の残作業です。
- **DTS 履歴**: `DtsProbe --history true` で返却履歴のイベント件数を集計します。payload を表示せず、`allGenerationsObserved=false` を明示。確認した SDK / 公開契約では全 executionId の列挙・網羅を保証できないため、admission20 の厳密判定は NotRun を維持します。設計書の一意性を実証済みと読める表現も修正しました。
- **検証**: Terraform main Mock27件・fmt・validate、.NET93件（既存91 + 履歴集計2件）が成功しました。PowerShell の timeout / VM transport / RecoveryValidation の3組も成功。新 runner は権限不足・古い観測・Task 再作成の拒否、再起動失敗時の復元、秘密非記録、実 CLI 子プロセスの引数保持を Fake で確認しました。手順は [信頼性の追加検証](reliability-validation.md) を参照してください。
- **次**: IaC の権限差分を反映した後、他のワークフローがない PoC Function で timeout と再起動復旧を個別に実機検証します。全世代起動履歴、host 停止中の Batch 安全網、Spot 回収、実 C++、認証 API、全面閉域化は未完了です。

## 2026-10-17 DTS の Azure 配置・実接続検証完了

- **最新状態**: `dts-poc` を Japan East の別 RG `poc-durable-scheduler-rg` に配置しました。Consumption Scheduler `poc-dts40481149-dts`、hub `DtsPocHub`、保持30日、新 Function `poc-dts40481149-func`、Storage、RHEL 検証 VM を作成しています。段階適用は4件と32件の新規作成で、変更・削除0件です。remote state は既存とは別の `dts-poc.tfstate` です。
- **既存環境との関係**: B1 の追加枠がないため、既存 Linux App Service Plan を参照して共有しています。既存 Azure Storage Function の構成・コードと既存 Batch account / pool は変更していませんが、Plan の CPU・メモリと Batch pool の処理容量は共有します。既存 Plan の参照、新 Function の別 RG 配置、hub スコープの MI Contributor と観測者 Reader を IaC に追加しました。
- **実 Azure 成功**: Private SCM 配置と12 Functions の登録、公開受信無効、新 RHEL VM からの受付、Windows Batch Spot で3 Task、Blob 結果・manifest の整合性（Sum=3）を確認しました。配置結果は `.deploy/dts-poc/20261017-224759-6853/deployment-result.json` です。
- **DTS の独立確認**: 読み取り専用 `tools/DtsProbe` から現在の Azure CLI 認証で hub を直接照会し、`poc-8689eb6004faae579c3e4eae6de22e50310d4ff9` が `MainOrchestrator` / `Completed` であることを確認しました。証跡は `artifacts/dts-validation/live-instance.json`。summary の構成値だけによる判定ではありません。
- **標準シナリオ**: `2026-10-17T14:06:03Z` に single / three / classification / failure / conflict の5件 Pass。admission20 は `AdmissionObservedButExecutionHistoryUnavailable` で NotRun、終了コード2です。Entity 経由の受付は動作しましたが、全世代の起動一意性は未実証です。証跡は `artifacts/dts-validation/azure-scenarios/`、VM の失敗段階・エラーコードは null です。
- **ローカル検証**: Terraform main Mock27件・fmt・validate、PowerShell5組、.NET91件、Release publish が成功しました。DtsProbe も build 成功。証跡は Git 管理外です。DTS 実装・配置手順・実 Azure 検証結果を同じコミットにまとめます。
- **残作業**: host 復旧、期限停止、Spot 回収復旧、実 C++ 連携、全世代の起動履歴観測、全面閉域化を継続します。DTS emulator と保持期限後の挙動は未検証です。以下の各節は過去のチェックポイントです。

## 2026-10-17 Durable Task Scheduler 選択対応（ローカル資材）

- **今回の範囲**: `durable_backend='azureStorage'|'durableTaskScheduler'` と `durable_task_hub_name` を追加しました。既定は従来の Azure Storage / PocBatchHub。稼働中の Azure 環境、実設定、remote state は変更していません。下記の Azure 5件 Pass は Azure Storage の記録であり、DTS での成功ではありません。
- **実装**: AzureManaged 1.10.3 拡張と host.json の既定 provider を整備しました。DTS 選択時のみ AzAPI 2.12.0 / ARM 2026-02-01 で Consumption scheduler・task hub・default retention 30日を作成し、Function MI に hub スコープの Durable Task Data Contributor を付与します。接続は MI、Function 受信は閉域のまま、DTS 送信は公開 TLS/gRPC + Entra 認証です。host Storage と入力・結果 Blob のリソース・権限は残します。
- **互換性と移行**: provider 間の履歴・Entity 状態移行はできません。既存 Environment の backend/hub 変更と、同じ Function を指す別ローカル Environment の切り替えを拒否します。旧 identity は Azure Storage / PocBatchHub として解釈します。別端末や直接 Terraform 操作まで防ぐものではありません。別 Function・Storage・state、新 requestId で検証します。Consumption の完了履歴保持上限30日を前提とし、保持期限後の再送保証は行いません。
- **資材**: `environments/durable-task-scheduler.example.psd1`、`src/Poc.Functions/local.settings.scheduler.json.example`、[DTS PoC 手順](durable-task-scheduler-poc.md) を追加しました。summary の durableBackend / taskHubName は構成値であり、実接続の独立した証明ではありません。
- **ローカル検証**: Terraform main Mock21件・fmt・validate、PowerShell5組、.NET91件、Release build/publish が成功しました。publish と ZIP 内に AzureManaged DLL / host startup 登録があり、既定 Azure Storage、local.settings 除外、POSIX ZIP パスを確認しました。.NET 証跡は `artifacts/dts-validation/`。DTS emulator / Azure DTS は未実行です。
- **次の作業**: Microsoft.DurableTask の登録・対象リージョン・クォータを確認し、DTS 用の別 Environment を plan/apply して smoke と標準シナリオを実施します。DTS での Entity 受付、host 復旧、保持期限の扱いも個別に確認します。


## 2026-10-17 RHEL 9 VM からの Private Function・3 Task 実証

- **最新状態（以下は過去のチェックポイント）**: Azure 上で Private SCM 配置、12 Functions の登録、VM からの HTTP 受付、Windows Batch Spot ノードでの3 Task 実行、Blob 結果回収・集約まで成功しました。`artifacts/deployment-recovery/rhel-private-deploy/vm-operation.json` は `exit_code=0`、`validation.Ready=true`、`Smoke=Pass`、`TaskCount=3`、`Sum=3` です。完了時刻は `2026-10-17T13:06:18Z`。これは実 Azure の成功記録です。
- **配置とネットワーク**: Batch Spot コア枠2・Spot1台・専用0台です。App Service B1 / Total Regional VMs の枠は各1へ反映し、同じ remote state でインフラ apply が完了しました。Function の `publicNetworkAccess=Disabled` と Private Endpoint 接続 `Approved`、Spot ノードの準備完了を確認しました。公開ネットワーク設定の確認と、外部からの HTTP 拒否試験は区別します。Storage / Batch の全面閉域化は未実施です。
- **検証 VM**: ユーザー希望により RHEL 9 を採用しました。B2s の `SkuNotAvailable` を受け、通常 VM `Standard_F1as_v7`（1 vCPU / 4 GB）+ NVMe を使用しています。`RedHat:RHEL:9-lvm-gen2:latest`（image `9.8.2026080415`）の配置が成功し、ゲストで RHEL 9.8、cloud-init `done`、Azure CLI 2.91.0、PowerShell 7.6.7、ready marker を確認しました。VM は Public IP なし・SSH 受信拒否、NAT 経由の送信と Run Command による操作です。
- **実機で判明した修正**: Windows PowerShell / .NET Framework の ZIP 作成で37エントリにバックスラッシュが入り、Linux host が `.azurefunctions` を認識できませんでした。ZIP の全エントリを相対 POSIX パスで作成する方式へ修正し、実89エントリのバックスラッシュ0・`.azurefunctions/` 配下36件を確認して再配置しました。SDK の worker indexing 用 metadata は DLL に生成済みで、`functions.metadata` がないこと自体は欠落ではありません。
- **配置スクリプトの修正**: SCM ホスト名の取得は補助的な構成・公開プロファイル取得を伴う `az functionapp show` から `sites/read` の ARM GET へ変更し、権限を追加せず解決しました。Blob 転送に `--no-progress` を追加して JSON への進捗混入を防止しました。Run Command の個別 stdout 形式と `[stdout]` / `[stderr]` 集約形式に対応し、成功済みのゲスト結果をホスト側が誤失敗にする不備を修正しました。失敗段階・HTTP 整数ステータスだけを記録し、例外本文・キー・トークン・管理 URL は記録しません。
- **ローカル回帰確認**: Terraform main Mock18件・fmt・validate、修正対象の PowerShell 試験（Deployment.Common / Test-Deployment / PocValidation / PrivateVm）が成功しました。ZIP の raw entry 名と内容、進捗抑止、最小権限の ARM GET、診断の秘匿、Run Command の集約形式と stderr による誤成功防止を確認しています。過去の .NET91件・Release publish 成功記録とは区別します。
- **独立ランナーの修正と実結果**: 初回の標準6シナリオは進行中 HTTP 202 の未対応で全件 `InvalidStatusResponse` でした。200 / 202 と既知の `runtimeStatus` を受理し、202 の観測回数を保存するよう修正しました。再検証は `2026-10-17T13:14:57Z` に完了し、single / three / classification / failure / conflict の5件が Pass、admission20 は `AdmissionObservedButExecutionHistoryUnavailable` による NotRun、summary 終了コード2です。全世代の Main 起動履歴を取得できないため、同時受付の観測を厳密な起動一意性の証明に繰り上げません。初回証跡は `artifacts/deployment-recovery/rhel-validation/`、再検証は `artifacts/deployment-recovery/rhel-validation-202/` に保持しています。
- **終了コードの転送修正と最終確認**: summary 終了コード2を Worker の子 `PowerShell -Command` が1へ畳んでいたため、子コマンド末尾で `exit $LASTEXITCODE` を明示しました。実コマンドによる子プロセス回帰試験に加え、修正後の Azure admission20 単独実行も `2026-10-17T13:17:59Z` に完了しました。`artifacts/deployment-recovery/rhel-validation-exitcode/` の summary 終了コード2、RHEL guest の `vm-operation.json` の `exit_code=2`・`error_code=null`・`failure_stage=null`・`summary_available=true` を確認し、wrapper スクリプトも2を返しました。自動化で `powershell -Command` から wrapper を呼ぶ場合は、呼び出し元も末尾に `exit $LASTEXITCODE` が必要です。これを省略した外側シェルの終了コード1は PowerShell の挙動であり、リポジトリの失敗とは区別します。標準6シナリオは5件 Pass・admission20 NotRun で完了しています。
- **後続検証**: 期限停止、host 復旧、Spot 回収復旧、実 C++ 連携は未実証です。既存 state を継続し、古い保存 plan は適用しません。


## 2026-10-17 Spot 切り替え・増枠承認待ち

- **現在の方針**: Windows Batch 計算ノードのみ Spot へ切り替えます。`batch_mode='new'` に `batch_node_type='dedicated'|'spot'` を追加し、省略時は従来互換の `dedicated`、PoC サンプルと実設定は `spot` とします。`batch_node_count=1` は Spot 1 台・専用 0 台です。VNet 内の Linux 検証 VM は通常 VM のままです。
- **申請と停止条件**: ユーザーから Spot コア増枠申請済みとの報告を受けました。承認は未確認で、今回 Azure の再照会・変更は行いません。最後に確認した実値は下記履歴の `dedicatedCoreQuota=0`、`lowPriorityCoreQuota=0`、`poolQuota=20` です。対象 Batch account の Spot コア（API 名 `lowPriorityCoreQuota`）が最低2になったことを確認するまで、Pool・検証 VM・Function・NAT の配置は再開しません。
- **再開手順**: 増枠の実値を Azure で確認後、同じ実設定・bootstrap state・remote state を使い `deploy.ps1` で再 plan してから apply します。state 更新前または Spot 設定変更前の保存 plan は適用しません。再 import や新規 state の作成は不要です。
- **Spot の制限**: クォータ内でも空き容量は保証されず、Pool 準備期限やワークフロー期限に達する場合があります。回収時の Task 再キューは非ゼロ終了に対する `MaxTaskRetryCount=0` と別の動作です。現在のノードローカル結果は Activity の回収前に失われる可能性があり、Task から Blob への直接永続化は未実装です。今回は C# を変更しません。詳細は [配置手順](../README.md) と [IaC 設計](one-command-deployment-inventory.md) を参照してください。
- **ローカル検証**: PowerShell オフライン5組、Terraform Mock17件（main16 + bootstrap1）、PowerShell parse、Terraform fmt / validate が成功しました。証跡は `artifacts/spot-validation/`（Git 管理外）、詳細は [検証結果](test-results.md) に記録しています。今回は C# 変更なしのため .NET91件・publish は再実行せず過去の成功記録を維持します。Azure 操作は実施せず、Spot ノード確保・回収・再キュー・結果回収は未検証です。ランナーの `batchNodeType` は設定値で実ノードの観測ではなく、Spot 回収・復旧（`SpotEvictionRecovery`）は対象外です。

## 2026-10-17 Azure 初回事前確認

- **当時の停止理由（専用ノード方針時の履歴）**: 実作成した専用 Batch account の `dedicatedCoreQuota=0`、`lowPriorityCoreQuota=0`、`poolQuota=20` を確認しました。当時予定していた `Standard_D2s_v5` 専用1台には dedicated core quota が最低2必要でした。現在は上記の Spot 方針へ変更しています。Compute VM 用の空き枠とは別の制限です。
- **作成済み**: state 専用 RG `poc-terraform-state-rg` と Storage / Container / backend 権限割当（4リソース）、PoC 専用 RG `poc-durable-batch-rg` と Batch account（2リソース）。既存 Gallery は参照のみです。実リソース名・ID・設定は Git 管理外の設定と state に保持しています。
- **計画と再開**: main の plan は新規35リソース・削除/置換なしでした。その後、クォータ事前確認のため同じ remote state で RG / Batch account のみを target apply しました。以前の full plan は state 更新前のものなので適用せず、増枠後は `deploy.ps1` で必ず再 plan してください。bootstrap state と remote state を保持し、既存環境への再 import や新規 state は作成しません。
- Azure CLI 2.91.0 のログインとサブスクリプション Owner 権限を確認しました。配置に必要な Resource Provider を登録し、東日本リージョンの検証 VM 用 vCPU 枠を確認しました。
- 既存 Gallery の Windows Server 2022 / version 1.0.0 は Windows・Generalized・Gen2、Japan East replica、Succeeded を確認しました。Gallery は参照のみです。Batch 上での実起動と Batch account の core quota は別途確認が必要です。
- 実設定は Git 管理外の `environments/poc.psd1` に作成しました。検証用 SSH 鍵は Git 管理外の `.deploy/poc/ssh/` に保存し、設定には公開鍵のみを記載しています。
- 実設定で build・単体テスト91件・Release publish が成功しました。state backend が未作成の状態で `-PlanOnly` が bootstrap plan のみを保存し、リソースを作成しないことも確認しました。
- CLI の新規インストール先が既存シェルの PATH に未反映だったため、`deploy.ps1` で設定済み Azure CLI のディレクトリを子プロセスの PATH に追加し、終了時に復元するよう修正しました。Terraform への引き継ぎと復元はオフライン試験で確認しています。
- 管理プレーンの事前確認と PoC ワークフローの実証は区別します。Function / Private SCM / VM / Batch Task の実環境動作はまだ未検証です。

## 2026-10-17 次期実装で確定した前提

- **検証環境の前提**: 本リポジトリは個人の検証環境を対象としています。将来の業務環境では ExpressRoute による閉域接続を想定していますが、個人環境で同一の専用回線・社内 DNS・企業ディレクトリ権限を利用できるとは仮定しません。
- **エンドポイント方針（ユーザー承認済み変更）**: Function は Private Endpoint を追加し、公開ネットワークアクセスを無効化します。VNet 内の Ubuntu 24.04 検証 VM から Azure CLI・PowerShell を実行します。VM は Public IP なし、SSH 受信拒否、NAT Gateway による明示的な送信経路とし、Run Command で操作します。Function key 認証を継続し、VM Managed Identity で key 取得・配置・Blob / Batch 参照を行います。Storage / Batch の全面閉域化と VPN / ExpressRoute 接続は後続フェーズです。
- **ネットワーク IaC の分離方針**: 将来のネットワーク IaC では、VNet・Private Endpoint・プライベート DNS と、VPN / ExpressRoute の回線接続部分を明確に分離します。既存の ExpressRoute 共有回線をアプリ側の IaC に直接取り込むことはしません。詳細は [接続設計](network-connectivity-design.md) を参照してください。
- **ステータス確認の原則**: 本ドキュメントの記録は過去のチェックポイント情報を含むため、作業時は必ず現在の Git HEAD、ワーキングツリーの変更状態、および実際のテスト結果を確認してください。なお、Azure 実環境への接続試験は引き続き未実施です。
- **独立検証ランナーの実装**: `scripts/Invoke-PocValidation.ps1` を実装しました。単一 / 3 Task、分類、意図的な exit 42、20 並列受付と再送、異なる入力パラメータによる 409 競合、専用環境でのタイムアウト停止を自動判定し、シークレットを除外した `summary.json` / `summary.txt` と終了コード（0/1/2）を出力します。ただし、現在の管理 API では全世代にわたる Main オーケストレーションの起動履歴を完全には証明できないため、20 並列シナリオは観測値を記録したうえで `NotRun` として扱います。
- **今回の変更前のローカル検証結果（履歴）**: .NET 単体テスト全 91 件（7 テストクラス、全件 Pass）、Release publish、ランナー オフライン試験（実ローカル HTTP 20 並行送信を含む）、PowerShell 試験 4 組、Terraform Mock 試験 8 件（main 7 件 + bootstrap 1 件）がすべて成功しています。証跡は `artifacts/` 配下に保存されます。実行手順は [README.md](../README.md)、詳細なテスト結果は [検証結果](test-results.md) を参照してください。
- **今回のローカル検証**: PowerShell オフライン試験5組（既存4組 + PrivateVm）、Terraform Mock14件（main13 + bootstrap1）が成功しました。.NET91件は前ターンに再実行して成功、今回 C# 変更はなく再実行していません。Release publish も今回は再実行していません。Azure 実環境は未検証です。実装済みの VM ラッパーはローカル checkout の Git・ソース fingerprint・package hash を転送し、VM 内で来歴を記録します。
- **処理フローの文書化**: 検証ランナーと Azure 側の処理順序、生成される成果物、およびシナリオ別の判定条件は [検証ランナーの処理フロー](validation-runner-flow.md) に記録されています。
- **今後のロードマップ**: 今回は VNet・検証 VM・Function Private Endpoint・Private DNS と VM 経由の配置・検証を整備します。実環境設定・SSH 公開鍵・権限を確定して VM 経由の Azure 実証を行い、その後、Storage / Batch とオンプレ接続の閉域 IaC → 認証・認可付き API → 実 C++ 入出力連携へ進みます。なお、検証ランナー自体はインフラ配置、Pool 設定変更、host 停止、権限変更、自動クリーンアップを行いません。

記録日: 2026-10-17  
本ドキュメントは、これまでの実装・検証の到達点と前提条件をまとめた引き継ぎメモです。作業再開時は、現在の Git ステータスおよび最新のソースコードとあわせて確認してください。

## 目的と運用前提

- **対象リポジトリ**: `D:\durable_functions_poc`（Azure Batch + Durable Functions による非同期計算ワークフローの PoC）
- **利用形態**: ユーザーはオンプレミス環境の Web UI からシナリオ投入およびジョブ開始を行う。
- **アプリケーション構成**: 計算処理本体は Windows 計算ノード上で動作する C++ アプリケーション。Web バックエンドは C# が想定されるが未確定。
- **業務領域**: 金融領域のオプションシミュレーション。シナリオ入力と計算結果は Azure Blob Storage 経由で受け渡す（PoC 段階では業務ロジックの詳細には踏み込まず、一般的な非同期計算ワークフローとして扱う）。
- **ネットワーク前提**: オンプレミスと Azure 間は ExpressRoute で接続されており、既存回線を前提に閉域接続（Private Endpoint 等）・DNS・認証・デプロイ経路を整備する。
- **PoC のスコープ**: 既存の C++ 金融計算モデルの再実装はスコープ外。現在の Batch Task は一定時間 `sleep` した後に整数 `1` を出力するダミー処理としている。

## コミットと検証済み範囲

- **今回の作業開始時 HEAD（履歴チェックポイント）**: `a8a8434` — `Refine documentation across repository for natural technical Japanese and consistency`。直前の `a518050` は HTTP / Gateway 単体テスト拡充。最新状態は常に git log / git status で確認します。
- **今回の変更前のローカル検証結果（履歴）**:
  - .NET 単体テスト: 91 件 Pass（全 7 テストクラス合格、0 Fail / 0 Skip）
  - Release publish: 成功
  - PowerShell オフライン試験: 4 組成功（Deployment.Common / Test-Deployment / Deploy.Workflow / PocValidation）
  - Terraform Mock 試験: main 7 件 + bootstrap 1 件成功
  - 証跡ログ: `artifacts/runner-validation/`、`artifacts/reliability-validation/dotnet-test.log`、`publish.log`（`artifacts/` は Git 管理外）
- **未検証範囲**: Azure 実環境へのデプロイ、Entra 認証・RBAC、Durable Entity の実環境復旧、実 Batch 上での Job 停止は未実施。

## 実装済みの改善内容

- **Terraform state backend の事前確認**: `Backend.Create=true` かつ `-PlanOnly` 指定時でも、backend が作成済みであれば main の `terraform plan` まで進行するように改善。また、backend の「未作成（不存在）」と「参照エラー（権限不足や通信失敗）」を明確に区別。
- **Batch Pool のノード数チェックとページネーション**: 既存 Pool 利用時はノード数 0 台（autoscale 待機状態など）でもデプロイを継続可能とし、新規 Pool 作成時は指定台数すべての準備完了を待機するよう実装。ノード一覧取得時の pagination にも対応。
- **同一 `requestId` の排他制御と冪等な受付**: immutable な入力 Blob と Durable Entity を組み合わせ、同一 `requestId` に対する入力内容の競合検出（409）と Orchestrator 起動の直列化を実装。HTTP 202 応答は「Entity への起動通知の受理」を意味し、Main Orchestrator のインスタンス登録が完了するまでの間に状態照会（GET）が 404 を返した場合は、クライアント側で一時待機して再照会する設計としている。
- **HTTP レイヤーの入力検証とエラー応答**: `StartWorkflow` において不正 JSON や無効パラメータに対する 400 Bad Request 応答、Durable クライアントとの通信障害時における 503 Service Unavailable + `Retry-After` 応答、正常受付時の 202 Accepted + Location ヘッダー返却を実装・単体テスト化。
- **Activity のリトライ制御と例外分類**: 一時障害（Transient Fault）のみを対象に最大 3 回試行（Durable タイマー待機 5 秒 / 10 秒）。SDK が投げる `AggregateException` や通信タイムアウトと、呼び出し元によるキャンセル要求を区別し、`CancellationToken` を SDK API まで伝播。
- **親子共有の監視期限（Deadline）と Batch Job 停止**: 親・子 Orchestrator 間で共有する監視期限を導入。期限超過時は Job / Pool / fingerprint / ETag を照合したうえで Batch Job に停止要求を送り、最大 2 分間停止完了を確認する。停止完了を確認できなかった場合は `customStatus` に記録し、Manifest の公開を抑止する。
- **Batch Job の `MaxWallClockTime`**: Durable Functions host が停止している間のセーフティネットとして設定。ただし起算点が「Job 作成時刻」となるため、ワークフロー全体の監視期限や厳密な実時間上限とは役割が異なる。
- **独立検証ランナーの導入**: `scripts/Invoke-PocValidation.ps1` により、インフラ再デプロイを行わずに各種シナリオ（正常系、分類、Task 失敗、20 並列受付、409 競合、タイムアウト停止）を HTTP 経由で独立検証可能化。

## 調査で判明した技術的制約

- **`GetInstance` → `Schedule` の非原子性**: 既存インスタンスの確認から起動までの処理はアトミックではないため、Orchestrator の重複排除オプション（DedupeStatuses）だけでは同時リクエストの重複受付を厳密には防げない。
- **Durable SDK 1.24.1 における Entity からの起動制限**: Entity 内から Orchestrator を起動する経路では `DedupeStatuses` が転送されない。そのため、Entity の状態更新と Orchestrator 起動アクションが単一の durable batch として永続化される仕組みを利用して排他制御を行っている。
- **Durable Timer と実行中 Activity の関係**: Orchestrator 側で Timer が発火しても、すでに実行中の Activity は物理的にはキャンセルされない。そのため、Job 作成 Activity の実行中に期限を迎えた場合は、遅れて孤立 Job が作成されるのを防ぐため Activity の完了を待ってから後続処理へ進む。
- **一意性保証の前提条件**: 同一 `requestId` の受付保証は「入力 Blob」「Entity 状態」「Main Orchestrator の実行履歴」が保持されていることを前提とする。個別の履歴パージ（Purge）、管理 API からの直接インスタンス起動、旧バージョンの starter との並行稼働時は保証対象外となる。
- **障害注入試験の限界**: 通常の Function App 再起動だけでは、「Entity の checkpoint 直前・直後にプロセスが落ちたケース」の厳密な実証にはならない。

## 次の作業（残タスク）

- 全体計画: `docs/remaining-validation-and-implementation-plan.md`
- 設計根拠: `docs/workflow-reliability.md`
- 検証結果・評価表: `docs/test-results.md`、`docs/feasibility-matrix.md`

1. **検証 VM 経由の実機検証を先行**: 今回の Function 受信閉域化・VM IaC と VM 経由の配置・検証をローカルで検証し、実設定・SSH 公開鍵・権限を用意して Azure で実証する。その後の **全面閉域ネットワーク・IaC 整備**: ExpressRoute / Private DNS、Function / Blob / host Storage（Blob・Queue・Table）、SCM デプロイ経路、Batch 操作・ノード管理の通信設計と Terraform 実装。
2. **オンプレ向け API と認証・認可**: 認証およびユーザー認可を備えた `start` / `status` / `results` / `cancel` API の実装（ブラウザ側に Function key や Durable 管理 URL を直接返さない構成）。
3. **実 C++ バイナリの実行経路**: 小規模なシナリオ 1 件を Blob に保存し、固定バージョンの C++ バイナリを Batch で実行して結果を Blob へ出力する一連の経路実装（入力の version / ETag / size / SHA-256 とアプリの version を記録）。
4. **独立検証ランナーの拡充**: 初版スクリプトは実装・ローカル検証完了。後続フェーズでは Azure 実環境での実行、host 復旧・障害注入、実行履歴観測の fixture 拡充を実施。
5. **Azure 実環境での信頼性検証**: 同時受付、再送、host 復旧、通信障害、監視期限超過、Job 停止失敗、0 台 Pool からの autoscale の実機検証。
6. **キャンセル連鎖と出力管理の完成**: ユーザーからのキャンセル要求（現時点で Batch への停止連鎖は未実装）と結果公開の競合制御、大容量出力の永続化方式、データの保持・クリーンアップ処理の実装。

※ 現時点の構成ファイルはサンプル値のみのため、実装・実機検証の前に Azure 接続情報、実環境パラメータ、実行権限、C++ バイナリ一式（CLI 引数・依存 DLL・期待出力）を確認する必要がある。

## 開発・調査の進め方（ユーザー方針）

- Azure の仕様や SDK の挙動は公式ドキュメントを一次情報として確認し、調査専任のサブエージェントを活用する。
- 独立して進められる実装・検証タスクはサブエージェントで並列化する。
- 設計上の矛盾や技術的に保証できないエッジケースが見つかった場合は、隠さず随時報告する。
