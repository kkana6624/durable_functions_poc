# ワークフローAPIの認証境界

確認日: 2026-10-18。今回の対象は、信頼されたオンプレミスWebバックエンドを呼出元とする認証アダプターとローカル拒否試験です。Entra登録、EasyAuthのIaC、runnerのBearer対応、Azure配置・実機試験は後続です。ローカル成功をEntra運用完成とは扱いません。

Releaseの新規`WorkflowAuthorizationTests`40/40件と既存`WorkflowHttpTests`81/81件が成功しました。Fake HTTP・Mock Durable・メモリ内repositoryによる検証であり、EasyAuthのトークン検証・ヘッダー除去や実Azureの両Durableバックエンドを実証した結果ではありません。設定値を追加するだけで安全なEntra公開を完成させることはできず、後述の登録・IaC・runner・実機確認が必要です。

## 呼出元と利用者の責任

バックエンドは自身の利用者セッションで利用者・所属組織を認証・認可したうえで、API向けのapp-only access tokenと利用者コンテキストを送ります。APIはEasyAuthが検証したサービス主体を許可リスト・App Roleで認可してから、`X-Poc-Owner-Id` と `X-Poc-Org-Id` を受付・所有者照合へ渡します。この2つのヘッダーはEntraが証明したエンドユーザーclaimではなく、認可されたバックエンドによる代理主張です。バックエンドはブラウザーから渡された値をそのまま転送してはいけません。

`appid` / `azp` は呼出元アプリケーション、`oid` はapp-onlyの場合はサービス主体を表します。これらをエンドユーザーIDに置き換えません。また、トークンのテナント`tid`と業務上の所属組織は別概念です。バックエンド別の所属組織制限を追加しない契約では、許可されたバックエンドに全利用者・組織の代理主張を委ねます。複数バックエンド間で同じ利用者IDが同一人物を表すようID空間を管理する責任もバックエンド側にあります。

既存PoCのFunction keyと所有者なし受付を維持する経路と、Entra必須経路を明示的に選択します。Entra必須経路で認証に失敗した要求を既存経路へフォールバックさせません。既存経路の所有者ヘッダーは本人性を証明しません。所有者なしの過去受付を任意利用者へ付け替えることもありません。

## EasyAuthとアプリケーションの検証

`WORKFLOW_AUTH_MODE`は既定`PocFunctionKey`、厳格な経路は`EntraEasyAuth`です。後者は以下の設定を必須とし、欠落・不正なモード/GUIDやAzure環境ガード不成立では起動を失敗させます。

| 設定 | 契約 |
|---|---|
| `WORKFLOW_AUTH_TENANT_ID` | 許可tenantの空でないGUID |
| `WORKFLOW_AUTH_AUDIENCE` | 許可する単一のaudience、完全一致 |
| `WORKFLOW_AUTH_ALLOWED_BACKEND_APP_IDS` | 許可する空でないclient ID GUIDのカンマ区切り。重複設定は集合へ正規化 |
| `WORKFLOW_AUTH_REQUIRED_APP_ROLE` | 必須App Role値、完全一致 |

issuerは設定tenantから導出した`https://sts.windows.net/<tenant-id>/`または`https://login.microsoftonline.com/<tenant-id>/v2.0`の完全一致です。現在の対象はAzure public cloudの単一tenantであり、別cloud/独自issuerは後続の明示設計が必要です。設定値・claim・ロールの任意の部分一致は行いません。

principalヘッダーは単値・最大32768文字、claimは最大256個、claim typeは最大512文字・valueは最大4096文字です。JSONの同名property、単値claimの重複、同じ概念のraw名/URI名の併存は、同値でも拒否します。rolesは異なる複数値を許可しますが重複値・raw/URI併存は拒否します。必須owner/orgは各128文字までとし、既存の利用者ID形式検証も適用します。principal欠落・不正形式は401、認可claim・owner/orgの不成立は403で、固定メッセージを返します。認証処理は開始bodyの読取り・受付Blobへのアクセス・Durable照会の前に行います。状態・結果APIではrequestIdの形式検査が先にあるため、不正なrequestIdは認証処理前に400になります。

