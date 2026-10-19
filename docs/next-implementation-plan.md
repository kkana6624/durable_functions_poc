# 次期実装計画

確認日: 2026-10-19。最新状態: A案（15関数パッケージのデプロイおよび全6シナリオ実機回帰検証・V12利用者キャンセル実証）完了、ならびに **B案（フェーズ 1〜3）完全完遂**: **B案フェーズ1（Entra ID サービス間認証・認可 & 稼働パッケージ同定）**、**B案フェーズ2（Azure Functions Regional VNet Integration & Azure Batch Private Endpoints）**、および **B案フェーズ3（Azure Batch Pool VNet サブネット統合 NoPublicIPAddresses & Azure Batch Account パブリックアクセス完全遮断）** の Azure 実機完全合格（Pass）。実機検証済みデプロイ基準: `.deploy/poc/20261019-131731-6874`（Batch Pool VNet統合 `NoPublicIPAddresses` + Batch Account パブリックアクセス完全遮断 `public_network_access_enabled = false`、閉域検証VM経由のPool準備状態確認Pass、15関数全件登録、閉域検証VM認証・ワークフロー完走・稼働パッケージ同定検証: `artifacts/private-auth-validation/20261019-042651-0796/summary.json`、終了コード 0、Pass、`livePackageVerified = true`）、ならびに全6シナリオ実機回帰: `.deploy/poc/20261019-080215-5263`（コミット `839932b`、閉域検証VM実機回帰: `artifacts/private-validation/20261018-230722-4253/summary.json`、全6シナリオPass）。

## 現在の到達点

コミット `e1d04d6`、`27b24b4`、`b14b25f`、`839932b`、`c14a0cf`、`406d3d5`、`119a088`、および最新デプロイ `.deploy/poc/20261019-131731-6874` により、受付・所有者の一体化、EasyAuth認可アダプター、opt-in EasyAuth IaC、独自API認証runner、**V12 利用者キャンセル（Job Cancellation）の実機実証を含む全6シナリオの実機回帰検証**、**Entra サービス間認証・認可・App Role 不足403遮断・暗号学的稼働パッケージ同定**、**Azure Functions Regional VNet 統合および Azure Batch Private Endpoints の実機検証**、ならびに **Batch Pool の VNet サブネット統合（NoPublicIPAddresses）と Batch パブリックアクセス完全遮断** がすべて合格（終了コード 0）しています。

- **15関数パッケージのAzureデプロイとスモークテスト合格**:
  - `scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -Smoke` を実行（ログ: `.deploy/poc/20261019-080215-5263/`、コミット `839932b`）。
  - 取消確定と成果物公開の排他調停（`resolution.json`）、停止未確認時の取消再送による Batch 停止再試行、停止確認永続化（`termination.json`）の Activity 再試行・状態反映、Job 作成前取消のステータス正規化を含む最新15関数が Function App 上に正常インデックス登録されたことを確認。
  - 3 Tasks並列集約スモークテストが合格（`requestId=deploy-13795081be9948a1b20a2114e447363b`、Sum=3）。
- **閉域検証VMからの実機回帰試験（全6シナリオPass）**:
  - 閉域検証VMから `scripts/Invoke-PrivatePocValidation.ps1` を実行（証跡: `artifacts/private-validation/20261018-230722-4253/summary.json`、終了コード 0）。
  - `single`: Pass（単一Task投入・正常完了・Sum=1）
  - `three`: Pass（3 Tasks並列集約・正常完了・Sum=3）
  - `classification`: Pass（light/heavy/auto軽重分類・正常完了）
  - `failure`: Pass（非ゼロ終了42・Failed確定・マニフェスト非公開）
  - `conflict`: Pass（別入力409 Conflict・同一入力同一所有者冪等性）
  - `cancellation`: Pass（**V12 利用者キャンセル実機完全実証**、`cancel.json` 先行永続化、`CancelRequested` 調停、Batch 有界停止、`manifest.json` 非公開、冪等 200/202 応答）
- **B案フェーズ1: Entra ID サービス間認証・認可 & 稼働パッケージ同定の Azure 実機完全合格（Pass）**:
  - App 登録 `poc-workflow-api`、App Role `Workflow.Execute`、optional claim `idtyp: app`、VM Managed Identity への App Role 付与、Function App の EasyAuth v2（`auth_settings_v2`, `active_directory_v2`）有効化。
  - 閉域検証 VM から `scripts/Invoke-PrivateWorkflowAuthValidation.ps1` を実行（証跡: `artifacts/private-auth-validation/20261019-023554-2683/summary.json`、デプロイ: `.deploy/poc/20261019-112704-9949`、終了コード 0、Pass）。
  - 401 遮断（未認証プローブ 12 件、不正 Audience）、403 遮断（App Role 不足のマネージド ID `poc-unauthorized-id`、所有者・組織欠落／不整合）を確認。正規トークン（`Workflow.Execute` 付与）による 202/200 完走を確認。
  - レスポンスヘッダー `livePackageSha256` / `liveAssemblySha256` とデプロイハッシュの完全一致による暗号学的稼働パッケージ同定（`livePackageVerified: true`）を確認。
