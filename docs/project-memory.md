# プロジェクトメモリ

## 2026-10-19 レビュー指摘3件（P2一時障害503応答・P2保存ヘルパーDIリポジトリ接続・P2初期化待機総予算算入）の改修完了

レビューで指摘された 3 件の課題（[P2] Ready 入力受付時の一時障害に対する Retry-After 付き 503 応答の保証、[P2] `WorkflowOperations.SaveReadyManifestAsync` における DI 登録済み `BlobStorageRepository` 接続による本番 `InvalidOperationException` の解消、[P2] `Invoke-PrivateVmOperation.ps1` における VM 初期化待ち時間の総時間予算算入とスクリプト開始時からの残余時間計算）を改修し、単体テスト（353件全件）および PowerShell オフライン全テスト（8 スイート）の合格を確認しました。

- **対応内容詳細**:
  1. **[P2] Ready 入力受付時の一時障害に対する Retry-After 付き 503 応答の保証**:
     - `src/Poc.Functions/WorkflowFunctions.cs`: `StartWorkflow` 内の Ready マニフェスト取得（`TryGetReadyManifestAsync`）および実体 Blob 検証（`VerifyReadyManifestBlobAsync`）を受付処理全体の `try { ... }` ブロックの内部に移動。
     - Storage の HTTP 503 / 500 / 408 / 429 等の一時例外が発生した場合に、未処理例外（500/クラッシュ）とせず `catch (Exception ex) when (IsTransientRequestFailure(ex, cancellationToken))` により捕捉し、`Retry-After: 10` ヘッダー付きの `503 Service Unavailable` を確実に返却するよう改修。
     - `tests/Poc.Tests/ReadyManifestTests.cs`: マニフェスト取得時および実体検証時の一時障害注入テスト（503 および `Retry-After: 10` の検証）を追加。
  2. **[P2] Ready マニフェスト保存ヘルパーの本番構成対応（DI リポジトリ接続）**:
     - `src/Poc.Core/WorkflowOperations.cs`: コンストラクタ引数に `BlobStorageRepository? storageRepository = null` を追加し、`storage = storageRepository ?? new BlobStorageRepository(results)` をフィールドとして保持。
     - `SaveReadyManifestAsync`, `TryGetReadyManifestAsync`, `GetReadyManifestAsync` においてリポジトリを毎回新規作成（`results` のみ渡す不完全なインスタンス）するのをやめ、DI 登録された `BlobStorageRepository`（`BlobServiceClient` / `BlobContainerClient` を持つ完全なインスタンス）を使用するよう改修。保存前実体検証が `InvalidOperationException` で失敗する問題を解消。
     - `tests/Poc.Tests/ReadyManifestTests.cs`: DI 構成のリポジトリを注入した `WorkflowOperations` によるマニフェスト保存・検証・取得の回帰テストを追加。
  3. **[P2] VM 初期化待ち時間の総時間予算算入とスクリプト開始時からの残余時間計算**:
     - `scripts/Invoke-PrivateVmOperation.ps1`: ゲストシェルスクリプト `run.sh` の最先頭（`cloud-init` 待機前）で `script_start_epoch=$(date +%s)` および `script_start_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)` を記録し、`POC_VM_SCRIPT_START_UTC` として環境変数をエクスポート。
     - 初期化消費時間 `init_elapsed=$(( $(date +%s) - script_start_epoch ))` を算出し、外側の timeout を `rem_guest_timeout=$(( guest_timeout - init_elapsed ))` として初期化時間を差し引いた残余タイムアウトを適用。
     - ゲスト pwsh 内で `$guestStartUtc` を `POC_VM_SCRIPT_START_UTC` から復元し、`$env:POC_VM_DEADLINE_UTC = $guestStartUtc.AddSeconds([int]$t.budget_seconds).ToString("o")` をスクリプト開始時を起点として計算。
     - 初期化に最大 900 秒（例: 700 秒）かかった場合でも、スクリプト全体の合計実行時間が Azure VM Run Command の 90 分（5,400 秒）上限を超過せず、Pool 復元時間（600 秒）や証跡回収時間を安全に確保。
     - `scripts/tests/PrivateVm.Tests.ps1`: 残余時間計算（`guest_timeout` / `rem_guest_timeout`、`POC_VM_SCRIPT_START_UTC`）のアサーションに同期更新。

## 2026-10-19 レビュー指摘2件（P2旧受付データ再送500回避・P2Ready入力実体検証の受付接続）の改修完了

レビューで指摘された 2 件の課題（[P2] 旧 admission.json v2 再シリアライズによる不一致と 500 エラーの解消、[P2] Ready 入力実体検証の受付経路接続およびバージョン固定必須化）を改修し、単体テスト（350件全件）および PowerShell オフライン全テスト（8 スイート）の合格を確認しました。

- **対応内容詳細**:
  1. **[P2] 旧受付データに対する同一要求の再送が500になる問題の解消**:
     - `src/Poc.Core/Contracts.cs`: `WorkflowRequest` の新規追加フィールド（`ReadyManifestId`, `SchemaVersion`, `AlgorithmVersion`, `Mode`, `InputPayloadJson`）に `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` を適用し、`SchemaVersion` は `int? SchemaVersion = null` として省略可能に定義。`Equals` / `GetHashCode` では `SchemaVersion ?? 1` として比較することで、新旧レコード間のセマンティック等価性を保証。
     - `src/Poc.Core/BlobResultRepository.cs`: `PutIfAbsentAsync` の競合検出時（409/412）、従来の完全バイト一致（`SequenceEqual`）が失敗した場合でも、JSON デシリアライズによるセマンティック等価性（`EqualityComparer<T>.Default.Equals`）フォールバック判定を追加。スキーマ進化による旧保存データの再送時に誤った 500 / Conflict を回避。
     - `tests/Poc.Tests/TestDoubles.cs`: テスト用 InMemory リポジトリにおいて `JsonSerializerDefaults.Web` を使用したキャメルケース一致を徹底。
     - `tests/Poc.Tests/ReadyManifestTests.cs`: 旧形式の `admission.json` v2 生 JSON に対する同一要求の再送試験（`AdmitAsync` / `StartWorkflow` で 202 が返ること）を追加。
  2. **[P2] Ready 入力実体検証の受付経路接続およびバージョン固定の必須化**:
     - `src/Poc.Core/Contracts.cs`: `ReadyManifestBlob.Validate()` において、`VersionId` および `Etag` の双方が未設定（null / 空白）の場合に即時バリデーションエラーとする不変性固定ガードを追加。
     - `src/Poc.Core/BlobStorageRepository.cs`: `SaveReadyManifestAsync` 実行時に、事前に `VerifyTargetBlobAsync` を呼び出して参照先 Blob の存在・サイズ・SHA-256 ストリーミングハッシュを検証してからマニフェストを保存するよう改修。
     - `src/Poc.Functions/WorkflowAdmission.cs` / `WorkflowFunctions.cs`: `StartWorkflow` の受付経路において、`ReadyManifestId` 受理時にマニフェスト取得・所有者検証だけでなく、`admission.VerifyReadyManifestBlobAsync` を呼び出して実体 Blob の存在・サイズ・SHA-256 を検証。実体欠損時は 400（Not Found）、ハッシュ/サイズ不一致時は 400（Invalid Data）、ETag 不一致時は 412（Precondition Failed）を返却し、202 応答前の実体保証を確立。

## 2026-10-19 Track 1（Input Ready パイプライン・契約拡張）および Track 3（Storage 閉域化 IaC）の実装完了

- サブエージェント並列作業により、作業計画書（`docs/app-service-blob-batch-work-plan.md`）の Track 1（TASK-0.1, TASK-1.1〜1.3）および Track 3（TASK-3.1）を実装し、全単体・統合・IaC テストに合格しました。
- **Track 1 (Input Ready & Contracts)**:
  - `src/Poc.Core/Contracts.cs`: `ReadyManifest` および `ReadyManifestBlob`（コンテナ・相対キー検証、SHA-256、サイズ、不変性）を定義。`WorkflowRequest` に `ReadyManifestId`、`SchemaVersion`、`AlgorithmVersion`、`Mode` を追加。Fingerprint v1/v2 計算および `inputs/{readyManifestId}/manifest.json` パス解決を実装。
  - `src/Poc.Core/BlobStorageRepository.cs`: `ReadyManifest` の条件付き作成（ETag による冪等・競合検出）、読み込み、ストリーミングハッシュ・サイズ検証（80KBバッファによる非メモリ展開）を実装。
  - `src/Poc.Functions/WorkflowAdmission.cs` / `WorkflowFunctions.cs`: `StartWorkflow` で `ReadyManifestId` 指定時にマニフェスト取得・バリデーション・呼出元所有者照合（不一致 403、不在 400）を実施し、`admission.json` v2 形式で排他受付。レガシー v1 要求の 100% 後方互換を維持。
  - `tests/Poc.Tests/ReadyManifestTests.cs`: 46 件の新規テストを追加（スキーマ検証、シリアライズ、ストア操作、Fingerprint、StartWorkflow 正常/不正/所有者不一致/競合/レガシー）。`dotnet test` 合計 340 件全件 Pass。
- **Track 3 (Storage Private Endpoints & DNS IaC)**:
  - `infra/storage.tf`: Storage Account に対する Blob / Queue / Table 各 Private Endpoint を追加（subnet `private_endpoints`）。`storage_public_network_access_enabled` 変数による PNA 切替に対応。
  - `infra/private-dns.tf`: `privatelink.blob.core.windows.net`, `privatelink.queue.core.windows.net`, `privatelink.table.core.windows.net` の Private DNS ゾーンと VNet リンクを定義。
  - `infra/outputs.tf` / `infra/tests/modes.tftest.hcl`: 各 Private IP / Endpoint 出力を追加し、Terraform test 全 38 件 Pass、`terraform validate` Pass。
  - PowerShell オフラインテスト 8 スイート全件 Pass を確認。

## 2026-10-19 App Service・Blob 入力・実業務 Batch 計算の次期実装指示書を作成

- `docs/app-service-blob-batch-implementation-instructions.md` を、`f87b050` 時点の既存基盤とコード契約を基準に作成しました。これは業務契約・認証/所有者境界・入力 Blob の不変性・Durable/Batch 互換・取消/公開調停・閉域化・受け入れ試験の実装要件を整理した計画書であり、App Service、入力受付、実 C++、Batch 大容量転送の実装完了を意味しません。
- 既存オンプレミス Web backend を置換するか App Service を追加するか、上流ユーザー認証と owner/org 対応、業務データ形式・最大サイズ・バイナリ仕様は未確定ゲートです。Azure に対する新規実装・構成変更・実機検証はこの文書作成では行っていません。
- Azure Storage Durable backend を既定の既存環境として保持し、Durable Task Scheduler への切替・追加検証は独立した環境と状態として扱う方針を記録します。実行中 history/Entity の backend 間移行や接続先設定だけの切替は前提にしません。
- DTS 比較環境は Azure Storage 環境と並存する別 RG・Terraform state key・Function App・Storage・Task Hub とし、DTS Private Endpoint/DNS の追加と現行コードの最新回帰が必要です。Durable backend の in-place 接続設定変更だけを履歴移行として扱いません。

## 2026-10-19 ドキュメント再構成およびレビュー指摘3件（P3閉域化範囲限定・P3認証実証整合・P3動的タイムアウト記述）の改修完了

ドキュメント全体の「AI くささ」を排除した人間向け構造化（結論・責務境界・クイックスタート・引き継ぎバックログの整理）に加え、レビューで指摘された 3 件の文書整合性課題（[P3] 閉域化達成範囲の限定と Storage 公開維持の明記、[P3] README 内での認証・受付排他の実証完了状況の整合、[P3] ゲスト操作タイムアウトの動的計算への同期）を改修しました。

