# Durable Task Scheduler を使う PoC

更新日: 2026-10-17

## 対象と検証状況

本構成では、Durable Functions の実行履歴・内部メッセージ・Durable Entity 状態の保存先（バックエンド）として、従来の **Azure Storage（`azureStorage`、既定）** または **Durable Task Scheduler（`durableTaskScheduler`、DTS）** を選択できます。

DTS 用の別環境を Azure 上へ配置し、Managed Identity 接続、12 個の Functions 登録、3 Task スモークテスト（`Sum=3`）、および標準検証シナリオ 5 件の Pass を実機で確認済みです。`admission20`（20 並列同時受付）については、公開 API から全世代の起動履歴を網羅取得できない制限により `NotRun`（summary 終了コード `2`）としています。あわせて、SDK（`tools/DtsProbe`）から DTS 上のスモークインスタンスを直接照会し、`MainOrchestrator / Completed` へ到達していることも確認しました。なお、既存の Azure Storage バックエンド環境の Function App 設定や保存先は一切変更していません。

Orchestrator、Activity、受付用 Durable Entity、および Windows Batch 側の処理コードは両バックエンドで共通です。DTS を選択した場合でも、Functions ホスト自体が使用する Storage と業務用の入力・結果保存 Blob は引き続き使用します。また、**DTS はワークフローの状態管理バックエンドであり、Azure Batch の計算ノードスケジューラを置き換えるものではありません。**

## Azure 実証結果

検証時は Japan East の専用リソースグループに Function App、Consumption Scheduler、Task Hub を配置しました。具体的な Azure リソース名、サブスクリプション ID、エンドポイントは環境固有情報のため、この資料には記載しません。サブスクリプションの B1 Plan クォータに空きがなかったため既存の Linux B1 Plan を読み取り専用で参照し、Function App・Storage・Task Hub・Terraform state は既存環境から分離しています（既存 Plan の設定自体は変更していませんが、Plan 内の CPU・メモリは共有されます）。

| 確認項目 | 結果・証跡（Git 管理外） |
|---|---|
| 配置・3 Task smoke | `Ready=true`、`Smoke=Pass`、`TaskCount=3`、`Sum=3`。証跡: `.deploy/dts-poc/20261017-224759-6853/deployment-result.json`（完了: `2026-10-17T13:56:57Z`） |
| 標準 6 シナリオ | `single` / `three` / `classification` / `failure` / `conflict` の 5 件が Pass、`admission20` は `AdmissionObservedButExecutionHistoryUnavailable` により `NotRun`。証跡: `artifacts/dts-validation/azure-scenarios/summary.txt`、終了コード `2`（完了: `2026-10-17T14:06:03Z`） |
| DTS データプレーン直接照会 | 証跡: `artifacts/dts-validation/live-instance.json`。スモークテストと同一のインスタンス `poc-8689eb6004faae579c3e4eae6de22e50310d4ff9` が `MainOrchestrator / Completed` であることを SDK で直接取得（配置設定値の表示とは独立したデータプレーン接続証跡） |
| ローカル IaC 検証 | Terraform main Mock 27 件、および `fmt` / `validate` が成功。既存 Plan の参照や observer 用の Task Hub スコープ読み取り権限も検証済み |

なお、Functions ホストの再起動復旧、監視期限超過（タイムアウト）による停止、権限・通信障害注入、0 台オートスケール、Spot 回収からの復旧、実 C++ バイナリ処理、および履歴保持期限経過後の動作は未実証です。また、`admission20` の観測結果をもって全世代にわたる厳密な起動一意性の保証とは扱いません。

## 設定と作成するリソース

| 設定項目 | 内容 |
|---|---|
| `durable_backend` | `azureStorage`（既定）または `durableTaskScheduler` |
| `durable_task_hub_name` | 既定は `PocBatchHub`（設定例では `your-task-hub`） |
| `existing_service_plan_id` | 既定は `null`（新規 Linux B1 Plan を作成）。指定時は同一サブスクリプション・リージョン内の既存 Linux Plan を読み取り専用で参照 |
| DTS SKU | `Consumption`（実行アクション数に応じた従量課金） |
| 履歴保持ポリシー | Scheduler の default retention policy を 30 日に設定（完了したオーケストレーションの履歴は永続保持されません） |
| 認証方式 | Function App の System-assigned Managed Identity（対象 Task Hub に `Durable Task Data Contributor` を付与） |
| 通信経路 | Function の受信は Private Endpoint（公開アクセス無効）を継続。Function から DTS への送信は公開 TLS/gRPC エンドポイント＋Entra 認証を使用 |

