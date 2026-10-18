# 次期実装計画

確認日: 2026-10-18。対象: `main` / `f70051c`。

## 現在の到達点

調査開始時の作業ツリーはクリーンでした。最新コミットで独自の状態照会・結果取得 API と所有者レコードの照合が追加されています。プロジェクトメモリと既存の残作業計画はこの変更を未反映です。V11 はローカル基盤の一部が実装済みであり、認証・秘密情報隠蔽・Azure 実証の完了ではありません。

計画作成時、現 HEAD でワークフロー単体試験148件、DtsProbe単体試験2件の成功（失敗・スキップ0件）を確認しました。既存資料の93件は過去の記録です。続く開始URL契約変更ではRelease試験・publish・PowerShellオフライン4組も成功しました。後続変更を含む確定検証結果は [プロジェクトメモリ](project-memory.md) に記録します。Terraform実行、Azure照会・再配置は行っていません。

V05/V07/V08/V09 の Azure 成功は既存の検証記録による到達点であり、最新コミットの配置・回帰成功を意味しません。全世代の起動履歴を網羅できないため、admission20 の厳密な一意性判定は引き続き NotRun とします。

## 優先順序と完了条件

| 順序 | 実装単位 | 完了条件 |
|---|---|---|
| 1 | V11: 独自 API 契約・秘密情報隠蔽 | 開始応答の本文と Location を独自 status URL に変更。Function key、Durable管理URL、SAS、未加工例外を利用者応答・ログへ出さない。状態の結果取得可否を公開成果物の整合性と一致させる |
| 2 | V11: 認証境界・所有者確定 | 信頼されたバックエンドのEntra認証とApp Role検証、所有者・組織の伝播契約を実装。未認証・権限不足・他所有者・他組織を拒否。所有者なしの旧受付との並行競合を防止 |
| 3 | V11: 配置・runner・実機試験 | 追加APIをFunctions期待一覧に反映し、runnerを独自APIへ追従。両Durableバックエンドで認証、再送、状態、結果、他利用者拒否、機密非露出を検証 |
| 4 | V12: 利用者キャンセル | 要求永続化、冪等cancel API、投入停止、Batch停止確認、取消とmanifest公開の排他調停を実装。受付前後・Job作成中・Task投入中・実行中・公開直前・完了後の競合を検証 |
| 5 | V10/T09: ファイル入出力 | 入力確定、Blob version/ETag/size/SHA-256とバイナリ版を実行契約へ追加。固定バイナリ、ResourceFiles、OutputFiles直送を導入し、転送成功とハッシュ照合後だけmanifest公開 |
| 6 | V00・運用検証 | Storage/Batch閉域化、DNS・送信経路・SCM経路、オンプレ接続を段階検証。その後Spot回収・一時障害注入・保持/清掃を拡充 |

## 最初の実装の具体化

開始URL契約の変更では、開始202の本文 `statusQueryGetUri` と `Location` を独自 `/api/workflows/{requestId}` に統一し、`Retry-After: 10` を維持します。内部runnerはこの契約を検証後、system keyから管理URIをメモリ内で生成して詳細診断を継続します。独自状態APIへの全面移行は各Functionの認証設計と併せて進めます。今回のURL変更だけでは、順序1の他応答・例外の安全化もV11全体も完了しません。最新コードのAzure実証は別途必要です。

続くローカル実装ではHTTP/旧custom statusのエラー遮蔽、明示ログのコード化、状態・結果API共通の公開成果物検証を進めます。SDK例外とinner exceptionのDurable履歴・ランタイムログへの永続化対策は、再試行分類とtimeout判定を維持する別の実装単位として残します。成果物検証は照会時点の整合性であり、複数Blobのトランザクションや後続取得成功を保証しません。

最初の変更は順序1を単独でレビュー可能な範囲にします。主対象は `WorkflowFunctions.cs`、`WorkflowOperations.cs`、HTTP試験、検証runnerです。開始202の独自URL、管理URL非露出、例外の安全なエラーコード化、manifest欠損・不整合時の状態を試験します。既存runnerがDurable管理応答に依存する箇所を洗い出し、内部診断用途と利用者API用途の契約を明示します。

続く認証実装では `WorkflowAdmission.cs` の所有者レコードと受付レコードが別Blobである点を解消します。現在は所有者なし受付と所有者付き受付の並行時に、確認とclaimが交差する可能性があります。新APIでは所有者を必須にし、所有者を含む単一の不変受付レコードを推奨します。旧レコードの移行・互換方針を定義し、任意利用者による旧ジョブの所有権取得を禁止します。

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
