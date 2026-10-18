# 残りの検証・実装計画

更新日: 2026-10-18

- **カテゴリ A（追加の耐障害性・運用系検証：`V05` / `V07` / `V08` / `V09`）の実装・Azure 実機検証完了**: 検証 VM の Function スコープ用カスタムロールへ `Microsoft.Web/sites/host/listkeys/action` 権限を反映したうえで、`dts-poc`（Durable Task Scheduler バックエンド）および `poc`（Azure Storage バックエンド）の両環境にて Function App 再起動復旧検証（`scripts/Invoke-PocRecoveryValidation.ps1`、`V05`）および `timeout` シナリオにおける監視期限停止と Batch Job の `MaxWallClockTime` 制約照合（`V07`）を実機で完了しました。さらに、Function ホスト停止中の Batch `MaxWallClockTime` 安全網による自動停止（`Invoke-PocRecoveryValidation.ps1 -ValidationMode HostStoppedBatchWallClock`、`V08`）、0 台オートスケール Pool への先行投入・自動拡張完走・固定スケール復元（`scripts/Invoke-PocAutoscaleValidation.ps1`、`V09`）、および `tools/DtsProbe --history true` による DTS 返却履歴のイベント件数集計をすべて実機で実行し、合格（Pass）を確認しました（詳細は [信頼性の追加検証](reliability-validation.md) および [検証結果](test-results.md) を参照）。なお、DTS 履歴集計でも過去の全世代にわたる起動一意性は証明できないため、`admission20` シナリオの `NotRun` 扱いは継続します。
- **Durable Task Scheduler（DTS）環境の実機検証完了**: 追加 PoC である [Durable Task Scheduler](durable-task-scheduler-poc.md) を別環境（`dts-poc`）へ配置し、Managed Identity 接続、12 個の Functions 登録、3 Task スモークテスト（`Sum=3`）、標準検証シナリオ 6 件（`single` / `three` / `classification` / `failure` / `conflict` / `timeout`）、Function App 再起動復旧（`V05`）、およびホスト停止中の Batch `MaxWallClockTime` 安全網（`V08`）の Pass を確認しました（`admission20` は全世代の起動履歴取得制限により `NotRun`、summary 終了コード `2`）。あわせて、SDK による DTS データプレーン直接照会でも全対象インスタンスのステータスと履歴イベント件数を確認済みです。本環境は既存の Linux B1 Plan を読み取り専用で共用しつつ、Function App・Storage・Task Hub・Terraform state を既存環境から分離しています（バックエンド間での履歴・Entity 状態の移行は行いません）。
- **Azure Storage バックエンド環境の実機検証完了**: 従来の Azure Storage バックエンド環境（`poc`）においても、Spot コア枠 2 への増枠後にインフラ配置、RHEL 9 検証 VM の初期設定、Private SCM 配置、12 個の Functions 登録、3 Task の投入・結果回収・`Sum=3`、標準 6 シナリオ（`single` / `three` / `classification` / `failure` / `conflict` / `timeout`）の Pass、Function App 再起動復旧（`V05`）の Pass、および専用 Pool（`batch_mode='new'`）を用いた 0 台オートスケール先行投入（`V09`）の Pass まで完了しています。最新のローカル検証（.NET 単体テスト 93 件、Terraform main Mock 30 件 + bootstrap 1 件、`fmt` / `validate`、PowerShell オフライン試験 7 組）と各バックエンドの実機検証結果は [検証結果](test-results.md) に分離して記録しています。
- **今後の実機検証と実装（カテゴリ B・C）**: 今後はカテゴリ B（実 C++ バイナリ・入出力連携 `V10`、大容量出力 `OutputFiles` 直送 `T09`、オンプレ向け認証・認可 API `V11`、利用者キャンセル連鎖 `V12`）およびカテゴリ C（Storage / Batch を含む全面閉域化・ExpressRoute/VPN 接続 `V00`）、ならびに Spot 回収復旧や一時障害注入試験へ進みます。構成を変更する際は既存の remote state を用いて再 `plan` を実行し、古い保存 `plan` は使用しません。なお、クォータ承認済みであっても Spot ノードの空き容量や継続稼働は保証されず、現行のノードローカル結果回収方式では回収前にノードが失われると結果を取得できない制限があります（Task から Blob への直接永続化は [IaC 設計](one-command-deployment-inventory.md) を参照）。

