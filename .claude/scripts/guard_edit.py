#!/usr/bin/env python3
"""Catch two classes of broken edit at the moment they happen, not hours later.

This runs as a PostToolUse hook on Edit and Write. It exists because of two things that
went wrong repeatedly and were expensive to find after the fact:

1. PHANTOM EDITS. An Edit reported success and the change was never on disk. Everything
   downstream then reasoned about code that did not exist - tests "passed", reviews
   "approved", and the defect was still sitting there. Nothing in the transcript
   distinguished this from a real edit, so it was only ever caught by chance later.

   The check is blunt and therefore reliable: after the tool says it wrote something,
   read the file back and confirm the new text is actually in it.

2. LOCALIZATION FILE CORRUPTION. PowerShell string interpolation mis-escaped apostrophes
   and flattened arrays, writing NUL bytes into Strings.tr.resx. Separately, duplicate
   key definitions crept into resource files and broke the build. Both are invisible at
   edit time and only surface as a confusing failure much later.

Deliberately NOT done here: the full translation check. scripts/check-localization.ps1
is a thorough four-axis validator, but it walks git history and takes seconds. Running it
on every edit would be unusable. It runs once, from scripts/verify.ps1. This hook only
does the fast structural checks that catch a file being physically malformed.

Exit codes:
    0  fine, or the hook could not do its job (never block work over the hook's own problems)
    2  a real defect was found; the message on stderr goes back as blocking feedback
"""

import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter

EXIT_OK = 0
EXIT_BLOCK = 2

# Files whose internal structure we check. Everything else only gets the phantom-edit check.
STRUCTURAL_SUFFIXES = (".resx", ".xml")

# Shell tools also get the structural check. This is not belt-and-braces: the NUL-byte
# corruption in reason 2 above came from PowerShell, which never fires a PostToolUse on
# Edit or Write, so for years the guard was watching the one door the bug did not use.
# There is no phantom-edit check here - a shell command has no declared "expected text"
# to compare against - only the physical-integrity check on resource files it named.
SHELL_TOOLS = ("Bash", "PowerShell")

# Any token in a command line that looks like a path to a resource file. Deliberately
# greedy about path punctuation (drive letters, both slash directions, $vars) and
# deliberately dumb about context: a false positive costs one file read of a file that
# was already going to be fine, while a false negative is the corruption shipping.
RESOURCE_PATH_IN_COMMAND = re.compile(
    r"[A-Za-z0-9_.:$(){}/\\-]+\.(?:resx|xml)\b", re.IGNORECASE
)

# Elements whose 'name' attribute must be unique within a resource file.
# .resx uses <data name="...">, Android strings.xml uses <string name="...">.
UNIQUE_NAME_TAGS = ("data", "string")


def block(headline, detail_lines):
    """Report a real defect and stop the tool call from being treated as successful."""
    out = [headline, ""]
    out.extend(detail_lines)
    print("\n".join(out), file=sys.stderr)
    sys.exit(EXIT_BLOCK)


def normalise(text):
    """Compare text without tripping over line endings.

    A file on this repo can be CRLF on disk while the edit payload uses LF. Without this,
    every multi-line edit on Windows would look like a phantom edit and the hook would be
    noise instead of signal - which is worse than not having it.
    """
    return text.replace("\r\n", "\n").replace("\r", "\n")


def check_phantom(tool_name, tool_input, path, text):
    """Confirm what the tool said it wrote is genuinely on disk."""
    if tool_name == "Write":
        expected = tool_input.get("content")
        if expected is None:
            return

        # A write that produced an EMPTY file when content was intended really did fail,
        # and that is worth blocking on.
        if expected.strip() and not text.strip():
            block(
                "The file is empty after Write reported writing content to it.",
                [
                    f"File: {path}",
                    "",
                    "Nothing landed. Check the path and redo the write.",
                ],
            )

        # A mismatch that is NOT emptiness gets reported but does not block. Requiring the
        # file to be byte-identical to the payload is structurally unreliable: formatters
        # and normalisers legitimately rewrite a file the moment it is written. This very
        # check fired on a YAML normaliser adding quotes around a description field that
        # contained an apostrophe - a correct write, correctly reformatted, reported as a
        # failure. Blocking on that would make the guard obstruct ordinary work, which is
        # how guards get switched off. The Edit path below asks the sharper question -
        # is the new text present? - and that one does block.
        if normalise(expected).strip() != normalise(text).strip():
            print(
                f"guard_edit: note - {path} differs from the Write payload "
                "(likely a formatter reran on it). Not blocking; check it if the change matters.",
                file=sys.stderr,
            )
        return

    # Edit
    new_string = tool_input.get("new_string")
    old_string = tool_input.get("old_string")

    if new_string:
        if normalise(new_string) not in normalise(text):
            block(
                "The edit reported success but the new text is not in the file.",
                [
                    f"File: {path}",
                    "",
                    "This is a phantom edit: the tool returned success and the change is not",
                    "on disk. Do not proceed as though it landed. Read the file, work out why",
                    "the edit did not apply, and redo it.",
                    "",
                    "First 200 characters that should be present but are not:",
                    normalise(new_string)[:200],
                ],
            )
    elif old_string:
        # An empty new_string means "delete this text". Success is the old text being gone.
        if normalise(old_string) in normalise(text):
            block(
                "The deletion reported success but the text is still in the file.",
                [
                    f"File: {path}",
                    "",
                    "The text that should have been removed is still present. Read the file",
                    "and redo the deletion.",
                ],
            )


