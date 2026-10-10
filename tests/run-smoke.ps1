<#
.SYNOPSIS
    Runs tests/detail-smoke.html in headless Edge (or Chrome) and checks the results.
    Exits non-zero if any check fails. Used by CI and handy locally.
#>
$ErrorActionPreference = 'Stop'

$browser = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Microsoft Edge or Google Chrome is required to run the smoke test.' }

$page = (Resolve-Path (Join-Path $PSScriptRoot 'detail-smoke.html')).Path
$url = 'file:///' + ($page -replace '\\', '/' -replace '#', '%23')
$profileDir = Join-Path ([IO.Path]::GetTempPath()) "readdit-smoke-$([guid]::NewGuid().ToString('N'))"

# Run the browser directly (not through PowerShell's pipeline, which turns its harmless stderr
# noise into errors) and give up after 90 seconds so a stuck page can't hang CI.
# (Plain Arguments string and Kill() so this works in Windows PowerShell 5.1 as well as pwsh.)
$start = New-Object System.Diagnostics.ProcessStartInfo $browser
$start.Arguments = (@('--headless=new', '--disable-gpu', '--no-first-run', '--disable-sync', '--allow-file-access-from-files',
                      "--user-data-dir=$profileDir", '--virtual-time-budget=5000', '--dump-dom', $url) |
                    ForEach-Object { '"' + $_ + '"' }) -join ' '
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
try {
    $process = [System.Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $null = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(90000)) {
        $process.Kill()
        Get-CimInstance Win32_Process -Filter "Name='msedge.exe' OR Name='chrome.exe'" |
            Where-Object { $_.CommandLine -like "*$profileDir*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        throw 'The smoke page did not finish within 90 seconds.'
    }
    $dom = $stdout.Result
} finally {
    Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($dom -notmatch 'data-result="([^"]*)"') { throw "The smoke page produced no result. Output:`n$dom" }
$result = [Net.WebUtility]::HtmlDecode($Matches[1]) | ConvertFrom-Json
if ($result.threw) { throw "The smoke page threw: $($result.threw)" }
$auto = if ($dom -match 'data-autoscroll="([^"]*)"') { [Net.WebUtility]::HtmlDecode($Matches[1]) | ConvertFrom-Json } else { $null }
$san = if ($dom -match 'data-sanitizer="([^"]*)"') { [Net.WebUtility]::HtmlDecode($Matches[1]) | ConvertFrom-Json } else { $null }

$checks = [ordered]@{
    'no script errors'                    = $result.errors.Count -eq 0
    'sanitizer blocks injected scripts'   = $result.pwned -eq 0
    'title rendered as text'              = $result.title -eq 'Test <b>post</b> & title'
    'all comments rendered'               = $result.comments -eq 4
    'load-more and continue-thread links' = $result.more.Count -eq 2 -and $result.more[0] -eq 'Load 4 more replies' -and $result.more[1] -like 'Continue this thread*'
    'image links become inline images'   = $result.inlineImages -eq 2   # c3's preview.redd.it image + c4's .gif
    'post vote state and score'           = $result.voteLiked -and $result.score -eq '1.2k'
    'comment sort menu'                   = $result.sortOptions -eq 6
    'clicks reach comment vote buttons'   = $result.elementUnderUpvote -eq 'BUTTON.up'
    'comment vote sent to host'           = $result.voteMessages.Count -eq 1 -and $result.voteMessages[0].id -eq 't1_c1'
    'edit only on own comments'           = $result.editButtons -eq 1
    'markdown preview'                    = $result.markdown -match '<strong>bold</strong>' -and $result.markdown -match 'md-spoiler-text' -and $result.markdown -match '<sup>'
    'editor opens and cancels'            = $result.editorOpen -and $result.editorClosed
    'new-since-last-visit badges'         = $result.newBadges -eq 3
    'Giphy GIF plays inline (Reddit mp4)' = $result.gifs.videos -contains 'https://media.giphy.com/media/abc123XYZ/giphy.mp4' -and -not $result.gifs.leftoverMarkdown
    'imgur gifv plays inline, text kept'  = $result.gifs.videos -contains 'https://i.imgur.com/xyz.mp4' -and $result.gifs.keptLinkText -contains 'funny'
    'linked .gif shows inline'            = $result.gifs.images -contains 'https://example.com/cat.gif'
    'GIF markdown in code left alone'     = $result.gifs.codeLeftAlone
    'unresolved media shows a label'      = $result.gifs.unresolvedLabel
    'GIF videos wait until on screen'     = $result.gifs.lazyVideos
    'mod buttons on post and comments'    = $result.mod.buttons -eq 5   # the post + 4 comments in a moderated sub
    'report count and reasons shown'      = $result.mod.postReportBadge -eq '2 REPORTS' -and $result.mod.reportText -match 'Spam' -and $result.mod.reportText -match 'Off topic'
    'mod menu offers the actions'         = ($result.mod.menuItems -join '|') -match 'Approve' -and ($result.mod.menuItems -join '|') -match 'Ban u/alice'
    'approve sends a mod request'         = $result.mod.sent -contains 'approve:t3_abc:csharp'
    'sanitizer: no event handlers'        = $san -and -not $san.threw -and @($san.eventHandlers).Count -eq 0
    'sanitizer: no dangerous elements'    = $san -and @($san.dangerousTags).Count -eq 0
    'sanitizer: no script/data URLs'      = $san -and @($san.scriptUrls).Count -eq 0
    'sanitizer: no inline styles'         = $san -and $san.inlineStyles -eq 0
    'sanitizer: keeps the readable text'  = $san -and $san.keptText
    'middle-click on a link still opens'  = $auto -and -not $auto.linkStarts
    'middle-click auto-scroll scrolls'    = $auto -and $auto.started -and $auto.scrolledDown
    'next click stops auto-scroll'        = $auto -and $auto.stoppedByClick
    'muted user collapsed'                = $result.mutedCollapsed -eq 1
    'highlighted user'                    = $result.highlighted -eq 1
    'find counts each match once'         = $result.findResult -eq '1 / 1'
    'Shift+N moves to the next new one'   = $result.newNavigation[0] -and $result.newNavigation[1] -and $result.newNavigation[0] -ne $result.newNavigation[1]
    'switching posts clears overlays'     = $result.overlaysClearedOnSwitch
}

$failed = 0
foreach ($check in $checks.GetEnumerator()) {
    if ($check.Value) { Write-Host "  PASS  $($check.Key)" -ForegroundColor Green }
    else { Write-Host "  FAIL  $($check.Key)" -ForegroundColor Red; $failed++ }
}
if ($failed) {
    Write-Host "`nRaw result:`n$($result | ConvertTo-Json -Depth 5)"
    throw "$failed smoke check(s) failed."
}
Write-Host "`nAll $($checks.Count) smoke checks passed." -ForegroundColor Green
