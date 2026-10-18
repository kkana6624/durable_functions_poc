# 残りの検証・実装計画

更新日: 2026-10-19。最新コード: 15関数コード（CancelWorkflow含む）のデプロイおよび全6シナリオ閉域実機回帰完了（A案完了、V12利用者キャンセル実機実証合格）、ならびに **B案: Microsoft Entra ID によるサービス間認証および認可の Azure 実機プロトタイプ検証完了（Pass）**。実機検証済みコード基準: `.deploy/poc/20261018-211336-1471`（15関数全件インデックス登録確認・スモークテスト合格: `deploy-27855cc869764f62abf22e94d3e7c2f1`、閉域検証VM実機回帰: `artifacts/private-validation/20261018-124022-0449/summary.json`、全6シナリオPass: `single`, `three`, `classification`, `failure`, `conflict`, `cancellation`）、および `.deploy/poc/20261019-001001-3480`（EasyAuth v2 有効化、閉域検証VM認証検証: `artifacts/private-auth-validation/20261018-160545-4455/summary.json`、終了コード 0、Pass）。
- **最新コードの実機到達点**: 独自開始・状態・結果・取消（`CancelWorkflow`）API、開始応答の管理URL除去、固定HTTPエラー、Activity例外の固定message・innerなし変換、公開成果物の完全性検証、受付・所有者一体化（`admission.json` v2）、opt-in EasyAuth 認可アダプター・IaC・認証 runner（`Invoke-WorkflowAuthValidation.ps1`）、**V12 利用者キャンセル（Job Cancellation）**（Blob 取消永続化、Durable 外部イベント通知、Batch 有界停止、成果物公開排他調停、所有者認可、runner への `cancellation` シナリオ追加）、ならびに **B案（Entra ID サービス間認証・認可）**（App 登録 `poc-workflow-api`、App Role `Workflow.Execute`、optional claim `idtyp: app`、VM Managed Identity への App Role 付与、EasyAuth v2 による 401 遮断、所有者伝播 403 遮断、正当トークン通過）が実装・検証され、Azure 実機環境（`poc` 環境・Azure Storage バックエンド）へのデプロイおよび閉域検証 VM からの各実機検証において終了コード 0 で合格しました。
- **実機検証に伴う不具合修正と堅牢化**:
  - `CancelWorkflow` において HttpRequestData の非シーク可能ストリーム（`CanSeek == false`）に対する `request.Body.Length` 呼び出しによる 500 エラーを修正（`CanSeek` ガードおよび `JsonException` 安全ハンドリング）。
  - `Invoke-PocValidation.ps1` における `Set-StrictMode -Version Latest` 下での `customStatus.termination` / `terminationErrorCode` の安全なプロパティアクセスへの改修、および CancelWorkflow 専用キー取得フォールバックの追加。
  - `Invoke-PrivatePocValidation.ps1` および `Deployment.Common.ps1` における Azure CLI（`az.cmd`）標準インストールパスへのフォールバック対応。
  - `Deployment.Common.ps1` の `ConvertTo-DeploymentHashtable` における文字列配列が `[pscustomobject]` 判定により `@{ Length = ... }` ハッシュテーブル化する問題の修正（プリミティブ／文字列ガード追加）。
  - `Invoke-WorkflowAuthValidation.ps1` のポーリング許容ステータスへの `Preparing` / `Publishing` 追加とアサーション詳細化。
- **確定した実機検証済み境界**:
  - `V01`（15関数パッケージデプロイ・Managed ID接続・スモークテスト合格: `deploy-27855cc869764f62abf22e94d3e7c2f1`）
  - `V02`（単一Task投入および3 Tasks並列集約・マニフェスト整合性Pass）
  - `V03`（非ゼロ終了42・Failed確定・成果物マニフェスト非公開Pass）
  - `V04`（別入力409 Conflictおよび同一入力同一所有者の冪等受付・再送Pass）
  - `V12`（利用者キャンセル: 202受理、cancel.json先行永続化、Batch completed有界停止、manifest非公開、冪等200応答の実機完全実証Pass）
  - `V11`（Entra ID サービス間認証・認可プロトタイプ実機検証Pass: EasyAuth v2 401プローブ遮断、認可済みトークン下での所有者/組織不整合403遮断、正当なVM Managed Identityトークンによる202/200完走確認。※App Role不足トークン等の実機拒否試験、および稼働パッケージ同定は未検証）
  - カテゴリ A（追加の耐障害性・運用系検証: `V05` Function App 再起動復旧、`V07` タイムアウト監視停止、`V08` ホスト停止中 Batch MaxWallClockTime 安全網、`V09` 0台オートスケール先行投入）についても過去に実機Pass済み。