- **B案フェーズ2: Azure Functions Regional VNet Integration & Azure Batch Private Endpoints 実機完全合格（Pass）**:
  - Functions Regional VNet Integration: `functions_outbound` サブネット（`10.80.3.0/24`、`Microsoft.Web/serverFarms` 委任、NAT Gateway 関連付け）、`vnet_route_all_enabled = true`。
  - Batch Private Endpoints: `azurerm_private_endpoint.batch_account`（`batchAccount`、`10.80.2.5`）、`azurerm_private_endpoint.batch_node_management`（`nodeManagement`、`10.80.2.6`）、Private DNS Zone `privatelink.batch.azure.com`。
  - 実機検証成果: `.deploy/poc/20261019-120416-8533`（コミット `119a088`）、証跡 `artifacts/private-auth-validation/20261019-031916-1137/summary.json`（終了コード 0、Pass）。
- **B案フェーズ3: Azure Batch Pool VNet サブネット統合 & Batch Account パブリックアクセス完全遮断 実機完全合格（Pass）**:
  - Batch Pool の `batch-nodes` サブネット（`10.80.4.0/24`）配置、`NoPublicIPAddresses` 構成適用（ノードのパブリック IP を完全排除）。ARM 上で `publicIPAddressConfiguration.provision = "NoPublicIPAddresses"`、`allocationState = "Steady"` を確認。
  - Batch Account のパブリックネットワークアクセス完全遮断（`public_network_access_enabled = false`）。ARM 上で `publicNetworkAccess = "Disabled"` を確認。
  - 閉域デプロイツール改修: `batch_private_ip` 検出時に Pool 準備状態確認（`Operation = PoolValidation`）を閉域検証 VM へ委譲し、Private Endpoint 経由での Pool 健全性検証を実現。
  - 実機検証成果: `.deploy/poc/20261019-131731-6874`、証跡 `artifacts/private-auth-validation/20261019-042651-0796/summary.json`（終了コード 0、Pass、`livePackageVerified = true`、パブリック遮断下の Batch Account に対し Private Endpoint 経由で Job 作成・Task 実行・Blob 集約を完走）。**B案完遂**。
- **検証実績**: Release 単体テスト全 294 件（`Poc.Tests` 292 件＋`DtsProbe` 2 件、失敗・スキップ 0 件）、Terraform Mock 37 件、PowerShell オフライン試験 8 組全件が合格。
- **未実施・後続事項**:
  - **C案**: 実 C++ バイナリ連携（`V10`）および Batch `OutputFiles` 大容量出力直送（`T09`）。
  - **V00**: Storage を含む全面閉域化、ExpressRoute / VPN オンプレミス接続。
  - **DTS 環境回帰**: 15 関数コードおよびキャンセル・認証・VNet統合・Batch閉域化設定の DTS 実機回帰。

## 優先順序と完了条件

