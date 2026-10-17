# プロジェクトメモリ

## 2026-10-17 次期実装で確定した前提

- **検証環境の前提**: 本リポジトリは個人の検証環境を対象としています。将来の業務環境では ExpressRoute による閉域接続を想定していますが、個人環境で同一の専用回線・社内 DNS・企業ディレクトリ権限を利用できるとは仮定しません。
- **エンドポイント方針**: 現フェーズでは独立検証ランナー（`Invoke-PocValidation.ps1`）を先行実装し、接続には現行の公開エンドポイントを使用します。VPN 接続またはプロキシとなる小型 WebApp の導入は、後続のネットワーク拡張フェーズで選定・実装します。
- **ネットワーク IaC の分離方針**: 将来のネットワーク IaC では、VNet・Private Endpoint・プライベート DNS と、VPN / ExpressRoute の回線接続部分を明確に分離します。既存の ExpressRoute 共有回線をアプリ側の IaC に直接取り込むことはしません。詳細は [接続設計](network-connectivity-design.md) を参照してください。
- **ステータス確認の原則**: 本ドキュメントの記録は過去のチェックポイント情報を含むため、作業時は必ず現在の Git HEAD、ワーキングツリーの変更状態、および実際のテスト結果を確認してください。なお、Azure 実環境への接続試験は引き続き未実施です。
- **独立検証ランナーの実装**: `scripts/Invoke-PocValidation.ps1` を実装しました。単一 / 3 Task、分類、意図的な exit 42、20 並列受付と再送、異なる入力パラメータによる 409 競合、専用環境でのタイムアウト停止を自動判定し、シークレットを除外した `summary.json` / `summary.txt` と終了コード（0/1/2）を出力します。ただし、現在の管理 API では全世代にわたる Main オーケストレーションの起動履歴を完全には証明できないため、20 並列シナリオは観測値を記録したうえで `NotRun` として扱います。
- **ローカル検証結果**: .NET 単体テスト全 91 件（7 テストクラス、全件 Pass）、Release publish、ランナー オフライン試験（実ローカル HTTP 20 並行送信を含む）、PowerShell 試験 4 組、Terraform Mock 試験 8 件（main 7 件 + bootstrap 1 件）がすべて成功しています。証跡は `artifacts/` 配下に保存されます。実行手順は [README.md](../README.md)、詳細なテスト結果は [検証結果](test-results.md) を参照してください。
- **処理フローの文書化**: 検証ランナーと Azure 側の処理順序、生成される成果物、およびシナリオ別の判定条件は [検証ランナーの処理フロー](validation-runner-flow.md) に記録されています。
- **今後のロードマップ**: 次のステップでは、個人用 Azure 設定および接続方式を確定して現行 PoC の実環境検証を実施し、その後、共通ネットワーク IaC の整備 → 認証・認可付き API の実装 → 実 C++ バイナリの入出力連携へと進みます。なお、検証ランナー自体はインフラ配置、Pool 設定変更、host 停止、権限変更、自動クリーンアップを行いません。

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

- **最新コミット**: `a518050` — `Add comprehensive unit tests for StartWorkflow HTTP layer and AzureBatchGateway`（単体テスト拡充・検証ランナー追加・ネットワーク設計を含む）
- **ローカル検証結果**:
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

1. **閉域ネットワーク・IaC 整備**: ExpressRoute / Private DNS、Function / Blob / host Storage（Blob・Queue・Table）、SCM デプロイ経路、Batch 操作・ノード管理の通信設計と Terraform 実装。
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
