"""Generate the shared routine fixtures (RemEx-pp0rt.3, routines spec §13.1).

Writes remex.core.tests/Fixtures/Routines/*.json and copies the directory byte for byte to
remex.android/app/src/test/resources/routines/. Both test suites read the same files:

  * <name>.valid.json / <name>.invalid.<code>.json  - a RoutineSet document and the verdict the C#
    RoutineValidator and the Kotlin RoutineValidator must both reach (the set-level code, else the
    first failing routine's code).
  * wire.<message_type>.json - one envelope {"type", "<slot>"} with every payload field populated.
    C# must read it and re-emit the slot as the same JSON tree; Kotlin must read it and its builder
    must emit the same tree.
  * rejected.<name>.json - a payload both readers must refuse (C#: null slot; Kotlin: null).
  * model.routine-runs.json - RoutineRun records with every nested key populated, re-emitted as the
    same tree on both sides.
  * reason-codes.json, host-identity-vectors.json - parity lists.
  * manifest.json - every fixture above, so neither side has to list a resource directory.

RoutineFixtureParityTests (C#) fails when the two directories differ, so after editing this file
run it and commit both copies:

    uv run python scripts/generate-routine-fixtures.py
"""

import copy
import hashlib
import json
import os
import shutil

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CS_DIR = os.path.join(REPO, "remex.core.tests", "Fixtures", "Routines")
KT_DIR = os.path.join(REPO, "remex.android", "app", "src", "test", "resources", "routines")

HOST = "9f2c4be07a1d33e5"
T0 = 1790000000000

REASON_CODES = [
    "ok", "pc_unreachable", "pc_unreachable_away", "wait_timeout", "wake_no_mac", "wake_send_failed",
    "permission_local_network", "pc_not_selected", "pc_not_paired", "pc_too_old", "step_timeout",
    "transport_lost", "after_power_off", "launch_not_allowed", "launch_failed", "power_unsupported",
    "power_denied_by_os", "power_failed", "media_unavailable", "sensor_unavailable",
    "idle_source_unavailable", "session_source_unavailable", "routine_not_found", "rejected_by_pc",
    "schema_too_new", "payload_too_large", "stale_revision", "revision_conflict", "blocked_by_pc",
    "destructive_not_last", "too_many_destructive", "too_many_steps", "too_many_routines",
    "too_many_homes", "budget_exceeded", "home_not_set", "step_not_allowed_on_pc", "trigger_not_pc",
    "wrong_pc", "unsupported_trigger", "unsupported_step", "duplicate_id", "field_not_allowed",
    "invalid_field", "paused_on_phone", "paused_on_pc", "disabled_on_pc", "skipped_disabled",
    "owner_absent", "already_running", "cooldown", "flap_suppressed", "rate_limited",
    "conflict_countdown_active", "cancelled_on_pc", "cancelled_on_phone", "interrupted_pc",
    "interrupted_phone", "notify_queued", "notify_expired", "notify_denied_phone",
    "background_restricted", "deferred_by_os", "simulated", "dry_run", "countdown_unseen",
    "nfc_unknown_tag", "nfc_device_locked", "nfc_disabled", "fingerprint_capture_failed",
    "home_fingerprint_stale", "store_reset", "internal_error",
]


def uuid4(n):
    """A deterministic lower-case UUID v4 from a small integer."""
    h = hashlib.sha256(str(n).encode()).hexdigest()
    return f"{h[0:8]}-{h[8:12]}-4{h[13:16]}-a{h[17:20]}-{h[20:32]}"


def routine(n=1, name="Game time", trigger=None, steps=None, **extra):
    r = {
        "id": uuid4(n),
        "name": name,
        "hostIdentity": HOST,
        "enabled": True,
        "revision": 1,
        "trigger": trigger if trigger is not None else {"type": "manual"},
        "steps": steps if steps is not None else [{"type": "power", "verb": "LOCK"}],
        "createdAtUnixMs": T0,
        "updatedAtUnixMs": T0,
    }
    r.update(extra)
    return r


def doc(*routines, schema=1):
    return {"schemaVersion": schema, "routines": list(routines)}


def key_for(pin):
    if pin is None:
        return None
    t = pin.strip()
    if t.startswith("sha256/"):
        t = t[len("sha256/"):]
    t = t.strip()
    return hashlib.sha256(t.encode("utf-8")).digest()[:8].hex() if t else None


