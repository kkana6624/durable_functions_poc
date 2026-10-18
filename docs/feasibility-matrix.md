# 実現性評価

更新日: 2026-10-17

本ドキュメントでは、Azure Batch と Durable Functions を組み合わせた非同期ワークフローの実現性評価結果を整理します。

ローカル検証（.NET 単体テスト全 93 件［ワークフロー 91 件 + DTS 履歴集計 2 件］、Release ビルドおよび publish、PowerShell オフライン試験、Terraform Mock 27 件 + bootstrap 1 件）はすべて合格（Pass）しています。また、デプロイから分離した独立検証ランナー（`scripts/Invoke-PocValidation.ps1`）および再起動復旧検証ランナー（`scripts/Invoke-PocRecoveryValidation.ps1`）を整備し、正常系・異常系の判定ロジック、機微情報を除外した証跡記録、終了コードの仕様、およびローカル環境での HTTP 20 並行リクエスト送信をオフライン試験で確認済みです。

Azure 実環境においては、従来の **Azure Storage バックエンド環境** と **Durable Task Scheduler（DTS）バックエンド環境** の双方で、インフラと RHEL 9 検証 VM の構築、Private SCM への ZIP 配置、12 個の Functions 登録、および検証 VM から Private Endpoint 経由での 3 Task 投入・結果回収・`Sum=3` の集約が成功しています（Function App の公開アクセス無効化と Private Endpoint 接続の Approved も確認済みです）。標準 6 シナリオは `single` / `three` / `classification` / `failure` / `conflict` の 5 件が Pass、`admission20`（20 並列同時受付）は全世代の起動履歴を外部 API から網羅取得できない制限により `NotRun`（summary 終了コード `2`）で完了しています。

下表では、ローカル検証（Fake / Mock / インメモリ保存）の結果と Azure 実環境での検証状況を明確に分離し、実機で確認済みの基本シナリオと未実証の障害復旧シナリオを区別して記載しています。なお、個人の検証環境と将来の業務環境におけるネットワーク境界については [接続方式と共通ネットワークの設計](network-connectivity-design.md) を、DTS 環境の詳細については [DTS PoC](durable-task-scheduler-poc.md) を参照してください。

- 検証結果の詳細・証跡: [検証結果](test-results.md)、[信頼性の追加検証](reliability-validation.md)
- 設計および改善内容の根拠: [信頼性改善の設計](workflow-reliability.md)

