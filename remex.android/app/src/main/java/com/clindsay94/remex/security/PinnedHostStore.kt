package com.clindsay94.remex.security

import com.clindsay94.remex.RemexCoreClient
import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.google.crypto.tink.Aead
import com.google.crypto.tink.RegistryConfiguration
import com.google.crypto.tink.aead.AeadConfig
import com.google.crypto.tink.aead.AesGcmKeyManager
import com.google.crypto.tink.integration.android.AndroidKeysetManager
import java.util.Base64
import kotlinx.coroutines.flow.first

// One DataStore instance per app (backed by applicationContext).
private val Context.pinnedHostDataStore: DataStore<Preferences> by
        preferencesDataStore(name = "remex_pinned_hosts")

// Records which keys belong to the same PC, so forgetting can find them all even after the pin
// they would have been derived from is gone (RemEx-uxem).
//
// NOT ENCRYPTED, and be precise about why that is acceptable: these values are not secrets. The
// hostId and address are already plaintext KEYS in the pin store, and the SPKI hash is a plaintext
// key in the reconnect-secret store. What is new is the GROUPING - this file is the only place that
// links hostId to address to fingerprint in one record - so it is excluded from cloud backup and
// device transfer alongside the pin store, in backup_rules.xml and data_extraction_rules.xml.
private val Context.hostAliasDataStore: DataStore<Preferences> by
        preferencesDataStore(name = "remex_host_aliases")

// Separate DataStore for PAIR-1 reconnect secrets, kept distinct from the SPKI store so listPaired()
// over the pinned-host store is never polluted by secret entries. (RemEx-xuo)
private val Context.reconnectSecretDataStore: DataStore<Preferences> by
        preferencesDataStore(name = "remex_reconnect_secrets")

/**
 * Encrypted storage for paired host SPKI hashes.
 *
 * Each value is encrypted via Tink AES-256-GCM AEAD before being written to DataStore. The host
 * identifier is used as associated data, binding each ciphertext to its key so that a moved/swapped
 * entry fails decryption.
 *
 * The Tink keyset is stored in a plain SharedPreferences file and is itself encrypted by an Android
 * Keystore-backed key — no deprecated EncryptedSharedPreferences or MasterKey APIs are used.
 */
object PinnedHostStore {

    private const val KEYSET_NAME = "remex_pinned_host_keyset"
    private const val TINK_PREFS_FILE = "remex_tink_prefs"
    private const val MASTER_KEY_URI = "android-keystore://remex_pinned_host_key"

    @Volatile
    private var aeadInstance: Aead? = null

    init {
        AeadConfig.register()
    }

    private fun aead(context: Context): Aead {
        return aeadInstance ?: synchronized(this) {
            aeadInstance ?: try {
                buildAead(context)
            } catch (e: Exception) {
                // If Tink or the Android Keystore is corrupted (e.g., app data cleared but Keystore remained,
                // or lock screen changes invalidated the key), we must clear the corrupted state and retry.
                android.util.Log.e("PinnedHostStore", "Failed to initialize Tink AEAD. Clearing corrupted keyset and retrying.", e)

                // Clear the SharedPreferences containing the Tink keyset
                context.applicationContext.getSharedPreferences(TINK_PREFS_FILE, Context.MODE_PRIVATE)
                    .edit()
                    .clear()
                    .apply()

                // Clear the DataStore since its encrypted contents are now unreadable.
                // We use runBlocking here because we are inside a synchronized block that must return an Aead synchronously.
                // It's safe because DataStore edit runs quickly and this is an exceptional recovery path.
                kotlinx.coroutines.runBlocking {
                    context.applicationContext.pinnedHostDataStore.edit { it.clear() }
                }

                // Retry initialization
                buildAead(context)
            }.also { aeadInstance = it }
        }
    }

    private fun buildAead(context: Context): Aead {
        return AndroidKeysetManager.Builder()
            .withSharedPref(context.applicationContext, KEYSET_NAME, TINK_PREFS_FILE)
            .withKeyTemplate(AesGcmKeyManager.aes256GcmTemplate())
            .withMasterKeyUri(MASTER_KEY_URI)
            .build()
            .keysetHandle
            .getPrimitive(RegistryConfiguration.get(), Aead::class.java)
    }