SPEC_EXAMPLE = doc(
    {
        "id": "3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10",
        "name": "Game time",
        "hostIdentity": HOST,
        "enabled": True,
        "revision": 3,
        "appearance": {"icon": "sports_esports", "color": "#6750A4"},
        "trigger": {"type": "home.arrive", "homeId": "b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11"},
        "steps": [
            {"type": "wake", "mac": "0A:1B:2C:3D:4E:5F", "broadcastIp": "192.168.1.255", "port": 9},
            {"type": "waitOnline", "timeoutSeconds": 120},
            {"type": "launchApp", "appId": "c1d2e3f4-0000-4000-8000-00000000abcd", "appLabel": "Steam"},
            {"type": "notify", "target": "phone", "title": "PC ready", "body": "Steam is starting."},
        ],
        "createdAtUnixMs": T0,
        "updatedAtUnixMs": T0,
    },
    {
        "id": "5a9d0f21-1c3e-4b7a-8e2f-6c4d3b2a1f00",
        "name": "Bedtime",
        "hostIdentity": HOST,
        "enabled": True,
        "revision": 2,
        "trigger": {"type": "pc.idle", "idleMinutes": 45, "ignoreWhileMediaPlaying": True},
        "steps": [
            {"type": "notify", "target": "phone", "title": "PC going to sleep", "body": "Idle for 45 minutes."},
            {"type": "power", "verb": "SLEEP"},
        ],
        "createdAtUnixMs": T0,
        "updatedAtUnixMs": T0 + 500000,
    },
)

PC_SENSOR = {"type": "pc.sensor", "sensorId": "/amdcpu/0/temperature/2", "sensorLabel": "CPU package",
             "direction": "above", "threshold": 85.5, "sustainSeconds": 60}

