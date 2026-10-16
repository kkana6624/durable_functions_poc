# PoC 検証結果

## 2026-10-17 独立検証runnerの追加

`Invoke-PocValidation.ps1` を追加し、配置済み環境への検証を配置処理から分離しました。今回もAzure実接続は未実施です。

| 対象 | ローカル結果 |
|---|---|
| runnerオフライン試験 | Pass。正常・分類・期待exit 42・409・20並列の判定、404待機上限、401/403、結果改ざん、停止未確認、秘密除外、途中失敗の要約、終了コード0/1/2、空の試験選択拒否 |
| HTTP並行送信 | ローカルTCPサーバーで、最初の応答前に20接続を確認。実Azureの同時受付成功を意味しない |
| fingerprint互換 | 実C# `WorkflowIdentity.Fingerprint` の固定値2件（auto/light、heavy/非ゼロ）と一致 |
| 既存.NET / Release publish | 54 Pass / 0 Fail / 0 Skip、publish成功 |
| 既存PowerShell / Terraform | 配置試験3組Pass、Terraform Mock main7件・bootstrap1件Pass |

証跡は `artifacts/runner-validation/powershell-runner-tests.log`、`publish.log`、`regression-summary.txt`。設定ファイル不存在を用いたCLI試験でも終了コード1と秘密を含まない要約を確認しました。Git revisionに加え、追跡ファイルとGit非無視の未追跡ファイルの内容から求める `sourceSha256` で未コミット状態を識別します。配置package hashは配置結果ファイル由来で、実hostの読込バイナリを別途実証した値ではありません。

`admission20` は同一instanceへの収束・期待Task数・完了後再送を観測しますが、現在の管理APIだけでは全世代のMain起動履歴を証明できないため、実runnerは `NotRun / AdmissionObservedButExecutionHistoryUnavailable` を返します。Fakeで履歴を与える試験のPassを実Azureの一回起動保証へ繰り上げません。

個人環境は公開endpointで検証を開始する前提とし、VPN/ExpressRouteと共通VNetの境界は[接続設計](network-connectivity-design.md)に記録しました。ネットワークリソースは作成していません。

> [!NOTE]
> Azure 実環境への接続値・資格情報が未提供のため、現時点での検証はすべてローカル環境（単体テスト・Fake/Mock・オフライン試験）で実施しています。Azure 実環境での動作検証は未実施です。

## 2026-10-17 信頼性改善後の再検証

信頼性改善を反映した再検証の結果は以下のとおりです（後述の 2026-10-16 時点の 30 件は初版時点の記録です）。

| 対象 | 結果 |
|---|---|
| .NET単体テスト | 54 Pass / 0 Fail / 0 Skip。`artifacts/reliability-validation/dotnet-test.log` |
| Release publish | 成功。`artifacts/reliability-validation/publish.log`、`publish/` |
| PowerShellオフライン試験 | Deployment.Common / Test-Deployment / Deploy.Workflowの3組成功 |
| Terraform Mock | main 7件、bootstrap 1件成功 |

### 単体テスト（Fake / Mock）での追加検証項目

- **受付・重複起動の抑止**
  - 同一入力の同時受付
  - 異なる入力での競合検出（409）
  - 通知応答が失われた後の再送
  - 既存インスタンスの再利用
  - Entity 状態の再読み込み後における重複起動の抑止
- **一時障害と再試行制御**
  - 一時障害に対する再試行上限と期限の適用
  - SDK の複合例外（`AggregateException`）および通信タイムアウトの判定
  - 呼び出し元によるキャンセルの非再試行扱い
  - 長時間 Activity 実行中に期限へ到達した場合の挙動
- **期限超過時の Batch Job 停止制御**
  - 停止対象 Job の照合
  - 停止要求の再実行（冪等性）
  - Job の自然完了との競合処理
  - 停止確認に失敗した場合の Manifest 未公開保証

### 配置スクリプト・Terraform の追加検証項目（Fake / Mock）

- 作成済み state backend に対する `-PlanOnly` 実行
- backend 不存在と参照エラーの区別
- 既存の 0 台 Pool に対するコード配置
- 新規 Pool 作成時の全台準備完了待ちと node 一覧の pagination 処理
- 12 個の Functions 検出確認
- インスタンス状態 GET 時の 404 待機と 401/403 エラー時の即時失敗

