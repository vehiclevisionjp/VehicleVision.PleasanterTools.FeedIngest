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
終了コードは 0＝全対象が成功または変更なし、1＝一部でも保留・失敗・未処理、2＝使い方・設定の誤り、130＝中断です。
出力は取り込み元ごとの件数だけで、URL・記事の内容・API キーは出しません。
取り込み先には、外部キー用に未使用の Class 列（選択肢なし）を 1 本確保してください。

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

## 接続確認

現在の CLI は、接続先の環境変数 `PLEASANTER_BASE_URL` と `PLEASANTER_API_KEY` を読みます。
API キーは秘密管理基盤などからプロセス環境へ設定し、コマンド履歴・設定ファイルへ直書きしないでください。
`PLEASANTER_API_KEY_FILE` で秘密ファイルの絶対パスも指定できます。
ファイルはUTF-8、最大64 KiBで、末尾の改行1組だけを除去します。
環境変数の値と `_FILE` の併用、空値、改行を含む値はエラーです。
接続先はHTTPSのみで、リダイレクトには追従しません。
`FEEDINGEST_PLEASANTER_BASE_URL`／`FEEDINGEST_PLEASANTER_API_KEY`（または `_FILE`）も使えますが、
旧 `PLEASANTER_*` と混在すると起動エラーになります。

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
