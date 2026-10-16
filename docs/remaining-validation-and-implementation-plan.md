# 残りの検証・実装計画

作成日: 2026-10-17。現行の54単体テスト、Release publish、PowerShellオフライン3組、Terraform Mock8件は成功済み。実Azure試験は未実施。本書は計画であり、記載した追加機能を実装済みとは扱わない。

## 運用前提と目標

利用者はオンプレミスのWebUIからデータ投入・ジョブ開始を行う。想定経路は `ブラウザ → オンプレWebバックエンド → AzureジョブAPI → Durable / Batch / Blob`。オンプレ側WebUIを新規に作り直す計画ではなく、既存バックエンドとの接続口を整える。

確定した前提は、Windows計算ノード上のC++アプリ、Blob経由の入出力、金融オプションシミュレーション用のシナリオ入力、オンプレからAzureへのExpressRoute接続。WebバックエンドはC#を仮定するが未確認。業務詳細は一般的なシナリオ実行として設計し、価格評価モデルやC++計算本体を再実装しない。

既存ExpressRouteを再利用する前提で、private peeringからFunction・StorageのPrivate Endpointへの到達性、オンプレDNS転送、FunctionsのVNet統合とBatch/Storageへの送信経路を先に確定する。ExpressRouteがあるだけで各PaaSのPrivate Endpoint・DNS・権限が設定済みとは扱わない。

シナリオJSON等の小さなファイルをオンプレバックエンドからBlobへ保存する方式を最初の実装とする。入力/出力の最大サイズ、認証基盤、既存C++アプリのCLIと依存ライブラリは未確定。巨大ファイルや中断再開が必要と分かった場合に、分割転送・永続upload sessionを追加する。

現在のPoCはJSONから固定PowerShell処理を起動して整数1を取得するもので、実ファイルの投入・計算・回収をまだ検証していない。ジョブ制御の実証と、業務データ処理の実証を別の合格条件にする。

## 実施順と成果物

| 段階 | 実施内容 | 完了条件 |
|---|---|---|
| 1 | ExpressRoute接続・オンプレ連携契約と試験環境を確定 | private接続/DNS/配置経路、シナリオ契約、C++起動契約、認証・状態・取消・結果取得、専用Azure設定が決まる |
| 2A | 実Azure検証runner・証跡・試験用障害制御 | シナリオ単位で再実行でき、成功・失敗・未実施の要約と秘密を除いた証跡が残る |
| 2B | データ投入・API境界を実装 | 入力確定後だけ開始可能。同じ入力の再送は同じジョブに対応。WebUIへ管理URLやFunction keyを返さない |
| 3 | 現行PoCをAzureで実証 | 配置、MI認証、単一Task、3Task、競合、復旧、期限停止の各試験が証跡付きで合格 |
| 4 | オンプレから実ファイルを用いて通し試験 | 投入→開始→状態照会→結果取得が通り、通信断・再送・WebUI再読込でも整合する |
| 5 | 取消・出力永続化・運用保守を仕上げ | 開始前/実行中の取消、停止確認失敗、出力公開条件、清掃・保持・障害対応の手順が検証される |

段階2Aと2Bは並列化する。段階3の正常系が通った後、競合・分類・データ整合性試験は別requestIdで並列実行できる。host停止・権限変更・障害注入は同じ環境内で競合するため直列化し、並列化するならFunction・Storage・Task Hubを分離する。

## 追加実装の範囲

### ExpressRouteと閉域構成