VALIDATION = {
    "spec-example.valid": SPEC_EXAMPLE,
    "manual-lock.valid": doc(routine()),
    "name-trimmed-40.valid": doc(routine(name="  " + "n" * 40 + "  ")),
    "pc-session-sleep-last.valid": doc(routine(
        trigger={"type": "pc.session", "sessionState": "locked"},
        steps=[{"type": "notify", "target": "pc", "title": "Locked"}, {"type": "power", "verb": "SLEEP"}])),
    "pc-sensor.valid": doc(routine(trigger=PC_SENSOR, steps=[
        {"type": "media", "mediaAction": "playPause"}, {"type": "notify", "target": "phone", "title": "Hot", "body": ""}])),
    "phone-steps-after-destructive.valid": doc(routine(steps=[
        {"type": "power", "verb": "SHUTDOWN", "delaySeconds": 600},
        {"type": "notify", "target": "phone", "title": "Shutting down"}])),
    "home-leave-bounds.valid": doc(routine(
        trigger={"type": "home.leave", "homeId": "b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11", "leaveDebounceSeconds": 1800},
        steps=[{"type": "delay", "seconds": 540}])),
    "twelve-steps.valid": doc(routine(steps=[{"type": "delay", "seconds": 1}] * 12)),
    "phone-budget-at-limit.valid": doc(routine(steps=[
        {"type": "delay", "seconds": 480},
        {"type": "launchApp", "appId": "C1D2E3F4-0000-4000-8000-00000000ABCD"}])),
    "pc-budget-at-limit.valid": doc(routine(trigger={"type": "pc.idle", "idleMinutes": 240}, steps=[
        {"type": "delay", "seconds": 600}, {"type": "delay", "seconds": 600}, {"type": "delay", "seconds": 510}])),
    "sixteen-pc-routines.valid": doc(*[routine(100 + i, trigger={"type": "pc.idle", "idleMinutes": 10}) for i in range(16)]),

    "id-uppercase.invalid.invalid_field": doc(routine(id=uuid4(1).upper())),
    "id-not-v4.invalid.invalid_field": doc(routine(id="3f0c2a4e-7b1d-1c55-9a60-2d7e8f1b9c10")),
    "name-blank.invalid.invalid_field": doc(routine(name="   ")),
    "name-41.invalid.invalid_field": doc(routine(name="n" * 41)),
    "name-newline.invalid.invalid_field": doc(routine(name="Game\ntime")),
    "host-identity-uppercase.invalid.invalid_field": doc(routine(hostIdentity=HOST.upper())),
    "revision-zero.invalid.invalid_field": doc(routine(revision=0)),
    "updated-before-created.invalid.invalid_field": doc(routine(updatedAtUnixMs=T0 - 1)),
    "appearance-color.invalid.invalid_field": doc(routine(appearance={"icon": "bolt", "color": "6750A4"})),
    "wakeonlan-reserved.invalid.invalid_field": doc(routine(steps=[{"type": "power", "verb": "WAKEONLAN"}])),
    "unknown-verb.invalid.invalid_field": doc(routine(steps=[{"type": "power", "verb": "REBOOT"}])),
    "wrong-json-type.invalid.invalid_field": doc(routine(steps=[
        {"type": "wake", "mac": "0A:1B:2C:3D:4E:5F", "port": "nine"}])),
    "null-for-non-nullable.invalid.invalid_field": doc(routine(enabled=None)),
    "fractional-int.invalid.invalid_field": doc(routine(steps=[{"type": "delay", "seconds": 5.5}])),
    "not-an-object.invalid.invalid_field": doc(42),
    "delay-zero.invalid.invalid_field": doc(routine(steps=[{"type": "delay", "seconds": 0}])),
    "delay-missing.invalid.invalid_field": doc(routine(steps=[{"type": "delay"}])),
    "idle-241.invalid.invalid_field": doc(routine(trigger={"type": "pc.idle", "idleMinutes": 241})),
    "sensor-no-threshold.invalid.invalid_field": doc(routine(trigger={k: v for k, v in PC_SENSOR.items() if k != "threshold"})),
    "session-state.invalid.invalid_field": doc(routine(trigger={"type": "pc.session", "sessionState": "away"})),
    "four-notify.invalid.invalid_field": doc(routine(steps=[{"type": "notify", "target": "phone", "title": "x"}] * 4)),
    "three-wait-online.invalid.invalid_field": doc(routine(steps=[{"type": "waitOnline"}] * 3)),
    "mac-lowercase.invalid.invalid_field": doc(routine(steps=[{"type": "wake", "mac": "0a:1b:2c:3d:4e:5f"}])),
    "broadcast-leading-zero.invalid.invalid_field": doc(routine(steps=[
        {"type": "wake", "mac": "0A:1B:2C:3D:4E:5F", "broadcastIp": "192.168.01.255"}])),
    "notify-title-blank.invalid.invalid_field": doc(routine(steps=[{"type": "notify", "target": "phone", "title": "\n \t"}])),
    "notify-body-121.invalid.invalid_field": doc(routine(steps=[{"type": "notify", "target": "pc", "title": "t", "body": "b" * 121}])),
    "empty-steps.invalid.invalid_field": doc(routine(steps=[])),
    "null-step.invalid.invalid_field": doc(routine(steps=[{"type": "delay", "seconds": 1}, None])),
    "schema-zero.invalid.invalid_field": doc(routine(), schema=0),
    "manual-with-home.invalid.field_not_allowed": doc(routine(trigger={"type": "manual", "homeId": "b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11"})),
    "lock-with-delay.invalid.field_not_allowed": doc(routine(steps=[{"type": "power", "verb": "LOCK", "delaySeconds": 5}])),
    "delay-with-verb.invalid.field_not_allowed": doc(routine(steps=[{"type": "delay", "seconds": 5, "verb": "LOCK"}])),
    "arrive-with-debounce.invalid.field_not_allowed": doc(routine(trigger={
        "type": "home.arrive", "homeId": "b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11", "leaveDebounceSeconds": 60})),
    "schedule-trigger.invalid.unsupported_trigger": doc(routine(trigger={"type": "schedule", "cron": "0 7 * * *"})),
    "shell-step.invalid.unsupported_step": doc(routine(steps=[{"type": "shell", "command": "calc"}])),
    "wake-on-pc.invalid.step_not_allowed_on_pc": doc(routine(trigger={"type": "pc.idle", "idleMinutes": 5}, steps=[
        {"type": "wake", "mac": "0A:1B:2C:3D:4E:5F"}])),
    "wait-online-on-pc.invalid.step_not_allowed_on_pc": doc(routine(trigger={"type": "pc.session", "sessionState": "unlocked"},
                                                                    steps=[{"type": "waitOnline"}])),
    "thirteen-steps.invalid.too_many_steps": doc(routine(steps=[{"type": "delay", "seconds": 1}] * 13)),
    "two-destructive.invalid.too_many_destructive": doc(routine(steps=[
        {"type": "power", "verb": "SLEEP"}, {"type": "power", "verb": "SHUTDOWN"}])),
    "pc-destructive-not-last.invalid.destructive_not_last": doc(routine(
        trigger={"type": "pc.idle", "idleMinutes": 30},
        steps=[{"type": "power", "verb": "HIBERNATE"}, {"type": "notify", "target": "pc", "title": "late"}])),
    "phone-budget.invalid.budget_exceeded": doc(routine(steps=[
        {"type": "delay", "seconds": 481}, {"type": "launchApp", "appId": "c1d2e3f4-0000-4000-8000-00000000abcd"}])),
    "phone-budget-countdown.invalid.budget_exceeded": doc(routine(steps=[
        {"type": "delay", "seconds": 466}, {"type": "power", "verb": "SHUTDOWN"}])),
    "pc-budget.invalid.budget_exceeded": doc(routine(trigger={"type": "pc.idle", "idleMinutes": 240}, steps=[
        {"type": "delay", "seconds": 600}, {"type": "delay", "seconds": 600}, {"type": "delay", "seconds": 511}])),
    "duplicate-id.invalid.duplicate_id": doc(routine(1), routine(1, name="Copy")),
    "seventeen-pc-routines.invalid.too_many_routines": doc(*[routine(200 + i, trigger={"type": "pc.idle", "idleMinutes": 10}) for i in range(17)]),
    "thirty-three-routines.invalid.too_many_routines": doc(*[routine(300 + i) for i in range(33)]),
    "schema-too-new.invalid.schema_too_new": doc(routine(), schema=2),
    "arrive-no-home.invalid.home_not_set": doc(routine(trigger={"type": "home.arrive"})),
    "wake-no-mac.invalid.wake_no_mac": doc(routine(steps=[{"type": "wake", "port": 9}])),
}