| 順序 | 実装単位 | 完了条件 |
|---|---|---|
| 1 | V11: 受付・所有者の一体化と旧レコードの扱い | **【ローカル完了・実機回帰Pass】** 単一の不変受付レコード（`admission.json` v2）と旧受付移行・拒否条件をローカル検証。実機回帰試験（conflict シナリオ）にて 409 競合調停・冪等再送の実機動作を確認完了 |
| 2 | V11: 認証境界（B案フェーズ1） | **【Azure 実機完全検証完了（Pass）】** 信頼されたバックエンドの Entra 認証・App Role・`idtyp=app` 検証アダプター、opt-in EasyAuth IaC、VM MI 用認証 runner（`Invoke-WorkflowAuthValidation.ps1`）を実機検証。App Role 不足トークンによる実機 403 拒否試験、所有者不整合 403 拒否試験、正当トークン通過（202/200）、および暗号学的稼働パッケージ同定（`livePackageVerified: true`）を確認（証跡: `artifacts/private-auth-validation/20261019-023554-2683/summary.json`） |
| 4 | V12: 利用者キャンセル | **【Azure 実機検証完了（Pass）】** 要求永続化、冪等 cancel API（Functions 計15件）、Durable 外部イベント、Batch 有界停止、取消と manifest 公開の排他調停、所有者認可、runner シナリオ追加を実装完了。閉域 VM からの実機検証にて 202 受理、cancel.json 存在、Batch completed、manifest 非公開、冪等 200 を確認し完全実証 |
| 3 | V11/V12: 15関数コードのAzure配置・閉域VM実機回帰（A案） | **【Azure 実機検証完了（Pass）】** 15 関数コードの Azure 配置および閉域 VM からの実機全 6 シナリオ回帰検証（証跡: `artifacts/private-validation/20261018-230722-4253/summary.json`、終了コード 0）が合格 |
| B案フェーズ2 | Functions VNet統合 & Batch Private Endpoints | **【Azure 実機完全検証完了（Pass）】** `functions_outbound` サブネット（`10.80.3.0/24`）経由の Regional VNet 統合（`vnet_route_all_enabled = true`）、Batch REST API（`batchAccount` `10.80.2.5`）およびノード管理（`nodeManagement` `10.80.2.6`）Private Endpoints、`privatelink.batch.azure.com` Private DNS Zone、`batch_nodes` サブネット（`10.80.4.0/24`）を整備。閉域検証 VM からのワークフロー起動・Batch 実行・稼働パッケージ同定（`livePackageVerified: true`）の完全合格を確認（証跡: `artifacts/private-auth-validation/20261019-031916-1137/summary.json`、デプロイ: `.deploy/poc/20261019-120416-8533`、コミット `119a088`） |
| B案フェーズ3 | Batch Pool VNet統合（NoPublicIPAddresses）& Batch パブリックアクセス完全遮断 | **【Azure 実機完全検証完了（Pass）】** Batch Pool を `batch-nodes` サブネット（`10.80.4.0/24`）へ統合し `NoPublicIPAddresses` 構成を適用、Batch Account のパブリックアクセスを完全遮断（`public_network_access_enabled = false`）。閉域検証 VM からの Pool 準備状態確認（`Operation = PoolValidation`）、プライベート SCM デプロイ、認証認可・ワークフロー実行・稼働パッケージ同定の全フロー完走を確認（デプロイ: `.deploy/poc/20261019-131731-6874`、証跡: `artifacts/private-auth-validation/20261019-042651-0796/summary.json`、終了コード 0、Pass、`livePackageVerified = true`）。**B案完遂** |
| 次の焦点 | C案（実 C++ バイナリ連携 & OutputFiles 直送）および V00（全面閉域化） | **【次なる作業候補】**<br>・**C案**: 実 C++ バイナリ連携（`V10`）および Batch `OutputFiles` 大容量出力直送（`T09`）の実機検証<br>・**DTS 環境回帰**: 15 関数コードおよびキャンセル・認証・VNet統合・Batch閉域化設定の DTS 実機回帰<br>・**V00**: Storage を含む全面閉域化およびオンプレミス接続実証 |
| 5 | V10/T09: ファイル入出力（C案） | 入力確定、Blob version/ETag/size/SHA-256 とバイナリ版を実行契約へ追加。固定バイナリ、ResourceFiles、OutputFiles 直送を導入し、転送成功とハッシュ照合後だけ manifest 公開 |
| 6 | V00・運用検証 | Storage全面閉域化、DNS・送信経路・SCM 経路、オンプレ接続を段階検証。その後 Spot 回収・一時障害注入・保持/清掃を拡充 |

## 実装済みの境界と次の実装

開始202の本文 `statusQueryGetUri` と `Location` は独自 `/api/workflows/{requestId}` に統一済みで、`Retry-After: 10` を維持しています。内部runnerはこの契約を検証後、system keyから管理URIをメモリ内で生成して詳細診断を継続します。独自状態APIへの全面移行は各Functionの認証設計と併せて進めます。開始専用Function keyを別Functionへ流用しません。

続くローカル実装でHTTP/旧custom statusのエラー遮蔽、明示ログのコード化、状態・結果API共通の公開成果物検証を追加しました。さらにActivity wrapperでは、再試行分類とtimeout型を維持し、固定message・innerなしの例外を外へ渡します。gRPC分類はHTTP/Activityとも `Unavailable` / `DeadlineExceeded` / `ResourceExhausted` に限定しました。これは冪等操作への有界再試行方針であり、回復保証ではありません。SDK層の再試行は別の層で、今回変更していません。

