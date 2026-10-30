# Multiple users on one server

Status: draft for the owner's review (2026-10-30). Builds on `2026-10-28-privatepaste-design.md` and `2026-10-29-privacy-pins-history-design.md`; the server, desktop and Android apps and the web UI as they are on `main` today.

## Why

Users asked for it. Today a server has one password (`PP_PASSWORD`) and everyone who knows it is the same anonymous "owner". People sharing a server (a household, a small team) want:

1. **To see who copied what.** History rows only show a device name, which says little when several people's devices are mixed together.
2. **To lock one person out** without changing the password for everyone. Today revoking someone's devices does nothing lasting: they sign straight back in with the shared password.

Everyone keeps sharing **one clipboard history**; that is the point of the product for these users.

## Today (for the builder)

- `PasswordVerifier` hashes `PP_PASSWORD` with Argon2id in memory at startup; `/api/auth/login` (`LoginRequest(Password, DeviceName, Platform)`) creates a row in `devices` and returns a long-lived bearer token. Devices belong to no one.
- The web UI signs in with a cookie whose principal is a fixed "web-owner" with no device (`Pages/Login.cshtml.cs`).
- One authorization policy, `Owner`: every device and web session can list and revoke all devices, change auto-delete, clear history, pin and delete clips.
- Clips record the last copier's device: `source_device_id`, `source_device_name`, `source_device_platform`.

## Goals (owner's decisions)

### People and roles

- The server has **people** (user accounts). Each signs in with a **name and password**. All people share the one history: everyone sees every clip, and everyone's copies sync to everyone's devices, exactly as today.
- There is **exactly one owner**. The owner can:
  - add people and remove them,
  - see every person's devices and sign out any of them,
  - reset anyone's password,
  - clear the whole history,
  - change auto-delete (a server-wide setting).
- **Regular people** can:
  - use the history fully: copy, pin, unpin and delete single clips,
  - see and sign out **their own** devices only,
  - change their own password.
  They cannot clear the whole history, change auto-delete, or see or manage other people.
- The owner is a person like any other: their copies sync and appear in history with their name.
- The owner can't be removed and there is no second owner.

### Accounts and passwords

- **The owner adds people on the web**, typing a name and a first password, and hands them over out of band. No self sign-up, no invites, no email.
- **Names are unique, case-insensitive** ("Ana" and "ana" are the same person).
- **Anyone can change their own password** on the web, after entering their current one. Their devices stay signed in.
- **The owner can reset anyone's password.** A reset also **signs out all of that person's devices** (and their web sessions), since a reset usually means a lost or leaked password.
- **Removing a person** signs out all their devices and web sessions immediately (open WebSockets closed, tokens rejected from then on). Their past clips stay in history with their name.

### Upgrading an existing server

