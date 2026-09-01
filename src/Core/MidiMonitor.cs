namespace UltraNovaCtl.Core;

/// <summary>
/// MIDI clock is 24 ticks per quarter note. Logging each F8 would drown the log
/// and, if we ever generated it, fight the DAW for tempo. This only reports a
/// running BPM once a second, and Start/Stop/Continue as they happen.
/// </summary>
public sealed class MidiClockMeter
{
    readonly Action<string> _say;
    readonly string _prefix;
    int _ticks;
    long _t0;
    bool _heard;

    public MidiClockMeter(Action<string> say, string prefix = "")
    {
        _say = say;
        _prefix = prefix;
    }

    public void Tick()
    {
        long now = Environment.TickCount64;
        if (_t0 == 0) _t0 = now;
        _ticks++;
        _heard = true;
        long dt = now - _t0;
        if (dt < 1000) return;
        if (_ticks >= 8)
            _say($"{_prefix}clock: ~{_ticks * 2500.0 / dt:0} BPM");
        _ticks = 0;
        _t0 = now;
    }

    public void Realtime(byte status)
    {
        if (status == 0xF8) { Tick(); return; }
        if (status == 0xFC && _heard)
        {
            _say($"{_prefix}clock: stopped");
            _heard = false;
            _ticks = 0;
            _t0 = 0;
        }
        else if (status is 0xFA or 0xFB)
        {
            _ticks = 0;
            _t0 = Environment.TickCount64;
        }
        if (status == 0xFE) return;   // active sensing, a few times a second for no gain
        _say($"{_prefix}{MidiNames.Realtime(status)}");
    }
}

/// <summary>Turns the NRPN/RPN CC dance into a parameter number on the log line.</summary>
public sealed class NrpnAssembler
{
    readonly int[] _msb = new int[16];
    readonly int[] _lsb = new int[16];
    readonly bool[] _nrpn = new bool[16];

    public string Annotate(int channel, int cc, int value)
    {
        int ch = Math.Clamp(channel, 1, 16) - 1;
        switch (cc)
        {
            case 99: _msb[ch] = value; _nrpn[ch] = true; return $" · NRPN {Param(ch)}";
            case 98: _lsb[ch] = value; _nrpn[ch] = true; return $" · NRPN {Param(ch)}";
            case 101: _msb[ch] = value; _nrpn[ch] = false; return $" · RPN {Param(ch)}";
            case 100: _lsb[ch] = value; _nrpn[ch] = false; return $" · RPN {Param(ch)}";
            case 6:
                return $" · {(_nrpn[ch] ? "NRPN" : "RPN")} {Param(ch)} = {value}";
            case 38:
                return $" · {(_nrpn[ch] ? "NRPN" : "RPN")} {Param(ch)} LSB {value}";
            default:
                return "";
        }
    }

    int Param(int ch) => (_msb[ch] << 7) | _lsb[ch];
}