Terraform の AzureRM プロバイダー（5.10.0）には DTS リソースが用意されていないため、バージョン固定した AzAPI プロバイダー（API バージョン `2026-02-01`）を用いて `Microsoft.DurableTask/schedulers`、`taskHubs`、`retentionPolicies` を管理します。`azureStorage` モードを選択した場合は、DTS リソースおよび関連ロール割り当ては一切作成されません。

Function App 側は `Microsoft.Azure.Functions.Worker.Extensions.DurableTask.AzureManaged` を参照し、DTS 選択時のみ App Settings で `host.json` のストレージプロバイダー設定を `azureManaged` へ上書きします。接続設定 `DURABLE_TASK_SCHEDULER_CONNECTION_STRING` の形式は以下のとおりです。

```text
Endpoint=<scheduler endpoint>;TaskHub=<hub name>;Authentication=ManagedIdentity
```

この設定には共有キーや SAS トークンを含めません。検証 VM には DTS へ直接アクセスする権限を付与せず、従来どおり Function App の HTTP API を経由してワークフローを呼び出します。なお、DTS Scheduler 側の Private Endpoint 化および Function App の送信用 VNet Integration は今回のスコープ外です。

## Azure 用の別環境を準備する

1. `environments/durable-task-scheduler.example.psd1` を `environments/dts-poc.psd1` にコピーし、サブスクリプション ID、SSH 公開鍵、操作者 Object ID、既存 Batch の参照情報、および Terraform state backend を設定します。
2. 稼働中の環境とは異なる Environment 名・Resource Group・name suffix を使用し、Function App・入出力 Storage・DTS Task Hub・Terraform state key を既存環境から分離してください（Batch Pool は既存参照モードで共有可能ですが、事前に名前や ID を確認して指定します）。
3. サブスクリプションで `Microsoft.DurableTask` を登録し、対象リージョンで Scheduler が提供されていること、および Consumption SKU のクォータを確認します。現在の提供リージョンは以下のコマンドで確認できます。

```powershell
az provider show --namespace Microsoft.DurableTask --query "resourceTypes[?resourceType=='schedulers'].locations | [0]" --output table
```

4. Function App と同一リージョンの DTS を選択し、`plan` の内容を確認してから配置します。

追加の App Service Plan を作成できない場合は、`existing_service_plan_id` に既存 Linux Plan の完全な ARM ID（例: `/subscriptions/<subscription>/resourceGroups/<plan-rg>/providers/Microsoft.Web/serverFarms/<plan-name>`）を指定することで、別リソースグループの Plan も読み取り専用で参照できます。追加 Plan の作成や既存 Plan の設定変更は行わず、配置前に OS が Linux であることとリージョンの一致を検証します。