- **対応内容詳細**:
  1. **[P3] 閉域化の達成範囲の適正化**:
     - `docs/feasibility-matrix.md`: 基盤全体がパブリックインターネットを経由しないとする記述を改め、「Function 受信および Batch データプレーンの閉域化」に限定。Storage の公開データエンドポイント通信が NAT Gateway 経由で維持されていること（Storage PE は次期 V00 で対応）を明確化。
  2. **[P3] README 内での認証・受付排他実証状況の整合**:
     - `README.md`: 319 行目・374 行目・384 行目の旧記述（「Azure配置・認証実証は未完」）を改修し、Azure Storage バックエンド環境では実機検証完了済み（未認証 401、App Role 不足 403、正当トークン 200/202、稼働パッケージ同定、conflict シナリオ Pass）であり、DTS バックエンド環境での最新構成の実機実証が未実施であることを正確に区別して記載。
  3. **[P3] ゲスト操作タイムアウト記述の動的計算への同期**:
     - `README.md`: 238 行目の固定 4,200 秒という記述を改修。`TimeoutSeconds=3600` の場合、通常操作は 3,900 秒、Autoscale は 5,100 秒（上限 5,300 秒）となる動的タイムアウト計算、および `POC_VM_DEADLINE_UTC` による Pool 復元時間（最低 600 秒）確保の仕様に同期。

## 2026-10-19 レビュー指摘2件（P2タイムアウト起算点統一／Pool復元保証・P2証跡回収パストラバーサル防止）の改修完了

レビューで指摘された 2 件の課題（[P2・残存] ゲスト検証タイムアウトの起算点不一致による Pool 復元中断リスクの解消、[P2・新規] ゲスト指定ファイル名による証跡ディレクトリ外上書き／パストラバーサルの防止）を改修し、PowerShell オフライン全テスト（8 スイート）の合格を確認しました。

