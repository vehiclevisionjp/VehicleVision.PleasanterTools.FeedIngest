# Claude Code 向け指示

@.github/copilot-instructions.md

現在の仕様と未決定事項は `_documents/アーキテクチャ方針.md`、運用は `_documents/開発運用.md` を読むこと。

Pleasanter 本体は Git で追跡しない参照専用ソースであり、実装へ取り込まない。
挙動を確かめる前に `bash scripts/sync-reference.sh` で `_reference/Implem.Pleasanter` を
上流の `main` の先頭へ更新する。`PLEASANTER_REFERENCE_DIR` があれば、その取得先を直接読む。
CI では取得せず、ビルド・配布物にも含めない。
文書には確認した完全なコミット SHA・ソースへの固定リンク・参照日を残す。
過去の根拠は再確認なしに更新しない。実機未検証ならその旨を残す。
正式な編集元は `.github/copilot-instructions.md`。

外部記憶は、個別のリポジトリを作らず、Internal の `VehicleVision.PleasanterTools.ClaudeMemory` に `FeedIngest/` フォルダを作って管理する。現時点ではフォルダを作っていない。
