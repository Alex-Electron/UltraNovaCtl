namespace UltraNovaCtl.Core;

/// <summary>
/// Where each addressable lamp sits on the UltraNova, looking at the panel.
/// Units are roughly centimetres from the left / top of the control surface
/// (GLOBAL at the left, ANIMATE at the right, encoder rings along the top).
/// Taken from the front-panel photograph, not from the button-code order.
/// </summary>
public static class PanelLamps
{
    /// <summary>
    /// 14 lamps flashing together already pumps the analogue output.
    /// Demo never lights more than this at once.
    /// </summary>
    public const int MaxAtOnce = 13;
    public const int HelloMs = 1000;
    public const int EnterMs = 700;
    public const int ShowMs = 20000;

    /// <summary>Name of the current act in the one-button 20 s film, for the LCD.</summary>
    public static string ActName(float t)
    {
        if (t < 0.12f) return "rings";
        if (t < 0.22f) return "bloom";
        if (t < 0.34f) return "sweep";
        if (t < 0.46f) return "engine";
        if (t < 0.56f) return "orbit";
        if (t < 0.66f) return "seq";
        return "chaos";
    }

    public readonly struct Spot
    {
        public readonly int Code;
        public readonly float X, Y;
        public Spot(int code, float x, float y) { Code = code; X = x; Y = y; }
    }

    public static readonly Spot[] All =
    {
        // GLOBAL — far left, under the mic
        new(8,  6, 28),   // AUDIO
        new(10, 14, 28),  // GLOBAL
        new(9,  6, 36),   // OCTAVE-
        new(11, 14, 36),  // OCTAVE+

        // MODE / SOUND — patch dial cluster
        new(13, 26,  8),  // PATCH
        new(12, 22, 22),  // SYNTH
        new(14, 30, 18),  // AUTOMAP
        new(15, 30, 28),  // COMPARE
        new(16, 30, 34),  // WRITE

        // CONTROL — eight encoder rings along the top
        new(42, 42, 4), new(43, 48, 4), new(44, 54, 4), new(45, 60, 4),
        new(46, 66, 4), new(47, 72, 4), new(48, 78, 4), new(49, 84, 4),

        // Automap row under the knobs, then LOCK / FILTER by the big filter knob
        new(0, 44, 12),  // LEARN
        new(1, 50, 12),  // VIEW
        new(2, 56, 12),  // USER
        new(3, 62, 12),  // FX
        new(4, 68, 12),  // INST
        new(5, 74, 12),  // MIXER
        new(6, 84, 12),  // LOCK
        new(7, 90, 12),  // FILTER (the button, next to LOCK)

        // PAGE beside the LCD
        new(17, 38, 18), // PAGE<
        new(18, 38, 24), // PAGE>

        // SELECT 1–6, a vertical stack left of SYNTH EDIT
        new(36, 40, 28), new(37, 40, 30), new(38, 40, 32),
        new(39, 40, 34), new(40, 40, 36), new(41, 40, 38),

        // SYNTH EDIT — two rows, columns run OSC/ENV, MIXER/LFO, FILTER/MOD, VOICE, EFFECT/VOCODER
        new(19, 48, 28),  // OSCILLATOR
        new(21, 56, 28),  // MIXER
        new(23, 64, 28),  // FILTER
        new(25, 72, 28),  // VOICE
        new(26, 80, 28),  // EFFECTS
        new(20, 48, 36),  // ENVELOPE
        new(22, 56, 36),  // LFO
        new(24, 64, 36),  // MODULATION
        new(27, 80, 36),  // VOCODER
        new(35, 80, 32),  // extra vocoder lamp

        // PERFORMANCE — far right
        new(28, 88, 28),  // ARP ON
        new(29, 88, 36),  // ARP SETTINGS
        new(30, 92, 36),  // ARP LATCH
        new(31, 96, 28),  // CHORD ON
        new(32, 96, 36),  // CHORD EDIT
        new(33, 102, 28), // ANIMATE TWEAK
        new(34, 102, 36), // ANIMATE TOUCH
    };

    public static void Clear(float[] score)
    {
        Array.Clear(score, 0, score.Length);
    }

