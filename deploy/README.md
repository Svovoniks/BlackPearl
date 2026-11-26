# Deploy the room service on a VPS

The VPS only coordinates rooms: invitations, who is in each room, who holds the streaming role, and WebRTC signaling. Video, voice and controls go directly between participants. When a direct path fails, Cloudflare's TURN service relays it, so no media passes through the VPS and its bandwidth use stays small.

Without a registered domain, use [sslip.io](https://sslip.io): a name such as `203-0-113-10.sslip.io` resolves to the IP address it spells (`203.0.113.10`), so the server can still get a public certificate. The examples below use that placeholder; substitute your server's name.

## Layout

- **HAProxy** owns public TCP 443 (`haproxy.cfg`). It forwards TLS connections to Caddy with the PROXY protocol and everything else to sshd, because some networks allow SSH only on 443. Clients that wait for the server's SSH banner connect after a 5 second delay. Because sshd sees these connections from loopback, HAProxy limits SSH attempts to 10 per minute per client address; use key-only SSH authentication on such a server.
- **Caddy** (2.7 or later, for `proxy_protocol`) terminates HTTPS on `127.0.0.1:8443` (`Caddyfile`). It obtains certificates with the TLS-ALPN challenge through 443, so port 80 stays free for other services.
- **BlackPearl.Server** listens on `127.0.0.1:5080` (`blackpearl.service`). It trusts `X-Forwarded-For` only from loopback, so rate limits apply per real client.

## Requirements

- Ubuntu with `haproxy`, `aspnetcore-runtime-10.0`, and Caddy from the [official repository](https://caddyserver.com/docs/install#debian-ubuntu-raspbian). Ubuntu's own Caddy package is too old.
- For automatic updates, also `curl` and `python3`, which Ubuntu installs by default, as it does `flock` and `setpriv`.
- Public TCP 443 reachable. Nothing else needs to be open.
- A Cloudflare Realtime TURN key: its key ID and API token. Without them the service still works, but only participants who can connect directly will succeed.

## Install or update

1. Publish on a development machine: `dotnet publish src/BlackPearl.Server -c Release -o artifacts/server-publish`. Copy the output to `/opt/blackpearl` on the VPS.
2. Install `blackpearl.service` to `/etc/systemd/system/`. Create `/etc/blackpearl/blackpearl.env` from `blackpearl.env.example` with mode 600, then `systemctl enable --now blackpearl`.
3. Install `Caddyfile` to `/etc/caddy/Caddyfile`. Set `Environment=BLACKPEARL_DOMAIN=203-0-113-10.sslip.io` (your server's name) in a `systemctl edit caddy` drop-in, then restart Caddy.
4. Install `haproxy.cfg` to `/etc/haproxy/haproxy.cfg`. Set `Environment=MULTIPLEX_BIND=:443` in a `systemctl edit haproxy` drop-in. Anything else listening on 443 must be stopped first. Keep a second SSH route open, or schedule an automatic rollback, before switching 443.
5. Verify that `https://203-0-113-10.sslip.io/health` returns `status: ok`, `relayConfigured: true` and `stunConfigured: true`, and that `ssh -p 443` still connects.

A server installed while the app was called Watchroom (`/opt/watchroom`, `watchroom.service`, `/etc/watchroom/watchroom.env`, `/var/lib/watchroom`) keeps working with its `WATCHROOM_*` settings, which the server still reads. To update it without moving anything, publish as in step 1 to `/opt/watchroom` and change `ExecStart` in `watchroom.service` to run `BlackPearl.Server.dll` instead of `Watchroom.Server.dll`. Keep the Caddy drop-in's `WATCHROOM_DOMAIN` unless you also install the new `Caddyfile`, which reads `BLACKPEARL_DOMAIN`.

To update by hand, repeat step 1 and run `systemctl restart blackpearl`, or let the server update itself as described next. Once it has, `/opt/blackpearl` is a link to a release folder; install a hand-made build with `blackpearl-update.sh --file` instead of copying over it. Saved rooms, with their members and the people removed from them, are stored under `/var/lib/blackpearl` and survive a restart; everyone in a room is disconnected by it and their apps re-enter by themselves.

## Automatic updates

Every release from 0.3.159 on carries `BlackPearl-Server-VERSION-linux-x64.tar.gz`, the room service built by the release workflow, listed in the release's `SHA256SUMS.txt`. `blackpearl-update.sh` keeps the server on the latest one. `blackpearl-update.timer` runs it as root every 15 minutes, and each run does this:

1. It asks GitHub for the latest stable release and stops if that is not newer than the running build.
2. It downloads the room service build and checks it against GitHub's SHA-256 and the release's `SHA256SUMS.txt`.
3. It unpacks the build into `/opt/blackpearl-releases/VERSION`, owned by root and not writable by anyone else, and runs it once as `nobody` with `--describe`. That shows the build starts here and reports its version and room protocols.
4. It switches when nobody is in a room: the service writes `status.json` to its data folder every 10 seconds, with how many people are connected to rooms. Phone remote links don't count, since a PC with the remote turned on keeps one open. It switches right away when the new build speaks a room protocol the running one does not, so newer apps aren't turned away for long, and in any case once a release has waited `MAX_WAIT_HOURS` (24), counted from the first release still waiting.
5. It points `/opt/blackpearl` at the new folder, restarts the service, and waits up to a minute for `/health` and for `status.json` to report the new version. If the build doesn't come up, it goes back to the previous folder, prints the failed build's log lines, and skips that version until a newer one is released.
6. It keeps the three newest releases.

To install it:

```sh
install -D -m 755 blackpearl-update.sh /usr/local/lib/blackpearl/blackpearl-update.sh
install -m 644 blackpearl-update.service blackpearl-update.timer /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now blackpearl-update.timer
```

`/opt/blackpearl` may hold a build installed by hand. On its first switch, the updater moves that folder to `/opt/blackpearl-releases/manual-DATE` and keeps it to go back to. A server installed under other names, such as the Watchroom ones above, sets them in `/etc/blackpearl/update.conf`; `blackpearl-update.conf.example` lists every setting. `blackpearl-update.sh --check` reports what a run would do without changing anything. `journalctl -u blackpearl-update` shows what each run did. `--file ARCHIVE --version X.Y.Z` installs a local build in the same way.

The service speaks two room protocols, listed by `/health` as `protocols`. Protocol 2 is rooms without a host. Protocol 1 is what apps from before that speak: a host who admits each guest. Update the service before releasing an app on a newer protocol: an app says so when the room server is too old for it, while older apps keep working with a newer service. With automatic updates, a server switches to such a release within about 15 minutes of it being published. A room serves one protocol at a time, set by whoever is in it: someone on the other kind of app is told to update, or that the others need to. Its invitation, creator, name and people are the same on both, so a room carries over as its people update.

## Internet acceptance test

Use two Windows computers on different internet connections (for example home broadband and a mobile hotspot), not merely two processes on the same LAN.

1. Play a real MKV and MP4 for at least 20 minutes, checking pause, seek, subtitles, and reconnection.
2. Set `BLACKPEARL_FORCE_RELAY=true`, restart `blackpearl`, and repeat to exercise Cloudflare's relay. Restore `false` afterward.
3. Limit the streamer's upload and verify shared buffering and automatic resume.
4. As the room's creator, remove someone while streaming and confirm media access stops and they cannot re-enter. Test a wrong code.
5. Have a second person start a video while the first is playing one, and have the streamer leave: the stream should move, voice should not drop, and the room should stay open.

The current architecture sends one copy per viewer, even through TURN, so a relay does not reduce the streamer's upload requirements. Every member also holds a connection to every other member for voice. Cloudflare bills relayed traffic beyond its free allowance; monitor usage in the Cloudflare dashboard.

## Logs

The service writes one line per event to the journal: rooms created, opened, emptied and closed, everyone entering and leaving with the reason and how long they stayed, removals and people let back in, a second window replacing an earlier one, phones asking for their PC, PCs coming online and going offline, and every connection turned away with the error the app was given. Under systemd each line starts with its journal priority, so the journal keeps the level and adds the time. Pings and playback never log, so a room that is only watching writes nothing; a typical evening of two people in a room and a phone remote is about a dozen lines.

```sh
journalctl -u blackpearl -f                          # follow (the Watchroom install's unit is watchroom)
journalctl -u blackpearl -p warning --since today    # only connections turned away or lost, and errors
journalctl -u blackpearl -g '\[3f2a9c1b\]'           # every line about one connection
journalctl -u blackpearl -g 'room 5f2494'            # everything about one room
journalctl -u blackpearl -o short-iso-precise        # timestamps to the microsecond
```

Every line about a connection starts with its connection id, eight hex digits in brackets that the service picks when the socket opens. The log never holds display or room names, invitation codes, keys, member, phone or PC ids, signal contents, or client addresses. A room, a person or a PC is named by a tag instead: the first six hex digits of the SHA-256 of its invitation code, member id or PC id, the same on every line and every connection. To find the tag for an invitation code or a PC id you have, `printf %s 0123456789ABCDEF01234567 | sha256sum | cut -c1-6`, with the code or id in capitals.

What a leaving line's reason means:

- `closed by the app`, or `the app cut the connection without a WebSocket close`: the app left. BlackPearl cuts its room connection when it leaves, so the second is the usual one; an app that quits or crashes looks the same.
- `no answer to the service's keep-alive pings for 30 s`: the app or its network went quiet without closing the connection, such as a sleeping laptop or a lost Wi-Fi link. The service pings every 20 seconds and ends the connection within about a minute.
- `replaced by connection …` or `removed by …`: someone entered again from another window, or the room's creator removed them.
- `refused: …`: the service turned the app away with that error, such as a message flood or an invalid invitation.
- `the app stopped reading`: 128 messages waited to be sent to it, so it was cut off.
- `reached the 6-hour connection limit`: by design; the app connects again on its own.

Levels and format are set with `Logging__*` settings in `/etc/blackpearl/blackpearl.env`, followed by a restart. `Logging__LogLevel__BlackPearl=Debug` adds a line for every signal relayed or dropped, which is busy during connection setup. `Logging__Console__FormatterName=json` writes JSON lines with each value as a named field, for tools such as `jq`; the journal then shows every line at the same priority. ASP.NET's own lines are limited to warnings by default; `Logging__LogLevel__Microsoft.AspNetCore=Information` brings back its line for every request, which includes the server's address.

## Operations

- Cloudflare credentials are fetched at startup and refreshed in the background. Each credential handed to a participant stays valid for at least 12 hours. If Cloudflare is unreachable, participants receive STUN only and the service retries every minute.
- SSH through HAProxy reaches sshd from `127.0.0.1`, so sshd logs show loopback instead of the client address.
- Invitation codes are random 96-bit capabilities: anyone holding one can enter the room, and `POST /presence` tells them who is in it. The room's creator can remove a member, who is then turned away until let back in. A room holds eight people at once. Rooms the app creates are saved; unsaved rooms, which only tests create, expire after six hours.
- The server does not persist accounts, videos, chat or libraries. Keep `/etc/blackpearl/blackpearl.env` private.
