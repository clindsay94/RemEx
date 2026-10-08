package com.clindsay94.remex.ui.files.preview

/** What a coloured stretch of a previewed line is (file browser redesign, 2026-10-08). */
enum class SyntaxKind {
    Plain,
    Comment,
    String,
    Number,
    Keyword,
    /** A JSON object key, or an XML tag or attribute name. */
    Key,
    Timestamp,
    LogError,
    LogWarning,
    LogDebug,
}

/** One coloured stretch of a line: characters `[start, start + length)`. */
data class SyntaxSpan(val start: Int, val length: Int, val kind: SyntaxKind)

/** Which rules colour a previewed file. */
enum class SyntaxLanguage {
    Plain,
    Log,
    Json,
    Xml,
    /** C-like: `//` comments, quoted strings, numbers. */
    CLike,
    /** Scripts and config: `#` comments, quoted strings, numbers. */
    Hash,
}

/**
 * Light, per-line colouring for the text preview: log levels and timestamps, JSON keys and strings, XML tags,
 * comments, strings and numbers. Twin of the PC's `SyntaxTokenizer`, rule for rule; the shared vectors in
 * `file-preview-vectors.json` hold the two to each other. Deliberately not a parser: each line is coloured on
 * its own, so a live tail can colour each new line as it arrives.
 */
object SyntaxTokenizer {
    /** The rules for [fileName], by extension. */
    fun languageFor(fileName: String): SyntaxLanguage = when (PreviewClassifier.extensionOf(fileName)) {
        ".log", ".txt" -> SyntaxLanguage.Log
        ".json", ".jsonc" -> SyntaxLanguage.Json
        ".xml", ".axaml", ".xaml", ".html", ".htm", ".csproj", ".props", ".targets", ".resx" -> SyntaxLanguage.Xml
        ".cs", ".kt", ".kts", ".java", ".gradle", ".js", ".ts", ".tsx", ".jsx", ".mjs", ".c", ".h",
        ".cpp", ".hpp", ".go", ".rs", ".css" -> SyntaxLanguage.CLike
        ".py", ".rb", ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".psd1", ".yaml", ".yml", ".toml",
        ".ini", ".cfg", ".conf", ".properties", ".env", ".gitignore", ".editorconfig" -> SyntaxLanguage.Hash
        else -> SyntaxLanguage.Plain
    }

    /** The coloured stretches of [line], in order and non-overlapping. Plain gaps are omitted. */
    fun tokenize(line: String, language: SyntaxLanguage): List<SyntaxSpan> = when (language) {
        SyntaxLanguage.Log -> tokenizeLog(line)
        SyntaxLanguage.Json -> tokenizeCode(line, lineComment = null, jsonKeys = true)
        SyntaxLanguage.Xml -> tokenizeXml(line)
        SyntaxLanguage.CLike -> tokenizeCode(line, lineComment = "//", jsonKeys = false)
        SyntaxLanguage.Hash -> tokenizeCode(line, lineComment = "#", jsonKeys = false)
        SyntaxLanguage.Plain -> emptyList()
    }

    // Upper case as most loggers write them, plus the four-letter lower-case forms the .NET console logger
    // (and so the PC's own logs) writes. Whole words only, so "NoErrors" or "failover" never colour a line.
    private val errorWords = setOf("ERROR", "ERR", "FATAL", "CRITICAL", "CRIT", "FAIL", "FAILED", "EXCEPTION", "fail", "crit")
    private val warningWords = setOf("WARN", "WARNING", "WRN", "warn")
    private val debugWords = setOf("DEBUG", "DBG", "TRACE", "TRC", "VERBOSE", "VRB", "dbug", "trce")

    private fun tokenizeLog(line: String): List<SyntaxSpan> {
        val spans = mutableListOf<SyntaxSpan>()
        val timestampEnd = leadingTimestampLength(line)
        if (timestampEnd > 0) spans += SyntaxSpan(0, timestampEnd, SyntaxKind.Timestamp)
        val level = levelOf(line)
        if (level != SyntaxKind.Plain && timestampEnd < line.length) {
            spans += SyntaxSpan(timestampEnd, line.length - timestampEnd, level)
        }
        return spans
    }

    /** The level a log line is at, judged by its first whole level word (ERROR, WARN, DEBUG…), case-sensitive. */
    internal fun levelOf(line: String): SyntaxKind {
        var i = 0
        while (i < line.length) {
            while (i < line.length && !isAsciiLetter(line[i])) i++
            val start = i
            while (i < line.length && isAsciiLetter(line[i])) i++
            if (i > start) {
                val word = line.substring(start, i)
                if (word in errorWords) return SyntaxKind.LogError
                if (word in warningWords) return SyntaxKind.LogWarning
                if (word in debugWords) return SyntaxKind.LogDebug
            }
        }
        return SyntaxKind.Plain
    }

