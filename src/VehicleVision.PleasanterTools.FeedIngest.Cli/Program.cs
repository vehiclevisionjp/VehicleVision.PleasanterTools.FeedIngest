using VehicleVision.PleasanterTools.FeedIngest.Cli;

// check: 接続確認（VVPT_PLEASANTER_* の環境変数）
// ingest: 設定ファイルに従ってフィードを取り込む。Ctrl+C で安全に止め（1 回目）、2 回目ですぐ終了（どちらも終了コード 130）
return await CliApp.RunWithConsoleAsync(args);
