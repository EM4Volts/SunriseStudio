namespace SunriseStudio.Editor;

public sealed partial class MissionGraphPage
{
    /** Opens the existing New mission script dialog the next time this page draws. */
    public void RequestNewScript() => RequestSwitch(() => showNewScript = true);
}
