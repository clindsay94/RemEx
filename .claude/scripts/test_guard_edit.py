"""Exercise .claude/scripts/guard_edit.py against every case it claims to catch.

Each case states the payload, what the file on disk actually contains, and the exit code
the guard must produce. 2 = blocked, 0 = allowed. A guard that blocks correct edits is
worse than no guard, so the false-positive cases matter as much as the detection ones.
"""

import json
import os
import subprocess
import sys
import tempfile

GUARD = os.path.join(os.path.dirname(os.path.abspath(__file__)), "guard_edit.py")

VALID_RESX = (
    '<?xml version="1.0" encoding="utf-8"?>\n'
    "<root>\n"
    '  <data name="Alpha" xml:space="preserve"><value>A</value></data>\n'
    '  <data name="Beta" xml:space="preserve"><value>B</value></data>\n'
    "</root>\n"
)

DUPLICATE_RESX = VALID_RESX.replace('name="Beta"', 'name="Alpha"')

MALFORMED_RESX = VALID_RESX.replace("</root>", "")

ANDROID_STRINGS_DUP = (
    '<?xml version="1.0" encoding="utf-8"?>\n'
    "<resources>\n"
    '  <string name="app_name">RemEx</string>\n'
    '  <string name="app_name">RemEx Again</string>\n'
    "</resources>\n"
)


def run_case(name, tool_name, tool_input, disk_bytes, expect, suffix=".txt", write_file=True):
    tmpdir = tempfile.mkdtemp()
    path = os.path.join(tmpdir, "subject" + suffix)

    if write_file:
        with open(path, "wb") as handle:
            handle.write(disk_bytes)

    payload = dict(tool_input)
    payload["file_path"] = path

    # Send real UTF-8 BYTES, exactly as the harness does. Using text=True here would
    # encode with the locale encoding on the way in, which cancels out a locale-decoding
    # bug on the way out and makes the suite blind to it - that is precisely how the
    # cp1252 stdin bug survived the first version of these tests.
    # Pin the child's text-IO encoding to cp1252. This is the whole point of the exercise:
    # the harness sends UTF-8 bytes, and the hook runs in a Windows shell whose locale
    # encoding is cp1252. Without pinning it, the test inherits whatever locale the
    # developer's shell happens to have - UTF-8 under Git Bash - and the mismatch that
    # breaks the hook in real use simply does not occur, so the suite reports green on
    # code that is broken in production. Pinning makes the hostile condition deterministic
    # regardless of who runs the tests or where.
    #
    # sys.stdin is affected by this; sys.stdin.buffer is not. That is exactly the
    # difference between the broken implementation and the correct one.
    env = dict(os.environ, PYTHONIOENCODING="cp1252")

    proc = subprocess.run(
        [sys.executable, GUARD],
        # ensure_ascii=False is essential, not cosmetic. The default escapes every
        # non-ASCII character to \uXXXX, making the payload pure ASCII - and a pure-ASCII
        # payload cannot be corrupted by a wrong decode, so the suite would go green
        # against broken code. The real harness sends raw UTF-8, which is how the mangled
        # em-dash showed up in the first place.
        input=json.dumps(
            {"tool_name": tool_name, "tool_input": payload}, ensure_ascii=False
        ).encode("utf-8"),
        capture_output=True,
        env=env,
    )
    proc.stderr = proc.stderr.decode("utf-8", errors="replace")

    ok = proc.returncode == expect
    status = "pass" if ok else "FAIL"
    print(f"[{status}] {name}  (expected {expect}, got {proc.returncode})")
    if not ok:
        print("        stderr:", (proc.stderr or "").strip().replace("\n", "\n        ")[:400])
    elif expect == 2:
        headline = (proc.stderr or "").strip().split("\n")[0]
        print(f"        blocked with: {headline}")
    return ok


results = []

# --- the edit genuinely landed -------------------------------------------------------
results.append(run_case(
    "edit that landed is allowed",
    "Edit",
    {"old_string": "hello", "new_string": "goodbye world"},
    b"prefix goodbye world suffix",
    expect=0,
))

# --- the false-positive guard: CRLF on disk, LF in the payload -----------------------
results.append(run_case(
    "CRLF file with LF payload is NOT flagged",
    "Edit",
    {"old_string": "x", "new_string": "line one\nline two"},
    b"line one\r\nline two\r\n",
    expect=0,
))