- 既存回線・private peering・接続先VNetを確認し、Function受信Private Endpointと送信用VNet Integrationを分けてIaCへ追加する。現在のB1は両機能の対応tierなので、閉域化だけを理由にplan変更しない。[Private Endpoint](https://learn.microsoft.com/en-us/azure/app-service/overview-private-endpoint)、[VNet Integration](https://learn.microsoft.com/en-us/azure/app-service/overview-vnet-integration)
- 入出力Blobに加え、Durable/Entityのhost Storage用Blob・Queue・TableのPrivate EndpointとDNSを設定する。オンプレDNSからPrivate Resolver等への転送を整える。[オンプレ接続構成](https://learn.microsoft.com/en-us/azure/architecture/web-apps/guides/networking/access-multitenant-web-app-from-on-premises)、[Functions Storage](https://learn.microsoft.com/en-us/azure/azure-functions/storage-considerations)
- private SCMのDNSと配置agentの到達性を用意してからpublic accessを制限する。Entra、ARM、監視、package取得の経路はExpressRoute private peeringとは別に確認する。[SCM要件](https://learn.microsoft.com/en-us/azure/app-service/overview-private-endpoint#kuduscm-endpoint)、[ExpressRoute routing](https://learn.microsoft.com/en-us/azure/expressroute/expressroute-routing)
- Batchを閉域化する場合、Job/Task操作用batchAccountと計算ノード管理用nodeManagementを別々に設計する。既存PoolのVNetと通信方式を確認し、専用試験環境でノード管理・Blob入出力を実証後に適用する。現IaCは公開endpoint構成で、この追加が必要。[Batch Private Endpoint](https://learn.microsoft.com/en-us/azure/batch/private-connectivity)、[Simplified通信](https://learn.microsoft.com/en-us/azure/batch/simplified-compute-node-communication)

### オンプレ向けAPIと認証

- ブラウザはオンプレWebバックエンドへ接続し、WebバックエンドがAzure APIを呼ぶ。サービス間認証と利用者のジョブ所有者認可を別々に定義する。
- Azure側はEntraベースのサービス間認証を基本候補にする。オンプレでAzure Managed Identityを自動利用できるとは想定せず、既存認証基盤に応じて証明書または対応する資格情報連携を選ぶ。
- 開始・状態照会・結果参照・取消を公開APIで提供する。Durable管理URL・system keyはサーバー内部だけで使用する。
- 状態はAccepted / Preparing / Running / Publishing / Succeeded / Failed / TimedOut / Cancelling / Cancelled等へ正規化する。受付記録があってMain登録前の状態をAcceptedとして返し、存在しないjobと混同しない。停止未確認は成功や取消完了にせず、別情報として返す。DurableのruntimeStatusと業務状態を混同しない。
- 利用者・所属・job IDの関連を保存し、他利用者の照会・取消・結果取得を拒否する。サービス認証だけを利用者認可の代わりにしない。

Entra受信認証を導入する際は既存Function key方式との関係も変更する。EasyAuthのtoken認証に加えてAPIでapp roleを確認し、信頼されたオンプレバックエンドからの利用者情報の受け渡しを定義する。[workload認証](https://learn.microsoft.com/en-us/entra/architecture/authorize-applications-resources-workloads)、[App Service認証・認可](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-provider-aad)

### 入力データと計算・出力

- アップロード開始、アップロード中、入力確定を区別する。中断・未完了データからジョブを開始しない。
- シナリオschema version、scenario ID、計算条件、必要な参照データのversion、C++アプリのbuild/versionを契約化する。乱数を使う場合はseed等の再現条件も記録する。具体的な金融モデルは既存C++アプリの責務とする。
- 入力manifestへ許可されたBlob参照、versionまたはETag、サイズ、SHA-256を記録し、確定後の差し替えを拒否する。ETagを内容ハッシュとして扱わない。入力参照と計算アプリversionをrequest fingerprintにも含める。
- データ量に応じてバックエンド中継と限定したBlob直接アップロードを比較する。直接転送を採用する場合は短寿命・対象限定の権限と中断再開を設計する。
- Windows Batchへ確定入力を渡す方式をResourceFiles等と比較し、実計算コマンドは既存C++アプリの仕様に合わせる。exe・依存DLL・設定・image versionを固定し、WebUIから任意コマンドや任意Blob URLを受け付けない。共有Poolへのidentity追加やイメージ変更を暗黙の前提にしない。
- 大容量出力は現在の数KB用Task File API転送から分け、OutputFiles等による永続化を検証する。転送完了・全Task成功・出力整合性を確認後にManifestを公開する。
- 結果取得はジョブ所有者の認可後に行う。結果ダウンロードの失敗で計算ジョブを再起動しない。

ResourceFiles/OutputFilesはSASまたは対応するPool identityを選択できる。WindowsだからManaged Identityが利用不可とは扱わず、既存Poolにidentityを設定できるかを確認して選ぶ。[入力公式](https://learn.microsoft.com/en-us/azure/batch/resource-files)、[出力公式](https://learn.microsoft.com/en-us/azure/batch/batch-task-output-files)

### 検証runnerと証跡

- `scripts/Invoke-PocValidation.ps1`等を新設し、配置済みoutputsとシナリオを入力にする。配置と試験を分離し、期待したTask失敗は試験成功として判定できるようにする。
- 試験単位で実行ID、UTC時刻、Git revision/未コミット差分の識別、package hash、秘密を除いた設定、HTTP応答、Durable状態、Batch Job/Task状態、Blob内容、関連ログを保存する。
- Function key、認証ヘッダー、SAS、Durable管理URLのqueryは保存しない。途中失敗でも結果要約を必ず残す。状態GET404には上限を設けて起動待ちと最終的な未起動を区別する。
- 通信timeout/429/503、停止確認失敗、アップロード中断を試験専用構成で再現する。SDK試験用transportによる故障注入と実Azureで発生した障害は結果で区別する。
- Entityのcheckpoint前後を通常のhost再起動だけで狙い撃ちしたとは主張しない。制御可能なruntime試験と実Azure再起動試験を組み合わせ、未観測の区間は未検証と記録する。Entityに試験のための外部I/Oを追加しない。

### 取消と清掃

- 取消要求を永続化し、開始前・Task投入中・実行中・結果公開との競合を定義する。Durableを先に強制終了して停止Activityを実行不能にする方式は避ける。
- 取消受理とBatch停止完了を区別し、再送可能にする。取消の確定受理とManifest公開は同じ制御点で順序を決め、取消を確定した後に成功公開しない。完了済みジョブへの取消は結果を破壊しない。
- 実行台帳の正確なJob ID・Pool・fingerprintを照合して清掃する。共有Pool/nodeやbackendを変更しない。
- 通常清掃は入力受付記録・Entity状態・Main履歴を保持する。保持期限を導入する場合は、重複受付を抑止する記録と結果データの保持方針を分ける。requestIdの再利用は別途仕様化する。

## 実Azureとオンプレ統合試験

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

V00〜V03が残りのクラウド試験の前提。V10〜V12はオンプレ連携契約と追加実装後に行う。実C++バイナリを用意できるまでは固定のC++試験アプリで入出力経路を検証できるが、業務アプリ実証とは区別する。V09用の0台autoscale Poolがなければ専用試験fixtureを用意する。現Terraformは新規固定1〜3台なので、そのまま0台autoscaleを作れるとは想定しない。

## 実施前に必要な情報

1. C#を仮定したオンプレWebバックエンドの実構成・利用者認証基盤。
2. シナリオ/出力の典型・最大サイズ、件数、保持期間、C++バイナリ/CLI/依存DLL/検証用期待値。
3. 既存ExpressRoute private peering/VNet接続、オンプレDNS、許可endpoint、Private Endpoint設置先、配置用実行ホスト。
4. 専用PoC subscription/RG、利用可能Poolまたは新規Pool方針、image、quota、配置実行者の認証と権限。

現リポジトリのenvironmentsにはサンプルのみ。実設定とAzure CLI/認証経路を用意してからAzure試験へ進む。計画段階ではクラウド資源作成・停止・削除を実行しない。

## 完了判定と後続範囲

必須試験は証跡付きPassが必要。環境不足・停止位置未観測・試験用transportでしか確認していない項目を実Azure成功へ繰り上げない。`docs/test-results.md`と実現性評価を更新し、残る制約と運用手順を引き渡す。

分類/Job作成/結果転送まで含む厳密な全体実時間上限、Entity状態等を削除したIDの再利用、旧starterとのrolling update、大規模性能、MPI、Fair Share、LSF互換は別の要件として扱う。オンプレ連携に必要な認証・入力・取消・出力永続化を先に完成させる。
