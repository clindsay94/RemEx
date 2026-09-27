package com.clindsay94.remex.routines

import android.annotation.SuppressLint
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
import java.security.GeneralSecurityException
import java.util.Base64
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

// The three routine stores (routines spec §6.8). Every one of them, and the keyset prefs file below,
// is excluded from cloud backup and device transfer in backup_rules.xml and data_extraction_rules.xml
// (T12, R-SEC-10); BackupRulesRoutineExclusionTest reads the names from here via
// [RoutineStoreNames] and fails if either XML file stops listing one.
internal val Context.routinesDataStore: DataStore<Preferences> by
    preferencesDataStore(name = RoutineStoreNames.ROUTINES)

// NFC tokens and the shortcut HMAC key (§6.8). Written by the S2 manual-surfaces slice; declared
// here because the keyset-loss recovery must clear it together with the other two.
internal val Context.routineSecretsDataStore: DataStore<Preferences> by
    preferencesDataStore(name = RoutineStoreNames.SECRETS)

internal val Context.routineHistoryDataStore: DataStore<Preferences> by
    preferencesDataStore(name = RoutineStoreNames.HISTORY)

/** The on-disk names the backup rules must exclude. */
object RoutineStoreNames {
    const val ROUTINES = "remex_routines"
    const val SECRETS = "remex_routine_secrets"
    const val HISTORY = "remex_routine_history"

    /** SharedPreferences file holding the routines' own Tink keyset (never PinnedHostStore's). */
    const val TINK_PREFS_FILE = "remex_routines_tink_prefs"

    /** Home presence's applied registration plan, the cheap "armed" flag (S3, §12). */
    const val PRESENCE_PREFS_FILE = "remex_routines_presence"

    val DATASTORES = listOf(ROUTINES, SECRETS, HISTORY)

    /** Every routine SharedPreferences file the backup rules must exclude. */
    val PREFS_FILES = listOf(TINK_PREFS_FILE, PRESENCE_PREFS_FILE)
}

internal class DataStoreRoutineKeyValueStore(private val store: DataStore<Preferences>) : RoutineKeyValueStore {
    override suspend fun get(key: String): String? = store.data.first()[stringPreferencesKey(key)]

    override suspend fun getAll(): Map<String, String> =
        store.data.first().asMap().entries.mapNotNull { (k, v) -> (v as? String)?.let { k.name to it } }.toMap()

    override suspend fun put(key: String, value: String) {
        store.edit { it[stringPreferencesKey(key)] = value }
    }

    override suspend fun remove(key: String) {
        store.edit { it.remove(stringPreferencesKey(key)) }
    }
}

/**
 * The routines' AEAD: a SEPARATE Tink keyset from the pairing store's (§6.8), so a problem with one
 * can never cost the other. Built exactly like `PinnedHostStore.buildAead`, which is not modified
 * (it is security-guarded, REGRESSION-GUARDS.md "Tink keyset recovery").
 *
 * **KEYSET LOSS IS RECOVERED AND REPORTED, NEVER SILENT.** When the keyset cannot be used, the
 * recovery clears it and all three routine stores (their ciphertext is unreadable under any new key)
 * and writes [KEY_LOSS_MARKER] into the keyset prefs file BEFORE rebuilding. The repository turns the
 * marker into the `store_reset` banner and history record, then clears it; if the process dies in
 * between, the marker is still there on the next start. PinnedHostStore's recovery has no such
 * marker because a lost pin re-pairs visibly; a lost routine would simply never fire again.
 */
internal class TinkRoutineCipherSource(context: Context) : RoutineCipherSource {
    private val appContext = context.applicationContext
    private val mutex = Mutex()

    @Volatile private var cipher: RoutineCipher? = null

    override suspend fun cipher(): RoutineCipher =
        withContext(Dispatchers.IO) {
            cipher ?: mutex.withLock {
                cipher ?: AeadCipher(buildOrRecover()).also { cipher = it }
            }
        }

    override suspend fun pendingKeyLossAtUnixMs(): Long? =
        withContext(Dispatchers.IO) {
            prefs().getLong(KEY_LOSS_MARKER, 0L).takeIf { it > 0L }
        }

    // commit() on IO: an apply() still pending at process death would report the same reset twice.
    @SuppressLint("ApplySharedPref")
    override suspend fun clearKeyLossMarker() {
        withContext(Dispatchers.IO) { prefs().edit().remove(KEY_LOSS_MARKER).commit() }
    }

    private fun prefs() = appContext.getSharedPreferences(RoutineStoreNames.TINK_PREFS_FILE, Context.MODE_PRIVATE)

    private suspend fun buildOrRecover(): Aead =
        try {
            build()
        } catch (e: Exception) {
            // Broad on purpose, like PinnedHostStore: a keystore failure surfaces as
            // GeneralSecurityException, IOException or a platform ProviderException (a
            // RuntimeException), and every one of them means the same thing here. build() does
            // not suspend, so this cannot swallow a cancellation.
            recover(e)
        }

    // commit(), not apply(): the marker must be on disk BEFORE the stores are cleared, or a process
    // death in between would lose the routines and the report that they were lost. This runs on IO.
    @SuppressLint("ApplySharedPref")
    private suspend fun recover(cause: Exception): Aead =
        // Once started, finish: a cancellation between the clears would leave ciphertext behind that
        // no surviving key opens (the RemEx-v3bd lesson in PinnedHostStore).
        withContext(NonCancellable) {
            RoutineLog.e("Routine keyset unusable; clearing the routine stores and starting a new keyset.", cause)
            // Only a keyset that existed can have lost anything: a first build that fails on a fresh
            // install must not greet the user with "your routines were reset".
            val hadKeyset = prefs().contains(KEYSET_NAME)
            prefs().edit().clear().apply { if (hadKeyset) putLong(KEY_LOSS_MARKER, System.currentTimeMillis()) }.commit()
            appContext.routinesDataStore.edit { it.clear() }
            appContext.routineSecretsDataStore.edit { it.clear() }
            appContext.routineHistoryDataStore.edit { it.clear() }
            build()
        }

    private fun build(): Aead =
        AndroidKeysetManager.Builder()
            .withSharedPref(appContext, KEYSET_NAME, RoutineStoreNames.TINK_PREFS_FILE)
            .withKeyTemplate(AesGcmKeyManager.aes256GcmTemplate())
            .withMasterKeyUri(MASTER_KEY_URI)
            .build()
            .keysetHandle
            .getPrimitive(RegistryConfiguration.get(), Aead::class.java)

    private class AeadCipher(private val aead: Aead) : RoutineCipher {
        override fun seal(plainText: String, associatedData: String): String =
            Base64.getEncoder().encodeToString(
                aead.encrypt(plainText.toByteArray(Charsets.UTF_8), associatedData.toByteArray(Charsets.UTF_8))
            )

        override fun open(sealed: String, associatedData: String): String? =
            try {
                String(
                    aead.decrypt(Base64.getDecoder().decode(sealed), associatedData.toByteArray(Charsets.UTF_8)),
                    Charsets.UTF_8,
                )
            } catch (_: GeneralSecurityException) {
                null
            } catch (_: IllegalArgumentException) {
                null
            }
    }

    companion object {
        const val KEYSET_NAME = "remex_routines_keyset"
        const val MASTER_KEY_URI = "android-keystore://remex_routines_key"

        /** Not a Tink key name; AndroidKeysetManager reads only [KEYSET_NAME] from this file. */
        const val KEY_LOSS_MARKER = "remex_routines_key_loss_at"

        init {
            AeadConfig.register()
        }
    }
}