    /**
     * The length of a leading timestamp: an optional '[', then digits and the separators of a date or time,
     * at least one ':' or '-', and an optional ']'. Zero when the line does not start with one.
     */
    internal fun leadingTimestampLength(line: String): Int {
        var i = 0
        if (i < line.length && line[i] == '[') i++
        val digitsStart = i
        var sawSeparator = false
        var sawDigit = false
        while (i < line.length) {
            val c = line[i]
            when {
                isAsciiDigit(c) -> { sawDigit = true; i++ }
                c == ':' || c == '-' || c == '/' -> { sawSeparator = true; i++ }
                (c == '.' || c == ',' || c == 'T' || c == 'Z' || c == '+') && sawDigit -> i++
                c == ' ' && i + 1 < line.length && isAsciiDigit(line[i + 1]) && sawSeparator -> i++
                else -> break
            }
        }
        if (!sawDigit || !sawSeparator || i - digitsStart < 5) return 0
        if (i < line.length && line[i] == ']') i++
        return i
    }

    private fun tokenizeCode(line: String, lineComment: String?, jsonKeys: Boolean): List<SyntaxSpan> {
        val spans = mutableListOf<SyntaxSpan>()
        var i = 0
        while (i < line.length) {
            val c = line[i]
            if (lineComment != null && line.startsWith(lineComment, i)) {
                spans += SyntaxSpan(i, line.length - i, SyntaxKind.Comment)
                break
            }
            if (c == '"' || c == '\'' || (c == '`' && !jsonKeys)) {
                val end = stringEnd(line, i)
                val kind = if (jsonKeys && c == '"' && nextNonSpace(line, end) == ':') SyntaxKind.Key else SyntaxKind.String
                spans += SyntaxSpan(i, end - i, kind)
                i = end
                continue
            }
            if (isAsciiDigit(c) && (i == 0 || !isWordChar(line[i - 1]))) {
                var end = i
                while (end < line.length && (isAsciiHexDigit(line[end]) || line[end] in ".xX_")) end++
                if (end == line.length || !isWordChar(line[end])) {
                    spans += SyntaxSpan(i, end - i, SyntaxKind.Number)
                    i = end
                    continue
                }
            }
            if (jsonKeys && isAsciiLetter(c) && (i == 0 || !isWordChar(line[i - 1]))) {
                var end = i
                while (end < line.length && isAsciiLetter(line[end])) end++
                val word = line.substring(i, end)
                if (word == "true" || word == "false" || word == "null") spans += SyntaxSpan(i, end - i, SyntaxKind.Keyword)
                i = end
                continue
            }
            i++
        }
        return spans
    }

    private fun tokenizeXml(line: String): List<SyntaxSpan> {
        val spans = mutableListOf<SyntaxSpan>()
        var i = 0
        while (i < line.length) {
            if (line.startsWith("<!--", i)) {
                val close = line.indexOf("-->", i + 4)
                val end = if (close < 0) line.length else close + 3
                spans += SyntaxSpan(i, end - i, SyntaxKind.Comment)
                i = end
                continue
            }
            if (line[i] == '<') {
                var start = i + 1
                if (start < line.length && line[start] in "/?!") start++
                var end = start
                while (end < line.length && (isWordChar(line[end]) || line[end] in ":-.")) end++
                if (end > start) spans += SyntaxSpan(start, end - start, SyntaxKind.Key)
                i = end // never stalls: start is already past the '<'
                continue
            }
            if (line[i] == '"' || line[i] == '\'') {
                val end = stringEnd(line, i)
                spans += SyntaxSpan(i, end - i, SyntaxKind.String)
                i = end
                continue
            }
            i++
        }
        return spans
    }

    /** One past the closing quote of the string opening at [open], honouring backslash escapes; the line's end if unclosed. */
    private fun stringEnd(line: String, open: Int): Int {
        val quote = line[open]
        var i = open + 1
        while (i < line.length) {
            if (line[i] == '\\') { i += 2; continue }
            if (line[i] == quote) return i + 1
            i++
        }
        return line.length
    }

    private fun nextNonSpace(line: String, from: Int): Char {
        for (i in from until line.length) if (!line[i].isWhitespace()) return line[i]
        return '\u0000'
    }

    private fun isAsciiLetter(c: Char) = c in 'a'..'z' || c in 'A'..'Z'
    private fun isAsciiDigit(c: Char) = c in '0'..'9'
    private fun isAsciiHexDigit(c: Char) = isAsciiDigit(c) || c in 'a'..'f' || c in 'A'..'F'
    private fun isWordChar(c: Char) = isAsciiLetter(c) || isAsciiDigit(c) || c == '_'
}