EasyAuthはトークンの署名・期限・issuer・audienceを検証する前段です。APIはその認証結果に対して、provider、許可tenant/issuer/audience、許可する呼出元client ID、必要なApp Roleを照合します。app-onlyの判定は`idtyp=app`を必須とし、delegated tokenの`scp`が存在する要求を拒否します。`roles`と`appid` / `azp`があるだけではapp-onlyを証明できません。`idtyp`はAPI側app registrationのaccess token optional claimに設定する必要があります。[App Role検証](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-protected-web-api-verification-scope-app-roles)、[optional claims](https://learn.microsoft.com/en-us/entra/identity-platform/optional-claims-reference)。

`X-MS-CLIENT-PRINCIPAL`はBase64で表されたJSONです。デコードはJWT署名検証の代わりにはなりません。公式資料が示すヘッダーの信頼性は、App Service認証モジュールが有効で、外部要求の同名ヘッダーを排除し、プラットフォームが注入する経路に依存します。providerを示す`X-MS-CLIENT-PRINCIPAL-IDP`単独でも認証証明にはなりません。[認証済みIDヘッダー](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-user-identities)。

claimには既定の名前変換があるため、既知のraw名と既知のURI名を扱います。異なる値が同じ概念として現れる場合や不正・多重ヘッダーは拒否します。任意の`role_typ`を新しい権限claimとして信頼しません。公式資料は`tid`からtenantid URIへの変換を例示しますが、利用環境のprincipalにissuer/audienceを含む必要claimが必ず揃うことは本作業では実機確認していません。必要claim欠落時は拒否し、互換性のため検査を省略しません。[EasyAuthのアプリ内認可](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-provider-aad#perform-validations-from-application-code)。

Azureの環境変数によるガードは、通常のローカル起動や認証無効構成で偽造principalを受理しないための条件です。`WEBSITE_AUTH_ENABLED`とsite/instance情報の存在は、EasyAuthが正しく構成されたことの暗号学的証明にはなりません。運用者が認証構成を維持し、認証前段を通らない到達経路を作らないことが前提です。ローカル試験での認証済みcaller注入をHTTPから選べる機能は提供しません。

3つの独自APIは引き続き`AuthorizationLevel.Function`です。Azure上ではEntra認証とは別にFunction keyが必要です。Bearerのみへの移行時には`Anonymous`への変更とEasyAuthによる保護を合わせて検証します。通常のCore Toolsローカル実行はFunction key検証を行わないため、ローカルHTTP成功はkey保護の実証にもなりません。[HTTP triggerの認可レベル](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-http-webhook-trigger#authorization-level)。

## 後続の構成と実証

tenant ID、API app/client ID、許可するバックエンドclient ID、audience、issuer、App Role、利用者ID形式は環境ごとに確定させます。未確定値を既存Azureリソースやサンプル値から推定しません。今回のIaC既定値やrunnerの通信方式は変更しません。

ロック済みAzureRM 5.10.0のローカルprovider schemaで、`azurerm_linux_function_app.auth_settings_v2`の`auth_enabled`、`require_authentication`、`require_https`、`unauthenticated_action="Return401"`、`active_directory_v2`の`client_id`、`tenant_auth_endpoint`、`allowed_applications`、`allowed_audiences`を確認しました。これは実装準備の情報であり、今回のIaC実装・Azure反映ではありません。tenant endpointはAzure公式資料に従い`https://login.microsoftonline.com/<tenant-id>/v2.0`とします。provider schema説明の例にはtenant/v2.0の順序が異なるものがあるため、その例を採用しません。

EasyAuthの`allowedApplications`はclient IDを`appid` / `azp`で検査します。必要に応じて`allowedPrincipals.identities`でサービス主体の`oid`も制限できます。両方設定した条件はANDです。API向けの未認証応答はredirectではなく401とし、許可tenantも限定します。API内App Role検証と所有者認可は引き続き必要です。[EasyAuth構成と組込み認可](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-provider-aad)。

後続の実証では、両Durableバックエンドに対し、正しいapp-only tokenとkey、認証なし、keyのみ、偽造principal、別tenant/issuer/audience/client、権限不足、delegated token、owner/org欠落・別所有者を検証します。Entra登録のApplication限定App Role・管理者同意・`idtyp`設定、実principalのclaim名と必要claimの有無、認証設定を無効にしたときの拒否、runnerによる独自APIの照会・結果取得を含めます。Bearer token、Function key、principal全文、Durable管理URLをログ・証跡・リポジトリへ保存しません。
