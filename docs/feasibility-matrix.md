# 実現性評価

2026-10-17。信頼性改善後の54単体テスト、Release publish、PowerShellオフライン3組、Terraform Mock8件が成功し、Azure実接続は未実施です。Fakeとメモリ上の保存先による検証を、実Pool / Durable host / Blob serviceによる実証と分けて記録します。証跡は[検証結果](test-results.md)、`artifacts/reliability-validation/dotnet-test.log`と`publish.log`です。変更の根拠は[信頼性改善の設計](workflow-reliability.md)を参照してください。

| 検証 | 実装 / 評価対象 | ローカル結果 | Azure 実接続結果 | 残る確認・制限 |
|---|---|---|---|---|
| F01 Batch 計算実行 | 既存 Windows Pool、PoC Job / Task、File API | Pass: コマンド exit 0 / result 1 と exit 42 を実 Windows で確認 | Not tested | SDK 認証、権限、PowerShell 5.1 と quoting を実 Pool で確認 |
| F02 非同期待機 | 状態照会 Activity + Durable Timer | Pass（Mock）: Timer と期限 clamp | Not tested | host 上の永続待機と履歴を確認 |
| F03 Fan-out / Fan-in | 子 Orchestrator と `Task.WhenAll` | Pass（Mock）: 3 子起動、全子待機、失敗時の非公開 | Not tested | 実3 Taskの並行監視と集約1回を観察 |
| F04 Replay / 再起動復旧 | Durable storage による履歴永続化 | Not tested | Not tested | PoC Function の再起動中断を実施 |
| F05 異常 / 期限超過 | exit code / failure info 判定、失敗結果、公開抑止 | Pass（Mock）: 失敗・欠損時の未公開、期限判定 | Not tested | 実非ゼロ Task と期限超過を観察 |
| F06 Blob 集約 | 小結果保存、sum、条件付き manifest | Pass（Fake / メモリ保存）: sum=3、分類、改ざん拒否 | Not tested | Blob service の条件付き書込と URI を確認 |
| Activity 冪等性 | 決定的 ID、入力照合、条件付き Blob | Pass（Fake / メモリ保存）: 再実行で1 Job / 3 Tasks / 5文書、入力衝突拒否 | Not tested | 実障害・リプレイ試験が必要 |
| 同一 requestId の受付 | immutable入力Blob＋Durable Entityの起動状態 | Pass（Mock）: 同時受付、入力競合、再送、状態再読込 | Not tested | Entity状態とMain履歴保持が前提。実host中断復旧を確認 |
| 期限超過時のBatch停止 | Job照合・停止要求・完了確認、server側期限 | Pass（Mock）: 停止再実行、自然完了競合、停止失敗時未公開 | Not tested | 実Job停止・host停止中の期限制約を確認 |
| 一時障害の再試行 | SDKとDurableの有界retry、呼出元cancel区別 | Pass（Mock）: 上限・期限・恒久エラー除外 | Not tested | 実サービスの通信障害・スロットリングを確認 |
| 利用者キャンセル連鎖 | Durable管理APIからのキャンセルとBatch連動 | Not tested | Not tested / Gap | P1。通常Task失敗では兄弟の自然終了を待つ |
| 本番の大容量結果永続化 | P0 は小さなローカル Task ファイルの転送 | Not tested | Not tested / Gap | OutputFiles / 直接アップロード / 修復を比較 |
| LSF / HPC Pack の完全代替 | スケジューラ互換、Fair Share、Backfilling | Not tested | Not tested | 今回の非スコープ |
| 数百台性能・価格 / MPI / HA | 本番負荷と障害条件 | Not tested | Not tested | 今回の非スコープ |

Durable に委ねる候補は状態遷移、待機の継続、並行処理の合流、履歴による復旧です。これらにより独自状態 DB、常駐ポーラ、共有カウンタの実装を減らす設計ですが、削減効果と再起動復旧の実証は実接続試験後に結論を更新します。PoC 成功だけでは LSF/HPC Pack の完全置換を主張できません。

ワンコマンド配置の追加資材はTerraform Mock 8件とPowerShellオフライン3組が成功しています。新規Batch Account/Pool作成の分岐、bootstrap state、配置順、対象subscription固定、任意smokeへの接続をローカルで確認しました。Azureへの実配置とノード割当、権限反映、ZIP配置は引き続きNot testedです。詳細は[検証結果](test-results.md)を参照してください。