- **残る未完了項目（カテゴリ B・C）**:
  - **V11 認可境界の追加検証**: 有効だが App Role（`Workflow.Execute`）を持たない Entra トークンや未許可 Client ID による実機 403 拒否試験、稼働パッケージの暗号学的同定（`livePackageVerified: false` の解消）。
  - **V10（実 C++ バイナリ連携）**: 固定 C++ 計算バイナリの実行、Blob 確定入力（SHA-256/ETag）、計算出力のハッシュ照合・業務アプリ側許容誤差検証（C案）。
  - **T09（大容量出力の Blob 直送）**: Azure Batch `OutputFiles` による Blob Storage 直接アップロード、転送完了とハッシュ整合性確認後のマニフェスト公開（C案）。
  - **V00（全面閉域化・オンプレミス接続）**: Storage / Batch を含む全面閉域化、ExpressRoute/VPN オンプレミス接続。
  - **DTS（Durable Task Scheduler）環境**: 最新 15 関数コード、cancellation、および認証設定の DTS 環境での実機回帰（過去の DTS 検証は 12 関数時点）。

> **注意:** 本書は Azure 実機検証および後続の実装フェーズに関する計画書です。ローカル環境で動作する独立検証ランナー（Fake / Mock）による検証結果と、Azure 実環境への接続を伴う実証試験の合格判定は明確に区別して管理します。Functionの受信閉域化・検証VM・15関数コード・利用者キャンセル・Entraサービス間認証プロトタイプは実機到達点です。App Role不足等の追加拒否試験、Storage / Batchの全面閉域化、実C++ファイル入出力は後続作業です。

---

## 1. 運用前提と目標

- **想定するシステム連携経路**
  - 利用者はオンプレミスのWebフロントエンドからデータ投入およびジョブ開始の要求を行います。
  - 通信経路は `ブラウザ → オンプレミスWebバックエンド → AzureジョブAPI → Durable Functions / Azure Batch / Blob Storage` を想定しています。オンプレミス側のWebフロントエンドを全面的に新規作成するのではなく、既存のWebバックエンドとの連携インターフェース（API境界）を整備します。

- **確定している前提とスコープの境界**
  - **業務環境の前提**: Windows計算ノード上でのC++アプリケーション実行、Blob Storageを介したファイル入出力、金融オプションシミュレーション用のシナリオデータ入力、およびオンプレミスからAzureへの既存ExpressRoute接続を前提とします。
  - **個人検証環境の前提**: 業務環境と同等の回線・DNS・アクセス権限を利用できるとは仮定しません。まず VNet 内の検証 VM から Private Endpoint 経由で Function を呼び出し、公開ネットワークアクセスを無効化した状態でワークフロー制御を検証します。将来的にVPNまたはExpressRouteを共通VNetへ接続します。詳細は [接続方式と共通ネットワークの設計](network-connectivity-design.md) を参照してください。
  - **未確認・仮定事項**: オンプレミス側のWebバックエンド技術スタックはC#と仮定していますが、確定情報ではありません。また、シナリオ入力／計算出力の最大データサイズ、企業内認証基盤、既存C++アプリのCLI引数仕様や依存DLLについても現時点では未確定です。
  - **責務の境界**: Azure側は汎用的なシナリオ実行・オーケストレーション基盤として設計し、金融工学の価格評価モデルやC++計算ロジック自体の再実装は行いません。

- **ネットワーク・データ転送・検証の基本方針**
  - **ExpressRouteと閉域網**: 既存のExpressRoute回線を再利用する前提ですが、回線が存在することと各PaaSのPrivate Endpoint・DNS・RBAC権限が整備済みであることは同義ではありません。そのため、Private PeeringからFunctionおよびStorageのPrivate Endpointへのネットワーク到達性、オンプレミスDNSフォワーダー、Functionsの送信用VNet Integration、ならびにBatch／Storageへのアウトバウンド送信経路を初期段階で確定させます。
  - **段階的なデータ投入実装**: 初期実装としては、シナリオJSON等の数KB〜数MB程度のファイルをオンプレミスWebバックエンドからBlob Storageへ直接／中継保存する方式を採用します。将来的に大容量ファイルやネットワーク切断時の中断・再開（レジューム）が必要と判明した段階で、ブロック分割アップロードや永続アップロードセッションの仕組みを追加します。
  - **検証の段階分け**: 現在のPoC実装は「JSONリクエストから固定のPowerShell処理を起動し、整数値 `1` を取得する」最小構成であり、実業務ファイルの投入・計算・結果回収は未検証です。そのため、「ジョブ制御・オーケストレーションの実証」と「実業務データ処理・バイナリ実行の実証」を明確に分離し、それぞれ別個の合格基準として定義します。

