// KSP Wingman · src/Stubs/UnityIMGUIModule/Stubs.cs (copied from LUNA EVO Mission Control's UnityIMGUIModule/Stubs.cs)
// [Patch] Stand-ins with the names and shapes of the Unity window-drawing calls the in-game LUNA EVO window uses.
// Used by: LunaEvo.ClientPatches (compile time only; in the game, Unity's real UnityEngine.IMGUIModule is used)
// Guide:   README.md (Building without KSP)

// Written by LUNA EVO, no Unity code: only the members EvoWindow calls, with Unity's own signatures (checked against
// the test rig's real assembly in ClientPatchTests), so the compiled calls bind to the real ones. Bodies never run.

namespace UnityEngine
{
    public class GUI
    {
        public delegate void WindowFunction(int id);

        public static bool enabled { get => throw new System.NotSupportedException(); set => throw new System.NotSupportedException(); }

        public static GUISkin skin { get => throw new System.NotSupportedException(); set => throw new System.NotSupportedException(); }

        public static Color color { get => throw new System.NotSupportedException(); set => throw new System.NotSupportedException(); }

        public static void DragWindow() => throw new System.NotSupportedException();
    }

    public sealed class GUILayoutOption
    {
    }

    public sealed class GUISkin : Object
    {
    }

    public class GUILayout
    {
        public static Rect Window(int id, Rect screenRect, GUI.WindowFunction func, string text, params GUILayoutOption[] options) =>
            throw new System.NotSupportedException();

        public static void Label(string text, params GUILayoutOption[] options) => throw new System.NotSupportedException();

        public static bool Button(string text, params GUILayoutOption[] options) => throw new System.NotSupportedException();

        public static bool Toggle(bool value, string text, params GUILayoutOption[] options) => throw new System.NotSupportedException();

        public static void BeginHorizontal(params GUILayoutOption[] options) => throw new System.NotSupportedException();

        public static void EndHorizontal() => throw new System.NotSupportedException();

        public static void BeginVertical(params GUILayoutOption[] options) => throw new System.NotSupportedException();

        public static void EndVertical() => throw new System.NotSupportedException();

        public static Vector2 BeginScrollView(Vector2 scrollPosition, params GUILayoutOption[] options) => throw new System.NotSupportedException();

        public static void EndScrollView() => throw new System.NotSupportedException();

        public static void Space(float pixels) => throw new System.NotSupportedException();

        public static void FlexibleSpace() => throw new System.NotSupportedException();

        public static GUILayoutOption Width(float width) => throw new System.NotSupportedException();

        public static GUILayoutOption Height(float height) => throw new System.NotSupportedException();
    }
}