- **対応内容詳細**:
  1. **[P2・残存] ゲスト開始時の絶対期限共有（`POC_VM_DEADLINE_UTC`）と Pool 復元予算の確保・失敗通知**:
     - `scripts/Invoke-PrivateVmOperation.ps1`: ホスト側で `budget_seconds`（`AutoscaleValidation` では `$TimeoutSeconds + 1200`、その他は `$TimeoutSeconds`）を算出し、Run Command の `$transport` メタデータへ含めて VM に渡す。
     - ゲストシェルスクリプト（`run.sh` / `pwsh`）: ゲスト `pwsh` の最先端（初期化直後）で `$guestStartUtc = [DateTime]::UtcNow` を記録し、`$env:POC_VM_DEADLINE_UTC = $guestStartUtc.AddSeconds([int]$t.budget_seconds).ToString("o")` を環境変数としてエクスポート。
     - 外側の bash `timeout` は `[Math]::Min(5300, $budgetSeconds + 300)` に設定し、Azure Action Run Command 上限（5,400 秒 / 90 分）の範囲内で内部処理全体に対して 300 秒の安全マージンを確保。
     - `scripts/Invoke-PocAutoscaleValidation.ps1`: `$deadlineUtc` を `[Environment]::GetEnvironmentVariable('POC_VM_DEADLINE_UTC')` から取得（`Get-Variable` は `Variable:` ドライブ限定のため使用せず、.NET API で確実に環境変数を読み取り。未設定時はフォールバック）。バンドルダウンロード再試行（最大 300 秒）や展開に要した時間にかかわらず、準備待機・0台待機・評価待機・ワークフロー検証の各残余制限時間を `($deadlineUtc - [DateTime]::UtcNow).TotalSeconds - $restorationReserveSeconds` から一元的に算出。
     - `finally` 内のプール復元（オートスケール無効化、固定スケールリサイズ待機、レディネス検証）用に共有期限から 600 秒（`$restorationReserveSeconds`）の復元予算を確保し、外側の `timeout` が復元時間を先行消費するのを防止。復元失敗時は `Status=Fail` / `Reason=PoolRestorationFailed` として明示的に検知・通知する設計へ改修。
  2. **[P2・新規] 証跡回収における固定許可リストとパストラバーサル防止**:
     - `scripts/Invoke-PrivateVmOperation.ps1`: VM から返却される `$result.summary_artifacts` の各ファイル名に対し、操作別の厳格な固定許可リスト（`WorkflowAuth`: `summary.json`、`AutoscaleValidation`: `autoscale-result.json`, `summary.json`, `summary.txt`、その他: `summary.json`, `summary.txt`）による検証を導入。
     - パス区切り文字（`\`, `/`）を含むファイル名や `[IO.Path]::GetFileName($name) -ne $name` となる不正な指定を即座に検知し、`throw "Private VM returned an untrusted or invalid summary artifact name: $name"` で拒否。
     - さらに `[IO.Path]::GetFullPath` による正準化を行い、`$targetFile.StartsWith($resolvedEvidenceDir)` により、万一の未知の文字列表現でも `$EvidenceDirectory` 境界外への書き込みを防御。
  3. **テスト検証**:
     - `scripts/tests/PrivateVm.Tests.ps1`:
       - `..\..\probe.txt` や許可リスト外のファイル名を安全な Fake で注入し、正しく拒否されることを検証するテストケースを追加。
       - Run Command のシェルコマンドに `POC_VM_DEADLINE_UTC` の初期化コードが含まれること、およびタイムアウト計算（2,400 秒 / 5,100 秒）が正しく反映されることのアサーションを追加。
     - `scripts/tests/AutoscaleValidation.Tests.ps1`:
       - `POC_VM_DEADLINE_UTC` に期限切れ値（`-10`秒）を設定した際に準備段階で即座に拒否（`Insufficient time remaining before pool preparation`）されること、復元確保時間を下回る逼迫値（`+630`秒）で同様に拒否されること、および十分な未来値（`+3600`秒）で正常に採用・合格することのテストケースを追加。
     - 全 8 スイートの PowerShell テストを実行し、全件 PASS を確認。

## 2026-10-19 レビュー指摘4件（P2配置ガード置換回避・P2既存Batch権限絞り込み・P2オートスケール復元時間確保・P3証跡回収失敗通知）の改修完了

全体レビューで指摘された 4 件の課題（[P2] `validation_batch` の Reader → Contributor 変更による既存環境デプロイ停止の回避、[P2] `batch_mode=existing` 共有 Batch に対する Reader 維持と過剰権限防止、[P2] オートスケールゲスト検証における全体期限共有と Pool 復元時間の確保、[P3] `Invoke-PrivateVmOperation` での証跡サマリ回収失敗の明示的通知）を改修し、IaC およびオフライン全テストの合格を確認しました。

- **対応内容詳細**:
  1. **[P2] 既存環境更新での配置ガード停止回避（既存割り当て維持＋新設割り当て）**:
     - `infra/validation-vm.tf`: `azurerm_role_assignment.validation_batch` のロール定義を `Azure Batch Account Reader` に戻し、既存環境でリソース置換（ForceNew / `delete`）が発生しないよう保持。
     - 専用 Batch アカウント環境（`var.batch_mode == "new"`）向けに、追加のリソース `azurerm_role_assignment.validation_batch_contributor` を `count = var.batch_mode == "new" ? 1 : 0` で定義。これにより既存環境への適用時も `create`（追加付与）となり、`Deployment.Common.ps1` の配置ガード（`Assert-DeploymentPlan`）を正常に通過。
  2. **[P2] 共有 Batch（`batch_mode=existing`）の最小権限（Reader）維持**:
     - `validation_batch_contributor` の条件判定により、共有 Batch 環境では Contributor の `count = 0` となり、共有 Batch に対する管理・削除権限を付与せず `Azure Batch Account Reader` のみを厳格に維持。
     - `infra/tests/modes.tftest.hcl`: `existing` モードでは `validation_batch_contributor` が 0 件・Reader のみであること、`new` モードでは Reader と Contributor（1 件）の双方が付与されることのアサーションを追加（Terraform Test 37/37 全件 Pass）。
  3. **[P2] ゲスト検証の全体期限共有と Pool 復元時間の確保**:
     - `scripts/Invoke-PrivateVmOperation.ps1`: ゲストシェルスクリプトの `timeout 4200` を動的化。`AutoscaleValidation` では `$TimeoutSeconds + 1320`（最大 5,300 秒、Action Run Command 上限 5,400 秒 / 90 分以内）を割り当て、検証遅延時でも外側のプロセス強制終了を防止。
     - `scripts/Invoke-PocAutoscaleValidation.ps1`: 全体期限バジェット（`$overallBudgetSeconds = $TimeoutSeconds + 600 + $restorationReserveSeconds`）と復元確保時間（`$restorationReserveSeconds = 600`）を新設。準備段階およびワークフロー実行段階で復元時間をあらかじめ差し引いた制限時間（`$workflowTimeout`）を割り当て、万一の遅延発生時も `finally` 内のプール復元（オートスケール無効化、固定スケールリサイズ待機、レディネス検証）に必要な時間を確実に担保。
  4. **[P3] 証跡サマリ回収失敗の明示的通知**:
     - `scripts/Invoke-PrivateVmOperation.ps1`: `storage blob download` による証跡サマリ回収処理において、例外を空握りつぶし（`catch { }`）していた箇所を改修。回収失敗したファイル名を追跡し、取得漏れが発生した場合は VM 側の成功とは区別して `"Private VM operation $id completed (exit_code=...), but failed to retrieve evidence summary file(s) (...) from the control container."` を明示的にスロー。
     - `scripts/PrivateVm.Worker.ps1`: 実際にアップロードされたサマリファイル一覧を `$result.summary_artifacts` に記録し、ホスト側がアップロード済みの全証跡成果物を確実に検証・回収するフローを確立。
     - `scripts/tests/PrivateVm.Tests.ps1`: サマリダウンロード失敗時に例外が発生すること、および動的タイムアウトが正しくシェルスクリプトに反映されることのテストケースを追加。

- **対応内容詳細**:
  1. **[P2] 閉域化後のオートスケール検証（V09）の閉域委譲と RBAC 拡充**:
     - `infra/validation-vm.tf`: 検証 VM（`poc-df40481149-validation`）の Managed Identity に対する Batch Account ロールを `Azure Batch Account Reader` から `Azure Batch Account Contributor` に昇格（Pool の縮小、Autoscale 式有効化、リサイズ復元等のデータプレーン変更操作を許可）。
     - `infra/tests/modes.tftest.hcl`: アサーションを更新し、Terraform Test 37/37 全件 Pass を確認。
     - `scripts/Invoke-PrivateVmOperation.ps1` & `scripts/PrivateVm.Worker.ps1`: 新オペレーション `AutoscaleValidation` を追加し、`Invoke-PocAutoscaleValidation.ps1` をバンドルして閉域検証 VM 上でゲスト実行（`-AsGuest`）。VM 内部から Private Endpoint（`10.80.2.5`）経由で Pool 操作・0 台待機・ワークフロー投入・自動拡張検知・固定スケール復元を実行し、サニタイズされた証跡（`autoscale-result.json`, `summary.json`, `summary.txt`）を回収するフローを確立。
     - `scripts/Invoke-PocAutoscaleValidation.ps1`: `batch_private_ip` が存在する環境（パブリックアクセス遮断環境）では、ホスト側からの直接 API 呼び出しを行わず `Invoke-PrivateVmOperation -Operation AutoscaleValidation` へ自動委譲する分岐を追加。ゲスト VM 内では直接 `Invoke-PocValidation`（またはスクリプト）を呼び出してワークフローを実行。
     - `scripts/tests/AutoscaleValidation.Tests.ps1` & `PrivateVm.Tests.ps1`: 閉域委譲テストおよびゲスト実行テストを追加し、オフライン検証合格。
  2. **[P2] PR 承認ガードにおける `pipefail` 明示と取得分離**:
     - `.github/workflows/branch-protection-guard.yml`: 各ステップに `shell: bash` を明示し、`set -eo pipefail` を追加。
     - `RAW_REVIEWS=$(gh api --paginate ...)` による API 取得と、その後の `jq` パイプラインを分離し、ページネーション取得途中のネットワークエラーや API エラー発生時に即座にチェックを失敗させ、部分的な承認情報による誤通過を確実に防止。
  3. **[P3] README.md の到達点・境界表の整合**:
     - `README.md`: B案フェーズ2 & 3（Function VNet Integration、Batch Private Endpoints、Batch Account パブリックアクセス完全遮断、Batch Pool ノード VNet 統合 & NoPublicIPAddresses）の完了と Azure 実機完全合格実績を反映。「未実証・未再検証の範囲」から Batch 閉域化を削除し、責務境界表および前提条件記述を最新 IaC と同期。
  4. **[P3] project-memory.md のパッケージ指紋誤記訂正**:
     - `docs/project-memory.md`: フェーズ3完了記録における `livePackageSha256` / `liveAssemblySha256` を、参照先証跡（`artifacts/private-auth-validation/20261019-042651-0796/summary.json`）の実際のデプロイ指紋（`9d6a957b...` / `00112c7f...`）に訂正。

## 2026-10-19 B案フェーズ3: Azure Batch Pool VNet 統合（NoPublicIPAddresses）& Batch Account パブリックアクセス完全遮断の Azure 実機完全合格（B案 100% 完遂）

B案フェーズ3（Azure Batch Pool の VNet サブネット統合／パブリック IP なし構成、および Azure Batch Account のパブリックネットワークアクセス完全遮断）を実 Azure 環境（`poc-durable-batch-rg`、Function App: `poc-df40481149-func`、閉域検証 VM: `poc-df40481149-validation`、Batch Account: `pocdf40481149batch`）に適用し、再デプロイおよび閉域検証 VM からの完全実機検証を実施して、**全項目完全合格（Pass、終了コード 0）** を確認しました。これにより **B案（フェーズ1・2・3）が 100% 完遂** しました。

- **対応内容詳細**:
  1. **Azure Batch Pool の VNet サブネット統合 & `NoPublicIPAddresses`**:
     - `azurerm_batch_pool.poc` に `network_configuration` ブロックを追加し、`subnet_id = azurerm_subnet.batch_nodes.id`（`10.80.4.0/24`）および `public_address_provisioning_type = "NoPublicIPAddresses"` を適用。
     - Simplified ノード通信モードにより、ノード用受信 NSG ルールを一切持たず、ノード管理通信は VNet 内の `nodeManagement` Private Endpoint（`10.80.2.6`）経由、外部通信（KMS・パッケージ）は NAT Gateway 経由のアウトバウンドのみで安全に稼働。
  2. **Azure Batch Account パブリックアクセス完全遮断**:
     - `azurerm_batch_account.poc` に `public_network_access_enabled = false` を設定。パブリックインターネットからの Batch REST API 呼び出しを完全に遮断。
  3. **デプロイ・運用スクリプトの安全機構と閉域ルーティング**:
     - `scripts/Deployment.Common.ps1` の `Assert-DeploymentPlan` に `[switch]$AllowBatchPoolRecreation` を新設。BatchMode = 'new' かつ明示的なオプトイン時のみ `azurerm_batch_pool` の置換（ForceNew）を許可し、他リソースの意図せぬ削除を厳格に阻止。
     - Batch パブリックアクセス遮断に伴い、オペレータ端末からの直接 Batch REST API 呼び出しが 403 Forbidden となるため、`scripts/deploy.ps1` においてプール健全性待機を閉域検証 VM 経由（`Invoke-PrivateVmOperation -Operation PoolValidation`）へ切り替え。
     - `scripts/PrivateVm.Worker.ps1` に `Assert-PrivateBatchDns`（Batch FQDN が `10.80.2.5` に解決されることの事前確認）および `PoolValidation` ハンドラー（ARM / Batch REST による steady/succeeded 待機）を実装。
  4. **IaC & テスト網羅**:
     - `infra/tests/modes.tftest.hcl` に `public_network_access_enabled == false` および `NoPublicIPAddresses` のアサーションを追加（Terraform Test 37/37 全件 Pass、`terraform fmt -check` Pass）。
     - `scripts/tests/Deployment.Common.Tests.ps1`, `PrivateVm.Tests.ps1`, `Deploy.Workflow.Tests.ps1` に `PoolValidation` およびプール再作成許可テストを追加（PowerShell オフライン試験 8 スイート全件 Pass、`dotnet test` 294 件 Pass）。
- **Azure 実機デプロイ成果**: `.deploy/poc/20261019-131731-6874`
  - インフラ差分: 1 to add / 2 to change / 1 to destroy（プール再作成と Batch Account パブリックアクセス遮断）。
  - プール健全性確認: 閉域検証 VM から Private Endpoint（`10.80.2.5`）経由でプールが `steady` かつ `succeeded` に達したことを確認。
  - Function パッケージ再配置および再起動後、15 関数全件のインデックス登録・HTTP 疎通を確認してデプロイ完了（終了コード 0）。
- **実機 ARM / Batch 状態検証**:
  - `az batch account show`: `publicNetworkAccess: "Disabled"` を確認。
  - `az rest --method get --url .../pools/poc-df40481149-pool`:
    - `provisioningState: "Succeeded"`
    - `allocationState: "Steady"`
    - `networkConfiguration.subnetId: ".../subnets/batch-nodes"`
    - `publicIPAddressConfiguration.provision: "NoPublicIPAddresses"`
- **実機検証エビデンス**: `artifacts/private-auth-validation/20261019-042651-0796/summary.json`（終了コード 0、Pass）
  - **未認証遮断（401）**: `anonymous`, `keyOnly`, `forgedPrincipal`, `bearerWithoutKey` の計12プローブすべてで HTTP 401 遮断を確認。
  - **Audience 不正遮断（401）**: ARM 向けトークンに対し EasyAuth による HTTP 401 遮断を全3 API で確認。
  - **App Role 不足遮断（403）**: `Workflow.Execute` を持たないユーザー割り当てマネージド ID からの要求に対し、`EntraEasyAuthCallerResolver` による HTTP 403 Forbidden 遮断を全3 API で厳密に確認。
  - **所有者伝播認可（403）**: 認可トークン存在下での所有者ヘッダー欠落・他者指定に対し HTTP 403 遮断を確認。
  - **正常系・完全閉域完走（202/200）**: VNet 統合された Function App から Batch Private Endpoint を経由してパブリックアクセス遮断下の Batch Account に Job を作成し、`NoPublicIPAddresses` の閉域ノード上で Task 実行・結果回収・マニフェスト集約を完走し、200 結果取得を確認。
  - **稼働パッケージ同定（Pass）**: 実機レスポンスヘッダー `livePackageSha256`（`9d6a957b...`）とデプロイコンテンツ指紋、および `liveAssemblySha256`（`00112c7f...`）とデプロイアセンブリハッシュが完全一致し、`livePackageVerified: true` を確認。

## 2026-10-19 B案フェーズ2: Azure Functions Regional VNet Integration & Azure Batch Private Endpoints 実機完全合格

B案フェーズ2（Azure Functions の Regional VNet 統合および Azure Batch Account の Private Endpoint 化）を実 Azure 環境（`poc-durable-batch-rg`、Function App: `poc-df40481149-func`、閉域検証 VM: `poc-df40481149-validation`）に適用し、再デプロイおよび閉域検証 VM からの完全実機検証を実施して、**全項目完全合格（Pass、終了コード 0）** を確認しました。

- **対応内容詳細**:
  1. **Azure Functions Regional VNet Integration**:
     - `azurerm_subnet.functions_outbound`（CIDR: `10.80.3.0/24`、`Microsoft.Web/serverFarms` 委任、NAT Gateway 関連付け）を新設。
     - `azurerm_linux_function_app.poc`（Basic B1 Linux）に `virtual_network_subnet_id` を設定し、`site_config.vnet_route_all_enabled = true` を適用して Function App の全アウトバウンド通信を VNet 経由にルーティング。
  2. **Azure Batch Account Private Endpoints & Private DNS**:
     - `azurerm_private_dns_zone.batch`（`privatelink.batch.azure.com`）を作成し、検証 VNet（`poc-df40481149-vnet`）へリンク。
     - `private-endpoints` サブネット（`10.80.2.0/24`）内に、Batch REST API データプレーン用 `batchAccount`（IP: `10.80.2.5`）およびノード管理用 `nodeManagement`（IP: `10.80.2.6`）の Private Endpoint を作成。
     - `azurerm_subnet.batch_nodes`（CIDR: `10.80.4.0/24`、`default_outbound_access_enabled = false`、NAT Gateway 関連付け）を新設し、フェーズ3（ノードの VNet 移行）に備えたネットワーク基盤を整備。
  3. **IaC & テスト網羅**:
     - `infra/outputs.tf` に `batch_private_ip` および `batch_node_management_private_ip` を追加。
     - `infra/tests/modes.tftest.hcl` に Function App の VNet 統合、Outbound サブネット委任、Batch Private DNS / Endpoints のアサーションを追加（Terraform Test 37/37 Pass、`terraform fmt -check` Pass、`validate` Pass）。
  4. **閉域検証スクリプトの接続性・回復性向上**:
     - `scripts/PrivateVm.Worker.ps1` において、`WorkflowAuth` オペレーション実行前に `Assert-PrivateFunctionConnectivity` によるエンドポイント疎通（ウォームアップ待機）を挿入。
     - `scripts/Invoke-WorkflowAuthValidation.ps1` の `Invoke-WorkflowAuthHttp` に一時的なネットワーク例外に対する指数バックオフリトライ（最大5回）と、サニタイズされたエラー詳細記録を追加。
- **Azure 実機デプロイ成果**: `.deploy/poc/20261019-120416-8533`
  - インフラ差分: 8 to add / 1 to change / 0 to destroy。
  - Function App 再起動後、閉域検証 VM からエンドポイント疎通および 15 関数全件のインデックス登録を確認して完了記録を保存。
- **実機検証エビデンス**: `artifacts/private-auth-validation/20261019-031916-1137/summary.json`（終了コード 0、Pass）
  - **未認証遮断（401）**: `anonymous`, `keyOnly`, `forgedPrincipal`, `bearerWithoutKey` の計12プローブすべてで HTTP 401 遮断を確認。
  - **Audience 不正遮断（401）**: ARM 向けトークンに対し EasyAuth による HTTP 401 遮断を全3 API で確認。
  - **App Role 不足遮断（403）**: `Workflow.Execute` を持たないユーザー割り当てマネージド ID からの要求に対し、`EntraEasyAuthCallerResolver` による HTTP 403 Forbidden 遮断を全3 API で厳密に確認。
  - **所有者伝播認可（403）**: 認可トークン存在下での所有者ヘッダー欠落・他者指定に対し HTTP 403 遮断を確認。
  - **正常系・閉域完走（202/200）**: VNet 統合された Function App から Batch Private Endpoint を経由して Batch Job 作成・Task 実行・結果回収・マニフェスト集約を完走し、200 結果取得を確認。
  - **稼働パッケージ同定（Pass）**: 実機レスポンスヘッダー `livePackageSha256`（`15b78402...`）とデプロイコンテンツ指紋（`packageContentSha256`）、および `liveAssemblySha256`（`ca3b1382...`）とデプロイアセンブリハッシュ（`assemblySha256`）が完全一致し、`livePackageVerified: true` を確認。

## 2026-10-19 レビュー指摘2件（P2再起動後復旧確認・P3同定保証境界と設計書整合）の改修および Azure 実機完全合格

全体レビューで指摘された 2 件の課題（[P2] `deploy.ps1` が再起動後の復旧・疎通を確認せず完了を記録する問題、[P3] `WorkflowFunctions.cs` の稼働中ファイル同定保証境界と `docs/workflow-authentication-design.md` の記述齟齬）を改修し、実 Azure 環境への再デプロイおよび閉域検証 VM（`poc-df40481149-validation`）からの完全実機検証を実施し、**全項目完全合格（Pass、終了コード 0）** を確認しました。

- **対応内容詳細**:
  1. **[P2] 再起動後の復旧・疎通確認プロセスの導入**:
     - `scripts/PrivateVm.Worker.ps1` / `scripts/Invoke-PrivateVmOperation.ps1` に新オペレーション `FunctionValidation` を新設。
     - `scripts/deploy.ps1` のフローを改修：プライベート SCM への ZIP 配置完了後、`az functionapp restart` を発行し、非同期再起動の安定化待機（10秒）を挟んだ上で、閉域検証 VM から `FunctionValidation` を実行。
     - `FunctionValidation` 内でプライベート DNS 解決（`Assert-PrivateFunctionDns`）、エンドポイント応答疎通確認（`Assert-PrivateFunctionConnectivity` による `/api/workflows` への非 5xx 応答待機）、および ARM 上での 15 関数全件インデックス登録確認（`Invoke-DeploymentValidation -Phase Functions`）を**再起動後に実施・成功確認した上で `deployment-result.json` に完了記録**を保存するよう改修。
     - オフライン試験 `Deploy.Workflow.Tests.ps1` / `PrivateVm.Tests.ps1` も改修シーケンスに合わせて更新・全件 Pass。
  2. **[P3] 同定方式の保証境界の明文化および設計書整合**:
     - `src/Poc.Functions/WorkflowFunctions.cs` のヘッダー定数およびハッシュ取得メソッド（`ComputePackageSha256`, `ComputeAssemblySha256`）に XML ドキュメントコメントを整備。
     - 本方式が発行・デプロイ時に算出し同梱されたマニフェスト暗号学的指紋（全101ファイルのソート済み SHA-256 マニフェスト `package-sha256.txt`、および `Poc.Functions.dll` の `assembly-sha256.txt`）をプロセス起動時にメモリに保持して返却するものであり、リクエスト毎のディスク全走査・動的再計算ではない旨の保証境界を明示。
     - `docs/workflow-authentication-design.md` から旧記述（`livePackageVerified=false 固定`）を完全改定し、ラッチング不変保護（途中の不一致・欠落で恒久的に false 化）および前提確認（`unauthorizedClientConfirmedInAllowedList = true`）を明文化。
- **Azure 実機デプロイ成果**: `.deploy/poc/20261019-112704-9949`
  - Function パッケージ SHA-256: `54aa7b1980db50d6d43bd40fb3d6571863d587f997d0290155dfbfccfbd82ae0`
  - エントリアセンブリ SHA-256: `a0be58b70585cbf28d7d7cfbbe2a2733161cec20537e37bc82b7b77907f658dd`
  - パッケージコンテンツ SHA-256: `ff5291b221b9cb3b08a829cad511a16ab405763a6910ecb3682545df0f51ddbe`
  - 再起動後の 15 関数全件登録・HTTP 疎通確認を実機検証して完了記録を保存。
- **実機検証エビデンス**: `artifacts/private-auth-validation/20261019-023554-2683/summary.json`（終了コード 0、Pass）
  - **未認証プローブ遮断（401）**: `anonymous`, `keyOnly`, `forgedPrincipal`, `bearerWithoutKey` の計12プローブすべてで HTTP 401 遮断を確認。
  - **Audience 不正遮断（401）**: ARM 宛てトークン（`https://management.azure.com/`）に対し、EasyAuth による HTTP 401 遮断を全3 API で確認。
  - **App Role 不足遮断（403）**: EasyAuth 許可リスト確認済み（`unauthorizedClientConfirmedInAllowedList = true`）の条件下で、`Workflow.Execute` を持たない `poc-unauthorized-id` からの要求に対し、`EntraEasyAuthCallerResolver` による HTTP 403 Forbidden 遮断を全3 API で厳密に確認。
  - **所有者伝播認可（403）**: 認可トークン存在下での所有者ヘッダー欠落・他者指定に対し HTTP 403 遮断を確認。
  - **正当マネージド ID 通過（200/202）**: `Workflow.Execute` を持つ正規マネージド ID からの要求が 202 受理、同一再送 202、入力競合 409、ステータスポーリングを経て 200 結果取得（全Task整合）まで完走。
  - **稼働パッケージ同定（Pass）**: 実機レスポンスヘッダー `livePackageSha256`（`ff5291b2...`）とデプロイコンテンツ指紋（`packageContentSha256`）、および `liveAssemblySha256`（`a0be58b7...`）とデプロイアセンブリハッシュ（`assemblySha256`）が完全一致し、`livePackageVerified: true` を確認。

