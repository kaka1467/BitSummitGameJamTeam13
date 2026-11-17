    // 捕獲後の心拍開始を依頼済みか（二重開始防止。依頼先でも1回だけにしているが、ここでも抑止する）。
    private bool _heartbeatStartRequested;

    // 直前の移動が正常に到着したか（揺れなど「到着時の動作」を到着前・移動失敗時に始めないため）。
    private bool _lastMoveArrived = true;