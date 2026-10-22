param([switch]$NoticeOnly)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$notice = Get-Content -LiteralPath (Join-Path $repoRoot 'NOTICE') -Raw
$cacheOutput = dotnet nuget locals global-packages --list
if ($LASTEXITCODE -ne 0) { throw 'NuGet キャッシュの取得に失敗しました。' }
$cache = ($cacheOutput -replace '^global-packages:\s*', '').Trim()
$packages = @{}
foreach ($directory in @('src', 'tests')) {
    foreach ($file in Get-ChildItem (Join-Path $repoRoot $directory) -Filter packages.lock.json -Recurse) {
        $lock = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        foreach ($framework in $lock.dependencies.Values) {
            foreach ($entry in $framework.GetEnumerator()) {
                if ($entry.Value.type -ne 'Project') {
                    $packages["$($entry.Key) $($entry.Value.resolved)"] = $entry.Key, $entry.Value.resolved
                }
            }
        }
    }
}
if ($packages.Count -eq 0) { throw '依存ロックが見つかりません。' }
$failed = $false
foreach ($entry in $packages.GetEnumerator() | Sort-Object Key) {
    $id, $version = $entry.Value
    $path = Join-Path $cache "$($id.ToLowerInvariant())\$($version.ToLowerInvariant())\$($id.ToLowerInvariant()).nuspec"
    [xml]$nuspec = Get-Content -LiteralPath $path -Raw
    $metadata = $nuspec.package.metadata
    $license = [string]$metadata.license.'#text'
    if ($id -eq 'xunit.abstractions' -and $version -eq '2.0.3' -and
        $metadata.licenseUrl -eq 'https://raw.githubusercontent.com/xunit/xunit/master/license.txt') {
        # SPDX欄のない旧パッケージ。一次情報の確認記録は設計の参照情報.mdに固定する。
        $license = 'Apache-2.0'
    } elseif ($metadata.license.type -ne 'expression') {
        throw "ライセンスの個別確認が必要です: $id $version"
    }
    if (!$NoticeOnly -and $license -notin @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', '0BSD', 'ISC')) {
        throw "許可していないライセンスです: $id $version / $license"
    }
    $expected = "$id $version / $license"
    if ($notice -notmatch "(?m)^$([regex]::Escape($expected))\r?$") {
        Write-Output "NG $id $version が NOTICE に見当たらない / $license"
        $failed = $true
    }
}
$npm = Get-Content -LiteralPath (Join-Path $repoRoot 'package-lock.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($entry in $npm.packages.GetEnumerator()) {
    if ($entry.Key -eq '') { continue }
    if (!$NoticeOnly -and $entry.Value.license -notin @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', '0BSD', 'ISC')) {
        throw "許可していない npm ライセンスです: $($entry.Key)"
    }
}
foreach ($id in $npm.packages[''].devDependencies.Keys) {
    $package = $npm.packages["node_modules/$id"]
    $expected = "$id $($package.version) / $($package.license)"
    if ($notice -notmatch "(?m)^$([regex]::Escape($expected))\r?$") {
        Write-Output "NG $id $($package.version) が NOTICE に見当たらない"
        $failed = $true
    }
}
if ($failed) { throw 'NOTICE と依存ロックの整合検査に失敗しました。' }
Write-Output "依存 $($packages.Count) パッケージと npm 全依存のライセンス・NOTICE 検査に成功しました。"
