// Minimal UnityEngine stand-ins so the library compiles and runs in a plain .NET test (no game, no Unity).
// Only what otc_bepinex touches. KeyCode values match Unity's.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public enum KeyCode
    {
        None = 0, Backspace = 8, Tab = 9, Return = 13, Escape = 27, Space = 32,
        Alpha0 = 48, Alpha1 = 49, Alpha2 = 50, Alpha3 = 51, Alpha4 = 52, Alpha5 = 53, Alpha6 = 54, Alpha7 = 55, Alpha8 = 56, Alpha9 = 57,
        A = 97, B = 98, C = 99, D = 100, E = 101, F = 102, G = 103,
        Keypad0 = 256, Keypad1 = 257, Keypad2 = 258, Keypad3 = 259, Keypad4 = 260, Keypad5 = 261, Keypad6 = 262,
        Keypad7 = 263, Keypad8 = 264, Keypad9 = 265, KeypadPeriod = 266, KeypadDivide = 267, KeypadMultiply = 268,
        KeypadMinus = 269, KeypadPlus = 270, KeypadEnter = 271,
        UpArrow = 273, Insert = 277, Home = 278, PageUp = 280,
        F1 = 282, F2 = 283, F3 = 284, F4 = 285, F5 = 286, F6 = 287, F7 = 288, F8 = 289, F9 = 290, F10 = 291, F11 = 292, F12 = 293,
    }

    public static class Input
    {
        public static readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
        public static bool Throw;
        public static bool GetKey(KeyCode k)
        {
            if (Throw) throw new InvalidOperationException("input stub broken");
            return Held.Contains(k);
        }
    }

    public static class Time
    {
        public static float unscaledTime;
    }

    public class Object { }

    public class Component : Object
    {
        public GameObject gameObject { get; internal set; }
    }

    public class Behaviour : Component { }

    public class MonoBehaviour : Behaviour { }

    public class GameObject : Object
    {
        public readonly List<Component> Components = new List<Component>();
        public T AddComponent<T>() where T : Component
        {
            var c = (T)Activator.CreateInstance(typeof(T), true);
            c.gameObject = this;
            Components.Add(c);
            return c;
        }
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
    }

    public static class Screen
    {
        public static int width = 1920, height = 1080;
    }

    public enum TextAnchor { UpperLeft, MiddleCenter }

    public class GUIStyle
    {
        public int fontSize;
        public TextAnchor alignment;
    }

    public class GUISkin
    {
        public GUIStyle label = new GUIStyle();
    }

    public static class GUI
    {
        public static bool Throw;
        public static int Boxes, Labels;
        public static string LastLabel;
        public static GUISkin skin = new GUISkin();
        public static void Box(Rect r, string text)
        {
            if (Throw) throw new MissingMethodException("GUI.Box stripped");
            Boxes++;
        }
        public static void Label(Rect r, string text, GUIStyle style) { Labels++; LastLabel = text; }
    }

    public static class Mathf
    {
        public static float Max(float a, float b) => a > b ? a : b;
    }
}
