# 検証runnerの処理フロー

更新日: 2026-10-17

この文書は `scripts/Invoke-PocValidation.ps1` が行う検証と、開始APIからDurable Functions、Azure Batch、Blob Storageへ進む処理を記録します。個人検証での現行接続は公開endpointです。VPN／ExpressRoute、Private Endpoint、WebAppはこのrunnerが作成せず、将来構成は[ネットワーク接続設計](network-connectivity-design.md)を参照してください。

## runnerの全体フロー

runnerは既存配置結果を読み込み、選択したシナリオを順番に実行します。シナリオ内の分類試験と `admission20` のPOSTは複数処理を行います。

```mermaid
flowchart TD
    A[runner起動] --> B[証跡ディレクトリ作成]
    B --> C[配置結果を読込]
    C --> D[Git revision・dirty状態・source hashを取得]
    D --> E{入力とendpointは有効か}
    E -- いいえ --> F[Setup Failを記録]
    E -- はい --> G[配置subscriptionでFunction key取得]
    G --> H{key取得成功か}
    H -- いいえ --> F
    H -- はい --> I[選択シナリオを順番に処理]
    I --> J{実行条件を満たすか}
    J -- いいえ --> K[NotRunと理由を記録]
    J -- はい --> L[requestIdと入力を生成]
    L --> M[開始APIへPOST]
    M --> N{HTTP 202とinstanceId・状態URLが妥当か}
    N -- いいえ --> O[Failと理由を記録]
    N -- はい --> P[状態URLを定期照会]
    P --> Q{状態照会結果}
    Q -- 404 --> P
    Q -- Running等 --> P
    Q -- 終端状態 --> R[BatchとBlobの状態を取得]
    R --> S{期待値・識別子・fingerprintが一致か}
    S -- はい --> T[Pass候補]
    S -- いいえ --> O
    T --> U{全世代の起動履歴を証明する試験か}
    U -- はい・履歴不可 --> K
    U -- いいえ／観測済 --> V{次のシナリオがあるか}
    K --> V
    O --> V
    V -- はい --> I
    V -- いいえ --> W[summary.jsonとsummary.txtを保存]
    F --> W
    W --> X[終了コードを返す]
```

状態URLはHTTPS、同一Function Appのauthority、想定instance pathを検証します。初期404はEntity受付後にMain instanceが登録されるまでの待機として扱い、設定された待機上限を超えるとFailになります。HTTPエラー本文や秘密を含むURLは証跡へ出しません。

## 1件のワークフローのシーケンス

成功系では、runnerはDurable状態だけで判定せず、Batch TaskとBlobの結果も照合します。Batchノード上のコマンドは現在のPoC用ダミーで、待機後に `result.txt` へ整数 `1` を出力します。

```mermaid
sequenceDiagram
    autonumber
    participant R as 検証runner
    participant API as StartWorkflow API
    participant BLOB as Blob Storage
    participant E as WorkflowAdmissionEntity
    participant D as Durable Orchestrator
    participant BA as Azure Batch
    participant N as Windows Batch node

    R->>API: POST /api/workflows (requestId, scenario)
    API->>BLOB: request.jsonを条件付き作成／既存入力照合
    alt 同じrequestIdで入力が異なる
        API-->>R: 409 Conflict
    else 新規または同じ入力
        API->>E: Start signal
        API-->>R: 202 Accepted (instanceId, status URL)
        Note over R: URLはメモリ内で照会に使う
        E->>D: 初回だけMain orchestrationを起動
        D->>D: 入力分類、Job ID・fingerprint・deadlineを決定
        D->>BA: fingerprint付きBatch Jobを作成／照合
        loop Taskごとに並行監視
            D->>BA: Task task-00等を作成／照合
            BA->>N: PowerShellダミーTaskを実行
            N->>N: sleep後result.txtに1を出力
            loop 未完了ならDurable Timer後に再照会
                D->>BA: Task状態を照会
                BA-->>D: 状態・exit code
            end
        end
        alt 全Task成功
            D->>BA: Task File APIからresult.txtを取得
            D->>BLOB: tasks/task-NN/result.jsonを保存
            D->>BLOB: aggregate.jsonを保存
            D->>BLOB: manifest.jsonを公開
            D-->>R: Completed / Succeeded
        else Task失敗
            D-->>R: Failed、成功Manifestなし
        else 監視期限超過
            D->>BA: 対象Jobを照合してTerminate
            D->>BA: Job Completedを確認
            D-->>R: Failed runtime、business state TimedOut
        end
    end
    R->>API: 状態URLをGET（未登録中の404は再試行）
    API-->>R: Durable runtimeStatus/customStatus/output
    R->>BA: Job・全Task状態をEntra認証で取得
    R->>BLOB: request・aggregate・manifest・Task結果を取得
    R->>R: ID・状態・値・件数・fingerprint・URIを照合
    R->>R: allowlist形式の証跡をsummaryへ追加
```

