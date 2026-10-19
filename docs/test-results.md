# PoC 検証結果

## 現在の到達点（2026-10-19、最新15関数コードの実機回帰全件合格および B案フェーズ1 Entra ID 認証・認可・稼働パッケージ同定の実機完全合格）

最新の実装は、開始202の独自状態URL、HTTP/旧custom statusのエラー遮蔽、manifest・aggregate・全Task結果の共通検証、Activity境界の固定message・innerなしの安全例外化、gRPC再試行対象3コード限定、受付と所有者の単一レコード化（`admission.json` Version 2、同一所有者・入力の再送許容、不一致時の403/409調停、旧受付移行・拒否）、**V12 利用者キャンセル（Job Cancellation）の実装**（`CancelWorkflow` 追加、計15関数、事前Blob永続化 `runs/poc-<requestId>/cancel.json`、Durable外部イベント `CancelRequested`、Batchジョブ有界停止 `TerminateBatchJobActivity`、成果物 `manifest.json` 公開抑止、所有者認可、冪等200/202）、ならびにレビュー指摘に基づく**取消確定と成果物公開の排他調停（`resolution.json`）**、**停止未確認時の取消再送による Batch 停止再試行**、**停止確認永続化（`termination.json`）の Activity 再試行・状態反映**、**Job 作成前取消のステータス正規化**、**Restart モード復旧状態検証（`HostStoppedBatchWallClock`）**を含めた15関数パッケージを実 Azure 環境へ再デプロイし、閉域検証 VM からの全6シナリオ実機回帰（`single`, `three`, `classification`, `failure`, `conflict`, `cancellation`）において**全件合格（終了コード 0、Pass）**を確認しました。さらに、**B案フェーズ1: Microsoft Entra ID によるサービス間認証および認可（App Role不足拒否403・不正Audience拒否401・正規MI成功200）および稼働パッケージ暗号学的同定（`livePackageVerified: true`）の Azure 実機完全合格** を達成しました。

| 対象 | 最新の確定結果 |
|---|---|
| Release単体試験 | Poc.Tests 292/292件、DtsProbe 2/2件、計294件成功（失敗・スキップ0件） |
| Release publish | 成功、`local.settings*` 不在 |
| Terraform Mock | `modes.tftest.hcl` 37/37件全Pass、`backend.tftest.hcl` 1/1件全Pass |
| PowerShellオフライン試験 | 8組全件Pass（Deployment.Common / Deploy.Workflow / Test-Deployment / PocValidation / PrivateVm / RecoveryValidation / AutoscaleValidation / WorkflowAuthValidation） |
| **Azure実環境デプロイ・回帰（最新成果）** | **全件Pass**（`poc`環境・Azure Storageバックエンド）。最新成果: `.deploy/poc/20261019-080215-5263`（コミット `839932b`、パッケージSHA-256: `06bf36f4...`、15関数登録・スモーク合格: `deploy-13795081be9948a1b20a2114e447363b`、全6シナリオ実機回帰Pass、証跡: `artifacts/private-validation/20261018-230722-4253/summary.json`、終了コード0）。 |
| **V12 利用者キャンセル & 排他調停** | **Azure 実機再検証完了（Pass）**。`POST /api/workflows/{requestId}/cancel`、事前Blob永続化（`cancel.json`）、取消確定・成果物公開の単一調停点（`resolution.json`）、Durable外部イベント待機（`CancelRequested`）、Batchジョブ有界停止（`TerminateBatchJobActivity`）、manifest公開抑止、所有者認可（他者403拒否）、停止確認永続化（`termination.json`）と状態反映、冪等200/202を最新Azure実環境で実証（証跡: `artifacts/private-validation/20261018-230722-4253/summary.json`） |
| **B案フェーズ1: Entra ID サービス間認証・認可 & 稼働パッケージ同定** | **Azure 実機完全合格（Pass）**。App 登録 `poc-workflow-api`、App Role `Workflow.Execute`、optional claim `idtyp: app`、VM Managed Identity への App Role 割り当て、Function App への EasyAuth v2 適用、および `X-Poc-Package-Sha256` レスポンスヘッダーによる稼働パッケージ同定下で、閉域検証 VM から実機検証を実行。<br>① 未認証・不正Audience遮断（401）<br>② App Role 不足マネージドIDの遮断（403）<br>③ 所有者不整合遮断（403）<br>④ 正当トークン通過（202/200）<br>⑤ 稼働パッケージ暗号学的同定（`livePackageVerified: true`、実機ヘッダーとアセンブリSHA-256完全一致）を確認（証跡: `artifacts/private-auth-validation/20261019-004913-5659/summary.json`、終了コード0、Pass）。 |
| 受付・所有者一体化 | `admission.json` Version 2で同一レコード化。同一所有者・入力の再送許容、不一致時の403/409調停、旧受付移行・拒否を検証（Azure実機でもPass確認） |
| 例外・再試行・排他調停 | 取消確定と成果物公開の単一調停点（`resolution.json`）、調停レコードの整合性検証、Restart モード復旧状態検証、キャンセル部分書き込み失敗時の余分な Batch 計算抑止、停止未確認時の取消再送による停止再試行、停止確認永続化（`termination.json`）の Activity 再試行・状態反映、Job 作成前取消のステータス正規化を実装・実機検証完了（最新デプロイ `.deploy/poc/20261019-080215-5263` による Azure 実機全6シナリオ回帰合格） |
| Azure（過去の耐障害性検証） | V05（再起動復旧）/ V07（監視期限超過）/ V08（ホスト停止中Batch期限）/ V09（0台オートスケール）は過去の配置でPass実証済み |

全294件のRelease単体テスト、Terraform Mock 38件、PowerShellオフライン試験8組は最新コードで全件Passを確認済みです。成果物検証は照会時点の整合性であり、複数Blobの原子的スナップショットや後続取得成功を保証しません。全ランタイムログ・全履歴の機密非露出、admission20の全世代にわたる厳密な起動一意性も未保証です。なお、B案フェーズ1（Entra ID サービス間認証・認可および稼働パッケージ同定）の実機完全合格に伴い、残る後続課題は Batch 側の閉域化（フェーズ2: Batch Account Private Endpoint、フェーズ3: Batch Pool VNet/NoPublicIP ノード構成）および実 C++ バイナリ連携（C案）となります。

以下の日付付き記録と初版の試験表は歴史的チェックポイントです。件数・未実施項目は当時の状態として読み、現在の判断には本節と [プロジェクトメモリ](project-memory.md) を優先してください。

## 2026-10-19 B案フェーズ1 レビュー指摘（P2/P3）対応および再起動後復旧再確認・同定方式保証範囲整合の Azure 実機完全合格

Azure 実環境（`poc-durable-batch-rg`、Function App: `poc-df40481149-func`、Azure Storage バックエンド）に対し、全体レビューで指摘された 2 件の課題（[P2] 再起動後の復旧確認とデプロイ完了記録、[P3] 同定方式の保証範囲と設計書記述の整合）を反映し、再デプロイおよび閉域検証 VM（`poc-df40481149-validation`）からの完全実機検証を実施しました（デプロイ成果: `.deploy/poc/20261019-112704-9949`、証跡: `artifacts/private-auth-validation/20261019-023554-2683/summary.json`、終了コード 0、Pass）。

