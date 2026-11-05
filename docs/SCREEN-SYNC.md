# Screen-sync processing

Screen sync processes the Windows primary display locally and sends colors to the selected lights. Each light can use the whole display or an assigned region. Segmented layouts are available for the models listed in the [compatibility guide](COMPATIBILITY.md).

## Capture and sampling

DirectX Desktop Duplication captures the display and reduces it to a small sample grid on the GPU. Capture targets 60 samples per second. Pointer-only updates reuse the current image.

Black-border geometry must remain consistent for 350 ms before the sampling regions change. Colors within the current regions continue updating during that interval. This prevents small near-black fluctuations from repeatedly moving the sampling boundaries.

## Color selection and Comfort

Continuous dominant-hue weighting and linear-light averaging produce representative colors without abruptly switching between competing hue groups. An Oklab perceptual threshold suppresses small color fluctuations, with stronger suppression around dark and neutral colors. Meaningful changes proceed on the next eligible update.

**Comfort** applies perceptual compression to bright peaks and strong saturation within the current frame. It ranges from 0 to 100 and defaults to 35. At 0 with **Full spectrum**, the sampled colors pass through without this treatment. **Warm range** and **Cool range** continuously map chromatic hues into narrower ranges while preserving neutral colors. The preview includes the selected treatment.

These controls use no temporal color averaging or queued fades. A real scene cut or flash can still produce an immediate lighting change. Color processing preserves the separate device brightness setting.

## Sending updates

The newest frame takes priority. Continuous updates are capped at 30 per second per device, with a lower rate when device replies slow down. Unchanged output is refreshed at 250 ms intervals to keep the stream active.

Mode-refresh behavior follows the [model's compatibility setting](COMPATIBILITY.md#streaming-compatibility). Whenever streaming mode is re-enabled, the current color packet is immediately resent and the next fresh update is forced.

## Capture recovery

Capture failure holds the last valid image for up to 500 ms while recovery is attempted. Only a fresh desktop frame establishes successful recovery. If recovery fails, the app uses GDI capture for the remainder of that sync session, avoiding repeated changes between capture methods.

Protected video and secure Windows desktops can restrict capture. Actual screen-to-light latency includes the display pipeline, capture, Wi-Fi delivery, and light firmware; capture or send timings alone do not measure it.

[Screen-sync setup](../README.md#screen-sync) · [Validation coverage](VALIDATION.md)
