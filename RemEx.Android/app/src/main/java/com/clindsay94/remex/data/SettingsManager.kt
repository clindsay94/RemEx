package com.clindsay94.remex.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "settings")

class SettingsManager(private val context: Context) {

    companion object {
        val HOST_KEY = stringPreferencesKey("host")
        val PORT_KEY = intPreferencesKey("port")
        val MAC_KEY = stringPreferencesKey("mac_address")
        val BROADCAST_IP_KEY = stringPreferencesKey("broadcast_ip")
    }

    val hostFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[HOST_KEY] ?: "192.168.1.100"
    }

    val portFlow: Flow<Int> = context.dataStore.data.map { preferences ->
        preferences[PORT_KEY] ?: 5005
    }

    val macAddressFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[MAC_KEY] ?: ""
    }

    val broadcastIpFlow: Flow<String> = context.dataStore.data.map { preferences ->
        preferences[BROADCAST_IP_KEY] ?: "255.255.255.255"
    }

    suspend fun saveSettings(host: String, port: Int, mac: String = "", broadcast: String = "255.255.255.255") {
        context.dataStore.edit { preferences ->
            preferences[HOST_KEY] = host
            preferences[PORT_KEY] = port
            preferences[MAC_KEY] = mac
            preferences[BROADCAST_IP_KEY] = broadcast
        }
    }
}
