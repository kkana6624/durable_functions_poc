# 次期実装計画

確認日: 2026-10-19。最新状態: A案（15関数パッケージのデプロイおよび全6シナリオ実機回帰検証・V12利用者キャンセル実証）完了、ならびに **B案（Entra ID サービス間認証・認可の Azure 実機プロトタイプ検証）** 完了（Pass）。実機検証済みデプロイ基準: `.deploy/poc/20261018-211336-1471`（15関数・スモークテスト合格: `deploy-27855cc869764f62abf22e94d3e7c2f1`、閉域検証VM実機回帰: `artifacts/private-validation/20261018-124022-0449/summary.json`、全6シナリオPass）、および `.deploy/poc/20261019-001001-3480`（EasyAuth v2 有効化、閉域検証VM認証検証: `artifacts/private-auth-validation/20261018-160545-4455/summary.json`、終了コード 0、Pass）。

## 現在の到達点

コミット `e1d04d6`、`27b24b4`、`b14b25f`、V12実装、**Azure実機デプロイ・閉域VM回帰検証（A案）**、および **B案（Entra ID サービス間認証・認可の Azure 実機検証）** により、受付・所有者の一体化、EasyAuth認可アダプター、opt-in EasyAuth IaC、独自API認証runner、**V12 利用者キャンセル（Job Cancellation）の実機実証を含む全6シナリオの実機回帰検証**、ならびに **Entra サービス間認証・認可プロトタイプ実機検証** が合格（終了コード 0）しています。

- **15関数パッケージのAzureデプロイとスモークテスト合格**:
  - `scripts/deploy.ps1 -Environment poc -ConfigFile ./environments/poc.psd1 -Smoke` を実行（ログ: `.deploy/poc/20261018-211336-1471/`）。
  - インフラ差分なし（0 add / 1 change / 0 destroy）、`CancelWorkflow` を含む全15関数がFunction App上に正常インデックス登録されたことを確認。
  - 3 Tasks並列集約スモークテストが合格（`requestId=deploy-27855cc869764f62abf22e94d3e7c2f1`、Sum=3）。
- **閉域検証VMからの実機回帰試験（全6シナリオPass）**:
  - 閉域検証VMから `scripts/Invoke-PrivatePocValidation.ps1` を実行（証跡: `artifacts/private-validation/20261018-124022-0449/summary.json`、終了コード 0）。
  - `single`: Pass（単一Task投入・正常完了・Sum=1）
  - `three`: Pass（3 Tasks並列集約・正常完了・Sum=3）
  - `classification`: Pass（light/heavy/auto軽重分類・正常完了）
  - `failure`: Pass（非ゼロ終了42・Failed確定・マニフェスト非公開）
  - `conflict`: Pass（別入力409 Conflict・同一入力同一所有者冪等性）
  - `cancellation`: Pass（**V12 利用者キャンセル実機完全実証**）
- **B案: Entra ID サービス間認証・認可の Azure 実機検証（Pass）**:
  - App 登録 `poc-workflow-api`、App Role `Workflow.Execute`、optional claim `idtyp: app`、VM Managed Identity への App Role 付与、Function App の EasyAuth v2（`auth_settings_v2`, `active_directory_v2`）有効化。
  - 閉域検証 VM から `scripts/Invoke-PrivateWorkflowAuthValidation.ps1` を実行（証跡: `artifacts/private-auth-validation/20261018-160545-4455/summary.json`、終了コード 0、Pass）。
  - 401 遮断（anonymous, keyOnly, forgedPrincipal, bearerWithoutKey）および 403 遮断（所有者・組織欠落／不整合）を確認。正規トークン（`Workflow.Execute` 付与）＋Function Key による 202/200 完走を確認。
- **実機検証に伴う不具合修正と堅牢化**:
  - `WorkflowFunctions.cs`: `CancelWorkflow` において HttpRequestData の非シーク可能ストリーム（`CanSeek == false`）に対する `request.Body.Length` 呼び出しによる 500 エラーを修正（`CanSeek` ガードおよび `JsonException` 安全ハンドリング）。
  - `Invoke-PocValidation.ps1`: `Set-StrictMode -Version Latest` 環境下で `$status.customStatus.termination` や `terminationErrorCode` が null / 未定義の際にスクリプト停止例外が発生する問題を改修（`PSObject.Properties` によるプロパティ存在確認と安全なアクセス）。また、CancelWorkflow 専用キー取得フォールバックを追加。
  - `Invoke-PrivatePocValidation.ps1` および `Deployment.Common.ps1`: Azure CLI（`az.cmd`）の標準インストールパスへのフォールバックおよび PATH 環境変数解決を整備。
  - `Deployment.Common.ps1`: `ConvertTo-DeploymentHashtable` の文字列配列型判定バグを修正（プリミティブ／文字列ガード）。
  - `Invoke-WorkflowAuthValidation.ps1`: ポーリング許容状態に `Preparing` / `Publishing` を追加。
- **検証実績**: Release 単体テスト全 285 件（`Poc.Tests` 283 件＋`DtsProbe` 2 件、失敗・スキップ 0 件）、Terraform Mock 38 件、PowerShell オフライン試験 8 組全件が合格。
- **未実施・前提事項**:
  - 15 関数コードの Azure Storage バックエンド（`poc` 環境）での実機回帰および V12 実証は完了。DTS（Durable Task Scheduler）バックエンド環境での 15 関数実機回帰は未実施。
  - Entra 実機検証において、App Role 不足トークン（有効だが `Workflow.Execute` を持たないトークン）や未許可 Client ID による実機 403 拒否試験、および稼働中パッケージの暗号学的同定（`livePackageVerified: false`）は未検証。
  - 実 C++ バイナリ連携（V10）および Batch `OutputFiles` 大容量出力直送（T09）は未実証。

