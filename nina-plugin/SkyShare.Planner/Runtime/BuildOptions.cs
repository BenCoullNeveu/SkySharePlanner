namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Site settings for the blocks the plugin generates. Edited on "SkyShare: Load Plan"
    /// (saved in the template) and handed to the Targets / Flats containers via PlanStore.
    /// </summary>
    /// <param name="MinAltitude">Stop a target's exposures when it drops below this altitude (degrees).</param>
    /// <param name="AutofocusAtStart">Run autofocus after centering each target.</param>
    /// <param name="DitherEvery">Dither after this many frames of each filter. 0 = never.</param>
    public sealed record BuildOptions(double MinAltitude = 20, bool AutofocusAtStart = true, int DitherEvery = 1);
}
