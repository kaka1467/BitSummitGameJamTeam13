using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// MotherHeightOffsetVerifier：
/// MotherApproachController.motherHeightOffset の仕様をEditMode上で機械的に確認する一時ツール。
/// メニュー「Tools/母親高さオフセット検証 (EditMode)」を実行すると、Consoleに各確認項目の結果（PASS/FAIL）を出力する。
///
/// 確認項目：
///   ① motherHeightOffset=0 のとき、到達点がwaypointの生座標と完全一致する（＝従来と同じ挙動）
///   ② ResetApproachを繰り返してもY補正が累積しない（常に「生座標＋offset」で一定）
///   ③ 帰還（startPoint）・ドア（doorPoint）・窓覗き（gardenPeekPoint）の各到達点で、
///      補正がそれぞれ1回だけ適用される（地点をまたいだ合算・同じ地点の再訪での二重加算がない）
///
/// 本体コードは一切変更しない。privateメンバ（motherHeightOffset／OffsetGoalPosition／
/// MovePositionOnly）はリフレクションで操作する。
/// 確認が終わればこのファイルごと削除してよい（Editorフォルダなのでビルドには含まれない）。
/// </summary>
public static class MotherHeightOffsetVerifier
{
    // 検出しやすいよう、わざと偶数・きり良くない値を使う（0.5や1だと偶然の一致を拾いにくくするため）。
    private const float TestOffset = 0.37f;

    // MoveTowardsを手動で回すときの暴走ガード（EditModeのTime.deltaTime値に依存しない十分な回数）。
    private const int CoroutineGuard = 100000;

    private static int _passCount;
    private static int _failCount;

