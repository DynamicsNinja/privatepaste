# Multiple Users Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let one PrivatePaste server have several people (one owner, any number of regular people) who sign in with a name and password and share one clipboard history, with each clip showing who copied it.

**Architecture:** A `users` table owns devices (`devices.user_id`) and clip attribution (`clips.source_user_id`). Device tokens and web cookies carry the person's id and an owner flag. A new `OwnerPolicy` guards owner-only actions, and the old catch-all policy becomes `SignedInPolicy`. The owner account is created at startup from `PP_PASSWORD`, and existing devices and clips are attached to it. `ClipDto` gains an optional `SourceUserName`, which the server fills only while more than one person exists. Renames, and changes in the number of people across one, re-send the affected clips with new `seq`s as `live: false` changes, the same way pins do.

**Tech Stack:** ASP.NET Core (minimal APIs + Razor Pages), SQLite (SQLCipher) via Microsoft.Data.Sqlite, Konscious Argon2id, xUnit, Avalonia, Kotlin/Compose with kotlinx.serialization and Robolectric.

**Spec:** `docs/superpowers/specs/2026-10-30-multiple-users-design.md`

## Global Constraints

- Protocol changes are additive: new optional fields only, nothing renamed or removed. On Android, new fields are nullable with a default (`ignoreUnknownKeys`, `explicitNulls = false`).
- `TreatWarningsAsErrors` is on; `dotnet test` from the repo root must pass after every task.
- Passwords are Argon2id (64 MiB, 3 iterations, parallelism 1, 32-byte hash, 16-byte random salt per password). Every hash or verification goes through `PasswordVerifier`'s gate (2 running / 4 queued). Sign-in stays limited to 5 attempts per minute per IP.
- Never log passwords, tokens or typed text.
- Owner's default name: `owner`. Names: trimmed, 1–32 characters, unique among current (not removed) people, case-insensitive. Passwords set on the web: 8–1024 characters. `PP_PASSWORD` has no minimum.
- UI copy, exactly:
  - "Wrong name or password."
  - "Update PrivatePaste to sign in."
  - "That name is already taken."
  - "Name must be 1-32 characters."
  - "Password must be at least 8 characters."
  - "Current password is wrong."
  - "Passwords don't match."
  - "Password changed."
  - Labels: "Name", "Add person", "Rename", "Reset password", "Remove", "Sign out", "Change password", "People".
- Notification and toast title: `New clip from {person} · {device}` on Android and `From {person} · {device}` on desktop when a person name is present; the current text otherwise.
- Old apps signing in with no name get HTTP 400 with detail "Update PrivatePaste to sign in."; a wrong name or password gets 401 with detail "Wrong name or password.". An unknown name costs one Argon2 hash, the same as a wrong password.
- Schema change: before anything is pushed to `main`, the owner must back up `/data` (pushing `main` deploys).
- UI changes get render/screenshot checks in both themes. The README hero images are rebuilt in the last task.

## Review Focus

1. **A removed person, or one whose password was reset, with an open WebSocket** must be disconnected at once and refused on reconnect. Test in Task 6: `RemovePerson_ClosesTheirOpenWebSocket`.
2. **A web cookie issued before the upgrade** (fixed `web-owner` identity, no person id) must keep working as the owner. A cookie for a removed or reset person must stop working on the next request. Tests in Task 3: `LegacyWebOwnerCookie_StillSignedInAsOwner`, `CookieOfRemovedPerson_RedirectsToLogin`.
3. **A regular person calling owner-only endpoints with a device token** (`POST /api/history/clear`, `PUT /api/settings`, `DELETE /api/devices/{someone else's}`) must be refused, even though the web UI hides those controls. Tests in Task 4.
4. **Restarting with an unchanged `PP_PASSWORD`** after the owner changed their password on the web must keep the web password. A changed `PP_PASSWORD` must reset it. Tests in Task 2.
5. **Names that differ only in case or spacing** ("  ANA ") must collide with "Ana" when adding or renaming a person. Test in Task 6: `AddPerson_NameDifferingOnlyInCaseOrSpaces_IsTaken`.

---

### Task 1: Stored per-person password hashes

**Files:**
- Modify: `src/PrivatePaste.Server/Auth/PasswordVerifier.cs`
- Modify: `src/PrivatePaste.Server/Program.cs` (drop the startup `GetRequiredService<PasswordVerifier>()` warm-up and its comment; Task 2 hashes at startup instead)
- Test: `tests/PrivatePaste.Tests/Auth/PasswordVerifierTests.cs` (new)

**Interfaces:**
- Produces:
  - `PasswordVerifier()`: parameterless, no longer reads `ServerOptions`.
  - `Task<string?> HashAsync(string password, CancellationToken ct = default)`: returns `"argon2id$<base64 salt>$<base64 hash>"`, or null when the gate is busy.
  - `Task<PasswordCheck> VerifyAsync(string? candidate, string? stored, CancellationToken ct = default)`. When `stored` is null (unknown name), it hashes the candidate against a fixed dummy salt and returns `Invalid`. `Busy` when the gate is full. Candidates over 1024 characters are `Invalid` without hashing.
  - `PasswordVerifier.MaxPasswordLength = 1024`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Hash_ThenVerify_Valid()      // HashAsync("pw-123456") then VerifyAsync("pw-123456", h) == Valid
