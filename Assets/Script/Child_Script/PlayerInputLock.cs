public static class PlayerInputLock
{
    public static bool IsLocked { get; private set; }

    public static void SetLocked(bool locked)
    {
        IsLocked = locked;
    }
}
