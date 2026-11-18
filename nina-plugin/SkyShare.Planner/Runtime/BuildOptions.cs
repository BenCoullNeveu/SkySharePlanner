namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Site settings for the blocks the plugin generates. Edited on "SkyShare: Load Plan"
    /// (saved in the template) and handed to the Targets / Flats containers via PlanStore.
    /// </summary>
    /// <param name="MinAltitude">Stop a target's exposures when it drops below this altitude (degrees).</param>
    /// <param name="AutofocusAtStart">Run autofocus after centering each target.</param>
    /// <param name="DitherEvery">Fixed-count (non-cycle) targets: dither after this many frames of each
    /// filter, 0 = never. Cycle targets ignore it: they always dither once per cycle.</param>
    /// <param name="CenterDriftArcMinutes">Center After Drift on every target: re-center when the
    /// plate-solved position has drifted more than this many arcminutes from the target.</param>
    public sealed record BuildOptions(double MinAltitude = 20, bool AutofocusAtStart = true, int DitherEvery = 1,
                                      double CenterDriftArcMinutes = 5);
}
