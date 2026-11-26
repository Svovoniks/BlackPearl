# Builds and app updates

## Release checklist for agents

The public source and release repository is `Svovoniks/BlackPearl` (formerly
`Svovoniks/WatchRoom`; see [Renaming from Watchroom](#renaming-from-watchroom)), and the release
branch is `master`. The updater uses its public `/releases/latest` endpoint.
Shipping is authorized when the user asks to ship an update. Complete the whole
flow below rather than stopping at a pushed branch, an open pull request, or a
workflow artifact.

Shipping means a reviewed pull request merged into `master`, then publishing and
verifying the GitHub release. Agents never push to `master` directly, and all agent
work happens in git worktrees (see AGENTS.md). Do not open, control, or update the
user's desktop app as part of shipping. Install a release locally only when the user
explicitly requests a local installation. Computer use is not part of this release
checklist.

1. **Open the pull request.** Commit the change on your worktree's branch. Write the
   description to a file outside the repository: what changed and why, intentional
   behavior changes, removals, or performance costs, and how it was tested. Then run
   `./scripts/ship.ps1 -Message '<release note>' -BodyFile <path>`.
2. **Get an independent review.** Ask a separate agent with fresh context to review
   the PR with the `review-pr` skill (`.claude/skills/review-pr/SKILL.md`), giving it
   the PR number and head commit. The reviewer posts its verdict on the pull request
   with `scripts/review-pr.ps1`. It blocks on bugs, performance regressions, and
   broken existing features unless the description says the change is intentional.
3. **Fix until approved.** Fix the blocking findings, commit, rerun
   `./scripts/ship.ps1` (no arguments are needed to update the same PR), and ask for
   a re-review of the new head with the earlier findings and your replies. If the
   same point is disputed twice, or after five rounds without approval, ask the user.
4. **Merge and release.** Run `./scripts/ship.ps1 -Merge`. Report the PR, version,
   release URL, Actions result, local sync result, and any validation limitations.

Use the scripted flow by default. The goal is to let the automation do the bulk
of the work while agents stay focused on the change, the review, failure triage,
and release validation.

## Shipping with the release script

Run `scripts/ship.ps1` in PowerShell 7 from your worktree, on a feature branch that
contains the latest `origin/master` and has no uncommitted tracked changes.

**Open or update a pull request** (default mode). The script rejects branches whose
diff from `origin/master` touches anything other than release source paths:
`sites`, `artifacts/`, `.tools/`, databases, credentials, local HTML reports,
generated media, top-level dotfiles such as `.gitignore`, and `.github/` outside
`workflows/` are refused. Change those by hand, outside this flow. It picks a version above both the latest public release
and `master`, and commits `Set version X` on the branch if needed. A branch that
already has such a version keeps it across review rounds. The script pushes the
branch and creates the pull request, or updates the existing one's title,
description, and release note, so the workflow and the review start at once. The
release note is kept in the description under `### Release note` and becomes the
release commit message and tag message. It then starts the release checks in a
background process that outlives the script, and returns. They run
`scripts/release-checks.ps1 -Lane all`, the same list of checks the workflow
splits across its runners, including the full smoke suite against a fresh test
directory. They run in a detached copy of the commit under the system temp
directory, so untracked files cannot affect the result and SQLite paths stay
within Windows limits. Their record and log live in the repository's shared git
directory, so every worktree sees them. Pushing a newer commit of the same branch
stops the checks of the older one. A commit whose checks already passed or are
still running is not checked again.

**Status** (`-Status`, optionally `-Pr N`). Shows the head's release checks
(running, passed, failed with the error and log, or interrupted), the workflow,
and the review verdict.

**Review.** `scripts/review-pr.ps1 -Pr N` checks out the head in a fresh temp
worktree and prints the description, changed files, CI state, release check state,
and earlier verdicts. It copies the cached voice libraries and models into the
worktree, so building there does not compile or download them again.
`-Verdict approve|changes -Head <sha> -BodyFile <path>` posts the verdict as a PR
comment with a `blackpearl-review` marker for that exact commit. Every account here
is the repository owner, and GitHub does not let authors approve their own pull
requests, so the marker comment is the approval. Only markers from the owner,
members, or collaborators count. The newest one wins.

**Merge** (`-Merge`, optionally `-Pr N`). The script refuses unless all of these
hold:

- The pull request is open, not a draft, and from this repository into `master`.
- The release checks have not failed on its exact head.
- The head contains the current `origin/master`.
- The newest reviewer verdict approves that head.
- Its version is higher than the latest release and has no tag.

It waits for the `Build and release` workflow runs and the release checks on the
head to succeed. Release checks that were interrupted, for example by a restart,
or never started on this computer run in the foreground first. It then
squash-merges with GitHub's head-SHA guard and the title `Release BlackPearl X:
<release note>`. It confirms the merged tree is exactly the reviewed tree, then
tags and pushes `vX`. It deletes the merged branch, waits for the tag workflow, and
verifies the public release, updater feed, installer size, and SHA-256 against both
GitHub and `SHA256SUMS.txt`. It downloads only the full installer; every other asset's
GitHub digest must match its line in `SHA256SUMS.txt`. Finally, it pulls `master` into the checkout that owns
that branch. If `master` has no checkout, it fast-forwards the local branch without
switching any checkout.

The script re-reads the verdict right before merging, so a rejection posted while CI
runs still stops the merge. If anything fails after the merge, the error names the
merged commit and the recovery step, because rerunning `-Merge` cannot help then.

**Without a release** (`-NoRelease`, only when the user asks not to release). Both
modes keep the version from `master`. They refuse a branch that changes it, and need
no release note. `-Merge -NoRelease` applies the same review, check, and CI gates.
It squash-merges with the PR title and synchronizes local `master`, with no tag or
release.

Any new commit, including a merge of `master` into the branch, needs another
`ship.ps1` run and a re-review, because approval covers one exact commit.

Local work is saved in a recovery stash and under the ignored verification
directory before synchronizing a dirty master. The script merges the worktree
and index separately to preserve staged and unstaged edits, retains untracked
files, and leaves an untracked `sites` folder alone. Already-shipped changes do
not remain as duplicate local edits. Divergence or conflicting local work stops
the sync without resetting commits or overwriting files. The recovery stash is
retained, and the error explicitly distinguishes a verified public release from
an incomplete local sync. `-VerifyOnly` also runs this final synchronization step
so it can complete a previously interrupted ship.

```powershell
# Preview the branch's files and version without changing anything.
./scripts/ship.ps1 -Plan

# Push, open the pull request, and start the release checks in the background.
./scripts/ship.ps1 -Message 'Clarify playback language preferences' -BodyFile $env:TEMP/pr-body.md

# Release checks, workflow, and review verdict for the branch's pull request.
./scripts/ship.ps1 -Status

# After fixing review findings or a failed check: push, update the same pull request, and check again.
./scripts/ship.ps1

# Reviewer: check out, then post a verdict for the exact reviewed head.
./scripts/review-pr.ps1 -Pr 12
./scripts/review-pr.ps1 -Pr 12 -Verdict approve -Head <sha> -BodyFile $env:TEMP/review.md

# Preview the merge gates, then merge, tag, and verify the release.
./scripts/ship.ps1 -Merge -Plan
./scripts/ship.ps1 -Merge

# A change the user does not want released: same flow, no version bump, tag, or release.
./scripts/ship.ps1 -NoRelease -Title 'Tidy the shipping docs' -BodyFile $env:TEMP/pr-body.md
./scripts/ship.ps1 -Merge -NoRelease

# Recheck an already pushed tag after a timeout or network interruption.
./scripts/ship.ps1 -VerifyOnly -Version 0.3.18

# Offline safety checks for the scripts; no pull request or release is created.
./scripts/ship-checks.ps1
```

Use `-Sdk C:/path/to/dotnet.exe` when the SDK is outside the main checkout.
Otherwise the script uses `.tools/dotnet/dotnet.exe` from the worktree or the main
checkout, then `dotnet` from PATH. Pull requests, reviews, and merges use the GitHub
credential git already uses for pushes, read through `git credential fill`; it is
never printed. Public release verification stays unauthenticated. No GitHub CLI is
needed. `-TimeoutMinutes` defaults to 60. Verification records and downloaded
installers remain under ignored `artifacts/`. A failed or interrupted check keeps
its copy of the commit, named by `-Status`, for investigation until the commit is
checked again or a newer commit of the branch replaces it. A failed workflow stops the script. It never
rewrites a tag, overwrites a published installer, or installs the app locally.

### Manual fallback

Use this only to recover when the scripts cannot run:

1. Work on a branch in a worktree. Keep personal HTML investigations, library
   snapshots, credentials, and the `sites` folder out of commits.
2. Choose a version higher than the latest published stable release. Update
   `<Version>` in `Directory.Build.props` (the single source of the app version).
   Use three numeric components, such as `0.2.1`; the release tag must be exactly
   `v0.2.1`. The workflow rejects tags that disagree with the source version.
3. Build `BlackPearl.slnx` in Release and run `./scripts/release-checks.ps1 -Lane all`
   from the worktree root, or at least the checks below, and resolve failures. For
   playback or room-networking changes, also run the full smoke suite against a
   fresh test directory. Do not reuse previous smoke data.
4. Push the branch, open a pull request, and get the reviewer's approval as above.
   Merge only the approved head. Then tag that exact merge commit with an annotated
   tag and push the tag. Never force-push or move an existing release tag.
5. Wait for the **tag's** `Build and release` Actions run to succeed. A branch or
   pull request build does not publish a release. The tag workflow uploads the
   installers to a draft release, then publishes it as the latest release. A pull
   request that raises the version builds them beside its checks, and the tag
   publishes those exact files when that run passed on the same tree, as it has for
   every merge from `ship.ps1`, and was built against the release that is still the
   latest. Otherwise the tag workflow checks and packages the commit itself.
6. Verify without authentication that `/releases/latest` returns the expected
   tag, `draft=false`, and `prerelease=false`. Check that the Windows installer has
   the exact expected filename, a nonzero size, and a `sha256:` digest. Download
   the installer and verify its SHA-256 against the API digest and SHA256SUMS.txt.
   This step matters: a private or incomplete release cannot update users' apps.
7. Check that the public GitHub release page is available and links to the
   installer and checksums.
8. Synchronize local `master` with `origin/master` and confirm it contains the
   published commit. Preserve unrelated staged/unstaged edits, untracked files,
   and the `sites` folder. Leave the user's installed app unchanged.

PowerShell checks (use `dotnet` instead of the workspace SDK path on other machines):

```powershell
.tools/dotnet/dotnet.exe restore BlackPearl.slnx --locked-mode
.tools/dotnet/dotnet.exe run --project tests/BlackPearl.UpdateChecks -c Release
.tools/dotnet/dotnet.exe run --project tests/BlackPearl.Smoke -c Release -- --metadata-cache
.tools/dotnet/dotnet.exe run --project tests/BlackPearl.DesktopChecks -c Release
```

When voice chat changes, also run `tests/BlackPearl.DesktopChecks -- --voice-processing`; a fresh checkout builds
the voice processing libraries once, which needs Visual Studio's C++ tools and Rust (`cargo`).

When packaging or the updater changes, also run `./scripts/update-package-checks.ps1`.
CI runs it on every packaged build.

Inspect Actions at https://github.com/Svovoniks/BlackPearl/actions and releases at
https://github.com/Svovoniks/BlackPearl/releases. Public REST endpoints work without
authentication; never print credentials when using authenticated APIs.

Public updater verification:

```powershell
$release = Invoke-RestMethod 'https://api.github.com/repos/Svovoniks/BlackPearl/releases/latest'
$release | Select-Object tag_name,draft,prerelease
$release.assets | Select-Object name,size,digest,browser_download_url
```

For a local installer build, publish first using the source version, then call
`./scripts/package.ps1 -SkipPublish`. Both the workflow and package script derive
the version from `Directory.Build.props`; the package script refuses to package an
app whose embedded version does not match. Do not call Inno Setup directly.

If a run fails before publication, inspect its logs and fix the failure. A draft
release is not shipped. If a failure leaves a draft behind, finish that draft only
when it still corresponds to the same validated commit and installer; otherwise
ship a higher version through a new pull request and leave the failed draft
unpublished. Never overwrite bytes for an already published version. For a
defective published version, release a higher patch version with the fix; the
updater refuses downgrades.

## Diagnosing room and playback failures

Playback snapshots distinguish requested Play/Pause from a temporary buffering
hold. Readiness messages from updated apps carry the movie ID and seek generation;
late readiness for an earlier seek is ignored. Explicit seeks supersede buffering
at the previous position. Canceled seeks stop transfers that have no
remaining consumers. VLC's network cache is chosen per movie from the measured
round trip (1.5–5 s), so starting and seeking wait only for that short buffer.
A viewer keeps the room's video in a temporary file
(`%TEMP%\BlackPearl\stream-cache\*.stream`), deleted two minutes after the
player stops reading from it, or when the viewer leaves the room or the app exits. Once the first frames play, read-ahead
downloads the rest of the video past the playback position, also while paused,
with up to 16 reads in flight. It starts no read while the player is itself
waiting for video. A seek outside the buffered video cancels its reads; it
continues from the new position once playback advances, or after a second when
the video is paused. The two minutes are for the video to come back
(`CloseIdleBuffers`): the same video opened again continues with the file. That
covers a viewer's own reconnect to the room, during which the time does not
count, and a streamer who lost the connection, left the room with it, and starts
the video again on returning.
While the drive has less than 4 GB free, or the file cannot be created, the
buffer is held in memory instead: 30 seconds of video (estimated from the
average bitrate plus 25%, capped at 256 MiB) plus 16 MiB of recently read data.
During a peer interruption, playback continues for as long as buffered video
lasts. Local-file playback bypasses network buffering. The diagnostics log
records `stream-cache` events and, in each `sample`, the bytes buffered ahead.
`dotnet run --project tests/BlackPearl.Smoke -c Release -- --buffer-run <video>`
measures buffering of a real video through real peer connections and the player.
Whoever plays a room's video chooses its **stream quality** in the player's
Quality pill: Original, 1080p, 720p or 480p. Original streams the file as above.
A lower quality converts the video on the streamer's PC while it plays, with the
bundled `ffmpeg\ffmpeg.exe` (GPL build, a separate program; version and hash are
pinned in `BlackPearl.Desktop.csproj` and it is cached under
`.tools\models\ffmpeg-<version>`). The choice is saved in the `streamQuality`
setting and applies to the next video. The converted stream carries the
selected audio track as stereo AAC and the selected subtitles drawn into the
picture; HDR is tone mapped. A video is offered the qualities of the 16:9 frame
it is cut from, so a 1920x800 film is offered 1080p, and it is never enlarged. A
file whose bitrate is already at or below the chosen quality's is streamed as its
file, whatever its size, and the menu says so. The picture is HEVC where the streamer's graphics hardware encodes it, and H.264
otherwise. HEVC shows the same picture in about a third fewer bits, so with it
1080p, 720p and 480p take at most 3.2, 1.9 and 1.0 Mbps of video instead of 5.0,
2.8 and 1.4; the menu and `RoomStream.VideoKbps` say which. Only hardware HEVC
encoders are used (NVIDIA, Intel Quick Sync, AMD, in that order):
`Ffmpeg.HevcEncoderAsync` finds one once per run by encoding a second of video
with each, and `BLACKPEARL_STREAM_CODEC=h264` leaves HEVC unused. One that passes
that check and then converts nothing is replaced by the H.264 encoders, and
streams after that are planned without it. H.264 comes from the first encoder
that works: NVIDIA, Intel Quick Sync, AMD, x264, Windows' own. Every encoder
stops at a picture quality where the video needs less than the bitrate, and at
the bitrate where it would need more. Intel's does that only when given a target
with a peak above it (its QVBR mode, with plain VBR as the fallback for hardware
without it); given a quality and a peak alone it ignores the peak. The peak is
one kbit above the target, because Intel's HEVC encoder stays at its peak for as
long as the video is hard.
The stream is offered in the room state (`RoomSnapshot.Stream`, sent with the
`stream` command) as four-second pieces. Viewers read
`stream/<id>/<piece>/size` and `stream/<id>/<piece>` over the media channel, keep
the pieces in `%TEMP%\BlackPearl\stream-cache\*.segments`, read ahead to the end
as with a file, and play them from a local playlist. ffmpeg runs from the piece
somebody asks for, so a seek converts from there; it holds five minutes ahead of
the furthest request. Changing the quality, audio track or subtitles makes a new
stream, which viewers reopen at the room's position. As a video that will be
converted starts, the streamer first offers a stream marked `Preparing`, within
1.5 seconds of the video: viewers wait for the stream instead of opening the
file and switching a moment later. If the stream is not ready 5 seconds after
the video started, the announcement is withdrawn and they open the file. Music
is neither announced nor converted, and has no Quality pill. A file the converter
cannot read, or a converter that does not start, leaves the file streamed as it
is, with the reason in the menu. Apps from before converted
streams ignore the offer and are served the file. Viewers of a converted stream
cannot change tracks. A viewer's pieces belong to the stream's id and wait two
minutes for it like the file's buffer, so a viewer who reconnects to the room
continues with what it had downloaded. A streamer who reconnects keeps its
conversion meanwhile, starts the video again under the id it had, and offers the
same stream, so viewers continue with their pieces then too.
The menu shows what sending the file takes (its size over its length) beside
Original. Each viewer's app measures **how fast the video reaches it**: the bytes
that arrive over the time it has at least eight reads under way (`TransferMeter`),
which is when the connection, not the video's bitrate, sets the pace. It needs
four such seconds within the last twenty. Every two seconds it sends a fresh
measurement to whoever plays the video as `receive-rate`, only if that app said it
takes it (`HostRoomState.Features`); the report changes nothing in the room. The
streamer's menu names the slowest viewer measured in the last minute. When a
measurement from the last ten seconds stays below what is sent plus 20% for ten
seconds, the player offers the best quality that fits (`StreamQuality.Suggest`),
or the smallest when none does, once per video; the card leaves after 45 seconds.
Nothing is measured while nobody else is in the room, or once a viewer has the
whole video buffered.
A viewer's timeline draws how far the video is **buffered** as a lighter grey
(`SliderBuffer.Fraction`, from `MediaBridge.BufferedUntilMs`). A converted
stream's pieces give the time exactly; for a file the buffered bytes are taken as
spread evenly over its length, so the bar is an estimate there. A video played
from this PC draws none.
The diagnostics log records `stream-quality`, `stream-convert` and
`stream-quality-offer` events on the streaming side and `piece-read` on the viewer's.
`dotnet run --project tests/BlackPearl.Smoke -c Release -- --buffer-run <video> --quality 720p`
measures it on a real video, and `-- --stream-frame <video> <seconds> <quality> <picture.png>`
saves the frame viewers get.
Buffering recovery requires 750 ms of stable full cache.
Every member of a room holds a direct connection to every other. A lost one is
replaced while the room service is reachable: the member whose id sorts first
makes the new offer, and the other asks for one through the room service. Six
seconds without control replies from whoever is playing the video also starts
recovery (twelve for the other members, whose connections carry only voice),
checked every two seconds. A viewer retains its video input and cache, continues already
running playback from buffered data for up to the active buffer setting after
recovery starts, and retries interrupted media ranges for up to 45 seconds.
Playback pauses sooner if the native input starves, fails or ends, or if Pause
is pending; a paused input never starts merely because recovery begins. Restored connections
resynchronize playback and report readiness again. A failed native player input
is reopened when necessary; when the streamer really leaves, its video is cleared
and the streaming role passes to whoever has been in the room longest.
Participant departure alerts wait 30 seconds and are canceled when that peer's
control handshake returns, avoiding sounds and floating alerts for brief losses.
Diagnostics include command queueing, the streamer's receipt/acceptance, acknowledgement,
player application, and scoped readiness, making network delay distinguishable
from a playback hold. The `applied` event records issuing the native player action;
`native-time` and `settle-end` establish when the native position catches up.

Windows builds save connection and playback diagnostics automatically to
`%LOCALAPPDATA%\BlackPearl\logs`. Settings offers **Open logs folder**. After
reproducing a failed watch session, close both apps and share the logs from both
devices, together with the approximate time of the failure.

`diagnostics.jsonl` contains timestamped structured events for app version,
room-service capabilities, peer connection states, control-channel negotiation,
member departures, changes of the streaming role, media loading, buffering, sync
corrections, and transfer errors.
It excludes invitation codes, credentials, raw signaling, and movie contents.
Updated peers negotiate an independent connection for media so a congested movie
transfer cannot queue playback commands behind its SCTP traffic. Diagnostics
record `media-transport-isolated` and `media-transport-state` for this connection.
Peers without this optional capability retain the legacy media protocol.
The logger keeps four files of up to 5 MiB each and writes asynchronously.
`BLACKPEARL_DIAGNOSTICS` still overrides the output directory for test runs.

## Build and updater behavior

GitHub Actions runs the checks on pull requests, split across three runners: the
desktop suites, and the two halves of the full smoke suite (`--part 1` and
`--part 2`), the first with the shipping script checks (`scripts/ship-checks.ps1`).
Both the workflow and `ship.ps1` take the list from `scripts/release-checks.ps1`. A pull request from this repository whose version is above the latest
release also builds the Windows x64 app and installers on a fourth runner, from the
head commit. Its artifact, `BlackPearl-windows-VERSION-after-LATEST`, names the
latest release its update installers were built against and is kept for 14 days.
A version tag publishes that artifact when a passing pull request run has the
tag's tree and the same latest release; otherwise it builds the installers itself,
and runs the checks too unless a pull request run already passed them on the same
tree. Manual runs always package, and check unless a pull request run already
passed on the same tree. Pushes to `master` only restore and build
whatever the build caches lack, since pull requests can read master's caches but
not each other's.

Push a version tag such as `v0.2.1` to publish a GitHub Release. Tags must use
`vMAJOR.MINOR.PATCH`; the same version is embedded in the application and installer.
The workflow uploads `BlackPearl-Setup-VERSION-win-x64.exe`, the file list
`BlackPearl-files-VERSION-win-x64.txt` (SHA-256, size and path of every installed
file), update installers `BlackPearl-Update-FROM-to-VERSION-win-x64.exe` from
each of the 20 previous stable releases, the room service build
`BlackPearl-Server-VERSION-linux-x64.tar.gz` that room servers update themselves
from (deploy/README.md, "Automatic updates"), and `SHA256SUMS.txt` covering all of
them. Only tagged builds publish releases. Do not replace an existing release
with different bytes; publish a new version instead.

An update installer holds only the files that differ from release FROM, plus
deletions of files the new version dropped, so an update usually downloads a few
megabytes instead of the whole app with its .NET runtime, VLC and models.
`scripts/package-updates.ps1` builds them on CI from the earlier releases'
published file lists. The most recent release, and up to three releases from
before file lists were published, are instead installed from their published
installers (digest verified) into a scratch folder; the most recent one's
update installer is applied over that installation, and the build fails unless
the folder then matches the new build exactly and the installation matched its
file list. The script refuses to run outside CI because it installs releases
into the current user profile. An update installer has the same AppId, so the
uninstaller also removes the files it added; it refuses to run unless the
registered installation is FROM in the target folder.
`scripts/update-package-checks.ps1` exercises this offline with a fixture under
a separate test identity, without file associations or shortcuts, in a scratch
folder under `artifacts/`: file lists, a direct update started while the app
is still closing, refusals, a two-step chain run the way the app runs it, and
uninstalling.

The Windows app checks GitHub's latest stable release 15 seconds after startup
and every six hours. Settings offers **Check for updates** and a saved preference
to disable automatic checks. Checks do not install anything. A newer release
shows an update card; **Later** hides that version until a manual check or a
different release.

**Update** streams the installer into the profile's `updates` folder, reports
progress, and verifies its length and GitHub-provided SHA-256 digest. Failed or
interrupted downloads never launch an installer. When the release has an update
installer from the running version, the updater downloads that one. An
installation older than the last 20 releases chains update installers through
intermediate releases (the cheapest chain among the last 100 releases) when the
chain is smaller than half the full installer; a short script in the `updates`
folder runs them one after another once the app has closed, only the last one
reopens BlackPearl, and a failed step reopens the version installed by then.
Before using update installers the app checks its installed files against its
own release's published file list. If the files differ, any download fails
verification, or an installer cannot be started (antivirus software may hold
back a fresh download briefly, which is retried, or remove it), the full
installer is downloaded instead; update installers that ran without reaching a
version are not offered for it again. The update card shows the download size. After successful verification,
the per-user Inno Setup installer updates the installed app's directory, and
reopens BlackPearl. The app starts the installer and then closes, and closing
can take several seconds, longer on a busy computer. Restart Manager cannot
close an app that has no window left, and a silent setup that finds its files
in use rolls back. So a silent setup first waits up to a minute for
`BlackPearl.exe` (or `Watchroom.exe`) in the installation folder to stop
running, and logs that wait. With `/SILENT`, as the app runs it, setup's window
shows a progress page while it waits and keeps responding. Only the copy running from the installer-registered folder (or
the default folder when none is registered) updates itself; development builds and
copied folders disable update checks so they cannot take over the installation.
Library data, rooms, queues, and credentials remain outside the installation
directory. Updating closes playback and disconnects the current room.
Installation failures are handled by Inno Setup; its setup log is available for
diagnosis. The binaries currently have no publisher code-signing certificate.
Checksums protect against transfer corruption, not a compromised GitHub account.

Update downloads require a public releases repository. No GitHub token is stored
in the distributed app. `UpdateRepository` is an MSBuild property baked into the
Windows assembly; it defaults to `Svovoniks/BlackPearl;Svovoniks/WatchRoom`, a list of names
the updater tries in order. The second is the repository's former name, which GitHub redirects.

## Renaming from Watchroom

The app was called Watchroom, and its repository `Svovoniks/WatchRoom`. The app became BlackPearl
in 0.3.91 (November 1, 2026), and the repository became `Svovoniks/BlackPearl` on
November 17, 2026; 0.3.132 was the first release published under the new name. What existing
installations rely on keeps working:

- **Installations.** The installer keeps the same Inno Setup `AppId`, so it updates a Watchroom
  installation in place, in its existing `Programs\Watchroom` folder. It removes the old
  `Watchroom.exe` and `Watchroom.*.dll`, the Watchroom Start menu shortcut, and the
  Watchroom file-association registration. It relaunches the app when asked with either
  `/BLACKPEARLUPDATE=1` or the older `/WATCHROOMUPDATE=1`.
- **Data.** A library in `%LOCALAPPDATA%\Watchroom` stays there and stays in use; new
  installations use `%LOCALAPPDATA%\BlackPearl`. A TMDB key saved under the Watchroom
  credential name moves to the BlackPearl name when it is first read. Every `WATCHROOM_*`
  environment setting still applies when the matching `BLACKPEARL_*` one is not set, in the app
  and on the room server, and plugins receive both names.
- **Compatibility with older versions.** The single-instance lock, the peer control channel
  (`watchroom-control-v2`) and the `X-Watchroom-Request-Id` header keep their names, so
  BlackPearl and Watchroom still work together in rooms and on one library.
- **Updates.** BlackPearl 0.3.91 and later try `Svovoniks/BlackPearl` first, then
  `Svovoniks/WatchRoom`, and accept downloads under either name. Watchroom 0.3.90 and older
  accept downloads only from URLs under `Svovoniks/WatchRoom` and look only for `Watchroom-*`
  assets, so they no longer update themselves; run the current full installer by hand on them.
  Releases now carry only BlackPearl names.
- **Repository.** Never create another repository named `WatchRoom` under the account: GitHub
  would stop redirecting the old name, and old links, clones and updaters that fall back to it
  would break. A clone made before the rename needs `git remote set-url origin
  https://github.com/Svovoniks/BlackPearl.git`; `ship.ps1` refuses the old remote. The workflow
  reads the TMDB key only from the `BLACKPEARL_TMDB_API_KEY` Actions secret, and the crash-report
  DSN from `BLACKPEARL_DESKTOP_SENTRY_DSN` (optional: without it the build warns and sends no reports).

To test: `dotnet run --project tests/BlackPearl.UpdateChecks -c Release`.
For isolated desktop tests set `BLACKPEARL_DISABLE_UPDATES=1`; normal users do not
need this environment variable.