---

## 2. 実施順と成果物

| 段階 | 実施内容 | 完了条件 |
|---|---|---|
| 既存の到達点 | 検証runner、Function受信閉域化・検証VM、主要耐障害性検証 | 過去のAzure実証はV05/V07/V08/V09等。全世代の起動履歴を網羅できないadmission20はNotRunを維持 |
| 15関数実機合格 | 独自API契約（全15 Functions）、エラー遮蔽、受付・所有者一体化（admission v2）、V12利用者キャンセル実機実証、閉域全6シナリオ実機回帰 | **【Azure 実機検証完了（Pass）】** 15関数コードデプロイ（`.deploy/poc/20261018-211336-1471`、スモーク合格）および閉域検証VM回帰試験（`artifacts/private-validation/20261018-124022-0449/summary.json`、全6シナリオPass）により完全合格 |
| 1 | V11: 所有者と受付を単一の不変レコードへ統合 | **【ローカル完了・実機回帰Pass】** 条件付き作成で入力・所有者を同時確定し、所有者なし旧受付との並行競合を防止。実機回帰（conflictシナリオ）にて409競合調停・再送を確認 |
| 2 | V11: Entra/App Role認証境界（B案） | **【Azure 実機プロトタイプ検証完了（Pass）】** 信頼されたバックエンド主体（VM Managed Identity）と利用者・組織の伝播契約、EasyAuth v2 による未認証 401 遮断、所有者不整合 403 遮断、正当トークン通過を実機実証（証跡: `artifacts/private-auth-validation/20261018-160545-4455/summary.json`）。※App Role 不足トークン実機拒否試験および稼働パッケージ同定は未検証 |
| 4 | V12: 利用者取消 | **【Azure 実機検証完了（Pass）】** 要求永続化、投入停止、Batch停止確認、取消とmanifest公開の排他調停、所有者認可、runner cancellationシナリオがAzure実機で完全実証 |
| 3 | V11/V12: 15関数コードのAzure配置・閉域VM実機回帰（A案） | **【Azure 実機検証完了（Pass）】** 15関数コードのAzure配置、全6シナリオ閉域回帰（single/three/classification/failure/conflict/cancellation）が終了コード0で合格 |
| 次の焦点 | 発展的検証（C案 / 認証厳格化）または PoC 完了報告 | **【次なる作業候補】**<br>・**C案**: 実 C++ バイナリ連携（`V10`）および Batch `OutputFiles` 大容量出力直送（`T09`）の実機検証<br>・**V11 認可厳格化**: App Role 不足トークンによる実機 403 拒否試験、稼働パッケージ暗号学的同定<br>・**DTS 環境回帰**: 15 関数コードおよびキャンセル・認証の DTS 実機回帰<br>・あるいは、これまでの実機検証成果（V01〜V09, V11, V12）を総括する **PoC 完了報告書・技術評価報告書** の作成 |
| 5 | V10/T09: C++入出力・出力直送 | 固定バイナリと入力version/ETag/size/SHA-256を契約化。ResourceFiles/OutputFilesを用い、転送と整合性確認後だけmanifest公開 |
| 6 | V00・運用検証 | Storage/Batch全面閉域化、DNS・SCM・オンプレ接続を段階検証。その後Spot回収・一時障害注入・保持/清掃を拡充 |

具体的な次の実装と完了条件は [次期実装計画](next-implementation-plan.md) を参照してください。
### 並列化と直列化の進め方

- 検証ランナー・ローカルテストの拡充と接続方式の設計は並行して進めます。既存のネットワークIaCと実機実証はFunction受信と検証VMまでです。受付競合対策と認証契約を先に確定し、Storage / Batchの全面閉域化、実C++計算バイナリ、および利用者取消は上記の順で進めます。
- **最新コードの実機検証** においては、基本正常系の疎通が確認された後、競合試験・分類試験・データ整合性試験をそれぞれ別個の `requestId` を用いて並列に実施可能です。
- 一方で、Functionsホストの停止、RBAC権限の一時変更、障害注入を伴う試験は同一環境内の他ワークフローと干渉するため、必ず直列化して実施します（これらを並列で検証する場合は、Function App、Storage Account、Task Hub を環境ごとに完全に分離する必要があります）。

---

## 3. 追加実装の範囲

### 3.1 ExpressRouteと閉域構成

