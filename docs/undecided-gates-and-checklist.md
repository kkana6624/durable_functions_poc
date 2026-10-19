# 未確定ゲート一覧と合意チェックシート (ADR)

作成日: 2026-10-19  
適用基準: [`docs/app-service-blob-batch-implementation-instructions.md`](file:///D:/durable_functions_poc/docs/app-service-blob-batch-implementation-instructions.md) / コミット `725fe85`

---

## 1. 目的と位置づけ

本ドキュメントは、Azure Durable Functions + Azure Batch 基盤上で業務 C++ 計算を実行する次期フェーズにおいて、**実装着手前に業務側・インフラ側・運用側の関係者間で合意・確定すべき未確定事項（ゲート）**を整理した合意チェックシートである。

PoC 実装では、実 C++ バイナリや本番スキーマの未受領であっても Fixture モード（ダミー計算・OutputFiles 直送）により基盤検証を先行しているが、**本番業務への適用には本シート記載の全ゲート合意が必要**である。

---

## 2. 未確定ゲート一覧とチェックシート

### ゲート 1: 業務 C++ バイナリおよび実行環境の仕様

| 項目 ID | 決定事項 | 選択肢 / 候補 | 決定値 / 状態 | 影響範囲・留意点 | 合意サイン |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **G1-1** | 実行バイナリ形式と配布方式 | A. Batch ResourceFiles による直接配置<br/>B. Batch Application Packages<br/>C. カスタム VM イメージ（Compute Gallery） | **保留 (TBD)** | - ResourceFiles は起動毎にダウンロード発生。<br/>- 大容量・多数 DLL の場合は Application Package または Gallery イメージが推奨。 | [ ] 業務 / インフラ |
| **G1-2** | 前提ランタイム・依存コンポーネント | A. VC++ 再頒布可能パッケージ（MSVCP140 等）<br/>B. .NET ランタイム<br/>C. OS 標準のみ | **保留 (TBD)** | Gallery イメージ（`gallery_image_version_id`）に事前ベイクするか、StartTask でインストールするかを決定。 | [ ] 業務側 |
| **G1-3** | CLI 引数・終了コード体系 | A. 終了コード `0` 正常終了、非 `0` 異常<br/>B. 固有終了コード（例: 業務エラー `10`、入力異常 `20`） | **保留 (TBD)** | Batch の OutputFiles は `TaskSuccess`（ExitCode `0`）時にのみ Blob アップロードを発火するため、ExitCode `0` の定義が必須。 | [ ] 業務側 |
| **G1-4** | 標準出力・標準エラー出力・診断ログ | A. `stdout.txt` / `stderr.txt` に出力<br/>B. 専用ログファイルを出力（`wd/log.txt`） | **保留 (TBD)** | ログサイズ上限、機密情報マスク方針、トラブルシュート時の証跡回収ルールを確定。 | [ ] 運用側 |

---

### ゲート 2: 入出力データ契約と大容量転送仕様

| 項目 ID | 決定事項 | 選択肢 / 候補 | 決定値 / 状態 | 影響範囲・留意点 | 合意サイン |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **G2-1** | 入力データの形式・文字コード・数値精度 | A. JSON (UTF-8)<br/>B. CSV / TSV<br/>C. 独自バイナリ形式 (Little-Endian) | **保留 (TBD)** | ストリーミングハッシュ（SHA-256）およびサイズ検証はバイナリ非依存で実装済み。業務スキーマ検証ルールが必要。 | [ ] 業務側 |
| **G2-2** | 最大入力サイズおよびノードディスク容量 | A. 小〜中規模（< 100 MB）<br/>B. 大規模（100 MB 〜 10 GB）<br/>C. 超大規模（> 10 GB） | **保留 (TBD)** | バッチノード（`Standard_D2s_v5` 等）のテンポラリディスク（`AZ_BATCH_TASK_DIR`）容量制限に依存。 | [ ] インフラ側 |
| **G2-3** | 計算時間とタイムアウト上限値 | A. 短時間（< 5 分）<br/>B. 中時間（5 〜 30 分）<br/>C. 長時間（30 〜 120 分） | **保留 (TBD)** | `WorkflowSettings.TimeoutMinutes`、Batch Job の `MaxWallClockTime`、Run Command 上限（5,400 秒）の整合調整。 | [ ] 業務 / インフラ |
| **G2-4** | 出力 Blob の命名規則・パーティショニング | `runs/poc-{requestId}/tasks/{taskId}/attempts/{attempt}/output.json` (現行) | **合意済 (PoC)** | attempt 単位のプレフィックス分離により、再試行・投機実行時の成果物汚染を防止。 | [x] 基盤合意 |

---

### ゲート 3: 上流業務アプリとの境界および認証・認可

| 項目 ID | 決定事項 | 選択肢 / 候補 | 決定値 / 状態 | 影響範囲・留意点 | 合意サイン |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **G3-1** | 業務 Web バックエンドの構成形態 | A. 既存オンプレミス Web バックエンドから直接 S2S 呼び出し<br/>B. クラウド上に App Service (ASP.NET Core) を新設して前置プロキシとする | **保留 (TBD)** | - App Service 新設時は Inbound Private Endpoint / Outbound VNet 統合 / App Role 認可の IaC 追加（Track 4）。 | [ ] アーキテクト |
| **G3-2** | 入力アップロードの担当境界 | A. クライアントが Storage へ直接アップロード（SAS 発行）<br/>B. 業務 Web / App Service が受領して Storage へアップロード | **保留 (TBD)** | 大容量アップロード時の帯域、SAS 漏洩リスク、および Ready Manifest 条件付き確定（ETag / SHA-256）の責任主体。 | [ ] セキュリティ |
| **G3-3** | 上流ユーザー認証と所有者（Owner / Org）写像 | A. Entra ID トークンの claims (`oid`, `tid` 等) から解決<br/>B. 業務認証基盤の Assertion をヘッダー伝播（S2S 認可前提） | **保留 (TBD)** | 不特定利用者のなりすまし防止。`X-Poc-Owner-Id` 単独の信用は不可、S2S 認証済み呼出元のみヘッダー伝播を認可。 | [ ] セキュリティ |

---

### ゲート 4: インフラ閉域化および Durable Backend 比較方針

| 項目 ID | 決定事項 | 選択肢 / 候補 | 決定値 / 状態 | 影響範囲・留意点 | 合意サイン |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **G4-1** | Storage Public Network Access (PNA) 遮断時期 | A. Track 3.3 にて PoC 検証完了後に即時遮断<br/>B. 本番移行直前の最終セキュリティフェーズで遮断 | **Track 3.3 予定** | オペレータ端末からのローカル Terraform apply や Blob 直接参照が不可となるため、閉域 VM または VPN 経由のデプロイが必須。 | [ ] インフラ側 |
| **G4-2** | Durable Task Scheduler (DTS) 比較環境の構築採否 | A. コスト・スケーラビリティ比較のため独立環境（別 RG / 別 Hub）を構築（Track 5）<br/>B. 既存の Azure Storage バックエンドのみで本番稼働とし、DTS は見送り | **オプショナル** | 既存 Storage 環境の in-place 切替は行わない。DTS 採用時は専用 RG、専用 Task Hub、専用 Private Endpoint が必要。 | [ ] PO / リード |

---

## 3. 合意プロセスと次のアクション

1. **レビューと合意形成**:
   - 各ゲートの「合意サイン」欄の関係者（業務・インフラ・セキュリティ）による記入と ADR 採番。
2. **実装着手のアンチブロッキング**:
   - ゲート 1・2・3 の未合意事項については、**既定の Fixture 仕様（`output.json`、ExitCode `0`、`value=1`）およびモックインターフェース**を適用し、インフラ・基盤パイプラインの実装をブロッキングなく継続する。