コミット `e1d04d6` で `WorkflowAdmission.cs` の受付と所有者の永続化を単一レコード（Version 2）へまとめ、同一入力・同一所有者の再送、異なる入力・所有者・組織の競合（409 Conflict）、所有者なし旧受付の扱いを実機検証しました。続くコミット `27b24b4` / `b14b25f` / `c14a0cf` / `406d3d5` で Entra ID サービス間認証・EasyAuth v2・App Role 403 遮断・稼働パッケージ暗号学的同定を実 Azure 上で完全実証しました。そしてコミット `119a088`（B案フェーズ2）により、Function App の Regional VNet Integration（`functions_outbound` `10.80.3.0/24`、`vnet_route_all_enabled = true`）および Azure Batch Account の Private Endpoint（`batchAccount` `10.80.2.5`、`nodeManagement` `10.80.2.6`、`privatelink.batch.azure.com` Private DNS Zone）を実機適用し、閉域検証 VM からの Batch Job/Task 完走および稼働パッケージ同定（`livePackageVerified: true`）を完全合格としました。

単一レコード化は、別Blobだった所有者と受付の確認・claimの交差を新Starter間で防ぎます。現PoCでは所有者なし受付を維持し、所有者必須化は後続の認証済みAPI契約で検討します。旧レコードの移行は旧Starter停止後を前提とし、任意利用者による旧ジョブの所有権取得を許しません。

`EntraEasyAuth`モードは認可済みバックエンドのみから必須owner/orgを受け取り、その値を利用者セッションへ正しく結び付ける責任をバックエンドに委ねます。実機環境では App Role `Workflow.Execute` を持つ正規マネージド ID からの要求のみを通過させ、App Role 不足のマネージド ID や未認証・他者コンテキスト要求を確実に 401/403 遮断できる境界を確立しました。後続ステップでは、新設済みの `batch_nodes` サブネット（`10.80.4.0/24`）へ Batch Pool を移行してパブリック IP を排除（`NoPublicIPAddresses`）し、Batch Account のパブリックネットワークアクセスを遮断（`public_network_access_enabled = false`）する B案フェーズ3 を進め、その後 C案（実 C++ バイナリ連携・大容量出力直送）および V00（全面閉域化）へ移行します。

## キャンセル・出力で守る条件

- Durable外部イベントは通知手段であり、取消の完了証跡ではありません。起動前の要求喪失を防ぐため永続化した取消状態を各処理境界で照合し、重複通知を許容します。
- Durableの強制TerminateはActivityや子orchestrationを停止しません。Job作成中に取消された場合も作成結果を確定させてから対象Jobを停止し、遅れて作られる孤立Jobを防ぎます。
- Batch停止完了はJobの `completed` で確認します。未開始Taskは `active` のまま残り得るため、全Taskの `completed` を条件にしません。停止未確認をCancelled確定として返しません。
- 取消確定とmanifest公開の順序を同じ永続制御点で調停し、再試行・クラッシュ後も順序が逆転しない方式を設計してから実装します。
- OutputFilesのアップロード失敗でもTaskはCompletedになり得ます。exit codeだけでなくexecutionInfoのresult/failureInfo、成果物の存在・サイズ・ハッシュを検証します。

## 外部情報と検証の境界

Entraテナント・許可するバックエンド主体/App Role・利用者ID形式、C++のCLI/依存DLL/期待値、入出力サイズと保持期間、PoolのManaged Identity設定可否、オンプレDNS/回線が実機試験の前提です。未確定項目に依存しない実装は先行し、検証用C++スタブ成功と業務バイナリ成功を区別します。

各実装単位で関連単体試験と必要なPowerShell/Terraform試験を実施し、配置変更時は新しいplanを作成します。実機証跡はコミット・ソース/パッケージハッシュと紐づけ、Mock/Fake、Azure、オンプレ接続を分離記録します。V11の取消認可はV12実装後に追加確認します。

## 公式根拠

- [App Serviceの認証済みユーザー情報](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-user-identities): プラットフォームの認証モジュールが注入するIDヘッダーの信頼境界。
- [APIのscope/App Role検証](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-protected-web-api-verification-scope-app-roles): サービス呼出元のroles検証。
- [Durable外部イベント](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-external-events): 非同期配信と重複・不存在instanceの扱い。
- [Durable instance management](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-instance-management): Terminateの非同期性と子/Activity非伝播。
- [Batch Job Terminate](https://learn.microsoft.com/en-us/rest/api/batchservice/jobs/terminate-job?view=rest-batchservice-2025-06-01): Job停止と未開始Taskの状態。
- [Batch OutputFiles](https://learn.microsoft.com/en-us/azure/batch/batch-task-output-files): 直接アップロードと転送失敗の確認。

既存の設計・検証根拠: [全体計画](remaining-validation-and-implementation-plan.md)、[信頼性設計](workflow-reliability.md)、[検証結果](test-results.md)、[プロジェクトメモリ](project-memory.md)。
