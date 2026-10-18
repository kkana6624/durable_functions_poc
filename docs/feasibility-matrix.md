# 実現性評価

更新日: 2026-10-19

本ドキュメントでは、Azure Batch と Durable Functions を組み合わせた非同期ワークフローの実現性評価結果を整理します。

ローカル検証（.NET単体テスト全290件（Poc.Tests 288件 + DtsProbe 2件、内 ResilienceTests 56件）全件Pass、Release ビルドおよび publish、PowerShell オフライン試験 8 組、Terraform Mock 37 件 + backend 1 件）はすべて合格（Pass）しています。また、デプロイから分離した独立検証ランナー（`scripts/Invoke-PocValidation.ps1`）、再起動復旧・ホスト停止安全網検証ランナー（`scripts/Invoke-PocRecoveryValidation.ps1`）、0 台オートスケール Pool 検証ランナー（`scripts/Invoke-PocAutoscaleValidation.ps1`）、および Entra 認証検証ランナー（`scripts/Invoke-WorkflowAuthValidation.ps1`）を整備し、正常系・異常系の判定ロジック、機微情報を除外した証跡記録、終了コードの仕様、およびローカル環境での HTTP 20 並行リクエスト送信をオフライン試験で確認済みです。

Azure 実環境においては、従来の **Azure Storage バックエンド環境** と **Durable Task Scheduler（DTS）バックエンド環境** の双方で、インフラと RHEL 9 検証 VM の構築、Private SCM への ZIP 配置、当時の 12 個の Functions 登録、3 Task 投入・結果回収・`Sum=3` の集約、標準 6 シナリオ（`single` / `three` / `classification` / `failure` / `conflict` / `timeout`）の Pass、および Function App 再起動復旧（`V05`）の Pass を確認しています（`admission20`［20 並列同時受付］は全世代の起動履歴を外部 API から網羅取得できない制限により `NotRun`、summary 終了コード `2`）。さらに、Function ホスト停止中の Batch `MaxWallClockTime` 安全網による自動停止（`V08`）および 0 台オートスケール Pool への先行投入・自動拡張完走・固定スケール復元（`V09`）も実 Azure 上で Pass を確認済みです。
最新の **Azure Storage バックエンド環境** では、`CancelWorkflow` を含む **15 関数パッケージ** のデプロイ（`.deploy/poc/20261018-211336-1471`、スモーク合格: `deploy-27855cc869764f62abf22e94d3e7c2f1`）および閉域検証 VM からの全 6 シナリオ回帰検証（`artifacts/private-validation/20261018-124022-0449/summary.json`）により、**V12 利用者キャンセル（Job Cancellation）の実機実証を含む全 6 シナリオ全件 Pass** を達成しました。さらに、EasyAuth v2 を有効化した環境（`.deploy/poc/20261019-001001-3480`）において、**B案 Entra ID サービス間認証・認可のプロトタイプ実機検証**（`artifacts/private-auth-validation/20261018-160545-4455/summary.json`）も合格（Pass）しています（なお、DTS バックエンド環境での 15 関数コード回帰は未実施です）。

下表では、ローカル検証（Fake / Mock / インメモリ保存）の結果と Azure 実環境での検証状況を明確に分離して記載しています。なお、個人の検証環境と将来の業務環境におけるネットワーク境界については [接続方式と共通ネットワークの設計](network-connectivity-design.md) を、DTS 環境の詳細については [DTS PoC](durable-task-scheduler-poc.md) を参照してください。

- 検証結果の詳細・証跡: [検証結果](test-results.md)、[信頼性の追加検証](reliability-validation.md)
- 設計および改善内容の根拠: [信頼性改善の設計](workflow-reliability.md)

