using System;
using System.Collections;
using UnityEngine;

namespace HotPatchKit
{
    /// <summary>
    /// Khởi động tự động (cấu hình <c>autoBoot: true</c>): sau khi cảnh đầu nạp xong, kiểm / tải / cài bản vá (<see cref="HotPatchLoader"/>)
    /// rồi gọi hàm vào game (<c>entry</c>). Cảnh đầu KHÔNG được chứa component của assembly mã game: mã game phải bắt đầu từ entry,
    /// sau khi bản vá đã cài (component có sẵn trong cảnh chạy Awake / Start trước khi kịp cài).
    /// Trong editor / bản không vá nóng: gọi entry ngay.
    /// </summary>
    public sealed class HotPatchBoot : MonoBehaviour
    {
        const float ShowUiAfter = 0.4f;
        float _start;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBoot()
        {
            var cfg = HotPatchConfig.Load();
            if (cfg == null || !cfg.autoBoot) return;
            var go = new GameObject("HotPatchBoot");
            DontDestroyOnLoad(go);
            go.AddComponent<HotPatchBoot>();
        }

        IEnumerator Start()
        {
            _start = Time.realtimeSinceStartup;
            yield return HotPatchLoader.Run();
            Destroy(gameObject);
            try
            {
                HotPatchLoader.InvokeEntry();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void OnGUI()
        {
            var s = HotPatchLoader.Status;
            if (string.IsNullOrEmpty(s) || Time.realtimeSinceStartup - _start < ShowUiAfter) return;
            var p = HotPatchLoader.Progress;
            GUI.Label(new Rect(16, Screen.height - 48, Screen.width - 32, 32), p >= 0 ? s + $"  {p * 100:0}%" : s);
        }
    }
}
