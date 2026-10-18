# 次期実装計画

確認日: 2026-10-18。最新HEAD: `b14b25f`。実機検証済みコード基準: `main` / `9c96054`。

## 現在の到達点

直近のコミット `e1d04d6`、`27b24b4`、`b14b25f` により、受付・所有者の一体化、EasyAuth 認可アダプター、opt-in EasyAuth IaC、および独自 API 認証 runner のローカル実装・単体試験・Mock 検証が完了しています。

- **受付・所有者の一体化（`e1d04d6`）**: `WorkflowAdmission.cs` において `runs/poc-<requestId>/admission.json`（Version 2）へ入力・instanceId・所有者・組織をまとめ、Blob 条件付き書き込み（If-None-Match）で原子的確定。同一所有者・同一入力の再送のみ許可、所有者・組織不一致時は 403、入力不一致時は 409。旧受付レコード（Version 1）からの安全な移行と旧匿名受付への所有者追加拒否をローカル検証済み。
- **EasyAuth 認可アダプター（`27b24b4`）**: 既定の `PocFunctionKey` 互換を保ちつつ、`EntraEasyAuth` モード選択時に `x-ms-client-principal` 解析、tenant / issuer / audience、許可 backend client ID、App Role、`idtyp=app`（Delegated の scope は即時拒否）、および必須 owner/org を検査するアダプター（`WorkflowAuthentication.cs`）をローカル検証済み。
- **EasyAuth IaC と独自 API 認証 runner（`b14b25f`）**: Terraform に opt-in の `workflow_auth_mode`（`none` / `strict`）および EasyAuth v2 ブロック（`modes.tftest.hcl` 37件 Pass）を追加。さらに検証 VM 内または直接到達環境から VM Managed Identity / IMDS トークンおよび Function key を用いて独自開始・状態・結果 API を検証する `Invoke-WorkflowAuthValidation.ps1`（PowerShell オフライン 8 組全件 Pass）を実装完了。
- **検証実績**: Release 単体テスト全 261 件（`Poc.Tests` 259 件＋`DtsProbe` 2 件、失敗・スキップ 0 件）、Terraform Mock 37 件、PowerShell オフライン試験 8 組全件が合格。Release publish（`artifacts/auth-boundary-release-publish`）成功・`local.settings*` 不在。
- **未実施・前提事項**:
  - 既定の `PocFunctionKey` 互換を維持し、Functions 期待数は 14 件。
  - 最新コードの Azure 再配置・実機回帰試験は未実施（以前の V05/V07/V08/V09 実機成功は `9c96054` 時点）。
  - Entra の専用 PoC API 登録・Application 限定 App Role 割当・`idtyp` optional claim 設定等の実テナント設定は自動変更しておらず、実機での偽造ヘッダー遮断や実 token 認証は未実証。
  - 厳格モードでは既存の Smoke / Private / 通常シナリオ / Recovery / Autoscale の内部診断が Bearer 未対応のため、`deploy.ps1 -Smoke` は初期検証で拒否。診断パス除外は設けず、新 runner（`Invoke-WorkflowAuthValidation.ps1`）で独自 API を検証する設計。SCM 配置は既存の Entra/ARM token 認証を維持。

## 優先順序と完了条件

| 順序 | 実装単位 | 完了条件 |
|---|---|---|
| 1 | V11: 受付・所有者の一体化と旧レコードの扱い | **【ローカル完了】** 単一の不変受付レコード（`admission.json` v2）と旧受付移行・拒否条件をローカル検証（単体テスト拡充 277 行）。旧 Starter 停止後の配置と Azure 実機切替は順序 3 へ引き継ぎ |
| 2 | V11: 認証境界（アダプター・IaC・runner） | **【ローカル完了】** 信頼されたバックエンドの Entra 認証・App Role・`idtyp=app` 検証アダプター、opt-in EasyAuth IaC、VM MI 用認証 runner（`Invoke-WorkflowAuthValidation.ps1`）を実装・検証（全 261 件・Mock 37 件・オフライン 8 組 Pass） |
| 3 | V11: Azure配置・実機回帰およびEntra実設定の準備 | **【次の実装対象】** 専用 PoC API 登録・App Role・`idtyp` 実設定の準備、専用厳格モード環境での配置・実機回帰、実 EasyAuth 偽造ヘッダー遮断検証、既存 runner の Bearer 追従検討、および新旧両 Durable バックエンドでの回帰検証 |
| 4 | V12: 利用者キャンセル | **【並行/後続の実装対象】** 要求永続化、冪等 cancel API、投入停止、Batch 停止確認、取消と manifest 公開の排他調停を実装。受付前後・Job 作成中・Task 投入中・実行中・公開直前・完了後の競合を検証 |
| 5 | V10/T09: ファイル入出力 | 入力確定、Blob version/ETag/size/SHA-256 とバイナリ版を実行契約へ追加。固定バイナリ、ResourceFiles、OutputFiles 直送を導入し、転送成功とハッシュ照合後だけ manifest 公開 |
| 6 | V00・運用検証 | Storage/Batch 閉域化、DNS・送信経路・SCM 経路、オンプレ接続を段階検証。その後 Spot 回収・一時障害注入・保持/清掃を拡充 |