共用するのは計算用の App Service Plan のみであり、DTS 用の Function App・Storage・Task Hub・Terraform state は分離します。ただし、Plan 内で CPU・メモリを共有するため、既存の Function App に対する性能影響がないことは保証しません（参考: [Dedicated Plan の共有](https://learn.microsoft.com/en-us/azure/azure-functions/dedicated-plan)、[別 RG の Plan 指定](https://learn.microsoft.com/en-us/cli/azure/functionapp#az-functionapp-create)）。

```powershell
./scripts/deploy.ps1 -Environment dts-poc -ConfigFile ./environments/dts-poc.psd1 -PlanOnly
./scripts/deploy.ps1 -Environment dts-poc -ConfigFile ./environments/dts-poc.psd1 -Smoke
```

事故防止のため、既存のローカル Environment に対する `durable_backend` や `durable_task_hub_name` の事後変更は拒否します（旧形式の identity ファイルは `azureStorage` / `PocBatchHub` として扱います）。この保護機構はローカルの配置記録に基づくものであり、別端末からの直接の Terraform 操作まで防ぐものではありません。`-PlanOnly` は接続先や state backend の存在確認を伴い、backend 未作成時は bootstrap の plan のみで終了します。

## DTS の実行履歴を直接確認する

`durable_observer_principal_id` に操作者の Object ID を明示設定すると、DTS の対象 Task Hub スコープにのみ `Durable Task Data Reader` ロールを付与します（既定は `null` で権限を追加せず、`azureStorage` モードでは指定を拒否します）。Function App の実行権限と操作者による読み取り用の権限は分離しています。

補助ツール `tools/DtsProbe` は DTS エンドポイントおよび Task Hub に直接接続し、対象インスタンス ID の状態を読み取ります。`GetInstanceAsync(..., getInputsAndOutputs:false)` を使用し、関数名・状態・時刻のみを JSON 出力します。キー、接続文字列、入出力ペイロード、生の例外は表示しません。デプロイ結果ファイルの `outputs.durable_scheduler_endpoint` と `task_hub_name`、およびスモークテスト等の `validation.InstanceId` を指定して実行してください。

```powershell
dotnet run --project tools/DtsProbe -- --endpoint https://YOUR-SCHEDULER.REGION.durabletask.io --task-hub YOUR-TASK-HUB --instance-id YOUR-INSTANCE-ID
```

取得成功時は終了コード `0` を返しますが、呼び出し側で `runtimeStatus=Completed` と期待するインスタンス ID・関数名を確認してください（未登録は `2`、照会失敗は `1`）。これは構成設定値の表示とは異なり、DTS データプレーン上の実データを直接照会する検証です。また、`--history true` オプションを付与すると保持履歴のイベント件数集計も確認できます（詳細は [ツール説明](../tools/DtsProbe/README.md) および [信頼性の追加検証](reliability-validation.md) を参照してください）。

## 履歴と requestId の扱い

バックエンド間で既存の実行履歴や Durable Entity 状態を移行する機能はありません。稼働中の Function App の App Settings だけを DTS に切り替えても、旧環境の処理状態は引き継がれません。旧環境は処理完了まで維持したうえで、別環境・新しい `requestId` で DTS の検証を開始してください。

同じ Batch Pool を共有する場合も、過去に使用した `requestId` を別環境で再使用しないでください。Batch の Job ID は `requestId` から決定論的に決まるため、別バックエンドであっても同じ ID を使うと過去の Batch Job と衝突します。

DTS の標準ペイロード上限は 1 MB、インスタンス ID は最大 100 文字の印字可能 ASCII です。本 PoC では小さな状態メタデータと Blob 参照のみを受け渡し、インスタンス ID は 44 文字のハッシュ形式としているため、大容量の入力・結果を Durable の状態へ直接埋め込む構成にはなりません。

Consumption SKU の完了履歴保持上限は 30 日です。受付 Blob・Entity 状態・オーケストレーション実行履歴の保持期限はそれぞれ個別に管理されるため、保持期間経過後の再送や過去 ID の再利用は保証しません（また、Entity 状態が 30 日で確実に消去されるという意味でもありません）。既存の「全世代の Main オーケストレーション起動履歴を取得できないため `admission20` は `NotRun` とする」という評価も、この追加のみでは解消しません。

## ローカル設定例と実接続の確認

`src/Poc.Functions/local.settings.scheduler.json.example` を `local.settings.json` にコピーすると、ローカルの DTS エミュレーター（`localhost:8080` / Task Hub `default`）を使用できます（Functions ホスト用に Azurite も必要です）。`Authentication=None` はローカルエミュレーター専用であり、Azure 用設定には使用しないでください。Batch および結果 Blob の設定は実際の検証先に置き換え、開発者の認証・権限を準備してください。なお、エミュレーターはインメモリ動作のため、プロセス停止をまたぐ永続性の実証には使用できません。

Azure 実環境では、DTS 配置・Managed Identity 接続・12 個の Functions 登録、単一 / 3 Task・分類・意図的失敗・入力競合・監視期限超過（`timeout`）の各シナリオ、および Batch Task 実行中の Functions ホスト再起動復旧を確認済みです。今後は ホスト停止中の Batch 側安全網（`MaxWallClockTime` 単独停止）や履歴保持期限の影響を別途確認します。ローカルの Mock・ビルド・publish の成功と、Azure 実機の証跡は明確に区別して管理します。

検証結果ファイルの `settings.durableBackend` と `settings.taskHubName` はデプロイ構成設定の記録であり、DTS サーバーへの実接続を独立に証明する欄ではありません。

## 公式の参照先

- [Functions の DTS 構成](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-task-scheduler/quickstart-durable-task-scheduler)
- [Managed Identity 接続](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-configure-managed-identity)
- [保存先の比較・移行制約](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-storage-providers)
- [Scheduler、提供リージョン・制限](https://learn.microsoft.com/en-us/azure/durable-task/scheduler/durable-task-scheduler)
- [SKU、課金と保持上限](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler-billing)
- [Scheduler の ARM / AzAPI 定義](https://learn.microsoft.com/en-us/azure/templates/microsoft.durabletask/2026-02-01/schedulers)
