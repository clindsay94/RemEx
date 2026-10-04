package com.clindsay94.remex.data

import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.runTest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * [SensorAlertsRepository]'s rules (RemEx-pp4cm.12): the PC owns the rules, the phone asks for them once
 * per connection, an edit is optimistic and sent as one rule, the PC's next list is the answer, and an
 * older PC is never sent a message. Fake send; no socket.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class SensorAlertsRepositoryTest {

    private val sent = ArrayList<String>()
    private var authenticated = true
    private val repo = SensorAlertsRepository(send = { sent += it }, isAuthenticated = { authenticated })

    private val supported = """{"supportsSensorAlerts":true}"""

    private fun rule(name: String, threshold: Double = 90.0) =
            JSONObject()
                    .put("sensorName", name)
                    .put("displayName", name)
                    .put("unit", "°C")
                    .put("threshold", threshold)
                    .put("direction", "Above")
                    .put("severity", "Warning")

    private fun rules(revision: Long, vararg rules: JSONObject): String =
            JSONObject()
                    .put("type", "sensor_alert_rules")
                    .put("sensorAlertRules", JSONObject().put("rules", JSONArray(rules.toList())).put("revision", revision))
                    .toString()

    private fun types(): List<String> = sent.map { JSONObject(it).getString("type") }

    private suspend fun connect(epoch: Long = 1) {
        repo.onConnected(epoch)
        repo.onHostInfo(epoch, supported)
    }

    @Test
    fun `nothing is shown and nothing is sent before a PC that supports alerts has acked`() = runTest {
        authenticated = false
        connect()

        assertTrue(repo.state.value.connected)
        assertTrue(repo.state.value.supported)
        assertFalse(repo.state.value.loaded)
        assertTrue("a request sent before the ack would be refused", sent.isEmpty())

        authenticated = true
        repo.onAuthenticated()

        assertEquals(listOf("sensor_alerts_get"), types())
    }

    @Test
    fun `the list is asked for once per connection however many edges arrive`() = runTest {
        connect()
        repo.onAuthenticated()
        repo.onAuthenticated()
        repo.onHostInfo(1, supported)

        assertEquals(listOf("sensor_alerts_get"), types())

        repo.onDisconnected()
        connect(epoch = 2)
        assertEquals(listOf("sensor_alerts_get", "sensor_alerts_get"), types())
    }

    @Test
    fun `an older PC is never sent anything and cannot be edited`() = runTest {
        repo.onConnected(1)
        repo.onHostInfo(1, """{"supportsHomePinsSync":true}""")
        repo.onAuthenticated()

        assertTrue(sent.isEmpty())
        assertFalse(repo.state.value.supported)
        assertFalse(repo.state.value.canEdit)
        assertFalse(repo.setRule("CPU", "CPU", "°C", 90.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))
        assertFalse(repo.removeRule("CPU"))
        assertTrue(sent.isEmpty())
    }

    @Test
    fun `a list from the PC replaces the rules`() = runTest {
        connect()
        repo.onRulesMessage(rules(1, rule("CPU"), rule("GPU")), 1)

        val state = repo.state.value
        assertTrue(state.loaded)
        assertEquals(listOf("CPU", "GPU"), state.rules.map { it.sensorName })
        assertEquals("cpu is found case-insensitively", "CPU", state.ruleFor("cpu")!!.sensorName)
        assertNull(state.ruleFor("Fan"))

        repo.onRulesMessage(rules(2, rule("GPU")), 1)
        assertEquals(listOf("GPU"), repo.state.value.rules.map { it.sensorName })
    }

    @Test
    fun `a list older than the last on this connection is ignored`() = runTest {
        connect()
        repo.onRulesMessage(rules(5, rule("CPU")), 1)
        repo.onRulesMessage(rules(4, rule("OLD")), 1)

        assertEquals(listOf("CPU"), repo.state.value.rules.map { it.sensorName })
    }

    @Test
    fun `a list that beats its connection is held and a list from an older connection is dropped`() = runTest {
        repo.onRulesMessage(rules(1, rule("HELD")), 2)
        assertTrue(repo.state.value.rules.isEmpty())

        repo.onConnected(2)
        assertEquals(listOf("HELD"), repo.state.value.rules.map { it.sensorName })

        repo.onRulesMessage(rules(9, rule("STALE")), 1)
        assertEquals(listOf("HELD"), repo.state.value.rules.map { it.sensorName })
    }

    @Test
    fun `a set is optimistic and sends one rule, and the PCs list is the answer`() = runTest {
        connect()
        repo.onRulesMessage(rules(1, rule("CPU")), 1)
        sent.clear()

        assertTrue(repo.setRule("GPU", "GPU temperature", "°C", 80.0, SensorAlertDirection.BELOW, SensorAlertSeverity.CRITICAL))

        val shown = repo.state.value.ruleFor("GPU")!!
        assertEquals(80.0, shown.threshold, 0.0)
        assertEquals(SensorAlertDirection.BELOW, shown.direction)
        assertEquals("GPU temperature", shown.displayName)
        assertEquals(listOf("sensor_alert_set"), types())
        assertEquals("GPU", JSONObject(sent.single()).getJSONObject("sensorAlertChange").getString("sensorName"))

        // The PC refused it and sends the list unchanged: the optimistic rule is gone.
        repo.onRulesMessage(rules(2, rule("CPU")), 1)
        assertNull(repo.state.value.ruleFor("GPU"))
    }

    @Test
    fun `a set on a sensor that has a rule replaces it instead of adding a second`() = runTest {
        connect()
        repo.onRulesMessage(rules(1, rule("CPU", 70.0)), 1)

        assertTrue(repo.setRule("cpu", "CPU", "°C", 95.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.CRITICAL))

        val state = repo.state.value
        assertEquals(1, state.rules.size)
        assertEquals(95.0, state.rules.single().threshold, 0.0)
        assertEquals("the PC's own spelling is kept", "CPU", state.rules.single().sensorName)
    }

    @Test
    fun `a remove is optimistic and sends one removal`() = runTest {
        connect()
        repo.onRulesMessage(rules(1, rule("CPU"), rule("GPU")), 1)
        sent.clear()

        assertTrue(repo.removeRule("cpu"))

        assertEquals(listOf("GPU"), repo.state.value.rules.map { it.sensorName })
        assertEquals(listOf("sensor_alert_remove"), types())
    }

    @Test
    fun `a rule the PC would refuse is not sent and changes nothing`() = runTest {
        connect()
        repo.onRulesMessage(rules(1, rule("CPU")), 1)
        sent.clear()

        assertFalse(repo.setRule("  ", "x", null, 5.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))
        assertFalse(repo.setRule("GPU", "GPU", null, Double.NaN, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))
        assertEquals(listOf("CPU"), repo.state.value.rules.map { it.sensorName })
        assertTrue(sent.isEmpty())
    }

    @Test
    fun `a new rule past the PCs limit is not sent but an edit at the limit is`() = runTest {
        connect()
        val full = (0 until SensorAlerts.MaxRules).map { rule("S$it") }.toTypedArray()
        repo.onRulesMessage(rules(1, *full), 1)
        sent.clear()

        assertFalse(repo.setRule("One too many", "x", null, 5.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))
        assertTrue(repo.setRule("S3", "S3", "°C", 5.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))
        assertEquals(1, sent.size)
    }

    @Test
    fun `an edit before the PC has acked this connection sends nothing`() = runTest {
        connect()
        sent.clear()
        authenticated = false

        assertFalse(repo.setRule("CPU", "CPU", null, 5.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))
        assertFalse(repo.removeRule("CPU"))
        assertFalse(repo.refresh())
        assertTrue(sent.isEmpty())
    }

    @Test
    fun `disconnecting clears the rules so one PC never shows as another`() = runTest {
        connect()
        repo.onRulesMessage(rules(1, rule("CPU")), 1)
        assertEquals(1, repo.state.value.rules.size)

        repo.onDisconnected()

        val state = repo.state.value
        assertTrue(state.rules.isEmpty())
        assertFalse(state.connected)
        assertFalse(state.supported)
        assertFalse(state.loaded)
    }

    @Test
    fun `a PC that sends a list is known to support alerts even if host info came late`() = runTest {
        repo.onConnected(1)
        repo.onRulesMessage(rules(1, rule("CPU")), 1)

        assertTrue(repo.state.value.supported)
        assertTrue(repo.state.value.canEdit)
    }

    // A refused phone Save used to close the sheet and say nothing (RemEx-pp4cm.12). The PC answers
    // every set with its whole list, so a list that lacks the rule just sent IS the refusal.

    private fun TestScope.collectRefusals(): List<String> {
        val got = ArrayList<String>()
        backgroundScope.launch(UnconfinedTestDispatcher(testScheduler)) { repo.refusals.toList(got) }
        return got
    }

    @Test
    fun `a list that lacks the rule just sent reports it refused`() = runTest {
        val refusals = collectRefusals()
        connect()
        repo.onRulesMessage(rules(1, rule("CPU")), 1)
        assertTrue(repo.setRule("GPU", "GPU temperature", "°C", 80.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))

        repo.onRulesMessage(rules(1, rule("CPU")), 1)

        assertEquals(listOf("GPU temperature"), refusals)
    }

    @Test
    fun `a list that keeps the rule just sent is not a refusal`() = runTest {
        val refusals = collectRefusals()
        connect()
        repo.onRulesMessage(rules(1, rule("CPU")), 1)
        assertTrue(repo.setRule("GPU", "GPU temperature", "°C", 80.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))

        repo.onRulesMessage(rules(2, rule("CPU"), rule("GPU", 80.0)), 1)

        assertTrue(refusals.isEmpty())
    }

    @Test
    fun `a list that holds the sensor with different values is a refusal too`() = runTest {
        val refusals = collectRefusals()
        connect()
        repo.onRulesMessage(rules(1, rule("CPU", 70.0)), 1)
        assertTrue(repo.setRule("CPU", "CPU", "°C", 95.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))

        repo.onRulesMessage(rules(1, rule("CPU", 70.0)), 1)

        assertEquals(listOf("CPU"), refusals)
    }

    @Test
    fun `a refusal is reported once and a later list does not repeat it`() = runTest {
        val refusals = collectRefusals()
        connect()
        repo.onRulesMessage(rules(1), 1)
        assertTrue(repo.setRule("GPU", "GPU", "°C", 80.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))

        repo.onRulesMessage(rules(1), 1)
        repo.onRulesMessage(rules(2), 1)

        assertEquals(listOf("GPU"), refusals)
    }

    @Test
    fun `a set that was never sent reports no refusal`() = runTest {
        val refusals = collectRefusals()
        repo.onConnected(1)
        repo.onHostInfo(1, """{"supportsHomePinsSync":true}""")

        assertFalse(repo.setRule("CPU", "CPU", "°C", 90.0, SensorAlertDirection.ABOVE, SensorAlertSeverity.WARNING))
        repo.onRulesMessage(rules(1), 1)

        assertTrue(refusals.isEmpty())
    }
}
