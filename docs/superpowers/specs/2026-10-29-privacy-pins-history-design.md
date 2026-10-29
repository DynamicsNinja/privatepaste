# Private copies, pinned clips and history clearing

Status: draft for the owner's review (2026-10-29). Builds on `2026-10-28-privatepaste-design.md`; the server, desktop and Android apps and the web UI as they are on `main` today.

## Why

Three gaps against other clipboard tools (research of 2026-10-29: Paste, Ditto, CopyQ, Windows clipboard history, ClipCascade):

1. **Passwords spread everywhere.** Every password copied from a password manager is uploaded, kept forever, searchable in the web UI and pushed to every device. Windows' own clipboard history, Paste and CopyQ skip copies that password managers mark as private. For a tool called PrivatePaste this is the most likely real harm.
2. **No way to keep things at hand.** Windows pins, Ditto sticky clips, Paste pinboards: everyone else lets you keep reusable clips. PrivatePaste only has a chronological history.
3. **No way to clear history.** History is kept forever and can only be deleted one clip at a time.

## Goals (owner's decisions)

### 1. Private copies never leave the device

- **Marked copies are ignored completely.** When the app a copy came from marks it as private, PrivatePaste does not upload it, does not add it to any history or cache, and does not send it anywhere. Same as Windows' own clipboard history. The owner uses Bitwarden, 1Password, KeePass/KeePassXC and LastPass.
- **Markers to honour (from research; verify each against the real apps, see Testing):**
  - Windows: any of the clipboard formats `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore`, or `CanIncludeInClipboardHistory` / `CanUploadToCloudClipboard` holding a DWORD 0.
  - macOS: the pasteboard types `org.nspasteboard.ConcealedType` (1Password, Keychain Access) and `org.nspasteboard.TransientType`.
  - Linux: the MIME target `x-kde-passwordManagerHint` with the value `secret` (KeePassXC, KDE Wallet).
  - Android (13+): `ClipDescription.EXTRA_IS_SENSITIVE`. Android only reads the clipboard when the user explicitly sends it (Quick Settings tile, Send activity) or shares something. The owner accepted that explicit sends still send what the user chose, so Android does not block on this flag. *My call:* the Send activity shows the usual result; no extra prompt.
- **A blocklist of apps on desktop.** Desktop Settings gets a list of apps whose copies are always ignored, like marked copies. It covers apps that don't mark their copies (possibly 1Password on Windows, banking apps). It is desktop only, because Android can't tell which app copied something.
  - The app is identified by its process/executable name (Windows: the clipboard owner window's process, falling back to the foreground window's process when the owner is unknown; macOS: the frontmost app's bundle id when the clipboard changed; Linux: the clipboard owner window's `_NET_WM_PID` on X11).
  - *My call (Linux):* the blocklist is offered on X11 only. On Wayland the copying app can't be identified, so the blocklist setting is hidden and replaced by a one-line explanation; a list that silently never matches would be a privacy failure.
  - *My call:* adding an app is done by picking from "apps you recently copied from" (names of the last ~10 source apps this session, kept in memory only) or typing an executable name; entries can be removed. Stored in the desktop settings file.
  - Known limitation, to say in the UI and docs: copies from a password manager's **browser extension** come from the browser, so the blocklist can't target them; they are only skipped if the extension marks them.
- **No guessing.** Text that only looks like a password (random strings, API keys, hashes) syncs normally.
- Nothing about a private copy is logged (no content, no length). A log line may say "skipped a copy marked private" or "skipped a copy from a blocked app".

### 2. Pinned clips