| 検証項目 | 実装 / 評価対象 | ローカル結果 | Azure 実接続結果 | 残る確認・制限事項 |
|---|---|---|---|---|
| F01 Batch 計算実行 | Windows Pool、PoC Job / Task、File API | Pass: ローカル Windows 上で exit 0 / result 1 および exit 42 の動作を確認 | Pass: Spot Windows Pool 上での 3 Task smoke および標準ランナー（single / three / classification / failure）成功 | 実 C++ バイナリの実行および共有 Pool での運用確認は後続フェーズ |
| F02 非同期待機 | 状態照会 Activity + Durable Timer | Pass（Mock）: Timer 待機と期限クランプ（上限補正）を確認 | Pass: 3 Task の完了待機およびホスト再起動後の待機再開成功 | Spot 回収発生時の待機・再キュー確認は別途実施 |
| F03 Fan-out / Fan-in | 子 Orchestrator と `Task.WhenAll` | Pass（Mock）: 3 つの子オーケストレーション起動、全完了待機、失敗時の公開抑止を確認 | Pass: 3 Task 完了後の `Sum=3` 集約成功（Azure Storage / DTS 双方） | 1 台 1 slot 構成では計算自体は順次実行。障害・リプレイ時の一意性は未実証 |
| F04 Replay / 再起動復旧 | Durable バックエンドによる履歴永続化 | Pass（Fake）: 再起動復旧ランナーの制御・照合ロジックをローカル Fake で確認 | Pass: `Invoke-PocRecoveryValidation.ps1` により Batch Task 実行中の Function App 再起動後も再 POST なしで同一インスタンス・同一 Task が完走し成果物が整合することを確認（`dts-poc`） | 特定ワーカープロセスの強制クラッシュや Entity チェックポイント確定前後の瞬間的中断は未検証 |
| F05 異常 / 期限超過 | exit code / failure info 判定、失敗結果、公開抑止 | Pass（Mock）: 失敗・結果欠損時の manifest 公開抑止、期限超過判定を確認 | Pass: `exit 42` の `failure` シナリオおよび監視期限超過の `timeout` シナリオ（`TimedOut`・manifest 非公開）ともに実機 Pass | ファイル欠損・改ざん注入時の実機確認は未実施 |
| F06 Blob 集約 | 小結果保存、sum、条件付き manifest | Pass（Fake / メモリ保存）: `Sum=3` の集計、ワークロード分類、既存成果物の改ざん拒否を確認 | Pass: 3 Task smoke、標準シナリオ、および再起動復旧後の成果物・Manifest 整合性を確認 | 実 Blob 上での直接競合・改ざん注入時の拒否確認は未実施 |
| Activity 冪等性 | 決定的 ID、入力照合、条件付き Blob | Pass（Fake / メモリ保存）: べき等な再実行（1 Job / 3 Tasks / 成果物 5 件）、入力競合の検知・拒否を確認 | Partial Pass: Function App 再起動後のリプレイ・監視再開で Task 再作成や成果物不整合が発生しないことを確認 | Job 作成 Activity 実行中の障害注入試験は未実施 |
| 同一 requestId の受付 | 不変（immutable）な入力 Blob ＋ Durable Entity による起動状態管理 | Pass（Mock）: 同時受付の排他制御、入力競合の検知、完了後の再送受付、状態再読み込みを確認 | Partial Pass: `conflict`（409 競合）は実機 Pass、`admission20` は受付・完了確認および `DtsProbe --history true` での現行履歴 `executionStartedCount=1` 確認後 `NotRun` | 全世代の起動履歴取得の制限により厳密な起動一意性は未実証。Entity 受付処理中の中断復旧も未確認 |
| 期限超過時の Batch 停止 | Job 照合・停止要求・完了確認、サーバー側期限制約（`MaxWallClockTime`） | Pass（Mock / Fake）: 停止要求の再実行、自然完了との競合処理、停止失敗時の公開抑止、制約秒数照合を確認 | Partial Pass: 監視期限超過（`timeout`）時に実 Batch Job が強制終了（`completed`）され、`maxWallClockTime=900s` 設定一致と manifest 非公開を実機確認（`dts-poc`） | Function ホストを停止したままにした状態でのサーバー側期限制約（`MaxWallClockTime` 単独）の実機確認は残作業 |
| 一時障害の再試行 | Azure SDK および Durable の有界リトライ、呼び出し元キャンセルとの判別 | Pass（Mock）: リトライ上限回数、全体期限の考慮、恒久エラー（4xx 等）の除外を確認 | Not tested | 実環境における一時的通信障害や API スロットリング時の動作確認が必要 |
| 利用者キャンセル連鎖 | Durable 管理 API からのキャンセルと Batch 連動 | Not tested | Not tested / Gap | P1 検討課題。現行の通常 Task 失敗時は兄弟 Task の自然終了を待機する設計 |
| 本番の大容量結果永続化 | P0 は小さな Task 出力ファイルの File API 経由取得に限定 | Not tested | Not tested / Gap | Batch `OutputFiles`、ノードからの直接 Blob アップロード、修復処理の実装が必要 |
| LSF / HPC Pack の完全代替 | スケジューラ互換、Fair Share、Backfilling | Not tested | Not tested | 本 PoC のスコープ外 |
| 数百台性能・価格 / MPI / HA | 本番負荷と障害条件 | Not tested | Not tested | 本 PoC のスコープ外 |

## 評価まとめ

- **Durable Functions への責務委譲**: 状態遷移、非同期待機の継続、並行処理の待ち合わせ（Fan-in）、および実行履歴からの復旧を Durable Functions（Azure Storage バックエンドまたは Durable Task Scheduler）に委ねることで、独自の状態管理 DB や常駐ポーリング処理、共有カウンタの自前実装を削減できることを確認しました。さらに実 Azure 上において、Batch Task 実行中の Function App 再起動からの無停止自動復旧（`V05`）および監視期限超過時の Batch Job 強制終了・マニフェスト公開抑止（`V07`）まで実証を完了しました。なお、本 PoC は非同期ワークフロー制御の検証を目的としており、LSF や HPC Pack のクラスタスケジューラ機能すべてを代替するものではありません。
- **配置・検証資材の到達点**: Terraform Mock 27 件（+ bootstrap 1 件）と PowerShell オフライン試験により、新規 / 既存 Batch 切り替え、Spot ノード対応、DTS バックエンド選択、既存 App Service Plan の参照、および検証 VM 経由の操作・復旧検証フローをローカルで確認済みです。さらに Azure 実環境においても、インフラ配置、Spot ノードの割り当て、RHEL 9 検証 VM の準備、Private SCM への ZIP 配置、3 Task スモークテスト、標準シナリオ 6 件（`single` / `three` / `classification` / `failure` / `conflict` / `timeout`）の Pass（`admission20` は全世代履歴取得制限により `NotRun`）、および Function App 再起動復旧の Pass まで実証を完了しています（詳細は [検証結果](test-results.md) を参照）。