- On first start of the new version, the **owner account is created from today's setup**: name "owner" (the owner can rename it), password = `PP_PASSWORD`.
- **Every existing device becomes the owner's**, and keeps working without signing in again. An existing web session cookie also keeps working as the owner.
- **Devices that are really someone else's are not moved.** On a server already shared by several people, the owner signs those devices out on the People page and each person signs in again with their own account (owner's decision). There is no "move device to person" control. The upgrade notes in README and `docs/self-hosting.md` say so.
- **Clips copied before the upgrade** are attributed to the owner (every existing device is the owner's; web copies were the owner's).
- **`PP_PASSWORD` after setup is the owner's recovery path.** The server remembers which `PP_PASSWORD` it last saw (as a hash, never in clear). If it starts with a different `PP_PASSWORD`, the owner's password is reset to it. So: owner forgot their password → change the environment variable, restart. If `PP_PASSWORD` is unchanged, a password the owner set on the web stays in force.
- **Old apps must update.** A sign-in without a name is refused; the message says to update PrivatePaste. Devices already signed in with an old app keep syncing (their token is still valid; they just don't send anything new).

### What people see

- **History rows show who copied each clip as a small name chip** before the platform icon and device name (mockup option B, chosen by the owner): `[Ana] 🖥 Work laptop, 2 min ago`. Same in the web history, the desktop popup and the Android history.
- **No chip while the server has only one person** (owner's decision), so single-user installs look the same as today after the upgrade. The chip appears on all rows, old and new, once a second person exists.
- The mockups the owner chose from are in the git-ignored `.superpowers/brainstorm/` folder on the owner's machine; the descriptions here are the source of truth.
- **The web UI's "Devices" page becomes "People"** (mockup option A, chosen by the owner): one card per person with their name chip, a role label for the owner, and their devices listed under it with "Sign out" per device. The owner sees every card plus "Add person" (name + password), and "Reset password" and "Remove" on each other person's card. A regular person sees only their own card.
- **Desktop and Android sign-in screens gain a name field** above the password.

## Non-goals

- Separate or private histories per person, sharing clips with only some people, hiding who copied something.
- Self sign-up, invite links, email, password reset by email.
- More than one owner, other roles, transferring ownership.
- SSO / external identity providers.
- An audit log of who deleted or pinned what.
- Signing old apps in without a name.

## Anti-goals (would be a failure even if it works)

- Anyone having to sign in again on any device just because the server was upgraded.
- A removed person (or someone whose password the owner reset) still syncing, even for a few minutes over an open WebSocket or a long-lived web cookie.
- A regular person able to clear history, change auto-delete or touch other people's devices through the API, even if the web UI hides the buttons.
- Breaking older or newer clients' parsing (see the protocol rules in `CLAUDE.md`).
- Leaking which names exist through sign-in errors or timing.

## Constraints

- **Protocol changes are additive only** (`CLAUDE.md`): new optional fields, nothing renamed or removed; on Android, new fields are nullable with defaults.
  - `LoginRequest` gains an optional name (e.g. `username`). New apps against an old server must still sign in: the old server ignores the unknown field.
  - `ClipDto` gains an optional, nullable name of the person who last copied it (e.g. `sourceUserName`). Clients that don't know it ignore it; a null means show no chip.
  - Anything else the builder adds (people on `DeviceDto`, new endpoints) follows the same rule.
- **Schema change with a startup migration** on the live server: follows the existing idempotent, one-transaction-per-step pattern in `PrivatePasteDb.Migrate`. Before pushing to `main` (which deploys), ask the owner to back up `/data`.
- **Release order:** the server ships first (old and new apps both work against it). New desktop and Android apps also work against an old server. The Dokploy template's image tag is updated with the server release.
- **Passwords** are hashed with Argon2id with a per-person salt, stored in the encrypted DB. Verification stays behind the existing concurrency gate (`PasswordVerifier`'s 2 running / 4 queued) and the existing 5-per-minute-per-IP login rate limit. A sign-in with an unknown name costs the same as one with a wrong password and returns the same error.
- **Authorization is enforced on the server** for every endpoint and Razor page (API and web), not only by hiding UI.
- **Web sessions are revocable.** The cookie must carry the person's id and be checked on each request against the person still existing and not having had their password reset since the cookie was issued (a security stamp or equivalent). Old "web-owner" cookies map to the owner.
- Never log passwords or tokens.
- `TreatWarningsAsErrors`; tests with every behaviour change; UI changes get render/screenshot checks in both themes (`CLAUDE.md`).
- **README in step:** the history-row change means rebuilding the hero images (`scripts/readme/build.ps1`); README, `docs/self-hosting.md` (what `PP_PASSWORD` now means, recovery), `docs/api.md` (new fields and endpoints, sign-in errors), `docs/desktop.md` and `docs/android.md` (sign-in) updated.

## Calls I made (the owner didn't decide these; check them)

- **Change password lives in Settings** for everyone (current, new, repeat). The People page keeps owner actions only. Settings hides auto-delete and Clear history for regular people.
- **The owner can rename any person, including themselves**, from their card on the People page. Clips show the person's **current** name (clips store the person's id; removed people keep a row so their name still resolves).
- **Renames reach every app.** Desktop and Android cache clips and catch up by `seq`, so a rename alone would leave old names in their caches. The server gives each of that person's clips a new `seq` and sends it as a `live: false` change, as pinning does, so it never touches anyone's clipboard. Renames are rare, so the burst is acceptable.
- **The single-person rule is the server's.** The server leaves `sourceUserName` null while there is only one current person (removed people don't count). When the count crosses between one and more than one, it re-sends every clip the same way as a rename, so chips appear or disappear on cached clips too. Clients just show a chip whenever the name is present.
- **A removed person's name can be reused.** Uniqueness only counts current people. An old clip from a removed "Ana" and a new clip from a new "Ana" look the same, which is acceptable.
- **Name rules:** trimmed, 1–32 characters. **Password rules for passwords set on the web:** at least 8 characters, at most 1024 (today's cap). `PP_PASSWORD` keeps its current rules (no minimum; local dev uses `dev`).
- **Recovering via `PP_PASSWORD` does not sign out the owner's devices.** It's the owner getting back in, not a lockout.
- **A failed sign-in says "Wrong name or password."** on all clients and the web.
- **Old apps:** they can't show a custom message, so the server answers a name-less sign-in with a non-401 error whose detail is "Update PrivatePaste to sign in." That keeps old apps from saying "Wrong password", which would mislead. What each old app shows is whatever it already shows for that error.
- **Desktop and Android remember the last name used** and prefill it on the sign-in screen.
- **Android's new-clip notification** says "New clip from Ana · Pixel 8" when a name is known, and the current text otherwise. The desktop toast does the same.
- **The People page lists app devices only.** Web sessions aren't devices today and stay unlisted. They end on sign-out, removal, or reset.

## Left to the builder

- Table and column design (a `users` table, `devices.user_id`, `clips.source_user_id`, where the `PP_PASSWORD` fingerprint lives), and the migration steps.
- Endpoint shapes for managing people and changing a password, and their status codes.
- How the cookie revocation check is done (security stamp claim, per-request lookup, caching).
- The chip's exact styling per platform, within `DESIGN.md` and the mockup.
- How the hero-image build gets a believable multi-person history.