> **注意:** 本書は Azure 実機検証および後続の実装フェーズに関する計画書です。ローカル環境で動作する独立検証ランナー（Fake / Mock）による検証結果と、Azure 実環境への接続を伴う実証試験の合格判定は明確に区別して管理します。また、今回の実装範囲は Function の受信閉域化と検証 VM までであり、Storage / Batch の全面閉域化、利用者向け認証付き API、および実 C++ ファイル入出力は後続フェーズの対象です。

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
| 1（既存） | デプロイと分離した検証runner・ローカル試験・接続設計 | `Pass / Fail / NotRun` の要約および機密情報を除外した証跡の生成。Azure未検証の項目をローカル成功をもって完了と見なさないこと |
| 2（今回） | VNet・検証 VM・Function Private Endpoint・Private DNS を IaC 化し、VM 経由の配置・投入・検証を整備 | 公開アクセス無効の Function と Private SCM を VM から利用できることを実機で確認した後、単一Task、3Task集約、軽重分類、非ゼロ終了、20並列同時受付、完了後再送、409競合、監視期限停止の各動作を確認。※監視期限試験は専用の検証環境でのみ実施 |
| 3 | 共通ネットワークIaCとVPNまたは既存ExpressRoute接続を整備 | Private DNS、Function受信/送信、Storage、SCMデプロイ経路、Batchノード管理通信の正常性を実証した上で、パブリックアクセスを段階的に制限 |
| 4 | 認証付きAPIとデータ投入境界を実装 | 入力データの確定後にのみジョブ起動を許可。同一入力の再送には同一ジョブで応答。Functionキー等の管理用シークレットをブラウザへ露出させない |
| 5 | C++入出力とオンプレ通し試験 | バージョン固定されたC++バイナリと入出力ファイルのハッシュ値を照合。ネットワーク切断、リクエスト再送、画面再読込が発生してもデータ整合性が維持されること |
| 6 | 復旧・障害・取消・出力永続化・運用保守を仕上げ | Functionsホスト再起動からの復旧、権限変更、通信障害、0台オートスケールPool、ジョブ取消、リソース保持・清掃の手順を専用環境で実証 |

### 並列化と直列化の進め方

- 検証ランナー・ローカルテストの拡充と接続方式の設計は並行して進めます。なお、今回のネットワークIaCは Function 受信と検証 VM までを対象とします。Storage / Batch の全面閉域化、API仕様の刷新、実C++計算バイナリの組み込み、および利用者によるジョブ取消機能は後続フェーズです。
- **段階2（実機検証）** においては、基本正常系の疎通が確認された後、競合試験・分類試験・データ整合性試験をそれぞれ別個の `requestId` を用いて並列に実施可能です。
- 一方で、Functionsホストの停止、RBAC権限の一時変更、障害注入を伴う試験は同一環境内の他ワークフローと干渉するため、必ず直列化して実施します（これらを並列で検証する場合は、Function App、Storage Account、Task Hub を環境ごとに完全に分離する必要があります）。

---

## 3. 追加実装の範囲

### 3.1 ExpressRouteと閉域構成

