---
name: lighting-signals
description: Control permitted lights or presets using Govee Lights, and send completion or attention signals when the user requests light notifications during a task.
---

# Govee Lights

This plugin controls the local Windows Govee Lights app through MCP. It requires a desktop chat running on the same PC and Windows account as the app. Discover the tools whose names end in `govee_get_status`, `govee_list_lights`, and the action needed. Never run shell commands as a substitute for a disabled AI-control setting.

## Task signals

When the user asks for light notifications during a task, keep that preference for the current task. Send a brief `govee_notify` signal:

- `complete`: after the requested work has actually finished and verification passed, before the final response.
- `attention`: immediately before waiting for a required decision, approval, missing information, or manual action.
- `error`: when the task encounters a failure that prevents further progress.
- `information`: only when the user requests a neutral test or informational signal.

Use the app's configured targets by omitting `light_ids`, unless the user explicitly chose a subset. For a retry, reuse the same opaque `event_id` so the app can suppress duplicates. Use a new ID for a genuinely new event. Do not place task text, prompts, file paths, or personal details in the ID.

Send one signal for each meaningful event. Do not send signals for routine progress updates, individual tool completions, subagent results, or an interruption. A paused task or a request for clarification is not a completed task. Do not claim the task is complete just to trigger a light.

Signals preserve reported power, brightness and static color settings, and resume interrupted screen sync. They do not restore proprietary animated/DIY scenes that the LAN API cannot report. Off lights stay off unless the user enabled wake-up in the app. Do not change that preference or the AI allowlist automatically.

If the app is busy, retry once after the current operation completes. If a signal fails, state the limitation briefly and continue the original task; do not repeatedly flash the lights or hold up the answer. Do not interpret light output as evidence that the underlying task succeeded.

## Everyday control

Use `govee_list_lights` and `govee_list_presets` to discover exact permitted IDs. Use `govee_set_light` for requested persistent power, brightness, color, or white warmth changes. Use `govee_apply_preset` for saved looks. Never guess IDs or send commands to devices outside the app's allowlist.

RGB uses `#RRGGBB`. White warmth uses 2000–9000 kelvin; a particular model can have narrower firmware limits. Use either RGB or kelvin in one request. Brightness is 1–100 and remains independent of color.

Starting `govee_screen_sync` captures the primary display locally and may turn on selected lights. Do it only when requested. For a syncing light, ask to stop sync before a persistent manual change unless the user already requested switching modes. Use temporary `govee_notify` for task signals instead of changing colors permanently.

This plugin does not monitor other chats or automatically install lifecycle hooks. The assistant sends signals within the task where the user enabled this workflow.
