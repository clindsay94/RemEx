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
            aeadInstance ?: AndroidKeysetManager.Builder()
                .withSharedPref(context.applicationContext, KEYSET_NAME, TINK_PREFS_FILE)
                .withKeyTemplate(AesGcmKeyManager.aes256GcmTemplate())
                .withMasterKeyUri(MASTER_KEY_URI)
                .build()
                .keysetHandle
                .getPrimitive(RegistryConfiguration.get(), Aead::class.java)
                .also { aeadInstance = it }
        }
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