## 実装済みの境界と次の実装

開始202の本文 `statusQueryGetUri` と `Location` は独自 `/api/workflows/{requestId}` に統一済みで、`Retry-After: 10` を維持しています。内部runnerはこの契約を検証後、system keyから管理URIをメモリ内で生成して詳細診断を継続します。独自状態APIへの全面移行は各Functionの認証設計と併せて進めます。開始専用Function keyを別Functionへ流用しません。

続くローカル実装でHTTP/旧custom statusのエラー遮蔽、明示ログのコード化、状態・結果API共通の公開成果物検証を追加しました。さらにActivity wrapperでは、再試行分類とtimeout型を維持し、固定message・innerなしの例外を外へ渡します。gRPC分類はHTTP/Activityとも `Unavailable` / `DeadlineExceeded` / `ResourceExhausted` に限定しました。これは冪等操作への有界再試行方針であり、回復保証ではありません。SDK層の再試行は別の層で、今回変更していません。他のデータ経路の機密情報対策、全ランタイムログ・履歴の評価、認証、Azure検証を残します。成果物検証は照会時点の整合性であり、複数Blobのトランザクションや後続取得成功を保証しません。

コミット `e1d04d6` では `WorkflowAdmission.cs` の受付と所有者の永続化を単一レコードへまとめました。同一入力・同一所有者の再送、異なる入力・所有者・組織の競合、所有者なし旧受付の扱い、確定前後の再試行をローカル検証済みです。続くコミット `27b24b4` で認証アダプターを接続し、`b14b25f` で opt-in EasyAuth IaC および独自 API 認証 runner を追加しました。

単一レコード化は、別Blobだった所有者と受付の確認・claimの交差を新Starter間で防ぎます。現PoCでは所有者なし受付を維持し、所有者必須化は後続の認証済みAPI契約で検討します。旧レコードの移行は旧Starter停止後を前提とし、任意利用者による旧ジョブの所有権取得を許しません。

互換`PocFunctionKey`モードで任意の `X-Poc-Owner-Id` / `X-Poc-Org-Id` を直接使う経路は利用者認証になりません。`EntraEasyAuth`モードは認可済みバックエンドのみから必須owner/orgを受け取り、その値を利用者セッションへ正しく結び付ける責任をバックエンドに委ねます。企業認証基盤・ID空間の確定、トークン登録・EasyAuth配置と実機拒否検証は引き続き必要です。

## キャンセル・出力で守る条件

- Durable外部イベントは通知手段であり、取消の完了証跡ではありません。起動前の要求喪失を防ぐため永続化した取消状態を各処理境界で照合し、重複通知を許容します。
- Durableの強制TerminateはActivityや子orchestrationを停止しません。Job作成中に取消された場合も作成結果を確定させてから対象Jobを停止し、遅れて作られる孤立Jobを防ぎます。
- Batch停止完了はJobの `completed` で確認します。未開始Taskは `active` のまま残り得るため、全Taskの `completed` を条件にしません。停止未確認をCancelled確定として返しません。
- 取消確定とmanifest公開の順序を同じ永続制御点で調停し、再試行・クラッシュ後も順序が逆転しない方式を設計してから実装します。
- OutputFilesのアップロード失敗でもTaskはCompletedになり得ます。exit codeだけでなくexecutionInfoのresult/failureInfo、成果物の存在・サイズ・ハッシュを検証します。

## 外部情報と検証の境界

Entraテナント・許可するバックエンド主体/App Role・利用者ID形式、C++のCLI/依存DLL/期待値、入出力サイズと保持期間、PoolのManaged Identity設定可否、オンプレDNS/回線が実機試験の前提です。未確定項目に依存しない実装は先行し、検証用C++スタブ成功と業務バイナリ成功を区別します。

各実装単位で関連単体試験と必要なPowerShell/Terraform試験を実施し、配置変更時は新しいplanを作成します。実機証跡はコミット・ソース/パッケージハッシュと紐づけ、Mock/Fake、Azure、オンプレ接続を分離記録します。V11の取消認可はV12実装後に追加確認します。

## 公式根拠

- [App Serviceの認証済みユーザー情報](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-user-identities): プラットフォームの認証モジュールが注入するIDヘッダーの信頼境界。
- [APIのscope/App Role検証](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-protected-web-api-verification-scope-app-roles): サービス呼出元のroles検証。
- [Durable外部イベント](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-external-events): 非同期配信と重複・不存在instanceの扱い。
- [Durable instance management](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-instance-management): Terminateの非同期性と子/Activity非伝播。
- [Batch Job Terminate](https://learn.microsoft.com/en-us/rest/api/batchservice/jobs/terminate-job?view=rest-batchservice-2025-06-01): Job停止と未開始Taskの状態。
- [Batch OutputFiles](https://learn.microsoft.com/en-us/azure/batch/batch-task-output-files): 直接アップロードと転送失敗の確認。

既存の設計・検証根拠: [全体計画](remaining-validation-and-implementation-plan.md)、[信頼性設計](workflow-reliability.md)、[検証結果](test-results.md)、[プロジェクトメモリ](project-memory.md)。