| 検証項目 | 実行コマンド / 証跡 | 結果と確認内容 |
|---|---|---|
| 再起動後復旧確認の導入（P2対応） | `scripts/deploy.ps1`, `scripts/PrivateVm.Worker.ps1`, `scripts/Invoke-PrivateVmOperation.ps1` | **完了**。`deploy.ps1` において、プライベート SCM への ZIP 配置完了後、`az functionapp restart` を発行し、非同期再起動の完了待ち・安定化インターバルの後、閉域検証 VM から新設の `FunctionValidation` オペレーションを実行。プライベート DNS 解決（`Assert-PrivateFunctionDns`）、エンドポイント応答疎通確認（`Assert-PrivateFunctionConnectivity` による `/api/workflows` への非 5xx 応答確認）、および 15 関数全件のインデックス登録確認（`Invoke-DeploymentValidation -Phase Functions`）を**再起動後に実施・成功確認した上で `deployment-result.json` に完了記録**を保存するフローへ改修 |
| 同定方式保証範囲と設計書整合（P3対応） | `src/Poc.Functions/WorkflowFunctions.cs`, `docs/workflow-authentication-design.md` | **完了**。`WorkflowFunctions.cs` の各ヘッダー定数・算出メソッドに XML ドキュメントコメントを付与し、発行時・デプロイ時に埋め込まれたマニフェスト指紋（全101ファイルのソート済み SHA-256 マニフェスト `package-sha256.txt`、および `Poc.Functions.dll` の `assembly-sha256.txt`）をプロセス起動時にメモリに保持して返却するものであり、リクエスト毎のディスク全走査・動的再計算ではない旨の保証境界を明文化。`workflow-authentication-design.md` の旧記述（`livePackageVerified=false 固定`）を完全改定し、ラッチング不変保護（途中の不一致・欠落で恒久的に false 化）および前提確認（`unauthorizedClientConfirmedInAllowedList = true`）を明記 |
| Function App 再デプロイ & 再起動後検証 | `scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1`（デプロイ成果: `.deploy/poc/20261019-112704-9949`） | **Pass**。インフラ差分なし（0 add / 0 change / 0 destroy）。Function パッケージ（SHA-256: `54aa7b19...`、アセンブリ: `a0be58b7...`、コンテンツ: `ff5291b2...`）をプライベート SCM に配置後、Function App を非同期再起動。再起動後に閉域検証 VM からエンドポイント疎通および 15 関数全件のインデックス登録を確認してデプロイ完了を記録 |
| 閉域検証 VM からの認証・認可・同定実機検証 | `scripts/Invoke-PrivateWorkflowAuthValidation.ps1 -DeploymentResultPath .deploy/poc/20261019-112704-9949/deployment-result.json -Resource "api://9d1aedba-a99b-4896-b212-77cf313dcfb9" -OwnerId "owner-live" -OrgId "org-live" -UnauthorizedManagedIdentityClientId "fb03274d-66d2-4f2e-856c-6bf17b142334"`（証跡: `artifacts/private-auth-validation/20261019-023554-2683/summary.json`） | **Pass**（終了コード 0）。全19項目のアサーション全件クリア：<br>① **未認証プローブ遮断（401）**: `anonymous`、`keyOnly`、`forgedPrincipal`、`bearerWithoutKey` に対し全3 API で 401 遮断を確認（計12プローブ）。<br>② **Audience 不正プローブ遮断（401）**: ARM 向けトークン（`https://management.azure.com/`）に対し、EasyAuth による 401 遮断を全3 API で確認。<br>③ **App Role 不足プローブ遮断（403）**: EasyAuth 許可リスト確認済み（`unauthorizedClientConfirmedInAllowedList = true`）の条件下で、`Workflow.Execute` を持たない `poc-unauthorized-id` からの要求に対し、Function App（`EntraEasyAuthCallerResolver`）による **HTTP 403 Forbidden 遮断**を全3 API で厳密に確認。<br>④ **所有者伝播認可（403）**: 認可トークン存在下での所有者ヘッダー欠落・他者指定に対し 403 遮断を確認。<br>⑤ **正常系・排他制御（200/202）**: App Role を保持する正規 VM マネージド ID からの 202 受理、同一再送 202、入力競合 409、ステータスポーリング、200 結果取得（全Task整合）を確認。<br>⑥ **稼働パッケージ同定（Pass）**: 実機レスポンスヘッダーの `livePackageSha256`（`ff5291b2...`）とデプロイコンテンツ指紋（`packageContentSha256`）、および `liveAssemblySha256`（`a0be58b7...`）とデプロイアセンブリハッシュ（`assemblySha256`）が完全一致し、`livePackageVerified: true` を確認。 |

### 実機検証の確定境界と後続課題
- **実証された確定境界**:
  - EasyAuth v2 による未認証・不正 Audience のプラットフォームレベル 401 遮断
  - EasyAuth v2 による未承認クライアント・偽造 `X-MS-CLIENT-PRINCIPAL` の遮断
  - `EntraEasyAuthCallerResolver` による App Role（`Workflow.Execute`）不足マネージド ID の HTTP 403 遮断（実機実証）
  - 認可済みバックエンド経由での利用者・組織コンテキストの検証と他者アクセス 403 遮断
  - 正当な App Role を持つマネージド ID からのワークフロー完走（202/200）
  - HTTP レスポンスヘッダーを介した稼働中アセンブリ暗号学的ハッシュの同定（`livePackageVerified: true`）
- **後続フェーズの課題**:
  - **B案フェーズ2**: Azure Batch Account の Private Endpoint（`batchAccount` サブリソース）化と、パブリックネットワークアクセスの無効化（`publicNetworkAccess: Disabled`）
  - **B案フェーズ3**: Azure Batch Pool の VNet サブネット統合およびパブリック IP なし（`NoPublicIPAddresses`）構成、送信 NAT Gateway 整備
  - **C案**: 実 C++ バイナリ・大容量入出力（`ResourceFiles` / `OutputFiles`）連携（`V10` / `T09`）

最新の15関数コード（取消確定と成果物公開の単一調停点 `resolution.json`、停止未確認時の取消再送による Batch 停止再試行、停止確認永続化 `termination.json` の Activity 再試行・状態反映、Job 作成前取消のステータス正規化、Restart モード復旧状態検証 `HostStoppedBatchWallClock`）を実Azure環境（`poc-durable-batch-rg`、Function App: `poc-df40481149-func`、Azure Storageバックエンド）へ再デプロイし、スモークテストおよび閉域検証VMからの全6シナリオ実機回帰試験を実行し、**全件合格（終了コード 0、Pass）** を確認しました。

| 検証項目 | 実行コマンド / 証跡 | 結果と確認内容 |
|---|---|---|
| Azure再デプロイ & スモークテスト | `scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -Smoke`（ログ: `.deploy/poc/20261019-080215-5263/`） | **Pass**（終了コード0）。インフラ計画差分なし（0 add / 1 change / 0 destroy）。Function AppへのZIP配置後、**15関数全件のインデックス登録を確認**（`AggregateActivity`, `CancelWorkflow`, `ClassifyActivity`, `EnsureBatchJobActivity`, `EnsureBatchTaskActivity`, `FetchAndPersistActivity`, `GetBatchTaskStatusActivity`, `GetWorkflowResult`, `GetWorkflowStatus`, `MainOrchestrator`, `MonitorBatchTaskOrchestrator`, `PublishManifestActivity`, `StartWorkflow`, `TerminateBatchJobActivity`, `WorkflowAdmissionEntity`）。スモークテストにて3 Tasks投入・Batch実行・結果回収・`Sum=3`集約完了を確認（`requestId=deploy-13795081be9948a1b20a2114e447363b`、`instanceId=poc-7dcabfe0556442d93128d4355d5e935e72d7c817`、パッケージSHA-256: `06bf36f42ee396c9291e4172a9926021b60b045cb64f11db2d99050fe11ca833`） |
| 閉域検証VM経由の全6シナリオ実機回帰 | `scripts/Invoke-PrivatePocValidation.ps1 -DeploymentResultPath .deploy/poc/20261019-080215-5263/deployment-result.json -Scenarios single,three,classification,failure,conflict,cancellation`（証跡: `artifacts/private-validation/20261018-230722-4253/summary.json`） | **Pass**（終了コード0）。全6シナリオ全件合格：<br>① `single`: 1 Task完走、`Sum=1`、`runtimeStatus=Completed`<br>② `three`: 3 Tasks完走、`Sum=3`、`runtimeStatus=Completed`<br>③ `classification`: `light`, `heavy`, `auto`（`light`自動判定）の3種投入がすべて正常完了（各`Sum=1`）<br>④ `failure`: Task exitCode=42によりOrchestratorが期待通り`runtimeStatus=Failed`・`businessState=Failed`へ遷移、成果物公開抑止<br>⑤ `conflict`: 同一requestId重複投入に対し`conflictHttpStatus=409`返戻を確認<br>⑥ `cancellation`（V12利用者キャンセル・最新調停ロジック実証）: `POST /api/workflows/{requestId}/cancel` に対し `cancelHttpStatus=202` 受理、先行Blob永続化 `runs/poc-<requestId>/cancel.json` 確認、Orchestratorの外部イベント `CancelRequested` 調停、Batch Job `completed` 強制終了、Task `exitCode=-1073741510` 停止、成果物 `manifest.json` 非公開確認、`businessState=Cancelled`・`runtimeStatus=Failed` 確定、完了後キャンセル再投入に対する `idempotentCancelHttpStatus=200` 冪等応答を確認 |
| 実機再検証で実証された改善項目 | `src/Poc.Core/WorkflowOperations.cs`, `src/Poc.Functions/WorkflowFunctions.cs`, `src/Poc.Functions/WorkflowAdmission.cs`, `tools/BatchSmoke/Program.cs` | ① 取消確定と成果物公開の排他調停（`resolution.json`）による二重確定抑止。<br>② 停止未確認時の取消再送による Batch 停止再試行の許容と、停止確認情報（`termination.json`）の Activity 内安全永続化・状態 API への即時反映。<br>③ Job 作成前および公開時取消におけるステータス変換の正規化（停止不要経路の正常終了反映）。<br>④ キャンセル部分書き込み失敗時の余分な Batch 計算進行抑止。<br>⑤ 補助 CLI BatchSmoke の NullReferenceException 解消。 |

## 2026-10-19 B案 Microsoft Entra ID サービス間認証・認可の Azure 実機検証（プロトタイプ到達点）

Azure 実環境（`poc-durable-batch-rg`、Function App: `poc-df40481149-func`、Azure Storage バックエンド）に対し、Microsoft Entra ID によるサービス間認証・認可（EasyAuth v2 + App Role + Managed Identity）のプロトタイプ構成を適用し、閉域検証 VM（`poc-df40481149-validation`）からの実機検証を実施しました（証跡: `artifacts/private-auth-validation/20261018-160545-4455/summary.json`、終了コード 0、Pass）。

