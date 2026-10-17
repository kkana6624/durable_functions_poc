# 実現性評価

更新日: 2026-10-17

本ドキュメントでは、Azure Batch と Durable Functions を組み合わせた非同期ワークフローの実現性評価結果を整理します。

信頼性改善後のローカル検証（.NET 単体テスト 54 件、Release ビルドおよび publish、PowerShell によるオフライン試験 3 組、Terraform Mock 8 件）はすべて合格（Pass）しています。また、独立した検証 runner（`scripts/Invoke-PocValidation.ps1`）を整備し、正常系・異常系の判定ロジック、機微情報を除外した証跡記録、終了コードの仕様、およびローカル環境での HTTP 20 並行リクエスト送信をオフライン試験で確認済みです。

一方、Azure 実環境への接続検証は未実施のため、下表ではローカル検証（Fake / Mock / メモリ保存）の結果と Azure 実環境での検証状況を明確に分離して記載しています。なお、Azure Functions 上で全世代のオーケストレーション起動履歴を外部 API から網羅的に取得できない制約があるため、同時受付時における厳密な起動一意性については、実機検証でも「未実証（NotRun）」として扱う方針です。個人の検証環境と将来の業務環境における接続境界については、[共通ネットワーク設計](network-connectivity-design.md) を参照してください。

- 検証結果の詳細・証跡: [検証結果](test-results.md)、`artifacts/reliability-validation/dotnet-test.log`、`publish.log`
- 設計および改善内容の根拠: [信頼性改善の設計](workflow-reliability.md)

| 検証 | 実装 / 評価対象 | ローカル結果 | Azure 実接続結果 | 残る確認・制限 |
|---|---|---|---|---|
| F01 Batch 計算実行 | 既存 Windows Pool、PoC Job / Task、File API | Pass: ローカル Windows 上で exit 0 / result 1 および exit 42 の動作を確認 | Not tested | SDK 認証、権限、PowerShell 5.1 と quoting を実 Pool で確認 |
| F02 非同期待機 | 状態照会 Activity + Durable Timer | Pass（Mock）: Timer 待機と期限クランプ（上限補正）を確認 | Not tested | 実 Function App host 上での永続待機と実行履歴の確認が必要 |
| F03 Fan-out / Fan-in | 子 Orchestrator と `Task.WhenAll` | Pass（Mock）: 3 つの子オーケストレーション起動、全完了待機、失敗時の公開抑止を確認 | Not tested | 実環境での 3 Task 並行監視と 1 回限りの結果集約を確認 |
| F04 Replay / 再起動復旧 | Durable storage による履歴永続化 | Not tested | Not tested | Function App host の再起動による中断・復旧試験が必要 |
| F05 異常 / 期限超過 | exit code / failure info 判定、失敗結果、公開抑止 | Pass（Mock）: 失敗・結果欠損時の manifest 公開抑止、期限超過判定を確認 | Not tested | 実環境での非ゼロ終了 Task の検知と期限超過時の挙動確認が必要 |
| F06 Blob 集約 | 小結果保存、sum、条件付き manifest | Pass（Fake / メモリ保存）: sum=3 の集計、ワークロード分類、既存成果物の改ざん拒否を確認 | Not tested | 実 Blob Storage での条件付き書き込みと成果物 URI の整合性を確認 |
| Activity 冪等性 | 決定的 ID、入力照合、条件付き Blob | Pass（Fake / メモリ保存）: べき等な再実行（1 Job / 3 Tasks / 成果物 5 件）、入力競合の検知・拒否を確認 | Not tested | 実環境でのインフラ障害注入やリプレイ時の検証が必要 |
| 同一 requestId の受付 | 不変（immutable）な入力 Blob ＋ Durable Entity による起動状態管理 | Pass（Mock）: 同時受付の排他制御、入力競合の検知、完了後の再送受付、状態再読み込みを確認 | Not tested | Entity 状態と Main オーケストレーション履歴の保持が前提。実 host 中断時の復旧確認が必要 |
| 期限超過時のBatch停止 | Job 照合・停止要求・完了確認、サーバー側期限制約 | Pass（Mock）: 停止要求の再実行、自然完了との競合処理、停止失敗時の公開抑止を確認 | Not tested | 実 Batch Job の停止動作および host 停止中におけるサーバー側期限制約の確認が必要 |
| 一時障害の再試行 | Azure SDK および Durable の有界リトライ、呼び出し元キャンセルとの判別 | Pass（Mock）: リトライ上限回数、全体期限の考慮、恒久エラー（4xx 等）の除外を確認 | Not tested | 実環境における一時的通信障害や API スロットリング時の動作確認が必要 |
| 利用者キャンセル連鎖 | Durable 管理 API からのキャンセルと Batch 連動 | Not tested | Not tested / Gap | P1 検討課題。現行の通常 Task 失敗時は兄弟 Task の自然終了を待機する設計 |
| 本番の大容量結果永続化 | P0 は小さな Task 出力ファイルの File API 経由取得に限定 | Not tested | Not tested / Gap | Batch OutputFiles、ノードからの直接アップロード、修復処理の比較評価が必要 |
| LSF / HPC Pack の完全代替 | スケジューラ互換、Fair Share、Backfilling | Not tested | Not tested | 本 PoC のスコープ外 |
| 数百台性能・価格 / MPI / HA | 本番負荷と障害条件 | Not tested | Not tested | 本 PoC のスコープ外 |

## 評価まとめ

- **Durable Functions への責務委譲**: 状態遷移、非同期待機の継続、並行処理の待ち合わせ（Fan-in）、実行履歴からの復旧を Durable Functions に委ねることで、独自の状態管理 DB や常駐ポーリング処理、共有カウンタの自前実装を削減する設計としています。実際のコード削減効果や host 再起動からの復旧動作については、Azure 実接続試験の完了後に最終評価を更新します。なお、本 PoC は非同期ワークフロー制御の検証を目的としており、LSF や HPC Pack のスケジューラ機能すべてを代替するものではありません。
- **ワンコマンドデプロイ資材の検証状況**: Terraform Mock 8 件と PowerShell オフライン試験 3 組により、新規 Batch Account / Pool 作成時の条件分岐、bootstrap state の扱い、リソース配置順序、対象サブスクリプションの固定、任意実行の smoke テスト連携までローカルで確認済みです。Azure 実環境へのデプロイ、計算ノードの割り当て、RBAC 権限の反映、ZIP デプロイについては引き続き `Not tested` です（詳細は[検証結果](test-results.md)を参照）。