### Azure 実環境で未検証の項目

以下の項目はローカルの Fake / Mock では再現できないため、実環境での検証が必要です。設計上の根拠と残りの試験項目は[設計と公式資料](workflow-reliability.md)に記載しています。

- Durable Entity の実 checkpoint 動作と host 中断からの復旧
- Azure Blob Storage 実サービス上での同時条件付き書き込み
- 実 Azure Batch 上での Job 停止動作

---

## 初版の記録（2026-10-16）

記録日: 2026-10-16。以下は初版実装時点でのローカル検証結果と、Azure 実環境試験に向けた手順・期待値の記録です。

## 実行環境・証跡

| 項目 | 記録 |
|---|---|
| OS | Windows / PowerShell |
| .NET SDK | 10.0.402 を作業フォルダー内に導入 |
| .NET package restore | Function project / solution restore 成功 |
| .NET build / publish | solution build 0 警告・0 エラー、Release publish 成功。`artifacts/build.log` / `artifacts/publish.log` |
| .NET unit test | 30 Pass / 0 Fail / 0 Skip、18.731 秒、0 警告。Microsoft.Testing.Platform |
| Terraform fmt / validate | Terraform 1.14.0 / AzureRM 5.10.0、2026-10-16 実測 Pass |
| Terraform plan / apply | Not tested: Azure 接続値なし |
| Azure 実行 instance / Job / Task ID | 未発行 |
| Azure Activity 履歴 / Blob URI | 未取得 |

## ワンコマンド配置資材の追加検証

2026-10-16 に Terraform の `mock_provider` および fake CLI / HTTP を用いて以下のオフライン検証を実施しました。

| 対象 | 実測 | 証跡 |
|---|---|---|
| Terraform main | fmt -check / validate成功、Mock 7件全Pass | `artifacts/deployment-validation/terraform-main.log` |
| Terraform bootstrap | validate成功、Mock 1件Pass | `artifacts/deployment-validation/terraform-bootstrap.log` |
| Deployment.Common | planの削除・置換・共有Batch更新拒否、ZIP構成、native異常終了、秘密抑止、JSON変換がPass | `artifacts/deployment-validation/common.log` |
| Test-Deployment | ノード全台準備待ち、権限反映待ち、Functions検出、HTTP・Blob整合性など11シナリオPass | `artifacts/deployment-validation/readiness.log` |
| Deploy workflow | PlanOnly、apply→Pool→ZIP→Functionsの順、subscription固定、単一Task smoke→HTTP検証、環境identity変更拒否、bootstrapの絶対stateパスと実行順がPass | `artifacts/deployment-validation/workflow.log` |

※ 新規 Batch の実クォータ・Gallery アクセス・node-agent 適合性、state backend の初回 RBAC 反映待ち、Entra 認証による実 ZIP 配置については、Azure 実環境での検証が必要です。

## SDK / API の確認

2026-10-16 に以下の公式ドキュメントを確認し、Functions v4 / .NET 10 isolated、Linux Dedicated プラン、Managed Identity ベースの host storage 構成を選択しました。