[Fact] public async Task Verify_WrongPassword_Invalid()
[Fact] public async Task Verify_NullStored_InvalidAfterHashing() // VerifyAsync("x", null) == Invalid
[Fact] public async Task Hash_SamePasswordTwice_DifferentSalts() // two HashAsync results differ; both verify
[Fact] public async Task Verify_TooLong_Invalid()     // new string('a', 1025)
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter FullyQualifiedName~PasswordVerifierTests`. Expected: compile errors, because the new members don't exist yet.
- [ ] **Step 3: Implement.** Keep the Argon2 parameters and the `ConcurrencyGate`. Parse `stored` by splitting on `$`. Compare with `CryptographicOperations.FixedTimeEquals`. Callers of the old `VerifyAsync(string?, ct)` (`AuthEndpoints`, `LoginModel`) won't compile until Task 3 switches them to `SignInVerifier`. Tasks 1–3 form one chain: if `dotnet test` isn't green at the end of Task 1 or Task 2, leave those changes uncommitted and commit them with Task 3. Each task still gets its own review.
- [ ] **Step 4: Run them and confirm they pass.** Run the same filter. Expected: PASS.
- [ ] **Step 5: Commit** with `feat(server): per-password salted Argon2id hashes`.

### Task 2: People table, migration and owner bootstrap

**Files:**
- Modify: `src/PrivatePaste.Server/Storage/Schema.sql`. Add `users(id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL, is_owner INTEGER NOT NULL, password_hash TEXT NOT NULL, security_stamp TEXT NOT NULL, created_at TEXT NOT NULL, removed_at TEXT)` and `CREATE UNIQUE INDEX IF NOT EXISTS ux_users_name ON users(name_key) WHERE removed_at IS NULL`.
- Modify: `src/PrivatePaste.Server/Storage/PrivatePasteDb.cs`. Add a `Migrate` step that adds `devices.user_id TEXT` and `clips.source_user_id TEXT`, following the existing `HasColumn` pattern. Also add them to the `CREATE TABLE`s in `Schema.sql` for new databases.
- Create: `src/PrivatePaste.Server/Auth/UserRepository.cs`
- Create: `src/PrivatePaste.Server/Auth/OwnerBootstrap.cs`
- Modify: `src/PrivatePaste.Server/Program.cs`. Register `UserRepository` and `OwnerBootstrap`, and call `await OwnerBootstrap.EnsureOwnerAsync()` right after `PrivatePasteDb.Initialize()`.
- Test: `tests/PrivatePaste.Tests/Storage/OwnerBootstrapTests.cs` (new)

**Interfaces:**
- Consumes: `PasswordVerifier.HashAsync` and `VerifyAsync` (Task 1).
- Produces:
  - `public sealed record UserRecord(Guid Id, string Name, bool IsOwner, string PasswordHash, string SecurityStamp, DateTimeOffset CreatedAt, DateTimeOffset? RemovedAt);`
  - `public sealed class NameTakenException : Exception`
  - `UserRepository(PrivatePasteDb db, TimeProvider time)` with:
    - `Task<UserRecord?> FindCurrentByNameAsync(string name)`: matches on `name.Trim().ToLowerInvariant()`, current people only.
    - `Task<UserRecord?> GetAsync(Guid id)`: includes removed people.
    - `Task<UserRecord?> GetOwnerAsync()`
    - `Task<IReadOnlyList<UserRecord>> ListCurrentAsync()`: owner first, then by name.
    - `Task<int> CountCurrentAsync()`
    - `Task<UserRecord> CreateAsync(string name, string passwordHash, bool isOwner)`: throws `NameTakenException` on the unique index.
    - `Task RenameAsync(Guid id, string name)`: throws `NameTakenException`.
    - `Task SetPasswordAsync(Guid id, string passwordHash, bool newSecurityStamp)`
    - `Task MarkRemovedAsync(Guid id)`: also sets a new security stamp.
    - `public static string? ValidateName(string? name)`: returns the error copy from Global Constraints, or null when the name is valid.
  - `OwnerBootstrap(UserRepository users, PasswordVerifier verifier, SettingsRepository settings, PrivatePasteDb db, ServerOptions options, ILogger<OwnerBootstrap> logger)` with `Task EnsureOwnerAsync()`:
    - **No owner yet:** create `owner` with `HashAsync(PP_PASSWORD)`. In one transaction, run `UPDATE devices SET user_id=@owner WHERE user_id IS NULL` and `UPDATE clips SET source_user_id=@owner WHERE source_user_id IS NULL`. Store the fingerprint: a second `HashAsync(PP_PASSWORD)` saved under settings key `pp_password_hash`.
    - **Owner exists and `VerifyAsync(PP_PASSWORD, fingerprint)` is not `Valid`:** set the owner's password to a new hash of `PP_PASSWORD` without changing the security stamp, replace the fingerprint, and log "The owner's password was reset from PP_PASSWORD."
    - **Otherwise:** do nothing.
    - At startup the gate is idle, so retry `HashAsync` if it returns null instead of failing.
  - `SettingsRepository` gains `Task<string?> GetAsync(string name)` and `Task SetAsync(string name, string value)`, if it has no generic accessor already.

- [ ] **Step 1: Write the failing tests** (use `PrivatePasteFactory`, then reach in through `Services`).

```csharp
[Fact] public async Task FirstStart_CreatesOwnerNamedOwner_WithPpPassword()
// users.GetOwnerAsync().Name == "owner"; VerifyAsync(PrivatePasteFactory.Password, owner.PasswordHash) == Valid
[Fact] public async Task Upgrade_ExistingDevicesAndClips_BelongToOwner_TokenStillWorks()
// Build a DB in the old shape: start a factory whose bootstrap is skipped, or insert a device row with user_id NULL
// and a clip with source_user_id NULL, then run EnsureOwnerAsync. The old token still gets 200 on GET /api/clips,
// and both rows now carry the owner's id.
[Fact] public async Task Restart_SamePpPassword_KeepsPasswordSetOnWeb()
// SetPasswordAsync(owner, Hash("new-web-pass")), EnsureOwnerAsync() again: "new-web-pass" is Valid, PP_PASSWORD is Invalid
[Fact] public async Task Restart_ChangedPpPassword_ResetsOwnerPassword_KeepsStamp()
// New factory on the same DataDir with PP_PASSWORD "recovered-pass": owner verifies "recovered-pass"; SecurityStamp unchanged
[Fact] public async Task CreateAsync_NameTakenIgnoringCase_Throws()
```

  To test restarts, add `PrivatePasteFactory(IReadOnlyDictionary<string,string>? settings = null, string? dataDir = null)` so a second factory can reuse the directory (don't delete it when you didn't create it).

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter FullyQualifiedName~OwnerBootstrapTests`. Expected: FAIL or compile errors.
- [ ] **Step 3: Implement** the schema, migration, `UserRepository`, `OwnerBootstrap` and their wiring in `Program.cs`. Store timestamps with the repository's existing `Format`/`Parse` helpers. Security stamps are `Guid.NewGuid().ToString("N")`.
- [ ] **Step 4: Run all tests and confirm they pass.** Run `dotnet test`. Existing tests may still fail where they sign in; Task 3 updates the sign-in helper. If they fail, finish Task 3 before committing.
- [ ] **Step 5: Commit** with `feat(server): people table and owner bootstrap from PP_PASSWORD`.