## 2026-10-19 B案フェーズ1: Microsoft Entra ID サービス間認証・認可の実機検証および稼働パッケージ暗号学的同定の完全合格（レビュー修正反映）

B案フェーズ1（Microsoft Entra ID によるサービス間認証・認可および稼働パッケージ同定）の実機実装・検証を実 Azure 環境（`poc-durable-batch-rg`、Function App: `poc-df40481149-func`、閉域検証 VM: `poc-df40481149-validation`）において実施し、レビュー指摘3件（P2 後続不一致での同定無効化、P3 パッケージ全体コンテンツ指紋測定 `package_content_sha256`、P3 App Role 不足拒否前提確認 `unauthorizedClientConfirmedInAllowedList`）の修正を適用した上で、**全項目完全合格（Pass、終了コード 0）** を確認しました。

- **Azure 実機デプロイ成果**: `.deploy/poc/20261019-102131-6222`
  - Function App の EasyAuth v2（`active_directory_v2`、`unauthenticated_action: Return401`、`allowed_audiences`、`allowed_applications`）を有効化。
  - アセンブリ SHA-256: `d62ce67d6fae7a5c1090ccad3a8171fa0479a6f5a580374d64bfb3bbc83b46a9`
  - パッケージコンテンツ SHA-256: `a654af0e07c5ba8f5aa596e7418971870841df71a454faa44b65dbe14beb3815`
  - パッケージ ZIP SHA-256: `d4970c4c410c0ab4a186039790f26e72a92d343489fc6bba1d82245d1e3eb17a`
  - 全 API レスポンスにコンテンツ指紋 `X-Poc-Package-Sha256` およびエントリアセンブリ指紋 `X-Poc-Assembly-Sha256` を付与。
- **実機検証エビデンス**: `artifacts/private-auth-validation/20261019-013214-2425/summary.json`
  - **未認証遮断（401）**: `anonymous`, `keyOnly`, `forgedPrincipal`, `bearerWithoutKey` の計12プローブすべてで HTTP 401 遮断を確認。
  - **Audience 不正遮断（401）**: ARM 宛てトークン（`https://management.azure.com/`）に対し、EasyAuth による HTTP 401 遮断を全3 API で確認。
  - **App Role 不足遮断（403）**: EasyAuth 許可リスト確認済み（`unauthorizedClientConfirmedInAllowedList = true`）の条件下で、有効な Entra トークンを取得可能だが `Workflow.Execute` App Role を持たないユーザー割り当てマネージド ID（`poc-unauthorized-id`: `fb03274d-66d2-4f2e-856c-6bf17b142334`）からの要求に対し、`EntraEasyAuthCallerResolver` による **HTTP 403 Forbidden 遮断** を全3 API で厳密に確認。
  - **所有者伝播認可（403）**: 認可トークン存在下での所有者ヘッダー欠落・他者指定に対し HTTP 403 遮断を確認。
  - **正当マネージド ID 通過（200/202）**: `Workflow.Execute` を持つ VM システム割り当てマネージド ID（`2896f1b6...`）からの要求が 202 受理、同一再送 202、入力競合 409、ステータスポーリングを経て 200 結果取得（全Task整合）まで完走することを確認。
  - **稼働パッケージ同定（Pass）**: 実機レスポンスヘッダー `livePackageSha256`（`a654af0e...`）とデプロイコンテンツ指紋（`packageContentSha256`）、および `liveAssemblySha256`（`d62ce67d...`）とデプロイアセンブリハッシュ（`assemblySha256`）が完全一致し、`livePackageVerified: true` を確認。途中の不一致やヘッダー欠落で無効化される不変保護も確認。
- **Batch ネットワーク調査成果（B案フェーズ2・3の確定）**:
  - 現行 Batch Account は `publicNetworkAccess: Enabled`、Batch Pool ノードは既定のパブリック送信 NAT を利用していることを確認。
  - 後続として、Batch Account の Private Endpoint（`batchAccount` サブリソース）化（フェーズ2）、および Batch Pool ノードの VNet サブネット統合・`NoPublicIPAddresses` 構成（フェーズ3）の移行計画を文書化（`batch-networking-and-plan-b.md`）。

## 2026-10-19 最新15関数コード（取消調停・停止再送・停止確認永続化・状態正規化）の Azure 実環境再デプロイおよび全6シナリオ実機回帰全件合格

レビュー指摘（取消確定と成果物公開の排他調停 `resolution.json`、調停レコード整合性検証、Restart モード復旧状態検証 `HostStoppedBatchWallClock`、キャンセル部分書き込み失敗時の余分な Batch 計算抑止、停止未確認時の取消再送による Batch 停止再試行、停止確認永続化 `termination.json` の Activity 再試行・状態反映、Job 作成前取消のステータス正規化、補助 CLI BatchSmoke 修正）を取り込んだ最新の15関数パッケージを実 Azure 環境へ再デプロイし、閉域検証 VM からの全6シナリオ実機回帰試験を実行して**全件合格（Pass、終了コード 0）** を確認しました。

- **Azure 実機デプロイ成果**: `.deploy/poc/20261019-080215-5263`
  - インフラ計画差分なし（0 add / 1 change / 0 destroy）。
  - Function App への ZIP 配置後、**15関数全件のインデックス登録を確認**（`AggregateActivity`, `CancelWorkflow`, `ClassifyActivity`, `EnsureBatchJobActivity`, `EnsureBatchTaskActivity`, `FetchAndPersistActivity`, `GetBatchTaskStatusActivity`, `GetWorkflowResult`, `GetWorkflowStatus`, `MainOrchestrator`, `MonitorBatchTaskOrchestrator`, `PublishManifestActivity`, `StartWorkflow`, `TerminateBatchJobActivity`, `WorkflowAdmissionEntity`）。
  - パッケージ SHA-256: `06bf36f42ee396c9291e4172a9926021b60b045cb64f11db2d99050fe11ca833`
  - スモークテスト合格: 3 Tasks 投入・Batch 実行・結果回収・`Sum=3` 集約完了を確認（`requestId=deploy-13795081be9948a1b20a2114e447363b`、`instanceId=poc-7dcabfe0556442d93128d4355d5e935e72d7c817`）。
- **閉域検証 VM 実機回帰試験（全6シナリオ Pass、終了コード 0）**:
  - 証跡: `artifacts/private-validation/20261018-230722-4253/summary.json`
  - `single`: Pass（1 Task 完走、`Sum=1`、`runtimeStatus=Completed`）
  - `three`: Pass（3 Tasks 完走、`Sum=3`、`runtimeStatus=Completed`）
  - `classification`: Pass（`light`, `heavy`, `auto`［`light`自動判定］の3種投入がすべて正常完了、各 `Sum=1`）
  - `failure`: Pass（Task exitCode=42 により Orchestrator が期待通り `runtimeStatus=Failed`・`businessState=Failed` へ遷移、成果物公開抑止）
  - `conflict`: Pass（同一 requestId 重複投入に対し `conflictHttpStatus=409` 返戻を確認）
  - `cancellation`: Pass（`cancelHttpStatus=202` 受理、`cancel.json` 先行永続化、Orchestrator の外部イベント `CancelRequested` 調停、Batch Job `completed` 強制終了、Task `exitCode=-1073741510` 停止、成果物 `manifest.json` 非公開確認、`businessState=Cancelled`・`runtimeStatus=Failed` 確定、完了後キャンセル再投入に対する `idempotentCancelHttpStatus=200` 冪等応答を確認）
