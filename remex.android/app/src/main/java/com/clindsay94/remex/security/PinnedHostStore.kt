package com.clindsay94.remex.security

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
        removePin(context, hostId)
        removeReconnectSecret(context, hostId)
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
