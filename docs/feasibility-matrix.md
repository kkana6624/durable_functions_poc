# 実現性評価マトリクス

更新日: 2026-10-19

本ドキュメントは、Azure Batch（Windows ノード）と Durable Functions を組み合わせた非同期計算基盤について、**PoC（概念実証）における技術的実現性の評価結果**を整理したものです。

---

## 1. 総合評価サマリ

本 PoC は、**クラウド基盤としての非同期制御・耐障害性・セキュリティ・閉域化の全主要要件を満たすことを実 Azure 上で実証（合格）**しました。

- **ローカル検証**: .NET 単体テスト全 294 件（失敗・スキップ 0 件）、Terraform Mock 38 件、PowerShell オフライン試験 8 組のすべてが Pass。
- **Azure 実機検証**:
  - **基本・耐障害性**: 15 関数デプロイ、全 6 シナリオ回帰（単一、並列集約、軽重分類、exit 42 失敗、409 競合、利用者キャンセル）、Function 再起動復旧（V05）、タイムアウト監視（V07）、ホスト停止時安全網（V08）、0 台オートスケール（V09）がすべて Pass。
  - **認証・認可 (B案-1)**: EasyAuth v2 による未認証 401 遮断、App Role 不足 403 遮断、所有者認可、稼働パッケージ同定（`livePackageVerified: true`）が Pass。
  - **完全閉域化 (B案-2,3)**: Functions Regional VNet 統合、Batch Private Endpoints、Batch Pool の VNet 統合（`NoPublicIPAddresses`）、Batch Account パブリックアクセス完全遮断下でのワークフロー完走が Pass。
- **次フェーズへの引き継ぎ**: 実業務 C++ 計算バイナリ連携（V10）および大容量出力の Blob 直接アップロード（T09）は、業務アプリ側の仕様確定後に結合開発フェーズで実施。

---

## 2. 機能・非機能要件の実現性マトリクス

下表では、ローカル検証（Fake / Mock）と Azure 実機環境での検証結果を分離して記載しています。

