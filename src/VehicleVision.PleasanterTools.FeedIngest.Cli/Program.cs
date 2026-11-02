using VehicleVision.PleasanterTools.FeedIngest.Cli;

// check: 接続確認（PLEASANTER_* / FEEDINGEST_PLEASANTER_* の環境変数）
// ingest: 設定ファイルに従ってフィードを取り込む。Ctrl+C で中断（終了コード 130）
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await CliApp.RunAsync(args, Console.Out, Console.Error, null, cancellation.Token);
