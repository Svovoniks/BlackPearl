#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ship.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('blackpearl-ship-checks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'src') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $testRoot 'src/app.cs'), 'fixture')
$checks = 0
function Expect-Rejected {
    param([scriptblock]$Action, [string]$Name)
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw "FAIL: $Name" }
    $script:checks++; Write-Host "PASS: $Name"
}
function Check {
    param([bool]$Value, [string]$Name)
    if (!$Value) { throw "FAIL: $Name" }
    $script:checks++; Write-Host "PASS: $Name"
}
try {
    Check ((Get-ShipVersion '' '0.3.17' '0.3.17') -eq '0.3.18') 'next patch version is chosen automatically'
    Check ((Get-ShipVersion '' '0.3.18' '0.3.17') -eq '0.3.19') 'public version takes precedence over older source'
    foreach ($version in @('0.3.17','0.3.16','0.3.18-beta','0.3')) {
        Expect-Rejected { Get-ShipVersion $version '0.3.17' '0.3.17' } "unsafe version $version is rejected"
    }
    Check ((Get-PullRequestVersion '' '0.3.17' '0.3.17' '0.3.17') -eq '0.3.18') 'a new pull request gets the next patch version'
    Check ((Get-PullRequestVersion '' '0.3.17' '0.3.17' '0.3.18') -eq '0.3.18') 'a bumped branch keeps its version across review rounds'
    Check ((Get-PullRequestVersion '' '0.3.18' '0.3.18' '0.3.18') -eq '0.3.19') 'a branch overtaken by a release is bumped again'
    Check ((Get-PullRequestVersion '' '0.3.17' '0.3.18' '0.3.18') -eq '0.3.19') 'a branch matching a newer master version is bumped'
    Check ((Get-PullRequestVersion '0.4.0' '0.3.17' '0.3.17' '0.3.18') -eq '0.4.0') 'an explicit version replaces the branch version'
    Expect-Rejected { Get-PullRequestVersion '0.3.17' '0.3.17' '0.3.17' '0.3.18' } 'an explicit version must still be higher than every release'
    foreach ($path in @('src/app.cs', 'Directory.Build.props', 'scripts/ship.ps1', '.claude/skills/review-pr/SKILL.md', 'src/BlackPearl.Desktop/Assets/BlackPearl.png', 'src/BlackPearl.Desktop/Assets/Tmdb.png', 'src/BlackPearl.Desktop/Assets/Promo/teaser-clam-intro.mp4', 'deploy/haproxy.cfg', 'deploy/blackpearl.env.example', '.gitattributes')) {
        Check ((Assert-ShipPathName $path) -eq $path) "release source path $path is accepted"
    }
    foreach ($path in @('../outside.cs','sites','artifacts/report.html','sites/src/app.ts','.tools/helper.ps1','src/credentials/token.json','src/.env','src/library.db','src/personal.png','src/BlackPearl.Desktop/Assets/snapshot.png','src/BlackPearl.Desktop/Assets/Promo/other.mp4','src/clip.mp4','TEST-REPORT.html','.claude/settings.json','deploy/blackpearl.env','deploy/.env')) {
        Expect-Rejected { Assert-ShipPathName $path } "excluded path $path cannot enter a pull request"
    }
    $summary = "Fixes seeking.`n`nIntentional: removes the old toggle."
    $body = Format-PullRequestBody $summary 'Seeking is faster' '0.3.18'
    Check ((Get-ReleaseNote $body) -ceq 'Seeking is faster' -and (Get-PullRequestSummary $body) -ceq $summary) 'release note and description survive a pull request body round trip'
    Check ($null -eq (Get-ReleaseNote 'Edited by hand')) 'a body without a release note yields none'
    $headA = 'a' * 40; $headB = 'b' * 40
    function New-ReviewComment { param([string]$Head, [string]$Verdict, [string]$Association = 'OWNER', [int]$Minute = 0)
        [pscustomobject]@{ body = "Findings`n$(Format-ReviewMarker $Head $Verdict)"; author_association = $Association; created_at = [datetime]::new(2026, 1, 1, 0, $Minute, 0); html_url = "https://github.com/fixture/$Minute" } }
    Check ((Assert-ReviewApproval @((New-ReviewComment $headA 'changes' -Minute 1), (New-ReviewComment $headA 'approve' -Minute 2)) $headA).Verdict -eq 'approve') 'an approval of the exact head after earlier findings allows the merge'
    Expect-Rejected { Assert-ReviewApproval @() $headA } 'a pull request without a review cannot merge'
    Expect-Rejected { Assert-ReviewApproval @(New-ReviewComment $headA 'approve') $headB } 'an approval of an older head does not cover new commits'
    Expect-Rejected { Assert-ReviewApproval @((New-ReviewComment $headA 'approve' -Minute 1), (New-ReviewComment $headA 'changes' -Minute 2)) $headA } 'requested changes after an approval block the merge'
    Expect-Rejected { Assert-ReviewApproval @((New-ReviewComment $headA 'changes' -Minute 2), (New-ReviewComment $headA 'approve' -Minute 1)) $headA } 'the newest verdict wins regardless of response order'
    Expect-Rejected { Assert-ReviewApproval @(New-ReviewComment $headA 'approve' 'NONE') $headA } 'a public commenter cannot approve'
    $quoting = New-ReviewComment $headA 'changes' -Minute 3
    $quoting.body = "The parser accepts ``$(Format-ReviewMarker $headA 'approve')`` anywhere.`n$(Format-ReviewMarker $headA 'changes')"
    Expect-Rejected { Assert-ReviewApproval @((New-ReviewComment $headA 'approve' -Minute 1), $quoting) $headA } 'findings quoting an approval marker cannot turn requested changes into an approval'
    $sameSecond = @((New-ReviewComment $headA 'changes'), (New-ReviewComment $headA 'approve'))
    $sameSecond[0] | Add-Member id 20; $sameSecond[1] | Add-Member id 10
    Expect-Rejected { Assert-ReviewApproval $sameSecond $headA } 'comments posted in the same second are ordered by id'
    Expect-Rejected { Assert-ReviewApproval @([pscustomobject]@{ body = "LGTM <!-- blackpearl-review head=$headA verdict=approved -->"; author_association = 'OWNER'; created_at = [datetime]::UtcNow }) $headA } 'a malformed verdict marker does not approve'
    $former = New-ReviewComment $headA 'approve'
    $former.body = "Findings`n<!-- watchroom-review head=$headA verdict=approve -->"
    Check ((Assert-ReviewApproval @($former) $headA).Verdict -eq 'approve') 'a verdict posted before the rename with the watchroom-review marker still counts'
    function New-BuildRun { param([string]$Event, [string]$Status, [string]$Conclusion, [int]$Id = 1, [int]$Attempt = 1, [string]$Head = $headA)
        [pscustomobject]@{ id = $Id; run_attempt = $Attempt; event = $Event; status = $Status; conclusion = $Conclusion; head_sha = $Head; name = 'Build and release'; html_url = "https://github.com/fixture/run/$Id" } }
    Check ((Get-HeadCheckState @() $headA).State -eq 'pending') 'merging waits for the build workflow to start'
    Check ((Get-HeadCheckState @((New-BuildRun 'push' 'completed' 'success'), (New-BuildRun 'pull_request' 'in_progress' $null -Id 2)) $headA).State -eq 'pending') 'merging waits for every build of the head'
    Check ((Get-HeadCheckState @((New-BuildRun 'push' 'completed' 'success'), (New-BuildRun 'pull_request' 'completed' 'success' -Id 2)) $headA).State -eq 'success') 'passing builds of the head allow the merge'
    Check ((Get-HeadCheckState @((New-BuildRun 'push' 'completed' 'success'), (New-BuildRun 'pull_request' 'completed' 'failure' -Id 2)) $headA).State -eq 'failure') 'a failed build of the head blocks the merge'
    Check ((Get-HeadCheckState @((New-BuildRun 'pull_request' 'completed' 'failure'), (New-BuildRun 'pull_request' 'completed' 'success' -Attempt 2)) $headA).State -eq 'success') 'a successful rerun replaces its failed attempt'
    Check ((Get-HeadCheckState @(New-BuildRun 'push' 'completed' 'cancelled') $headA).State -eq 'failure') 'a cancelled build of the head blocks the merge'
    Check ((Get-HeadCheckState @(New-BuildRun 'push' 'completed' 'success' -Head $headB) $headA).State -eq 'pending') 'builds of other commits do not count'
    $fileName = 'BlackPearl-Setup-0.3.18-win-x64.exe'
    $filePath = Join-Path $testRoot $fileName
    [IO.File]::WriteAllBytes($filePath, [byte[]](1,2,3,4))
    $hash = (Get-FileHash $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset = [pscustomobject]@{ name=$fileName; size=4; digest="sha256:$hash"; browser_download_url="https://github.com/Svovoniks/BlackPearl/releases/download/v0.3.18/$fileName" }
    $sumPath = Join-Path $testRoot 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($sumPath, "$hash  $fileName`n")
    $fileList = [pscustomobject]@{ name='BlackPearl-files-0.3.18-win-x64.txt'; size=10; digest="sha256:$('a' * 64)"; browser_download_url='https://github.com/Svovoniks/BlackPearl/releases/download/v0.3.18/BlackPearl-files-0.3.18-win-x64.txt' }
    $serverBuild = [pscustomobject]@{ name='BlackPearl-Server-0.3.18-linux-x64.tar.gz'; size=10; digest="sha256:$('b' * 64)"; browser_download_url='https://github.com/Svovoniks/BlackPearl/releases/download/v0.3.18/BlackPearl-Server-0.3.18-linux-x64.tar.gz' }
    $release = [pscustomobject]@{ tag_name='v0.3.18'; draft=$false; prerelease=$false; assets=@($asset, [pscustomobject]@{name='SHA256SUMS.txt';browser_download_url='https://github.com/Svovoniks/BlackPearl/releases/download/v0.3.18/SHA256SUMS.txt'}, $fileList, $serverBuild) }
    $verified = Assert-ReleaseMetadata $release '0.3.18'
    Check ($verified.Updates -contains $serverBuild) 'the room service build is checked against SHA256SUMS.txt with the other assets'
    $release.assets = @($release.assets | Where-Object { $_ -ne $fileList })
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'a release without its file list cannot be reported as shipped'
    $release.assets = @($release.assets) + $fileList
    $release.assets = @($release.assets | Where-Object { $_ -ne $serverBuild })
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'a release without its room service build cannot be reported as shipped'
    $release.assets = @($release.assets) + $serverBuild
    Check ((Assert-InstallerBytes $filePath $sumPath $asset) -eq $hash) 'correct installer matches both public digests'
    $release.draft=$true
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'draft cannot be reported as shipped'
    $release.draft=$false; $release.prerelease=$true
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'prerelease cannot be reported as stable'
    $release.prerelease=$false
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.19' } 'wrong latest version cannot pass verification'
    $asset.browser_download_url='https://example.com/installer.exe'
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'installer URL must belong to the expected release'
    $asset.browser_download_url="https://github.com/Svovoniks/BlackPearl/releases/download/v0.3.18/$fileName"
    $asset.digest=''
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'missing GitHub digest prevents verification'
    $asset.digest="sha256:$hash"
    [IO.File]::WriteAllBytes($filePath, [byte[]](4,3,2,1))
    Expect-Rejected { Assert-InstallerBytes $filePath $sumPath $asset } 'same-size corrupted installer is rejected'
    [IO.File]::WriteAllBytes($filePath, [byte[]](1,2,3,4))
    [IO.File]::WriteAllText($sumPath, ('0' * 64) + "  $fileName`n")
    Expect-Rejected { Assert-InstallerBytes $filePath $sumPath $asset } 'checksum-file disagreement is rejected'
    [IO.File]::WriteAllText($sumPath, "$hash  $fileName`n")
    $updateName = 'BlackPearl-Update-0.3.17-to-0.3.18-win-x64.exe'
    $updatePath = Join-Path $testRoot $updateName
    [IO.File]::WriteAllBytes($updatePath, [byte[]](5,6))
    $updateHash = (Get-FileHash $updatePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::AppendAllText($sumPath, "$updateHash  $updateName`n")
    $update = [pscustomobject]@{ name=$updateName; size=2; digest="sha256:$updateHash"; browser_download_url="https://github.com/Svovoniks/BlackPearl/releases/download/v0.3.18/$updateName" }
    $release.assets = @($release.assets) + @($update, [pscustomobject]@{ name='BlackPearl-Update-0.3.16-to-0.3.17-win-x64.exe'; size=1; digest=''; browser_download_url='https://example.com/old.exe' })
    $verified = Assert-ReleaseMetadata $release '0.3.18'
    Check (@($verified.Updates | Where-Object name -like 'BlackPearl-Update-*').Count -eq 1 -and (Assert-InstallerBytes $updatePath $sumPath $update) -eq $updateHash) 'update installers for this version are verified like the full installer'
    Assert-PublishedDigest $sumPath $update
    Check $true 'an update installer is verified by its published digest without downloading it'
    $update.digest = 'sha256:' + ('0' * 64)
    Expect-Rejected { Assert-PublishedDigest $sumPath $update } 'an update installer whose published digest is missing from SHA256SUMS.txt is rejected'
    $update.digest = "sha256:$updateHash"
    $update.browser_download_url = 'https://example.com/update.exe'
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'update installer URL must belong to the expected release'
    $update.browser_download_url = "https://github.com/Svovoniks/BlackPearl/releases/download/v0.3.18/$updateName"
    $update.digest = ''
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'update installer without a GitHub digest prevents verification'
    function Get-PublicJson {
        param([string]$Url)
        return @{ workflow_runs = @(
            @{id=1;head_branch='master';head_sha='fixture';name='Build and release';status='completed';conclusion='success';run_attempt=1},
            @{id=2;head_branch='v0.3.18';head_sha='other';name='Build and release';status='completed';conclusion='success';run_attempt=1},
            @{id=3;head_branch='v0.3.18';head_sha='fixture';name='Build and release';status='completed';conclusion=$script:mockConclusion;run_attempt=1;html_url='https://github.com/fixture/run'}
        ) }
    }
    $script:mockConclusion = 'success'
    $run = Wait-ReleaseBuild '0.3.18' 'fixture' ([datetime]::UtcNow.AddSeconds(5))
    Check ($run.id -eq 3) 'only the exact tag and commit workflow count as a release build'
    $script:mockConclusion = 'failure'
    Expect-Rejected { Wait-ReleaseBuild '0.3.18' 'fixture' ([datetime]::UtcNow.AddSeconds(5)) } 'failed tag workflow stops shipping'
    # Release checks run in the background after the push; -Merge reads their record.
    $recordPath = Join-Path $testRoot "checks/$headA.json"
    Check ((Get-CheckState $recordPath).State -eq 'missing') 'a commit without a check record is reported as unchecked'
    Write-CheckRecord $recordPath @{ Commit = $headA; Version = '0.3.18'; Checked = '2026-01-01T00:00:00Z' }
    Check ((Get-CheckState $recordPath).State -eq 'passed') 'a record written before background checks means the checks passed'
    $self = Get-Process -Id $PID
    Write-CheckRecord $recordPath @{ Commit = $headA; State = 'running'; Pid = $PID; ProcessStart = $self.StartTime.ToUniversalTime().Ticks }
    Check ((Get-CheckState $recordPath).State -eq 'running') 'checks whose process is alive are running'
    Write-CheckRecord $recordPath @{ Commit = $headA; State = 'running'; Pid = $PID; ProcessStart = $self.StartTime.ToUniversalTime().Ticks - 1 }
    Check ((Get-CheckState $recordPath).State -eq 'interrupted') 'a reused process id does not count as the running checks'
    $finished = Start-Process -FilePath $self.Path -ArgumentList @('-NoProfile', '-Command', 'exit 0') -WindowStyle Hidden -PassThru
    $finished.WaitForExit()
    Write-CheckRecord $recordPath @{ Commit = $headA; State = 'running'; Pid = $finished.Id; ProcessStart = $finished.StartTime.ToUniversalTime().Ticks }
    Check ((Get-CheckState $recordPath).State -eq 'interrupted') 'checks whose process ended without a result were interrupted'
    Write-CheckRecord $recordPath @{ Commit = $headA; State = 'running'; Pid = 4; ProcessStart = 1 }
    Check ((Get-CheckState $recordPath).State -eq 'interrupted') 'a process id reused by a protected system process does not count as the running checks'
    Write-CheckRecord $recordPath @{ Commit = $headA; State = 'failed'; Error = 'fixture failure' }
    Check ((Get-CheckState $recordPath).State -eq 'failed') 'failed checks are reported as failed'
    Check (@(Get-ChildItem -LiteralPath (Split-Path -Parent $recordPath) -Filter '*.partial-*').Count -eq 0) 'check records are replaced whole'
    $copyRepo = Join-Path $testRoot 'check-copy-repo'
    Invoke-Native git @('init', '--quiet', $copyRepo)
    Invoke-Native git @('-C', $copyRepo, '-c', 'user.name=Shipping checks', '-c', 'user.email=checks@example.invalid', 'commit', '--quiet', '--allow-empty', '-m', 'fixture')
    $staleCopy = Join-Path $testRoot 'stale-check-copy'
    Invoke-Native git @('-C', $copyRepo, 'worktree', 'add', '--quiet', '--detach', $staleCopy, 'HEAD')
    $self = Get-Process -Id $PID
    Write-CheckRecord $recordPath @{ Commit = $headA; State = 'running'; Pid = $PID; ProcessStart = $self.StartTime.ToUniversalTime().Ticks; Checkout = $staleCopy }
    Remove-StaleCheckCopy $copyRepo $recordPath
    Check (Test-Path -LiteralPath $staleCopy) 'the copy of checks still running is kept'
    Write-CheckRecord $recordPath @{ Commit = $headA; State = 'running'; Pid = 4; ProcessStart = 1; Checkout = $staleCopy }
    Remove-StaleCheckCopy $copyRepo $recordPath
    Check (!(Test-Path -LiteralPath $staleCopy) -and @(Invoke-Native git @('-C', $copyRepo, 'worktree', 'list')).Count -eq 1) 'checking a commit again removes the copy an interrupted run left behind'
    # A newer commit of the branch replaces older ones, whose failed or interrupted copies are then removed.
    $copyRecords = Join-Path (Invoke-Native git @('-C', $copyRepo, 'rev-parse', '--path-format=absolute', '--git-common-dir')).Trim() 'blackpearl-checks'
    $otherCopy = Join-Path $testRoot 'other-branch-copy'
    Invoke-Native git @('-C', $copyRepo, 'worktree', 'add', '--quiet', '--detach', $staleCopy, 'HEAD')
    Invoke-Native git @('-C', $copyRepo, 'worktree', 'add', '--quiet', '--detach', $otherCopy, 'HEAD')
    Write-CheckRecord (Join-Path $copyRecords "$headA.json") @{ Commit = $headA; Branch = 't3/fixture'; State = 'failed'; Error = 'fixture'; Checkout = $staleCopy }
    Write-CheckRecord (Join-Path $copyRecords "$('c' * 40).json") @{ Commit = 'c' * 40; Branch = 't3/other'; State = 'failed'; Error = 'fixture'; Checkout = $otherCopy }
    Stop-SupersededChecks $copyRepo 't3/fixture' $headB
    $replaced = Get-CheckState (Join-Path $copyRecords "$headA.json")
    Check (!(Test-Path -LiteralPath $staleCopy) -and $replaced.State -eq 'failed' -and !$replaced.PSObject.Properties['Checkout']) 'a newer commit of the branch removes the copy a failed run of an older one kept'
    Check (Test-Path -LiteralPath $otherCopy) 'copies kept for other branches stay'
    Invoke-Native git @('-C', $copyRepo, 'worktree', 'add', '--quiet', '--detach', $staleCopy, 'HEAD')
    Write-CheckRecord (Join-Path $copyRecords "$headA.json") @{ Commit = $headA; Branch = 't3/fixture'; State = 'running'; Pid = 4; ProcessStart = 1; Checkout = $staleCopy }
    Stop-SupersededChecks $copyRepo 't3/fixture' $headB
    $replaced = Get-CheckState (Join-Path $copyRecords "$headA.json")
    Check (!(Test-Path -LiteralPath $staleCopy) -and $replaced.State -eq 'interrupted' -and !$replaced.PSObject.Properties['Checkout']) 'a newer commit of the branch removes the copy an interrupted run of an older one left'
    # One list of checks: the workflow runs the shared script rather than its own copy of the suites.
    $workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.github/workflows/build.yml') -Raw
    Check ($workflow -match 'scripts/release-checks\.ps1 -Lane \$\{\{ matrix\.lane \}\}' -and $workflow -notmatch 'dotnet run --project tests/') 'the workflow runs the release checks from the shared list'
    # A run with a wait limit stops with a named next command instead of waiting past it, so an agent can wait in the
    # foreground: a thread whose pull request merged can be settled, and its agent stopped, between turns.
    $now = [datetime]::UtcNow
    Check ((Get-ShipPauseAt 0) -eq [datetime]::MaxValue) 'a wait limit of 0 never stops a run early'
    Check ((Get-ShipPauseAt 7 $now) -eq $now.AddMinutes(7)) 'a wait limit stops a run after that many minutes'
    Suspend-ShipWhenDue $now.AddMinutes(5) 'fixture' './scripts/ship.ps1 -Merge'
    Check $true 'a run within its wait limit keeps waiting'
    $pause = $null
    try { Suspend-ShipWhenDue $now.AddSeconds(-1) 'the fixture is still running' './scripts/ship.ps1 -Merge' } catch { $pause = $_.Exception }
    Check ($pause -is [ShipPausedException] -and $pause.Message.Contains('./scripts/ship.ps1 -Merge') -and $pause.Message.Contains('the fixture is still running')) 'a run past its wait limit stops and names what it waits for and the command that continues'
    # One deadline for a whole ship: each stopped run hands it to the next, so -TimeoutMinutes still ends a stuck ship.
    Check ((Get-ShipDeadline '' 60 $now) -eq $now.AddMinutes(60)) 'a first run gets the deadline -TimeoutMinutes sets'
    $carried = Format-ShipResume './scripts/ship.ps1 -Merge' $now.AddMinutes(53) 6
    $until = [regex]::Match($carried, "-Until '([^']+)'").Groups[1].Value
    Check ((Get-ShipDeadline $until 60 $now.AddMinutes(30)) -eq $now.AddMinutes(53) -and $carried.Contains('-WaitMinutes 6')) 'a rerun keeps the deadline and wait limit of the run that stopped'
    Check ($null -ne [System.Management.Automation.Language.Parser]::ParseInput($carried, [ref]$null, [ref]$null) -and $carried.StartsWith('./scripts/ship.ps1 -Merge ')) 'the command a stopped run names can be run as printed'
    foreach ($bad in @('tomorrow', '2026-01-01T00:00:00', '2026-01-01T04:00:00+04:00')) {
        Expect-Rejected { Get-ShipDeadline $bad 60 $now } "-Until $bad is rejected"
    }
    $originalPublicJson = ${function:Get-PublicJson}
    try {
        $script:fixtureRun = [pscustomobject]@{ id = 7; status = 'in_progress'; conclusion = $null; head_branch = 'v0.3.18'; head_sha = $headA; name = 'Build and release'; run_attempt = 1; html_url = 'https://github.com/fixture/run' }
        $script:fixtureLatest = 'v0.3.17'
        function Get-PublicJson {
            param([string]$Uri)
            if ($Uri -match '/releases/latest$') { return [pscustomobject]@{ tag_name = $script:fixtureLatest } }
            if ($Uri -match '/actions/runs[?]') { return [pscustomobject]@{ workflow_runs = @($script:fixtureRun) } }
            return $script:fixtureRun
        }
        $resume = './scripts/ship.ps1 -VerifyOnly -Version 0.3.18'
        $pause = $null
        try { Wait-ReleaseBuild '0.3.18' $headA $now.AddMinutes(60) $now.AddSeconds(-1) $resume | Out-Null } catch { $pause = $_.Exception }
        Check ($pause -is [ShipPausedException] -and $pause.Message.Contains($resume)) 'a tag build still running past the wait limit stops the run and names -VerifyOnly'
        $pause = $null
        try { Wait-ReleaseBuild '0.3.18' $script:fixtureRun.head_sha $now.AddMinutes(60) $now.AddSeconds(-1) $resume '#7 is merged as abc and tagged v0.3.18' | Out-Null } catch { $pause = $_.Exception }
        Check ($pause -is [ShipPausedException] -and $pause.Message.Contains('#7 is merged as abc and tagged v0.3.18, but the v0.3.18 tag build') -and $pause.Message.Contains("Run $resume in the foreground")) 'a stop after the merge says the pull request is merged, outside the command to run'
        $timeout = $null
        try { Wait-ReleaseBuild '0.3.18' $headA $now.AddSeconds(-1) $now.AddSeconds(-1) $resume | Out-Null } catch { $timeout = $_.Exception }
        Check ($timeout -and $timeout -isnot [ShipPausedException]) 'a tag build past the overall timeout still fails'
        $script:fixtureRun.status = 'completed'; $script:fixtureRun.conclusion = 'success'
        Check ((Wait-ReleaseBuild '0.3.18' $headA $now.AddMinutes(60) $now.AddSeconds(-1) $resume).id -eq 7) 'a finished tag build is used even when the wait limit has passed'
        Check ((Wait-ReleaseBuild '0.3.18' $headA $now.AddSeconds(-1) $now.AddSeconds(-1) $resume).id -eq 7) 'a rerun that starts at its carried deadline still finds a finished tag build'
        $script:fixtureRun.conclusion = 'failure'
        $failed = $null
        try { Wait-ReleaseBuild '0.3.18' $headA $now.AddMinutes(60) $now.AddSeconds(-1) $resume | Out-Null } catch { $failed = $_.Exception }
        Check ($failed -and $failed -isnot [ShipPausedException] -and $failed.Message -match 'Release build failed') 'a failed tag build fails rather than stopping to wait'
        $pause = $null
        try { Test-PublishedRelease '0.3.18' $testRoot $now.AddMinutes(60) $now.AddSeconds(-1) $resume | Out-Null } catch { $pause = $_.Exception }
        Check ($pause -is [ShipPausedException] -and $pause.Message.Contains('v0.3.17')) 'a release not yet public past the wait limit stops the run'
        $script:fixtureLatest = 'v0.0.1'
        $failed = $null
        try { Test-PublishedRelease '0.3.18' $testRoot $now.AddSeconds(-1) $now.AddSeconds(-1) $resume | Out-Null } catch { $failed = $_.Exception }
        Check ($failed -and $failed -isnot [ShipPausedException]) 'a release still not public at the carried deadline fails instead of waiting again'
    }
    finally { Set-Item -Path function:Get-PublicJson -Value $originalPublicJson }
    . (Join-Path $PSScriptRoot 'ship-sync-checks.ps1') -TestRoot $testRoot
    Write-Host "$checks shipping safety checks passed. No releases were created."
}
finally {
    # This is the exact fresh directory created above, beneath the system temp root.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -match '^blackpearl-ship-checks-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