- [Functions isolated guide](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide)
- [Durable MI guide](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-configure-managed-identity)
- [AzureRM Linux Function schema](https://github.com/hashicorp/terraform-provider-azurerm/blob/main/website/docs/r/linux_function_app.html.markdown)
- [Azure Batch SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/compute.batch-readme)

Function プロジェクトでは現行の `Azure.Functions.Sdk/1.0.0`、Worker `2.52.0`、DurableTask extension `1.19.0` を使用しています。

SDK 互換性の確認では、当初指定していた `Azure.Identity 1.17.1` と Functions 側が依存する `Azure.Core 1.54` の間で型定義の衝突が発生しました。そのため、`Azure.Core 1.53` 以降の Identity 型統合に対応した `Azure.Identity 1.21.0` へ更新し、ビルドが正常に通ることを確認しています。なお、ビルド時は Function プロジェクトを先に restore して SDK の生成ターゲットを準備してから solution 全体を restore する手順としており、README にも明記しています。

## シナリオ

| ID | 手順 | 期待値 | 実測 / 証跡の場所 | 結果 |
|---|---|---|---|---|
| Build | `dotnet build DurableBatchPoc.slnx --no-restore` | コンパイル成功 | 0 警告・0 エラー。`artifacts/build.log` | Pass |
| Publish | `dotnet publish src/Poc.Functions/Poc.Functions.csproj -c Release --no-restore -o artifacts/publish` | デプロイ用成果物生成 | 成功。`artifacts/publish.log` / `artifacts/publish/` | Pass |
| Unit | `dotnet test --project tests/Poc.Tests/Poc.Tests.csproj --no-restore` | Fake の正常、異常、冪等性が合格 | 30 Pass / 0 Fail / 0 Skip、18.731 秒。`artifacts/test-summary.txt`（実測要約、生ログではない） | Pass |
| Windows command | ローカル Windows で `cmd` → `powershell.exe` を実行 | 正常 exit 0 / result 1、故意失敗 exit 42 | 正常 exit 0・result.txt=1、失敗 exit 42・result ファイルなし。`artifacts/test-summary.txt` | Pass（ローカルのみ） |
| Terraform init | `terraform -chdir=infra init -backend=false` | provider の取得成功 | AzureRM v5.10.0 (signed by HashiCorp)、`.terraform.lock.hcl` 生成 | Pass |
| Terraform fmt | `terraform -chdir=infra fmt -check` | 差分なし、exit 0 | 2026-10-16 実行、標準出力なし、exit 0 | Pass |
| Terraform validate | `terraform -chdir=infra validate -no-color` | 構成の検証成功 | 2026-10-16: `Success! The configuration is valid.`、exit 0 | Pass |
| Step A | `dotnet run --project tools/BatchSmoke` | 既存 Windows Pool の単一 Task 成功、File API で整数1 | Azure 接続なし | Not tested |
| T01 | 3 Task の新規 requestId を POST | 3結果、sum=3、manifest 存在 | Azure 接続なし | Not tested |
| T02 | light / heavy 別 requestId で実行 | 分類理由と待機時間差がログと結果に残る | Azure 接続なし | Not tested |
| T03 | `failTaskIndex=1` | 親 Failed、aggregate / manifest なし | Azure 接続なし | Not tested |
| T04 | heavy の長時間 Task | HTTP 202、Timer 待機、状態照会 | Azure 接続なし | Not tested |
| T05 | 待機中に PoC Function のみ再起動 | 同じ instance で再開し manifest 公開 | Azure 接続なし | Not tested |
| T06 Mock | Fake で結果ファイル取得失敗 | 明示失敗、manifest なし | `MissingTaskFileIsAnErrorAndPreventsManifest` 成功 | Pass（Mock） |
| T06 Azure | 実 Batch File API で取得不能 | 明示失敗、manifest なし | 実ノードは未試験 | Not tested |
| Timeout | 待機より短い期限 | TimedOut、manifestなし、Job停止結果を記録 | Mock成功、実Azure接続なし | Not tested（Azure） |
| T07 | 同一 requestId の同時 POST | 同入力は同instance、異入力は409、Entityで起動を直列化 | 2026-10-17 Mock成功、実Azure接続なし | Not tested（Azure） |
| T08 Mock | Activity の副作用後に再呼び出し | 同一 Job / Task / manifest を再利用 | `ReplayedActivitiesReuseResourcesAndManifest` 成功。1 Job / 3 Tasks / 5文書 | Pass（Mock） |
| T08 Azure | 実 Activity 副作用後の中断・再実行 | 同一 Job / Task / manifest を再利用 | クラウド replay 未実施 | Not tested |
| T09 | OutputFiles 直送比較 | Blob への直接永続化 | P1 / 未実装 | Not tested |
| T10 | Durable / Batch 停止 | 両方の状態整合性 | 期限超過時のJob停止は実装・Mock成功。利用者キャンセル連鎖は未実装 | Not tested（Azure） |

Azure 実環境での試験実施時は、`artifacts/` 配下へ実行日時、設定値（秘密情報を除く）、状態照会 JSON、Batch ID / exit code / failure info、Blob URI と一覧、Application Insights ログを保存し、上表の該当行に証跡パスを追記します。なお、Durable Functions の `runtimeStatus` が `Completed` であっても、業務出力（`output`）が `Failed` または `TimedOut` の場合は失敗とみなします。最終的な合格判定では、出力された manifest の内容と入力データとの整合性まで確認します。