    public static void Add(float[] score, int code, float v)
    {
        if ((uint)code < (uint)score.Length && v > score[code])
            score[code] = v;
    }

    static readonly int[] AutomapRow = { 0, 1, 2, 3, 4, 5, 6, 7 };
    static readonly int[] Rings = { 42, 43, 44, 45, 46, 47, 48, 49 };
    static readonly int[] Select = { 36, 37, 38, 39, 40, 41 };
    static readonly int[] EditPath = { 19, 20, 21, 22, 23, 24, 25, 26, 27, 35 };
    static readonly int[] Perf = { 28, 29, 30, 31, 32, 33, 34 };
    /// <summary>Under the mic: AUDIO, GLOBAL, OCTAVE−, OCTAVE+.</summary>
    static readonly int[] Mic = { 8, 10, 9, 11 };
    /// <summary>MODE/SOUND: SYNTH, AUTOMAP, PATCH BROWSE, COMPARE, WRITE.</summary>
    static readonly int[] Mode = { 12, 14, 13, 15, 16 };
    /// <summary>PAGE BACK / PAGE NEXT beside the LCD.</summary>
    static readonly int[] Pages = { 17, 18 };

    /// <summary>A sharp blade scanning the panel. <paramref name="sec"/> is wall-clock.</summary>
    public static void ScoreSweep(float[] score, float t, float sec)
    {
        Clear(score);
        float pos = PingPong(sec * 0.9f, 1) * 110 - 4;
        Blade(score, pos, 4.2f);
        Comet(score, Rings, PingPong(sec * 2.0f, 7), 3);
        Comet(score, Mode, PingPong(sec * 1.7f, 4), 1.6f);
        MicWhenLeft(score, pos);
        FlipPages(score, sec, 3f);
    }

    /// <summary>KITT scanner on the rings, a second bar on LEARN…FILTER the other way.</summary>
    public static void ScoreRings(float[] score, float t, float sec)
    {
        Clear(score);
        Comet(score, Rings, PingPong(sec * 2.4f, 7), 2.6f);
        Comet(score, AutomapRow, PingPong(sec * 2.4f + 7, 7), 2.0f);
        Comet(score, Mode, PingPong(sec * 2.0f + 2, 4), 1.7f);
        FlipPages(score, sec, 4f);
    }

    /// <summary>A tight comet racing the synth-edit grid, rings as a clock.</summary>
    public static void ScoreEngine(float[] score, float t, float sec)
    {
        Clear(score);
        Comet(score, EditPath, sec * 3.6f, 2.0f, loop: true);
        Comet(score, Rings, PingPong(sec * 2.2f, 7), 1.8f);
        Comet(score, Mode, sec * 2.8f, 1.8f, loop: true);
        FlipPages(score, sec, 5f);
    }

    /// <summary>
    /// Debug-on hello (~1 s): a beat of rings, then the mad-console chaos
    /// that used to play here.
    /// </summary>
    public static void ScoreHello(float[] score, float t, float sec)
    {
        if (t < 0.22f)
        {
            Clear(score);
            Comet(score, Rings, (t / 0.22f) * 7f, 3.5f);
        }
        else
            ScoreChaos(score, t, sec);
    }