- **実機再検証により解消された課題**:
  - 先行してローカル Fake/Mock/単体テストのみで合格していたレビュー指摘修正群（取消確定の排他調停、停止未確認時の停止再試行、停止確認永続化の例外遮蔽・Activity再試行、Job作成前取消ステータス正規化等）が、**Azure 実機環境でも完全に動作し、回帰問題がないことを実証**しました。

## 2026-10-18 7821d82 全体レビュー指摘8件の修正完了（P1成果物公開競合・認可境界、P2例外型保持・イベント送達・終端409・検証スクリプト堅牢化）

コミット `7821d82` を基準としたレビュー指摘8件（P1 2件、P2 6件）の不具合・競合・認可漏れ・テストスクリプト堅牢化の改修を完了しました。

- **対応内容詳細**:
  1. **[P1] 取消受理後の成果物公開競合の解消**:
     - `runs/poc-<requestId>/final/resolution.json` (`WorkflowIdentity.ResolutionPath`) を導入し、`PublishManifestAsync` (Succeeded) と `CancelAsync` (Cancelled) の排他調停点として単一 Blob の条件付き書き込み (`PutIfAbsentAsync`) を確立。
     - 取消確定後の `manifest.json` 公開を確実に抑止し、成果物公開確定後の取消を 409 Conflict で拒否。
  2. **[P1] 所有者なしジョブへの取消認可境界の厳格化**:
     - `AuthorizeCancellationAsync` を新設。読み取り互換（`AuthorizeAccessAsync`）と異なり、`record?.Owner != caller` の厳密一致を要求。
     - 所有者なし旧ジョブに対する第三者 identity（Entra 伝播 Owner/Org 等）からの取消要求を HTTP 403 Forbidden で確実に拒否。
  3. **[P2] 取消イベント送達失敗の握りつぶし防止**:
     - `CancelWorkflow` における `client.RaiseEventAsync` の空の try/catch を撤廃。
     - イベント送信失敗時に 202 を返さずエラー（500/503）として伝播させ、安全な再送・回復を促進。
  4. **[P2] Activity 境界での WorkflowCancelledException 保持**:
     - `RetryableActivity` において `catch (WorkflowCancelledException ex) { throw new WorkflowCancelledException(ex.Message); }` を追加。
     - `PermanentWorkflowException` への型変換を防止し、`MainOrchestrator` の `IsCausedBy<WorkflowCancelledException>()` 判定を正常化（`Cancelled` 終端状態への確実な遷移）。
  5. **[P2] 終端ジョブ（Failed / TimedOut）に対する取消要求の 409 Conflict 返戻**:
     - `CancelWorkflow` において `normalized.Status is "Failed" or "TimedOut"` に対するチェックを追加。
     - 終端ジョブに対する不要な取消レコード作成を抑止し、HTTP 409 Conflict を返戻。
  6. **[P2・条件付き] CancelWorkflow 専用キーフォールバック到達性の改修**:
     - `Invoke-PocValidationHttp` に `[int[]]$AllowedStatusCodes = @(404,409)` を追加。
     - `cancellation` シナリオ初回の 401 応答を例外化せず許容し、`CancelWorkflow` 専用キーへのフォールバック取得ロジックへ正常到達可能に改修。
  7. **[P2] Function 復旧未確認時の復旧検証失敗保証**:
     - `Invoke-PocRecoveryValidation.ps1` の `finally` において、`-not $operation.restoredRunningStateVerified` 時に例外を throw するガードを追加。
  8. **[P2] Autoscale 復元失敗時の証跡 Fail 記録保証**:
     - `Invoke-PocAutoscaleValidation.ps1` の `finally` 内の Pool 復元および Readiness 検証を try/catch でラップし、失敗時に `$summary.Status = 'Fail'`, `$summary.Reason = 'PoolRestorationFailed'`, `$summary.ExitCode = 1` を記録して再スロー。
- **確定した検証実績**:
  - Release 単体テスト: 全279件（`Poc.Tests` 277件 + `DtsProbe` 2件、失敗・スキップ0件）合格（+9件: 競合調停、終端状態409、認可境界、Activity例外保持、Orchestrator遷移試験等）。
  - PowerShell オフライン試験: 全8組Pass（`PocValidation.Tests.ps1`, `RecoveryValidation.Tests.ps1`, `AutoscaleValidation.Tests.ps1` に新規検証ケースを追加）。
  - Terraform Mock: 全37件Pass。
  - `git diff --check`: エラー0件。
- **Azure 実機境界の明確化**:
  - 今回の修正コードはローカル環境（Fake/Mock）での検証完了であり、**Azure 実環境へのデプロイ・実機検証は未実施**です。

## 2026-10-18 15関数パッケージAzure実機デプロイおよび全6シナリオ実機回帰・V12利用者キャンセルの完全合格

`CancelWorkflow` を追加した最新の15関数コードパッケージの Azure 実環境デプロイ（`.deploy/poc/20261018-211336-1471`）、および閉域検証 VM 経由での全6シナリオ実機回帰検証（`artifacts/private-validation/20261018-124022-0449/summary.json`）を**全件合格（Pass、終了コード 0）** で完了しました。

- **Azure 実機デプロイ成果**: `.deploy/poc/20261018-211336-1471`
  - インフラ計画差分なし（0 add / 1 change / 0 destroy）。
  - Function App への ZIP 配置後、**15関数全件のインデックス登録を確認**（`AggregateActivity`, `CancelWorkflow`, `ClassifyActivity`, `EnsureBatchJobActivity`, `EnsureBatchTaskActivity`, `FetchAndPersistActivity`, `GetBatchTaskStatusActivity`, `GetWorkflowResult`, `GetWorkflowStatus`, `MainOrchestrator`, `MonitorBatchTaskOrchestrator`, `PublishManifestActivity`, `StartWorkflow`, `TerminateBatchJobActivity`, `WorkflowAdmissionEntity`）。
  - スモークテスト合格: 3 Tasks 投入・Batch 実行・結果回収・`Sum=3` 集約完了を確認（`requestId=deploy-27855cc869764f62abf22e94d3e7c2f1`、`instanceId=poc-b6948f0806f68ab70a04801aab6a57de46d242db`）。
- **閉域検証VM実機回帰試験（全6シナリオ Pass、終了コード 0）**:
  - `single`: Pass（1 Task 完走、`Sum=1`、`runtimeStatus=Completed`）
  - `three`: Pass（3 Tasks 完走、`Sum=3`、`runtimeStatus=Completed`）
  - `classification`: Pass（`light`, `heavy`, `auto`［`light`自動判定］の3種投入がすべて正常完了、各 `Sum=1`）
  - `failure`: Pass（Task exitCode=42 により Orchestrator が期待通り `runtimeStatus=Failed`・`businessState=Failed` へ遷移、成果物公開抑止）
  - `conflict`: Pass（同一 requestId 重複投入に対し `conflictHttpStatus=409` 返戻を確認）
  - `cancellation`（V12 実証）: Pass（`POST /api/workflows/{requestId}/cancel` に対し `cancelHttpStatus=202` 受理、先行 Blob 永続化 `runs/poc-<requestId>/cancel.json` 確認、Orchestrator の外部イベント `CancelRequested` 調停、Batch Job `completed` 強制終了、Task `exitCode=-1073741510` 停止、成果物 `manifest.json` 非公開確認、`businessState=Cancelled`・`runtimeStatus=Failed` 確定、完了後キャンセル再投入に対する `idempotentCancelHttpStatus=200` 冪等応答を確認）
- **実機検証で判明・改修した不具合**:
  1. `CancelWorkflow` において HttpRequestData の非シーク可能ストリーム（CanSeek == false）に対する `request.Body.Length` 呼び出しによる 500 エラーを修正（CanSeek ガードおよび JsonException 安全ハンドリング）。
  2. `Invoke-PocValidation.ps1` における `Set-StrictMode -Version Latest` 下での `customStatus.termination` / `terminationErrorCode` の安全なプロパティアクセスへの改修、および CancelWorkflow 専用キー取得フォールバックを追加。
  3. `Invoke-PrivatePocValidation.ps1` および `Deployment.Common.ps1` における Azure CLI（`az.cmd`）標準インストールパスへのフォールバック・環境変数 PATH 補正対応。
- **確定した検証実績**:
  - Release 単体テスト: 全270件（`Poc.Tests` 268/268件 + `DtsProbe` 2/2件、失敗・スキップ0件）合格。
  - PowerShell オフライン試験: 全8組Pass。
  - Terraform Mock: 全37件Pass。

## 2026-10-18 V12 利用者キャンセル（Job Cancellation）のローカル実装完了

V12 利用者キャンセル（Job Cancellation）のローカル実装およびテストを完了しました。
- **Functions総数**: `CancelWorkflow` を追加し、全15関数（HTTP 4件、Orchestrator 2件、Activity 8件、Entity 1件）へ更新。
- **仕様詳細**:
  - `POST /api/workflows/{requestId}/cancel` エンドポイントの実装。
  - Storage 上の `runs/poc-<requestId>/cancel.json` へのキャンセル要求レコード（`WorkflowCancelRecord`）先行永続化（条件付き作成）。
  - `MainOrchestrator` による Durable 外部イベント `CancelRequested` の待機と、子オーケストレーション並列監視（`Task.WhenAll`）との競合調停（`Task.WhenAny`）。
  - キャンセル検知時の Batch ジョブ有界停止（`TerminateBatchJobActivity`、最大2分ポーリング）の安全呼び出し。
  - キャンセル成立時の `manifest.json` 公開抑止、カスタムステータス確定（`state = "Cancelled"`, `errorCode = "workflow_cancelled"`）、例外終了（`WorkflowCancelledException`）。
  - 所有者認可（他者・別組織のキャンセル要求に対する HTTP 403 拒否、認証ヘッダー不正時の 400 拒否）。
  - 冪等応答（実行中 202 Accepted、完了・失敗・キャンセル済み終端状態への再要求 200 OK、未存在 404 Not Found）。
- **確定したローカル検証**:
  - Release 単体テスト: 全270件（`Poc.Tests` 268/268件 + `DtsProbe` 2/2件、失敗・スキップ0件）合格。内、新規 `CancelWorkflow` 試験を含む `WorkflowHttpTests` 88件（+7件）、`OrchestrationTests` 5件（+2件）。
  - PowerShell オフライン試験: 8組全件Pass（`PocValidation.Tests.ps1` に `cancellation` シナリオ［202/200冪等性、事前Blob確認、失敗系］を追加、`Test-Deployment.Tests.ps1` / `Deploy.Workflow.Tests.ps1` の期待関数数を15件に更新）。
  - Terraform Mock: 37/37件全Pass、`fmt -check` / `validate` Pass。
- **Azure 実機境界の明確化**: 前回の 14 関数コードについては Azure 実環境（`poc` 環境・Azure Storage バックエンド）への配置・スモークテストおよび閉域 5 シナリオ回帰で全件 Pass（`20261018-083838-2879`）を確認していますが、今回の `CancelWorkflow` を含む **15 関数コードの Azure 実機デプロイ・回帰検証は未実施** です。

## 2026-10-18 前回の14関数コード実機検証（新受付・独自状態API）

前回の14関数コード（14関数、`admission.json` v2受付、独自状態API契約、エラー遮蔽）をAzure実環境（`poc`環境・Azure Storageバックエンド）へデプロイし、スモークテスト（3 Tasks、Sum=3）および閉域検証VM経由の主要5シナリオ（`single`, `three`, `classification`, `failure`, `conflict`）を実行しました（※今回の15関数コードのAzure実機検証は未実施です）。