| 検証項目 | 実装 / 評価対象 | ローカル結果 | Azure 実接続結果 | 残る確認・制限事項 |
|---|---|---|---|---|
| F01 Batch 計算実行 | Windows Pool（固定 / 0台Autoscale）、PoC Job / Task、File API | Pass: ローカル Windows 上で exit 0 / result 1 および exit 42、Terraform/PowerShell で 0 台 Autoscale 構成を確認 | Pass: Spot Windows Pool 上での 3 Task smoke、標準ランナー、および 0 台 Autoscale Pool への先行投入・自動拡張完走（`V09`）が成功 | 実 C++ バイナリの実行および共有 Pool での運用確認は後続フェーズ |
| F02 非同期待機 | 状態照会 Activity + Durable Timer | Pass（Mock）: Timer 待機と期限クランプ（上限補正）を確認 | Pass: 3 Task の完了待機、0 台 Autoscale 起動待機（約 7 分半）、およびホスト再起動後の待機再開成功 | Spot 回収発生時の待機・再キュー確認は別途実施 |
| F03 Fan-out / Fan-in | 子 Orchestrator と `Task.WhenAll` | Pass（Mock）: 3 つの子オーケストレーション起動、全完了待機、失敗時の公開抑止を確認 | Pass: 3 Task 完了後の `Sum=3` 集約成功（Azure Storage / DTS 双方） | 1 台 1 slot 構成では計算自体は順次実行。障害・リプレイ時の一意性は未実証 |
| F04 Replay / 再起動復旧 | Durable バックエンドによる履歴永続化 | Pass（Fake）: 再起動復旧ランナーの制御・照合ロジックをローカル Fake で確認 | Pass: `Invoke-PocRecoveryValidation.ps1` により Batch Task 実行中の Function App 再起動後も再 POST なしで同一インスタンス・同一 Task が完走し成果物が整合することを確認（Azure Storage / DTS 双方、`V05`） | 特定ワーカープロセスの強制クラッシュや Entity チェックポイント確定前後の瞬間的中断は未検証 |
| F05 異常 / 期限超過 | exit code / failure info 判定、失敗結果、公開抑止 | Pass（Mock）: 失敗・結果欠損時の manifest 公開抑止、期限超過判定を確認 | Pass: `exit 42` の `failure` シナリオおよび監視期限超過の `timeout` シナリオ（`TimedOut`・manifest 非公開、`V07`）ともに両バックエンドで実機 Pass | ファイル欠損・改ざん注入時の実機確認は未実施 |
| F06 Blob 集約 | 小結果保存、sum、条件付き manifest | Pass（Fake / メモリ保存）: `Sum=3` の集計、ワークロード分類、既存成果物の改ざん拒否を確認 | Pass: 3 Task smoke、標準シナリオ、および再起動復旧後の成果物・Manifest 整合性を確認 | 実 Blob 上での直接競合・改ざん注入時の拒否確認は未実施 |
| Activity 冪等性 | 決定的 ID、入力照合、条件付き Blob | Pass（Fake / メモリ保存）: べき等な再実行（1 Job / 3 Tasks / 成果物 5 件）、入力競合の検知・拒否を確認 | Partial Pass: Function App 再起動後のリプレイ・監視再開で Task 再作成や成果物不整合が発生しないことを確認 | Job 作成 Activity 実行中の障害注入試験は未実施 |
| 同一 requestId の受付 | 不変（immutable）な入力 Blob ＋ Durable Entity による起動状態管理 | Pass（Mock）: 同時受付の排他制御、入力競合の検知、完了後の再送受付、状態再読み込みを確認 | Partial Pass: `conflict`（409 競合）は実機 Pass、`admission20` は受付・完了確認および `DtsProbe --history true` での現行履歴 `executionStartedCount=1` 確認後 `NotRun` | 全世代の起動履歴取得の制限により厳密な起動一意性は未実証。Entity 受付処理中の中断復旧も未確認 |
| 期限超過時の Batch 停止 | Job 照合・停止要求・完了確認、サーバー側期限制約（`MaxWallClockTime`） | Pass（Mock / Fake）: 停止要求の再実行、自然完了との競合処理、停止失敗時の公開抑止、制約秒数照合、ホスト停止中の `MaxWallClockTime` 検証を確認 | Pass: 監視期限超過（`timeout`、`V07`）時の Batch Job 強制終了・`maxWallClockTime=900s` 一致・manifest 非公開に加え、Function App を `Stopped` にした状態で Batch 側の `MaxWallClockTime` により `MaxWallClockTimeExpiry` で自動終了すること（`V08`）を実機確認 | 停止要求自体が権限不足等で失敗した場合の異常系注入試験は別途 |
| 一時障害の再試行 | Azure SDK および Durable の有界リトライ、呼び出し元キャンセルとの判別、Activity 境界安全例外化、gRPC 対象限定 | Pass（Mock）: リトライ上限回数、全体期限の考慮、恒久エラー（4xx 等）の除外、Activity 境界での安全例外化（SafeActivityException、固定 message / inner なし）、および gRPC 再試行対象の 3 コード限定（Unavailable / DeadlineExceeded / ResourceExhausted）を確認 | Pass: Azure Storage バックエンドでの 15 関数デプロイ・スモークおよび回帰検証にて Activity 境界での安全例外化・有界再試行が動作することを確認 | DTS 環境での 15 関数最新コード回帰は未実施 |
| 独自 API 契約・エラー遮蔽 | 開始 202 での独自 status URL 返却、Retry-After: 10s、Durable 管理 URL 遮蔽、HTTP / custom status エラー遮蔽 | Pass（Mock / Fake）: 開始 202 本文・Location の独自 URL 返却（/api/workflows/{requestId}）、Durable 管理 URL 非露出、固定エラーメッセージ遮蔽を確認 | Pass: 実 Azure 閉域 VM からの回帰検証および認証検証にて独自 API 疎通、管理 URL 非露出、401/403/409 遮断、ステータスポーリング整合性を実機確認 | DTS 環境での独自 API 実機回帰は未実施 |
| 公開成果物検証 | manifest / aggregate / 全 Task 結果の存在・内容整合性検証（状態・結果照会共通） | Pass（Mock / Fake）: 成果物欠損時の取得不可（409 / resultAvailable=false）、不整合時の安全な 500、一時障害時の 503 + Retry-After: 10 判定を確認 | Pass: 実 Azure 閉域 VM からの各シナリオおよび認証検証において、manifest / aggregate / Task 結果の存在・内容整合性検証を実機確認 | 実 Blob 上での成果物直接改ざん注入時の挙動確認は未実施 |
| 利用者キャンセル連鎖 | HTTP 取消 API（`CancelWorkflow`）、事前 Blob 永続化（`cancel.json`）、Durable 外部イベント、Batch Job 有界停止、成果物公開排他調停（`resolution.json`） | Pass（Mock / Fake）: 取消要求永続化、投入停止、Batch 停止確認、取消と manifest 公開の排他調停、所有者認可、停止未確認時の `Cancelling` 遷移と再試行 | Pass: Azure Storage バックエンドにて実機実証（`V12`、202 受理、cancel.json 先行保存、Batch completed 強制停止、manifest 非公開、完了後 200 冪等確認） | DTS バックエンド環境でのキャンセル回帰、および Spot 回収と取消の同時発生時の動作確認は後続課題 |
| 本番の大容量結果永続化 | P0 は小さな Task 出力ファイルの File API 経由取得に限定 | Not tested | Not tested / Gap | Batch `OutputFiles`、ノードからの直接 Blob アップロード、修復処理の実装が必要 |
| LSF / HPC Pack の完全代替 | スケジューラ互換、Fair Share、Backfilling | Not tested | Not tested | 本 PoC のスコープ外 |
| 数百台性能・価格 / MPI / HA | 本番負荷と障害条件 | Not tested | Not tested | 本 PoC のスコープ外 |