| 検証項目 | 実行コマンド / 証跡 | 結果と確認内容 |
|---|---|---|
| Entra テナント設定 & App Role 割当 | `az ad app create`, `az rest .../appRoleAssignedTo` | **完了**。App 登録 `poc-workflow-api`（Client ID: `9d1aedba-a99b-4896-b212-77cf313dcfb9`、Identifier URI: `api://9d1aedba-a99b-4896-b212-77cf313dcfb9`）、Application 専用 App Role `Workflow.Execute`（ID: `81a93380-4dff-4a6e-883c-61bd66328ef9`）、optional claim `idtyp: app`（`accessToken`）を設定。検証 VM のシステム割り当てマネージド ID（Client ID: `2896f1b6-4379-4e55-bcde-8438912bb918`）へ App Role を割り当て |
| Function App への EasyAuth v2 適用 | `scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1`（デプロイ成果: `.deploy/poc/20261019-001001-3480`） | **Pass**。インフラ計画差分（0 add / 1 change / 0 destroy）。Function App の `auth_settings_v2`（`active_directory_v2`、`unauthenticated_action: Return401`、`allowed_audiences: ["api://..."]`、`allowed_applications: ["2896f1b6..."]`）および `WORKFLOW_AUTH_MODE=EntraEasyAuth` を有効化 |
| 閉域検証 VM からの認証・認可実機検証 | `scripts/Invoke-PrivateWorkflowAuthValidation.ps1 -DeploymentResultPath .deploy/poc/20261019-001001-3480/deployment-result.json -Resource "api://9d1aedba-a99b-4896-b212-77cf313dcfb9" -OwnerId "auth-user-01" -OrgId "auth-org-01"`（証跡: `artifacts/private-auth-validation/20261018-160545-4455/summary.json`） | **Pass**（終了コード 0）。<br>① 未認証プローブ遮断: `anonymous`（401）、`keyOnly`（401）、`forgedPrincipal`（401）、`bearerWithoutKey`（401）を 3 種類の独自 API（`StartWorkflow`, `GetWorkflowStatus`, `GetWorkflowResult`）すべてで確認（計 12 プローブ 401 遮断）。<br>② 所有者伝播認可: 認可済みトークン存在下での `X-Poc-Owner-Id` / `X-Poc-Org-Id` 欠落、および別所有者・別組織指定に対し、3 API すべてで 403 遮断を確認。<br>③ 正常系・排他制御: 正当な IMDS トークン（`Workflow.Execute` 付与）＋Function Key＋所有者情報による 202 受理、同一再送 202、入力競合 409、ステータスポーリング（`Accepted` -> `Preparing` -> `Running` -> `Publishing` -> `Succeeded`）、および 200 結果取得（fingerprint / sum / task 完全整合）を確認 |

### 実機検証の到達境界と未検証事項
- **検証された到達点**:
  - プラットフォーム（EasyAuth v2）によるトークンなしアクセスの 401 遮断
  - プラットフォームによる偽装 `X-MS-CLIENT-PRINCIPAL` の 401 遮断（トークン未提示時）
  - アプリケーションによる Function Key 必須化と Bearer トークンの併用確認
  - アプリケーション（`EntraEasyAuthCallerResolver`）による認可済みバックエンド経由の所有者・組織コンテキストの検証（欠落・他者指定時の 403 遮断）
  - App Role を保持する正規 Managed Identity からの正常受付・実行完走・結果整合性
- **実機における未検証事項（限定事項）**:
  1. **App Role 不足トークンの実機拒否試験**: 有効な Entra トークンを取得可能だが `Workflow.Execute` App Role を持たないサービスプリンシパル／マネージド ID からの要求が、実機上で確実に 403 拒否されるかの実機テストは未実施（ローカル単体テスト `WorkflowAuthorizationTests` でのみ検証済み）。
  2. **未許可 Client ID の実機拒否試験**: 有効なテナントトークンだが `WORKFLOW_AUTH_ALLOWED_BACKEND_APP_IDS` に含まれない Client ID による実機 403 拒否は未実施（単体テストのみ）。
  3. **稼働パッケージの暗号学的同定**: 証跡 `summary.provenance.livePackageVerified` は `false`。デプロイ記述子（`deployment-result.json`）の公称来歴のみを記録しており、Azure 上で実際に稼働しているプロセスのバイナリハッシュや Git SHA を直接測定・照合したものではありません。

## 2026-10-18 最新15関数コード（V12 利用者キャンセル）の Azure 実環境デプロイおよび全6シナリオ実機回帰検証（全件合格）

最新の15関数コード（`CancelWorkflow` 追加、計15 Functions、V12 利用者キャンセル、事前Blob永続化 `cancel.json`、Durable外部イベント調停、`admission.json` v2受付、独自状態照会URL契約、エラー遮蔽、公開成果物検証）を実Azure環境（`poc-durable-batch-rg`、Function App: `poc-df40481149-func`、Azure Storageバックエンド）へ配置し、スモークテストおよび閉域検証VMからの全6シナリオ実機回帰試験を実行し、**全件合格（終了コード 0）** を確認しました。

| 検証項目 | 実行コマンド / 証跡 | 結果と確認内容 |
|---|---|---|
| Azureデプロイ & スモークテスト | `scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -Smoke`（ログ: `.deploy/poc/20261018-211336-1471/`） | **Pass**（終了コード0）。インフラ計画差分なし（0 add / 1 change / 0 destroy）。Function AppへのZIP配置後、**15関数全件のインデックス登録を確認**（`AggregateActivity`, `CancelWorkflow`, `ClassifyActivity`, `EnsureBatchJobActivity`, `EnsureBatchTaskActivity`, `FetchAndPersistActivity`, `GetBatchTaskStatusActivity`, `GetWorkflowResult`, `GetWorkflowStatus`, `MainOrchestrator`, `MonitorBatchTaskOrchestrator`, `PublishManifestActivity`, `StartWorkflow`, `TerminateBatchJobActivity`, `WorkflowAdmissionEntity`）。スモークテストにて3 Tasks投入・Batch実行・結果回収・`Sum=3`集約完了を確認（`requestId=deploy-27855cc869764f62abf22e94d3e7c2f1`、`instanceId=poc-b6948f0806f68ab70a04801aab6a57de46d242db`） |
| 閉域検証VM経由の全6シナリオ実機回帰 | `scripts/Invoke-PrivatePocValidation.ps1 -DeploymentResultPath .deploy/poc/20261018-211336-1471/deployment-result.json -Scenarios single,three,classification,failure,conflict,cancellation`（証跡: `artifacts/private-validation/20261018-124022-0449/summary.json`） | **Pass**（終了コード0）。全6シナリオ全件合格：<br>① `single`: 1 Task完走、`Sum=1`、`runtimeStatus=Completed`<br>② `three`: 3 Tasks完走、`Sum=3`、`runtimeStatus=Completed`<br>③ `classification`: `light`, `heavy`, `auto`（`light`自動判定）の3種投入がすべて正常完了（各`Sum=1`）<br>④ `failure`: Task exitCode=42によりOrchestratorが期待通り`runtimeStatus=Failed`・`businessState=Failed`へ遷移、成果物公開抑止<br>⑤ `conflict`: 同一requestId重複投入に対し`conflictHttpStatus=409`返戻を確認<br>⑥ `cancellation`（V12利用者キャンセル実証）: `POST /api/workflows/{requestId}/cancel` に対し `cancelHttpStatus=202` 受理、先行Blob永続化 `runs/poc-<requestId>/cancel.json` 確認、Orchestratorの外部イベント `CancelRequested` 調停、Batch Job `completed` 強制終了、Task `exitCode=-1073741510` 停止、成果物 `manifest.json` 非公開確認、`businessState=Cancelled`・`runtimeStatus=Failed` 確定、完了後キャンセル再投入に対する `idempotentCancelHttpStatus=200` 冪等応答を確認 |
| 実機検証に伴う不具合修正 | `src/Poc.Functions/WorkflowFunctions.cs`, `scripts/Invoke-PocValidation.ps1`, `scripts/Invoke-PrivatePocValidation.ps1`, `scripts/Deployment.Common.ps1` | ① `CancelWorkflow` において HttpRequestData の非シーク可能ストリーム（CanSeek == false）に対する `request.Body.Length` 呼び出しによる 500 エラーを修正（CanSeek ガードおよび JsonException 安全ハンドリング）。<br>② `Invoke-PocValidation.ps1` における `Set-StrictMode -Version Latest` 下での `customStatus.termination` / `terminationErrorCode` の安全なプロパティアクセスへの改修、および CancelWorkflow 専用キー取得フォールバックを追加。<br>③ `Invoke-PrivatePocValidation.ps1` および `Deployment.Common.ps1` における Azure CLI（`az.cmd`）標準インストールパスへのフォールバック・環境変数 PATH 補正対応。 |

## 2026-10-18 前回の14関数コード（新受付スキーマ・独自状態API）の Azure 実環境デプロイおよび回帰検証

