using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ParentAudioSettings：親機の BGM・SE 音量を保存し、親機の AudioSource へ適用する。
///
/// 責務の境界：
///  ・保存キーは親機専用（ParentBgmVolume / ParentSeVolume）。子機の保存仕様には触れない。
///  ・既定値は子機の既定（1.0）に合わせる（未保存時・Reset時）。
///  ・各音の「基準音量」（ParentAudioVolumeTarget が捕捉）に倍率を掛ける。現在値へ掛けて累積しない。
///  ・シーン遷移後・後から生成された音にも反映する（シーンロードで再適用）。
///  ・Mixerルーティング・3D設定・pitch には触れない。
///
/// 生成：
///  ・RuntimeInitializeOnLoadMethod で1つだけ生成する（シーンに置かなくても動く）。
///  ・重複生成は Awake で自壊する。
/// </summary>
public class ParentAudioSettings : MonoBehaviour
{
    private const string KeyBgm = "ParentBgmVolume";
    private const string KeySe  = "ParentSeVolume";

    /// <summary>未保存時・Reset時の既定値（子機の既定に合わせる）。</summary>
    public const float DefaultVolume = 1f;

    public static ParentAudioSettings Instance { get; private set; }

    /// <summary>音量が変化したことを通知する（フェード側などが倍率を読み直すため）。</summary>
    public static event Action Changed;

    private static readonly List<ParentAudioVolumeTarget> _targets = new List<ParentAudioVolumeTarget>();

    public static float BgmVolume => Instance != null ? Instance._bgm : DefaultVolume;
    public static float SeVolume  => Instance != null ? Instance._se  : DefaultVolume;

    private float _bgm = DefaultVolume;
    private float _se  = DefaultVolume;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("ParentAudioSettings");
        DontDestroyOnLoad(go);
        go.AddComponent<ParentAudioSettings>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        // 保存値を有効範囲（0〜1）に収めて読み出す。
        _bgm = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyBgm, DefaultVolume));
        _se  = Mathf.Clamp01(PlayerPrefs.GetFloat(KeySe,  DefaultVolume));

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Instance = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // 新しいシーンの音にも反映する（各ターゲットは OnEnable でも自身を適用する）。
        ApplyAll();
    }

    public void SetBgmVolume(float value)
    {
        _bgm = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(KeyBgm, _bgm);
        PlayerPrefs.Save();
        ApplyAll();
    }

    public void SetSeVolume(float value)
    {
        _se = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(KeySe, _se);
        PlayerPrefs.Save();
        ApplyAll();
    }

    /// <summary>既定値（子機と同じ 1.0）へ戻し、その結果も保存する。</summary>
    public void ResetToDefaults()
    {
        _bgm = DefaultVolume;
        _se  = DefaultVolume;
        PlayerPrefs.SetFloat(KeyBgm, _bgm);
        PlayerPrefs.SetFloat(KeySe,  _se);
        PlayerPrefs.Save();
        ApplyAll();
    }

    /// <summary>演出用の音量（設定倍率を掛ける前）に SE 設定倍率を掛けた最終音量を返す。</summary>
    public static float ApplySe(float stagingVolume) => Mathf.Clamp01(stagingVolume * SeVolume);

    /// <summary>演出用の音量に BGM 設定倍率を掛けた最終音量を返す。</summary>
    public static float ApplyBgm(float stagingVolume) => Mathf.Clamp01(stagingVolume * BgmVolume);

    public static void Register(ParentAudioVolumeTarget target)
    {
        if (target == null) return;
        if (!_targets.Contains(target)) _targets.Add(target);
    }

    public static void Unregister(ParentAudioVolumeTarget target)
    {
        if (target != null) _targets.Remove(target);
    }

    /// <summary>登録済みの全ターゲットへ倍率を再適用する（基準×倍率。累積しない）。</summary>
    public static void ApplyAll()
    {
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            ParentAudioVolumeTarget t = _targets[i];
            if (t == null) { _targets.RemoveAt(i); continue; }
            t.Apply();
        }

        Changed?.Invoke();
    }
}
