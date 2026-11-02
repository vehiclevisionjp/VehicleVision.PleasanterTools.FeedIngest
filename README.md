# VehicleVision.PleasanterTools.FeedIngest

RSS / Atom のフィードを取得し、記事を Pleasanter の列へ対応づけて、
標準 Web API でレコードを作成・更新するためのツールです。

## 現在の状態

**取り込みの最小の流れ（取得 → 解析 → 対応づけ → 外部キー照合 → 作成・更新）を実装した段階です。
フェイク通信の単体試験だけで確認しており、実機 Pleasanter・実際のフィードでは未確認です。**
`ingest --config <設定ファイル>` で 1 回取り込んで終了します（[取り込み](#取り込み) を参照）。

**永続状態（取得状態・送信状態・実行排他の DB）は未実装です。** そのため、条件付き取得（ETag）は使わず毎回取得し、
前回送った値を覚えていません（既存レコードの所有列が取り込み値と違えば毎回上書きします）。
同じ送り先へ同時に 2 つ実行しないでください。

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