前回の14関数コード（全14 Functions、`admission.json` v2受付、独自状態照会URL契約、エラー遮蔽、公開成果物検証）を実Azure環境（`poc-durable-batch-rg`、Function App: `poc-df40481149-func`、Azure Storageバックエンド）へ配置し、スモークテストおよび閉域検証VMからの主要回帰シナリオを実行しました（※今回のV12 CancelWorkflowを含む15関数コードのAzure実機検証は未実施です）。

| 検証項目 | 実行コマンド / 証跡 | 結果と確認内容 |
|---|---|---|
| Azureデプロイ & スモークテスト | `scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -Smoke`（ログ: `.deploy/poc/20261018-171029-4809/`） | **Pass**（終了コード0）。インフラ計画差分なし（0 add / 1 change / 0 destroy）。Function AppへのZIP配置後、**当時の全14 Functionsのインデックス登録を確認**（`AggregateActivity`, `ClassifyActivity`, `EnsureBatchJobActivity`, `EnsureBatchTaskActivity`, `FetchAndPersistActivity`, `GetBatchTaskStatusActivity`, `GetWorkflowResult`, `GetWorkflowStatus`, `MainOrchestrator`, `MonitorBatchTaskOrchestrator`, `PublishManifestActivity`, `StartWorkflow`, `TerminateBatchJobActivity`, `WorkflowAdmissionEntity`）。スモークテストにて3 Tasks投入・Batch実行・結果回収・`Sum=3`集約完了を確認（`requestId=deploy-47611f6d13994d43837aaf8b92b38e5d`） |
| 検証ランナーの新受付スキーマ追従 | `scripts/Invoke-PocValidation.ps1`, `scripts/tests/PocValidation.Tests.ps1` | `runs/poc-<requestId>/admission.json`（Version 2）を優先読取し、旧 `request.json`（Version 1）へフォールバックする後方互換対応を実施。オフライン回帰試験 `PocValidation.Tests.ps1` Pass |
| 閉域検証VM経由の主要5シナリオ回帰 | `scripts/Invoke-PrivatePocValidation.ps1 -DeploymentResultPath .deploy/poc/20261018-171029-4809/deployment-result.json -Scenarios single,three,classification,failure,conflict`（証跡: `artifacts/private-validation/20261018-083838-2879/summary.json`） | **Pass**（終了コード0）。5シナリオ全件合格：<br>① `single`: 1 Task完走、`Sum=1`、`runtimeStatus=Completed`<br>② `three`: 3 Tasks完走、`Sum=3`、`runtimeStatus=Completed`<br>③ `classification`: `light`, `heavy`, `auto`（`light`自動判定）の3種投入がすべて正常完了<br>④ `failure`: Task exitCode=42によりOrchestratorが期待通り`runtimeStatus=Failed`・`businessState=Failed`へ遷移<br>⑤ `conflict`: 同一requestId重複投入に対し`conflictHttpStatus=409`返戻を確認<br>※ すべてのシナリオで独自状態URL契約（開始202で独自URL返戻・Durable管理URL遮蔽）および機密非漏洩を確認 |


## 2026-10-18 カテゴリ A（追加の耐障害性・運用系検証：`V05` / `V07` / `V08` / `V09`）の Azure 実接続検証

検証 VM カスタムロールへの `Microsoft.Web/sites/host/listkeys/action` 権限反映後、`dts-poc` 環境（Durable Task Scheduler バックエンド）および `poc` 環境（Azure Storage バックエンド）にて、Function App 再起動復旧検証（`V05`）、監視期限超過 `timeout` シナリオ（`V07`）、Function ホスト停止中の Batch `MaxWallClockTime` 安全網検証（`V08`）、0 台オートスケール Pool への先行投入検証（`V09`）、および `tools/DtsProbe --history true` の実機確認を実施し、すべて Pass しました。

| 対象 | 実環境・ローカルの結果 |
|---|---|
| カスタムロール更新 | `dts-poc` / `poc` 両環境の `azurerm_role_definition.validation_function` を targeted `terraform apply` で更新（各 0 add / 1 change / 0 destroy）。実機ロール定義に `Microsoft.Web/sites/host/listkeys/action` が追加されたことを確認 |
| スクリプト・IaC 拡充と回帰試験 | ①ゲストバンドル内での `Invoke-PrivateVmOperation.ps1` 条件付き読込、②`PrivateVm.Worker.ps1` の `$recoveryPhase` 変数名衝突解消、③`Get-PocWorkflowSettings` の ARM `POST .../config/appsettings/list` 直接呼出し化、④`Invoke-PocRecoveryValidation.ps1` への `-ValidationMode HostStoppedBatchWallClock` 追加、⑤Terraform `batch_scale_mode`（`fixed` / `autoscale`）追加（main Mock 30 件 Pass）および `Invoke-PocAutoscaleValidation.ps1` 実装を実施し、全オフライン回帰試験 Pass |
| Function App 再起動復旧（実機 `V05`：DTS / Azure Storage 両バックエンド） | **Pass**（`ExitCode=0`、証跡 `artifacts/reliability/dts-recovery/` および `artifacts/reliability/storage-recovery/`）。1 Task（300 秒遅延）の Batch `running` かつ Durable `Running` を VM で確認後、操作者権限で Function App を `restart`（`finally` で `start` 復元）。再 POST なしで同一 `instanceId`・同一作成時刻・同一 Task（`task-00`）が `Completed` / `Succeeded` となり、`aggregate.json` / `manifest.json` / `tasks/task-00/result.json` の整合を確認 |
| 監視期限超過 `timeout`（実機 `V07`：DTS / Azure Storage 両バックエンド） | **Pass**（`ExitCode=0`、証跡 `artifacts/reliability/dts-timeout/` および `artifacts/reliability/storage-timeout/`）。監視期限（15 分）超過後に `runtimeStatus=Failed`・`businessState=TimedOut`、Batch Job の `batchMaxWallClockSeconds=900` 一致、Batch Job `completed`（Task 強制終了）、成功 `manifest.json` 非公開を確認 |
| ホスト停止中の Batch `MaxWallClockTime` 安全網（実機 `V08`） | **Pass**（`ExitCode=0`、証跡 `artifacts/reliability/dts-host-stopped-wallclock/`）。Batch Task 開始確認後に Function App を `az functionapp stop` で停止（`Stopped` 確認）し、Function 停止中のまま Batch 側の `MaxWallClockTime`（900 秒）期限到達により Job が `completed`（`batchTerminationReason='MaxWallClockTimeExpiry'`, `manifestPublished=false`）へ自動終了すること、および `finally` で Function App が `Running` に復元されることを確認 |
| 0 台オートスケール Pool 先行投入（実機 `V09`） | **Pass**（`ExitCode=0`、証跡 `artifacts/reliability/autoscale-v09/`）。専用 Pool（`batch_mode='new'`）を 0 台 `steady` へ縮小し `$PendingTasks` Autoscale 式（`PT5M`）を有効化、0 台待機確認（`zeroNodesVerifiedBeforeSubmission=true`）後に `single` シナリオを投入して Spot 1 台への自動スケールアウト（`scaledUpObserved=true`）と Task 完走（`Sum=1`）、および `finally` での固定 1 台構成復元（`restoredFixedScaleSucceeded=true`, `restoredPoolReadyVerified=true`）を確認 |
| `DtsProbe --history true`（実機） | **Pass**（証跡 `artifacts/dts-validation/live-instance-history.json`）。スモーク・標準シナリオ・`recovery`・`timeout` の全インスタンスでペイロードを出力せず `historyObservation`（`scope=ReturnedInstanceHistoryOnly`, `allGenerationsObserved=false`, `executionStartedCount=1`）を確認 |

## 2026-10-17 信頼性の追加実装（ローカル検証）

再起動復旧ランナー、DTS の返却履歴の集計、timeout 時の実 Batch 制約照合を追加した時点のローカル検証記録です（実機検証は上記 2026-10-18 節で完了）。

| 対象 | 結果と範囲 |
|---|---|
| .NET | solution test 93件 Pass（既存91件と新しい履歴集計2件）。payload 秘匿、開始イベントが複数でも全世代観測を主張しないことを確認 |
| Terraform | main Mock27件、fmt / validate Pass。VM の host key 読取は Function スコープ、restart / start / wildcard 権限を付与しないことを確認 |
| timeout runner | Fake 試験 Pass。制約欠落・不一致・不正値を Fail とし、正常時の制約秒数と限定した停止理由を記録。秘密を含む不正値は記録しない |
| VM transport | Fake 試験 Pass。RecoveryStart / RecoveryComplete のバンドル・summary 回収、Context の不明フィールドを Azure 呼出し前に拒否 |
| 再起動復旧 runner | Fake 試験 Pass。権限不足で投入前に停止、再 POST なしの完了照会、Task 再作成拒否、古い観測で restart 拒否、restart 失敗時の start、時刻の精度保持、別プロセスからの CLI 引数保持、秘密非記録を確認 |

