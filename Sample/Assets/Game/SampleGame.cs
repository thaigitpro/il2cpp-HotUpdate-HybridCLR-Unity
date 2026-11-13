using System;
using UnityEngine;

namespace Sample
{
    /// <summary>Vào game: HotPatchKit gọi sau khi cài bản vá (cấu hình "entry": "Sample.GameMain.Start").</summary>
    public static class GameMain
    {
        public static void Start()
        {
            var go = new GameObject("SampleGame");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<SampleView>();
            Debug.Log("[sample] " + Greeter.Message());
        }
    }

    public class Greeter
    {
        public static string Message() => "v1 — mã của bản cài";
    }

    public class Counter
    {
        public int Value;

        public int Add(int n)
        {
            Value += n;
            return Value;
        }
    }

    public class SampleView : MonoBehaviour
    {
        string _text;
        float _t0;

        void Start()
        {
            _text = Greeter.Message();
            _t0 = Time.realtimeSinceStartup;
        }

        void Update()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-autoquit") >= 0 && Time.realtimeSinceStartup - _t0 > 2f) Application.Quit();
        }

        void OnGUI() => GUI.Label(new Rect(20, 20, 600, 40), _text);
    }
}
