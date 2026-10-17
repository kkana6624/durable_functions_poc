# Durable Task Scheduler を使う PoC

更新日: 2026-10-17

## 対象と検証状況

Durable Functions の履歴・内部メッセージ・Entity 状態の保存先を、Azure Storage または Durable Task Scheduler（DTS）から選択します。既定は従来の `azureStorage` です。DTS 用の別環境を Azure へ配置し、Managed Identity 接続、12 Functions の登録、3 Task smoke（sum=3）と標準検証の5件 Pass を確認しました。`admission20` は起動履歴取得の制限による NotRun、summary 終了コード2です。SDK で DTS 上の smoke インスタンスも直接照会し、`MainOrchestrator / Completed` を確認しました。既存 Azure Storage 環境の Function 設定・保存先は変更していません。

Orchestrator、Activity、受付 Entity、Windows Batch の処理は共通です。DTS を選択しても Functions のホスト用 Storage と業務入力・結果 Blob は残します。DTS は Batch のスケジューラを置き換えるものではありません。

## Azure 実証結果

Japan East の別 RG `poc-durable-scheduler-rg` に Function `poc-dts40481149-func`、Consumption scheduler `poc-dts40481149-dts`、task hub `DtsPocHub` を配置しました。追加 B1 Plan の枠がないため既存 B1 を読み取り専用で参照し、Function・Storage・hub・Terraform state は分離しました。既存 Plan の設定は変更していませんが、CPU・メモリは共有します。

| 確認 | 結果・証跡（Git 管理外） |
|---|---|
| 配置・3 Task smoke | `Ready=true`、`Smoke=Pass`、`TaskCount=3`、`Sum=3`。`.deploy/dts-poc/20261017-224759-6853/deployment-result.json`（完了 `2026-10-17T13:56:57Z`） |
| 標準6シナリオ | single / three / classification / failure / conflict は Pass、admission20 は `AdmissionObservedButExecutionHistoryUnavailable` による NotRun。`artifacts/dts-validation/azure-scenarios/summary.txt`、終了コード2（完了 `2026-10-17T14:06:03Z`） |
| DTS データプレーン直接照会 | `artifacts/dts-validation/live-instance.json`。smoke と同じ `poc-8689eb6004faae579c3e4eae6de22e50310d4ff9` の `MainOrchestrator / Completed` を SDK で取得。配置設定の表示とは別の接続証跡 |
| ローカル IaC | Terraform main Mock27件、fmt / validate 成功。既存 Plan の参照、observer の hub スコープ読み取り権限も検証 |

host 再起動復旧、期限停止、権限・通信障害注入、0台 autoscale、Spot 回収復旧、実 C++ 処理、保持期限経過後の動作は未実証です。admission20 の観測結果を、全世代にわたる厳密な起動一意性の保証には繰り上げません。

## 設定と作成するリソース

| 設定 | 内容 |
|---|---|
| `durable_backend` | `azureStorage`（既定）または `durableTaskScheduler` |
| `durable_task_hub_name` | 既定 `PocBatchHub`。DTS のサンプルでは `DtsPocHub` |
| `existing_service_plan_id` | 既定 `null` で新規 Linux B1 Plan を作成。指定時は同じサブスクリプション・リージョンの既存 Linux Plan を読み取り専用で参照 |
| DTS SKU | Consumption。実行アクションに応じた従量課金 |
| 履歴保持 | Scheduler の default retention policy を30日に設定。完了したオーケストレーションの履歴は永続保持されない |
| 認証 | Function の system-assigned Managed Identity。対象 task hub に `Durable Task Data Contributor` を付与 |
| 通信 | Function の受信は Private Endpoint を継続。DTS への送信は公開 TLS/gRPC エンドポイントと Entra 認証を使用 |

AzureRM 5.10 に scheduler / task hub リソースがないため、固定バージョンの AzAPI で `Microsoft.DurableTask/schedulers`、`taskHubs`、`retentionPolicies` を管理します。API は `2026-02-01` を使用します。Azure Storage モードでは DTS リソース・DTS 権限を作成しません。

Functions は `Microsoft.Azure.Functions.Worker.Extensions.DurableTask.AzureManaged` を参照し、DTS 選択時は app settings で host.json の provider を `azureManaged` に上書きします。接続設定は `DURABLE_TASK_SCHEDULER_CONNECTION_STRING` で、形式は次のとおりです。

```text
Endpoint=<scheduler endpoint>;TaskHub=<hub name>;Authentication=ManagedIdentity
```

この設定にキーや SAS は含めません。検証 VM が DTS へ直接アクセスする権限は付与せず、従来どおり Function の HTTP API を呼び出します。Scheduler の Private Endpoint と Function の送信用 VNet Integration はこの追加の対象外です。

## Azure 用の別環境を準備する

1. `environments/durable-task-scheduler.example.psd1` を `environments/dts-poc.psd1` にコピーし、subscription、SSH 公開鍵、operator ID、既存 Batch の参照先、state backend を埋めます。
2. 稼働中の環境と異なる Environment 名・resource group・name suffix を使用します。Function App、入力・結果 Storage、DTS task hub と Terraform state key を分離してください。Batch Pool は既存参照で共有できますが、名前や ID を確認して指定します。
3. `Microsoft.DurableTask` を登録し、対象リージョンで scheduler が提供されることと Consumption のクォータを確認します。現在の提供リージョンは次で確認できます。

```powershell
az provider show --namespace Microsoft.DurableTask --query "resourceTypes[?resourceType=='schedulers'].locations | [0]" --output table
```

4. 同じリージョンの Function と DTS を選択し、plan を確認してから配置します。