新 runner の手順・権限反映の前提・証明範囲は [信頼性の追加検証](reliability-validation.md) を参照してください。通常の ARM restart 後の継続確認を、Entity checkpoint 境界の強制 crash や Activity の単一実行保証とは扱いません。DTS の全世代観測制約は残り、admission20 は NotRun のままです。

## 2026-10-17 DTS の Azure 配置・実接続検証

DTS 用の別環境を Japan East の専用リソースグループに配置し、実 Azure 上で動作を確認しました。既定は Azure Storage のままで、既存 Azure Storage Function の設定・コードは変更していません。既存 B1 Plan と Batch pool は共有するため、CPU・メモリと処理容量は共有します。emulator は未実行です。以降の RHEL VM の節は Azure Storage バックエンドでの過去の履歴です。

| 対象 | 今回の結果 |
|---|---|
| Terraform | main Mock27件 Pass、fmt / validate Pass。既定 DTS リソース0件、DTS 選択時 Consumption / hub / 30日保持 / MI権限 / provider設定、不正 backend 拒否、既存 Linux Plan の別 RG 参照・既定 managed Plan 互換・Windows / 他リージョン / 不正 ID 拒否、任意の hub Reader を確認 |
| PowerShell | Common / Deploy.Workflow / PocValidation / Test-Deployment / PrivateVm の5組 Pass。旧 identity 互換、backend / hub 切り替え拒否、DTS 専用RP確認、summary 構成値と不正値拒否を含む |
| .NET | build、単体91件（失敗0・skip0）、Release publish が成功 |
| 配置パッケージ | AzureManaged host 拡張 DLL と startup 登録あり。既定 provider=azureStorage、local.settings の出力なし、ZIP 内のパスが POSIX 形式であることを確認 |
| Azure 配置 | Scheduler / hub 等4件と残り32件を新規作成、変更・削除0件。Consumption、専用 Task Hub、保持30日、Function MI Contributor、観測者 Reader。Private SCM 配置・12 Functions 登録成功 |
| DTS smoke | RHEL VM から非公開 Function を呼び出し、Entity 経由の受付、Windows Batch Spot の3 Task、結果 Blob / manifest 回収成功。TaskCount=3、Sum=3 |
| DTS 直接照会 | 読み取り専用 SDK で当該 hub を直接照会し、MainOrchestrator / Completed を確認。入力・出力や管理 URL は取得・記録しない |
| DTS 標準シナリオ | single / three / classification / failure / conflict の5件 Pass。admission20 は AdmissionObservedButExecutionHistoryUnavailable による NotRun、終了コード2。全世代の起動一意性は未実証 |

配置結果は `.deploy/<deployment-run-id>/deployment-result.json`、直接照会は `artifacts/dts-validation/live-instance.json`、標準シナリオは `artifacts/dts-validation/azure-scenarios/`（完了 `2026-10-17T14:06:03Z`）です。VM 証跡は exit_code=2、error_code / failure_stage=null、summary_available=true を確認しました。すべて Git 管理外です。直接照会した instance は `<instance-id-redacted>` です。host 復旧、期限停止、Spot 回収復旧、実 C++、全面閉域化、保持期限後の挙動は未実証です。DTS 用サンプル・配置手順・移行不可と保持上限は [DTS PoC](durable-task-scheduler-poc.md) を参照してください。


## 2026-10-17 RHEL 9 VM・Private Function・3 Task の Azure 実証

Private SCM への配置、当時の12 Functions の登録、VNet 内の VM からの HTTP 受付、Windows Batch Spot ノードでの3 Task、結果 Blob の回収・集約が実 Azure 上で成功しました。この節は過去のチェックポイントであり、現在の到達点はページ上部を参照してください。

| 対象 | 実環境の結果 |
|---|---|
| インフラ | RHEL 設定の残り apply 成功（12件追加・変更削除0）。Batch Spot コア枠2、Spot1台・専用0台、データプレーンでノード ready を確認 |
| 検証 VM | 通常 `Standard_F1as_v7` + NVMe、RHEL 9.8（image `9.8.2026080415`）。cloud-init `done`、CLI 2.91.0、PowerShell 7.6.7、ready marker を Run Command で確認 |
| Function の受信 | `publicNetworkAccess=Disabled`、Private Endpoint 接続 `Approved` を確認。外部からの HTTP 拒否試験とは区別 |
| Private SCM / Functions | ZIP 配置成功、12 Functions の登録を確認 |
| 3 Task smoke | `exit_code=0`、`Ready=true`、`Smoke=Pass`、`TaskCount=3`、`Sum=3`。HTTP 受付から結果・Manifest の整合性検証まで成功 |
| 標準6シナリオ | HTTP 202 対応修正後、single / three / classification / failure / conflict の5件 Pass、admission20 は `AdmissionObservedButExecutionHistoryUnavailable` による NotRun。summary 終了コード2で完了 |

成功証跡は `artifacts/deployment-recovery/rhel-private-deploy/vm-operation.json`（完了 `2026-10-17T13:06:18Z`）、配置結果は `artifacts/deployment-recovery/deployment-result.json` です。独立検証の初回失敗証跡は `artifacts/deployment-recovery/rhel-validation/` に保持し、修正版の再検証先は `artifacts/deployment-recovery/rhel-validation-202/` です。いずれも Git 管理外です。Function キー・トークン・Durable 管理 URL・生の例外本文は記録しません。

実機で確認した不具合と修正は次のとおりです。

- Windows PowerShell / .NET Framework の ZIP にバックスラッシュを含む37エントリが入り、Linux host で `.azurefunctions` を認識できませんでした。相対 POSIX パスで ZIP を作成するよう修正し、実89エントリでバックスラッシュ0・`.azurefunctions/` 配下36件を確認して再配置しました。新 SDK は DLL に metadata provider を生成するため、`functions.metadata` 不在自体は不備ではありません。
- `az functionapp show` の補助的な構成・公開プロファイル取得が最小権限で失敗したため、SCM ホスト名の取得を `sites/read` の ARM GET へ変更しました。権限は追加していません。
- CLI の転送進捗が JSON へ混入する問題は、Blob 転送すべての `--no-progress` 指定で修正しました。
- Run Command の集約応答を stdout と認識せず、成功したゲスト結果を誤失敗にしていました。個別 stdout と `[stdout]` / `[stderr]` 集約形式の両方を扱い、stderr の成功文字列では誤成功にならないよう修正しました。
- 独立ランナーは Durable 状態 API の進行中 HTTP 202 を不正扱いしていました。HTTP 200 / 202 の双方を受理し、既知の `runtimeStatus` を必須として、202 の観測回数も証跡へ記録します。オフライン試験で 202 Running → 200 Completed の成功、202 の不明・欠落ステータスの即時拒否を確認しました。修正後の Azure 再検証は `2026-10-17T13:14:57Z` に5件 Pass・1件 NotRun で完了しました。admission20 の NotRun は全世代の起動履歴を取得できないためであり、厳密な起動一意性の実証ではありません。
- summary の終了コード2は Worker の子 `PowerShell -Command` で1へ変換されていたため、`exit $LASTEXITCODE` を明示しました。実際の Worker コマンドを子プロセスで実行する回帰試験は Pass です。修正後の Azure admission20 単独実行は `2026-10-17T13:17:59Z` に完了し、`artifacts/deployment-recovery/rhel-validation-exitcode/` で summary 終了コード2、RHEL guest の `vm-operation.json` の `exit_code=2`・`error_code=null`・`failure_stage=null`・`summary_available=true` を確認しました。wrapper スクリプトも2を返します。自動化で `powershell -Command` から呼ぶ場合は、呼び出し元の末尾にも `exit $LASTEXITCODE` を指定します。省略時に外側シェルが1を返すことは PowerShell の挙動であり、検証失敗ではありません。標準6シナリオの最終結果は5件 Pass・admission20 NotRun です。

標準6シナリオの来歴は Git revision `a8a84344315e553752c2bfc970cd3c33697553b3`・dirty=true、source SHA-256 `f5b62dc38172c2b8451d13d393d1b33d9480bae46a1ce60cc9c0d16b84c9fe2a`、package SHA-256 `92753cc6c3f407bc879370ca2fab24d295f8ccdaaa461b5506cf99141e9fbef7` です。これらは実行時に渡した来歴情報であり、Azure host のロード済みバイナリの直接照合値ではありません。

ローカルでは Terraform main Mock18件・fmt・validate が成功しました。修正対象の PowerShell 試験4組（Deployment.Common / Test-Deployment / PocValidation / PrivateVm）も成功し、raw ZIP エントリ名と内容、進捗抑止、ARM GET、固定値だけの失敗診断、Run Command の成功・失敗判定を確認しました。.NET91件・Release publish の過去の成功記録とは区別します。期限停止、host 復旧、Spot 回収復旧、実 C++ 連携、Storage / Batch の全面閉域化は未実証です。


## 2026-10-17 Batch Spot 対応（ローカル更新）