### Task 3: Sign in by name (API, web, sessions)

**Files:**
- Modify: `src/PrivatePaste.Protocol/Dtos.cs`. Give `LoginRequest` a `public string? Username { get; init; }` property. Keep the positional constructor so old call sites still compile.
- Create: `src/PrivatePaste.Server/Auth/SignInVerifier.cs`
- Modify: `src/PrivatePaste.Server/Auth/DeviceTokenAuthHandler.cs` (claims), `src/PrivatePaste.Server/Auth/DeviceRepository.cs`, `src/PrivatePaste.Server/Api/AuthEndpoints.cs`
- Modify: `src/PrivatePaste.Server/Pages/Login.cshtml` and `Login.cshtml.cs`. Add a `Name` field above Password with `autocomplete="username"`. The error reads "Wrong name or password.".
- Create: `src/PrivatePaste.Server/Auth/WebSessionValidator.cs`. Wire it into `Program.cs` as `o.Events.OnValidatePrincipal`.
- Modify: `tests/PrivatePaste.Tests/PrivatePasteFactory.cs`. Change `LoginDeviceAsync(string deviceName = "PC-test", string platform = "windows", string username = "owner", string password = Password)`, and add `Task<HttpClient> LoginWebAsync(string username = "owner", string password = Password)`, which posts the login form and keeps the cookie and antiforgery token. Reuse whatever `Web/` tests already do.
- Test: `tests/PrivatePaste.Tests/Api/AuthApiTests.cs` (extend) and `tests/PrivatePaste.Tests/Web/WebSessionTests.cs` (new)