    private fun prefKey(hostId: String) = stringPreferencesKey(hostId)

    suspend fun setPin(context: Context, hostId: String, spkiHash: String) {
        val cipher =
                aead(context)
                        .encrypt(
                                spkiHash.toByteArray(Charsets.UTF_8),
                                hostId.toByteArray(Charsets.UTF_8)
                        )
        val encoded = Base64.getEncoder().encodeToString(cipher)
        context.applicationContext.pinnedHostDataStore.edit { prefs ->
            prefs[prefKey(hostId)] = encoded
        }
    }

    suspend fun getPin(context: Context, hostId: String): String? {
        val prefs = context.applicationContext.pinnedHostDataStore.data.first()
        val encoded = prefs[prefKey(hostId)] ?: return null
        return try {
            val cipher = Base64.getDecoder().decode(encoded)
            val plain = aead(context).decrypt(cipher, hostId.toByteArray(Charsets.UTF_8))
            String(plain, Charsets.UTF_8)
        } catch (_: Exception) {
            // Corrupted or tampered entry — treat as unpaired.
            null
        }
    }

    suspend fun removePin(context: Context, hostId: String) {
        context.applicationContext.pinnedHostDataStore.edit { prefs ->
            prefs.remove(prefKey(hostId))
        }
    }

    /**
     * Persists the PAIR-1 reconnect secret for [hostId], encrypted with the same Tink AEAD as the
     * pinned SPKI hashes (host identifier bound as associated data). The native client supplies this
     * on connect to answer the host's proof-of-possession reconnect challenge; without it a paired
     * client is challenged and rejected as unverified. (RemEx-xuo)
     */
    suspend fun setReconnectSecret(context: Context, hostId: String, secret: String) {
        val cipher =
                aead(context)
                        .encrypt(
                                secret.toByteArray(Charsets.UTF_8),
                                hostId.toByteArray(Charsets.UTF_8)
                        )
        val encoded = Base64.getEncoder().encodeToString(cipher)
        context.applicationContext.reconnectSecretDataStore.edit { prefs ->
            prefs[prefKey(hostId)] = encoded
        }
    }

    suspend fun getReconnectSecret(context: Context, hostId: String): String? {
        val prefs = context.applicationContext.reconnectSecretDataStore.data.first()
        val encoded = prefs[prefKey(hostId)] ?: return null
        return try {
            val cipher = Base64.getDecoder().decode(encoded)
            val plain = aead(context).decrypt(cipher, hostId.toByteArray(Charsets.UTF_8))
            String(plain, Charsets.UTF_8)
        } catch (_: Exception) {
            // Corrupted or tampered entry — treat as no stored secret (forces re-pair).
            null
        }
    }

    /**
     * Records that [aliases] all identify the same PC, so [forgetHost] can clear every one of them
     * given any single alias.
     *
     * WHY THIS IS RECORDED RATHER THAN DERIVED (RemEx-uxem). Forgetting used to work out the alias
     * set by looking up the pin for the key it was handed and matching every other pin key with the
     * same hash. That works only while the pin is intact - and the population that most needs a clean
     * forget is exactly the one whose pin is already half-gone, from the earlier forget that removed
     * one key and left the rest. Deriving after the fact fails precisely when it matters.
     *
     * Written under EVERY alias, so any one of them resolves the whole set.
     */
    suspend fun recordAliases(context: Context, aliases: List<String>) {
        val fresh = aliases.filter { it.isNotBlank() }.distinct()
        if (fresh.size < 2) return

        // MERGE, do not overwrite. The same PC gets re-paired at new addresses over time - LAN then
        // Tailscale, or after a DHCP change - and each pairing knows only the address it just used.
        // Overwriting would drop the earlier ones from the record while their pins remain on disk.
        val existing = context.applicationContext.hostAliasDataStore.data.first()
        val merged = LinkedHashSet<String>()
        for (alias in fresh) {
            merged += alias
            existing[prefKey(alias)]?.split(ALIAS_SEPARATOR)?.forEach { if (it.isNotBlank()) merged += it }
        }

        val joined = merged.joinToString(ALIAS_SEPARATOR)
        context.applicationContext.hostAliasDataStore.edit { prefs ->
            for (alias in merged) prefs[prefKey(alias)] = joined
        }
    }

