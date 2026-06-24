"""Extract the EXACT SystemPrompt + EnvelopeGrammar from MistralChatbotService.cs.

The system prompt is a chain of C# string literals concatenated with `+`. We scan
the assignment region, pull every double-quoted literal (honouring \\" / \\\\ escapes
and skipping // comments), then C#-unescape and join. This guarantees the harness
fires the byte-identical prompt the app ships, and survives prompt edits.
"""
import re
import sys
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
CS = ROOT / "src" / "SenseEase.Infrastructure" / "Real" / "LocalLlmChatbotService.cs"
OUT = pathlib.Path(__file__).resolve().parent


def extract_concat_string(src: str, decl_regex: str) -> str:
    m = re.search(decl_regex, src)
    if not m:
        raise SystemExit(f"declaration not found: {decl_regex}")
    i = m.end()
    n = len(src)
    in_str = False
    buf = []
    literals = []
    while i < n:
        c = src[i]
        if in_str:
            if c == "\\":
                buf.append(c)
                buf.append(src[i + 1])
                i += 2
                continue
            if c == '"':
                in_str = False
                literals.append("".join(buf))
                buf = []
                i += 1
                continue
            buf.append(c)
            i += 1
            continue
        # not in string
        if c == '"':
            in_str = True
            i += 1
            continue
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            # skip line comment
            j = src.find("\n", i)
            i = n if j < 0 else j + 1
            continue
        if c == ";":
            break
        i += 1
    return "".join(_unescape(lit) for lit in literals)


def _unescape(s: str) -> str:
    out = []
    j = 0
    mapping = {
        "n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\",
        "/": "/", "b": "\b", "f": "\f", "0": "\0", "a": "\a", "v": "\v",
    }
    while j < len(s):
        ch = s[j]
        if ch == "\\":
            e = s[j + 1]
            if e == "u":
                out.append(chr(int(s[j + 2:j + 6], 16)))
                j += 6
                continue
            out.append(mapping.get(e, e))
            j += 2
            continue
        out.append(ch)
        j += 1
    return "".join(out)


def extract_raw_string(src: str, decl_regex: str) -> str:
    """Extract a C# raw string literal opened with four double-quotes (\"\"\"\")."""
    m = re.search(decl_regex, src)
    if not m:
        raise SystemExit(f"declaration not found: {decl_regex}")
    rest = src[m.end():]
    open_idx = rest.find('""""')
    body = rest[open_idx + 4:]
    close_idx = body.find('""""')
    raw = body[:close_idx]
    # strip the opening newline and the closing-delimiter indentation
    lines = raw.split("\n")
    if lines and lines[0].strip() == "":
        lines = lines[1:]
    # closing delimiter indent = whitespace before the """" on the closing line
    close_line = body[close_idx - 40:close_idx].split("\n")[-1]
    indent = len(close_line) - len(close_line.lstrip(" "))
    stripped = [ln[indent:] if ln[:indent].strip() == "" else ln for ln in lines]
    return "\n".join(stripped).rstrip("\n") + "\n"


def main():
    src = CS.read_text(encoding="utf-8")
    prompt = extract_concat_string(src, r"private const string SystemPrompt\s*=")
    grammar = extract_raw_string(src, r"const string EnvelopeGrammar\s*=")

    (OUT / "system_prompt.txt").write_text(prompt, encoding="utf-8")
    (OUT / "grammar.gbnf").write_text(grammar, encoding="utf-8")

    # sanity checks
    assert prompt.startswith("You are SenseEase's comfort companion"), prompt[:60]
    assert "SYMPTOM-STATE BOUNDARY" in prompt
    assert "feather_width_delta" not in grammar, "grammar should NOT contain feather"
    assert "intent ::=" in grammar
    assert "ReportDiscomfort" in grammar
    print(f"system_prompt.txt: {len(prompt)} chars, {prompt.count(chr(10))+1} lines")
    print(f"grammar.gbnf: {len(grammar)} chars")
    print("OK")


if __name__ == "__main__":
    main()