    [MenuItem("Tools/母親高さオフセット検証 (EditMode)")]
    public static void Run()
    {
        _passCount = 0;
        _failCount = 0;

        // 検証用の小さなシーンを作る（既存シーンには触れない。実行後に必ず破棄する）。
        var controllerGo = new GameObject("検証用 MotherRouteRoot");
        var startGo      = new GameObject("検証用 startPoint");
        var turnGo       = new GameObject("検証用 turnPoint");
        var doorGo       = new GameObject("検証用 doorPoint");
        var peekGo       = new GameObject("検証用 gardenPeekPoint");

        try
        {
            // 各waypointのYを互いに違う値にして、「地点ごとに生座標から再計算していること」を検証できるようにする。
            startGo.transform.position = new Vector3(0f, 0f, 0f);
            turnGo.transform.position  = new Vector3(2f, 1f, 2f);
            doorGo.transform.position  = new Vector3(4f, 2f, 4f);
            peekGo.transform.position  = new Vector3(6f, 3f, 6f);

            var controller = controllerGo.AddComponent<MotherApproachController>();
            var parentDetection = controllerGo.GetComponent<MotherSuspicionSystem>();
            if (parentDetection == null)
                parentDetection = controllerGo.AddComponent<MotherSuspicionSystem>();
            if (parentDetection == null)
                parentDetection = Object.FindFirstObjectByType<MotherSuspicionSystem>();

            if (parentDetection == null)
            {
                Debug.LogWarning("[検証] MotherSuspicionSystemが見つからないため、移動速度を取得できず検証を中止します。");
                return;
            }

            controller.startPoint       = startGo.transform;
            controller.turnPoint        = turnGo.transform;
            controller.doorPoint       = doorGo.transform;
            controller.gardenPeekPoint  = peekGo.transform;

            var offsetField = typeof(MotherApproachController).GetField(
                "motherHeightOffset", BindingFlags.NonPublic | BindingFlags.Instance);
            var parentDetectionField = typeof(MotherApproachController).GetField(
                "parentDetection", BindingFlags.NonPublic | BindingFlags.Instance);
            var offsetGoalMethod = typeof(MotherApproachController).GetMethod(
                "OffsetGoalPosition", BindingFlags.NonPublic | BindingFlags.Instance);
            // 位置移動は MovePositionOnly に統一された（旧 MoveToPoint／MoveToPointFacingMovement は廃止）。
            var moveToPointMethod = typeof(MotherApproachController).GetMethod(
                "MovePositionOnly", BindingFlags.NonPublic | BindingFlags.Instance);

            if (offsetField == null || offsetGoalMethod == null ||
                moveToPointMethod == null ||
                parentDetectionField == null)
            {
                Debug.LogError("[検証] リフレクション対象のメンバが見つからない（実装が変わっていないか確認してください）。");
                return;
            }

            parentDetectionField.SetValue(controller, parentDetection);
            Debug.Log($"[検証] MotherSuspicionSystem.CurrentApproachSpeed={parentDetection.CurrentApproachSpeed:F2}");

            Transform mother = controllerGo.transform;

            // ────────────────────────────────────────────────────────────────
            // ① offset=0：従来と同じ挙動
            // ────────────────────────────────────────────────────────────────
            offsetField.SetValue(controller, 0f);

            controller.ResetApproach();
            Check("① ResetApproach（offset=0）はstartPointの生座標と一致",
                  mother.position, startGo.transform.position);

            var goal0 = (Vector3)offsetGoalMethod.Invoke(controller, new object[] { doorGo.transform.position });
            Check("① OffsetGoalPosition（offset=0）はdoorPointの生座標と一致",
                  goal0, doorGo.transform.position);

            DriveCoroutine(moveToPointMethod, controller, doorGo.transform);
            Check("① MoveToPoint（offset=0）終了位置はdoorPointの生座標と一致",
                  mother.position, doorGo.transform.position);

            // ────────────────────────────────────────────────────────────────
            // ② offset=0.37：ResetApproachを繰り返しても累積しない
            // ────────────────────────────────────────────────────────────────
            offsetField.SetValue(controller, TestOffset);

            Vector3 expectedStart = new Vector3(startGo.transform.position.x,
                                                startGo.transform.position.y + TestOffset,
                                                startGo.transform.position.z);
            for (int i = 1; i <= 10; i++)
            {
                controller.ResetApproach();
                Check($"② ResetApproach {i}回目（offset={TestOffset}）は「startPoint＋offset」で一定",
                      mother.position, expectedStart);
            }

            // ────────────────────────────────────────────────────────────────
            // ③ 各到達点で1回だけ適用（帰還 → ドア → 再訪 → 窓 → 帰還）
            // ────────────────────────────────────────────────────────────────
            controller.ResetApproach();
            Check("③ 帰還地点（startPoint）は1回だけ適用",
                  mother.position, expectedStart);

            DriveCoroutine(moveToPointMethod, controller, doorGo.transform);
            Vector3 expectedDoor = new Vector3(doorGo.transform.position.x,
                                               doorGo.transform.position.y + TestOffset,
                                               doorGo.transform.position.z);
            // 前の地点（startPoint＋0.37）にさらに0.37が足されず、doorPointの生座標基準であることを確認する。
            Check("③ ドア（doorPoint）は生座標基準で1回だけ適用（前地点と合算しない）",
                  mother.position, expectedDoor);

            // 同じ地点に再訪しても二重加算されないことを確認する。
            DriveCoroutine(moveToPointMethod, controller, doorGo.transform);
            Check("③ 同じドア地点を再訪しても累積しない",
                  mother.position, expectedDoor);

            DriveCoroutine(moveToPointMethod, controller, peekGo.transform);
            Vector3 expectedPeek = new Vector3(peekGo.transform.position.x,
                                               peekGo.transform.position.y + TestOffset,
                                               peekGo.transform.position.z);
            Check("③ 窓覗き地点（gardenPeekPoint）は生座標基準で1回だけ適用",
                  mother.position, expectedPeek);

            controller.ResetApproach();
            Check("③ 帰還（ResetApproach）は再び1回だけ適用（2回目分が累積しない）",
                  mother.position, expectedStart);

            Debug.Log($"[検証] 完了：PASS={_passCount} / FAIL={_failCount}" +
                      (_failCount == 0 ? " — すべての確認項目を満たしています。" : " — FAIL項目を確認してください。"));
        }
        finally
        {
            Object.DestroyImmediate(controllerGo);
            Object.DestroyImmediate(startGo);
            Object.DestroyImmediate(turnGo);
            Object.DestroyImmediate(doorGo);
            Object.DestroyImmediate(peekGo);
        }
    }

    /// <summary>
    /// privateな移動コルーチンをEditMode上で手動実行する（MoveNextを最後まで回すと到達点スナップまで完了する）。
    /// </summary>
    private static void DriveCoroutine(MethodInfo method, MotherApproachController controller, Transform target)
    {
        var routine = (IEnumerator)method.Invoke(controller, new object[] { target });

        for (int i = 0; i < CoroutineGuard && routine.MoveNext(); i++)
        {
            // MoveNextの戻り値がfalseになるまで反復（＝コルーチン完了）。
        }

        if (routine.MoveNext())
            Debug.LogError("[検証] 移動コルーチンがガード回数内に完了しませんでした（結果は不正の可能性あり）。");
    }

    private static void Check(string label, Vector3 actual, Vector3 expected)
    {
        // 到達点は同一の演算（x, y+offset, z）で作られるため、誤差は原理的に0。
        // 浮動小数の安全側として1e-5未満を一致とみなす。
        bool ok = Vector3.Distance(actual, expected) < 1e-5f;
        if (ok)
        {
            _passCount++;
            Debug.Log($"[検証] PASS  {label} | actual={actual}  expected={expected}");
        }
        else
        {
            _failCount++;
            Debug.LogError($"[検証] FAIL  {label} | actual={actual}  expected={expected}");
        }
    }
}