    private const val ALIAS_SEPARATOR = "\u001F"

    private suspend fun recordedAliases(context: Context, key: String): List<String>? {
        val prefs = context.applicationContext.hostAliasDataStore.data.first()
        return prefs[prefKey(key)]?.split(ALIAS_SEPARATOR)?.filter { it.isNotBlank() }
    }

    /**
     * Forgets [hostId] completely: the pinned SPKI hash AND the reconnect secret.
     *
     * USE THIS, NOT [removePin] ALONE (RemEx-j9ei). The two live in separate DataStores, so clearing
     * the pin leaves the PAIR-1 secret behind and the next connect can still fail the
     * proof-of-possession challenge - the phone shows "paired", the host issues a reconnect
     * challenge it cannot answer, and the user is stuck with no way forward from the UI. Both
     * callers that cleared a pin had that bug, and [removeReconnectSecret] had NO caller at all: it
     * was written for this and never wired up.
     *
     * Being one function is the point. Two calls that must always happen together is an invariant
     * nobody can see at a call site; one call is an invariant nobody can break.
     */
    suspend fun forgetHost(context: Context, hostId: String) {
        // EVERY KEY PAIRING WROTE UNDER, not just the one the caller happens to hold (RemEx-1phe).
        // Pairing stores the pin under both the mDNS hostId and the typed host, and the secret under
        // those PLUS the SPKI hash - so clearing one key left the others behind, and the two paths
        // that read them are exactly the ones that reproduce "paired but will not connect":
        // self-healing discovery trusts a host if EITHER pin key is set, and the reconnect-secret
        // read PREFERS the spkiHash key.
        //
        // The aliases are DISCOVERED rather than demanded from the caller, which holds only one of
        // them: every pin key mapping to the same hash is the same PC by definition.
        // BOTH sources, unioned - never one instead of the other. The record survives a
        // half-cleared pin, which derivation cannot; derivation finds addresses added after the
        // record was written, which the record may not. Review caught the first version using the
        // record INSTEAD of derivation: re-pairing the same PC at a second address (LAN then
        // Tailscale, or a DHCP change) rewrote the record without the old address while its pin was
        // still on disk, so forget left it behind - reintroducing the exact state RemEx-1phe fixed.
        // Union is strictly more clearing, so it stays direction-safe.
        val paired = listPaired(context)
        val hash = paired[hostId]
        val derived =
                if (hash == null) setOf(hostId)
                else paired.filterValues { it == hash }.keys + hostId
        val aliases = recordedAliases(context, hostId).orEmpty().toSet() + derived

        for (alias in aliases) {
            removePin(context, alias)
            removeReconnectSecret(context, alias)
            // The native in-memory pin outlives the DataStore one and the connect path falls back to
            // it, so without this the forget does not take effect until the process restarts.
            RemexCoreClient.ClearPinnedHostHash(alias)
        }

        // The secret is also keyed by the hash itself. It is in the recorded set, but not in a
        // derived one - a hash is a pin VALUE, never a pin key.
        if (hash != null) removeReconnectSecret(context, hash)

        // And the linkage itself, or the next forget resurrects a set of keys that no longer exist.
        context.applicationContext.hostAliasDataStore.edit { prefs ->
            for (alias in aliases) prefs.remove(prefKey(alias))
            if (hash != null) prefs.remove(prefKey(hash))
        }
    }

    suspend fun removeReconnectSecret(context: Context, hostId: String) {
        context.applicationContext.reconnectSecretDataStore.edit { prefs ->
            prefs.remove(prefKey(hostId))
        }
    }

    suspend fun listPaired(context: Context): Map<String, String> {
        val aead = aead(context)
        val prefs = context.applicationContext.pinnedHostDataStore.data.first()
        return buildMap {
            for ((key, value) in prefs.asMap()) {
                val hostId = key.name
                val encoded = value as? String ?: continue
                try {
                    val plain =
                            aead.decrypt(
                                    Base64.getDecoder().decode(encoded),
                                    hostId.toByteArray(Charsets.UTF_8)
                            )
                    put(hostId, String(plain, Charsets.UTF_8))
                } catch (_: Exception) {
                    // Skip corrupted entries.
                }
            }
        }
    }
}
