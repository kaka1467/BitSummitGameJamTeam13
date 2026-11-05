using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>パターン内の1個が「どの種類のアイテムか」。具体的なPrefabは ItemPool の登録内容から自動で選ぶ。</summary>
public enum PatternItemKind
{
    /// <summary>得点の低い Score（登録されている Score 型Prefabのうち scoreAmount が最小）。</summary>
    LowScore,
    /// <summary>得点の高い Score（同じく scoreAmount が最大。Score型が1種類だけなら LowScore と同じ）。</summary>
    HighScore,
    /// <summary>悪いアイテム（Clock＝減点）。サイレン(BGM)は時間条件があるためパターンには使わない。</summary>
    Bad,
    Fever,
    Boost,
}

/// <summary>そのパターンをいつ使うか。</summary>
public enum PatternUse
{
    /// <summary>通常時のみ（フィーバー中は選ばない）。</summary>
    NormalOnly,
    /// <summary>フィーバー中のみ（フィーバー開始時に1回出す。悪いアイテムは入れない前提）。</summary>
    FeverOnly,
}

[Serializable]
public class PatternSlot
{
    public PatternItemKind kind = PatternItemKind.LowScore;

    [Tooltip("レーン番号（0=上, 1=中, 2=下。lanesY の並び順）。範囲外は端に丸める。")]
    [Min(0)] public int lane = 1;

    [Tooltip("先頭からの列番号。1列あたりの間隔は SpawnPattern.columnSpacing。0 が最初に画面へ入る列。")]
    [Min(0f)] public float column = 0f;
}

/// <summary>
/// 短い配置パターン1つ分の定義。slots が空で restSeconds が正なら「休憩区間」（アイテムは出さず、通常出現を薄くするだけ）。
/// </summary>
[Serializable]
public class SpawnPattern
{
    public string name = "Pattern";
    public bool enabled = true;

    [Tooltip("選ばれやすさ（相対重み）。0 以下なら選ばれない。")]
    [Min(0f)] public float weight = 1f;

    public PatternUse use = PatternUse.NormalOnly;

    [Tooltip("本編開始からこの秒数が過ぎるまでは選ばない。")]
    [Min(0f)] public float minElapsedSeconds = 0f;

    [Tooltip("列と列の間隔（ワールド単位）。アイテムの実サイズより狭くはならない（自動で広げる）。")]
    [Min(0.05f)] public float columnSpacing = 0.5f;

    [Tooltip("ONなら、毎回50%で上下を反転する（上段⇔下段）。中段だけのパターンでは効果なし。")]
    public bool allowVerticalFlip = true;

    [Tooltip("このパターンの後（配置が画面を抜けた後）に続ける休憩区間の長さ（秒）。0 で休憩なし。")]
    [Min(0f)] public float restSeconds = 0f;

    [Tooltip("休憩区間中の通常出現の濃さ（1=通常、0.2=通常の2割）。")]
    [Range(0.05f, 1f)] public float restDensity = 0.25f;

    public List<PatternSlot> slots = new List<PatternSlot>();
}

