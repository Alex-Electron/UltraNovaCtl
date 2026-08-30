using System.Globalization;
using System.Xml.Linq;

namespace UltraNovaCtl.Core;

/// <summary>
/// Reads Novation's own .automap files. They are plain XML: a Controls group holding one
/// group per page and section -
///     Page 1 Encoders    the eight knobs under the display
///     Page 1 BigKnob     the filter knob
///     Page 1 ModPedals   wheels and pedals
///
/// Three things in the format are easy to get wrong, and all three were:
///
///   An empty &lt;Param /&gt; means "nothing assigned here". Most elements in a real file
///   are empty - in one of the captured files, 4072 of 5656. Reading them as defaults
///   turns a mostly blank mapping into one where almost every control sends CC 0.
///
///   Type says what the ids mean. Type 0 is a MIDI map and id is a controller number;
///   Type 2 is plug-in automation, where id is a parameter index running past 400 and
///   means nothing to us. Clamping those into 0..127 produces convincing nonsense.
///
///   step carries the only hint of whether a control is continuous or a switch. A step
///   equal to the whole range is a two-position switch; the number of positions is
///   (high - low) / step + 1.
/// </summary>
public static class AutomapImport
{
    public static bool LooksLikeAutomap(string path) =>
        path.EndsWith(".automap", StringComparison.OrdinalIgnoreCase);

    /// <summary>Type attribute of the file: 0 is a MIDI map, 2 is plug-in automation.</summary>
    public static int ReadType(XDocument doc)
    {
        var el = doc.Descendants("Type").FirstOrDefault();
        return el != null && int.TryParse((string)el.Attribute("Type"), out int t) ? t : -1;
    }

    public static Config Load(string path, out string report)
    {
        var doc = XDocument.Load(path);
        int type = ReadType(doc);
        if (type != 0)
        {
            // Refuse rather than import rubbish: a plug-in map has no controller numbers
            // in it at all, so there is nothing here we could honestly translate.
            throw new InvalidOperationException(
                type == 2
                    ? "this is a plug-in automation map - its numbers are plug-in parameter " +
                      "indexes, not controller numbers, so there is nothing to import"
                    : $"unexpected file type {type}; only MIDI maps (Type 0) can be imported");
        }

        var pages = new SortedDictionary<int, Page>();
        int assigned = 0, blank = 0, switches = 0;

        foreach (var g in doc.Descendants("Group"))
        {
            string name = (string)g.Attribute("name") ?? "";
            if (!name.StartsWith("Page ", StringComparison.OrdinalIgnoreCase)) continue;

            string[] bits = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length < 3 || !int.TryParse(bits[1], out int number)) continue;
            string section = bits[2];

            if (!pages.TryGetValue(number, out var page))
            {
                page = Config.NewPage("Imported", number);
                // Everything starts silent; only what the file assigns comes alive.
                foreach (var enc in page.Encoders) enc.Send = "none";
                foreach (var a in page.Analog.Values) a.Send = "none";
                pages[number] = page;
            }

            var mappings = g.Elements("Param").Select(FromParam).ToList();
            foreach (var m in mappings)
            {
                if (m.Silent) blank++;
                else { assigned++; if (m.Mode == "step") switches++; }
            }

            switch (section.ToLowerInvariant())
            {
                case "encoders":
                    for (int i = 0; i < mappings.Count && i < 8; i++) page.Encoders[i] = mappings[i];
                    break;

                case "bigknob":
                    if (mappings.Count > 0) page.Encoders[8] = mappings[0];
                    break;

                case "modpedals":
                    for (int i = 0; i < mappings.Count; i++)
                        page.Analog[(i + 1).ToString()] = mappings[i];
                    break;
            }
        }

        var cfg = new Config();
        var bank = new Bank { Name = "IMPORTED", SelectButton = Config.BtnUser };
        int n = 1;
        foreach (var kv in pages)
        {
            kv.Value.Name = $"Page {n++}";
            bank.Pages.Add(kv.Value);
        }
        if (bank.Pages.Count == 0) bank.Pages.Add(Config.NewPage("Imported", 1));
        cfg.Banks.Add(bank);

        report = $"{bank.Pages.Count} pages, {assigned} assigned controls " +
                 $"({switches} of them switches), {blank} left empty";
        return cfg;
    }

    /// <summary>One Param element. An empty one means the control is unassigned.</summary>
    static Mapping FromParam(XElement e)
    {
        var m = new Mapping { Send = "none", Channel = 1, From = 0, To = 127 };

        // No id at all: this slot is simply not in use.
        string idText = (string)e.Attribute("id");
        if (string.IsNullOrEmpty(idText) || !int.TryParse(idText, out int id)) return m;
        if (id < 0 || id > 127) return m;      // outside MIDI: not a controller number

        m.Send = "cc";
        m.Number = id;

        string label = (string)e.Attribute("shortName") ?? "";
        // "CC# 33" as a label says nothing the number does not already say.
        if (!label.StartsWith("CC#", StringComparison.OrdinalIgnoreCase))
            m.Label = label.Length > 8 ? label.Substring(0, 8) : label;

        double lo = Number(e, "low", 0);
        double hi = Number(e, "high", 127);
        m.From = Clamp(lo);
        m.To = Clamp(hi);

        // step tells continuous from switched, and how many positions a switch has.
        double step = Number(e, "step", 0);
        double span = Math.Abs(hi - lo);
        if (step > 0 && span > 0 && step < span * 1.5)
        {
            int points = (int)Math.Round(span / step) + 1;
            if (points >= 2 && points <= 128 && points < 64)
            {
                m.Mode = "step";
                m.Points = points;
            }
        }

        return m;
    }

    static double Number(XElement e, string name, double fallback)
    {
        string t = (string)e.Attribute(name);
        return !string.IsNullOrEmpty(t) &&
               double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
               ? v : fallback;
    }

    static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 127);
}