    /// <summary>
    /// Crazy spaceship console: lamps flicker on their own clocks, some latch as
    /// status lights, clusters fire like a subsystem throwing alarms. Timed in
    /// seconds so a 20 s show stays busy instead of slowing down. Never more
    /// than MaxAtOnce — Pick keeps the thirteen hottest.
    /// </summary>
    public static void ScoreChaos(float[] score, float t, float sec)
    {
        Clear(score);
        int n = All.Length;
        int win = (int)(sec * 7);
        int spark = (int)(sec * 22);
        float swell = 0.72f + 0.28f * MathF.Sin(sec * 0.85f);
        foreach (var p in All)
        {
            float phase = p.Code * 1.61803f + p.X * 0.13f + p.Y * 0.07f;
            float slow = 0.5f + 0.5f * MathF.Sin(sec * (7.4f + (p.Code % 6) * 1.21f) + phase);
            float twitch = 0.5f + 0.5f * MathF.Sin(sec * (19.5f + p.Code * 0.37f) + phase * 2.07f);
            float latch = Hash(p.Code * 31 + win) > 0.68f ? 0.55f : 0;
            float blip = Hash(p.Code * 13 + spark) > 0.87f ? 0.70f : 0;
            // Background only — Pick used to spend all 13 slots on the dense
            // right-hand cluster, so LEARN…OCTAVE never got a turn.
            score[p.Code] = (slow * 0.30f + twitch * 0.22f) * swell + latch + blip;
        }

        // A snake through every lamp on the panel, 8 at a time. At 18 steps/s
        // each code is forced on for ~0.4 s, and a full lap is under 3 s.
        int start = (int)(sec * 18) % n;
        for (int i = 0; i < 8; i++)
            Add(score, All[(start + i) % n].Code, 1.80f);

        int start2 = n - 1 - ((int)(sec * 12) % n);
        for (int i = 0; i < 3; i++)
            Add(score, All[(start2 - i + n * 4) % n].Code, 1.50f);

        FlipPages(score, sec, 3.2f);
    }

    /// <summary>Shockwaves out from the LCD.</summary>
    public static void ScoreBloom(float[] score, float t, float sec)
    {
        Clear(score);
        float pulse = sec * 0.85f % 1f;
        Annulus(score, 62, 12, pulse * 48, 3.2f);
        MicWhenLeft(score, 62 - pulse * 48);
        FlipPages(score, sec, 2.4f);
    }

    /// <summary>Two tight spots racing: rings vs SYNTH EDIT, opposite directions.</summary>
    public static void ScoreOrbit(float[] score, float t, float sec)
    {
        Clear(score);
        float a = sec * MathF.PI * 1.5f;
        float ox = 63 + 22 * MathF.Cos(a);
        SpotAt(score, ox, 4, 3.4f);
        SpotAt(score, 64 + 18 * MathF.Cos(-a * 1.15f), 32 + 6 * MathF.Sin(-a * 1.15f), 3.4f);
        MicWhenLeft(score, ox);
        Comet(score, Mode, PingPong(sec * 1.9f, 4), 1.5f);
        FlipPages(score, sec, 3.5f);
    }

    /// <summary>Groovebox: SELECT is the step, rings the clock, edit+ARP the hits.</summary>
    public static void ScoreSeq(float[] score, float t, float sec)
    {
        Clear(score);
        int step = ((int)(sec * 8)) % 6;
        Add(score, Select[step], 1.4f);
        if (step > 0) Add(score, Select[step - 1], 0.35f);

        int clock = ((int)(sec * 16)) % 8;
        Add(score, Rings[clock], 1.3f);
        Add(score, Rings[(clock + 7) % 8], 0.45f);

        Add(score, Perf[step], 1.2f);
        Add(score, EditPath[step], 1.1f);
        Add(score, Mode[step % Mode.Length], 1.15f);
        Add(score, Pages[step % 2], 1.3f);
    }

    /// <summary>
    /// The one Demo-button film: every scene in sequence, Chaos as the long
    /// last act, rings crashing in from both ends at the close.
    /// </summary>
    public static void ScoreFilm(float[] score, float t, float sec)
    {
        if (t < 0.12f)
            ScoreRings(score, t, sec);
        else if (t < 0.22f)
            ScoreBloom(score, t, sec);
        else if (t < 0.34f)
            ScoreSweep(score, t, sec);
        else if (t < 0.46f)
            ScoreEngine(score, t, sec);
        else if (t < 0.56f)
            ScoreOrbit(score, t, sec);
        else if (t < 0.66f)
            ScoreSeq(score, t, sec);
        else if (t < 0.88f)
            ScoreChaos(score, t, sec);
        else
        {
            ScoreChaos(score, t, sec);
            float u = (t - 0.88f) / 0.12f;
            float left = u * 4.2f;
            float right = 7 - u * 4.2f;
            for (int i = 0; i < 8; i++)
            {
                float d = MathF.Min(MathF.Abs(i - left), MathF.Abs(i - right));
                Add(score, Rings[i], MathF.Max(0, 1.5f - d * 1.05f));
            }
        }
    }