ユーザーから Spot コア増枠申請済みとの報告を受け、承認待ちの間にローカル資材を更新しています。承認は未確認で、今回 Azure の再照会・変更は行いません。新規 Batch のノード種別を `dedicated` / `spot` から選択でき、省略時は従来互換の `dedicated`、PoC サンプルと実設定は Spot 1台・専用0台です。Linux 検証 VM は通常 VM のままです。

| 対象 | 今回の結果 |
|---|---|
| Terraform Mock | main 16件 + bootstrap 1件、計17件 Pass。既定の専用ノード互換性、Spot 台数・output、不正な種別の拒否、既存 Pool 参照維持を確認 |
| PowerShell オフライン試験 | 5組 Pass（Deployment.Common / Test-Deployment / Deploy.Workflow / PocValidation / PrivateVm）。種別の引き継ぎ、準備完了ノードの種別照合、回収済み・利用不能・無効ノードの除外、空の Spot Pool の待機期限、設定種別の要約記録を Fake で確認 |
| 構文・書式 | PowerShell parse、Terraform fmt / validate が Pass |
| .NET 単体テスト / Release publish | 今回 C# 変更なしのため再実行なし。前回の91件 Pass・publish 成功記録を維持 |

証跡は `artifacts/spot-validation/` に保存します（Git 管理外）。ランナーは `summary.settings.batchNodeType` に設定上の種別を記録します。Azure ノードの実測値ではありません。Spot 回収・復旧はシナリオ対象外（`SpotEvictionRecovery`）とし、Azure での Spot ノード確保、回収時の Task 再キュー、結果回収は未検証です。ローカル結果は Activity による取得前にノードを失うと回収できない場合があり、Task から Blob への直接永続化は今回実装していません。

配置再開には、対象 Batch account の Spot コア（API 名 `lowPriorityCoreQuota`）が最低2であることの Azure 上での確認と、同じ設定・state による再 plan が必要です。以下のクォータ値と35リソースの plan は過去の確認記録で、現在値や今回の Spot plan の証跡ではありません。古い保存 plan は適用しません。

## 2026-10-17 実設定での事前確認

**当時の実環境の停止条件**: state 基盤4リソースと専用 PoC RG / Batch account の2リソースを作成しました。Batch account の実値は `dedicatedCoreQuota=0`、`lowPriorityCoreQuota=0`、`poolQuota=20` でした。当時予定していた `Standard_D2s_v5` 専用1台には専用 core quota が最低2必要だったため、Pool / VM / Function / NAT は未作成です。現在は上記 Spot 方針へ変更しています。Azure ワークフロー試験は未実施のままです。

main の plan は新規35リソースで削除・置換なしでした。クォータ確認では同じ remote state に対し、対象型が RG / Batch account のみであることを検査してから target apply しました。state が更新されたため、この時点より前の保存 full plan は再利用しません。再開時は `deploy.ps1` により再 plan します。証跡は Git 管理外の `.deploy/poc/` 配下です。

Azure CLI 2.91.0 のログイン、Owner 権限、必要 Provider の登録、既存 Windows Gallery image の metadata と Japan East replica、検証 VM 用 vCPU 枠を Azure 管理プレーンで確認しました。実設定は Git 管理外の `environments/poc.psd1` を使用します。

実設定を用いた build、単体テスト91件（失敗0・skip0）、Release publish が成功しました。初回の `-PlanOnly` は state backend 未作成のため bootstrap plan のみを保存し、apply・コード配置を実行しませんでした。初回確認で判明した CLI PATH の引き継ぎ不備を修正し、オフライン回帰試験と再実行で確認しました。

これは管理プレーンの事前確認であり、Function の起動・Private DNS / SCM・VM Identity・Batch Task 実行の合格判定ではありません。

## 2026-10-17 Function 受信閉域化・検証 VM（今回）

VNet 内の検証 VM、NAT、Function Private Endpoint / Private DNS、公開ネットワークアクセス無効化、および VM Run Command 経由の配置・独立検証を追加する作業です。今回のローカル試験では以下を確認しました。いずれも Azure 実環境での検証ではありません。

| 対象 | 今回の結果 |
|---|---|
| PowerShell オフライン試験 | 5 組 Pass（Deployment.Common / Test-Deployment / Deploy.Workflow / PocValidation / PrivateVm）。VM 転送・実行の制御、証跡回収、エラー時の抑止、配置経路変更を Fake で検証 |
| Terraform Mock | main 13 件 + bootstrap 1 件、計14件 Pass。Function 公開アクセス無効化、Private Endpoint / DNS、VM Public IP なし、NAT と権限、任意 operator ID の未指定・明示指定・不正 GUID を含む |
| スクリプト構文 | PowerShell parse とゲスト実行 shell の Bash -n が Pass |
| .NET 単体テスト | 2026-10-17 の VM 実装前チェックポイント（HEAD `a8a8434`）で91件 Pass を再確認。今回 C# 変更なしのため再実行なし |
| Release publish | 既存の成功記録を維持。今回は再実行なし |

今回のローカル証跡は `artifacts/private-vm-validation/` に保存しています（Git 管理外）。

以下の PowerShell 4 組・Terraform 8 件は以前のベースラインの記録です。

Azure 実環境での cloud-init、Private DNS / HTTPS、SCM ZIP 配置、VM Identity の権限反映、公開アクセス拒否、Job 投入・結果回収は **未検証** です。実環境の設定・SSH 公開鍵・権限が必要です。

## 2026-10-17 独立検証ランナーの追加と単体テスト拡充（既存ベースライン）

`Invoke-PocValidation.ps1` を追加し、デプロイ済み環境に対する検証処理をデプロイ実行スクリプトから分離しました。あわせて HTTP レイヤー（`StartWorkflow`）および回復性・受付制御の単体テストを大幅に拡充し、.NET 単体テストは **全 7 テストクラス・91 件すべてが合格（91 Pass / 0 Fail / 0 Skip）** しています。なお、Azure 実環境への実接続試験は引き続き未実施です。
各処理の順序、生成される成果物、およびシナリオ別の詳細な判定基準については [検証ランナーの処理フロー](validation-runner-flow.md) を参照してください。

| 対象 | ローカル結果 |
|---|---|
| ランナー オフライン試験 (`PocValidation.Tests.ps1`) | Pass。正常系・分類・意図的な exit 42・409 競合・20 並列受付の判定、404 待機上限、401/403 即時失敗、結果改ざん検知、停止未確認のハンドリング、シークレット除外、途中失敗時の要約出力、終了コード（0/1/2）、空の試験選択の拒否を検証 |
| HTTP 並行送信検証 | Pass。ローカル TCP サーバーを用い、最初のレスポンス返却前に 20 接続が並行確立されることを確認（ローカルでの並行受付確認であり、Azure 実環境での同時受付成功を保証するものではありません） |
| fingerprint 互換性 | Pass。実 C# 実装の `WorkflowIdentity.Fingerprint` 固定値 2 件（auto/light、heavy/非ゼロ終了）と PowerShell 側の算出値が完全に一致 |
| .NET 単体テスト / Release publish | **91 Pass / 0 Fail / 0 Skip（全 7 テストクラス合格）**、Release publish 成功 |
| PowerShell スクリプト試験 | **4 組全 Pass**（Deployment.Common, Test-Deployment, Deploy.Workflow, PocValidation） |
| Terraform Mock 試験 | **8 件全 Pass**（main 7 件、bootstrap 1 件） |

試験の証跡ログは `artifacts/runner-validation/powershell-runner-tests.log`、`publish.log`、`regression-summary.txt` に記録されています。設定ファイルが存在しない場合の CLI 挙動試験においても、意図どおり終了コード 1 とシークレット情報を含まない結果要約が出力されることを確認しました。また、Git revision に加え、Git 追跡対象ファイルおよび .gitignore 対象外の未追跡ファイルの内容ハッシュから算出する `sourceSha256` により、未コミットの変更状態を一意に識別します。なお、デプロイパッケージのハッシュ値（package hash）はデプロイ結果ファイルに記録された値であり、Azure 上の実 host が読み込んだバイナリそのものを直接照合した値ではありません。

`admission20` シナリオでは、同一インスタンスへの集約、期待される Task 数、および完了後の再送動作を観測します。しかし、現在の Azure Functions / Durable 管理 API のみでは全世代にわたる Main オーケストレーションの起動履歴を完全には証明できないため、検証ランナーは観測値を記録したうえで `NotRun / AdmissionObservedButExecutionHistoryUnavailable` を返します。ローカル Fake 環境で履歴を擬似的に与えたテストの合格をもって、Azure 実環境における「1 度限りの起動保証」が実証されたとはみなしていません。

上記はネットワーク実装前のローカル検証記録です。現在の個人検証方針は、VNet 内の VM から Function Private Endpoint へ接続する方式へ変更しました。[接続設計](network-connectivity-design.md) を参照してください。Azure 上でのリソース作成・到達性・認証・配置の検証は未実施です。

> [!NOTE]
> 当時はAzure実環境への接続値・資格情報が未提供のため、この時点の検証はすべてローカル環境（単体テスト・Fake/Mock・オフライン試験）で実施しました。後続のAzure成功記録と最新コードの未検証範囲はページ上部を参照してください。

