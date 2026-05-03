package com.clindsay94.remex.security

import android.content.Context
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

object PinnedHostStore {
    private const val FILE_NAME = "remex_pinned_hosts"

    private fun getPrefs(context: Context) = EncryptedSharedPreferences.create(
        context,
        FILE_NAME,
        MasterKey.Builder(context).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build(),
        EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
        EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
    )

    fun getPin(context: Context, hostId: String): String? {
        return getPrefs(context).getString(hostId, null)
    }

    fun setPin(context: Context, hostId: String, spkiHash: String) {
        getPrefs(context).edit().putString(hostId, spkiHash).apply()
    }

    fun removePin(context: Context, hostId: String) {
        getPrefs(context).edit().remove(hostId).apply()
    }

    fun listPaired(context: Context): Map<String, String> {
        val all = getPrefs(context).all
        val result = mutableMapOf<String, String>()
        for ((k, v) in all) {
            if (v is String) {
                result[k] = v
            }
        }
        return result
    }
}
