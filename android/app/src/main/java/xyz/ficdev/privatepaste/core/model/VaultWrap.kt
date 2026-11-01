package xyz.ficdev.privatepaste.core.model

import kotlinx.serialization.Serializable

// The passphrase-wrapped vault key as the server stores it (docs/api.md). Base64 here is standard, with padding.

@Serializable
data class VaultKdf(
    val alg: String,
    val memoryKiB: Int,
    val iterations: Int,
    val parallelism: Int,
    val salt: String,
)

@Serializable
data class VaultWrap(
    val keyId: String,
    val kdf: VaultKdf,
    val nonce: String,
    val wrappedKey: String,
)
