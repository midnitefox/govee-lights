# Govee Lights desktop plugin

Control permitted lights, apply presets, manage screen sync, and send temporary AI task notifications through the Govee Lights Windows app.

Requires Windows 11, a running Govee Lights app under the same Windows account, and a local desktop chat. Use the app and plugin from the same release. No extra language runtime or API key is needed.

1. Enable **Govee Home → light → Settings → LAN Control** for each light, connect it to the PC's local network, and add it through **Lights → Find lights**.
2. In the app, open **Settings → AI apps & notifications**, enable access, and select allowed lights.
3. Add the `midnitefox/govee-lights` marketplace in Codex and install `govee-lights@midnitefox-govee`.
4. Reload the desktop app if the plugin is not yet visible, then use it in a new local chat.

Say: **“Use Govee Lights to signal when this task finishes or needs my attention.”** The assistant can also control power, brightness, RGB/white warmth, presets and screen sync.

Green means complete, amber attention, red error and blue information. Signals restore reported power, brightness and static colors, and resume interrupted sync or app-managed individual-member settings. Off lights stay off unless wake-up is enabled in the app. Cancellation restores lighting too. Proprietary animated and DIY scenes cannot be fully restored because their state is not reported by the LAN API.

The plugin has no background chat monitor or lifecycle hooks. Signals are sent in tasks where the user requests them. Cloud/web/mobile chats cannot connect to the local Windows controller.

The MCP executable communicates through an account-restricted Windows named pipe. It does not expose a network listener, accept arbitrary addresses, store task text or use cloud credentials.

Available controls depend on the light's model and firmware. See [device compatibility](https://github.com/midnitefox/govee-lights/blob/main/docs/COMPATIBILITY.md) and the [app download and setup instructions](https://github.com/midnitefox/govee-lights). MIT license.