デプロイは `scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -Smoke`（ログ: `.deploy/poc/20261018-171029-4809/`）により実施され、インフラ差分なし（0 add / 1 change / 0 destroy）、Function AppへのZIP配置、14関数全件のインデックス登録、スモークテスト合格（`requestId=deploy-47611f6d13994d43837aaf8b92b38e5d`）を確認しました。
検証ランナー（`scripts/Invoke-PocValidation.ps1`）を改修し、`runs/poc-<requestId>/admission.json`（Version 2）を優先参照しつつ旧 `request.json` へフォールバックする後方互換を実装しました（オフライン試験 `PocValidation.Tests.ps1` Pass）。
閉域検証VMからの `scripts/Invoke-PrivatePocValidation.ps1` 実行により、5シナリオ全件Pass（終了コード0、証跡: `artifacts/private-validation/20261018-083838-2879/summary.json`）を確認しました。独自状態URL契約（開始202で独自URL返戻・Durable管理URL遮蔽）、エラー遮蔽、公開成果物検証、409競合調停がAzure実環境で正常に機能することを実証しました。

## 2026-10-18 EasyAuth IaCと独自API認証runner

作業開始時HEADは`27b24b4`でGitはcleanでした。既定`PocFunctionKey`を維持し、明示した厳格モード向けのEasyAuth IaCとVM Managed Identity/IMDS用の`Invoke-WorkflowAuthValidation.ps1`を追加します。Function keyとowner/orgを維持し、独自開始・状態・結果APIを使います。Entra登録・App Role割当・`idtyp` optional claimは自動変更しません。

厳格モードで`deploy.ps1 -Smoke`は初期検証で拒否します。既存のSmoke・Private/通常シナリオ・Recovery/Autoscale runnerの内部診断はBearer未対応です。新runnerの追加はこれらの移行や両Durableバックエンドの回帰完了を意味しません。診断用EasyAuth除外パスは設けず、SCMは既存のEntra/ARM token配置経路を維持します。

ロックAzureRM 5.10.0の実provider schemaを確認しました。tenant endpoint説明例の順序はMicrosoft公式資料と相違するため、公式の`/<tenant-id>/v2.0`を採用します。`WEBSITE_AUTH_ENABLED`は自動注入のread-only値で、IaCへ手動設定しません。必要claimが実EasyAuth principalに揃うか、偽造ヘッダー除去、認証無効化時の拒否は未実証です。公式根拠と事前設定は[認証境界](workflow-authentication-design.md)を参照してください。

今回のTerraform Mockは37/37件、`fmt -check`と`validate`が成功しました。PowerShellオフライン試験はDeployment.Common / Deploy.Workflow / Test-Deployment / PocValidation / PrivateVm / RecoveryValidation / AutoscaleValidation / WorkflowAuthValidationの計8組が成功しています。新runnerは来歴のscalar型ガード追加後にも再実行して成功し、秘密を含む配列を来歴fieldへ与えてもnullへ落とし証跡へ転記しないことを確認しました。runnerのHTTP・token・keyはFakeであり、EntraやAzureの実証ではありません。C#認証アダプターの変更はなく、以前のRelease261件は今回再実行した試験数ではありません。`git diff --check`も成功しています。

runner証跡の来歴は配置記述子に記載された値を形式検証して選択したものです。`livePackageVerified=false`を維持し、稼働packageの実測や最新commitのAzure配置成功とは扱いません。

## 2026-10-18 認証境界アダプター

作業開始時HEADは`e1d04d6`でGitはcleanでした。今回は既定`PocFunctionKey`互換を保ち、`EntraEasyAuth`を明示選択した場合のprincipal解析・tenant/issuer/audience・許可backend client ID・App Role・`idtyp=app`・scope不存在を検査するアダプターとローカル拒否試験を追加します。厳格モードではowner/orgが必須で、利用者情報を主張する責任は認可済みバックエンドにあります。Function keyは引き続き必要です。

Azure環境変数のガードは構成の暗号学的証明ではありません。必要claim欠落・曖昧なclaim/ヘッダーは拒否し、実EasyAuth principalにissuer/audience等が揃うかは未確認です。Entra登録・App Role割当・idtyp設定、EasyAuth IaC、runnerのBearer対応、Azure配置・両Durableバックエンドでの認証/偽造ヘッダー試験を残します。今回Azureへ接続していません。契約・公式根拠と後続手順は [認証境界](workflow-authentication-design.md) を参照してください。以下の過去試験数は今回の認証変更の結果ではありません。

今回の関連Release試験は、新規`WorkflowAuthorizationTests`40/40件・既存`WorkflowHttpTests`81/81件が成功しました。Fake HTTP/Mock Durable/メモリ内repositoryによる検証であり、Entraや実EasyAuthの実証ではありません。

認証試験追加後のsolution Releaseは全261件（Poc.Tests259件・DtsProbe2件）が成功し、失敗・スキップは0件です。Release publish（`artifacts/auth-boundary-release-publish`）も成功し、`local.settings*`の非混入を確認しました。`git diff --check`は成功しています。Azure配置・実機試験は未実施です。

## 2026-10-18 受付と所有者の単一レコード化

新受付は `runs/poc-<requestId>/admission.json` の Version 2 に入力・instanceId・nullableな所有者/組織をまとめ、条件付き作成で確定します。所有者なし/ありも同じ競合点で調停し、所有者/組織不一致は403、所有者一致・入力不一致は409です。同一所有者・入力の再送を許し、状態・結果照会も新レコードを参照します。

旧入力・所有者が整合する場合は再送時に正規化し、所有者なし旧受付への所有者取得を拒否します。旧所有者だけ残る片割れ、新旧レコードやinstanceとの矛盾は拒否します。旧Blob読取と新Blob作成は非原子的で、切替前の旧Starter停止が前提です。受付BlobとDurable通知も非原子的であり、通知不確実時は受付を保持し再送で回復します。

Entra認証・App Role・信頼された利用者情報の伝播は今回対象外で、所有者ヘッダーは本人性を証明しません。PoC/runnerの所有者なし互換を維持します。今回のAzure配置・実機試験は未実施です。以下の既存コード基準・試験件数・HEADは過去のチェックポイントで、今回の差分の検証結果とは区別します。

今回の関連ローカル試験はReleaseの `WorkflowAdmissionTests` + `WorkflowHttpTests` 96/96件成功です。初回読取りを同期した並行競合、旧受付の移行・拒否、通知失敗後の再送、所有者フィールド欠落時の安全な500を含みます。これはFake/Mockによる試験であり、Azure BlobやDurable両バックエンドの実機保証ではありません。

今回の最終検証はsolution Releaseの全221件（Poc.Tests219件・DtsProbe2件）が成功し、失敗・スキップは0件です。Release publish（`artifacts/admission-owner-release-publish`）も成功し、`local.settings*` の非混入を確認しました。`git diff --check` は成功し、変更は意図した8ファイルのみで生成物の混入はありません。Azureへの配置・実機検証は今回実施していません。

## 現在の要約（2026-10-18、15関数パッケージAzure実機デプロイおよび全6シナリオ実機回帰・V12利用者キャンセルの完全合格）

最新コードにおける実装は、独自開始URL、HTTP/custom status/明示ログのエラー遮蔽、状態・結果API共通の公開成果物検証、Activity wrapperの安全例外化、gRPC3コード限定、受付と所有者の単一レコード化（`admission.json` Version 2、同一所有者・入力の再送許容、不一致時の403/409調停、旧受付移行・拒否）、`EntraEasyAuth`選択時の認可アダプター（principal解析、tenant/issuer/audience・許可backend client ID・App Role・`idtyp=app`検査）、opt-inのEasyAuth IaC（`workflow_auth_mode`）、VM Managed Identity/IMDSトークンによる独自API認証runner（`Invoke-WorkflowAuthValidation.ps1`）に加え、**V12 利用者キャンセル（Job Cancellation）の実装**（`CancelWorkflow` 追加、計15関数、事前Blob永続化 `runs/poc-<requestId>/cancel.json`、Durable外部イベント `CancelRequested`、Batchジョブ有界停止 `TerminateBatchJobActivity`、成果物 `manifest.json` 公開抑止、所有者認可、冪等200/202）を含めた **15 関数パッケージの Azure 実環境デプロイ（`.deploy/poc/20261018-211336-1471`）および閉域検証 VM 経由での全 6 シナリオ回帰検証（`artifacts/private-validation/20261018-124022-0449/summary.json`、終了コード 0）を全件合格（Pass）** で完了しました。

**Azure 実機検証の到達点**:
- 15関数全件インデックス登録確認（`AggregateActivity`, `CancelWorkflow`, `ClassifyActivity`, `EnsureBatchJobActivity`, `EnsureBatchTaskActivity`, `FetchAndPersistActivity`, `GetBatchTaskStatusActivity`, `GetWorkflowResult`, `GetWorkflowStatus`, `MainOrchestrator`, `MonitorBatchTaskOrchestrator`, `PublishManifestActivity`, `StartWorkflow`, `TerminateBatchJobActivity`, `WorkflowAdmissionEntity`）。
- スモークテスト（3 Tasks並列集約、`Sum=3`）合格（`deploy-27855cc869764f62abf22e94d3e7c2f1`）。
- 閉域検証VM全6シナリオ回帰検証全件Pass（`single`, `three`, `classification`, `failure`, `conflict`, `cancellation`）。
- V12利用者キャンセルがAzure実環境で完全実証（202 Accepted受理、Durable外部イベント調停、Batch Job completed強制終了、タスク exitCode -1073741510停止、cancel.json作成確認、manifest.json非公開確認、完了後 200 OK 冪等確認）。
- 実機検証に伴い、HttpRequestData非シーク可能ストリーム（CanSeek == false）でのBody.Length 500エラー修正、PowerShell strict mode下でのプロパティアクセス改修、Azure CLIパス補正を実施済み。

確定した検証実績:
- Release単体テスト: 全270件（`Poc.Tests` 268件＋`DtsProbe` 2件、失敗・スキップ0件）全件Pass（WorkflowHttpTests 88件、OrchestrationTests 5件）。
- Terraform Mock: `modes.tftest.hcl` 37/37件全Pass、`fmt -check` / `validate` Pass。
- PowerShellオフライン試験: 8組全件Pass（Deployment.Common / Deploy.Workflow / Test-Deployment / PocValidation［cancellation追加］ / PrivateVm / RecoveryValidation / AutoscaleValidation / WorkflowAuthValidation）。
- Functions総数: 15関数（`CancelWorkflow` 追加）。

次の作業の選択肢:
- B案: Entra ID 実テナント設定（専用 PoC API 登録・App Role・`idtyp` claim 設定）・実機での偽造ヘッダー遮断および Bearer 認証実証（`Invoke-WorkflowAuthValidation.ps1` による VM MI 連携）。
- C案: 実 C++ バイナリ連携（`V10`）および Batch `OutputFiles` 大容量出力直送（`T09`）の実機検証。
- あるいは、これまでの実機検証成果（V01〜V09, V12）を総括する PoC 完了報告書・技術評価報告書の作成。以下は日付別の履歴で、過去の試験件数や未実施記述は当時の状態を示します。

## 2026-10-18 ActivityのgRPC再試行分類の限定（ローカル実装）