## 評価まとめ

- **Durable Functions への責務委譲**: 状態遷移、非同期待機の継続、並行処理の待ち合わせ（Fan-in）、および実行履歴からの復旧を Durable Functions（Azure Storage バックエンドまたは Durable Task Scheduler）に委ねることで、独自の状態管理 DB や常駐ポーリング処理、共有カウンタの自前実装を削減できることを確認しました。さらに実 Azure 上において、両バックエンドでの Function App 再起動からの無停止自動復旧（`V05`）、監視期限超過時の Batch Job 強制終了・マニフェスト公開抑止（`V07`）、Function ホスト停止中の Batch `MaxWallClockTime` 安全網による自動終了（`V08`）、および 0 台オートスケール Pool への先行投入・自動拡張完走（`V09`）まで実証を完了しました。なお、本 PoC は非同期ワークフロー制御の検証を目的としており、LSF や HPC Pack のクラスタスケジューラ機能すべてを代替するものではありません。
- **配置・検証資材の到達点**: Terraform Mock 38 件（`modes.tftest.hcl` 37 件 + `backend.tftest.hcl` 1 件）と PowerShell オフライン試験 8 組、Release 単体テスト全 290 件全件 Pass により、新規 / 既存 Batch 切り替え、Spot ノード対応、固定 / 0 台 Autoscale Pool 切り替え、DTS バックエンド選択、既存 App Service Plan の参照、および検証 VM 経由の操作・復旧・Autoscale・認証検証フローをローカルで確認済みです。さらに Azure 実環境においても、インフラ配置、Spot ノードの割り当て、RHEL 9 検証 VM の準備、Private SCM への ZIP 配置、15 関数の登録、3 Task スモークテスト合格（`deploy-27855cc869764f62abf22e94d3e7c2f1`）、閉域検証 VM からの全 6 シナリオ回帰（`single` / `three` / `classification` / `failure` / `conflict` / `cancellation`）の全件 Pass（`artifacts/private-validation/20261018-124022-0449/summary.json`）、Function App 再起動復旧（`V05`）、ホスト停止中の Batch `MaxWallClockTime` 安全網（`V08`）、0 台オートスケール Pool 先行投入（`V09`）、ならびに B案 Entra ID サービス間認証・認可プロトタイプ実機検証（`artifacts/private-auth-validation/20261018-160545-4455/summary.json`）の Pass まで実証を完了しています。DTS バックエンド環境での 15 関数およびキャンセル・認証の回帰、App Role 不足トークン等による追加拒否試験、実 C++ バイナリ連携（`V10`）、および大容量ファイル直送（`T09`）は後続課題です（詳細は [検証結果](test-results.md) を参照）。

