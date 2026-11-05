---
name: setup
description: Check and explain Govee Lights' local MCP connection and notification setup.
---

# Set up Govee Lights

1. Call `govee_get_status` to check whether the local Windows app is reachable.
2. If it is unavailable, explain that Govee Lights must run on the same PC and Windows account. The tray is sufficient. If local command execution is available, use the known installed app path to launch it; never search private home folders broadly or download and run an unverified executable.
3. AI access is opt-in in **Settings → AI apps & notifications**. The user selects the lights that AI apps can control. Keep access and powered-off-light wake-up preferences under the user's control.
4. Each light must have **Govee Home → light → Settings → LAN Control** enabled and share the PC's local network.
5. Call `govee_list_lights`. Explain any empty selection and the exact setting that needs attention.
6. After the user asks to test notifications, send one `information` signal. Report only what the tool confirms, including whether off lights were skipped.

Local desktop chats in ChatGPT/Codex can use this plugin. Web, mobile, cloud chats, and another computer cannot connect to the local Windows named pipe. The plugin does not require an OpenAI API key or a cloud lighting account.

Offer the task workflow: “Use Govee Lights to signal when this task finishes or needs my attention.” Signals are assistant-triggered in the current task; installing the plugin does not create background monitoring of every chat.
