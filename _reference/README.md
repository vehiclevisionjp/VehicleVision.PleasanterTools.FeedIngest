# _reference（参考専用・AGPL）

Pleasanter 本体（**GNU AGPL v3**）は、AI（Claude・Copilot・Codex）が
API 仕様・スキーマ・列定義・挙動を事実確認するためだけに読む参照ソース。
Git で追跡せず、必要時に上流の `main` の先頭を取得する。
CI は参照ソースを使わず、checkout の `submodules: false` も維持する。

- 上流: <https://github.com/Implem/Implem.Pleasanter>
- 既定の取得先: リポジトリ直下の `_reference/Implem.Pleasanter/`（Git 管理外）
- 共有取得先: 環境変数 `PLEASANTER_REFERENCE_DIR`（その場所を直接読む）
- この `README.md` は引き続き Git で追跡する

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。**
  本アプリは本体を改造せず、**標準 API を叩く別アプリ**として実装する
- ビルド対象・配布物・ソリューションへ含めない
- 取得した本体の作業ツリーを編集しない。本リポジトリへのコミット対象でもない
- 実機未検証の結果を動作確認済みと書かない。
  JSON・日時・タイムゾーン・未設定値・実運用の設定や権限は実機で確認する

FeedIngest 自身も AGPL で提供する選択肢があるが、本体コードを取り込んでよいという意味ではない。
本体の著作権は当社になく、取り込むと商用ライセンス側を提供できなくなる（デュアル。[LICENSING.md](../LICENSING.md)）。

## 取得と更新

事実確認前に毎回実行する。Git と bash が必要で、Windows では Git Bash を使う。

```bash
bash scripts/sync-reference.sh
```

worktree 間で共有する場合は、PowerShell からも取得先を指定できる。

```powershell
$env:PLEASANTER_REFERENCE_DIR = 'D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
```

未取得なら `main` を `git clone --depth 1` で浅く取得する。
既存なら `git fetch --depth 1 origin main` と `git checkout --detach FETCH_HEAD` で更新する。
終了時に Path・短い SHA・コミット日時・Status・完全な FullSHA・取得日時（UTC）・
csproj の Version を出す。Version が読めない場合は警告と `Version=unknown` を出し、推測しない。

変更済み・ステージ済み・未追跡ファイルがある作業ツリー、異なる origin、
別リポジトリや非空の取得先では停止する。
通信失敗は失敗コードで終了し、既存の HEAD とファイルを維持する。
新規 clone は一時取得先で成功してから配置する。並行取得はロックで止める。
異常終了で `.sync-lock` ディレクトリが残った場合は、
実行中の同期が無いことを確認してから空ディレクトリを取り除く。

`PLEASANTER_REFERENCE_DIR` 指定時は取得先を直接読み、
`_reference/Implem.Pleasanter` にジャンクションやシンボリックリンクを作らない。
相対パスはリポジトリ直下を基準にする。共有先の更新中に他の AI が読まないよう調整する。

FeedIngest は一覧ファイルなしの本体 1 件を使う。
共通スクリプトの `references.json`・`--manifest`・`--root` は複数参照用であり、本アプリには追加しない。

## 事実の記録と再現性

Template の2026-10-26の決定に従い、参照用サブモジュールと日次更新を廃止する。
AI の参照専用で CI は使わないため、ポインタ更新 PR・参照更新用 bot の迂回権限は不要。
既存の日次更新ブランチ・Draft PR・Issue はこの移行では変更しない。

再現性は gitlink ではなく、**文書に残す確認済みの完全な SHA・固定リンク・参照日**で保つ。
過去の事実は [設計の参照情報](../_documents/設計の参照情報.md) の2026-10-22の記録を維持する。
取得先が同じ SHA とは限らないので、過去の記述を最新ソースの根拠として扱わない。
再確認していない SHA・参照日を自動で最新の値に置き換えない。

`main` はリリース前の変更を含む。Version だけでリリース済みと判断せず、
必要なら上流の `Pleasanter_*` タグとの対応も確認する。
この移行では API の挙動の再調査や実機検証はしていない。

採用元（2026-10-27 参照）:

- vehiclevisionjp/VehicleVision.PleasanterTools.Template#61・
  vehiclevisionjp/VehicleVision.PleasanterTools.Template#63
- 承認済みコミット `3312f003fbe9827090e20bc643800824bdf30e22` の [配布手順][distribution]
- 同コミットの [取得スクリプト][sync-script]・[ネットワーク不要の回帰テスト][sync-test]
  （本リポジトリの `scripts/` に同一内容を配布）

[distribution]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/blob/3312f003fbe9827090e20bc643800824bdf30e22/_documents/参照ソースのオンデマンド取得への配布手順.md
[sync-script]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/blob/3312f003fbe9827090e20bc643800824bdf30e22/scripts/sync-reference.sh
[sync-test]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/blob/3312f003fbe9827090e20bc643800824bdf30e22/scripts/test-sync-reference.sh
