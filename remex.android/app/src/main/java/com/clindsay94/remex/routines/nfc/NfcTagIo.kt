package com.clindsay94.remex.routines.nfc

import android.app.Activity
import android.content.Context
import android.nfc.FormatException
import android.nfc.NdefMessage
import android.nfc.NdefRecord
import android.nfc.NfcAdapter
import android.nfc.Tag
import android.nfc.TagLostException
import android.nfc.tech.Ndef
import android.nfc.tech.NdefFormatable
import com.clindsay94.remex.routines.RoutineLog
import java.io.IOException

/**
 * Reading and writing RemEx tags (routines spec §8.3.2). The write sheet reads in reader mode, which
 * also keeps the system from dispatching the tag to [NfcRoutineActivity] while the sheet is open, so
 * writing or testing a tag never runs its routine.
 *
 * Every call runs on the NFC reader thread and returns an outcome instead of throwing.
 */
object NfcTagIo {
    fun adapter(context: Context): NfcAdapter? = NfcAdapter.getDefaultAdapter(context.applicationContext)

    /** The tag contents (spec §8.3.2): the routine URI, then the Application Record for this package. */
    fun message(context: Context, tag: NfcRoutineTag): NdefMessage =
        NdefMessage(
            arrayOf(
                NdefRecord.createUri(tag.toUri()),
                NdefRecord.createApplicationRecord(context.packageName),
            ),
        )

    fun inspect(tag: Tag): NfcTagInspection {
        val ndef = Ndef.get(tag)
        if (ndef != null) {
            val uri =
                runCatching {
                    ndef.cachedNdefMessage?.records?.firstNotNullOfOrNull { record -> runCatching { record.toUri() }.getOrNull()?.toString() }
                }.getOrNull()
            return NfcTagInspection(NfcTagInspection.Kind.NDEF, ndef.isWritable, ndef.maxSize, uri)
        }
        if (NdefFormatable.get(tag) != null) return NfcTagInspection(NfcTagInspection.Kind.FORMATABLE, writable = true, maxSize = -1, existingUri = null)
        return NfcTagInspection(NfcTagInspection.Kind.UNSUPPORTED, writable = false, maxSize = 0, existingUri = null)
    }

    /** The first URI on the tag, read fresh (the "Test it" tap). */
    fun readUri(tag: Tag): String? = inspect(tag).existingUri

    fun write(tag: Tag, message: NdefMessage): NfcWriteOutcome =
        try {
            val ndef = Ndef.get(tag)
            if (ndef != null) {
                ndef.use {
                    it.connect()
                    if (!it.isWritable) return NfcWriteOutcome.READ_ONLY
                    if (it.maxSize < message.byteArrayLength) return NfcWriteOutcome.TOO_SMALL
                    it.writeNdefMessage(message)
                }
                NfcWriteOutcome.WRITTEN
            } else {
                val formatable = NdefFormatable.get(tag) ?: return NfcWriteOutcome.READ_ONLY
                formatable.use {
                    it.connect()
                    it.format(message)
                }
                NfcWriteOutcome.WRITTEN
            }
        } catch (e: TagLostException) {
            NfcWriteOutcome.LOST_CONTACT
        } catch (e: IOException) {
            RoutineLog.w("Writing an NFC tag failed.", e)
            NfcWriteOutcome.LOST_CONTACT
        } catch (e: FormatException) {
            RoutineLog.w("An NFC tag refused the message format.", e)
            NfcWriteOutcome.LOST_CONTACT
        } catch (e: SecurityException) {
            // The Tag object went stale (the tag left and came back): the same as losing contact.
            NfcWriteOutcome.LOST_CONTACT
        }

    /** Reader mode for the write sheet; [onTag] runs on the NFC thread. */
    fun enableReader(activity: Activity, onTag: (Tag) -> Unit) {
        val flags =
            NfcAdapter.FLAG_READER_NFC_A or NfcAdapter.FLAG_READER_NFC_B or NfcAdapter.FLAG_READER_NFC_F or
                NfcAdapter.FLAG_READER_NFC_V or NfcAdapter.FLAG_READER_NFC_BARCODE
        try {
            adapter(activity)?.enableReaderMode(activity, { tag -> onTag(tag) }, flags, null)
        } catch (e: IllegalStateException) {
            RoutineLog.w("NFC reader mode could not start.", e)
        }
    }

    fun disableReader(activity: Activity) {
        try {
            adapter(activity)?.disableReaderMode(activity)
        } catch (e: IllegalStateException) {
            RoutineLog.w("NFC reader mode could not stop.", e)
        }
    }
}