RUN_ID = "0d4c9a51-7f3b-4e2a-9c61-3b5e8d2f1a70"
ROUTINE_A = "5a9d0f21-1c3e-4b7a-8e2f-6c4d3b2a1f00"
ROUTINE_B = "7c2e1b90-4d5f-4a3b-b6c7-8d9e0f1a2b3c"
SENSOR_ROUTINE = routine(4, name="Too hot", trigger=PC_SENSOR, steps=[{"type": "notify", "target": "phone", "title": "CPU hot", "body": "Over 85.5"}],
                         appearance={"icon": "thermostat", "color": "#b3261e"})

WIRE = {
    "routines_sync": ("routinesSync", {
        "schemaVersion": 1, "revision": 7, "paused": True,
        "routines": [SPEC_EXAMPLE["routines"][1], SENSOR_ROUTINE],
        "runCursor": 42, "forget": False, "sentAtUnixMs": T0 + 600000,
    }),
    "routine_sync_result": ("routineSyncResult", {
        "revision": 7, "storedRevision": 7, "status": "partial",
        "results": [
            {"routineId": ROUTINE_A, "accepted": True, "reasonCode": "ok"},
            {"routineId": SENSOR_ROUTINE["id"], "accepted": False, "reasonCode": "sensor_unavailable", "detail": "trigger.sensorId"},
        ],
        "hostPaused": False, "ownerPaused": True, "unsolicited": True, "pcDisabled": [ROUTINE_B],
        "ownerSuspended": "owner_absent", "idleSource": "win32.lastinput", "sessionSource": "wts", "sensorTrigger": True,
    }),
    "routine_step_request": ("routineStepRequest", {
        "runId": RUN_ID, "routineId": ROUTINE_A, "routineName": "Bedtime", "triggerType": "manual", "stepIndex": 2,
        "step": {"type": "power", "verb": "SHUTDOWN", "delaySeconds": 30},
        "testRun": True, "source": "manual.app",
    }),
    "routine_step_result": ("routineStepResult", {
        "runId": RUN_ID, "stepIndex": 2, "outcome": "cancelled", "reasonCode": "cancelled_on_phone",
        "countdownShown": True, "cancelledBy": "phone", "detail": "cancelled from the phone",
    }),
    "routine_notify": ("routineNotify", {
        "notifyId": "e2b7c3d4-5a6f-4b8c-9d0e-1f2a3b4c5d6e", "kind": "countdown", "routineId": ROUTINE_A,
        "routineName": "Bedtime", "runId": RUN_ID, "title": "PC going to sleep", "body": "In 15 seconds. Tap Cancel to stop it.",
        "countdownEndsAtUnixMs": T0 + 15000, "queuedAtUnixMs": T0, "expiresAtUnixMs": T0 + 3600000,
    }),
    "routine_notify_ack": ("routineNotifyAck", {
        "notifyIds": ["e2b7c3d4-5a6f-4b8c-9d0e-1f2a3b4c5d6e", "f3c8d4e5-6b7a-4c9d-8e1f-2a3b4c5d6e7f"],
    }),
    "routine_run_report": ("routineRunReport", {
        "runs": [{
            "runId": RUN_ID, "seq": 43, "routineId": SENSOR_ROUTINE["id"], "routineName": "Too hot", "routineRevision": 1,
            "origin": "pc", "hostIdentity": HOST, "source": "pc.sensor", "testRun": False,
            "sourceDetail": {"sensorName": "CPU package", "value": 91.5, "unit": "°C"},
            "triggeredAtUnixMs": T0, "startedAtUnixMs": T0 + 20, "endedAtUnixMs": T0 + 16020,
            "outcome": "cancelled", "reasonCode": "cancelled_on_phone",
            "reasonArgs": {"pc": "DESKTOP-1", "phone": "Pixel", "action": "shut down"},
            "cancelledBy": "phone", "attributes": ["countdown_unseen", "notify_queued"],
            "steps": [
                {"index": 0, "kind": "notify", "status": "succeeded", "startedAtUnixMs": T0 + 20, "endedAtUnixMs": T0 + 25, "reasonCode": "ok"},
                {"index": 1, "kind": "power", "status": "cancelled", "startedAtUnixMs": T0 + 25, "endedAtUnixMs": T0 + 16020,
                 "reasonCode": "cancelled_on_phone", "reasonArgs": {"action": "shut down"}},
            ],
            "countdown": {"shown": False, "startedAtUnixMs": T0 + 25, "cancelledBy": "phone"},
        }],
        "more": True, "live": False,
    }),
    "routine_cancel": ("routineCancel", {"runId": RUN_ID, "reason": "user"}),
    "routine_run_request": ("routineRunRequest", {
        "runId": RUN_ID, "routineId": ROUTINE_A, "testRun": True, "source": "manual.app",
    }),
}


