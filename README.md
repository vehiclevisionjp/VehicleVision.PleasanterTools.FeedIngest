# VehicleVision.PleasanterTools.FeedIngest

RSS / Atom のフィードを取得し、記事を Pleasanter の列へ対応づけて、
標準 Web API でレコードを作成・更新するためのツールです。

## 現在の状態

**初期化・設計段階です。フィードの取得・解析・対応づけ・レコードの作成／更新は未実装です。**
現在あるのは、テンプレート由来の最小 API クライアントと接続確認 CLI のみです。
設計メモのレビュー後に実装へ進みます。

初回実装では RSS 2.0 / Atom 1.0 を対象にします。
管理画面・スケジューラ・Docker 化は次の段階であり、現時点では提供しません。
メール受信は別ツール MailIngest の範囲です。

## 接続確認

現在の CLI は、接続先の環境変数 `PLEASANTER_BASE_URL` と `PLEASANTER_API_KEY` を読みます。
API キーは秘密管理基盤などからプロセス環境へ設定し、コマンド履歴・設定ファイルへ直書きしないでください。
現在の CLI は `_FILE` に未対応です。

```powershell
dotnet run --project src\VehicleVision.PleasanterTools.FeedIngest.Cli -- check
```

実機 Pleasanter に対する動作は未確認です。
設計と対応予定は [アーキテクチャ方針](_documents/アーキテクチャ方針.md)、
ビルドなど開発者向けの手順は [開発ガイド](_documents/開発ガイド.md) を参照してください。

## ライセンス

**AGPL-3.0-or-later 単独**で提供します（[LICENSE](LICENSE)）。
商用ライセンスとのデュアル提供は行いません。
依存のライセンスは [NOTICE](NOTICE)、貢献と参照ソースの扱いは
[ライセンス](_documents/ライセンス.md) に記載しています。
