// KSP Wingman · src/Stubs/ (copied from LUNA EVO Mission Control: UnityCoreModule/Stubs.cs)
// [Patch] Stand-ins with the names and shapes of the few Unity types the patches use, so they build without KSP.
// Used by: LunaEvo.ClientPatches (compile time only; in the game, Unity's real UnityEngine.CoreModule is used)
// Guide:   README.md (Building without KSP)

// Written by LUNA EVO, no Unity code: the class chain MonoBehaviour derives from, the methods and value types the patches
// use (Color and Vector2 with Unity's own fields, as they're copied by value), with Unity's own signatures, so the compiled calls bind to the real ones in the game. Bodies never run.

namespace UnityEngine
{
    public class Object
    {
        public static void DontDestroyOnLoad(Object target) => throw new System.NotSupportedException();

        public static void Destroy(Object obj) => throw new System.NotSupportedException();
    }

    public class Component : Object
    {
    }

    public class Behaviour : Component
    {
    }

    public class MonoBehaviour : Behaviour
    {
    }

    public static class Time
    {
        public static float fixedDeltaTime => throw new System.NotSupportedException();

        public static float timeScale { get => throw new System.NotSupportedException(); set => throw new System.NotSupportedException(); }
    }

    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public Color(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
    }

    public struct Vector2
    {
        public float x;
        public float y;
    }

    public struct Rect
    {
        public Rect(float x, float y, float width, float height) => throw new System.NotSupportedException();
    }

    public class Texture : Object
    {
    }

    public class Texture2D : Texture
    {
        public Texture2D(int width, int height) => throw new System.NotSupportedException();

        public void SetPixel(int x, int y, Color color) => throw new System.NotSupportedException();

        public void Apply() => throw new System.NotSupportedException();
    }

    public class Screen
    {
        public static int width => throw new System.NotSupportedException();

        public static int height => throw new System.NotSupportedException();
    }

    public static class Debug
    {
        public static void Log(object message) => throw new System.NotSupportedException();

        public static void LogError(object message) => throw new System.NotSupportedException();
    }
}