# --- non-ASCII: the bug that got through the first version of this suite --------------
# The payload reaches the hook as UTF-8 bytes. If it is decoded with the Windows locale
# encoding instead, an em-dash arrives as three mangled characters and never matches the
# file, so a perfectly good edit is reported as a phantom one. Localization files are full
# of non-ASCII text, so this would have misfired constantly on exactly the files that
# matter most.
results.append(run_case(
    "em-dash edit that landed is NOT flagged",
    "Edit",
    {"old_string": "x", "new_string": "a dash — here"},
    "prefix a dash — here suffix".encode("utf-8"),
    expect=0,
))

results.append(run_case(
    "accented translation text that landed is NOT flagged",
    "Edit",
    {"old_string": "x", "new_string": "<value>Fichier reçu — terminé</value>"},
    '<root><data name="A"><value>Fichier reçu — terminé</value></data></root>'.encode("utf-8"),
    expect=0,
    suffix=".resx",
))

results.append(run_case(
    "non-ASCII phantom edit is still caught",
    "Edit",
    {"old_string": "x", "new_string": "übertragung abgeschlossen"},
    "the file says something else".encode("utf-8"),
    expect=2,
))

# --- phantom edit ---------------------------------------------------------------------
results.append(run_case(
    "phantom edit is blocked",
    "Edit",
    {"old_string": "hello", "new_string": "text that never landed"},
    b"the file still says something else entirely",
    expect=2,
))

# --- deletion that did not happen -----------------------------------------------------
results.append(run_case(
    "failed deletion is blocked",
    "Edit",
    {"old_string": "remove me", "new_string": ""},
    b"this content still has remove me inside it",
    expect=2,
))

# --- successful deletion --------------------------------------------------------------
results.append(run_case(
    "successful deletion is allowed",
    "Edit",
    {"old_string": "remove me", "new_string": ""},
    b"this content is clean now",
    expect=0,
))

# --- Write mismatch -------------------------------------------------------------------
# A differing-but-non-empty file is NOT blocked. Formatters legitimately rewrite files
# immediately after they are written - a YAML normaliser adding quotes around a value
# containing an apostrophe triggered exactly this. Blocking on it would make the guard
# fight ordinary tooling.
results.append(run_case(
    "Write reformatted by a linter is reported but NOT blocked",
    "Write",
    {"content": "description: it's fine"},
    b'description: "it\'s fine"',
    expect=0,
))

results.append(run_case(
    "Write that produced an empty file is blocked",
    "Write",
    {"content": "what we asked for"},
    b"",
    expect=2,
))

# --- Write that matched ---------------------------------------------------------------
results.append(run_case(
    "Write that matched is allowed",
    "Write",
    {"content": "what we asked for"},
    b"what we asked for\n",
    expect=0,
))

# --- file missing after the write -----------------------------------------------------
results.append(run_case(
    "missing file after write is blocked",
    "Write",
    {"content": "anything"},
    b"",
    expect=2,
    write_file=False,
))

# --- resx structural checks -----------------------------------------------------------
results.append(run_case(
    "clean resx is allowed",
    "Write",
    {"content": VALID_RESX},
    VALID_RESX.encode("utf-8"),
    expect=0,
    suffix=".resx",
))

results.append(run_case(
    "resx with NUL bytes is blocked",
    "Edit",
    {"old_string": "x", "new_string": "Alpha"},
    VALID_RESX.encode("utf-8").replace(b"<value>A</value>", b"<value>A\x00</value>"),
    expect=2,
    suffix=".resx",
))

results.append(run_case(
    "resx with a duplicate key is blocked",
    "Write",
    {"content": DUPLICATE_RESX},
    DUPLICATE_RESX.encode("utf-8"),
    expect=2,
    suffix=".resx",
))

results.append(run_case(
    "malformed resx XML is blocked",
    "Write",
    {"content": MALFORMED_RESX},
    MALFORMED_RESX.encode("utf-8"),
    expect=2,
    suffix=".resx",
))

