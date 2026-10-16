# 残りの検証・実装計画

作成日: 2026-10-17

前回記録では、ローカル環境での各検証（単体テスト54件、Release publish、PowerShellオフラインテスト3組、Terraform Mockテスト8件）が完了しています。今回を含む最新の検証結果は `docs/test-results.md` を参照してください。Azure実環境での試験はこれから実施する段階です。

> 本書は実機検証と後続実装の計画です。独立runnerのローカル検証と、Azure実接続の合格判定は区別します。閉域ネットワーク・利用者向けAPI・実C++入出力は後続実装です。

---

## 1. 運用前提と目標

- **想定するシステム連携経路**
  - 利用者はオンプレミスのWebUIからデータの投入およびジョブの開始を行います。
  - 通信経路は `ブラウザ → オンプレWebバックエンド → AzureジョブAPI → Durable / Batch / Blob` を想定しています。オンプレミス側のWebUIを新規に作り直すのではなく、既存のWebバックエンドとの接続口（API境界）を整備します。

- **確定している前提とスコープの境界**
  - **業務環境の前提**: Windows計算ノード上のC++アプリ実行、Blob経由のファイル入出力、金融オプションシミュレーション用のシナリオ入力、およびオンプレミスからAzureへの既存ExpressRoute接続。
  - **個人検証環境の前提**: 業務環境の回線・DNS・権限を利用できるとは仮定しません。最初は現行の公開endpointでPoCを検証し、将来VPNまたはExpressRouteを共通VNetへの接続部分として差し替えます。設計は [接続方式と共通ネットワーク](network-connectivity-design.md) を参照してください。
  - **未確認・仮定事項**: オンプレミスのWebバックエンドはC#と仮定していますが詳細は未確認です。また、入力/出力の最大サイズ、認証基盤、既存C++アプリのCLI仕様や依存ライブラリも未確定です。
  - **責務の境界**: Azure側は一般的なシナリオ実行基盤として設計し、金融の価格評価モデルやC++計算本体は再実装しません。

- **ネットワーク・データ転送・検証の基本方針**
  - **ExpressRouteと閉域網**: 既存のExpressRouteを再利用する前提ですが、回線が存在していても各PaaSのPrivate Endpoint・DNS・権限が設定済みとは限りません。そのため、private peeringからFunction・StorageのPrivate Endpointへの到達性、オンプレミスDNS転送、FunctionsのVNet Integration、およびBatch/Storageへの送信経路を最初に確定します。
  - **段階的なデータ投入実装**: まずはシナリオJSON等の小さなファイルをオンプレミスのWebバックエンドからBlobへ保存する方式を初期実装とします。今後、大容量ファイルや中断再開（レジューム）が必要と判明した場合に、分割転送・永続upload sessionを追加します。
  - **検証の段階分け**: 現在のPoCは「JSONから固定PowerShell処理を起動して整数 `1` を取得する」ものであり、実ファイルの投入・計算・回収はまだ検証していません。そのため、「ジョブ制御の実証」と「業務データ処理の実証」をそれぞれ別の合格条件として定義します。

---

## 2. 実施順と成果物

| 段階 | 実施内容 | 完了条件 |
|---|---|---|
| 1（今回） | 配置と分離した検証runner・ローカル試験・接続設計 | `Pass / Fail / NotRun`の要約と秘密を除いた証跡。Azure未実施をローカル成功へ置き換えない |
| 2 | 個人環境の接続方式・専用Azure設定を確定し現行PoCを実証 | 公開endpointで単一Task・3Task・分類・非ゼロ・20並列・再送・409・期限停止を確認。期限試験は専用環境のみ |
| 3 | 共通ネットワークIaCとVPNまたは既存ExpressRoute接続を整備 | private DNS・Function受信/送信・Storage・SCM・Batchノード管理を実証してから公開アクセスを制限 |
| 4 | 認証付きAPIとデータ投入境界を実装 | 入力確定後だけ開始可能。同じ入力の再送は同じジョブに対応。管理用秘密をブラウザへ渡さない |
| 5 | C++入出力とオンプレ通し試験 | 固定versionのバイナリと入出力hashを照合。通信断・再送・再読込でも整合する |
| 6 | 復旧・障害・取消・出力永続化・運用保守を仕上げ | host復旧、権限変更、通信障害、0台autoscale、取消、保持・清掃を専用環境で実証 |