**Interfaces:**
- Consumes: `UserRepository` (Task 2) and `PasswordVerifier` (Task 1).
- Produces:
  - `UserClaims` (in `DeviceTokenAuthHandler.cs`, next to `DeviceClaims`):
    - constants `UserId = "uid"`, `IsOwner = "owner"` (value `"true"` when set), `Stamp = "stamp"`
    - extensions `Guid? UserId(this ClaimsPrincipal)` and `bool IsOwner(this ClaimsPrincipal)`
  - `SignInVerifier(UserRepository users, PasswordVerifier verifier)` with `Task<(PasswordCheck Check, UserRecord? User)> VerifyAsync(string? name, string? password, CancellationToken ct)`.
  - `DeviceRepository`:
    - `CreateAsync(string name, string platform, Guid userId)`
    - `FindByTokenAsync` returns `AuthenticatedDevice?` (`public sealed record AuthenticatedDevice(DeviceDto Device, Guid UserId, bool IsOwner)`). It joins `users` and only matches people who are not removed.
  - The device-token principal and the cookie principal both carry `uid` and `owner`. The cookie also carries `stamp`.
  - `WebSessionValidator.ValidateAsync(CookieValidatePrincipalContext ctx)`:
    - **Cookie without `uid` but with `NameIdentifier == "web-owner"`:** replace it with an owner principal (`ctx.ReplacePrincipal`, `ctx.ShouldRenew = true`).
    - **Person missing or removed, or stamp mismatch:** `ctx.RejectPrincipal()`, then sign out of the cookie scheme.

- [ ] **Step 1: Write the failing tests**