results.append(run_case(
    "android strings.xml duplicate is blocked",
    "Write",
    {"content": ANDROID_STRINGS_DUP},
    ANDROID_STRINGS_DUP.encode("utf-8"),
    expect=2,
    suffix=".xml",
))

# --- non-resource files skip the structural checks ------------------------------------
results.append(run_case(
    "duplicate names in a non-resource file are ignored",
    "Write",
    {"content": DUPLICATE_RESX},
    DUPLICATE_RESX.encode("utf-8"),
    expect=0,
    suffix=".txt",
))

# --- a shell tool with no command payload is ignored ----------------------------------
results.append(run_case(
    "a shell payload with no command is ignored",
    "Bash",
    {"content": "irrelevant"},
    b"irrelevant",
    expect=0,
))


def run_shell_case(name, command, expect, disk=None, suffix=".xml", filename="subject"):
    """Exercise the shell branch: a command names a resource file the guard must inspect.

    The shell branch exists because the NUL-byte corruption came from PowerShell, which
    never fires a PostToolUse on Edit or Write. These cases are the proof it now bites,
    and the false-positive cases below are what keep it from becoming noise.

    CLAUDE_PROJECT_DIR is pointed at the temp dir because the guard deliberately ignores
    resource files outside the project - so without this the guard would correctly skip
    every subject and the suite would go green while checking nothing.
    """
    tmpdir = tempfile.mkdtemp()
    path = os.path.join(tmpdir, filename + suffix)
    if disk is not None:
        with open(path, "wb") as handle:
            handle.write(disk)

    env = dict(os.environ, PYTHONIOENCODING="cp1252", CLAUDE_PROJECT_DIR=tmpdir)
    proc = subprocess.run(
        [sys.executable, GUARD],
        input=json.dumps(
            {
                "tool_name": "Bash",
                "tool_input": {"command": command.replace("{path}", path)},
            },
            ensure_ascii=False,
        ).encode("utf-8"),
        capture_output=True,
        env=env,
    )
    stderr = proc.stderr.decode("utf-8", errors="replace")
    ok = proc.returncode == expect
    print(f"[{'pass' if ok else 'FAIL'}] {name}  (expected {expect}, got {proc.returncode})")
    if not ok:
        print("        stderr:", stderr.strip().replace("\n", "\n        ")[:400])
    elif expect == 2:
        print(f"        blocked with: {stderr.strip().splitlines()[0]}")
    return ok


# --- the shell branch: PowerShell-class corruption is now caught ----------------------
results.append(run_shell_case(
    "shell write leaving NUL bytes is blocked",
    'pwsh -c "Set-Content {path} $value"',
    expect=2,
    disk=b'<root>\x00 broken</root>',
))

results.append(run_shell_case(
    "shell write leaving duplicate keys is blocked",
    'pwsh -c "Set-Content {path} $value"',
    expect=2,
    disk=DUPLICATE_RESX.encode("utf-8"),
    suffix=".resx",
))

results.append(run_shell_case(
    "shell write leaving malformed XML is blocked",
    'pwsh -c "Set-Content {path} $value"',
    expect=2,
    disk=MALFORMED_RESX.encode("utf-8"),
    suffix=".resx",
))

# --- false positives: the guard must stay quiet on healthy or irrelevant files --------
results.append(run_shell_case(
    "shell command naming a healthy resource file is allowed",
    "cat {path}",
    expect=0,
    disk=VALID_RESX.encode("utf-8"),
    suffix=".resx",
))

results.append(run_shell_case(
    "shell command naming a file that does not exist is allowed",
    "cat {path}",
    expect=0,
    disk=None,
))

results.append(run_shell_case(
    "shell command touching no resource file is allowed",
    "git status --short",
    expect=0,
))

# A different drive letter is the normal case on this machine: the repo is on Z: and most
# absolute paths a command mentions are on C:. os.path.commonpath raises ValueError there,
# which crashed the hook (exit 1) until it was handled. Exit 1 is not exit 2, so this
# would never have blocked anything - it would just have failed silently forever.
results.append(run_shell_case(
    "resource file on another drive does not crash the guard",
    "cat C:/Windows/definitely-not-ours.xml",
    expect=0,
))

print()
print(f"{sum(results)} of {len(results)} cases passed.")
sys.exit(0 if all(results) else 1)