接続方式、IaCの所有権境界、DNS構成、および移行・切替順序は [接続方式と共通ネットワークの設計](network-connectivity-design.md) を正とします。Function受信用Private Endpoint・DNSと検証VMはIaC化・過去の実機検証済みです。以下のうち送信用 VNet Integration、Storage / Batch の Private Endpoint、回線接続、任意の小型 WebApp は後続フェーズです。また、既存Poolの完全な無変更維持や無停止切替を保証するものではありません。

- **Functionsの閉域化**: 既存回線・Private Peering・接続先VNetを確認し、Function受信用Private Endpointと送信用VNet Integrationを分離してIaCへ追加します。現在のApp Serviceプラン（B1）は両機能に対応しているTierであるため、閉域化のみを理由としたプラン変更は行いません（参考: [Private Endpoint](https://learn.microsoft.com/en-us/azure/app-service/overview-private-endpoint)、[VNet Integration](https://learn.microsoft.com/en-us/azure/app-service/overview-vnet-integration)）。
- **StorageのPrivate EndpointとDNS**: 入出力Blobに加え、Durable FunctionsおよびEntityの内部状態管理に使用するBlob、Queue、TableのPrivate EndpointとPrivate DNSを設定し、オンプレミスDNSからAzure側Private DNS Resolverへの条件付き転送を整備します（参考: [オンプレ接続構成](https://learn.microsoft.com/en-us/azure/architecture/web-apps/guides/networking/access-multitenant-web-app-from-on-premises)、[Functions Storage](https://learn.microsoft.com/en-us/azure/azure-functions/storage-considerations)）。
- **SCM配置経路と外部通信の確認**: Private SCMのDNS名前解決とデプロイエージェントの到達性を確認した上で、パブリックアクセスの遮断を実施します。また、Microsoft Entra ID、ARM管理プレーン、監視テレメトリ送信、パッケージ取得の各通信経路については、ExpressRouteのPrivate Peeringとは別系統として事前に棚卸しを行います（参考: [SCM要件](https://learn.microsoft.com/en-us/azure/app-service/overview-private-endpoint#kuduscm-endpoint)、[ExpressRoute routing](https://learn.microsoft.com/en-us/azure/expressroute/expressroute-routing)）。
- **Batchの閉域化**: Azure Batchを閉域化する場合、Job/Task操作用の `batchAccount` と計算ノード管理用の `nodeManagement` を別々のサブリソースとして設計します。既存Poolが接続するVNetと通信モード（Simplified通信）を確認し、専用の試験環境でノード管理通信およびBlob入出力を実証した上で本番環境へ適用します。現行IaCはパブリックエンドポイント構成であるため、本設計の追加が必要です（参考: [Batch Private Endpoint](https://learn.microsoft.com/en-us/azure/batch/private-connectivity)、[Simplified通信](https://learn.microsoft.com/en-us/azure/batch/simplified-compute-node-communication)）。

### 3.2 オンプレ向けAPIと認証

- **サービス間認証と利用者認可の分離**: ブラウザはオンプレミスWebバックエンドへ直接アクセスし、WebバックエンドがAzureジョブAPIを仲介呼び出しします。WebバックエンドとAzure間の「サービス間認証」と、エンドユーザーの「ジョブ所有者認可」は明確に分離して定義します。
- **Entraベースのサービス間認証**: Azure側のAPI呼び出しにはMicrosoft Entra IDベースのサービス間認証を標準候補とします。オンプレミス環境からはAzure Managed Identityを直接利用できないため、既存の企業内認証基盤に応じてクライアント証明書認証または対応するワークロードID連携を選択します。
- **管理URL・秘密情報の隠蔽**: ジョブの開始・状態照会・結果参照・取消を業務APIとして抽象化して提供し、Durable Functions管理URLやシステムキー（Function Key）はサーバー内部でのみ保持・使用してブラウザへは一切返却しません。
- **業務ステータスの正規化**: ジョブのステータスは `Accepted` / `Preparing` / `Running` / `Publishing` / `Succeeded` / `Failed` / `TimedOut` / `Cancelling` / `Cancelled` 等の業務ステータスとして正規化し、Durable Functionsの内部的な `runtimeStatus` と混同しないように設計します。受付記録が存在するもののMainオーケストレーション登録前の状態は `Accepted` として返却し、存在しないジョブ（404 Not Found）と明確に区別します。また、Batchの停止確認が未完了である状態は、安易にSucceededやCancelledとは判定せず、停止確認待ちとして返却します。
- **ジョブ所有者のアクセス制御**: ユーザーID、所属組織、およびジョブIDの対応関係を台帳として保存し、他ユーザーによるジョブ照会・取消・結果取得を確実に拒否します（サービス間認証の成立のみをもって利用者認可の代わりとはしません）。
- **認証方式の移行とロール確認**: Entra受信認証を導入する際は、現行のFunction Key方式からの移行計画を策定します。EasyAuthによるトークン検証に加え、APIレイヤーでApp Roleを検証し、信頼されたオンプレミスバックエンドからのユーザーコンテキストの伝播仕様を定義します（参考: [workload認証](https://learn.microsoft.com/en-us/entra/architecture/authorize-applications-resources-workloads)、[App Service認証・認可](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-provider-aad)）。

### 3.3 入力データと計算・出力

- **アップロード状態の管理**: データ投入ライフサイクルとして「アップロード開始」「アップロード中」「入力確定」の各状態を厳密に区別し、転送中断や未完了のデータからジョブが誤って起動されることを防止します。
- **シナリオと実行条件の契約化**: シナリオスキーマバージョン、シナリオID、計算パラメータ、参照データのバージョン、およびC++計算バイナリのビルド／バージョンを実行条件契約として明確化します。乱数を用いるシミュレーションの場合は再現用シード値等も記録します（具体的な金融価格評価モデルは既存C++アプリ側の責務とします）。
- **入力Manifestと改ざん防止**: 入力マニフェスト（manifest）に許可されたBlobパス、BlobバージョンまたはETag、ファイルサイズ、およびSHA-256ハッシュ値を記録し、確定後の不正なファイル差し替えを拒否します（※ETagは一意な内容ハッシュとしては扱わずSHA-256で同一性を担保します）。また、入力データ参照と計算バイナリバージョンをリクエストフィンガープリントに含めて管理します。
- **データ転送方式の選定**: データ量と転送要件に応じて、バックエンド中継方式と短寿命SASを用いたBlob Storage直接アップロード方式を比較評価します。直接アップロードを採用する場合は、最小権限・短寿命のSASトークン発行および分割転送・中断再開の仕組みを設計します。
- **Batch計算ノードの実行制御**: Windows計算ノードへ確定入力を転送する方式として Azure Batch の `ResourceFiles` 等を比較検討し、実際の計算コマンドラインは既存C++アプリの仕様に合わせて厳密に構築します。実行ファイル、依存DLL、設定ファイル、およびコンテナ／ACGイメージバージョンを固定化し、Webフロントエンドから任意のコマンドライン文字列や未許可のBlob URLを受け付けない安全な設計を徹底します。なお、共有PoolへのManaged Identity付与やイメージ更新は事前調整なしには前提としません。
- **大容量出力の永続化**: 大容量の出力成果物は、現行PoCで用いている数KB専用のTask File API経由の転送から切り離し、Azure Batch の `OutputFiles` 機能等によるBlob Storage直接アップロードを検証します。成果物のアップロード完了、全タスクの正常終了、および出力ハッシュの整合性を確認した上で最終マニフェスト（`manifest.json`）を公開します。
- **結果取得の認可と分離**: 計算結果のダウンロードはジョブ所有者の認可を確認した後に許可します。クライアント側での結果ダウンロード失敗によって計算ジョブ自体が再実行されることのないよう、取得処理とジョブ実行ライフサイクルを分離します。
- **BatchのStorage認証方式**: `ResourceFiles` および `OutputFiles` のストレージ認証方式として、短寿命SASトークンまたはPoolに割り当てられたManaged Identityのいずれかを選択可能です。WindowsノードであってもUser-assigned Managed Identityの利用が可能であるため、既存PoolへのManaged Identity設定可否を管理者に確認した上で最適な方式を選定します（参考: [入力公式](https://learn.microsoft.com/en-us/azure/batch/resource-files)、[出力公式](https://learn.microsoft.com/en-us/azure/batch/batch-task-output-files)）。

### 3.4 検証runnerと証跡

- **検証スクリプトの分離**: 検証スクリプト `scripts/Invoke-PocValidation.ps1` は、デプロイ済みの `deployment-result.json` と検証シナリオ定義を入力として動作します。待機タイムアウトおよびポーリング間隔の既定値はそれぞれ900秒および10秒です。インフラのプロビジョニングから完全に分離して実行可能であり、意図的な失敗シナリオでは期待される終了コード42および成功マニフェストの不在をもってテスト合格と判定します。
- **初期シナリオと操作境界**: 初期シナリオでは、単一Task投入、3Task並列集約、light/heavy/auto軽重分類、非ゼロ終了（意図的失敗）、同一入力20並列同時受付・完了後再送、異なる入力の409競合、および監視期限超過を対象とします。検証ランナー自体は Terraform apply、ZIPデプロイ、Batch Pool構成変更、RBAC変更、ホスト停止、履歴パージ、リソース自動削除などの破壊的変更操作は一切行いません。なお、監視期限試験の実施には専用検証環境の指定とパラメータ確認を前提とします。
- **結果ステータスの契約（終了コード定義）**: 選択された全試験がPassの場合は終了コード0、1件でもFailがある場合は1、FailがなくNotRunが含まれる場合は2を返却します。実行結果は `summary.json` および可読性の高い要約テキストとして保存します。20並列同時受付試験では、インスタンス数やTask数のみならずMainオーケストレーションの実行履歴を照合し、厳密な1回起動が観測できない場合は合格とは見なしません。
- **証跡の記録項目**: 試験ケース単位で、実行ID、UTC実行時刻、Gitコミットハッシュ／差分識別情報、パッケージハッシュ、機密情報を除外した設定情報、HTTPレスポンス、Durable Functions状態、Batch Job/Task状態、Blob出力内容、および関連ログを証跡として保存します。
- **秘密情報の除外と404上限**: Functionキー、認証Authorizationヘッダー、SASトークン、Durable管理URL、および機密を含む可能性のある未加工の例外・HTTP本文は証跡に保存せず、あらかじめ許可された安全なフィールドのみを抽出して記録します。テストが途中失敗した場合でも結果要約を確実に保存します。状態照会GETにおける `404` は上限回数付きのインスタンス起動待ちとして扱い、401/403は認証・権限エラーとして即座に失敗判定とします。また、ランナー側の待機期限到達のみをもってBatch側が正常停止したとは見なしません。
- **故障注入と実障害の区別**: 通信タイムアウト、429スロットリング、503一時障害、Job停止確認失敗、アップロード中断などの異常系は、テスト専用の環境構成でシミュレートします。Azure SDKのテスト用Transport等を用いた故障注入による検証と、実Azure環境で実際に観測された障害は、テスト結果レポート上で明確に区別して記載します。
- **Entity中断試験の記録方針**: 通常のFunctionsホスト再起動操作のみでEntityのチェックポイント確定前後をミリ秒単位で狙い撃ちすることは困難であるため、ローカルで制御可能なランタイムテストと実Azure環境での再起動試験を併用し、実機環境において実際に観測できていない区間については「未検証」として客観的に記録します。なお、テスト目的でEntity内部に副作用を伴う外部I/Oを追加することはしません。

### 3.5 取消と清掃

- **取消（キャンセル）の競合制御**: ジョブの取消要求を永続化し、ジョブ開始前、Task投入中、ノード実行中、および結果公開処理との競合発生時の挙動を明確に定義します。Durableオーケストレーションを先に外部から強制Terminateし、Batch停止Activityが実行不能になってリソースが放置されるようなアンチパターンは回避します。
- **取消確定とManifest公開の順序制御**: 「取消要求の受理」と「Batch側の停止完了」を明確に区別し、再送可能な冪等インターフェースとします。取消確定とマニフェスト公開は同一の排他制御ポイントで順序関係を調停し、取消を確定させたジョブの成果物が後から誤って公開されることを防止します。また、すでに正常完了したジョブに対する取消要求は既存の出力成果物を破壊しません。
- **安全な清掃対象の確認**: ジョブの清掃処理（クリーンアップ）を行う際は、実行台帳に記録された正確なBatch Job ID、Pool ID、およびフィンガープリントを照合し、関係のない共有Pool、計算ノード、またはTerraform backendリソースへ影響を与えない安全な設計とします。
- **履歴保持とID再利用の扱い**: 通常のクリーンアップ処理では、重複リクエスト抑止のためにリクエスト受付記録、Entity状態、およびMainオーケストレーション実行履歴を一定期間保持します。データ保持期限（TTL）を導入する場合は、重複抑止用インデックスと大容量結果データの保持ポリシーを分離して設計します。なお、過去の `requestId` の再利用運用については別途仕様を策定します。

---

## 4. 実Azureとオンプレ統合試験

| ID | 試験項目 | 合格基準 |
|---|---|---|
| V00 | ExpressRoute・Private DNS・デプロイ経路 | オンプレミス実ホストからFunction/Blobを想定Private IPで名前解決および疎通確認。API、Storage各サービス、SCMデプロイ経路、および必要な管理プレーン認証経路を個別に実証 |
| V01 | PlanOnly・デプロイ・マネージドID認証 | **【Azure 実機検証完了（Pass）】** backendの既存／未作成／アクセス拒否を厳密に区別。PlanOnly実行時にapplyやZIP配置が実行されないこと。最新15関数コードにてインフラ差分なしデプロイ、全15関数のインデックス登録、Managed Identity接続、3 Taskスモークテスト（Sum=3: deploy-27855cc869764f62abf22e94d3e7c2f1）の合格を確認（.deploy/poc/20261018-211336-1471） |
| V02 | 単一Task投入・3Task並列集約 | **【Azure 実機検証完了（Pass）】** 最新15関数コードの閉域VM実機回帰にて single（Windowsノード上でexitコード0およびresult=1返却）および three（3 Task並列集約, sum=3）の正常終了・マニフェスト整合性を実証しPass |
| V03 | 非ゼロ終了・ファイル欠損・改ざん検知 | **【Azure 実機検証完了（Pass）】** 最新15関数コードの閉域VM実機回帰にて failure シナリオ（意図的非ゼロ終了 exit code 42）による Failed 確定、成果物マニフェスト非公開を実証しPass |
| V04 | 同時受付排他・冪等再送 | **【Azure 実機検証完了（Pass）**（競合・再送）】 最新15関数コードの閉域VM実機回帰にて conflict シナリオ（別入力に対する 409 Conflict）および同一入力・同一所有者の冪等受付・完了後再送を実証しPass。（※全世代の完全起動履歴取得制限に伴う admission20 は NotRun 扱いを維持） |
| V05 | Functionsホスト中断からの復旧 | プロセス再起動後に同一インスタンスで処理が再開され、結果整合性が保たれ不要なJob/Taskが作成されないこと。受付時およびEntity処理中の中断試験は、停止位置の証跡と未観測区間を併記すること |
| V06 | 一時障害リトライ・キャンセルトークン伝播 | SDKレイヤーとDurableレイヤーのリトライ試行数を分離して記録。Durableは最大3試行（5秒・10秒待機）。認証・権限エラー等の恒久エラーおよび呼び出し元キャンセル時は再試行されないこと |
| V07 | 監視期限超過（タイムアウト）・停止処理 | 期限到達時にステータスがTimedOutとなり、対象Batch JobがCompletedへ遷移してマニフェストが非公開となること。権限不足や停止確認タイムアウト時は未確認ステータスとして記録されること |
| V08 | ホスト停止中のBatch側安全網 | Functionsホストが停止した状態でも、Batch側のMaxWallClockTimeによりJobが終端化されること。作成時刻、制約パラメータ、停止結果の証跡を記録 |
| V09 | 0台オートスケールPoolへの投入 | 稼働ノード0台の状態からTaskを先行投入し、自動拡張（autoscale）によりノードが起動してTaskが正常完了すること。既存共有Poolの設定は一切変更しないこと |
| V10 | オンプレミス連携・実C++実行・結果照合 | 小規模シナリオ入力1件をBlob Storageへ確定保存し、バージョン固定されたC++計算バイナリを実行してBlob出力すること。通信切断、ボタン二重クリック、画面再読込が発生しても同一ジョブとして処理され、ファイルハッシュ照合および業務アプリ側の期待値・許容誤差を満たすこと |
| V11 | 利用者認可・シークレット隠蔽（Entra サービス間認証・認可） | **【Azure 実機プロトタイプ検証完了（Pass）】** EasyAuth v2 有効化、閉域検証 VM（`poc-df40481149-validation`）からの `Invoke-PrivateWorkflowAuthValidation.ps1` 実行（`artifacts/private-auth-validation/20261018-160545-4455/summary.json`）により、未認証 401 遮断、所有者不整合 403 遮断、正当トークン通過（202/200）を確認。※App Role 不足トークンの実機 403 拒否試験および稼働パッケージ同定は未検証 |
| V12 | 利用者取消 | **【Azure 実機検証完了（Pass）】** 15関数コード（CancelWorkflow含む）のデプロイおよび閉域検証VM経由のcancellationシナリオ実機検証（artifacts/private-validation/20261018-124022-0449/summary.json）において、独自取消API（202 Accepted受理）、cancel.json（Version 1）先行永続化、Durable外部イベントによるオーケストレーション取消検知、Batchジョブ有界停止（completed遷移）、manifest.json公開抑止、Cancelled確定、同一所有者からの再送200 OK冪等応答をAzure実機環境で完全実証しPass |

| T09 | 大容量出力のBlob直送 | OutputFiles等の転送完了と成果物の存在・サイズ・ハッシュを確認した後だけmanifestを公開。結果取得の再試行で計算を再実行しないこと |

### 試験実施上の補足

- 個人環境では先に VM から Function / SCM への Private DNS 解決・HTTPS 到達性と Function 公開アクセス拒否を確認します。Storage / Batch とオンプレミス接続を含む `V00` 全体の完成は前提にしません。`V01` のデプロイ・認証と基本正常系を確認してから競合・異常系試験へ進み、将来の閉域化完了後に同一のジョブ検証を再実行します。
- `scripts/Invoke-PocValidation.ps1` は `V02`、`V03`（非ゼロ終了・異常系注入）、`V04`（同一入力 20 並列・別入力 `409 Conflict`）、`V07`（監視期限超過と `MaxWallClockTime` 設定・`TerminateJobAsync` 検証）、`V12`（`cancellation` シナリオ：取消 API 呼び出し、202/200 受理、`cancel.json` 確認、`manifest.json` 非存在確認、Batch `completed` 確認、冪等 200 確認）、および軽重分類を検証します。最新の 15 関数コード（`CancelWorkflow` 含む）において閉域検証 VM から全 6 シナリオの実機回帰試験を実施し、全件 Pass（終了コード 0、`artifacts/private-validation/20261018-124022-0449/summary.json`）を確認済みです。また、`scripts/Invoke-PocRecoveryValidation.ps1` により `V05`（Function App 再起動復旧）と `V08`（ホスト停止中の Batch `MaxWallClockTime` 安全網）、`scripts/Invoke-PocAutoscaleValidation.ps1` により `V09`（0 台オートスケール Pool への先行投入）をそれぞれ実 Azure 環境で検証済みです。なお、ローカル Fake や SDK モックによる故障注入（改ざん・欠損・RBAC 権限剥奪等）の結果を Azure 実環境障害の実証結果へ繰り上げることはせず、`docs/test-results.md` および `docs/feasibility-matrix.md` で厳密に区別します。
- `V10` はオンプレミス連携インターフェースの契約締結および追加実装の完了後に実施します。実C++バイナリが準備できるまでの間は、固定のC++検証用スタブを用いて入出力経路を先行検証可能ですが、業務アプリ自体の動作実証とは明確に区別します。
- `V09` の 0 台オートスケール検証については、Terraform（`infra/batch.tf`）の `batch_scale_mode = "autoscale"`（0 台待機・`$PendingTasks` による Spot 最大 1 台自動拡張）と `scripts/Invoke-PocAutoscaleValidation.ps1`（Dedicated 0 / Spot 2 vCPU クォータ制約下での共有 Pool 一時 0 台退避・自動拡張検証・原状回復ガード）として実装・実機検証済みです。

---

## 5. 実施前に必要な情報

1. C#を想定しているオンプレミスWebバックエンドの実構成および企業内利用者認証基盤の仕様。
2. シナリオデータ／計算出力の典型サイズおよび最大サイズ、ジョブ件数、データ保持期間、C++計算バイナリ／CLI引数仕様／依存DLL／検証用期待値データ。
3. 閉域化に向けた既存ExpressRouteまたはVPN/VNet接続環境、端末・オンプレミス側DNSフォワーダー、許可対象エンドポイント、Private Endpoint設置先サブネット、およびデプロイ実行ホストの接続性（※今回の VM 経由検証では既存 ExpressRoute は不要。検証 VM の SSH 公開鍵、SKU・クォータと VM Run Command 実行権限が必要です）。
4. 検証専用のAzureサブスクリプション／リソースグループ、利用可能な既存Batch Poolまたは新規Pool作成方針、ACGコンテナイメージ、クォータ上限、デプロイ実行者のRBAC権限。

> **注意:** 現リポジトリの `environments` ディレクトリにはサンプル設定のみが含まれています。実環境の設定とAzure CLI/認証経路を用意してから実機試験へ進みます（計画段階ではクラウド資源の作成・停止・削除を実行しません）。

---

## 6. 完了判定と後続範囲

- **完了判定基準**: 必須試験項目はすべて客観的な証跡を伴う Pass が必要です。検証環境の不足、プロセス停止タイミングの未観測、あるいはテスト用Transportによるシミュレーションのみで確認された項目については、実Azure環境での合格とは見なしません。全試験の完了後、`docs/test-results.md` および技術実現性評価レポートを更新し、残存する制約事項と本番運用手順を引き渡します。
- **今後の拡張・別要件として扱う項目**: ワークロード分類からJob作成・結果転送までを含めたワークフロー全体の厳密な実時間上限保証、Entity内部状態等を完全に削除した過去IDの再利用、旧Starterバージョンとのローリングアップデート運用、数百ノード規模での高負荷性能検証、MPI密結合計算、Fair Share、Backfilling、LSF互換スケジューリングについては別要件として整理し、まずはオンプレミス連携に不可欠なサービス間認証、入力データ確定、ジョブ取消、および大容量出力永続化の確立を最優先とします。
