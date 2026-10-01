namespace NeuroSDR.Plugins.Kiwi.Jnx;

internal readonly struct ProcessCharResult
{
    public bool Success { get; init; }
    public int Tally { get; init; }
    public bool Resync { get; init; }
}

internal interface IJnxEncoding
{
    int GetNbits();
    int GetMsb();
    bool CheckBits(int v);
    void Reset();
    bool SearchSync(int bit);
    ProcessCharResult ProcessChar(int code, int fixedStart, Action<string> output, bool showRaw, bool showErrs);
}