### 並列化と直列化の進め方

- runner・ローカル試験と接続設計は並列に進めます。ネットワークIaC、API刷新、C++実計算、利用者取消は今回の実装に含めません。
- **段階2** では、正常系が通った後、競合・分類・データ整合性試験を別々の `requestId` で並列実行できます。
- 一方で、host停止・権限変更・障害注入を伴う試験は同一環境内で競合するため直列化して実施します（これらを並列化する場合は、Function・Storage・Task Hubを分離します）。

---

## 3. 追加実装の範囲

### 3.1 ExpressRouteと閉域構成

接続方式・IaCの所有境界・DNS・切替順序は [接続方式と共通ネットワーク](network-connectivity-design.md) を正とします。以下は後続実装であり、今回の変更で閉域資源や任意の小型WebAppは作成しません。既存Pool無変更や無停止切替は保証しません。

- **Functionsの閉域化**: 既存回線・private peering・接続先VNetを確認し、Function受信用Private Endpointと送信用VNet Integrationを分けてIaCへ追加します。現在のApp Serviceプラン（B1）は両機能の対応tierであるため、閉域化のみを理由としたプラン変更は行いません（参考: [Private Endpoint](https://learn.microsoft.com/en-us/azure/app-service/overview-private-endpoint)、[VNet Integration](https://learn.microsoft.com/en-us/azure/app-service/overview-vnet-integration)）。
- **StorageのPrivate EndpointとDNS**: 入出力Blobに加え、Durable/Entityのhost Storage用Blob・Queue・TableのPrivate EndpointとDNSを設定し、オンプレミスDNSからPrivate Resolver等への転送を整えます（参考: [オンプレ接続構成](https://learn.microsoft.com/en-us/azure/architecture/web-apps/guides/networking/access-multitenant-web-app-from-on-premises)、[Functions Storage](https://learn.microsoft.com/en-us/azure/azure-functions/storage-considerations)）。
- **SCM配置経路と外部通信の確認**: private SCMのDNSと配置agentの到達性を用意してからpublic accessを制限します。Entra、ARM、監視、package取得の経路はExpressRoute private peeringとは別に確認します（参考: [SCM要件](https://learn.microsoft.com/en-us/azure/app-service/overview-private-endpoint#kuduscm-endpoint)、[ExpressRoute routing](https://learn.microsoft.com/en-us/azure/expressroute/expressroute-routing)）。
- **Batchの閉域化**: Batchを閉域化する場合、Job/Task操作用の `batchAccount` と計算ノード管理用の `nodeManagement` を別々に設計します。既存PoolのVNetと通信方式を確認し、専用試験環境でノード管理・Blob入出力を実証した後に適用します。現IaCは公開endpoint構成であるため、この追加が必要です（参考: [Batch Private Endpoint](https://learn.microsoft.com/en-us/azure/batch/private-connectivity)、[Simplified通信](https://learn.microsoft.com/en-us/azure/batch/simplified-compute-node-communication)）。

### 3.2 オンプレ向けAPIと認証

- **サービス間認証と利用者認可の分離**: ブラウザはオンプレミスWebバックエンドへ接続し、WebバックエンドがAzure APIを呼び出します。サービス間認証と、利用者のジョブ所有者認可は別々に定義します。
- **Entraベースのサービス間認証**: Azure側はEntraベースのサービス間認証を基本候補とします。オンプレミス環境ではAzure Managed Identityをそのまま自動利用できるわけではないため、既存認証基盤に応じて証明書または対応する資格情報連携を選択します。
- **管理URL・秘密情報の隠蔽**: 開始・状態照会・結果参照・取消を公開APIとして提供し、Durable管理URLやsystem keyはサーバー内部だけで使用します。
- **業務ステータスの正規化**: ジョブの状態は `Accepted` / `Preparing` / `Running` / `Publishing` / `Succeeded` / `Failed` / `TimedOut` / `Cancelling` / `Cancelled` 等へ正規化し、Durableの `runtimeStatus` と業務状態を混同しないようにします。受付記録がありMain登録前の状態は `Accepted` として返し、存在しないjob（404）と区別します。また、Batchの停止未確認は成功や取消完了にはせず、別情報として返します。
- **ジョブ所有者のアクセス制御**: 利用者・所属・job IDの関連を保存し、他利用者の照会・取消・結果取得を拒否します（サービス間認証のみを利用者認可の代わりにはしません）。
- **認証方式の移行とロール確認**: Entra受信認証を導入する際は、既存のFunction key方式との関係も変更します。EasyAuthのtoken認証に加えてAPIでapp roleを確認し、信頼されたオンプレミスバックエンドからの利用者情報の受け渡しを定義します（参考: [workload認証](https://learn.microsoft.com/en-us/entra/architecture/authorize-applications-resources-workloads)、[App Service認証・認可](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-provider-aad)）。

### 3.3 入力データと計算・出力

- **アップロード状態の管理**: 「アップロード開始」「アップロード中」「入力確定」を区別し、中断・未完了データからはジョブを開始しないように制御します。
- **シナリオと実行条件の契約化**: シナリオschema version、scenario ID、計算条件、必要な参照データのversion、C++アプリのbuild/versionを契約化します。乱数を使う場合はseed等の再現条件も記録します（具体的な金融モデルは既存C++アプリの責務とします）。
- **入力Manifestと改ざん防止**: 入力manifestへ許可されたBlob参照、versionまたはETag、サイズ、SHA-256を記録し、確定後の差し替えを拒否します（※ETagは内容ハッシュとして扱いません）。また、入力参照と計算アプリversionをrequest fingerprintにも含めます。
- **データ転送方式の選定**: データ量に応じてバックエンド中継と限定したBlob直接アップロードを比較します。直接転送を採用する場合は、短寿命・対象限定の権限と中断再開を設計します。
- **Batch計算ノードの実行制御**: Windows Batchへ確定入力を渡す方式を `ResourceFiles` 等と比較し、実計算コマンドは既存C++アプリの仕様に合わせます。exe・依存DLL・設定・image versionを固定し、WebUIから任意コマンドや任意Blob URLを受け付けないようにします。なお、共有Poolへのidentity追加やイメージ変更は事前の確認なしに前提とはしません。
- **大容量出力の永続化**: 大容量出力は現在の数KB用Task File API転送から分け、`OutputFiles` 等による永続化を検証します。転送完了・全Task成功・出力整合性を確認した後にManifestを公開します。
- **結果取得の認可と分離**: 結果取得はジョブ所有者の認可後に行います。結果ダウンロードの失敗によって計算ジョブを再起動することはありません。
- **BatchのStorage認証方式**: `ResourceFiles` / `OutputFiles` はSASまたは対応するPool identityを選択できます。WindowsノードであってもManaged Identityは利用可能であるため、既存Poolにidentityを設定できるかを確認して選択します（参考: [入力公式](https://learn.microsoft.com/en-us/azure/batch/resource-files)、[出力公式](https://learn.microsoft.com/en-us/azure/batch/batch-task-output-files)）。

### 3.4 検証runnerと証跡

- **検証スクリプトの分離**: `scripts/Invoke-PocValidation.ps1` は配置済み `deployment-result.json` とシナリオを入力とします。待機上限・poll間隔の既定は900秒・10秒です。インフラ配置と分離し、期待するexit 42と成功Manifest不在を試験成功として判定します。
- **初期シナリオと操作境界**: 単一Task、3Task集約、light/heavy/auto分類、非ゼロ終了、同入力20並列・完了後再送、異入力409、監視期限超過を対象とします。runnerはapply・ZIP配置・Pool変更・権限変更・host停止・履歴purge・自動削除を行いません。監視期限試験は専用環境指定と実設定確認を必要とします。
- **結果契約**: 選択試験すべてPassなら終了コード0、Failがあれば1、FailなしでNotRunがあれば2とします。`summary.json`と読みやすい要約を保存します。20並列はinstance・Task数だけでなくMain履歴を照合し、観測できない場合は1回起動実証済みとしません。
- **証跡の記録項目**: 試験単位で実行ID、UTC時刻、Git revision/未コミット差分の識別、package hash、秘密を除いた設定、HTTP応答、Durable状態、Batch Job/Task状態、Blob内容、関連ログを保存します。
- **秘密情報の除外と404上限**: Function key、認証ヘッダー、SAS、Durable管理URL、生の例外・HTTP本文は保存せず、証跡を許可フィールドから組み立てます。途中失敗でも結果要約を残します。状態GETの `404` は上限付き起動待ちとし、401/403は即時失敗にします。runnerの待機期限到達だけでBatch停止済みとは扱いません。
- **故障注入と実障害の区別**: 通信timeout / `429` / `503`、停止確認失敗、アップロード中断を試験専用構成で再現します。SDKの試験用transportによる故障注入と、実Azureで発生した障害は結果上で明確に区別します。
- **Entity中断試験の記録方針**: 通常のhost再起動だけでEntityのcheckpoint前後を厳密に狙い撃ちすることはできないため、制御可能なruntime試験と実Azure再起動試験を組み合わせ、実機で未観測の区間は「未検証」と記録します。なお、試験目的でEntityに外部I/Oを追加することはしません。

### 3.5 取消と清掃

- **取消（キャンセル）の競合制御**: 取消要求を永続化し、開始前・Task投入中・実行中・結果公開との競合時の挙動を定義します。Durableを先に強制終了して停止Activityを実行不能にする方式は避けます。
- **取消確定とManifest公開の順序制御**: 取消受理とBatch停止完了を区別し、再送可能にします。取消の確定受理とManifest公開は同じ制御点で順序を決め、取消を確定した後に成功公開しないようにします。完了済みジョブへの取消要求は既存の結果を破壊しません。
- **安全な清掃対象の確認**: 実行台帳の正確なJob ID・Pool・fingerprintを照合して清掃します。共有Pool/nodeやTerraform backendは変更しません。
- **履歴保持とID再利用の扱い**: 通常清掃では入力受付記録・Entity状態・Main履歴を保持します。保持期限を導入する場合は、重複受付を抑止する記録と結果データの保持方針を分けます。`requestId` の再利用は別途仕様化します。

---

## 4. 実Azureとオンプレ統合試験

| ID | 試験 | 合格条件 |
|---|---|---|
| V00 | ExpressRoute・private DNS・配置経路 | オンプレ実ホストからFunction/Blobを想定private IPで解決・到達。API・Storage各サービス・SCM配置経路と必要な認証経路を個別確認 |
| V01 | PlanOnly・配置・認証 | backend既存/未作成/参照拒否を区別。PlanOnlyにapply/ZIP配置なし。現行PoC配置後12 FunctionsとMI接続を確認。追加API実装後は期待一覧を更新 |
| V02 | 単一Task・3Task | Windowsでexit 0/result=1、3Taskでsum=3。入力・Job・Task・Manifestが一致 |
| V03 | 非ゼロ・欠損・改ざん | 明示失敗し成功Manifestなし。兄弟Taskと部分保存の状態を記録 |
| V04 | 同時受付・再送 | 同入力20並列で同instance。Mainの実行履歴が1件、Batch Taskが重複しない。異入力は409。完了後再送でも再起動しない |
| V05 | host中断復旧 | 同instanceで再開、結果整合、不要なJob/Taskなし。受付とEntity中断は停止位置の証跡と未観測区間を併記 |
| V06 | 再試行・取消token | SDKとDurable試行数を分離。Durable最大3試行、5/10秒待機。恒久エラーと呼出元cancelは再試行なし |
| V07 | 監視期限・停止失敗 | TimedOut、Job Completed確認、Manifestなし。権限不足/停止確認期限超過は未確認として記録 |
| V08 | host停止中のserver期限 | Functionを停止した状態でもBatchのMaxWallClockTimeでJob終端化。作成時刻・制約・停止結果を記録 |
| V09 | 0台autoscale Pool | 初期0台からTask先行投入で拡張・完了。既存共有Pool設定を変更しない |
| V10 | オンプレ投入・C++実行・参照 | 小さなシナリオ入力1件をBlobへ確定し、固定versionのC++アプリで実行・Blob出力。通信断/二重クリック/再読込でも同job。転送前後のhashを照合し、計算結果は業務アプリ側で定める期待値・許容誤差で判定 |
| V11 | 利用者認可 | 他利用者の開始済みjob/結果/取消を拒否。ブラウザ・ログへ管理用秘密が漏れない |
| V12 | 利用者取消・結果転送 | 開始前/投入中/実行中の取消が収束。出力転送失敗で成功公開しない。取得再試行で再計算しない |

### 試験実施上の補足

- 個人環境の公開endpoint試験は`V00`の閉域構成完成を前提にしません。`V01`の配置・認証と正常系を確認してから競合・異常系へ進み、閉域化後に同じジョブ試験を再実行します。
- 初期runnerは`V02`、`V03`の非ゼロ終了、`V04`、`V07`の正常な期限停止、および分類を対象とします。欠損・改ざん、host復旧、権限変更、通信故障、停止確認失敗、0台autoscale、実C++は後続試験です。Fake故障注入の結果は実Azure障害の実証へ繰り上げません。
- `V10`〜`V12` はオンプレミス連携契約と追加実装の後に行います。実C++バイナリを用意できるまでは固定のC++試験アプリで入出力経路を検証できますが、業務アプリの実証とは区別します。
- `V09` 用の0台autoscale Poolがない場合は、専用の試験fixtureを用意します（現Terraformは新規Poolを固定1〜3台で作成する構成のため、そのままでは0台autoscaleの試験には使用できません）。

---

## 5. 実施前に必要な情報

1. C#を仮定したオンプレミスWebバックエンドの実構成・利用者認証基盤。
2. シナリオ/出力の典型・最大サイズ、件数、保持期間、C++バイナリ/CLI/依存DLL/検証用期待値。
3. 閉域化時には既存ExpressRouteまたはVPN/VNet接続、端末・オンプレミスDNS、許可endpoint、Private Endpoint設置先、配置用実行ホスト。個人公開endpoint検証では既存ExpressRouteを必須にしません。
4. 専用PoC subscription/RG、利用可能Poolまたは新規Pool方針、image、quota、配置実行者の認証と権限。

> ※現リポジトリの `environments` ディレクトリにはサンプル設定のみが含まれています。実環境の設定とAzure CLI/認証経路を用意してからAzure試験へ進みます（計画段階ではクラウド資源の作成・停止・削除を実行しません）。

---

## 6. 完了判定と後続範囲

- **完了判定基準**: 必須試験はすべて証跡付きのPassが必要です。環境不足・停止位置未観測・試験用transportでしか確認していない項目は、実Azureでの成功としては扱いません。試験完了後、`docs/test-results.md` と実現性評価を更新し、残る制約と運用手順を引き渡します。
- **今後の拡張・別要件として扱う項目**: 分類/Job作成/結果転送まで含む厳密な全体実時間上限、Entity状態等を削除したIDの再利用、旧starterとのrolling update、大規模性能検証、MPI、Fair Share、LSF互換については別要件として扱い、まずはオンプレミス連携に必要な認証・入力・取消・出力永続化の完成を優先します。
