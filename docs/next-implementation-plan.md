# 次期実装計画

確認日: 2026-10-18。検証済みコード基準: `main` / `9c96054`。現文書HEADは `e3d3734` です（文書・スクリプト棚卸しのみの変更）。

## 現在の到達点

今回のローカル変更では受付・所有者を `admission.json`（Version 2）へ一体化します。所有者なしも不変の属性とし、同一所有者・入力の再送のみ許可します。旧入力/所有者が整合する受付は再送時に正規化し、旧匿名受付への所有者追加、孤立所有者、新旧矛盾は拒否します。旧Starter停止後の切替が前提で、別Blobの読取りと作成、Durable通知との原子性は保証しません。認証境界・Azure配置と実機試験は未実施です。以下の `9c96054` と211件の試験結果は今回の変更前の検証基準です。

独自の開始・状態照会・結果取得API、所有者レコードの照合、開始応答の管理URL除去、HTTPエラーとActivity例外の安全化、公開成果物の完全性検証をローカル実装済みです。HTTP/ActivityのgRPC再試行対象は `Unavailable` / `DeadlineExceeded` / `ResourceExhausted` に限定しました。V11全体の認証とAzure実証は未完です。受付・所有者の競合対策は今回のローカル変更で扱います。

最新のRelease検証はPoc.Tests 209件・DtsProbe 2件、計211件成功（失敗・スキップ0件）です。Release publish（`artifacts/grpc-retry-release-publish`）も成功し、`local.settings*` の非混入を確認しました。開始URL変更時のPowerShellオフライン4組の成功と、過去のTerraform/Azure検証は [プロジェクトメモリ](project-memory.md)・[検証結果](test-results.md) に分離して記録しています。最新変更のAzure照会・再配置・実機検証は行っていません。

V05/V07/V08/V09 の Azure 成功は既存の検証記録による到達点であり、最新コミットの配置・回帰成功を意味しません。全世代の起動履歴を網羅できないため、admission20 の厳密な一意性判定は引き続き NotRun とします。

## 優先順序と完了条件

| 順序 | 実装単位 | 完了条件 |
|---|---|---|
| 1 | V11: 受付・所有者の一体化と旧レコードの扱い（ローカル変更） | 単一の不変受付レコードと旧受付の移行・拒否条件をローカル検証。旧Starter停止後の配置とAzure実証は後続 |
| 2 | V11: 認証境界 | 信頼されたバックエンドのEntra認証とApp Role検証、所有者・組織の伝播契約を実装。未認証・権限不足・他所有者・他組織を拒否 |
| 3 | V11: 最新コードの配置・runner・実機試験 | Functions期待一覧は14件へ更新済み。認証に合わせrunnerを独自APIへ追従し、両Durableバックエンドで認証、再送、状態、結果、他利用者拒否、機密非露出と既存シナリオの回帰を検証 |
| 4 | V12: 利用者キャンセル | 要求永続化、冪等cancel API、投入停止、Batch停止確認、取消とmanifest公開の排他調停を実装。受付前後・Job作成中・Task投入中・実行中・公開直前・完了後の競合を検証 |
| 5 | V10/T09: ファイル入出力 | 入力確定、Blob version/ETag/size/SHA-256とバイナリ版を実行契約へ追加。固定バイナリ、ResourceFiles、OutputFiles直送を導入し、転送成功とハッシュ照合後だけmanifest公開 |
| 6 | V00・運用検証 | Storage/Batch閉域化、DNS・送信経路・SCM経路、オンプレ接続を段階検証。その後Spot回収・一時障害注入・保持/清掃を拡充 |

## 実装済みの境界と次の実装

開始202の本文 `statusQueryGetUri` と `Location` は独自 `/api/workflows/{requestId}` に統一済みで、`Retry-After: 10` を維持しています。内部runnerはこの契約を検証後、system keyから管理URIをメモリ内で生成して詳細診断を継続します。独自状態APIへの全面移行は各Functionの認証設計と併せて進めます。開始専用Function keyを別Functionへ流用しません。

続くローカル実装でHTTP/旧custom statusのエラー遮蔽、明示ログのコード化、状態・結果API共通の公開成果物検証を追加しました。さらにActivity wrapperでは、再試行分類とtimeout型を維持し、固定message・innerなしの例外を外へ渡します。gRPC分類はHTTP/Activityとも `Unavailable` / `DeadlineExceeded` / `ResourceExhausted` に限定しました。これは冪等操作への有界再試行方針であり、回復保証ではありません。SDK層の再試行は別の層で、今回変更していません。他のデータ経路の機密情報対策、全ランタイムログ・履歴の評価、認証、Azure検証を残します。成果物検証は照会時点の整合性であり、複数Blobのトランザクションや後続取得成功を保証しません。

今回の変更は `WorkflowAdmission.cs` の受付と所有者の永続化に絞ります。同一入力・同一所有者の再送、異なる入力・所有者・組織の競合、所有者なし旧受付の扱い、確定前後の再試行をローカル検証し、その契約へ次段階でEntra/App Role認証を接続します。

今回の単一レコード化は、別Blobだった所有者と受付の確認・claimの交差を新Starter間で防ぎます。現PoCでは所有者なし受付を維持し、所有者必須化は後続の認証済みAPI契約で検討します。旧レコードの移行は旧Starter停止後を前提とし、任意利用者による旧ジョブの所有権取得を許しません。

任意の `X-Poc-Owner-Id` / `X-Poc-Org-Id` を直接信頼する現状は利用者認証になりません。オンプレWebバックエンドを呼出元とする構成では、サービス主体の認証・App Roleと、エンドユーザー識別情報の伝播責任を分けて契約化します。企業認証基盤の確定前でも、認証済み呼出元を抽象化したローカル実装・拒否系試験は先行可能です。

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
