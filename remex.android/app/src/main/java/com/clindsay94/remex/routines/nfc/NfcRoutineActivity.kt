package com.clindsay94.remex.routines.nfc

import android.app.KeyguardManager
import android.content.Intent
import android.nfc.NdefMessage
import android.nfc.NfcAdapter
import android.os.Bundle
import android.os.SystemClock
import androidx.activity.ComponentActivity
import androidx.core.content.IntentCompat
import androidx.lifecycle.lifecycleScope
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.RoutineLog
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.manual.RoutineManualSurfaces
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRunSources
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/**
 * Runs the routine a RemEx NFC tag names (routines spec §8.3.2, T1-T3).
 *
 * **THE ONLY EXPORTED ROUTINE COMPONENT, AND ONLY THE NFC SERVICE CAN START IT.** Tag dispatch needs an
 * exported activity, so this one is guarded by `android:permission="android.permission.
 * DISPATCH_NFC_MESSAGE"`, which only the system NFC service holds: another app cannot send it an
 * intent. Removing that permission turns `remex://routine/...` into an intent any app can fire.
 * `RoutineManifestExportTest` pins it; REGRESSION-GUARDS "NfcRoutineActivity keeps
 * DISPATCH_NFC_MESSAGE".
 *
 * Even then nothing on the tag is trusted: the token must match the routine's own (constant time),
 * the phone must be unlocked (`nfc_device_locked`), and one routine runs at most once per 10 s.
 */
class NfcRoutineActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uri = tagUri(intent)
        lifecycleScope.launch {
            try {
                handle(uri)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                RoutineLog.e("Handling an NFC routine tag failed.", e)
            } finally {
                finish()
            }
        }
    }

    private suspend fun handle(uri: String?) {
        val tag = NfcRoutineTag.parse(uri)
        if (tag == null) {
            if (NfcRoutineTag.looksLikeRoutineTag(uri)) {
                RoutineManualSurfaces.toast(applicationContext, getString(R.string.routines_nfc_toast_invalid))
            }
            return
        }
        val repository = Routines.repository(applicationContext)
        val routine = repository.routine(tag.routineId)
        val stored = routine?.let { Routines.secrets(applicationContext).nfcBinding(tag.routineId)?.token }
        val locked = getSystemService(KeyguardManager::class.java)?.isDeviceLocked ?: true
        when (val verdict = verifier.verify(tag, stored, routine != null, locked, SystemClock.elapsedRealtime())) {
            is NfcTapVerdict.Run -> RoutineManualSurfaces.start(this, verdict.routineId, RoutineRunSources.NFC_TAP)
            is NfcTapVerdict.UnknownTag -> {
                verdict.routineId?.let { repository.recordRefusal(it, RoutineRunSources.NFC_TAP, RoutineReasonCodes.NFC_UNKNOWN_TAG) }
                RoutineManualSurfaces.toast(applicationContext, getString(R.string.routines_nfc_toast_invalid))
            }
            is NfcTapVerdict.DeviceLocked -> {
                repository.recordRefusal(verdict.routineId, RoutineRunSources.NFC_TAP, RoutineReasonCodes.NFC_DEVICE_LOCKED)
                RoutineManualSurfaces.toast(applicationContext, RoutineReasonText.message(applicationContext, RoutineReasonCodes.NFC_DEVICE_LOCKED, null))
            }
            is NfcTapVerdict.Debounced -> RoutineLog.d("A second tap of routine ${RoutineLog.id(verdict.routineId)}'s tag was ignored.")
            NfcTapVerdict.NotRoutineTag -> Unit
        }
    }

    companion object {
        /** Process-wide, so the 10 s window holds across taps that each start this activity. */
        private val verifier = NfcTokenVerifier()

        /** The tag's first URI record, from the NDEF messages, falling back to the intent data. */
        fun tagUri(intent: Intent?): String? {
            if (intent == null) return null
            return try {
                val messages = IntentCompat.getParcelableArrayExtra(intent, NfcAdapter.EXTRA_NDEF_MESSAGES, NdefMessage::class.java)
                messages
                    ?.filterIsInstance<NdefMessage>()
                    ?.flatMap { it.records.orEmpty().toList() }
                    ?.firstNotNullOfOrNull { record -> runCatching { record.toUri() }.getOrNull()?.toString() }
                    ?: intent.dataString
            } catch (e: RuntimeException) {
                // Malformed extras from the dispatch are simply not a tag.
                null
            }
        }
    }
}
