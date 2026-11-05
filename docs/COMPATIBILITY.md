# Device compatibility

Govee Lights supports local control of compatible Govee lighting devices on Windows 11 x64. Available features depend on the model, firmware, and the application's bundled device catalog.

## Connection requirements

- Enable **Govee Home → light → Settings → LAN Control** for every device. Models without this setting cannot connect to the app.
- Connect the lights and PC to the same local network. Network isolation or multicast filtering can prevent discovery.
- The model must appear in the bundled catalog. Screen-sync eligibility follows the catalog's local streaming flag.

The catalog identifies available capabilities; it is not a list of hardware independently tested with every firmware version.

## Feature support

| Feature | Requirements and limits |
| --- | --- |
| Rooms and saved lights | Devices discovered through LAN Control and recognized by the catalog. |
| Power, brightness, RGB color, white warmth | Available according to device capabilities. The white slider covers the LAN command range of 2000–9000 K; firmware may accept a narrower range. |
| Presets | Save reported power, brightness, RGB color, and white temperature. Proprietary animated and DIY scenes cannot be captured in full. |
| Whole-device screen sync | Requires a model marked as streaming-capable in the catalog. Choose the whole primary display or a left, right, top, or bottom region. |
| Individual-member controls and segmented screen sync | Currently implemented for H6056 paired light bars only. Other models use whole-device controls and, where available, whole-device streaming. |
| AI control and notifications | Requires local AI access to be enabled and each target light to be allowed in Settings. |

## Individual members and sampling zones

For an H6056 set, **Light controls** offers **Both bars**, **Bar 1**, and **Bar 2**. The app shows how the hardware numbers map to the saved screen layout. Choose the hardware layout that matches the set:

| Segments per bar | Available sampled zones per bar | Physical mapping |
| --- | --- | --- |
| 6 | 1, 2, 3, or 6 | Each sampled zone drives 6, 3, 2, or 1 segments, respectively. |
| 3 | 1, 2, or 3 | One zone covers all segments; two zones divide them into groups of two and one; three zones map individually. |

Left/right swap and vertical flips change the mapping between screen regions and physical bars. The preview shows the resulting sampling layout.

Individual RGB colors and relative intensity use an app-managed stream. An intensity of 0% makes that member dark without changing the set's overall brightness. Power and white temperature remain shared across the set.

Individual control continues in the tray. **Release bars** or app exit restores the reported settings from before the stream. A whole-set command releases the stream before applying the new command. Screen sync also takes ownership of any selected set. AI notifications temporarily pause and then resume individual settings.

When individual control begins from a white-temperature setting, the untouched member starts at neutral RGB white. Release restores the original white-temperature setting. Save presets after releasing individual control: the LAN status API cannot report these individual streaming colors.

## Streaming compatibility

Streaming defaults vary by model:

- **H6056:** streaming mode is enabled once, followed by continuing color updates. Periodic mode refresh is disabled by default. A per-set firmware compatibility option enables it when required by the device.
- **Other streaming-capable models:** periodic mode refresh remains enabled. The app immediately resends the current colors after a refresh.

Keep the default unless the device stops maintaining streaming output. Changing firmware compatibility settings does not expand the model's supported controls or segment mapping.

## Hardware coverage

Physical validation covers an H6056 with six segments per bar on Windows 11 x64. The three-segment mapping, additional models, and multi-device behavior have automated simulation coverage; physical behavior on those configurations remains unverified. Support can vary across firmware revisions.

See [validation coverage](VALIDATION.md) for the tested areas and limits. Software timing and UDP status replies do not measure screen-to-light optical latency.

[Setup and troubleshooting](../README.md#if-a-light-is-missing) · [Screen-sync processing](SCREEN-SYNC.md)
