# プロジェクトメモリ

記録日: 2026-10-17。会話から引き継ぐ前提と作業到達点。再開時はGit状態と現行コードを確認する。

## 目的と運用前提

- リポジトリ: `D:\durable_functions_poc`。Azure Batch + Durable Functionsによる非同期計算ワークフローのPoC。
- 利用者はオンプレWebUIからシナリオ投入とジョブ開始を行う。
- 計算アプリはWindows計算ノード上のC++。WebバックエンドはおそらくC#だが未確定。
- 業務は金融領域のオプションシミュレーション。シナリオ入力と結果はBlob経由。業務詳細は一般論で進めてよい。
- オンプレとAzureの接続はExpressRoute。既存回線を前提にprivate接続・DNS・認証・配置経路を整える。
- 既存C++の金融計算モデルを再実装する依頼ではない。現在のTaskはsleep後に整数1を出すダミー処理。

## コミットと検証済み範囲

- 最新の依頼済みコミット: `7c0027b` — `Improve workflow reliability and deployment validation`。24ファイル、実装・テスト・設計・残作業計画を含む。
- ローカル.NETテスト54件成功、Release publish成功、PowerShellオフライン試験3組成功、Terraform Mock main7件+bootstrap1件成功。
- 証跡: `artifacts/reliability-validation/dotnet-test.log`、`publish.log`。artifactsはGit管理外。
- Azure実環境の配置、認証、Entity復旧、実Batch停止は未検証。ローカル成功を実Azure成功として扱わない。

## 実装済みの改善

- backend作成済みなら`Backend.Create=true`の`-PlanOnly`でもmain計画へ進行。存在確認エラーと不存在を区別。
- 既存Poolは0台でもコード配置可能。新規Poolは全指定台数の準備確認、ノード一覧のpagination対応。
- immutable入力BlobとDurable Entityで同一requestIdの入力競合と起動を制御。202はEntity通知受理、Main登録前の状態GET404は一時待機。
- Activity一時障害だけを最大3試行、Durable待機5/10秒。SDKのAggregateExceptionと通信timeout、呼出元cancelを区別し、CancellationTokenをSDKまで伝播。
- 親子共有の監視期限。期限超過時にJob/Pool/fingerprint/ETagを照合して停止し、最大2分で完了確認。停止未確認はcustomStatusへ記録、Manifest非公開。
- Batch Job MaxWallClockTimeはhost停止中の安全網。Job作成時起算で、監視期限や全体の厳密な実時間上限とは異なる。

## 調査で判明した重要な制約

- GetInstance→Scheduleは原子的ではない。重複拒否オプションだけで厳密な同時受付を保証しない。
- SDK1.24.1のEntity起動経路はDedupeStatusesを転送しない。Entity状態と起動Actionのdurable batchを使用する。
- Timerは実行中Activityを物理キャンセルしない。Job作成は後発作成を防ぐため完了を待つ。
- 受付保証は入力Blob・Entity状態・Main履歴保持が前提。個別purge、同IDの直接管理API起動、旧starterとの同時稼働は保証外。
- 通常のFunction再起動だけではEntity checkpoint直前/直後に停止した証明にならない。

## 次の作業

現行計画: `docs/remaining-validation-and-implementation-plan.md`。根拠: `docs/workflow-reliability.md`。結果: `docs/test-results.md`と`docs/feasibility-matrix.md`。

1. ExpressRoute/private DNS、Function/Blob/host Storage(Blob・Queue・Table)、SCM配置経路、Batch操作・node管理の通信設計とIaC。
2. オンプレ向け認証・利用者認可付きstart/status/results/cancel API。ブラウザへFunction keyやDurable管理URLを返さない。
3. 小さなシナリオ1件をBlobへ確定し、固定版C++を実行してBlobへ出力する経路。入力version/ETag/size/SHA-256とアプリversionを記録。
4. 独立検証runner、秘密を除いた証跡、期待した異常を判定する試験を追加。ネットワーク/API実装と並列化可能。
5. 実Azureの同時受付・再送・host復旧・通信障害・監視期限・停止失敗・0台autoscaleを検証。
6. 利用者取消と結果公開の競合、出力永続化、保持・清掃を仕上げる。現在は利用者キャンセルのBatch連鎖は未実装。

実環境設定は現時点でサンプルのみ。Azure接続条件・実設定・実行権限・C++バイナリ/CLI/依存DLL/期待値は実装/実証前に確認する。追加API・閉域IaC・実ファイル計算はまだ計画段階。

## ユーザーの作業希望

公式ドキュメントを参照し、調査専用エージェントを利用する。独立した実装はサブエージェントで並列化し、論理破綻や保証できない点は随時報告する。
