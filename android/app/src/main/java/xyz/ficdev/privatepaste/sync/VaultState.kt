package xyz.ficdev.privatepaste.sync

/** Where this phone stands with the server's encryption; only [UNLOCKED] syncs and shows the history. */
enum class VaultState {
    /** Not asked yet, or the server couldn't be reached. */
    UNKNOWN,

    /** The server has no `/api/server-info`: it predates encryption. */
    SERVER_TOO_OLD,

    /** The server answered 426: this app is too old for it. */
    UPDATE_REQUIRED,

    /** No vault yet, and this account is the owner's. */
    NEEDS_OWNER_SETUP,

    /** No vault yet, and only the owner can set it up. */
    WAITING_FOR_OWNER,

    /** A vault exists; this phone doesn't hold its key. */
    LOCKED,

    UNLOCKED,
}
