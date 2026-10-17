# 信頼性の追加検証

更新日: 2026-10-17

本ドキュメントに記載する追加機能（再起動復旧ランナー、タイムアウト時の Batch 制約照合、DTS 保持履歴のイベント集計）は、現時点でローカルの Fake および単体試験まで確認済みです。Azure 実環境上での Function App 再起動復旧試験および期限停止試験は未実行であり、確認済みの DTS 基本動作（標準 5 シナリオ Pass）とは明確に区別して管理します。

---

## 1. Function App の再起動復旧検証

`scripts/Invoke-PocRecoveryValidation.ps1` は、長時間の待機を伴う単一 Task のワークフローを開始し、Batch Task および Durable オーケストレーションの双方が `Running` 状態にあることを検証 VM から確認したうえで、操作者の Azure CLI 資格情報を用いて対象の Function App のみを再起動（`az functionapp restart`）します。再起動後、開始 API の再送は行わずに再度 VM から状態を照会し、同一のオーケストレーションインスタンスと Batch Task がそのまま完了へ至ること、および作成時刻・Task ID・fingerprint・結果 Blob・最終 `manifest.json` の整合性が維持されていることを検証します。

- **事前の権限反映**:
  本ランナーを実機で実行する前に、現行 IaC（`infra/validation-vm.tf`）へ追加した `Microsoft.Web/sites/host/listkeys/action` 権限を、検証 VM の Function スコープ用カスタムロールへ反映（`terraform apply`）する必要があります。これは Durable の状態照会 URL を再構築するために system key を取得する権限であり、host key の読み取りも含みます。なお、検証 VM に対して Function App の `restart` / `start` 権限は付与しません。取得したキーや管理 URL は VM のメモリ内でのみ使用し、転送バンドル・Blob・証跡ファイルには一切保存しません。
- **実行環境の前提**:
  専用の PoC Function App 上で他のワークフローが稼働していないことを操作者が確認してから実行してください。`-DedicatedEnvironment` スイッチはこの運用前提を明示するためのものであり、稼働中インスタンスの自動列挙や環境の専有性を機械的に保証するものではありません。また、現在の B1 App Service Plan および Batch Pool は別環境と共有しているため、他環境の試験との同時実行は避けてください。

```powershell
./scripts/Invoke-PocRecoveryValidation.ps1 `
  -DeploymentResultPath .deploy/dts-poc/<run>/deployment-result.json `
  -ExpectedFunctionAppName poc-dts40481149-func `
  -AllowFunctionRestart -DedicatedEnvironment `
  -DelaySeconds 300 -TimeoutSeconds 900 `
  -EvidenceDirectory artifacts/reliability/recovery
```

- **安全機構と証跡**:
  再起動の実行前に、対象 Function App 名、ARM ID、ホスト名、公開アクセス無効化設定、および稼働状態（Running）を照合します。また、Task 開始状態の観測時刻が古い場合は再起動を拒否します。再起動要求を行った場合は、`finally` ブロックで必ず対象 Function App の `start` を要求します（ただし異常時は復元要求自体が失敗する可能性もあるため、試験後は実際の稼働状態を確認してください）。
  実行結果は `before/summary.json` と `after/summary.json` に加え、再起動要求の開始・完了時刻、管理 API の成否、復元 `start` 要求の成否をまとめた `recovery-result.json` に保存されます。
- **検証範囲の境界**:
  本試験で検証するのは「ARM 経由の `restart` 操作後にワークフローが継続・完了すること」であり、特定ワーカープロセスの強制クラッシュや Durable Entity のチェックポイント確定前後の瞬間を狙った中断を証明するものではありません。また、Durable Activity は再実行され得る（at-least-once）仕様であるため、Activity の呼び出し回数ではなく「同一の Batch Job / Task が維持され、最終成果物の整合性が保たれること」を合否基準とします（参考: [公式 Replay の説明](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-orchestrations)、[Activity の実行保証](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-types-features-overview)）。

---

## 2. タイムアウトと Batch 側の停止制約（`MaxWallClockTime`）照合

独立検証ランナーの `timeout` シナリオでは、ワークフローの監視期限設定より長く待機する Task を投入し、オーケストレーションが `Failed`（業務ステータス `TimedOut`）となること、対象の Batch Job が `Completed`（強制終了）へ遷移し停止確認が完了すること、および成功時の `manifest.json` が公開されないことを検証します。

今回の追加実装では、これらに加えて実際の Batch Job に設定された `constraints.maxWallClockTime` がワークフローの設定値と一致しているかを照合し、`batchMaxWallClockSeconds` として証跡に記録します（設定の欠落・不正値・不一致はいずれも `Fail` と判定します）。また、Batch Job の終了理由は既知の分類（`MaxWallClockTimeExpiry` / `Other` / `Unobserved`）のみを記録し、生のサービス文字列は保存しません。

```powershell
./scripts/Invoke-PrivatePocValidation.ps1 `
  -DeploymentResultPath .deploy/dts-poc/<run>/deployment-result.json `
  -Scenarios timeout -DedicatedEnvironment -TimeoutSeconds 1200 `
  -EvidenceDirectory artifacts/reliability/timeout
```

現在の既定設定（監視期限 15 分）で検証する場合は、停止確認用の猶予（180 秒）を加味した待機時間（`-TimeoutSeconds 1200` 等）を指定してください。本ランナーは Function App の設定自体を変更しません。

なお、Batch 側の `MaxWallClockTime` は「Job 作成時刻」からの経過時間であり、ワークフロー側の監視期限とは起算点が異なります。また、「Job に `MaxWallClockTime` が正しく設定されていることの確認」と、「Function App ホストを停止した状態で Batch 側の安全網（`MaxWallClockTime`）によって Job が自動停止することの実証」は別の検証項目であり、後者は今後の残作業です（参考: [Create Job API](https://learn.microsoft.com/rest/api/batchservice/jobs/create-job)、[Batch のエラー・終了理由](https://learn.microsoft.com/en-us/azure/batch/batch-job-task-error-checking)）。

---

## 3. DTS の保持履歴観測と 20 並列受付（`admission20`）の限界

`tools/DtsProbe` に `--history true` オプションを指定すると、対象インスタンス ID に対して DTS から返却された実行履歴のイベント件数を集計できます。

```powershell
$env:AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
dotnet run --project tools/DtsProbe -- `
  --endpoint https://<scheduler>.japaneast.durabletask.io `
  --task-hub DtsPocHub --instance-id <instance-id> --history true
```

実行者には対象 Task Hub スコープの `Durable Task Data Reader` ロールが必要です。本ツールは取得した履歴から「イベント総数」および「開始・完了・停止・ContinueAsNew の各ライフサイクルイベント件数」のみを集計して出力し、イベントの入出力ペイロード本文は一切表示しません。また、出力には `scope=ReturnedInstanceHistoryOnly` および `allGenerationsObserved=false` を明示します。

本ツールが使用する [Durable Task Client SDK 1.26.0](https://github.com/microsoft/durabletask-dotnet/blob/v1.26.0/src/Client/Core/DurableTaskClient.cs) の履歴取得 API は `instanceId` のみを受け取る仕様であり、公開契約上、過去に存在したすべての世代（全 `executionId`）を列挙・網羅する保証はありません。そのため、`executionStartedCount=1` が観測された場合であっても、それを「全世代を通じた厳密な 1 回限りの起動」の証明へ格上げすることはせず、`admission20` シナリオは引き続き `NotRun` として扱います。