- **Any clip can be pinned**: text, links and images.
- **A separate Pinned tab** in every app: web UI, desktop popup and Android app. History stays the normal newest-first list of everything (pinned clips included, with a pin marker); the Pinned tab shows only pinned clips, **newest pinned first**. Search filters whichever tab is open.
- **Pin and unpin everywhere**, synced to all devices:
  - Web UI: a pin/unpin control per row; History/Pinned tabs on the history page.
  - Desktop popup: History/Pinned tabs, Tab switches between them; a pin control on each row and *my call* Ctrl+P toggles the pin of the selected row (the popup's key router must route Tab and Ctrl+P instead of swallowing them).
  - Android: History/Pinned tabs; long-press a row opens a small menu with Pin/Unpin and Delete (replacing long-press = delete).
- Pinning or unpinning does not change a clip's position in History, does not count as copying it and never touches any clipboard.
- Deleting a pinned clip deletes it (and its pin) everywhere, as today.
- Pinned clips older than a client's local cache (desktop keeps the latest 500, Android the latest 200) must still show in that client's Pinned tab.

### 3. Clearing history

- **Kept forever by default**, as now.
- **"Clear history" in the web UI**: deletes every unpinned clip on the server and every device, after a confirmation that says how many clips will go and that pinned clips stay.
- **Optional auto-delete in the web UI**, off by default: delete unpinned clips older than 7, 30 or 90 days. It is a server setting (applies to every device). *My call:* the server checks at startup and then hourly.
- **A clip's age** counts from the later of its last copy and its unpin (owner's decision): unpinning an old clip puts it back in history as if just used, so it is not auto-deleted straight away.
- **Cleared stays cleared** (owner's decision). A device that was offline during a clear may still have clips queued from before it (desktop and Android upload offline copies later as backfill with their original copy time). The server remembers when history was last cleared and drops backfilled uploads copied before then; it also applies the auto-delete age limit to backfilled uploads, so they don't appear briefly and then vanish.
- Pinned clips are never cleared or auto-deleted.

## Non-goals

- End-to-end encryption, file sync, multiple users (later candidates from the same research).
- Guessing passwords from content.
- Manual ordering of pins.
- Clear history in the desktop or Android apps.
- Blocking explicit sends on Android.
- Retroactively finding passwords already in history (the owner can delete them or use Clear history).

## Anti-goals (would be a failure even if it works)

- A marked copy reaches the server, a log, a cache file or another device in any form.
- Pinning or clearing breaks older clients still in use (desktop 0.1.5, Android 0.1.2), e.g. they crash, loop on sync or paste something.
- Clear history or auto-delete removes a pinned clip.
- The popup loses the "never takes the focus" behaviour from desktop 0.1.5.

## Constraints

- **Protocol compatibility both ways** (CLAUDE.md): new fields optional, nothing renamed or removed. Older clients must keep working against the new server and vice versa. *My call:* "older clients" means the versions in use, desktop 0.1.5 and Android 0.1.2; check their live-apply and catch-up code at those tags. Android parses with `ignoreUnknownKeys`; anything that can be missing must be nullable with a default.
- A pin change must reach other devices through the existing sync (live WebSocket and `/api/sync` catch-up) without being mistaken for a copy: it must never be applied to a clipboard, by old or new clients. Bulk deletes must reach clients that were offline (tombstones / `deletedIds`), including when thousands of clips go at once.
- **Schema change**: the server migrates the live database at startup when `main` is pushed. The migration must be idempotent and transactional like the existing one, and the owner must back up `/data` before that push.
- Desktop: macOS and Linux read the clipboard through Avalonia polling; whether the marker types are visible there must be checked on each platform. If a platform can't see them, ship without it there and say so in the docs (Windows is the one the owner uses daily).
- UI copy and look follow `DESIGN.md`. A visible change to the web history, desktop popup or Android history means rebuilding the README hero (CLAUDE.md rule).
- Never log clip contents, typed keys or private copies.

## Testing the owner cares about

- With the real apps on Windows: copy a password from Bitwarden, 1Password, KeePass/KeePassXC and LastPass (desktop apps, and each browser extension) and confirm what is skipped; for anything that isn't marked, confirm the blocklist catches the desktop app. Record the results in `docs/desktop.md` (which apps are skipped automatically, which need the blocklist, which can't be caught).
  - *My call:* the owner runs these with a test build (they need the owner's real apps and accounts, and agents must not touch the owner's installed setup). The builder covers the detection with automated tests by putting the marker formats on the clipboard themselves. A manager that turns out not to mark its copies does not block the release; it goes in the docs as "add it to the blocklist".
- Pin on one device, see it in Pinned on the others; unpin likewise; old clients unaffected.
- Clear history with pins present; an offline device catches up correctly when it reconnects.

## Left to the builder

API shapes, storage of pins and settings, how pin changes and bulk deletes are carried over sync, how clients keep pinned clips beyond their cache, the exact UI layout within `DESIGN.md`, and per-platform detection code.
