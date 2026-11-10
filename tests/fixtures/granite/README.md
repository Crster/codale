# Granite 4.2 chat-template goldens

Each `.txt` file is the exact prompt that the chat template embedded in the pinned
`granite-4.2-8b-Q4_K_M.gguf` (revision `93f3f6a8938ee922b784cf4e5b4203cd3428df8f`) renders with
`add_generation_prompt=True`. The settings match transformers' `apply_chat_template`:

- `trim_blocks` and `lstrip_blocks` on
- the `loopcontrols` extension
- `tojson` implemented as `json.dumps`

`GraniteTemplateTests` asserts that `SmartConversation.Render` reproduces every file byte for byte.

| File | Case |
|---|---|
| `plain_off` / `plain_full` / `plain_low` | system `S`, user `Hello`, with `enable_thinking` off, on, and on with `low_effort` |
| `tools_round_full` / `_low` / `_off` | one `read_file` tool, one call with reasoning `I should read it.`, then the tool response `{"text":"hi"}` |
| `tools_final_truncated` | the round above, an answer, then the final-answer user turn rendered with thinking off; earlier reasoning is truncated |

To regenerate, run this from a throwaway virtual environment that has `jinja2` installed:

```powershell
python -I tests/fixtures/granite/render.py <path-to>/granite-4.2-8b-Q4_K_M.gguf tests/fixtures/granite
```