## 優先順序と完了条件

| 順序 | 実装単位 | 完了条件 |
|---|---|---|
| 1 | V11: 受付・所有者の一体化と旧レコードの扱い | **【ローカル完了・実機回帰Pass】** 単一の不変受付レコード（`admission.json` v2）と旧受付移行・拒否条件をローカル検証。実機回帰試験（conflict シナリオ）にて 409 競合調停・冪等再送の実機動作を確認完了 |
| 2 | V11: 認証境界（B案） | **【Azure 実機プロトタイプ検証完了（Pass）】** 信頼されたバックエンドの Entra 認証・App Role・`idtyp=app` 検証アダプター、opt-in EasyAuth IaC、VM MI 用認証 runner（`Invoke-WorkflowAuthValidation.ps1`）を実機検証（証跡: `artifacts/private-auth-validation/20261018-160545-4455/summary.json`）。※App Role 不足トークンによる実機拒否試験および稼働パッケージ同定は未検証 |
| 4 | V12: 利用者キャンセル | **【Azure 実機検証完了（Pass）】** 要求永続化、冪等 cancel API（Functions 計15件）、Durable 外部イベント、Batch 有界停止、取消と manifest 公開の排他調停、所有者認可、runner シナリオ追加を実装完了。閉域 VM からの実機検証にて 202 受理、cancel.json 存在、Batch completed、manifest 非公開、冪等 200 を確認し完全実証 |
| 3 | V11/V12: 15関数コードのAzure配置・閉域VM実機回帰（A案） | **【Azure 実機検証完了（Pass）】** 15 関数コードの Azure 配置（インフラ差分なし、15 関数登録、スモークテスト合格: `deploy-27855cc869764f62abf22e94d3e7c2f1`）および閉域 VM からの実機全 6 シナリオ回帰検証（`artifacts/private-validation/20261018-124022-0449/summary.json`、終了コード 0）が合格 |
| 次の焦点 | 発展的検証（C案 / 認証厳格化）または PoC 完了報告 | **【次なる作業候補】**<br>・**C案**: 実 C++ バイナリ連携（`V10`）および Batch `OutputFiles` 大容量出力直送（`T09`）の実機検証<br>・**V11 認可厳格化**: App Role 不足トークンによる実機 403 拒否試験、稼働パッケージ暗号学的同定<br>・**DTS 環境回帰**: 15 関数コードおよびキャンセル・認証の DTS 実機回帰<br>・あるいは、これまでの実機検証成果（V01〜V09, V11, V12）を総括する **PoC 完了報告書・技術評価報告書** の作成 |
| 5 | V10/T09: ファイル入出力 | 入力確定、Blob version/ETag/size/SHA-256 とバイナリ版を実行契約へ追加。固定バイナリ、ResourceFiles、OutputFiles 直送を導入し、転送成功とハッシュ照合後だけ manifest 公開 |
| 6 | V00・運用検証 | Storage/Batch 閉域化、DNS・送信経路・SCM 経路、オンプレ接続を段階検証。その後 Spot 回収・一時障害注入・保持/清掃を拡充 |

## 実装済みの境界と次の実装

開始202の本文 `statusQueryGetUri` と `Location` は独自 `/api/workflows/{requestId}` に統一済みで、`Retry-After: 10` を維持しています。内部runnerはこの契約を検証後、system keyから管理URIをメモリ内で生成して詳細診断を継続します。独自状態APIへの全面移行は各Functionの認証設計と併せて進めます。開始専用Function keyを別Functionへ流用しません。

続くローカル実装でHTTP/旧custom statusのエラー遮蔽、明示ログのコード化、状態・結果API共通の公開成果物検証を追加しました。さらにActivity wrapperでは、再試行分類とtimeout型を維持し、固定message・innerなしの例外を外へ渡します。gRPC分類はHTTP/Activityとも `Unavailable` / `DeadlineExceeded` / `ResourceExhausted` に限定しました。これは冪等操作への有界再試行方針であり、回復保証ではありません。SDK層の再試行は別の層で、今回変更していません。他のデータ経路の機密情報対策、全ランタイムログ・履歴の評価、認証、Azure検証を残します。成果物検証は照会時点の整合性であり、複数Blobのトランザクションや後続取得成功を保証しません。

コミット `e1d04d6` では `WorkflowAdmission.cs` の受付と所有者の永続化を単一レコードへまとめました。同一入力・同一所有者の再送、異なる入力・所有者・組織の競合、所有者なし旧受付の扱い、確定前後の再試行をローカル検証済みです。続くコミット `27b24b4` で認証アダプターを接続し、`b14b25f` で opt-in EasyAuth IaC および独自 API 認証 runner を追加しました。

単一レコード化は、別Blobだった所有者と受付の確認・claimの交差を新Starter間で防ぎます。現PoCでは所有者なし受付を維持し、所有者必須化は後続の認証済みAPI契約で検討します。旧レコードの移行は旧Starter停止後を前提とし、任意利用者による旧ジョブの所有権取得を許しません。

互換`PocFunctionKey`モードで任意の `X-Poc-Owner-Id` / `X-Poc-Org-Id` を直接使う経路は利用者認証になりません。`EntraEasyAuth`モードは認可済みバックエンドのみから必須owner/orgを受け取り、その値を利用者セッションへ正しく結び付ける責任をバックエンドに委ねます。企業認証基盤・ID空間の確定、トークン登録・EasyAuth配置と実機拒否検証は引き続き必要です。

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
