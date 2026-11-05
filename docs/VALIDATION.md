# Validation coverage

This document describes test coverage and its limits. Model-specific features are listed in the [compatibility guide](COMPATIBILITY.md).

## Automated coverage

| Area | Checks |
| --- | --- |
| GPU capture | Alternating split colors through direct and copied texture paths. |
| Scene processing | Letterbox noise, 350 ms geometry confirmation, immediate color cuts, dark and neutral suppression, continuous hue ranges, black preservation, and all zone layouts. |
| Capture recovery | Recovery deadlines persist across reopen attempts; only a fresh desktop frame establishes recovery. |
| Streaming | Model-specific mode-refresh defaults, immediate color replay after refresh, channel mapping, and group restoration. |
| Individual members | Independent color and intensity, preservation of the other member and device brightness, failure cleanup, removal, app exit, and saved compatibility settings. |
| Concurrent operations | Exclusive ownership during restoration, read-only AI status, notification interruption and restoration, and screen-sync takeover. |
| App controls | Live sliders, rooms, presets and rollback, discovery, shared replies, tray behavior, sign-in startup, and delayed-network recovery. |
| AI integration | Access restrictions, MCP connection and tool discovery, notifications and cancellation, and sync restoration. |

The tests use isolated UI and registry fixtures. Screen-sync notification restoration and multi-device scenarios have simulation coverage. Sign-in startup has isolated integration coverage; a complete Windows sign-out/sign-in cycle has not been validated.

## Hardware scope

Physical checks cover Windows 11 x64 and an H6056 set with six segments per bar. They cover separate-member colors, continuing color streaming without periodic mode refresh, primary-display capture, notification delivery, and restoration of reported power, brightness, and RGB or white-temperature state.

Hardware coverage does not establish identical behavior across firmware revisions or additional models. Screen-to-light optical latency has not been measured. UDP status and software timing cannot establish optical response time or the absence of every visible flash.

## Release verification

Release checks include app and plugin version consistency, package contents and checksums, public downloads, and screenshot links. An independent official MCP client checks the handshake, nine-tool discovery, and permitted-light status access. Screenshots are rendered from the application with example devices and documentation-only network addresses.

Protected content, network isolation, firmware behavior, and proprietary scenes that the LAN API cannot report remain practical limits. See [screen-sync processing](SCREEN-SYNC.md) for capture and output behavior.