```csharp
// AuthApiTests
[Fact] public async Task Login_WithOwnerName_ReturnsToken()                    // username "owner", PP password → 200
[Fact] public async Task Login_NameIsCaseAndSpaceInsensitive()                 // "  OWNER " → 200
[Fact] public async Task Login_NoUsername_400_UpdateMessage()                 // problem detail == "Update PrivatePaste to sign in."
[Fact] public async Task Login_UnknownName_401_SameMessageAsWrongPassword()   // detail == "Wrong name or password." for both
[Fact] public async Task Login_DeviceBelongsToSignedInPerson()                // devices row user_id == owner id
// WebSessionTests
[Fact] public async Task WebLogin_WithName_ReachesHistory()
[Fact] public async Task WebLogin_WrongPassword_ShowsWrongNameOrPassword()
[Fact] public async Task LegacyWebOwnerCookie_StillSignedInAsOwner()
// Issue a cookie whose principal has only NameIdentifier "web-owner", Name "Web" and Platform "web" (sign in through a
// test-only endpoint or the cookie handler directly). GET / returns 200, and a following owner-only call
// (POST /api/history/clear) succeeds.
[Fact] public async Task CookieOfRemovedPerson_RedirectsToLogin()
// Sign in on the web as a person added through UserRepository + PasswordVerifier, call MarkRemovedAsync, then GET /
// redirects to /login.
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter "FullyQualifiedName~AuthApiTests|FullyQualifiedName~WebSessionTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** In `/api/auth/login`, a blank `Username` returns 400 with the update message *before* any hashing. Then call `SignInVerifier`: `Busy` returns 429 and `Invalid` returns 401 "Wrong name or password.". Then validate the device name as today. `LoginModel` gets `[BindProperty] public string? Name`. The `web-owner` claim isn't used for new sessions.
- [ ] **Step 4: Run all tests and confirm they pass.** Run `dotnet test`. Expected: PASS. The existing tests now sign in as `owner` through the factory defaults.
- [ ] **Step 5: Commit** with `feat(server): sign in with a name; sessions carry the person`.

### Task 4: Owner-only actions

**Files:**
- Modify: `src/PrivatePaste.Server/Auth/DeviceTokenAuthHandler.cs`. Rename `AuthSchemes.OwnerPolicy` (value `"Owner"`) to `SignedInPolicy` (value `"SignedIn"`), and add a new `OwnerPolicy = "Owner"` that requires the `owner` claim to be `"true"`. Update every existing use to `SignedInPolicy` (`Program.cs`, `Api/*.cs`).
- Modify: `src/PrivatePaste.Server/Program.cs` (register both policies)
- Modify: `src/PrivatePaste.Server/Api/ClipEndpoints.cs`. `POST history/clear` requires `OwnerPolicy`.
- Modify: `src/PrivatePaste.Server/Api/SettingsEndpoints.cs`. `PUT settings` requires `OwnerPolicy`; `GET` stays `SignedInPolicy`.
- Modify: `src/PrivatePaste.Server/Api/DeviceEndpoints.cs`. A regular person's `GET` lists only their own devices. Their `DELETE` of a device that isn't theirs returns 404.
- Modify: `src/PrivatePaste.Server/Auth/DeviceRepository.cs`:
  - `ListAsync(Guid? currentId, Guid? userId = null)` (null means all)
  - `RevokeAsync(Guid id, Guid? userId = null)` (null means any device)
  - `Task<IReadOnlyList<Guid>> RevokeAllForUserAsync(Guid userId)`
- Test: `tests/PrivatePaste.Tests/Api/OwnerOnlyTests.cs` (new)

**Interfaces:**
- Consumes: `UserClaims.IsOwner()` and `UserId()` (Task 3).
- Produces: `AuthSchemes.SignedInPolicy`, `AuthSchemes.OwnerPolicy`, the scoped `DeviceRepository` methods above, and the test helper `PrivatePasteFactory.AddPersonAsync(string name, string password = "person-pass-1")`. Until Task 6 the helper uses `UserRepository` + `PasswordVerifier` directly; Task 6 switches it to `PeopleService.AddAsync`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task RegularPerson_ClearHistory_403()
[Fact] public async Task RegularPerson_PutSettings_403()
[Fact] public async Task RegularPerson_GetSettings_200()
[Fact] public async Task RegularPerson_ListDevices_OnlyOwn()               // owner has 1 device, Ana 1: Ana sees 1, owner sees 2
[Fact] public async Task RegularPerson_RevokeOthersDevice_404_DeviceStillWorks()
[Fact] public async Task RegularPerson_RevokeOwnDevice_204()
[Fact] public async Task RegularPerson_PinDeleteRestore_Allowed()         // POST pin, DELETE clip, POST restore → 2xx
[Fact] public async Task Owner_ClearHistory_200()
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter FullyQualifiedName~OwnerOnlyTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all tests and confirm they pass.** Run `dotnet test`. Expected: PASS.
- [ ] **Step 5: Commit** with `feat(server): owner-only clear, settings and other people's devices`.

### Task 5: Clips record who copied them

**Files:**
- Modify: `src/PrivatePaste.Protocol/Dtos.cs`. Add to `ClipDto`: `public string? SourceUserName { get; init; }`, with an XML doc saying it's null while the server has one person, and null for unknown.
- Modify: `src/PrivatePaste.Server/Clips/ClipService.cs`. `DeviceIdentity` gains `Guid? UserId` as its last positional parameter. Pass it into `NewClip` and `BumpAsync`. Add `Task ResendAsync(Guid? userId)`.
- Modify: `src/PrivatePaste.Server/Auth/DeviceTokenAuthHandler.cs`. `ToDeviceIdentity()` fills `UserId` from the `uid` claim.
- Modify: `src/PrivatePaste.Server/Storage/ClipRepository.cs`:
  - `NewClip` gains `Guid? SourceUserId`.
  - `BumpAsync(Guid id, Guid? deviceId, string deviceName, string? devicePlatform, Guid? userId, DateTimeOffset at)`. Both the insert and the bump write `source_user_id`.
  - Replace every `FROM clips c` (4 places) with a shared `const string From = "clips c LEFT JOIN users u ON u.id = c.source_user_id"`.
  - Append to `Columns`: `CASE WHEN (SELECT count(*) FROM users WHERE removed_at IS NULL) > 1 THEN u.name END`, read into `SourceUserName`.
  - Add `Task<IReadOnlyList<ClipDto>> ResequenceAsync(Guid? userId)`: in one transaction and in `seq` order, give each matching clip (all clips when null) a new `seq` without touching `last_copied_at`, and return them.
- Test: `tests/PrivatePaste.Tests/Api/ClipAttributionTests.cs` (new)

**Interfaces:**
- Consumes: `AddPersonAsync` and `LoginDeviceAsync(..., username)` (Tasks 3–4).
- Produces: `ClipDto.SourceUserName` and `ClipService.ResendAsync(Guid? userId)`. `ResendAsync` broadcasts each clip that `ResequenceAsync` returns with `ClipChangedAsync(clip, live: false)`. If a socket's outbox (64) overflows, the hub drops it and that client catches up by `seq` on reconnect, which is the intended fallback.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task SinglePerson_SourceUserNameNull()                     // owner uploads; GET clip → SourceUserName null
[Fact] public async Task TwoPeople_ClipShowsWhoCopiedIt()                      // after AddPersonAsync("Ana"), Ana uploads → "Ana"
[Fact] public async Task Recopy_ByOtherPerson_UpdatesName()                   // owner uploads text, Ana uploads same text → "Ana"
[Fact] public async Task WebCopy_RecordsWebPerson()                           // web session restore → SourceUserName == "owner" (2 people exist)
[Fact] public async Task Resequence_NewSeqs_LastCopiedAtUnchanged_NotLive()  // ResendAsync(null): sync after old latestSeq returns all clips, same LastCopiedAt
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter FullyQualifiedName~ClipAttributionTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Also update `docs/api.md`: document `sourceUserName` on clips, the login `username` field, and the 400 and 401 sign-in responses.
- [ ] **Step 4: Run all tests and confirm they pass.** Run `dotnet test`. Expected: PASS.
- [ ] **Step 5: Commit** with `feat: clips carry the name of the person who copied them`.

### Task 6: Managing people

**Files:**
- Create: `src/PrivatePaste.Server/Auth/PeopleService.cs`
- Modify: `src/PrivatePaste.Server/Program.cs` (register it)
- Modify: `tests/PrivatePaste.Tests/PrivatePasteFactory.cs`. `AddPersonAsync` goes through `PeopleService.AddAsync`.
- Test: `tests/PrivatePaste.Tests/Auth/PeopleServiceTests.cs` (new)

**Interfaces:**
- Consumes: `UserRepository` (Task 2), `PasswordVerifier` (Task 1), `DeviceRepository.RevokeAllForUserAsync` (Task 4), `ConnectionHub.CloseDeviceAsync`, `ClipService.ResendAsync` (Task 5).
- Produces:
  - `public sealed record PeopleResult(bool Ok, string? Error)`. `Error` holds the exact copy from Global Constraints.
  - `PeopleService` methods:
    - `Task<PeopleResult> AddAsync(string? name, string? password)`. When the current count goes from 1 to 2, call `ResendAsync(null)`.
    - `Task<PeopleResult> RenameAsync(Guid id, string? name)`. Calls `ResendAsync(id)` when the name actually changed and there is more than one current person.
    - `Task<PeopleResult> ResetPasswordAsync(Guid id, string? newPassword)`. New security stamp, revokes all their devices and closes their sockets. Refused for the owner's own id with "Use Change password in Settings.".
    - `Task<PeopleResult> RemoveAsync(Guid id)`. `MarkRemovedAsync`, revokes their devices, closes sockets. Refused for the owner. When the count goes from 2 to 1, call `ResendAsync(null)`.
    - `Task<PeopleResult> ChangeOwnPasswordAsync(Guid id, string? current, string? newPassword, string? repeat)`. Verifies `current`, keeps the stamp and devices, and returns "Current password is wrong." / "Passwords don't match." when those checks fail.
  - A gate that returns `Busy` maps to the error "Too many attempts. Wait a minute and try again."

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task AddPerson_CanSignIn()
[Fact] public async Task AddPerson_NameDifferingOnlyInCaseOrSpaces_IsTaken()      // "Ana" then "  ANA " → Error "That name is already taken."
[Fact] public async Task AddPerson_ShortPassword_Rejected()                        // "short" → "Password must be at least 8 characters."
[Fact] public async Task AddPerson_NameOf33Chars_Rejected()
[Fact] public async Task RemovedName_CanBeReused()
[Fact] public async Task RemovePerson_TokensRejected()                             // their device token → 401 on GET /api/clips
[Fact] public async Task RemovePerson_ClosesTheirOpenWebSocket()                   // connect /ws with Ana's token; RemoveAsync; socket receives close
[Fact] public async Task RemovePerson_PastClipsKeepTheirName()                     // with 3 people, remove Ana → her clip still "Ana"
[Fact] public async Task RemoveOwner_Refused()
[Fact] public async Task ResetPassword_SignsOutTheirDevices_NewPasswordWorks()
[Fact] public async Task ChangeOwnPassword_KeepsDevices()
[Fact] public async Task ChangeOwnPassword_WrongCurrent_Rejected()
[Fact] public async Task Rename_ResendsTheirClips_WithNewName()                    // sync after old latestSeq contains Ana's clip renamed
[Fact] public async Task SecondPersonAdded_ResendsAllClips_WithNames()             // owner clip from before now syncs with "owner"
[Fact] public async Task BackToOnePerson_ResendsAllClips_WithoutNames()
```

  For the WebSocket test, reuse the connect helper in `tests/PrivatePaste.Tests/Realtime/`.

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter FullyQualifiedName~PeopleServiceTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all tests and confirm they pass.** Run `dotnet test`. Expected: PASS.
- [ ] **Step 5: Commit** with `feat(server): add, rename, reset and remove people`.

### Task 7: Web UI — People page, Settings, name chip

**Files:**
- Create: `src/PrivatePaste.Server/Pages/People.cshtml` and `People.cshtml.cs`. Replace `Devices.cshtml(.cs)` and delete them. Map `GET /devices` to a permanent redirect to `/people`.
- Modify: `src/PrivatePaste.Server/Pages/Shared/_Layout.cshtml`. The nav reads History · People · Settings, and the sign-out form posts to `/People` handler `SignOut`.
- Modify: `src/PrivatePaste.Server/Pages/Settings.cshtml` and `.cshtml.cs`:
  - Add a "Change password" form (current, new, repeat) for everyone, showing "Password changed." on success.
  - Render the auto-delete and Clear history sections only for the owner.
  - The `AutoDelete` and `Clear` handlers return 403 for regular people.
- Modify: `src/PrivatePaste.Server/Pages/Index.cshtml` (and `site.js`, if it renders rows on the client). In the meta line, before the platform icon, add `<span class="person-chip">@clip.SourceUserName</span>` when the name isn't null.
- Modify: `src/PrivatePaste.Server/wwwroot/site.css`. Style `.person-chip` per the chosen mockup: 11px, semibold, pill radius, an accent-tinted background from the existing color tokens in both themes. Add `.person-card` styles for the People page.
- Test: `tests/PrivatePaste.Tests/Web/PeoplePageTests.cs` (new), plus the existing web render/screenshot tests extended.

**Interfaces:**
- Consumes: `PeopleService` (Task 6), `DeviceRepository.ListAsync(currentId, userId)` and `RevokeAsync(id, userId)` (Task 4), and `UserClaims` (Task 3).
- Produces: People page handlers `OnPostAdd(name, password)`, `OnPostRename(id, name)`, `OnPostResetPassword(id, password)`, `OnPostRemove(id)`, `OnPostSignOutDevice(id)` and `OnPostSignOut()`. Owner-only handlers return 403 for regular people. `OnPostSignOutDevice` scopes to the person's own devices for regular people.

People page layout (mockup A):
- One card per person: their name chip, the role label "owner" on the owner's card, and their devices listed underneath (name · platform · seen time) with a "Sign out" button per device.
- The owner sees every card. Each card has "Rename". Cards other than the owner's also have "Reset password" and "Remove", each with a `data-confirm`.
- Below the cards, the owner gets an add form with Name, Password and an "Add person" button.
- A regular person sees only their own card, with no actions apart from Sign out on their devices.
- Errors appear above the form they belong to, using the page's existing error style.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task People_Owner_SeesEveryoneAndAddForm()
[Fact] public async Task People_RegularPerson_SeesOnlyOwnCard_NoAddForm()
[Fact] public async Task People_RegularPerson_PostRemove_403()
[Fact] public async Task People_AddDuplicateName_ShowsTakenError()
[Fact] public async Task Devices_RedirectsToPeople()
[Fact] public async Task Settings_RegularPerson_NoClearOrAutoDelete_HasChangePassword()
[Fact] public async Task Settings_ChangePassword_ShowsPasswordChanged()
[Fact] public async Task History_TwoPeople_RowShowsPersonChip()   // HTML contains class="person-chip">Ana<
[Fact] public async Task History_OnePerson_NoPersonChip()
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter "FullyQualifiedName~PeoplePageTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Reference CSS and JS with `asp-append-version`, as today.
- [ ] **Step 4: Run all tests and confirm they pass.** Run `dotnet test`. Expected: PASS.
- [ ] **Step 5: Check by eye.** Run a local server (`PP_PASSWORD=dev`, throwaway `PP_DATA_DIR`) and add a second person. Check History, People and Settings in light and dark themes, as both the owner and the regular person.
- [ ] **Step 6: Commit** with `feat(web): People page, change password, person chip on history rows`.

### Task 8: Desktop app

**Files:**
- Modify: `src/PrivatePaste.Client.Core/Api/PrivatePasteApi.cs`: `LoginAsync(string username, string password, string deviceName, string platform, CancellationToken ct = default)`, which sends `new LoginRequest(password, deviceName, platform) { Username = username }`.
- Modify: `src/PrivatePaste.Client.Core/Settings/ClientSettings.cs`: add `string Username = ""` as the last positional parameter (missing in old files means empty).
- Modify: `src/PrivatePaste.Desktop/DesktopHost.cs`. `SignInAsync(string serverUrl, string username, string password, string deviceName)` saves `Username` into settings on success.
- Modify: `src/PrivatePaste.Desktop/ViewModels/SignInViewModel.cs` and `Views/SignInWindow.axaml`. Add a `Username` property, prefilled from settings, as a "Name" field above Password. `CanSignIn` requires it. `WrongPasswordMessage = "Wrong name or password."`. An `ApiException` with status 400 maps to a new `SignInResult.UpdateNeeded`, which shows the server's detail. That case only arises if the server is newer and rejects the request.
- Modify: `src/PrivatePaste.Desktop/ViewModels/PopupViewModel.cs`. `PopupItem` gains `string? Person` (from `clip.SourceUserName`). `Views/PopupWindow.axaml` shows a pill `Border` holding `Person` before the meta text, visible only when `Person` isn't null, using theme resources from `Theme/NightVault.axaml` (add a `PpChipBackground` brush for both themes).
- Modify: `src/PrivatePaste.Desktop/Tray/ClipToast.cs`. When a person is present, the title is `From {person} · {device}`.
- Test:
  - `tests/PrivatePaste.Tests/Client/SignInViewModelTests.cs`, `ApiClientTests.cs`, `PopupViewModelTests.cs`, `SettingsTests.cs` (extend)
  - a `ClipToast` test, wherever the toast is tested today
  - `DesktopRenderTests.cs` (add a two-person popup render in both themes)

**Interfaces:**
- Consumes: `LoginRequest.Username` and `ClipDto.SourceUserName` (Tasks 3, 5).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Login_SendsUsername()                  // ApiClientTests: request JSON has "username":"Ana"
[Fact] public void SignIn_RequiresName()                        // CanSignIn false with empty Username
[Fact] public void SignIn_PrefillsLastName()
[Fact] public void SignIn_WrongPassword_Message()               // "Wrong name or password."
[Fact] public void Settings_OldFileWithoutUsername_LoadsEmpty()
[Fact] public void PopupItem_PersonFromSourceUserName()
[Fact] public void Toast_WithPerson_Title()                     // "From Ana · Work laptop"
[Fact] public void Toast_WithoutPerson_TitleUnchanged()         // "From Work laptop"
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter "FullyQualifiedName~Client"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Never focus or activate the popup (`CLAUDE.md`: nothing in it may be focusable). The chip is a plain `Border` + `TextBlock`.
- [ ] **Step 4: Run all tests and confirm they pass.** Run `dotnet test`, then render screenshots with `PP_SCREENSHOT_DIR` set and `DesktopRenderTests`. Check the sign-in window and a two-person popup in both themes.
- [ ] **Step 5: Commit** with `feat(desktop): sign in with a name; person chip in the popup and toast`.

### Task 9: Android app

**Files:**
- Modify: `android/app/src/main/java/xyz/ficdev/privatepaste/core/model/Protocol.kt`. `ClipDto` gains `val sourceUserName: String? = null`.
- Modify: `.../api/PrivatePasteApi.kt`. `login(username: String, password: String, deviceName: String)` puts `"username"`. A 400 maps to a failure that carries the server's detail.
- Modify: `.../sync/SessionManager.kt`. `signIn(url, username, password, deviceName)` saves the last username.
- Modify: `.../storage/SettingsStore.kt`. Add `KEY_LAST_USERNAME` and a `lastUsername` property.
- Modify: `.../ui/signin/SignInViewModel.kt` and `SignInScreen.kt`. Add a "Name" field above Password, prefilled and required.
- Modify: `.../ui/history/ClipRow.kt`. Show a pill chip with `sourceUserName` before the platform icon when it isn't null.
- Modify: `.../service/Notifications.kt`. With a person: "New clip from {person} · {device}".
- Modify: `android/app/src/main/res/values/strings.xml`:
  - `signin_name` = "Name"
  - `signin_wrong_password` = "Wrong name or password."
  - `notification_new_clip_from_person` = "New clip from %1$s · %2$s"
- Test: `PrivatePasteApiTest.kt`, `SessionManagerTest.kt`, `SignInViewModelTest.kt`, `HistoryScreenTest.kt`, `SettingsStoreTest.kt`, `ScreenshotTest.kt` (two-person history and sign-in in both themes), and the `core/model` decoding test.

**Interfaces:**
- Consumes: the same wire fields as Task 8.

- [ ] **Step 1: Write the failing tests**

```kotlin
@Test fun login_sendsUsername()                         // request body has "username":"Ana"
@Test fun clipDto_withoutSourceUserName_decodes()       // old-server JSON → sourceUserName == null
@Test fun clipDto_withSourceUserName_decodes()
@Test fun signIn_requiresName()
@Test fun signIn_prefillsLastName()
@Test fun clipRow_showsPersonChip_whenNamePresent()     // HistoryScreenTest: node with text "Ana" exists
@Test fun clipRow_noChip_whenNameNull()
```

- [ ] **Step 2: Run them and confirm they fail.** In `android/`, with `JAVA_HOME` set to `C:\Program Files\Microsoft\jdk-21.0.13.21-hotspot`, run `./gradlew testDebugUnitTest`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests and the end-to-end check.**
  - Run `./gradlew testDebugUnitTest`. Expected: PASS.
  - Update `LocalServerE2eTest` to sign in as `owner`, start a local server (`PP_PASSWORD=dev`), and run `PP_E2E_URL=http://localhost:5202 ./gradlew testDebugUnitTest --tests '*LocalServerE2eTest*'`. Expected: PASS.
  - Look at the screenshots from `ScreenshotTest` with `PP_SCREENSHOT_DIR` set.
- [ ] **Step 5: Commit** with `feat(android): sign in with a name; person chip and notification`.

### Task 10: Docs and README images

**Files:**
- Modify: `README.md`:
  - feature list: "multiple people, one shared history"
  - setup: `PP_PASSWORD` is the owner's password, and the owner signs in as `owner`
  - an upgrade note: on a server already shared by several people, the owner signs other people's devices out on the People page and each person signs in again with their own account
- Modify: `docs/self-hosting.md`:
  - `PP_PASSWORD` meaning: creates the owner on first start; changing it later resets the owner's password, which is the recovery path
  - managing people
  - the upgrade note
  - back up `/data` before upgrading
- Modify: `docs/desktop.md` and `docs/android.md` (the sign-in Name field), and `docs/api.md` if Task 5 left anything out.
- Modify: `scripts/readme/demo.json` and `scripts/readme/capture.mjs`. Seed a second person and clips from both, and sign in with name `owner` and password `dev`. Check the "Text to keep in step" list in `scripts/readme/README.md` and update anything else it names.
- Run: `powershell -File scripts/readme/build.ps1`. Commit both `docs/images/hero-*.png`.

- [ ] **Step 1: Update the docs.** Make each fact match the spec.
- [ ] **Step 2: Rebuild and inspect the hero images.** Run `powershell -File scripts/readme/build.ps1`. Expected: both `docs/images/hero-*.png` show name chips on rows from two people. If the build can't run (no JDK or Node), say so in the summary.
- [ ] **Step 3: Run the full test suite.** Run `dotnet test`, then `./gradlew testDebugUnitTest` in `android/`. Expected: all PASS.
- [ ] **Step 4: Commit** with `docs: multiple people; README images with person chips`.
- [ ] **Step 5: Before merging,** remind the owner of two things:
  - Back up `/data` before pushing `main`; the schema migration runs on deploy.
  - Release `server-v*` first, then `desktop-v*` and `android-v*`, and update the image tag in `deploy/dokploy/privatepaste/` with the server release.