追加の App Service Plan を作成できない場合は、`existing_service_plan_id` に既存 Plan の完全な ARM ID を指定できます。別 RG の Plan も参照でき、追加 Plan の作成・既存 Plan の設定変更は行いません。例は `/subscriptions/<subscription>/resourceGroups/<plan-rg>/providers/Microsoft.Web/serverFarms/<plan-name>` です。Linux とリージョンの一致を配置前に検証します。

共用するのは計算用 Plan だけで、DTS 用の Function App・Storage・task hub・Terraform state は分離します。Plan 内では CPU・メモリを共有するため、既存 Function への性能影響がないことは保証しません。[Dedicated Plan の共有](https://learn.microsoft.com/en-us/azure/azure-functions/dedicated-plan)、[別 RG の Plan 指定](https://learn.microsoft.com/en-us/cli/azure/functionapp#az-functionapp-create) を参照してください。

```powershell
./scripts/deploy.ps1 -Environment dts-poc -ConfigFile ./environments/dts-poc.psd1 -PlanOnly
./scripts/deploy.ps1 -Environment dts-poc -ConfigFile ./environments/dts-poc.psd1 -Smoke
```

既存ローカル Environment の backend / hub 変更は拒否します。旧 identity ファイルは `azureStorage` / `PocBatchHub` として扱います。この保護はローカルの配置記録に基づくもので、別端末からの直接 Terraform 操作まで防ぐものではありません。`-PlanOnly` は接続先や state backend の存在確認を伴い、backend 未作成時は bootstrap の plan のみで終了します。

## DTS の実行履歴を直接確認する

`durable_observer_principal_id` に明示した操作者 Object ID を設定すると、DTS の対象 task hub にだけ `Durable Task Data Reader` を付与します。既定は `null` で権限を追加せず、Azure Storage モードでは指定を拒否します。Function の実行権限と読み取り用の権限は別です。

`tools/DtsProbe` は DTS endpoint / task hub に直接接続し、スモークテストの instance ID を読み取ります。`GetInstanceAsync(..., getInputsAndOutputs:false)` を使い、名前・状態・時刻だけを JSON に出します。キー、接続文字列、入力・出力、生の例外は表示しません。デプロイ結果ファイルの `outputs.durable_scheduler_endpoint` と `task_hub_name`、スモークの `validation.InstanceId` を指定してください。

```powershell
dotnet run --project tools/DtsProbe -- --endpoint https://YOUR-SCHEDULER.REGION.durabletask.io --task-hub DtsPocHub --instance-id YOUR-INSTANCE-ID
```

取得成功は終了コード0ですが、呼び出し側で `runtimeStatus=Completed` と期待する ID / 関数名を確認します。未登録は2、照会失敗は1です。これは設定値の表示とは異なり、DTS データプレーン上の履歴を直接照会する検証です。詳細は [ツール説明](../tools/DtsProbe/README.md) を参照してください。

## 履歴と requestId の扱い

バックエンド間で既存の実行履歴・Entity 状態を移行する機能はありません。既存 Function の app settings だけを DTS に切り替えても、旧環境の処理は引き継がれません。旧環境は処理完了まで維持し、別環境・新しい requestId で DTS の検証を開始します。

同じ Batch Pool を共有する場合も、過去に使った requestId を別環境で再使用しないでください。Job ID は requestId から決まるため、別バックエンドで同じ ID を使うと過去の Batch Job と衝突します。

DTS の標準ペイロード上限は1MB、instance ID は最大100文字の printable ASCII です。現 PoC は小さな状態と Blob 参照を渡し、instance ID は44文字のハッシュ形式です。大きな入力・結果を Durable の状態へ直接埋め込む構成にはしません。

Consumption の履歴保持上限は30日です。受付 Blob・Entity 状態・実行履歴の保持は別々に扱い、保持期間後の再送・過去 ID 再利用を保証しません。Entity 状態も30日で確実に消えるという意味ではありません。既存の「全世代の Main 起動履歴を取得できないため admission20 は NotRun」という評価も、この追加のみでは解消しません。

## ローカル設定例と実接続の確認

`src/Poc.Functions/local.settings.scheduler.json.example` を `local.settings.json` にコピーすると、DTS emulator の `localhost:8080` / task hub `default` を使用できます。Functions host 用に Azurite も必要です。`Authentication=None` はローカル emulator 専用で、Azure 用設定には使用しません。Batch / 結果 Blob の設定は実際の検証先に置き換え、開発者の認証・権限を準備してください。emulator はインメモリのため、停止をまたぐ永続性の実証には使いません。

Azure では DTS 配置・MI 接続・12 Functions の登録、単一 / 3 Task・分類・意図的失敗・入力競合を確認済みです。次は host 再起動後の Entity / orchestration 復旧、期限停止、履歴保持の影響を別途確認します。Mock・ビルド・publish の成功と実機の証跡は区別します。

検証結果の `settings.durableBackend` と `settings.taskHubName` は配置設定の記録であり、DTS サーバーへの実接続を独立に証明する欄ではありません。

## 公式の参照先

- [Functions の DTS 構成](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-task-scheduler/quickstart-durable-task-scheduler)
- [Managed Identity 接続](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-configure-managed-identity)
- [保存先の比較・移行制約](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-storage-providers)
- [Scheduler、提供リージョン・制限](https://learn.microsoft.com/en-us/azure/durable-task/scheduler/durable-task-scheduler)
- [SKU、課金と保持上限](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler-billing)
- [Scheduler の ARM / AzAPI 定義](https://learn.microsoft.com/en-us/azure/templates/microsoft.durabletask/2026-02-01/schedulers)