| 評価項目 | 対象機能 / シナリオ | ローカル結果 | Azure 実機結果 | 判定 | 備考・残課題 |
|---|---|---|---|---|---|
| **F01: Batch 計算実行** | Windows Pool（固定 / 0台Autoscale）、Task 投入、File API | Pass (Windows ローカル) | Pass (Spot / Dedicated) | **合格** | 実業務 C++ バイナリ連携は次フェーズ |
| **F02: 非同期待機** | 状態照会 Activity + Durable Timer | Pass (Mock Timer) | Pass (実機タイマーポーリング) | **合格** | 最大 7 分半の 0 台起動待機・再起動後再開を確認 |
| **F03: 並列集約 (Fan-out/in)** | 子 Orchestrator + `Task.WhenAll` | Pass (Mock 3 Task) | Pass (Sum=3 集約) | **合格** | 1 台 1 slot 時は計算順次・監視並列 |
| **F04: 再起動復旧 (V05)** | Durable 履歴永続化による Replay 復元 | Pass (Fake 再起動) | Pass (実機再起動復旧) | **合格** | Batch Task 実行中の Function 再起動から無送出で正常完走 |
| **F05: 異常終了検知 (V03,07)** | exit code 判定、失敗マニフェスト非公開 | Pass (exit 42 / timeout) | Pass (実機 Failed / TimedOut) | **合格** | 意図的失敗時に後続集約を抑止し成果物を非公開化 |
| **F06: Blob 集約** | 小結果回収、sum 計算、マニフェスト生成 | Pass (メモリ/Fake Blob) | Pass (実 Blob 整合) | **合格** | 現行は数 KB 以下の小結果専用（大容量は T09 へ） |
| **受付排他・冪等性 (V04)** | 不変 `admission.json` v2 + Durable Entity | Pass (同時受付/競合) | Pass (409 競合 / 冪等再送) | **合格** | 別パラメータ時は 409 Conflict、同一入力は結果再返却 |
| **利用者キャンセル (V12)** | `CancelWorkflow` API、`cancel.json`、有界停止 | Pass (外部イベント/調停) | Pass (実機キャンセル完走) | **合格** | 202 受理、cancel.json 先行保存、Batch 停止、manifest 非公開、200 冪等 |
| **ホスト停止時安全網 (V08)** | Batch サーバー側 `MaxWallClockTime` | Pass (制約秒数照合) | Pass (Stopped 中に自動終了) | **合格** | Function が停止していても Batch 側で安全に Job 終端化 |
| **0台オートスケール (V09)** | `$PendingTasks` による Spot 自動拡張 | Pass (Autoscale 式検証) | Pass (0 台から自動起動完走) | **合格** | 0 台状態から Task 投入で 1 台拡張、完了後に固定スケール復元 |
| **サービス間認証 (B案-1)** | EasyAuth v2、App Role `Workflow.Execute` | Pass (Fake トークン) | Pass (実機 401/403/200) | **合格** | 未認証 401、App Role 不足 403、正当トークン通過、稼働パッケージ同定 |
| **Functions 送信閉域化 (B案-2)** | Regional VNet 統合 (`10.80.3.0/24`) | Pass (Terraform Mock) | Pass (VNet 経由送信) | **合格** | `vnet_route_all_enabled = true` で全アウトバウンドを VNet 経由化 |
| **Batch 完全閉域化 (B案-2,3)** | Private Endpoints、Pool VNet 統合、Public 遮断 | Pass (Terraform Mock) | Pass (閉域ノード上完走) | **合格** | Batch Account パブリックアクセス無効、ノード Public IP なし（NoPublicIP） |
| **大容量出力 Blob 直送 (T09)** | Batch `OutputFiles` による Blob Storage 直接保存 | 未実施 | 未実施 | **次フェーズ** | 業務アプリ側の入出力データ仕様確定後に実装 |
| **実 C++ バイナリ連携 (V10)** | 実業務バイナリ配布、入力確定マニフェスト | 未実施 | 未実施 | **次フェーズ** | 業務アプリ側のバイナリ仕様確定後に実装 |
| **全面閉域化 (V00)** | Storage Private Endpoint、ExpressRoute 接続 | 未実施 | 未実施 | **次フェーズ** | インフラ本番回線接続フェーズで実施 |

---

## 3. 評価まとめ

### 1. Durable Functions によるオーケストレーションの有効性
状態遷移、非同期待機、並列集約（Fan-in）、および障害復旧を Durable Functions に委ねることで、**独自の状態管理 DB や常駐ポーリングデーモン、共有カウンタの自前実装を完全に排除**できることを確認しました。
プロセス再起動時も実行履歴から自動的に中間状態が復元され、二重投入を起こさずに安全に処理が継続されます。

### 2. ネットワーク閉域化とセキュリティ境界の確立
Function App の受信用 Private Endpoint、送信用 Regional VNet 統合、Azure Batch Account の Private Endpoint、および Batch Pool の VNet サブネット統合（`NoPublicIPAddresses`）により、**Function 受信および Batch データプレーンの閉域化**を確立しました（※Storage の公開データエンドポイント通信は NAT Gateway 経由で維持されており、Storage の Private Endpoint 化は次期 V00 で対応）。
さらに Microsoft Entra ID（EasyAuth v2）および App Role 認可により、信頼されたバックエンドからの正規要求のみを受理するセキュリティ境界を実証しました。

### 3. 利用者キャンセルと排他制御の実用性
長時間計算において不可欠な「利用者からの途中キャンセル（V12）」について、要求の先行永続化、Durable 外部イベント、Batch Job の有界停止、および成果物公開との厳格な排他調停（`resolution.json`）を導入し、**停止されたジョブの成果物が誤って公開される不整合を確実に防止できる設計**を実証しました。

### 4. 次期開発フェーズへの引き継ぎ
基盤としての成立性は証明されたため、残る課題（実 C++ バイナリ連携 `V10`、`OutputFiles` による大容量ファイル直送 `T09`、Storage の Private Endpoint 化 `V00`）は、実業務アプリケーションの仕様確定に伴う**「業務アプリ結合開発フェーズ」**の初期タスクとして引き継ぐことを推奨します。
