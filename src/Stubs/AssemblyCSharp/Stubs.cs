// KSP Wingman · src/Stubs/AssemblyCSharp/Stubs.cs (copied from LUNA EVO Mission Control's AssemblyCSharp/Stubs.cs)
// [Patch] Stand-ins with the names and shapes of the few KSP types the patches use, so they build without KSP.
// Used by: LunaEvo.ClientPatches (compile time only; in the game, KSP's real Assembly-CSharp is used)
// Guide:   README.md (Building without KSP)

// Written by LUNA EVO, no Squad code: only names, and enum values read from KSP 1.12.5's metadata. The compiler bakes
// enum values in as numbers, so they must match KSP's exactly (checked in ClientPatchTests). Members are empty: they
// never run, the game's own ones do.

public enum GameScenes
{
    LOADING = 0,
    LOADINGBUFFER = 1,
    MAINMENU = 2,
    SETTINGS = 3,
    CREDITS = 4,
    SPACECENTER = 5,
    EDITOR = 6,
    FLIGHT = 7,
    TRACKSTATION = 8,
    PSYSTEM = 9,
    MISSIONBUILDER = 21,
}

[System.AttributeUsage(System.AttributeTargets.Class)]
public class KSPAddon : System.Attribute
{
    public enum Startup
    {
        Instantly = -2,
        MainMenu = 2,
        SpaceCentre = 5,
        Flight = 7,
    }

    public KSPAddon(Startup startup, bool once)
    {
    }
}

public class HighLogic : UnityEngine.MonoBehaviour
{
    public static GameScenes LoadedScene;

    public static Game CurrentGame => throw new System.NotSupportedException();

    public static UnityEngine.GUISkin Skin => throw new System.NotSupportedException();
}

namespace KSP.UI.Screens
{
    /// <summary>KSP's "Mission Summary" window after a recovery.</summary>
    public class MissionRecoveryDialog : UnityEngine.MonoBehaviour
    {
    }
}

public class Game
{
    public KerbalRoster CrewRoster;
}

public class KerbalRoster
{
    public int GetAvailableCrewCount() => throw new System.NotSupportedException();
}

public class Vessel : UnityEngine.MonoBehaviour
{
    /// <summary>True while the vessel is "on rails" (no physics, moved along its orbit).</summary>
    public bool packed;

    public System.Guid id;

    public string vesselName;

    public double latitude;

    public double longitude;

    public double altitude;

    public string GetDisplayName() => throw new System.NotSupportedException();
}

public class FlightGlobals : UnityEngine.MonoBehaviour
{
    public static bool ready;

    public static Vessel ActiveVessel => throw new System.NotSupportedException();

    public static System.Collections.Generic.List<Vessel> VesselsLoaded => throw new System.NotSupportedException();

    public static System.Collections.Generic.List<Vessel> Vessels => throw new System.NotSupportedException();

    public static bool SetActiveVessel(Vessel v) => throw new System.NotSupportedException();
}

public delegate void Callback();

public class EventVoid
{
    public delegate void OnEvent();

    public void Add(OnEvent evt) => throw new System.NotSupportedException();

    public void Remove(OnEvent evt) => throw new System.NotSupportedException();
}

public class GameEvents
{
    public static EventVoid onGUIApplicationLauncherReady;

    public static EventVoid onGUIApplicationLauncherDestroyed;
}

namespace KSP.Localization
{
    public class Localizer
    {
        public static string Format(string template) => throw new System.NotSupportedException();
    }
}

namespace KSP.UI.Screens
{
    public class ApplicationLauncher : UnityEngine.MonoBehaviour
    {
        public enum AppScenes
        {
            NEVER = 0,
            ALWAYS = -1,
            SPACECENTER = 1,
            FLIGHT = 2,
            MAPVIEW = 4,
            VAB = 8,
            SPH = 16,
            TRACKSTATION = 32,
            MAINMENU = 64,
        }

        public static bool Ready;

        public static ApplicationLauncher Instance => throw new System.NotSupportedException();

        public ApplicationLauncherButton AddModApplication(Callback onTrue, Callback onFalse, Callback onHover, Callback onHoverOut,
            Callback onEnable, Callback onDisable, AppScenes visibleInScenes, UnityEngine.Texture texture) =>
            throw new System.NotSupportedException();

        public void RemoveModApplication(ApplicationLauncherButton button) => throw new System.NotSupportedException();
    }

    public class ApplicationLauncherButton : UnityEngine.MonoBehaviour
    {
        public void SetTrue(bool makeCall) => throw new System.NotSupportedException();

        public void SetFalse(bool makeCall) => throw new System.NotSupportedException();
    }
}
