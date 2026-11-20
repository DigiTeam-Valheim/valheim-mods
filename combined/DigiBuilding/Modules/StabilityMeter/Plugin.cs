// Stability Meter (off by default): while the hammer is out, a small bar and percentage just below the crosshair show
// how stable the building piece you aim at will be once the game has finished working out its support, and turn red
// with "Will collapse" when it is going to come down. The game spreads support out a little at a time, so a new piece
// can look fine and fall a while later; this works out the end result at once (DigiShared/StabilityMeter.cs, shared
// with Ultimate Builder, whose own meter stays off while this one is on).
using BepInEx.Configuration;

namespace StabilityMeter
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.stabilitymeter";
        public const string Name = "Digi's Stability Meter";
        public const string Version = "1.0.0";

        private static ConfigEntry<bool> _showValue;
        private DigiShared.StabilityMeter _meter;

        private void Awake()
        {
            _showValue = Config.Bind("Stability Meter", "Show support value", false,
                "Also show the support numbers behind the percentage: the final support, the most this material can have, and " +
                "the least it needs to stand (below that it collapses).");
            DigiShared.StabilityMeter.Wanted = () => true;
            DigiShared.StabilityMeter.ShowValue = () => _showValue.Value;
            DigiShared.StabilityMeter.Patch(ModuleHarmony);
            _meter = gameObject.AddComponent<DigiShared.StabilityMeter>();
        }

        private void OnDestroy()
        {
            if (_meter != null) Destroy(_meter);
        }
    }
}
