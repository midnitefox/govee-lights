# Govee Lights 1.0.0

Local lighting control, rooms, presets, screen sync, and optional AI task notifications for Windows 11.

## Downloads

- **Govee-Lights-v1.0.0-win-x64.zip** — the Windows app, capture library, MCP companion, quick-start guide, and MIT license. Extract the files together into a permanent folder and open **Govee Lights.exe**.
- **Govee-Lights-OpenAI-plugin-v1.0.0.zip** — the local desktop plugin with its bundled MCP server and setup/notification skills.
- **SHA256SUMS.txt** — checksums for both packages.

## Before discovery

Enable **Govee Home → each light → Settings → LAN Control**. The lights and PC must share a local network.

## Included

- Rooms, live brightness, a radial color wheel, a white warmth slider, and saved lighting presets.
- Selectable multi-light screen sync with GPU capture and regional color sampling.
- Individual-member controls and segmented layouts for compatible sets, with a live preview, side swap, and vertical flips.
- Comfort processing and optional warm/cool ranges, with stable sampling and no queued fades.
- Tray operation and optional Windows sign-in startup with automatic sync resume.
- Clear LAN setup and troubleshooting guidance.
- Local AI controls and temporary completion, attention, error, and information signals with lighting restoration.

## AI integration

Enable **Settings → AI apps & notifications** and select the lights AI apps may control. The app and AI client must run on the same Windows PC under the same account. The plugin supports local desktop chats and sends notifications only when requested in a task. Web, mobile, and cloud-only chats cannot connect to the local controller.

## Compatibility and validation

Available controls depend on the model, firmware, and bundled device catalog. Individual-member controls and segmented layouts have model-specific limits; other streaming-capable models use a single color from their assigned screen region. See the [compatibility guide](https://github.com/midnitefox/govee-lights/blob/main/docs/COMPATIBILITY.md) before choosing a layout.

Presets and notifications restore the static settings reported by the LAN API. Proprietary animated and DIY scenes cannot be restored in full. The Windows build is unsigned. [Validation coverage](https://github.com/midnitefox/govee-lights/blob/main/docs/VALIDATION.md) distinguishes automated checks from physical hardware coverage.

[View screenshots and setup instructions](https://github.com/midnitefox/govee-lights).