- Activity側のgRPC再試行対象を `Unavailable` / `DeadlineExceeded` / `ResourceExhausted` に限定し、既存の独自HTTP API分類と揃えました。その他のコードは自動再試行せず、安全な恒久失敗型へ変換します。caller取消の優先、Aggregate内部全件の判定、安全なmessage・innerなし、最大3回・5/10秒・共有deadlineは維持します。
- 3コードの採用は、冪等操作に対する有界再試行のアプリ方針です。期限超過は成功済みの場合もあり、資源枯渇はquota・容量不足を含むため、回復を保証しません。SDKやgRPC通信層の再試行は別の層であり、今回変更していません。根拠: [gRPC status codes](https://grpc.io/docs/guides/status-codes/)、[gRPC retry](https://grpc.io/docs/guides/retry/)。
- 確定したローカル検証: ResilienceTests 56/56件、Release全体のPoc.Tests 209/209件・DtsProbe 2/2件（計211件、失敗・スキップ0件）が成功しました。Release publish（`artifacts/grpc-retry-release-publish`）が成功し、`local.settings*` が含まれないことを確認しました。`git diff --check` も成功し、未追跡ファイル・生成物の混入はありません。Azure配置・実機検証は今回実施していません。

## 2026-10-18 Activity例外のDurable境界での安全化（ローカル実装）

- 全8 Activityが通る `RetryableActivity` で、元例外を既存の条件で分類した後、固定message・innerなしの例外へ変換します。一時障害は `TransientWorkflowException`、timeoutは `TimeoutException`、恒久失敗は `PermanentWorkflowException` とし、caller取消はcaller tokenを持つ安全な `OperationCanceledException` とします。
- 利用SDK 1.24.1の `FailureDetails.IsCausedBy<T>()` はErrorTypeによる型判定であるため、一時障害型とtimeout型を維持します。最大3回・5/10秒待機・共有deadlineとCore gateway内部の例外契約は変更していません。
- 達成範囲はActivity wrapperが外へ渡す例外情報です。Activityの入力・出力、Entity等の別経路、全ランタイムログ・履歴の秘密非露出を保証しません。この時点で残したActivityのgRPC分類見直しは上記の小単位で対応しました。認証境界・所有者競合・Azure検証は残作業です。
- 確定したローカル検証: ReleaseのPoc.Tests 192/192件・DtsProbe 2/2件（計194件）が成功しました。Release publish（`artifacts/activity-boundary-release-publish`）が成功し、`local.settings*` が含まれないことを確認しました。`git diff --check` も成功し、生成物の混入はありません。Azure配置・実機検証は今回実施していません。

## 2026-10-18 V11のHTTPエラー・結果可用性の整合（ローカル実装）

- 独自HTTPのエラー応答と、旧custom statusから読み出すエラーを固定の説明へ統一しました。orchestratorが書くcustom status・監視結果・明示的な失敗ログから生の例外messageを除去しました。
- 状態照会と結果取得は同じ公開成果物検証を使用します。manifest・aggregate・全Task結果の固定パス、識別子、fingerprint、件数・合計、分類等を照合し、欠損は取得不可、不整合は安全な500、照会の一時障害は503とRetry-Afterで返します。manifestに記載された外部URIは辿りません。
- 検証は現行の整数1専用PoC契約内の整合性確認です。複数Blobの原子的スナップショット、状態照会後の存在、権限を持つ改竄者への耐性を保証しません。
- 当時はSDK例外とinner exceptionのDurable境界での安全化が未対応でした。後続のActivity wrapper安全化で対応した範囲は上記節を参照してください。全ログ・全履歴での秘密非露出は断言せず、認証境界・所有者競合対策とAzure検証は引き続き残作業です。
- 確定したローカル検証: ReleaseのPoc.Tests 191/191件・DtsProbe 2/2件（計193件）が成功しました。Release publish（`artifacts/api-contract-release-publish`）が成功し、`local.settings*` が含まれないことを確認しました。`git diff --check` も成功し、生成物の混入はありません。Azure配置・実機検証は今回実施していません。

## 2026-10-18 V11開始応答の独自URL契約（ローカル実装）

- 開始202の本文 `statusQueryGetUri` と `Location` を独自状態照会 `/api/workflows/{requestId}` に統一し、`Retry-After: 10` を返します。利用者向け開始応答からDurable管理URLとキーを除去します。
- 内部検証runnerは開始URLを検証した後、既存のsystem key取得権限を使い管理URIをメモリ内で生成して詳細診断を継続します。管理URI・キーを証跡へ保存しません。開始専用Function keyを別Functionへ流用しません。
- PowerShellオフライン試験4組（Test-Deployment / PocValidation / RecoveryValidation / Deploy.Workflow）が成功しました。独自開始URLのquery・fragment・userinfo・異origin・HTTP・管理APIパス・別requestId・不正URIを拒否し、内部診断が別途取得したsystem keyを使うことを確認しました。Function登録の期待一覧は14件です。Fake試験でありAzure接続は行っていません。
- V11は未完です。Entra認証・App Role、所有者/組織の信頼境界、所有者なし旧受付との競合、他の応答・例外の安全化、および最新コードのAzure検証を引き続き行います。実装順序は [次期実装計画](next-implementation-plan.md) を参照してください。
- ローカル検証: ReleaseのPoc.Tests 148/148件・DtsProbe 2/2件が成功しました。Release publish（`artifacts/api-response-release-publish`）も成功し、`local.settings.json` が含まれないことを確認しました。Azureへの配置・実機検証は今回実施していません。

## 2026-10-18 カテゴリ A（追加の耐障害性・運用系検証：`V05` / `V07` / `V08` / `V09`）の実装・Azure 実機検証完了

- **権限反映**: `dts-poc` および `poc` 両環境の検証 VM 用カスタムロール（`azurerm_role_definition.validation_function`）に対して `Microsoft.Web/sites/host/listkeys/action` の targeted `terraform apply`（各 1 件 update、追加・削除 0 件）を実施し、実機ロール定義への反映を確認しました。
- **実機で判明したスクリプト修正**:
  1. 検証 VM ゲスト側バンドル内で `Invoke-PocRecoveryValidation.ps1` を dot-source する際、ホスト専用の `Invoke-PrivateVmOperation.ps1` が存在しない環境でも読み込めるよう存在確認ガードを追加しました。
  2. `PrivateVm.Worker.ps1` において、`Test-Deployment.ps1` の `[ValidateSet('Pool','Functions','All')][string]$Phase` が残るスコープ上で `$phase` に `'Start'` / `'Complete'` を代入していた変数名衝突を `$recoveryPhase` へ変更して解消しました。
  3. `Get-PocWorkflowSettings`（`Invoke-PocValidation.ps1`）で使用していた `az functionapp config appsettings list` が補助的に `GET .../config/slotConfigNames`（`sites/config/read` 権限が必要）を呼び出して 403 となっていたため、既存の最小権限（`sites/config/list/action`）のみで動作する ARM `POST .../config/appsettings/list` 直接呼び出しへ修正しました。
  4. `Invoke-PocAutoscaleValidation.ps1` において、Azure Batch REST API の `--body` に `@file` 形式を使用してシェルエスケープ問題を解消するとともに、`enableautoscale` 実行後の `allocationState == 'steady'` 待機および 1 回の `enableautoscale` + `evaluateautoscale` による 0 台待機確認フローへ整理しました。
- **Function App 再起動復旧の実機成功（`V05`：DTS / Azure Storage 両バックエンド）**: `dts-poc` 環境（証跡: `artifacts/reliability/dts-recovery/`）および `poc` 環境（Azure Storage バックエンド、証跡: `artifacts/reliability/storage-recovery/`）の双方に対して `scripts/Invoke-PocRecoveryValidation.ps1`（`-DelaySeconds 300 -TimeoutSeconds 900`）を実行し、1 Task が Batch 上で `running` かつ Durable オーケストレーションが `Running` であることを VM から確認後、操作者権限で Function App を `restart`（および `finally` で `start` 復元）しました。再 POST なしで同一 `instanceId`・同一作成時刻・同一 Batch Task（`task-00`）のまま `Completed` / `Succeeded` に至り、`aggregate.json` / `manifest.json` / `tasks/task-00/result.json` の整合性を確認しました（双方 `ExitCode=0`）。
- **監視期限超過（`timeout`）の実機成功（`V07`：DTS / Azure Storage 両バックエンド）**: `dts-poc` 環境（証跡: `artifacts/reliability/dts-timeout/`）および `poc` 環境（証跡: `artifacts/reliability/storage-timeout/`）の双方に対して `scripts/Invoke-PrivatePocValidation.ps1 -Scenarios timeout -DedicatedEnvironment -TimeoutSeconds 1200` を実行し、監視期限（15 分）超過後に `runtimeStatus=Failed`・`businessState=TimedOut` となること、Batch Job の `maxWallClockTime=900` 秒が設定値と一致し Job 状態が `completed`（Task 強制終了）へ遷移すること、および成功 `manifest.json` が公開されないことを確認しました（双方 `ExitCode=0`）。
- **Function ホスト停止中の Batch `MaxWallClockTime` 安全網の実機成功（`V08`）**: `scripts/Invoke-PocRecoveryValidation.ps1` に `-ValidationMode HostStoppedBatchWallClock -AllowFunctionStop` を実装し、実機で検証しました（証跡: `artifacts/reliability/dts-host-stopped-wallclock/`）。Batch Task が `running` に入った直後に Function App を `az functionapp stop` で停止（`Stopped` 確認）し、Function ホストが停止したままの状態で Batch 側の `MaxWallClockTime`（900 秒）期限到達により Job が `completed`（`batchTerminationReason='MaxWallClockTimeExpiry'`、`manifestPublished=false`）へ自動終了すること、および `finally` で Function App が `Running` に復元されることを確認しました（`ExitCode=0`）。
- **0 台オートスケール Pool への先行投入の実機成功（`V09`）**: Terraform IaC（`batch_scale_mode='fixed'|'autoscale'`, `batch_autoscale_evaluation_interval='PT5M'`、main Mock 30 件 Pass）および `scripts/Invoke-PocAutoscaleValidation.ps1` を実装し、`poc` 環境の専用 Pool（`batch_mode='new'`）に対して実機検証を実施しました（証跡: `artifacts/reliability/autoscale-v09/`）。Pool を 0 台 `steady` へ縮小したうえで `$PendingTasks` ベースの Autoscale 式を有効化し、ノード 0 台（`zeroNodesVerifiedBeforeSubmission=true`）の状態から `single` シナリオを投入して Spot 1 台への自動スケールアウト（`scaledUpObserved=true`）と Task 完走（`Sum=1`）、および `finally` での固定 1 台構成への復元（`restoredFixedScaleSucceeded=true`, `restoredPoolReadyVerified=true`）を確認しました（`ExitCode=0`）。
- **DTS 履歴集計の実機確認**: `tools/DtsProbe --history true` を実機 DTS Task Hub に対して実行し、スモーク・標準シナリオ・再起動復旧・timeout の全インスタンスでペイロードを露出させずに `historyObservation`（`scope=ReturnedInstanceHistoryOnly`, `allGenerationsObserved=false`, `executionStartedCount=1`）を取得できることを確認しました（証跡: `artifacts/dts-validation/live-instance-history.json`）。
- **残作業**: カテゴリ B（実 C++ バイナリ・入出力連携 `V10`、大容量出力 `OutputFiles` 直送 `T09`、認証・認可 API `V11`、利用者キャンセル連鎖 `V12`）およびカテゴリ C（全面閉域化・ExpressRoute/VPN 接続 `V00`）、ならびに Spot 回収復旧や一時障害注入試験は後続フェーズの残作業です。

## 2026-10-17 信頼性検証ランナーの追加（ローカル実装）

- **範囲**: 再起動復旧 runner、DTS の現行履歴集計、timeout の Batch 制約照合を追加しました。この時点ではローカル実装・単体試験まで（実機検証は上記 2026-10-18 節で完了）。
- **復旧 runner**: `scripts/Invoke-PocRecoveryValidation.ps1` が非公開 Function の1 Task の Running を VM で確認し、操作者の認証で指定 Function を restart、再送なしで同じ instance / Task の完了・作成時刻・fingerprint・Blob 成果物を照合します。対象の明示、専用試験の明示、観測時刻の鮮度確認、finally での start を実装しました。Entity checkpoint の強制中断は証明しません。
- **権限差分**: 状態確認用の system key をメモリ内で再取得するため、VM の Function スコープのロールに `Microsoft.Web/sites/host/listkeys/action` を追加しました。VM への restart / start 権限は追加していません。管理 URL・キーをバンドルや Blob に保存しません。
- **timeout**: 実際の Job の maxWallClockTime が設定と一致することを必須にし、欠落・不正・不一致を Fail とします。既知の停止理由と制約秒数だけを証跡へ追加しました。Function 停止中の Batch 安全網の実証は別の残作業です。
- **DTS 履歴**: `DtsProbe --history true` で返却履歴のイベント件数を集計します。payload を表示せず、`allGenerationsObserved=false` を明示。確認した SDK / 公開契約では全 executionId の列挙・網羅を保証できないため、admission20 の厳密判定は NotRun を維持します。設計書の一意性を実証済みと読める表現も修正しました。
- **検証**: Terraform main Mock27件・fmt・validate、.NET93件（既存91 + 履歴集計2件）が成功しました。PowerShell の timeout / VM transport / RecoveryValidation の3組も成功。手順は [信頼性の追加検証](reliability-validation.md) を参照してください。

## 2026-10-17 DTS の Azure 配置・実接続検証完了

- **最新状態**: `dts-poc` を Japan East の専用 RG に配置しました。Consumption Scheduler、専用 Task Hub、保持30日、新 Function App、Storage、RHEL 検証 VM を作成しています。段階適用は4件と32件の新規作成で、変更・削除0件です。remote state は既存とは別の `dts-poc.tfstate` です。
- **既存環境との関係**: B1 の追加枠がないため、既存 Linux App Service Plan を参照して共有しています。既存 Azure Storage Function の構成・コードと既存 Batch account / pool は変更していませんが、Plan の CPU・メモリと Batch pool の処理容量は共有します。既存 Plan の参照、新 Function の別 RG 配置、hub スコープの MI Contributor と観測者 Reader を IaC に追加しました。
- **実 Azure 成功**: Private SCM 配置と12 Functions の登録、公開受信無効、新 RHEL VM からの受付、Windows Batch Spot で3 Task、Blob 結果・manifest の整合性（Sum=3）を確認しました。配置結果は `.deploy/dts-poc/20261017-224759-6853/deployment-result.json` です。
- **DTS の独立確認**: 読み取り専用 `tools/DtsProbe` から現在の Azure CLI 認証で hub を直接照会し、`<instance-id-redacted>` が `MainOrchestrator` / `Completed` であることを確認しました。証跡は `artifacts/dts-validation/live-instance.json`。summary の構成値だけによる判定ではありません。
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
- **作成済み**: state 専用 RG `<state-resource-group>` と Storage / Container / backend 権限割当（4リソース）、PoC 専用 RG `<batch-resource-group>` と Batch account（2リソース）。既存 Gallery は参照のみです。実リソース名・ID・設定は Git 管理外の設定と state に保持しています。
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

- **対象リポジトリ**: `<workspace-path>`（Azure Batch + Durable Functions による非同期計算ワークフローの PoC）
- **利用形態**: ユーザーはオンプレミス環境の Web UI からシナリオ投入およびジョブ開始を行う。
- **アプリケーション構成**: 計算処理本体は Windows 計算ノード上で動作する C++ アプリケーション。Web バックエンドは C# が想定されるが未確定。
- **業務領域**: 金融領域のオプションシミュレーション。シナリオ入力と計算結果は Azure Blob Storage 経由で受け渡す（PoC 段階では業務ロジックの詳細には踏み込まず、一般的な非同期計算ワークフローとして扱う）。
- **ネットワーク前提**: オンプレミスと Azure 間は ExpressRoute で接続されており、既存回線を前提に閉域接続（Private Endpoint 等）・DNS・認証・デプロイ経路を整備する。
- **PoC のスコープ**: 既存の C++ 金融計算モデルの再実装はスコープ外。現在の Batch Task は一定時間 `sleep` した後に整数 `1` を出力するダミー処理としている。

## コミットと検証済み範囲

- **今回の作業開始時 HEAD（履歴チェックポイント）**: `9ad401c` — `fix(cancellation): harden restart recovery validation, partial cancel writes, and resolution integrity`。最新状態は常に git log / git status で確認します。
- **今回の変更・実機検証結果**:
  - **B案（Entra ID サービス間認証・認可）Azure 実機プロトタイプ検証（主要プローブ Pass）**:
    - 証跡: `artifacts/private-auth-validation/20261018-160545-4455/summary.json`（終了コード 0、Pass、`CustomApiChecksPassed`）
    - Entra App 登録 `poc-workflow-api`、App Role `Workflow.Execute`、optional claim `idtyp: app`、VM Managed Identity への App Role 割り当て、および Function App への EasyAuth v2（`active_directory_v2`）適用による実機検証を実施。未認証 401 遮断、所有者不整合 403 遮断、正当トークン通過（202/200）を確認。
  - .NET 単体テスト: 285 件 Pass（Poc.Tests 283 件 + DtsProbe 2 件合格、0 Fail / 0 Skip）
  - Release publish: 成功
  - PowerShell オフライン試験: 8 組成功（Autoscale / Deploy.Workflow / Deployment.Common / PocValidation / PrivateVm / RecoveryValidation / Test-Deployment / WorkflowAuthValidation）
  - Terraform Mock 試験: main 37 件 + bootstrap 1 件成功
  - 証跡ログ: `artifacts/private-auth-validation/`、`artifacts/private-validation/`、`artifacts/runner-validation/`、`artifacts/reliability-validation/`、`publish.log`（`artifacts/` は Git 管理外）
- **未検証範囲**: App Role 不足トークン・未許可 Client ID による実機 403 拒否試験、稼働中パッケージの暗号学的同定（`livePackageVerified: false`、デプロイ記述子公称値のみ記録）、DTS 環境での実機回帰、実 C++ バイナリ連携（V10）、Batch `OutputFiles` 大容量出力直送（T09）、Storage/Batch 全面閉域化（V00）。

## 実装済みの改善内容

- **Terraform state backend の事前確認**: `Backend.Create=true` かつ `-PlanOnly` 指定時でも、backend が作成済みであれば main の `terraform plan` まで進行するように改善。また、backend の「未作成（不存在）」と「参照エラー（権限不足や通信失敗）」を明確に区別。
- **Batch Pool のノード数チェックとページネーション**: 既存 Pool 利用時はノード数 0 台（autoscale 待機状態など）でもデプロイを継続可能とし、新規 Pool 作成時は指定台数すべての準備完了を待機するよう実装。ノード一覧取得時の pagination にも対応。
- **同一 `requestId` の排他制御と冪等な受付**: immutable な入力 Blob と Durable Entity を組み合わせ、同一 `requestId` に対する入力内容の競合検出（409）と Orchestrator 起動の直列化を実装。HTTP 202 応答は「Entity への起動通知の受理」を意味し、Main Orchestrator のインスタンス登録が完了するまでの間に状態照会（GET）が 404 を返した場合は、クライアント側で一時待機して再照会する設計としている。
- **HTTP レイヤーの入力検証とエラー応答**: `StartWorkflow` において不正 JSON や無効パラメータに対する 400 Bad Request 応答、Durable クライアントとの通信障害時における 503 Service Unavailable + `Retry-After` 応答、正常受付時の 202 Accepted + Location ヘッダー返却を実装・単体テスト化。
- **Activity のリトライ制御と例外分類**: 一時障害（Transient Fault）のみを対象に最大 3 回試行（Durable タイマー待機 5 秒 / 10 秒）。SDK が投げる `AggregateException` や通信タイムアウトと、呼び出し元によるキャンセル要求を区別し、`CancellationToken` を SDK API まで伝播。
- **親子共有の監視期限（Deadline）と Batch Job 停止**: 親・子 Orchestrator 間で共有する監視期限を導入。期限超過時は Job / Pool / fingerprint / ETag を照合したうえで Batch Job に停止要求を送り、最大 2 分間停止完了を確認する。停止完了を確認できなかった場合は `customStatus` に記録し、Manifest の公開を抑止する。
- **Batch Job の `MaxWallClockTime`**: Durable Functions host が停止している間のセーフティネットとして設定。ただし起算点が「Job 作成時刻」となるため、ワークフロー全体の監視期限や厳密な実時間上限とは役割が異なる。
- **独立検証ランナーの導入**: `scripts/Invoke-PocValidation.ps1` により、インフラ再デプロイを行わずに各種シナリオ（正常系、分類、Task 失敗、20 並列受付、409 競合、タイムアウト停止）を HTTP 経由で独立検証可能化。
- **V12 キャンセル調停・厳格認可・検証スクリプト堅牢化**:
  - **取消確定と結果公開の排他調停**: `resolution.json`（PutIfAbsentAsync による条件付き書き込み）を単一の勝者決定点とし、成功マニフェスト公開とキャンセル確定の競合を厳密に調停。
  - **厳格な取消認可**: 所有者情報のない旧リクエストに対する別名義での取消要求を拒絶（403 Forbidden）。
  - **取消専用例外の保持とイベント再送**: `WorkflowCancelledException` が汎用例外に潰されるのを防止し、キャンセル通知イベントの送信失敗を検知して適切にハンドリング。
  - **終端状態へのキャンセル要求ハンドリング**: 既に Failed / TimedOut となったジョブに対するキャンセル要求の再送時に無効な 202 を返さないよう保護。
  - **Restart モード復旧検証の厳格化**: `Invoke-PocRecoveryValidation.ps1` の Restart モードにおいて、再起動コマンドの呼び出しだけでなく Azure REST API での `Running` 状態到達を必須確認。
  - **キャンセル部分書き込み障害時の余分な Batch 計算抑止**: `cancel.json` の書き込みが失敗して `resolution.json`（Cancelled）のみが残った場合でも、`ClassifyAsync` および `GetWorkflowStatus` が `resolution.json` のキャンセル状態を認識し、余分な Batch Job 作成・Task監視を即時抑止。
  - **調停レコードの整合性検証**: `WorkflowResolutionRecord.Validate` により、破損・改ざんされた `resolution.json`（不正な Version、RequestId 不一致、不正 Status）を検出し、500 エラー（IntegrityFailure）または致命的例外として即時昇格。
- **V11 Entra ID サービス間認証・認可の Azure 実機検証（B案プロトタイプ到達点）**:
  - **テナント・API 登録構成**: App Registration `poc-workflow-api` を作成し、Application 限定 App Role（`Workflow.Execute`）、optional claim（`idtyp: app`）、identifier URI（`api://<client-id>`）を設定。
  - **Graph API による App Role 割当**: 閉域検証 VM の Managed Identity（`poc-df40481149-validation`）に対し、Microsoft Graph API 経由で `Workflow.Execute` ロールを付与。
  - **EasyAuth v2 のインフラ適用**: Terraform により `poc-df40481149-func` の `auth_settings_v2`（`active_directory_v2`、`unauthenticated_action: Return401`）を有効化し、`WORKFLOW_AUTH_MODE=EntraEasyAuth` を設定。
  - **閉域検証 VM からの実機検証（主要プローブ Pass）**:
    - `anonymous`、`keyOnly`、`forgedPrincipal`、`bearerWithoutKey` の各プローブで確実に HTTP 401 遮断を観測。
    - 所有者欠落および他所有者要求での HTTP 403 遮断を観測。
    - 正当な IMDS Bearer トークン + Function Key による 202 受理、同一再送 202、入力競合 409、ステータスポーリング（`Preparing` / `Running` / `Publishing` / `Succeeded`）、結果取得（200 OK、ハッシュ・フィンガープリント完全整合）を実証（`artifacts/private-auth-validation/20261018-160545-4455/summary.json`）。
    - ※App Role 不足トークン（有効だが `Workflow.Execute` を持たないトークン）や未許可 Client ID による実機 403 拒否試験、および稼働中パッケージの暗号学的同定（`livePackageVerified: false`、デプロイ記述子のみ記録）は実機未検証（単体テストでのみ検証済み）。
  - **データ変換堅牢化**: `ConvertTo-DeploymentHashtable` に文字列・プリミティブ値ガードを追加し、配列内文字列が `[pscustomobject]` キャストで `@{ Length = ... }` ハッシュテーブル化する問題を解消。また、認証ランナーのポーリング許容ステータスに `Preparing` と `Publishing` を追加。
  - **閉域認証実行ランナーの整備**: `scripts/Invoke-PrivateWorkflowAuthValidation.ps1` を新設し、`Invoke-PrivateVmOperation.ps1` / `PrivateVm.Worker.ps1` に `WorkflowAuth` オペレーションを追加。

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