# Payloads both readers must REJECT (C#: a null slot on a non-null envelope; Kotlin: the reader returns
# null). A null element inside a list is refused on both sides (NoNullElementsListConverter / Reader).
REJECTED = {
    "notify-ack-null-id": ("routine_notify_ack", "routineNotifyAck", {"notifyIds": ["e2b7c3d4-5a6f-4b8c-9d0e-1f2a3b4c5d6e", None]}),
    "sync-result-null-result": ("routine_sync_result", "routineSyncResult", {"revision": 1, "status": "ok", "results": [None]}),
    "sync-result-null-disabled": ("routine_sync_result", "routineSyncResult", {"revision": 1, "status": "ok", "pcDisabled": [ROUTINE_A, None]}),
    "run-report-null-run": ("routine_run_report", "routineRunReport", {"runs": [None], "more": False}),
    "run-report-null-attribute": ("routine_run_report", "routineRunReport", {"runs": [
        {"runId": RUN_ID, "routineRevision": 1, "testRun": False, "triggeredAtUnixMs": T0, "startedAtUnixMs": T0,
         "attributes": ["simulated", None]}]}),
    "run-report-null-step": ("routine_run_report", "routineRunReport", {"runs": [
        {"runId": RUN_ID, "routineRevision": 1, "testRun": False, "triggeredAtUnixMs": T0, "startedAtUnixMs": T0,
         "steps": [None]}]}),
}

