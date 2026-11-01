CREATE TABLE IF NOT EXISTS counters(name TEXT PRIMARY KEY, value INTEGER NOT NULL);

-- People. name_key is the trimmed, lowercased name; removed people keep their row so their clips keep a name.
CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL,
  is_owner INTEGER NOT NULL, password_hash TEXT NOT NULL, security_stamp TEXT NOT NULL, created_at TEXT NOT NULL,
  removed_at TEXT);

CREATE UNIQUE INDEX IF NOT EXISTS ux_users_name ON users(name_key) WHERE removed_at IS NULL;

CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, platform TEXT NOT NULL,
  token_hash TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL, last_seen_at TEXT, revoked_at TEXT, user_id TEXT);

CREATE TABLE IF NOT EXISTS clips(id TEXT PRIMARY KEY, seq INTEGER NOT NULL UNIQUE, kind TEXT NOT NULL, text TEXT,
  hash TEXT NOT NULL UNIQUE, width INTEGER, height INTEGER, size_bytes INTEGER NOT NULL,
  source_device_id TEXT, source_device_name TEXT NOT NULL, created_at TEXT NOT NULL, last_copied_at TEXT NOT NULL,
  source_device_platform TEXT, pinned_at TEXT, unpinned_at TEXT, source_user_id TEXT,
  payload TEXT, key_id TEXT, has_thumb INTEGER NOT NULL DEFAULT 0, blob_count INTEGER NOT NULL DEFAULT 0);
-- Columns added after the first release are also added to existing databases by PrivatePasteDb.Migrate.

CREATE INDEX IF NOT EXISTS ix_clips_order ON clips(last_copied_at DESC, seq DESC);

CREATE TABLE IF NOT EXISTS tombstones(clip_id TEXT PRIMARY KEY, seq INTEGER NOT NULL);

CREATE TABLE IF NOT EXISTS settings(name TEXT PRIMARY KEY, value TEXT NOT NULL);

-- The wrapped vault key (one row) and one-time device handovers; see docs/api.md.
CREATE TABLE IF NOT EXISTS vault(id INTEGER PRIMARY KEY CHECK (id = 1), wrap_json TEXT NOT NULL, key_id TEXT NOT NULL);

CREATE TABLE IF NOT EXISTS handovers(id TEXT PRIMARY KEY, blob BLOB NOT NULL, expires_at TEXT NOT NULL);