## .NET 単体テストの構成と検証項目（全 270 件・10 テストクラス）

信頼性改善および HTTP レイヤー／Gateway 検証の追加、独自API契約・エラー遮蔽・公開成果物検証・Activity安全例外化・gRPC再試行限定、受付と所有者の単一レコード化（`admission.json` Version 2、277行拡充）、opt-in EasyAuth 認可アダプター（`WorkflowAuthorizationTests` 40件追加）に加え、V12 利用者キャンセル（Job Cancellation、`CancelWorkflow` 追加、計15関数）を経て、.NET 単体テストは **全 270 件（`Poc.Tests` 268 件 + `DtsProbe` 2 件、10 テストクラス）すべて合格（270 Pass / 0 Fail / 0 Skip）** となっています。

| テストクラス | テスト数 | 主な検証内容 |
|---|---|---|
| `IdentityAndCommandTests` | 7 | リクエスト ID / Job ID / Task ID の決定論的導出、PowerShell コマンドラインのエスケープと生成 |
| `OrchestrationTests` | 5 | 親子オーケストレーションの連携、Timer による待機、兄弟 Task 完了待ち、全件成功時のみ Manifest 公開、V12 利用者キャンセル検知時の Batch Job 有界停止・Manifest 公開抑止（+2件） |
| `RequestTests` | 12 | 入力パラメータのバリデーション（TaskCount 1〜3、classification、パストラバーサルや不正文字列の拒否） |
| `ResilienceTests` | 56 | 一時障害（HTTP 408/429/503・gRPC 3コード限定）の Durable 再試行、監視期限超過時の Batch Job 停止（Terminate）照合・完了確認、Manifest 未公開保証、Blob の条件付き作成（If-None-Match）、Task 競合処理、ファイル欠損検知 |
| `WorkflowAdmissionTests` | 15 | 受付と所有者の単一レコード化（`admission.json` Version 2、277行拡充）、同一所有者・入力の再送許容、異なる入力パラメータ時の 409 Conflict 競合検出、異なる所有者・組織時の 403 拒否、旧受付レコード（Version 1）からの安全な移行と旧匿名受付への所有者追加拒否、Entity 起動直列化 |
| `WorkflowAuthorizationTests` | 40 | `EntraEasyAuth` モードでの呼出元認可検証。x-ms-client-principal 解析、tenant ID / issuer / audience 検証、許可 backend client ID 照合、App Role 検証、`idtyp=app` 必須検証、scope 存在時の拒否、必須 owner / org ヘッダー検証、認可失敗時の 401 / 403 応答と機密非露出 |
| `WorkflowHttpTests` | 88 | HTTP トリガー API の挙動検証（独自 status URL・Location・Retry-After 返却、不正 JSON や不正パラメータへの 400 Bad Request、RPC 一時障害時の 503 + Retry-After 応答、エラー遮蔽・内部例外秘匿、認証モードに応じた認可ディスパッチ、新規 `CancelWorkflow` API の受付 202・事前 Blob 記録・完了後 200 冪等性・他者認可拒否 403 検証［+7件］） |
| `WorkflowOperationsTests` | 8 | 3 Task 正常実行と集約（sum=3）、分類と待機時間の保持、Task 失敗時の成果物非公開、Activity リプレイ時の既存リソース再利用、集約破損時の Manifest 公開抑止 |
| `WorkflowPublishedResultTests` | 37 | 公開成果物（`manifest.json`, `aggregate.json`, `tasks/*/result.json`）の整合性検証。fingerprint、件数、合計値、Task分類・成果物パスの一致照合、成果物欠損時の 409 / resultAvailable=false、不整合時の固定 500、一時障害時の 503 |
| `DtsProbeTests.HistoryObservationTests`（`DtsProbe.Tests`） | 2 | DTS SDK 直接照会における返却履歴イベント件数集計、ペイロード非表示、全世代観測を主張しない制約の検証 |
| **合計** | **270** | **270 Pass / 0 Fail / 0 Skip（`Poc.Tests` 268 件 + `DtsProbe` 2 件）** |

### 単体テスト（Fake / Mock）での検証カテゴリ

- **利用者キャンセル制御（Job Cancellation & Boundary Arbitration: V12）**
  - `POST /api/workflows/{requestId}/cancel` によるキャンセル受付（実行中 202 Accepted / 完了・終了済み 200 OK 冪等応答）
  - 受付時所有者との一致検証（他者・別組織からのキャンセル要求に対する HTTP 403 Forbidden 拒否、認証ヘッダー不正時の 400 拒否）
  - Storage 上へのキャンセルレコード先行永続化（`runs/poc-<requestId>/cancel.json`、条件付き作成）
  - オーケストレータの外部イベント待機（`CancelRequested`）と兄弟 Task 監視オーケストレーション待機の調停
  - キャンセル検知時の Batch ジョブ有界停止（`TerminateBatchJobActivity`、最大2分ポーリング）の安全呼び出し
  - キャンセル時の成果物（`manifest.json`）公開の厳格な抑止とカスタムステータス（`state = "Cancelled"`, `errorCode = "workflow_cancelled"`）の確定
- **受付・所有者の一体化（Atomic Admission & Ownership）**
  - `admission.json` Version 2 による入力・instanceId・所有者・組織の単一レコード化
  - 同一所有者・同一入力での再送受け入れと既存インスタンス返却
  - 所有者不一致・組織不一致時の HTTP 403 Forbidden 拒否
  - 所有者一致かつ入力不一致時の HTTP 409 Conflict 競合検出
  - 旧受付レコード（Version 1）からの安全な移行と旧匿名受付への所有者追加拒否
  - Durable Entity による起動直列化と重複起動抑止
- **呼出元認可・認証境界（Caller Authorization & Authentication Boundary）**
  - `EntraEasyAuth` モードにおける `x-ms-client-principal`（JSON / Base64）解析
  - テナント ID、issuer（`https://login.microsoftonline.com/<tenant-id>/v2.0`）、audience（API client ID）の厳密検証
  - 許可されたバックエンド Managed Identity client ID のみの allowlist 照合
  - Application 限定 App Role 検証（ユーザー/委託ロールではなくアプリケーションロール必須）
  - App-only トークン検証（`idtyp == "app"` 必須、Delegated の `scp` / `http://schemas.microsoft.com/identity/claims/scope` 含有は即時拒否）
  - 厳格モードにおける必須 `X-Poc-Owner-Id` / `X-Poc-Org-Id` のヘッダー検証と詐称防止
  - 認証・認可失敗時の安全な HTTP 401 / 403 応答（内部詳細や構成情報の秘匿）
- **一時障害と再試行制御（Transient Faults & Retry Policies）**
  - 一時障害（408 / 429 / 503、gRPC `Unavailable` / `DeadlineExceeded` / `ResourceExhausted` の 3 コード限定）に対する Durable 再試行上限と期限の適用
  - SDK の複合例外（`AggregateException`）およびネットワークタイムアウトの正確な一時障害判定
  - 呼び出し元によるキャンセル要求（`CancellationToken`）の非再試行扱いと伝播
  - 長時間 Activity 実行中に監視期限へ到達した場合の競合制御
- **期限超過時の Batch Job 停止制御（Deadline & Termination Control）**
  - 停止対象 Job の Pool ID および fingerprint 照合（他 Job や他 Pool の誤停止防止）
  - 停止要求の再実行耐性（冪等性）
  - Job の自然完了と停止処理の競合ハンドリング
  - 停止確認に失敗した場合の Manifest 未公開保証
- **HTTP レイヤーと公開成果物検証（HTTP API, Error Sanitization & Artifact Integrity）**
  - 独自開始契約（HTTP 202 Accepted、独自 `/api/workflows/{requestId}` status URL、Location、Retry-After 10秒）の返却
  - 不正 JSON 構文および不正リクエストパラメータに対する適切なエラー応答（HTTP 400 Bad Request）
  - gRPC / RPC 一時通信障害時における HTTP 503 Service Unavailable および `Retry-After` ヘッダー付与
  - HTTP / Activity 境界でのエラー遮蔽と安全例外化（固定 message、inner exception 除去）
  - 公開成果物（manifest / aggregate / task results）の整合性照合（fingerprint、件数、合計値、Task 分類、成果物パス）
- **Activity リプレイ耐性とデータ整合性（Replay Safety & Data Integrity）**
  - Activity リプレイ時における既存 Batch Job / Task および Manifest の再利用
  - 計算結果ファイル（`result.txt`）欠損時の明示的な失敗処理
  - 集約データ破損時における最終 Manifest 公開の抑止

### 配置スクリプト・Terraform の追加検証項目（Fake / Mock）

- 作成済み state backend に対する `-PlanOnly` 実行
- backend 不存在と参照エラーの区別
- 既存の 0 台 Pool に対するコード配置
- 新規 Pool 作成時の全台準備完了待ちと node 一覧の pagination 処理
- 15 個の Functions 検出確認
- インスタンス状態 GET 時の 404 待機と 401/403 エラー時の即時失敗