    /// <summary>
    /// Entering AUTOMAP: rings run left→right, LEARN…FILTER the other way, 0.7 s.
    /// </summary>
    public static void ScoreEnter(float[] score, float t, float sec)
    {
        Clear(score);
        Comet(score, Rings, t * 7f, 3.4f);
        Comet(score, AutomapRow, (1f - t) * 7f, 3.4f);
    }

    public static void Score(string id, float[] score, float t, float sec)
    {
        if (id == "hello") ScoreHello(score, t, sec);
        else if (id == "enter") ScoreEnter(score, t, sec);
        else ScoreFilm(score, t, sec);
    }

    static bool IsMic(int code) => code is 8 or 9 or 10 or 11;

    /// <summary>AUDIO/GLOBAL/OCTAVE only when the main motion is actually on the left.</summary>
    static void MicWhenLeft(float[] score, float focusX)
    {
        if (focusX > 26) return;
        float amp = MathF.Max(0, 1.15f - focusX / 26f);
        foreach (int c in Mic) Add(score, c, amp);
    }

    /// <summary>PAGE BACK and NEXT take turns, like a cursor beside the LCD.</summary>
    static void FlipPages(float[] score, float sec, float hz)
    {
        int i = ((int)(sec * hz) & 1);
        Add(score, Pages[i], 1.35f);
        Add(score, Pages[1 - i], 0.25f);
    }

    static void Comet(float[] score, int[] codes, float head, float tail, bool loop = false)
    {
        int n = codes.Length;
        if (n == 0 || tail < 0.2f) return;
        float h = loop ? ((head % n) + n) % n : head;
        for (int i = 0; i < n; i++)
        {
            float d = loop ? Circ(i, h, n) : MathF.Abs(i - h);
            if (d <= tail)
                Add(score, codes[i], 1.45f - d * (1.2f / tail));
        }
    }

    static void Blade(float[] score, float x, float sigma)
    {
        float k = 2 * sigma * sigma;
        foreach (var p in All)
        {
            float dx = p.X - x;
            float s = MathF.Exp(-(dx * dx) / k);
            if (s > score[p.Code]) score[p.Code] = s;
        }
    }

    static void Annulus(float[] score, float cx, float cy, float radius, float sigma)
    {
        float k = 2 * sigma * sigma;
        foreach (var p in All)
        {
            float d = MathF.Abs(Dist(p.X, p.Y, cx, cy) - radius);
            float s = MathF.Exp(-(d * d) / k);
            if (s > score[p.Code]) score[p.Code] = s;
        }
    }

    static void SpotAt(float[] score, float x, float y, float sigma)
    {
        float k = 2 * sigma * sigma;
        foreach (var p in All)
        {
            float d = Dist(p.X, p.Y, x, y);
            float s = MathF.Exp(-(d * d) / k);
            if (s > score[p.Code]) score[p.Code] = s;
        }
    }

    static float Hash(int x)
    {
        uint n = unchecked((uint)x);
        n = (n ^ 61u) ^ (n >> 16);
        n *= 9u;
        n ^= n >> 4;
        n *= 0x27d4eb2du;
        n ^= n >> 15;
        return (n & 0xFFFFu) / 65535f;
    }

    static float Dist(float x0, float y0, float x1, float y1)
    {
        float dx = x0 - x1, dy = y0 - y1;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Light the strongest scores, never more than <paramref name="cap"/>.</summary>
    public static void Pick(float[] score, int cap, bool[] on)
    {
        Array.Clear(on, 0, on.Length);
        if (cap < 1) return;
        for (int k = 0; k < cap; k++)
        {
            int best = -1;
            float bestS = 0.16f;
            for (int i = 0; i < score.Length; i++)
            {
                if (on[i]) continue;
                if (score[i] > bestS) { bestS = score[i]; best = i; }
            }
            if (best < 0) break;
            on[best] = true;
        }
    }

    static float Circ(float a, float b, int n)
    {
        float d = MathF.Abs(a - b);
        return MathF.Min(d, n - d);
    }

    public static float PingPong(float t, float len)
    {
        if (len <= 0) return 0;
        float cycle = len * 2;
        float m = t % cycle;
        if (m < 0) m += cycle;
        return m <= len ? m : cycle - m;
    }
}
