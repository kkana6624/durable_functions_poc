# PoC 検証結果

## 2026-10-17 信頼性改善後の再検証

Azure実接続は引き続き未実施。下の2026-10-16の30件は初版の履歴で、現在の結果は次のとおりです。

| 対象 | 結果 |
|---|---|
| .NET単体テスト | 54 Pass / 0 Fail / 0 Skip。`artifacts/reliability-validation/dotnet-test.log` |
| Release publish | 成功。`artifacts/reliability-validation/publish.log`、`publish/` |
| PowerShellオフライン試験 | Deployment.Common / Test-Deployment / Deploy.Workflowの3組成功 |
| Terraform Mock | main 7件、bootstrap 1件成功 |

追加検証は同入力の同時受付、異入力の競合、通知応答喪失後の再送、既存インスタンスの再利用、Entity状態再読み込み後の起動抑止、一時障害の再試行上限と期限、SDKの複合例外と通信timeout、呼出元cancelの非再試行、長時間Activity中の期限到達、停止対象照合、停止要求の再実行、自然完了との競合、停止確認失敗時の未公開を対象とします。

配置試験では作成済みbackendのPlanOnly、backend不存在と参照エラーの区別、既存0台Pool、新規全台準備、node pagination、12 Functions検出、状態GET404待機と401/403失敗を確認しました。いずれもFake/Mockです。Entityの実checkpoint、host中断復旧、Blobサービスでの同時条件付き書き込み、実Batchの停止は未実証です。[設計と公式資料](workflow-reliability.md)に根拠と残るAzure試験を記載します。

## 初版の記録

記録日: 2026-10-16。Azure 接続値と資格情報が提供されていないため実環境試験は未実施です。以下は期待値と実施手順の記録であり、実環境での成功を意味しません。

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

2026-10-16に以下を実行しました。Terraformは`mock_provider`、配置スクリプトはfake CLI/HTTPを使用しており、Azureへのplan/apply・ZIP配置・ノード起動を実施した結果ではありません。

| 対象 | 実測 | 証跡 |
|---|---|---|
| Terraform main | fmt -check / validate成功、Mock 7件全Pass | `artifacts/deployment-validation/terraform-main.log` |
| Terraform bootstrap | validate成功、Mock 1件Pass | `artifacts/deployment-validation/terraform-bootstrap.log` |
| Deployment.Common | planの削除・置換・共有Batch更新拒否、ZIP構成、native異常終了、秘密抑止、JSON変換がPass | `artifacts/deployment-validation/common.log` |
| Test-Deployment | ノード全台準備待ち、権限反映待ち、Functions検出、HTTP・Blob整合性など11シナリオPass | `artifacts/deployment-validation/readiness.log` |
| Deploy workflow | PlanOnly、apply→Pool→ZIP→Functionsの順、subscription固定、単一Task smoke→HTTP検証、環境identity変更拒否、bootstrapの絶対stateパスと実行順がPass | `artifacts/deployment-validation/workflow.log` |

新規Batchの実quota・Galleryアクセス・node-agent適合、state backendの初回RBAC反映、Entra認証による実ZIP配置は未検証です。配置コードとMock試験の成功を、Azure基盤の稼働確認と混同しません。

## SDK / API の確認

2026-10-16 に [Functions isolated guide](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide)、[Durable MI guide](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-configure-managed-identity)、[AzureRM Linux Function schema](https://github.com/hashicorp/terraform-provider-azurerm/blob/main/website/docs/r/linux_function_app.html.markdown)、[Azure Batch SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/compute.batch-readme) を確認しました。Functions v4 / .NET 10 isolated、Linux Dedicated、Managed Identity host storage を選択しています。Function project は現行 `Azure.Functions.Sdk/1.0.0`、Worker `2.52.0`、DurableTask extension `1.19.0` を使用します。資料上の確認と package restore は Azure 実接続試験の Pass ではありません。

SDK 互換性の確認では、当初の `Azure.Identity 1.17.1` と Functions が依存する `Azure.Core 1.54` の型定義が衝突しました。Azure.Core 1.53 以降の Identity 型統合に対応する現行 `Azure.Identity 1.21.0` へ更新し、最終ビルドで互換性を確認しました。Function project を先に直接 restore して SDK の生成ターゲットを準備し、続けて solution を restore する手順を README に記載しています。

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

Azure 試験時は `artifacts/` 等へ日時、設定（秘密を除く）、状態照会 JSON、Batch ID / exit code / failure info、Blob URI と一覧、Application Insights ログを保存し、該当行に具体的なパスを追記します。Durable の runtimeStatus が Completed でも業務 output が Failed / TimedOut の場合は成功としません。最終成功は manifest の内容と入力との整合性まで確認します。