### Azure 実環境で未検証の項目

以下の項目はローカルの Fake / Mock では再現できないため、Azure 実環境での検証が必要です。設計上の根拠と残りの試験項目は [設計と公式資料](workflow-reliability.md) に記載しています。

- Durable Entity の実 checkpoint 動作と Functions host 中断からの復旧
- Azure Blob Storage 実サービス上での同時条件付き書き込み（If-None-Match）
- 実 Azure Batch 上での Job 停止（Terminate）動作およびノード障害時の挙動

---

## 初版の記録（2026-10-16）

記録日: 2026-10-16。以下は初版実装時点でのローカル検証結果と、Azure 実環境試験に向けた手順・期待値の記録です（最新の検証状況はページ上部を参照してください）。

## 実行環境・証跡

| 項目 | 記録 |
|---|---|
| OS | Windows / PowerShell |
| .NET SDK | 10.0.402 を作業フォルダー内に導入 |
| .NET package restore | Function project / solution restore 成功 |
| .NET build / publish | solution build 0 警告・0 エラー、Release publish 成功。`artifacts/build.log` / `artifacts/publish.log` |
| .NET unit test | 初版: 30 Pass / 0 Fail / 0 Skip（現行: 270 Pass / 0 Fail / 0 Skip［Poc.Tests 268 + DtsProbe 2］） |
| Terraform fmt / validate | Terraform 1.14.0 / AzureRM 5.10.0、2026-10-16 実測 Pass |
| Terraform plan / apply | Not tested: Azure 接続値なし |
| Azure 実行 instance / Job / Task ID | 未発行 |
| Azure Activity 履歴 / Blob URI | 未取得 |

## ワンコマンド配置資材の追加検証

2026-10-16 に Terraform の `mock_provider` および fake CLI / HTTP を用いて以下のオフライン検証を実施しました。

| 対象 | 実測 | 証跡 |
|---|---|---|
| Terraform main | fmt -check / validate成功、Mock 7件全Pass | `artifacts/deployment-validation/terraform-main.log` |
| Terraform bootstrap | validate成功、Mock 1件Pass | `artifacts/deployment-validation/terraform-bootstrap.log` |
| Deployment.Common | planの削除・置換・共有Batch更新拒否、ZIP構成、native異常終了、秘密抑止、JSON変換がPass | `artifacts/deployment-validation/common.log` |
| Test-Deployment | ノード全台準備待ち、権限反映待ち、Functions検出、HTTP・Blob整合性など11シナリオPass | `artifacts/deployment-validation/readiness.log` |
| Deploy workflow | PlanOnly、apply→Pool→ZIP→Functionsの順、subscription固定、単一Task smoke→HTTP検証、環境identity変更拒否、bootstrapの絶対stateパスと実行順がPass | `artifacts/deployment-validation/workflow.log` |

※ 新規 Batch の実クォータ・Gallery アクセス・node-agent 適合性、state backend の初回 RBAC 反映待ち、Entra 認証による実 ZIP 配置については、Azure 実環境での検証が必要です。

## SDK / API の確認

2026-10-16 に以下の公式ドキュメントを確認し、Functions v4 / .NET 10 isolated、Linux Dedicated プラン、Managed Identity ベースの host storage 構成を選択しました。

- [Functions isolated guide](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide)
- [Durable MI guide](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-configure-managed-identity)
- [AzureRM Linux Function schema](https://github.com/hashicorp/terraform-provider-azurerm/blob/main/website/docs/r/linux_function_app.html.markdown)
- [Azure Batch SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/compute.batch-readme)

Function プロジェクトでは現行の `Azure.Functions.Sdk/1.0.0`、Worker `2.52.0`、DurableTask extension `1.19.0` を使用しています。

SDK 互換性の確認では、当初指定していた `Azure.Identity 1.17.1` と Functions 側が依存する `Azure.Core 1.54` の間で型定義の衝突が発生しました。そのため、`Azure.Core 1.53` 以降の Identity 型統合に対応した `Azure.Identity 1.21.0` へ更新し、ビルドが正常に通ることを確認しています。なお、ビルド時は Function プロジェクトを先に restore して SDK の生成ターゲットを準備してから solution 全体を restore する手順としており、README にも明記しています。

## シナリオ

| ID | 手順 | 期待値 | 実測 / 証跡の場所 | 結果 |
|---|---|---|---|---|
| Build | `dotnet build DurableBatchPoc.slnx --no-restore` | コンパイル成功 | 0 警告・0 エラー。`artifacts/build.log` | Pass |
| Publish | `dotnet publish src/Poc.Functions/Poc.Functions.csproj -c Release --no-restore -o artifacts/publish` | デプロイ用成果物生成 | 成功。`artifacts/publish.log` / `artifacts/publish/` | Pass |
| Unit | `dotnet test --project tests/Poc.Tests/Poc.Tests.csproj --no-restore` | Fake の正常、異常、冪等性が合格 | 初版: 30 Pass、現行: 270 Pass / 0 Fail / 0 Skip［Poc.Tests 268 + DtsProbe 2］ | Pass |
| Windows command | ローカル Windows で `cmd` → `powershell.exe` を実行 | 正常 exit 0 / result 1、故意失敗 exit 42 | 正常 exit 0・result.txt=1、失敗 exit 42・result ファイルなし。`artifacts/test-summary.txt` | Pass（ローカルのみ） |
| Terraform init | `terraform -chdir=infra init -backend=false` | provider の取得成功 | AzureRM v5.10.0 (signed by HashiCorp)、`.terraform.lock.hcl` 生成 | Pass |
| Terraform fmt | `terraform -chdir=infra fmt -check` | 差分なし、exit 0 | 2026-10-16 実行、標準出力なし、exit 0 | Pass |
| Terraform validate | `terraform -chdir=infra validate -no-color` | 構成の検証成功 | 2026-10-16: `Success! The configuration is valid.`、exit 0 | Pass |
| Step A | `dotnet run --project tools/BatchSmoke` | 既存 Windows Pool の単一 Task 成功、File API で整数1 | Azure 接続なし | Not tested |
| T01 | 3 Task の新規 requestId を POST | 3結果、sum=3、manifest 存在 | Azure 接続なし | Not tested |
| T02 | light / heavy 別 requestId で実行 | 分類理由と待機時間差がログと結果に残る | Azure 接続なし | Not tested |
| T03 | `failTaskIndex=1` | 親 Failed、aggregate / manifest なし | Azure 接続なし | Not tested |
| T04 | heavy の長時間 Task | HTTP 202、Timer 待機、状態照会 | Azure 接続なし | Not tested |
| T05 | 待機中に PoC Function のみ再起動 | 同じ instance で再開し manifest 公開 | Azure 接続なし | Not tested |
| T06 Mock | Fake で結果ファイル取得失敗 | 明示失敗、manifest なし | `MissingTaskFileIsAnErrorAndPreventsManifest` 成功 | Pass（Mock） |
| T06 Azure | 実 Batch File API で取得不能 | 明示失敗、manifest なし | 実ノードは未試験 | Not tested |
| Timeout | 待機より短い期限 | TimedOut、manifestなし、Job停止結果を記録 | Mock成功、実Azure接続なし | Not tested（Azure） |
| T07 | 同一 requestId の同時 POST | 同入力は同instance、異入力は409、Entityで起動を直列化 | Mock 成功（WorkflowAdmissionTests / WorkflowHttpTests）、実 Azure 接続なし | Not tested（Azure） |
| T08 Mock | Activity の副作用後に再呼び出し | 同一 Job / Task / manifest を再利用 | `WorkflowOperationsTests.ReplayedActivitiesReuseResourcesAndManifest` 成功。1 Job / 3 Tasks / 5文書 | Pass（Mock） |
| T08 Azure | 実 Activity 副作用後の中断・再実行 | 同一 Job / Task / manifest を再利用 | クラウド replay 未実施 | Not tested |
| T09 | OutputFiles 直送比較 | Blob への直接永続化 | P1 / 未実装 | Not tested |
| T10 | Durable / Batch 停止 | 両方の状態整合性 | 期限超過時のJob停止および利用者キャンセル連鎖（V12）はローカル実装・Mock成功（CancelWorkflow、Blob先行永続化、外部イベント待機、Batch停止、manifest公開抑止、冪等200/202）。15関数コードの実Azure接続・回帰は未実施 | Pass（Mock） / Not tested（Azure） |

Azure 実環境での試験実施時は、`artifacts/` 配下へ実行日時、設定値（認証情報やキー等のシークレットを除く）、状態照会 JSON、Batch ID / exit code / failure info、Blob URI と一覧、Application Insights ログを保存し、上表の該当行に証跡パスを追記します。なお、`MainOrchestrator` は失敗・タイムアウト時に例外を再スローするため、その場合の `runtimeStatus` は `Failed`、`customStatus.state` は `Failed` または `TimedOut` になります。`runtimeStatus` が `Completed` となるのは `output.state` が `Succeeded` の場合のみで、それ以外は失敗とみなします。最終的な合格判定では、出力された manifest の内容と入力データとの整合性まで確認します。
