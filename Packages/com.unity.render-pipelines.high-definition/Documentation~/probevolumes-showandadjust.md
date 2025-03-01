# Display and adjust Probe Volumes

You can use the Rendering Debugger to see how HDRP places Light Probes in a Probe Volume, then use Probe Volume settings to configure the layout.

## Display Probe Volumes

To display Probe Volumes, open the [Rendering Debugger](rendering-debugger-window-reference.md#ProbeVolume).

You can display the following:

- Enable **Display Probes** to display the locations of Light Probes.
- Enable **Display Bricks** to display the outlines of groups of Light Probes ('bricks'). See [Understand Probe Volumes](probevolumes-concept.md#brick-size-and-light-probe-density) for more information on bricks.
- Enable **Display Cells** to display the outlines of cells, which are the units that [streaming](probevolumes-streaming.md) uses.
- Enable **Debug Probe Sampling** to inspect details about Probe Volume sampling at a given world position.

To update the location of Light Probes, bricks, and cells automatically when you change settings, enable **Realtime Update**.

![](Images/probevolumes-debug-displayprobes.PNG)<br/>
The Rendering Debugger with **Display Probes** enabled.

![](Images/probevolumes-debug-displayprobebricks1.PNG)<br/>
The Rendering Debugger with **Display Bricks** enabled.

![](Images/probevolumes-debug-displayprobecells.PNG)<br/>
The Rendering Debugger with **Display Cells** enabled.

![](Images/APVsamplingDebug.png)<br/>
The Rendering Debugger with **Debug Probe Sampling** enabled

## Adjust

### Adjust Probe Volume size

To achieve the highest quality lighting, you should enable **Global** in the Probe Volume Inspector, so the Probe Volume covers the entire Scene.

You can also do the following in a Probe Volume Inspector to set the size of a Probe Volume:

- Disable **Global** and set the size manually.
- Disable **Global** and select **Fit to all Scenes**, **Fit to Scene** or **Fit to Selection**. See [Probe Volume Inspector properties](probevolumes-settings.md#probe-volume-properties) for more information.
- Select **Override Renderer Filter**, then select which layers HDRP considers when it generates Light Probe positions. For more information about Layers, see [Layers and Layer Masks](https://docs.unity3d.com/Manual/layers-and-layermasks.html).

You can overlap multiple Probe Volumes in one Scene or Baking Set.

### Adjust Light Probe density

If your Scene includes areas of detailed geometry, you might need to increase Light Probe density in these areas to achieve a good lighting result.

You can use the following to adjust Light Probe density across a whole Probe Volume:

- In the [Baking Set properties](probevolumes-settings.md#pv-tab), set the **Min Probe Spacing** and **Max Probe Spacing** - which affects all the Scenes and Probe Volumes in the Set.
- In a [Probe Volume's Inspector](probevolumes-settings.md#probe-volume-properties), adjust the **Override Probe Spacing** slider - which affects only the Probe Volume, and overrides the settings set in the **Baking Set**.

Note: In the Inspector for a Probe Volume, the values in **Override Probe Spacing** can't exceed the **Min Probe Spacing** or the **Max Probe Spacing** in the **Baking Set Settings**.

If you increase Light Probe density, you might increase bake time and how much disk space your Probe Volumes use.

### Use multiple Probe Volumes

You can use multiple Probe Volumes to control Light Probe density in more detail across a Scene or Baking Set. For example:

1. In the [Baking Set properties](probevolumes-settings.md#pv-tab), set the probe spacing to between 1 meters and 27 meters.
2. To cover empty areas, add another Probe Volume, enable **Global**, and override the probe spacing to between 9 meters and 27 meters.
3. To cover a smaller high-detail area, add another Probe Volume, disable **Global**, set a smaller size, and override the probe spacing to between 1 meters and 9 meters.

### Terrain

Because terrain is detailed but less important to you than your main scenery or characters, you can do the following:

1. Put terrain on its own [Layer](https://docs.unity3d.com/Manual/layers-and-layermasks.html).
2. Surround the terrain with a Probe Volume.
3. In the Inspector for the Probe Volume, enable **Override Renderer Filters**, then in **Layer Mask** select only your terrain Layer.
4. To adjust Light Probe density to capture more or less lighting detail, enable **Override Probe Spacing** and adjust the slider values.

## Additional resources

* [Rendering Debugger](rendering-debugger-window-reference.md#probe-volume)
* [Probe Volumes settings and properties](probevolumes-settings.md)