def check_structure(path, raw_bytes, text):
    """Fast physical-integrity checks for resource files."""
    if b"\x00" in raw_bytes:
        offset = raw_bytes.index(b"\x00")
        block(
            "This resource file now contains NUL bytes and is corrupt.",
            [
                f"File: {path}",
                f"First NUL byte at offset {offset}.",
                "",
                "This is the signature of a PowerShell string-interpolation write that",
                "mis-escaped content or flattened an array. Restore the file from git and",
                "redo the change with a Python script using explicit UTF-8 encoding.",
            ],
        )

    try:
        root = ET.fromstring(text)
    except ET.ParseError as exc:
        block(
            "This resource file is no longer valid XML.",
            [
                f"File: {path}",
                f"Parser said: {exc}",
                "",
                "The build will fail on this. Fix the markup before continuing.",
            ],
        )
        return

    names = [
        el.get("name")
        for el in root.iter()
        if el.tag in UNIQUE_NAME_TAGS and el.get("name")
    ]
    duplicates = sorted(n for n, count in Counter(names).items() if count > 1)
    if duplicates:
        shown = duplicates[:10]
        more = len(duplicates) - len(shown)
        block(
            f"This resource file defines {len(duplicates)} key(s) more than once.",
            [
                f"File: {path}",
                "",
                "Duplicate keys break the build:",
                *(f"  {n}" for n in shown),
                *( [f"  ...and {more} more"] if more > 0 else [] ),
                "",
                "Remove the duplicates before continuing.",
            ],
        )


def check_shell_command(payload):
    """Structurally check any resource file a shell command named.

    A shell write cannot be checked for phantom edits - there is no declared expected
    content - but it can be checked for the damage it actually caused: NUL bytes, broken
    XML, duplicate keys. That is the whole of reason 2 in the module docstring.

    Only files that exist and sit inside the project are checked, so a command that merely
    mentions a path (a grep pattern, a log line) costs at most one read of a healthy file.
    """
    command = (payload.get("tool_input") or {}).get("command")
    if not command:
        return EXIT_OK

    response = payload.get("tool_response")
    if isinstance(response, dict) and response.get("error"):
        return EXIT_OK

    project_dir = os.path.abspath(os.environ.get("CLAUDE_PROJECT_DIR") or os.getcwd())

    seen = set()
    for match in RESOURCE_PATH_IN_COMMAND.findall(command):
        candidate = os.path.abspath(
            match if os.path.isabs(match) else os.path.join(project_dir, match)
        )
        if candidate in seen:
            continue
        seen.add(candidate)

        # Stay inside the project. A shell command may reference SDK or NuGet XML that is
        # none of this guard's business and may legitimately be huge.
        #
        # commonpath raises on Windows when the two paths are on different drives, which
        # is the normal case here: the repo is on Z: and most absolute paths a command
        # mentions are on C:. A different drive is definitively outside the project, so
        # treat the raise as the "skip it" answer rather than letting it crash the hook.
        try:
            inside = os.path.commonpath([candidate, project_dir]) == project_dir
        except ValueError:
            inside = False
        if not inside:
            continue
        if not os.path.isfile(candidate):
            continue

        try:
            with open(candidate, "rb") as handle:
                raw_bytes = handle.read()
            text = raw_bytes.decode("utf-8")
        except (OSError, UnicodeDecodeError):
            # Unreadable or not UTF-8. The text-level checks do not apply.
            continue

        check_structure(candidate, raw_bytes, text)

    return EXIT_OK


def main():
    try:
        # Read the raw bytes and decode UTF-8 explicitly. Do NOT use json.load(sys.stdin):
        # on Windows, sys.stdin decodes using the locale encoding (cp1252 here), so every
        # non-ASCII character in the payload arrives mangled - an em-dash turns up as the
        # three characters "a-circumflex, euro, emdash". The text would then never match
        # the correctly-encoded file on disk, and the guard would report a phantom edit on
        # every edit containing a non-ASCII character. That includes every localization
        # file, which is most of what this guard is here to protect.
        payload = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    except Exception:
        # The hook could not read its own input. That is the hook's problem, not the
        # user's; failing the tool call over it would be worse than staying quiet.
        return EXIT_OK

    tool_name = payload.get("tool_name") or ""

    if tool_name in SHELL_TOOLS:
        return check_shell_command(payload)

    if tool_name not in ("Edit", "Write"):
        return EXIT_OK

    tool_input = payload.get("tool_input") or {}
    path = tool_input.get("file_path")
    if not path:
        return EXIT_OK

    # If the tool itself already reported a failure, it has been surfaced already.
    response = payload.get("tool_response")
    if isinstance(response, dict) and response.get("error"):
        return EXIT_OK

    if not os.path.isfile(path):
        block(
            "The file does not exist after the tool reported writing it.",
            [
                f"File: {path}",
                "",
                "Nothing landed. Check the path and redo the write.",
            ],
        )

    try:
        with open(path, "rb") as handle:
            raw_bytes = handle.read()
    except OSError as exc:
        print(f"guard_edit: could not read {path} ({exc}); skipping checks.", file=sys.stderr)
        return EXIT_OK

    try:
        text = raw_bytes.decode("utf-8")
    except UnicodeDecodeError:
        # Binary or a non-UTF-8 encoding. The text-level checks do not apply.
        return EXIT_OK

    check_phantom(tool_name, tool_input, path, text)

    if path.lower().endswith(STRUCTURAL_SUFFIXES):
        check_structure(path, raw_bytes, text)

    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
