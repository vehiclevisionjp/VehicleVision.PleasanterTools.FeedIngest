# VehicleVision.PleasanterTools.FeedIngest

RSS / Atom のフィードを取得し、記事を Pleasanter の列へ対応づけて、
標準 Web API でレコードを作成・更新するためのツールです。

## 現在の状態

**取り込みの流れ（取得 → 解析 → 対応づけ → 外部キー照合 → 作成・更新）と、状態 DB による再実行・再開を実装した段階です。
フェイク通信の単体試験と一時 SQLite の試験だけで確認しており、実機 Pleasanter・実際のフィードでは未確認です。**
先に `migrate` で状態 DB を作り、`ingest --config <設定ファイル>` で 1 回取り込んで終了します（[状態 DB](#状態-db)・[取り込み](#取り込み) を参照）。

状態 DB（既定は SQLite）には、取得の検証子（ETag・Last-Modified）、記事のスナップショット、送信した値のハッシュを保存します。
そのため、フィードが変わっていなければ 304 で済み、送る値が変わっていなければ Pleasanter の API を呼びません
（利用者が所有列を編集していても、送る値が変わらない限り戻しません）。前回の実行が途中で止まっても、保存済みの記事から再開します。
同じ状態 DB を使う実行は 1 つだけです（2 本目は何もせず終了コード 1。異常終了で排他が残った場合は、他の実行が動いていないことを確認してから `unlock` で解除します）。
`--dry-run` は状態 DB も Pleasanter も使いません。

初回実装では RSS 2.0 / Atom 1.0 を対象にします。
管理画面・スケジューラ・Docker 化は次の段階であり、現時点では提供しません。
メール受信は別ツール MailIngest の範囲です。

## 取り込み

```powershell
dotnet run --project src\VehicleVision.PleasanterTools.FeedIngest.Cli -- ingest --config feedingest.json
dotnet run --project src\VehicleVision.PleasanterTools.FeedIngest.Cli -- ingest --config feedingest.json --dry-run
```

設定ファイルの形は [対応づけ設定](_documents/対応づけ設定.md) を参照してください（秘密は環境変数名で参照します）。
`--dry-run` は Pleasanter へ接続せず、取得・解析・対応づけまでを確認します。
終了コードは 0＝全対象が成功または変更なし、1＝一部でも保留・失敗・未処理（排他を取れなかった場合を含む）、2＝使い方・設定の誤り、130＝中断です
（想定外の例外は `[ERROR]` を出して 1 です）。
出力は取り込み元ごとの件数だけで、URL・記事の内容・API キーは出しません。結果の行は `[INFO] source=<ID> created=… updated=… unchanged=… held=… failed=… pending=… mapped=…`
（実行を止めたときは `[INFO] stopped reason=<分類>`）の形で標準出力へ出します。`key=value` の部分は従来のままで、行頭に `[INFO]` の接頭辞が付きます。
診断（`[WARN]`・`[ERROR]`）は標準エラーへ出します。メッセージは英語・ASCII です。
`--json` を付けると、結果を 1 行の JSON（`command`・`ok`・`exitCode`・`errors`・`warnings` と、`dryRun`・`stoppedReason`・`sources`）で標準出力へ出します。
取り込み先には、外部キー用に未使用の Class 列（選択肢なし）を 1 本確保してください。

## コマンドラインの書式

引数の解析・ヘルプ・`--version`・シェル補完は、共通部品 Core.Cli が行います。
オプションは `--名前`・`-名前`・`/名前` のどれでも書け、値は `--config 値`・`--config=値`・`--config:値` のどれでも渡せます（名前の大文字小文字は区別しません）。
`--help`（`-h`・`-?`・`/?`）で使い方を、`--version` で版を表示します。
`completion <powershell|bash|zsh>` は、そのシェル用の補完スクリプトを標準出力へ出します。
ツール名は `feedingest` です。

| コマンド | 意味 | `--json` |
| --- | --- | --- |
| `check` | Pleasanter への接続確認 | 対応 |
| `ingest --config <ファイル> [--dry-run]` | 設定ファイルに従って取り込む | 対応 |
| `migrate` | 状態 DB のマイグレーションを適用する | — |
| `migrate-status` | 未適用のマイグレーションを表示する（未適用があれば終了コード 1） | 対応 |
| `unlock` | 異常終了で残った実行の排他を解除する | — |
| `completion <shell>` | 補完スクリプトを出す | — |

## 状態 DB

```powershell
dotnet run --project src\VehicleVision.PleasanterTools.FeedIngest.Cli -- migrate-status   # 未適用があれば終了コード 1
dotnet run --project src\VehicleVision.PleasanterTools.FeedIngest.Cli -- migrate
dotnet run --project src\VehicleVision.PleasanterTools.FeedIngest.Cli -- unlock           # 異常終了で残った実行の排他を解除（他の実行が無いことを確認してから）
```

状態 DB は既定で SQLite（`App_Data/feedingest.db`）です。接続先は環境変数 `FEEDINGEST_DB_PROVIDER`・
`FEEDINGEST_DB_CONNECTIONSTRING`・`FEEDINGEST_DB_ALLOW_INSECURE` で指定します（接続文字列は設定ファイルへ書きません）。
記事のスナップショット（本文・enclosure の URL）を保存するので、DB のアクセスを制限してください。
`ingest`（`--dry-run` 以外）は、マイグレーション済みの状態 DB が必要です（未適用なら終了コード 2）。
`migrate`・`migrate-status` の出力は共通部品 Core.Data のもので、適用済み・適用完了の行は `[OK]` で始まり、DB の待機などの進行表示は標準エラーへ出ます。
設定が不正なときなど、Core.Data 由来の例外メッセージも英語・ASCII です。

## 接続確認

`check` は、接続先の環境変数 `VVPT_PLEASANTER_BASE_URL` と `VVPT_PLEASANTER_API_KEY` を読みます
（`VVPT_` は VehicleVision PleasanterTools の共通の接頭辞で、製品群の各ツールが同じ名前を使います）。
API キーは秘密管理基盤などからプロセス環境へ設定し、コマンド履歴・設定ファイルへ直書きしないでください。
`VVPT_PLEASANTER_API_KEY_FILE` で秘密ファイルのパスも指定できます（前後の空白・改行を除いて使います）。
`VVPT_PLEASANTER_API_KEY` と `VVPT_PLEASANTER_API_KEY_FILE` の併用、空のファイルはエラーです。
接続先はHTTPSのみで、リダイレクトには追従しません。

**旧名は受け付けません。** `PLEASANTER_BASE_URL`・`PLEASANTER_API_KEY`・`PLEASANTER_API_KEY_FILE`、
`FEEDINGEST_PLEASANTER_BASE_URL`・`FEEDINGEST_PLEASANTER_API_KEY`・`FEEDINGEST_PLEASANTER_API_KEY_FILE` が
設定されていると（新名との併用を含む）、`check` と `ingest` は
`[ERROR] Environment variable X is no longer supported. Use Y.` を出して終了コード 2 で止まります。
設定ファイルの `apiKeyEnv` などが指す環境変数の名前は、利用者が決めるものですが、**旧名と同じ名前（例: `FEEDINGEST_PLEASANTER_API_KEY`）は使えません**
（旧名として検出され、止まります）。旧名と別の名前（例: `FEEDINGEST_OFFICE_API_KEY`）にしてください。
`FEEDINGEST_DB_PROVIDER`・`FEEDINGEST_DB_CONNECTIONSTRING`・`FEEDINGEST_DB_ALLOW_INSECURE` は変わりません。

### 旧版からの移行

1. `PLEASANTER_BASE_URL`・`PLEASANTER_API_KEY`（`_FILE`）、または `FEEDINGEST_PLEASANTER_*` を設定している場所
   （App Service の構成・タスクスケジューラ・systemd・コンテナ定義・CI の秘密など）を、`VVPT_PLEASANTER_BASE_URL`・`VVPT_PLEASANTER_API_KEY`（`_FILE`）へ書き換える
2. 旧名を消す（残っていると、新名との併用でも動かない）
3. 設定ファイルの `apiKeyEnv`・`usernameEnv` などが旧名と同じ名前なら、別の名前へ変える（設定ファイルと環境変数の両方）
4. 結果の行を解析しているスクリプトは、行頭の `[INFO]` の接頭辞と、診断が標準エラーへ移ったことに合わせる。`migrate` の待機期限切れなどを終了コード 2 と読んでいた場合は 1 に直す
5. 新しい版へ更新する（1〜3 と同時に）

| 旧名 | 新名 |
| --- | --- |
| `PLEASANTER_BASE_URL`・`FEEDINGEST_PLEASANTER_BASE_URL` | `VVPT_PLEASANTER_BASE_URL` |
| `PLEASANTER_API_KEY`・`FEEDINGEST_PLEASANTER_API_KEY` | `VVPT_PLEASANTER_API_KEY` |
| `PLEASANTER_API_KEY_FILE`・`FEEDINGEST_PLEASANTER_API_KEY_FILE` | `VVPT_PLEASANTER_API_KEY_FILE` |

```powershell
dotnet run --project src\VehicleVision.PleasanterTools.FeedIngest.Cli -- check
```

実機 Pleasanter に対する動作は未確認です。
設計と対応予定は [アーキテクチャ方針](_documents/アーキテクチャ方針.md)、
ビルドなど開発者向けの手順は [開発ガイド](_documents/開発ガイド.md) を参照してください。

## ライセンス

**デュアルライセンス**で提供します。詳細は [ライセンスについて](LICENSING.md) を参照してください。

| | ライセンス | 想定する利用 |
| --- | --- | --- |
| A | [GNU AGPL v3 以降](LICENSE) | オープンソースとして利用・改変・再配布する場合 |
| B | [株式会社ピー・エム・シー ITソリューション事業部の商用ライセンス](LICENSE-COMMERCIAL.md) | AGPL の義務を負わずに利用したい場合（個別契約） |

依存のライセンスは [NOTICE](NOTICE)、貢献と参照ソースの扱いは
[ライセンス](_documents/ライセンス.md) に記載しています。
貢献を取り込む前に CLA への署名が必要です。
手続きは [貢献のしかた](CONTRIBUTING.md) と [CLA](CLA.md) を参照してください。
