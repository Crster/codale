"""Render Granite 4.2 chat-template goldens straight from the GGUF's embedded template.

Usage: python -I render.py <model.gguf> <output-dir>
Environment matches transformers' apply_chat_template (trim_blocks, lstrip_blocks, loopcontrols, json.dumps tojson).
"""
import json
import struct
import sys

import jinja2
import jinja2.ext
from jinja2.sandbox import ImmutableSandboxedEnvironment


def chat_template(path):
    f = open(path, "rb")

    def r(fmt):
        size = struct.calcsize(fmt)
        return struct.unpack("<" + fmt, f.read(size))[0]

    def rs():
        return f.read(r("Q")).decode("utf-8")

    sizes = {0: "B", 1: "b", 2: "H", 3: "h", 4: "I", 5: "i", 6: "f", 7: "?", 10: "Q", 11: "q", 12: "d"}

    def value(kind):
        if kind == 8:
            return rs()
        if kind == 9:
            inner, count = r("I"), r("Q")
            return [rs() if inner == 8 else r(sizes[inner]) for _ in range(count)]
        return r(sizes[kind])

    assert f.read(4) == b"GGUF"
    r("I"); r("Q"); count = r("Q")
    for _ in range(count):
        key = rs(); kind = r("I"); v = value(kind)
        if key == "tokenizer.chat_template":
            return v
    raise SystemExit("no chat template")


def tojson(x, ensure_ascii=False, indent=None, separators=None, sort_keys=False):
    return json.dumps(x, ensure_ascii=ensure_ascii, indent=indent, separators=separators, sort_keys=sort_keys)


TOOL = {"name": "read_file", "description": "Read a file.",
        "parameters": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"], "additionalProperties": False}}
CALL = {"name": "read_file", "arguments": {"path": "a.txt"}}
FINAL = "Using the observations above, return the final answer for this task in exactly the format the system message requires. Do not call tools or reason further. Task:\nRead a.txt"

CASES = {
    "plain_off": dict(messages=[{"role": "system", "content": "S"}, {"role": "user", "content": "Hello"}], enable_thinking=False),
    "plain_full": dict(messages=[{"role": "system", "content": "S"}, {"role": "user", "content": "Hello"}], enable_thinking=True),
    "plain_low": dict(messages=[{"role": "system", "content": "S"}, {"role": "user", "content": "Hello"}], enable_thinking=True, low_effort=True),
    "tools_round_full": dict(messages=[
        {"role": "system", "content": "S"}, {"role": "user", "content": "Read a.txt"},
        {"role": "assistant", "reasoning_content": "I should read it.", "content": "", "tool_calls": [{"type": "function", "function": CALL}]},
        {"role": "tool", "content": "{\"text\":\"hi\"}"}], tools=[{"type": "function", "function": TOOL}], enable_thinking=True),
    "tools_round_low": dict(messages=[
        {"role": "system", "content": "S"}, {"role": "user", "content": "Read a.txt"},
        {"role": "assistant", "reasoning_content": "I should read it.", "content": "", "tool_calls": [{"type": "function", "function": CALL}]},
        {"role": "tool", "content": "{\"text\":\"hi\"}"}], tools=[{"type": "function", "function": TOOL}], enable_thinking=True, low_effort=True),
    "tools_round_off": dict(messages=[
        {"role": "system", "content": "S"}, {"role": "user", "content": "Read a.txt"},
        {"role": "assistant", "content": "", "tool_calls": [{"type": "function", "function": CALL}]},
        {"role": "tool", "content": "{\"text\":\"hi\"}"}], tools=[{"type": "function", "function": TOOL}], enable_thinking=False),
    "tools_final_truncated": dict(messages=[
        {"role": "system", "content": "S"}, {"role": "user", "content": "Read a.txt"},
        {"role": "assistant", "reasoning_content": "I should read it.", "content": "", "tool_calls": [{"type": "function", "function": CALL}]},
        {"role": "tool", "content": "{\"text\":\"hi\"}"},
        {"role": "assistant", "content": "<think>\nIt says hi.\n</think>\n\nThe file says hi."},
        {"role": "user", "content": FINAL}], tools=[{"type": "function", "function": TOOL}], enable_thinking=False),
}


def main():
    source = chat_template(sys.argv[1])
    env = ImmutableSandboxedEnvironment(trim_blocks=True, lstrip_blocks=True, extensions=[jinja2.ext.loopcontrols])
    env.filters["tojson"] = tojson
    template = env.from_string(source)
    for name, case in CASES.items():
        text = template.render(add_generation_prompt=True, **case)
        with open(f"{sys.argv[2]}/{name}.txt", "w", encoding="utf-8", newline="") as out:
            out.write(text)
        print(name, len(text))


main()