# Standalone RoutineRun records (the host store shape, so ownerClientId appears) with every
# sourceDetail and reasonArgs key populated somewhere: a key typo on either side fails the tree check.
MODEL_RUNS = [
    {
        "runId": "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", "routineId": uuid4(1), "routineName": "Game time",
        "routineRevision": 4, "origin": "phone", "hostIdentity": HOST, "source": "home.arrive", "testRun": True,
        "sourceDetail": {"homeLabel": "Home (router 192.168.1.1)"},
        "triggeredAtUnixMs": T0, "startedAtUnixMs": T0 + 61000, "endedAtUnixMs": T0 + 70000,
        "outcome": "failed", "reasonCode": "launch_not_allowed",
        "reasonArgs": {"pc": "DESKTOP-1", "app": "Steam", "detail": "appId not in launchers.json"},
        "attributes": ["deferred_by_os", "simulated"],
        "steps": [
            {"index": 0, "kind": "wake", "status": "succeeded", "startedAtUnixMs": T0 + 61000, "endedAtUnixMs": T0 + 61100, "reasonCode": "ok"},
            {"index": 1, "kind": "launchApp", "status": "failed", "startedAtUnixMs": T0 + 61100, "endedAtUnixMs": T0 + 70000,
             "reasonCode": "launch_not_allowed", "reasonArgs": {"app": "Steam", "routine": "Game time"}},
            {"index": 2, "kind": "notify", "status": "skipped"},
        ],
    },
    {
        "runId": "2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e", "seq": 118, "ownerClientId": "client-7f3a9c",
        "routineId": ROUTINE_A, "routineName": "Bedtime", "routineRevision": 2, "origin": "pc", "hostIdentity": HOST,
        "source": "pc.idle", "testRun": False, "sourceDetail": {"idleMinutes": 45},
        "triggeredAtUnixMs": T0, "startedAtUnixMs": T0, "endedAtUnixMs": T0,
        "outcome": "skipped", "reasonCode": "owner_absent",
        "reasonArgs": {"phone": "Pixel 9", "date": "2026-08-27", "n": "3"},
        "steps": [],
    },
    {
        "runId": "3c4d5e6f-7a8b-4c9d-ae0f-1a2b3c4d5e6f", "seq": 119, "ownerClientId": "client-7f3a9c",
        "routineId": ROUTINE_B, "routineName": "Lock and sleep", "routineRevision": 9, "origin": "pc", "hostIdentity": HOST,
        "source": "pc.session", "testRun": False,
        "sourceDetail": {"sessionState": "locked", "sensorName": "GPU hot spot", "value": -12.25, "unit": "°C"},
        "triggeredAtUnixMs": T0, "startedAtUnixMs": T0 + 3000, "endedAtUnixMs": T0 + 18500,
        "outcome": "succeeded", "reasonCode": "ok",
        "reasonArgs": {"sensor": "GPU hot spot", "duration": "15 s", "routine": "Lock and sleep", "action": "sleep"},
        "attributes": ["countdown_unseen", "dry_run", "notify_queued", "background_restricted"],
        "steps": [
            {"index": 0, "kind": "power", "status": "simulated", "startedAtUnixMs": T0 + 3000, "endedAtUnixMs": T0 + 18500,
             "reasonCode": "simulated", "reasonArgs": {"action": "sleep", "pc": "DESKTOP-1"}},
        ],
        "countdown": {"shown": True, "startedAtUnixMs": T0 + 3000},
    },
]


def write(name, value):
    path = os.path.join(CS_DIR, name)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(value, indent=2, ensure_ascii=False) + "\n")


def main():
    if os.path.isdir(CS_DIR):
        shutil.rmtree(CS_DIR)
    os.makedirs(CS_DIR)

    validation = []
    for name, value in sorted(VALIDATION.items()):
        write(name + ".json", value)
        expected = "ok" if name.endswith(".valid") else name.rsplit(".", 1)[1]
        validation.append({"file": name + ".json", "expected": expected})

    wire = []
    for message_type, (slot, payload) in sorted(WIRE.items()):
        write(f"wire.{message_type}.json", {"type": message_type, slot: payload})
        wire.append({"file": f"wire.{message_type}.json", "type": message_type, "slot": slot})

    rejected = []
    for name, (message_type, slot, payload) in sorted(REJECTED.items()):
        write(f"rejected.{name}.json", {"type": message_type, slot: payload})
        rejected.append({"file": f"rejected.{name}.json", "type": message_type, "slot": slot})

    write("model.routine-runs.json", MODEL_RUNS)

    write("reason-codes.json", {"codes": REASON_CODES})

    pins = [
        "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=",
        "sha256/47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=",
        "  sha256/ 47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=  ",
        "47deqpj8hbsa+/timw+5jceuqerkm5nmpjwzg3hsufu=",
        "SHA256/47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=",
        "sha256/sha256/abc",
        "abc",
        "",
        "   ",
        "sha256/",
        None,
    ]
    write("host-identity-vectors.json", {"vectors": [{"pin": p, "key": key_for(p)} for p in pins]})

    write("manifest.json", {
        "validation": validation,
        "wire": wire,
        "rejected": rejected,
        "routineRuns": "model.routine-runs.json",
        "reasonCodes": "reason-codes.json",
        "hostIdentityVectors": "host-identity-vectors.json",
    })

    if os.path.isdir(KT_DIR):
        shutil.rmtree(KT_DIR)
    shutil.copytree(CS_DIR, KT_DIR)
    print(f"wrote {len(os.listdir(CS_DIR))} fixtures to both directories")


if __name__ == "__main__":
    main()