接続方式、IaCの所有権境界、DNS構成、および移行・切替順序は [接続方式と共通ネットワークの設計](network-connectivity-design.md) を正とします。Function 受信用 Private Endpoint・DNS と検証 VM は今回 IaC 化します。以下のうち送信用 VNet Integration、Storage / Batch の Private Endpoint、回線接続、任意の小型 WebApp は後続フェーズです。また、既存Poolの完全な無変更維持や無停止切替を保証するものではありません。

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
| V01 | PlanOnly・デプロイ・マネージドID認証 | backendの既存／未作成／アクセス拒否を厳密に区別。PlanOnly実行時にapplyやZIP配置が実行されないこと。現行PoCデプロイ後に計12件のFunctions正常登録およびManaged Identity接続を確認（追加API実装後は期待一覧を更新） |
| V02 | 単一Task投入・3Task並列集約 | Windowsノード上でexitコード0およびresult=1が返却され、3Task集約でsum=3となること。リクエスト入力、Batch Job/Task ID、および最終マニフェストの内容が整合すること |
| V03 | 非ゼロ終了・ファイル欠損・改ざん検知 | 異常発生時に明示的にワークフローが失敗（Failed）となり、成功マニフェストが公開されないこと。並列実行中の他Taskおよび部分保存された状態が記録されること |
| V04 | 同時受付排他・冪等再送 | 同一入力の20並列同時POSTに対して同一インスタンスIDで応答し、Mainオーケストレーションの実行履歴が1件のみかつBatch Taskが重複しないこと。異なる入力の同時POSTは409 Conflictとなること。ジョブ完了後の再送でも再起動されないこと |
| V05 | Functionsホスト中断からの復旧 | プロセス再起動後に同一インスタンスで処理が再開され、結果整合性が保たれ不要なJob/Taskが作成されないこと。受付時およびEntity処理中の中断試験は、停止位置の証跡と未観測区間を併記すること |
| V06 | 一時障害リトライ・キャンセルトークン伝播 | SDKレイヤーとDurableレイヤーのリトライ試行数を分離して記録。Durableは最大3試行（5秒・10秒待機）。認証・権限エラー等の恒久エラーおよび呼び出し元キャンセル時は再試行されないこと |
| V07 | 監視期限超過（タイムアウト）・停止処理 | 期限到達時にステータスがTimedOutとなり、対象Batch JobがCompletedへ遷移してマニフェストが非公開となること。権限不足や停止確認タイムアウト時は未確認ステータスとして記録されること |
| V08 | ホスト停止中のBatch側安全網 | Functionsホストが停止した状態でも、Batch側のMaxWallClockTimeによりJobが終端化されること。作成時刻、制約パラメータ、停止結果の証跡を記録 |
| V09 | 0台オートスケールPoolへの投入 | 稼働ノード0台の状態からTaskを先行投入し、自動拡張（autoscale）によりノードが起動してTaskが正常完了すること。既存共有Poolの設定は一切変更しないこと |
| V10 | オンプレミス連携・実C++実行・結果照合 | 小規模シナリオ入力1件をBlob Storageへ確定保存し、バージョン固定されたC++計算バイナリを実行してBlob出力すること。通信切断、ボタン二重クリック、画面再読込が発生しても同一ジョブとして処理され、ファイルハッシュ照合および業務アプリ側の期待値・許容誤差を満たすこと |
| V11 | 利用者認可・シークレット隠蔽 | 他のユーザーによるジョブ照会・結果ダウンロード・取消要求を確実に拒否すること。ブラウザ応答やアプリケーションログにFunctionキー等の管理用シークレットが露出しないこと |
| V12 | 利用者取消・大容量出力転送 | ジョブ開始前・Task投入中・ノード実行中の各フェーズにおける取消要求が正常に停止収束すること。出力成果物の転送失敗時に成功マニフェストが公開されないこと。結果ダウンロードの再試行によってジョブが再実行されないこと |

### 試験実施上の補足

- 個人環境では先に VM から Function / SCM への Private DNS 解決・HTTPS 到達性と Function 公開アクセス拒否を確認します。Storage / Batch とオンプレミス接続を含む `V00` 全体の完成は前提にしません。`V01` のデプロイ・認証と基本正常系を確認してから競合・異常系試験へ進み、将来の閉域化完了後に同一のジョブ検証を再実行します。
- 初期検証ランナーは `V02`、`V03`（非ゼロ終了）、`V04`、`V07`（正常な期限停止）、および軽重分類を対象とします。データ改ざん、ホスト強制停止復旧、RBAC権限変更、ネットワーク障害注入、停止確認失敗、0台オートスケール、実C++実行は後続の試験フェーズで実施します。なお、ローカルFakeやSDKモックによる故障注入の結果を、Azure実環境障害の実証結果へ安易に繰り上げることはしません。
- `V10`〜`V12` はオンプレミス連携インターフェースの契約締結および追加実装の完了後に実施します。実C++バイナリが準備できるまでの間は、固定のC++検証用スタブを用いて入出力経路を先行検証可能ですが、業務アプリ自体の動作実証とは明確に区別します。
- `V09` の検証にあたって0台オートスケールPoolが存在しない場合は、専用の検証用fixtureを別途用意します（現行Terraformコードは新規Poolを固定1〜3台でプロビジョニングする構成のため、そのままでは0台オートスケール試験には適用できません）。

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