/// <summary>既定の配置パターン（Inspectorで編集・追加・削除できる。シーンに未保存の間だけこの内容が入る）。</summary>
public static class ItemSpawnPatternDefaults
{
    public static List<SpawnPattern> Create()
    {
        var list = new List<SpawnPattern>();

        // ── 連続収集 ──────────────────────────────────────────
        list.Add(Pattern("連続収集・中段", 1f, PatternUse.NormalOnly, 0f, 0.5f, false, 0f,
            Row(PatternItemKind.LowScore, 1, 0, 6)));

        list.Add(Pattern("連続収集・端レーン", 1f, PatternUse.NormalOnly, 0f, 0.5f, true, 0f,
            Row(PatternItemKind.LowScore, 0, 0, 6)));

        // ── 上下移動 ──────────────────────────────────────────
        list.Add(Pattern("上下移動・階段", 1f, PatternUse.NormalOnly, 0f, 0.6f, true, 0f,
            Seq(PatternItemKind.LowScore, 0, 1, 2, 1, 0, 1, 2)));

        list.Add(Pattern("上下移動・往復", 0.8f, PatternUse.NormalOnly, 15f, 0.7f, true, 0f,
            Seq(PatternItemKind.LowScore, 0, 2, 0, 2, 0)));

        // ── 安全な低得点 / 危険な高得点 ───────────────────────────
        // 下段：安全な低得点が5個。上段：先頭に悪いアイテム、その後ろに高得点が3個。
        list.Add(Pattern("選択・端の高得点", 1f, PatternUse.NormalOnly, 20f, 0.5f, true, 0f,
            Row(PatternItemKind.LowScore, 2, 0, 5),
            Row(PatternItemKind.Bad, 0, 0, 1),
            Row(PatternItemKind.HighScore, 0, 1, 3)));

        // 中段：悪いアイテムの後ろに高得点が3個。上下段：安全な低得点。
        list.Add(Pattern("選択・中段の高得点", 0.8f, PatternUse.NormalOnly, 30f, 0.5f, false, 0f,
            Row(PatternItemKind.LowScore, 0, 0, 5),
            Row(PatternItemKind.LowScore, 2, 0, 5),
            Row(PatternItemKind.Bad, 1, 0, 1),
            Row(PatternItemKind.HighScore, 1, 1, 3)));

        // ── 低密度・休憩 ──────────────────────────────────────
        SpawnPattern shortRest = Pattern("休憩・短め", 0.8f, PatternUse.NormalOnly, 0f, 0.5f, false, 4f);
        shortRest.restDensity = 0.25f;
        list.Add(shortRest);

        SpawnPattern longRest = Pattern("休憩・長め", 0.5f, PatternUse.NormalOnly, 60f, 0.5f, false, 7f);
        longRest.restDensity = 0.1f;
        list.Add(longRest);

        // ── フィーバー中に稼ぐ配置（悪いアイテムなし） ─────────────────
        list.Add(Pattern("フィーバー・得点の壁", 1f, PatternUse.FeverOnly, 0f, 0.45f, false, 0f,
            Row(PatternItemKind.LowScore, 0, 0, 5),
            Row(PatternItemKind.LowScore, 1, 0, 5),
            Row(PatternItemKind.LowScore, 2, 0, 5)));

        list.Add(Pattern("フィーバー・高得点の波", 1f, PatternUse.FeverOnly, 0f, 0.4f, false, 0f,
            Seq(PatternItemKind.HighScore, 1, 0, 1, 2, 1, 0, 1, 2, 1)));

        return list;
    }

    private static SpawnPattern Pattern(string name, float weight, PatternUse use, float minElapsed,
        float spacing, bool flip, float rest, params List<PatternSlot>[] rows)
    {
        var p = new SpawnPattern
        {
            name = name,
            weight = weight,
            use = use,
            minElapsedSeconds = minElapsed,
            columnSpacing = spacing,
            allowVerticalFlip = flip,
            restSeconds = rest,
        };

        if (rows != null)
        {
            foreach (var row in rows) p.slots.AddRange(row);
        }

        return p;
    }

    /// <summary>同じレーンに startColumn から count 個を1列おきに並べる。</summary>
    private static List<PatternSlot> Row(PatternItemKind kind, int lane, int startColumn, int count)
    {
        var row = new List<PatternSlot>(count);
        for (int i = 0; i < count; i++)
            row.Add(new PatternSlot { kind = kind, lane = lane, column = startColumn + i });
        return row;
    }

    /// <summary>列0から1列ずつ、指定したレーン順に並べる（上下移動用）。</summary>
    private static List<PatternSlot> Seq(PatternItemKind kind, params int[] lanes)
    {
        var row = new List<PatternSlot>(lanes.Length);
        for (int i = 0; i < lanes.Length; i++)
            row.Add(new PatternSlot { kind = kind, lane = lanes[i], column = i });
        return row;
    }
}