期限超過でrunner自身の待機上限に達した場合、runnerは結果をFailとして記録します。runnerの停止はAzure側Jobの停止を意味しません。期限試験ではDurableのTimedOut状態、Batch JobのCompleted、Manifest不在をそれぞれ確認します。

## 生成物と保存先

| 保存先 | データ | 生成条件 |
|---|---|---|
| 実行端末 `artifacts/validation/<run>/` | `summary.json`、`summary.txt` | runnerの終了時。Setup失敗でも作成 |
| Azure Blob `runs/poc-<requestId>/request.json` | version、instanceId、ワークフロー入力 | API受付時 |
| Azure Batch Job | 決定的Job ID、Pool、fingerprint metadata、期限制約 | ワークフロー実行時 |
| Azure Batch Task | `task-00`等、Task command、fingerprint、exit/result | Task投入時 |
| Azure Blob `tasks/task-NN/result.json` | request/job/task identity、分類、fingerprint、値 | Batch Task成功後 |
| Azure Blob `final/aggregate.json` | Task ID一覧、件数、合計、fingerprint | 全Task結果照合後 |
| Azure Blob `final/manifest.json` | 成功状態、aggregateとTask結果のURI | すべて整合した成功時のみ |

summaryはGit revision・dirty flag・source SHA-256・配置結果のpackage SHA-256、試験別時刻と状態、選択したJob／Task観測値を記録します。認証値や管理URL、応答本文、生の例外を含めません。package hashは配置結果ファイルの値であり、稼働中hostが読んだバイナリのhashを直接測定したものではありません。

## シナリオと合格判定

| シナリオ | 実行内容 | Pass条件または留意点 |
|---|---|---|
| `single` | 1 Task | exit 0、Task結果1、aggregateとManifest整合 |
| `three` | 3 Task | Task 3件、各結果1、合計3 |
| `classification` | light、heavy、autoを各1回 | 分類値とTask結果が期待通り（現在autoはlight） |
| `failure` | 1 Taskをexit 42にする | 業務状態Failed、exit 42、成功Manifestなしなら試験Pass |
| `admission20` | 同じ入力で20並列POST後、完了後に再送 | 同じinstance・Task集合・完了状態を観測。全世代の起動履歴APIがないため最終結果はNotRun |
| `conflict` | 同じrequestIdに異なる入力をPOST | HTTP 409 |
| `timeout` | 専用設定でTaskを監視期限より長くする | Durable TimedOut、Batch停止完了、Manifestなし。専用環境指定と十分な待機時間がなければNotRun |

Exit codeは全選択シナリオPassで0、Failありで1、FailなしでNotRunありの場合2です。検証runnerはJob／Taskを作成するため、実Azure実行時にはBatch料金が発生し得ます。履歴、受付Entity、Job、結果Blobの自動削除はしません。

Fake試験、ローカルloopback HTTP試験、実C# fingerprint照合はrunner実装のローカル検証です。Azureでの実Pool、Durable host、Blob serviceによる実証とは区別し、Azure試験が未実施ならNotRunのまま記録します。
