// Kinetik Weather: a weather desktop widget in the style of Kinetik's widget and Kinetik Calendar.
// Current conditions, the next hours with their chance of rain, sunrise and sunset, and the days ahead, from Open-Meteo
// (no account, no API key).
// Copyright © 2026 Akila Sella Hennedige. GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Kinetik Weather")]
[assembly: AssemblyProduct("Kinetik Weather")]
[assembly: AssemblyCopyright("Copyright © 2026 Akila Sella Hennedige")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

static class Program
{
    public const string Version = "1.0.1";
    // --demo: a made-up forecast, nothing fetched, read from or saved to this PC. "--theme Name" picks its theme.
    // "--shot file.png" draws the demo card into a PNG and quits (for the README screenshots).
    public static bool Demo;
    public static string DemoTheme, ShotPath, DemoPlace; // DemoPlace: "lat,lon" fetched live instead of the made-up forecast
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        Demo = args.Contains("--demo");
        int ti = Array.IndexOf(args, "--theme");
        if (ti >= 0 && ti + 1 < args.Length) DemoTheme = args[ti + 1];
        int si = Array.IndexOf(args, "--shot");
        if (si >= 0 && si + 1 < args.Length) { ShotPath = Path.GetFullPath(args[si + 1]); Demo = true; }
        int pi = Array.IndexOf(args, "--place");
        if (pi >= 0 && pi + 1 < args.Length) DemoPlace = args[pi + 1];
        bool created;
        using (new Mutex(true, Demo ? @"Local\KinetikWeatherDemo" + (ShotPath != null ? Guid.NewGuid().ToString("N") : "") : @"Local\KinetikWeather", out created))
        {
            if (!created) return; // already running
            SetProcessDPIAware();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ToolStripManager.Renderer = new DarkMenu();
            Backup.KeyName = "KinetikWeather"; Backup.AppTitle = "Kinetik Weather";
            if (!Demo) Backup.OfferRestore();
            var app = new WeatherForm();
            using (new KinetikSuite.Shutdown("KinetikWeather" + (Demo ? "Demo" : ""), () => { app.Close(); Application.Exit(); }))
                Application.Run(app);
        }
        if (!Demo) Backup.AfterExit();
    }
}

// ======================================================================= Settings
class Settings
{
    const string KeyPath = @"Software\KinetikWeather";
    static readonly bool Metric = RegionInfo.CurrentRegion.IsMetric;
    public int X = int.MinValue, Y = int.MinValue, Width = 340, Scale = 100, Opacity = CommonMenu.DefaultOpacity, Layer = 0;
    public int Hours = 12, Days = 5, Refresh = 30, Wind = Metric ? 0 : 1; // Wind: 0 km/h, 1 mph, 2 m/s
    public bool Locked, Snap = true, ClickThrough, Fahrenheit = !Metric;
    public bool ShowHourly = true, ShowRain = true, ShowSun = true, ShowDaily = true;
    public bool Use24h = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H");
    public string ThemeName = "Midnight", FontName = ""; // "" = Segoe UI Variable Text
    public string Place = "", Lat = "", Lon = "";        // Lat/Lon as invariant-culture decimals

    public bool HasPlace { get { return Lat != "" && Lon != ""; } }

    static IEnumerable<FieldInfo> Fields
    {
        get
        {
            return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(int) || f.FieldType == typeof(bool) || f.FieldType == typeof(string));
        }
    }

    // Set when the saved settings exist but couldn't be read. The widget then runs on defaults, and Save does nothing,
    // so those defaults can never overwrite the real settings. Restarting the app tries again.
    public static bool Unreadable;

    public static Settings Load()
    {
        if (Program.Demo)
            return new Settings
            {
                Layer = 2, ThemeName = Program.DemoTheme ?? "Midnight", X = 240, Y = 160,
                Place = "Melbourne", Lat = "-37.81", Lon = "144.96", Fahrenheit = false, Wind = 0, Use24h = false
            };
        Settings s = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) Thread.Sleep(500);
            bool ok;
            s = TryLoad(out ok);
            if (ok) { Unreadable = false; s.Opacity = CommonMenu.Migrate(s.Opacity); return s; }
        }
        Unreadable = true;
        MessageBox.Show("Kinetik Weather couldn't read its saved settings, so it's running on the defaults for now.\n\n" +
            "Your settings haven't been changed, and nothing will be saved over them while this is showing. " +
            "Restart Kinetik Weather to try again" + (Backup.Exists ? ", or use right-click → Backup → Restore settings…" : "."),
            "Kinetik Weather", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return s;
    }

    static Settings TryLoad(out bool ok)
    {
        ok = true;
        var s = new Settings();
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                if (k != null)
                    foreach (var f in Fields)
                    {
                        var v = k.GetValue(f.Name);
                        if (v == null) continue;
                        try
                        {
                            if (f.FieldType == typeof(string)) f.SetValue(s, v.ToString());
                            else if (f.FieldType == typeof(int)) f.SetValue(s, Convert.ToInt32(v));
                            else f.SetValue(s, Convert.ToInt32(v) != 0);
                        }
                        catch { ok = false; }
                    }
        }
        catch { ok = false; }
        KinetikSuite.SettingsValidation.Sanitize(s); return s;
    }

    public void Save()
    {
        if (Program.Demo || Unreadable) return;
        KinetikSuite.SettingsValidation.Sanitize(this);
        if (KinetikSuite.RegistryFiles.CannotSave) return;
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                foreach (var f in Fields)
                {
                    var v = f.GetValue(this);
                    if (f.FieldType == typeof(bool)) k.SetValue(f.Name, (bool)v ? 1 : 0);
                    else if (f.FieldType == typeof(int)) k.SetValue(f.Name, (int)v);
                    else k.SetValue(f.Name, (string)v ?? "");
                }
        }
        catch { }
    }
}

// ======================================================================= Forecast data (Open-Meteo)
class Hour { public DateTime T; public double Temp, Pop; public int Code; public bool Day; }
class DayF { public DateTime Date, Rise, Set; public double Max, Min, Pop; public int Code; }

class Forecast
{
    public double Temp, Feels, Wind, WindDir, Humidity; // °C, km/h, degrees, %
    public int Code, Offset;                            // WMO weather code; the place's offset from UTC in seconds
    public bool IsDay;
    public DateTime Fetched;                            // UTC
    public List<Hour> Hours = new List<Hour>();          // the place's own local times
    public List<DayF> Days = new List<DayF>();

    public DateTime PlaceNow { get { return DateTime.UtcNow.AddSeconds(Offset); } }
}

static class Weather
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Url(string lat, string lon)
    {
        return "https://api.open-meteo.com/v1/forecast?latitude=" + lat + "&longitude=" + lon +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m,wind_direction_10m" +
            "&hourly=temperature_2m,precipitation_probability,weather_code,is_day" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset" +
            "&timezone=auto&forecast_days=8";
    }

    public static string Get(string url) { return Get(url, CancellationToken.None); }
    public static string Get(string url, CancellationToken cancel)
    { return KinetikSuite.Http.Text(url, 4 << 20, "KinetikWeather/" + Program.Version, cancel); }

    static object Val(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) ? o : null; }
    static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { return Val(d, k) as Dictionary<string, object>; }
    static object[] Arr(Dictionary<string, object> d, string k)
    {
        var o = Val(d, k);
        if (o is object[]) return (object[])o;
        var e = o as System.Collections.IEnumerable;
        return e != null && !(o is string) ? e.Cast<object>().ToArray() : new object[0];
    }
    static double D(object o) { if (o == null) return double.NaN; try { return Convert.ToDouble(o, Inv); } catch { return double.NaN; } }
    static double D(object[] a, int i) { return i < a.Length ? D(a[i]) : double.NaN; }
    static int I(object[] a, int i) { var d = D(a, i); return double.IsNaN(d) ? -1 : (int)d; }
    static DateTime Time(object o)
    {
        var s = o as string ?? "";
        return DateTime.ParseExact(s, s.Length > 10 ? "yyyy-MM-dd'T'HH:mm" : "yyyy-MM-dd", Inv);
    }

    public static Forecast Parse(string json)
    {
        var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
        if (root == null) throw new FormatException("Open-Meteo sent something unexpected");
        if (Val(root, "error") is bool && (bool)Val(root, "error")) throw new Exception(Val(root, "reason") as string ?? "Open-Meteo reported an error");
        var f = new Forecast();
        var off = D(Val(root, "utc_offset_seconds"));
        f.Offset = double.IsNaN(off) ? 0 : (int)off;

        var cur = Obj(root, "current");
        f.Temp = D(Val(cur, "temperature_2m")); f.Feels = D(Val(cur, "apparent_temperature"));
        f.Humidity = D(Val(cur, "relative_humidity_2m")); f.Wind = D(Val(cur, "wind_speed_10m")); f.WindDir = D(Val(cur, "wind_direction_10m"));
        var code = D(Val(cur, "weather_code")); f.Code = double.IsNaN(code) ? -1 : (int)code;
        f.IsDay = D(Val(cur, "is_day")) == 1;

        var h = Obj(root, "hourly");
        object[] ht = Arr(h, "time"), temp = Arr(h, "temperature_2m"), pop = Arr(h, "precipitation_probability"), hc = Arr(h, "weather_code"), day = Arr(h, "is_day");
        for (int i = 0; i < ht.Length; i++)
            f.Hours.Add(new Hour { T = Time(ht[i]), Temp = D(temp, i), Pop = D(pop, i), Code = I(hc, i), Day = D(day, i) == 1 });

        var d = Obj(root, "daily");
        object[] dt = Arr(d, "time"), dc = Arr(d, "weather_code"), mx = Arr(d, "temperature_2m_max"), mn = Arr(d, "temperature_2m_min"),
                 dp = Arr(d, "precipitation_probability_max"), rise = Arr(d, "sunrise"), set = Arr(d, "sunset");
        for (int i = 0; i < dt.Length; i++)
        {
            var x = new DayF { Date = Time(dt[i]), Code = I(dc, i), Max = D(mx, i), Min = D(mn, i), Pop = D(dp, i) };
            try { x.Rise = Time(rise[i]); x.Set = Time(set[i]); } catch { } // polar day or night has none
            f.Days.Add(x);
        }
        if (f.Hours.Count == 0 || double.IsNaN(f.Temp)) throw new FormatException("The forecast came back empty");
        return f;
    }

    // ---- place search (Open-Meteo geocoding)
    public class Spot
    {
        public string Name, Label, Lat, Lon;
        public override string ToString() { return Label; }
    }

    public static List<Spot> Search(string q) { return Search(q, CancellationToken.None); }
    public static List<Spot> Search(string q, CancellationToken cancel)
    {
        var json = Get("https://geocoding-api.open-meteo.com/v1/search?count=12&language=en&format=json&name=" + Uri.EscapeDataString(q), cancel);
        var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
        var list = new List<Spot>();
        foreach (var o in Arr(root, "results"))
        {
            var r = o as Dictionary<string, object>;
            if (r == null) continue;
            var name = Val(r, "name") as string ?? "";
            var parts = new[] { name, Val(r, "admin1") as string, Val(r, "country") as string }.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            double la = D(Val(r, "latitude")), lo = D(Val(r, "longitude"));
            if (double.IsNaN(la) || double.IsNaN(lo)) continue;
            list.Add(new Spot { Name = name, Label = string.Join(", ", parts), Lat = la.ToString("0.####", Inv), Lon = lo.ToString("0.####", Inv) });
        }
        return list;
    }

    // "-37.81, 144.96" typed straight in
    public static Spot ParseCoords(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"^\s*(-?\d+(?:\.\d+)?)\s*[,; ]\s*(-?\d+(?:\.\d+)?)\s*$");
        if (!m.Success) return null;
        double la = double.Parse(m.Groups[1].Value, Inv), lo = double.Parse(m.Groups[2].Value, Inv);
        if (Math.Abs(la) > 90 || Math.Abs(lo) > 180) return null;
        var t = la.ToString("0.####", Inv) + ", " + lo.ToString("0.####", Inv);
        return new Spot { Name = t, Label = t + " (coordinates)", Lat = la.ToString("0.####", Inv), Lon = lo.ToString("0.####", Inv) };
    }

    // ---- the last forecast, kept so the widget fills in at once at startup and keeps working offline
    static string CacheFile
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KinetikWeather", "forecast.json"); }
    }

    public static void SaveCache(string lat, string lon, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
            File.WriteAllText(CacheFile, lat + "," + lon + "\n" + json);
        }
        catch { }
    }

    public static Forecast LoadCache(string lat, string lon)
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var text = File.ReadAllText(CacheFile);
            int nl = text.IndexOf('\n');
            if (nl < 0 || text.Substring(0, nl) != lat + "," + lon) return null;
            var f = Parse(text.Substring(nl + 1));
            f.Fetched = File.GetLastWriteTimeUtc(CacheFile);
            return f;
        }
        catch { return null; }
    }

    // ---- WMO weather codes
    public static string Describe(int c, bool day)
    {
        switch (c)
        {
            case 0: return day ? "Sunny" : "Clear";
            case 1: return day ? "Mostly sunny" : "Mostly clear";
            case 2: return "Partly cloudy";
            case 3: return "Cloudy";
            case 45: case 48: return "Fog";
            case 51: case 53: return "Drizzle";
            case 55: return "Heavy drizzle";
            case 56: case 57: return "Freezing drizzle";
            case 61: return "Light rain";
            case 63: return "Rain";
            case 65: return "Heavy rain";
            case 66: case 67: return "Freezing rain";
            case 71: return "Light snow";
            case 73: return "Snow";
            case 75: return "Heavy snow";
            case 77: return "Snow grains";
            case 80: return "Showers";
            case 81: return "Heavy showers";
            case 82: return "Violent showers";
            case 85: case 86: return "Snow showers";
            case 95: return "Thunderstorm";
            case 96: case 99: return "Thunderstorm, hail";
        }
        return "—";
    }

    public static string Compass(double deg)
    {
        if (double.IsNaN(deg)) return "";
        var n = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        return n[(int)Math.Round(((deg % 360) + 360) % 360 / 45) % 8];
    }

    // A made-up forecast for --demo: a mild spring day with showers in the afternoon.
    public static Forecast Demo()
    {
        var f = new Forecast { Temp = 18.4, Feels = 17.1, Humidity = 62, Wind = 14, WindDir = 320, Code = 2, IsDay = true, Fetched = DateTime.UtcNow.AddMinutes(-6) };
        f.Offset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalSeconds;
        var now = f.PlaceNow;
        var start = now.Date;
        for (int i = 0; i < 24 * 8; i++)
        {
            var t = start.AddHours(i);
            double hr = t.Hour + 0;
            double temp = 14 + 6 * Math.Sin((hr - 9) / 24 * 2 * Math.PI) + (i / 24) * 0.6;
            double since = (t - now).TotalHours;
            double pop = since > 2 && since < 9 ? Math.Max(0, 70 - Math.Abs(since - 5) * 18) : 5 + (i * 37 % 11);
            int code = pop >= 50 ? 80 : pop >= 25 ? 3 : (t.Hour > 7 && t.Hour < 18 ? 2 : 1);
            f.Hours.Add(new Hour { T = t, Temp = temp, Pop = Math.Round(pop / 5) * 5, Code = code, Day = t.Hour >= 6 && t.Hour < 20 });
        }
        int[] codes = { 80, 61, 3, 1, 2, 0, 95, 2 };
        double[] max = { 21, 17, 16, 19, 23, 26, 22, 20 }, min = { 11, 10, 9, 8, 12, 14, 15, 12 }, pops = { 70, 85, 30, 5, 10, 0, 60, 15 };
        for (int i = 0; i < 8; i++)
            f.Days.Add(new DayF { Date = start.AddDays(i), Code = codes[i], Max = max[i], Min = min[i], Pop = pops[i], Rise = start.AddDays(i).AddHours(6).AddMinutes(24 - i), Set = start.AddDays(i).AddHours(19).AddMinutes(41 + i) });
        return f;
    }
}

// ======================================================================= Weather icons, drawn rather than bitmaps so they're sharp at any size
static class WxIcon
{
    public static readonly Color Sun = Color.FromArgb(255, 196, 64), Rain = Color.FromArgb(77, 166, 255), Bolt = Color.FromArgb(255, 214, 90);

    public static void Paint(Graphics g, RectangleF r, int code, bool day, bool light)
    {
        float s = Math.Min(r.Width, r.Height), x = r.X + (r.Width - s) / 2, y = r.Y + (r.Height - s) / 2;
        Color cloud = light ? Color.FromArgb(160, 168, 184) : Color.FromArgb(214, 220, 232);
        Color cloudDark = light ? Color.FromArgb(112, 120, 138) : Color.FromArgb(150, 158, 178);
        Color snow = light ? Color.FromArgb(120, 160, 215) : Color.FromArgb(236, 242, 255);

        if (code == 0) { Orb(g, x + s / 2, y + s / 2, s * 0.24f, day, s, light); return; }
        if (code == 1) { Orb(g, x + s * 0.44f, y + s * 0.44f, s * 0.22f, day, s, light); Cloud(g, new RectangleF(x + s * 0.42f, y + s * 0.56f, s * 0.5f, s * 0.32f), cloud); return; }
        if (code == 2) { Orb(g, x + s * 0.36f, y + s * 0.36f, s * 0.2f, day, s, light); Cloud(g, new RectangleF(x + s * 0.22f, y + s * 0.4f, s * 0.7f, s * 0.44f), cloud); return; }
        if (code == 3) { Cloud(g, new RectangleF(x + s * 0.3f, y + s * 0.18f, s * 0.6f, s * 0.38f), cloudDark); Cloud(g, new RectangleF(x + s * 0.08f, y + s * 0.34f, s * 0.78f, s * 0.48f), cloud); return; }
        if (code == 45 || code == 48)
        {
            Cloud(g, new RectangleF(x + s * 0.12f, y + s * 0.12f, s * 0.76f, s * 0.46f), cloud);
            using (var pen = new Pen(cloudDark, s * 0.065f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, x + s * 0.16f, y + s * 0.7f, x + s * 0.84f, y + s * 0.7f);
                g.DrawLine(pen, x + s * 0.26f, y + s * 0.84f, x + s * 0.74f, y + s * 0.84f);
            }
            return;
        }
        bool showers = code >= 80 && code <= 86;
        bool thunder = code >= 95;
        bool snowy = (code >= 71 && code <= 77) || code == 85 || code == 86;
        if (showers) Orb(g, x + s * 0.3f, y + s * 0.3f, s * 0.17f, day, s, light);
        Cloud(g, new RectangleF(x + s * 0.12f, y + s * (showers ? 0.24f : 0.1f), s * 0.76f, s * 0.46f), thunder ? cloudDark : cloud);
        float top = y + s * (showers ? 0.72f : 0.62f);
        if (thunder)
        {
            var p = new[] { new PointF(x + s * 0.54f, top - s * 0.06f), new PointF(x + s * 0.38f, top + s * 0.17f), new PointF(x + s * 0.5f, top + s * 0.17f),
                            new PointF(x + s * 0.42f, top + s * 0.34f), new PointF(x + s * 0.64f, top + s * 0.1f), new PointF(x + s * 0.52f, top + s * 0.1f), new PointF(x + s * 0.6f, top - s * 0.06f) };
            using (var b = new SolidBrush(Bolt)) g.FillPolygon(b, p);
            return;
        }
        int n = code == 51 || code == 61 || code == 71 || code == 80 || code == 85 ? 2 : 3;
        float span = s * 0.5f, x0 = x + s * 0.5f - span / 2;
        for (int i = 0; i < n; i++)
        {
            float dx = x0 + span * (n == 1 ? 0.5f : i / (float)(n - 1)), dy = top + (i % 2) * s * 0.08f;
            if (snowy)
                using (var b = new SolidBrush(snow)) g.FillEllipse(b, dx - s * 0.05f, dy, s * 0.1f, s * 0.1f);
            else
                using (var pen = new Pen(Rain, s * 0.07f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, dx + s * 0.04f, dy, dx - s * 0.03f, dy + (code >= 51 && code <= 57 ? s * 0.08f : s * 0.16f));
        }
    }

    // The sun (with rays when there's room) or a crescent moon.
    static void Orb(Graphics g, float cx, float cy, float rad, bool day, float s, bool light)
    {
        if (day)
        {
            using (var b = new SolidBrush(Sun)) g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
            if (s >= 18)
                using (var pen = new Pen(Sun, Math.Max(1, s * 0.05f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                        g.DrawLine(pen, cx + c * rad * 1.4f, cy + sn * rad * 1.4f, cx + c * rad * 1.75f, cy + sn * rad * 1.75f);
                    }
            return;
        }
        // a crescent: the outer circle's far arc, then back along the arc of the same circle shifted up and right
        float R = rad * 1.15f, dist = R * 0.9f, axis = -45;
        float alpha = (float)(Math.Acos(dist / (2 * R)) * 180 / Math.PI);
        float ox = cx + (float)Math.Cos(axis * Math.PI / 180) * dist, oy = cy + (float)Math.Sin(axis * Math.PI / 180) * dist;
        using (var p = new GraphicsPath())
        {
            p.AddArc(cx - R, cy - R, 2 * R, 2 * R, axis + alpha, 360 - 2 * alpha);
            p.AddArc(ox - R, oy - R, 2 * R, 2 * R, axis + 180 + alpha, -2 * alpha);
            p.CloseFigure();
            using (var b = new SolidBrush(light ? Color.FromArgb(150, 160, 190) : Color.FromArgb(222, 226, 240))) g.FillPath(b, p);
        }
    }

    // A cloud filling r: three bumps on a rounded base.
    static void Cloud(Graphics g, RectangleF r, Color col)
    {
        using (var p = new GraphicsPath { FillMode = FillMode.Winding })
        {
            float w = r.Width, h = r.Height;
            p.AddPath(Draw.Round(new RectangleF(r.X, r.Y + h * 0.42f, w, h * 0.58f), h * 0.29f), false);
            p.AddEllipse(r.X + w * 0.12f, r.Y + h * 0.28f, h * 0.62f, h * 0.62f);
            p.AddEllipse(r.X + w * 0.32f, r.Y, h * 0.86f, h * 0.86f);
            p.AddEllipse(r.X + w * 0.6f, r.Y + h * 0.3f, h * 0.58f, h * 0.58f);
            using (var b = new SolidBrush(col)) g.FillPath(b, p);
        }
    }
}

// ======================================================================= The widget
class WeatherForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    readonly Settings c = Settings.Load();
    readonly DibSurface surface = new DibSurface();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer(), fetchTimer = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = KinetikSuite.MenuFocus.Attach(new ContextMenuStrip());
    int layer, winW, winH, margin;
    float u = 1;
    bool dragging, moved, fetching;
    Point grab, downAt;
    ThemePicker picker;
    Forecast data;
    string lastError;

    public WeatherForm()
    {
        layer = c.Layer;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Kinetik Weather";
        menu.Opening += (s, e) => BuildMenu();
        try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { tray.Icon = SystemIcons.Application; }
        tray.Text = Program.Demo ? "Kinetik Weather (demo)" : "Kinetik Weather";
        Backup.BeforeExit = () => tray.Visible = false;
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (s, e) => Fetch();
        tray.Visible = Program.ShotPath == null;
        tick.Tick += (s, e) => { Tick(); Render(); };
        fetchTimer.Tick += (s, e) => { fetchTimer.Stop(); Fetch(); };

        if (Program.Demo && Program.DemoPlace != null)
        {
            var sp = Weather.ParseCoords(Program.DemoPlace);
            if (sp != null) { c.Lat = sp.Lat; c.Lon = sp.Lon; c.Place = "Live demo"; }
            try { data = Weather.Parse(Weather.Get(Weather.Url(c.Lat, c.Lon))); data.Fetched = DateTime.UtcNow; }
            catch (Exception ex) { lastError = Friendly(ex); }
        }
        else if (Program.Demo) data = Weather.Demo();
        else if (c.HasPlace) data = Weather.LoadCache(c.Lat, c.Lon);

        SystemEvents.SessionSwitch += OnSession;
        SystemEvents.PowerModeChanged += OnPower;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetwork;
    }

    // Nothing redraws while the PC is locked; it catches up (and fetches if the forecast is old) on unlock.
    void OnSession(object s, SessionSwitchEventArgs e)
    {
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock) tick.Stop();
                else if (e.Reason == SessionSwitchReason.SessionUnlock) { Tick(); Render(); if (Stale) Fetch(); }
            }));
        }
        catch { }
    }

    // After sleep the network takes a moment to come back, so the fetch waits a little.
    void OnPower(object s, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        try { BeginInvoke((Action)(() => { Tick(); Render(); ScheduleFetchSeconds(15); })); } catch { }
    }

    void OnNetwork(object s, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        try { BeginInvoke((Action)(() => { if (lastError != null || Stale) ScheduleFetchSeconds(5); })); } catch { }
    }

    bool Stale { get { return data == null || (DateTime.UtcNow - data.Fetched).TotalMinutes >= c.Refresh; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
            if (layer == 2) cp.ExStyle |= 0x8;          // TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE → MA_NOACTIVATE
        if (m.Msg == 0x46 && layer == 0) // WM_WINDOWPOSCHANGING: "on the desktop" keeps it underneath every other window
        {
            var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
            if ((wp.flags & 0x4) == 0) { wp.hwndInsertAfter = new IntPtr(1); Marshal.StructureToPtr(wp, m.LParam, false); }
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Tick();
        Render();
        if (Program.ShotPath != null) { BeginInvoke((Action)SaveShot); return; }
        if (!Program.Demo)
        {
            if (!c.HasPlace) BeginInvoke((Action)ChoosePlace);
            else if (Stale) Fetch();
            else ScheduleFetchSeconds((int)Math.Max(5, c.Refresh * 60 - (DateTime.UtcNow - data.Fetched).TotalSeconds));
        }
    }

    void SaveShot()
    {
        try { surface.Save(Program.ShotPath); } catch { }
        tray.Visible = false;
        Application.Exit();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) CreateHandle();
        base.SetVisibleCore(false); // shown by Render() with SW_SHOWNA, so it never steals focus
        if (value) BeginInvoke((Action)Render);
    }

    protected override void Dispose(bool disposing)
    {
        surface.Dispose();
        if (disposing)
        {
            SystemEvents.SessionSwitch -= OnSession; SystemEvents.PowerModeChanged -= OnPower;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
            tick.Dispose(); fetchTimer.Dispose(); tray.Dispose(); menu.Dispose(); DisposeFonts(); shadow.Dispose(); if (textLayer != null) textLayer.Dispose();
        }
        base.Dispose(disposing);
    }

    // Redraws just after each minute turns over: "updated 5 min ago", the sun's place on its arc, the hour rolling on.
    void Tick()
    {
        var now = DateTime.Now;
        tick.Interval = Math.Max(50, (60 - now.Second) * 1000 - now.Millisecond + 20);
        tick.Start();
    }

    // ---- fetching
    void ScheduleFetchSeconds(int seconds)
    {
        if (Program.Demo) return;
        fetchTimer.Stop();
        fetchTimer.Interval = Math.Max(1000, seconds * 1000);
        fetchTimer.Start();
    }

    void Fetch()
    {
        if (Program.Demo || !c.HasPlace || fetching) return;
        fetching = true;
        fetchTimer.Stop();
        string lat = c.Lat, lon = c.Lon;
        if (data == null) Render();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Forecast f = null; string json = null, err = null;
            try { json = Weather.Get(Weather.Url(lat, lon)); f = Weather.Parse(json); f.Fetched = DateTime.UtcNow; }
            catch (Exception ex) { err = Friendly(ex); }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    fetching = false;
                    if (lat != c.Lat || lon != c.Lon) { Fetch(); return; } // the place changed meanwhile
                    if (f != null) { data = f; lastError = null; Weather.SaveCache(lat, lon, json); }
                    else lastError = err;
                    ScheduleFetchSeconds(f != null ? c.Refresh * 60 : Math.Min(5, c.Refresh) * 60);
                    Render();
                }));
            }
            catch { }
        });
    }

    public static string Friendly(Exception ex)
    {
        var we = ex as WebException;
        if (we != null)
        {
            if (we.Status == WebExceptionStatus.NameResolutionFailure || we.Status == WebExceptionStatus.ConnectFailure) return "No internet connection";
            if (we.Status == WebExceptionStatus.Timeout) return "Open-Meteo didn't answer in time";
            var hr = we.Response as HttpWebResponse;
            if (hr != null) return "Open-Meteo answered " + (int)hr.StatusCode + " " + hr.StatusDescription;
            return "Couldn't reach Open-Meteo";
        }
        return ex.Message;
    }

    // ---- mouse: drag to move, double-click for the radar map, right-click for the menu
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            downAt = Cursor.Position; moved = false;
            if (!c.Locked) { dragging = true; grab = new Point(Cursor.Position.X - c.X, Cursor.Position.Y - c.Y); }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging && (moved || Math.Abs(Cursor.Position.X - downAt.X) + Math.Abs(Cursor.Position.Y - downAt.Y) > 4))
        {
            moved = true;
            var p = Snap(new Point(Cursor.Position.X - grab.X, Cursor.Position.Y - grab.Y));
            c.X = p.X; c.Y = p.Y;
            SetWindowPos(Handle, IntPtr.Zero, p.X, p.Y, 0, 0, 0x1 | 0x4 | 0x10); // NOSIZE | NOZORDER | NOACTIVATE
            Cursor = Cursors.SizeAll;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (dragging && moved) c.Save();
            dragging = false;
            Cursor = Cursors.Default;
        }
        else if (e.Button == MouseButtons.Right) menu.Show(Cursor.Position);
        base.OnMouseUp(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !moved) OpenMap();
        base.OnMouseDoubleClick(e);
    }

    void OpenMap()
    {
        if (!c.HasPlace) { ChoosePlace(); return; }
        try { System.Diagnostics.Process.Start("https://www.windy.com/" + c.Lat + "/" + c.Lon + "/radar?radar," + c.Lat + "," + c.Lon + ",8"); } catch { }
    }

    Point Snap(Point p)
    {
        if (!c.Snap) return p;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int gap = (int)(12 * u), reach = (int)(18 * u);
        int left = p.X + margin, top = p.Y + margin, right = p.X + winW - margin, bottom = p.Y + winH - margin;
        if (Math.Abs(left - (wa.Left + gap)) < reach) p.X = wa.Left + gap - margin;
        else if (Math.Abs(right - (wa.Right - gap)) < reach) p.X = wa.Right - gap - winW + margin;
        if (Math.Abs(top - (wa.Top + gap)) < reach) p.Y = wa.Top + gap - margin;
        else if (Math.Abs(bottom - (wa.Bottom - gap)) < reach) p.Y = wa.Bottom - gap - winH + margin;
        return p;
    }

    void EnsurePosition()
    {
        if (c.X == int.MinValue || !Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(c.X, c.Y, winW, winH))))
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int gap = (int)(12 * u);
            c.X = wa.Right - gap - winW + margin;
            c.Y = wa.Top + gap - margin;
        }
    }

    // ---- formatting
    string Temp(double celsius)
    {
        if (double.IsNaN(celsius)) return "–";
        var v = c.Fahrenheit ? celsius * 9 / 5 + 32 : celsius;
        var s = Math.Round(v).ToString("0");
        if (s == "-0") s = "0";
        return s.Replace("-", "−") + "°";
    }

    string WindText(double kmh)
    {
        if (double.IsNaN(kmh)) return "";
        if (c.Wind == 1) return Math.Round(kmh / 1.609).ToString("0") + " mph";
        if (c.Wind == 2) return Math.Round(kmh / 3.6).ToString("0") + " m/s";
        return Math.Round(kmh).ToString("0") + " km/h";
    }

    // With full = false, the 12-hour clock leaves off am/pm (the strip shows it only where it changes, to fit 12 columns).
    string HourText(DateTime t, bool full = true)
    {
        if (c.Use24h) return t.ToString("HH");
        int h = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        return h + (full || h == 12 ? (t.Hour < 12 ? "am" : "pm") : "");
    }

    string ClockText(DateTime t) { return c.Use24h ? t.ToString("H:mm") : t.ToString("h:mm") + (t.Hour < 12 ? "am" : "pm"); }

    static string Ago(DateTime utc)
    {
        var m = (DateTime.UtcNow - utc).TotalMinutes;
        if (m < 1) return "just now";
        if (m < 60) return (int)m + " min ago";
        if (m < 60 * 48) return (int)(m / 60) + " h ago";
        return (int)(m / 1440) + " days ago";
    }

    // The hours to show: from the current one, every hour (or every other one for 24).
    List<Hour> ShownHours()
    {
        if (data == null) return new List<Hour>();
        var now = data.PlaceNow;
        var first = data.Hours.FindIndex(h => h.T.AddHours(1) > now);
        if (first < 0) return new List<Hour>();
        int step = c.Hours > 12 ? 2 : 1, n = Math.Min(12, c.Hours / step);
        var list = new List<Hour>();
        for (int i = first; i < data.Hours.Count && list.Count < n; i += step) list.Add(data.Hours[i]);
        return list;
    }

    List<DayF> ShownDays()
    {
        if (data == null) return new List<DayF>();
        var today = data.PlaceNow.Date;
        return data.Days.Where(d => d.Date >= today).Take(c.Days).ToList();
    }

    DayF Today { get { if (data == null) return null; var t = data.PlaceNow.Date; return data.Days.FirstOrDefault(d => d.Date == t); } }

    // ---- layout: every section's height, so the card is sized before it's drawn
    const float HeaderH = 22, NowH = 66, HourlyH = 58, RainH = 44, SunH = 50, DayRowH = 25, Gap = 12;

    float ContentHeight()
    {
        if (data == null) return 22 + 48;
        float h = HeaderH + NowH;
        if (c.ShowHourly && ShownHours().Count > 0) h += Gap + HourlyH;
        if (c.ShowRain && ShownHours().Count > 0) h += (c.ShowHourly ? 2 : Gap) + RainH;
        if (c.ShowSun && Today != null && Today.Rise != DateTime.MinValue) h += Gap + SunH;
        if (c.ShowDaily) { var n = ShownDays().Count; if (n > 0) h += Gap + n * DayRowH; }
        return h;
    }

    // ---- painting
    public void Render()
    {
        if (IsDisposed || !IsHandleCreated) return;
        u = DeviceDpi / 96f * c.Scale / 100f;
        margin = (int)(14 * u);
        float pad = 14 * u;
        int cardW = (int)(c.Width * u), cardH = (int)(pad * 2 + ContentHeight() * u);
        winW = cardW + margin * 2; winH = cardH + margin * 2;

        bool wantThrough = c.Locked && c.ClickThrough;
        int ex = GetWindowLong(Handle, -20);
        SetWindowLong(Handle, -20, wantThrough ? ex | 0x20 : ex & ~0x20); // TRANSPARENT: clicks fall through

        EnsurePosition();
        var bmp = surface.Canvas(winW, winH);
        using (var g = Graphics.FromImage(bmp)) PaintCard(g, cardW, cardH, pad);
        surface.Push(Handle, c.X, c.Y);
        if (Program.ShotPath != null) return;
        ShowWindow(Handle, 8); // SW_SHOWNA
        if (layer == 0) SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
        else if (layer == 2) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
    }

    Bitmap textLayer;
    readonly ShadowCache shadow = new ShadowCache();
    string Fam { get { return c.FontName != "" && Draw.HasFont(c.FontName) ? c.FontName : Draw.Family; } }

    // Fonts by size and style, made again only when the font or scale changes.
    readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    string fontKey;

    Font F(float size, FontStyle style = FontStyle.Regular)
    {
        var key = c.FontName + "|" + u + "|" + DeviceDpi;
        if (key != fontKey) { DisposeFonts(); fontKey = key; }
        var k = size + "/" + (int)style;
        Font f;
        if (!fonts.TryGetValue(k, out f)) fonts[k] = f = new Font(Fam, size * u / (DeviceDpi / 96f), style, GraphicsUnit.Point);
        return f;
    }

    void DisposeFonts() { KinetikSuite.Resources.DisposeValues(fonts); fontKey = null; }

    static readonly StringFormat fmtL = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat fmtR = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat fmtC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

    void PaintCard(Graphics target, int cardW, int cardH, float pad)
    {
        var th = Theme.Get(c.ThemeName);
        target.Clear(Color.Transparent);
        target.SmoothingMode = SmoothingMode.AntiAlias;
        target.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var card = new RectangleF(margin, margin, cardW, cardH);
        float rad = 16 * u;
        shadow.Paint(target, winW, winH, card + "|" + u + "|" + c.Opacity, sg =>
        {
            for (int i = 1; i <= 6; i++)
                using (var p = Draw.Round(RectangleF.Inflate(card, i * 1.6f * u, i * 1.6f * u), rad + i * 1.6f * u))
                using (var b = new SolidBrush(Color.FromArgb((int)(14 * c.Opacity / 100f / i), 0, 0, 0))) sg.FillPath(b, p);
        });

        // ClearType needs solid pixels behind the text: a mostly opaque card is drawn solid on a layer that is then
        // blended in at the card's opacity. A very see-through card keeps grayscale text.
        int alpha = 255 * c.Opacity / 100;
        bool crisp = alpha >= 150;
        var g = target;
        if (crisp)
        {
            if (textLayer == null || textLayer.Width != winW || textLayer.Height != winH) { if (textLayer != null) textLayer.Dispose(); textLayer = new Bitmap(winW, winH, PixelFormat.Format32bppPArgb); }
            g = Graphics.FromImage(textLayer);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.TextContrast = 2;
        }
        else g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        try { PaintContent(g, th, card, rad, crisp ? 255 : alpha, pad); }
        finally
        {
            if (crisp)
            {
                g.Dispose();
                if (alpha >= 255) target.DrawImageUnscaled(textLayer, 0, 0);
                else
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha / 255f });
                        target.DrawImage(textLayer, new Rectangle(0, 0, textLayer.Width, textLayer.Height), 0, 0, textLayer.Width, textLayer.Height, GraphicsUnit.Pixel, ia);
                    }
            }
        }
    }

    void PaintContent(Graphics g, Theme th, RectangleF card, float rad, int alpha, float pad)
    {
        using (var p = Draw.Round(card, rad))
        {
            using (var b = new SolidBrush(Color.FromArgb(alpha, th.Bg))) g.FillPath(b, p);
            using (var pen = new Pen(th.Line, 1)) g.DrawPath(pen, p);
        }

        using (var text = new SolidBrush(th.Text))
        using (var sub = new SolidBrush(th.Sub))
        {
            float x = card.X + pad, w = card.Width - pad * 2, y = card.Y + pad;

            // header: the place, and how fresh the forecast is
            string status = fetching && data == null ? "Updating…"
                : lastError != null ? (data == null ? "" : "Offline · " + Ago(data.Fetched))
                : data != null ? "Updated " + Ago(data.Fetched) : "";
            var sw = status == "" ? 0 : g.MeasureString(status, F(8.5f), 400, fmtR).Width + 4 * u;
            using (var warn = new SolidBrush(lastError != null ? th.Accent : th.Sub))
                g.DrawString(status, F(8.5f), warn, new RectangleF(x + w - sw, y, sw, HeaderH * u), fmtR);
            g.DrawString(c.HasPlace ? (c.Place == "" ? "Weather" : c.Place) : "Kinetik Weather", F(11, FontStyle.Bold), text, new RectangleF(x, y, w - sw - 6 * u, HeaderH * u), fmtL);
            y += HeaderH * u;

            if (data == null)
            {
                string msg = !c.HasPlace ? "Right-click → Set location… to choose a place."
                    : lastError != null ? lastError + ". Trying again in a few minutes." : "Getting the forecast…";
                g.DrawString(msg, F(9.5f), sub, new RectangleF(x, y + 6 * u, w, 40 * u), new StringFormat { Trimming = StringTrimming.EllipsisWord });
                return;
            }

            PaintNow(g, th, text, sub, x, y, w);
            y += NowH * u;

            var hours = ShownHours();
            float colW = hours.Count > 0 ? w / hours.Count : 0;
            if (c.ShowHourly && hours.Count > 0)
            {
                y += Gap * u;
                Rule(g, th, x, y - Gap * u / 2, w);
                for (int i = 0; i < hours.Count; i++)
                {
                    var h = hours[i];
                    var col = new RectangleF(x + i * colW, y, colW, HourlyH * u);
                    bool full = i == 1 || (i > 1 && (hours[i - 1].T.Hour < 12) != (h.T.Hour < 12)) || hours.Count <= 8;
                    g.DrawString(i == 0 ? "Now" : HourText(h.T, full), F(8), i == 0 ? text : sub, new RectangleF(col.X - 6 * u, col.Y, col.Width + 12 * u, 16 * u), fmtC);
                    float isz = Math.Min(22 * u, colW - 2 * u);
                    WxIcon.Paint(g, new RectangleF(col.X + (colW - isz) / 2, col.Y + 18 * u, isz, isz), h.Code, h.Day, th.Light);
                    g.DrawString(Temp(i == 0 ? data.Temp : h.Temp), F(9, FontStyle.Bold), text, new RectangleF(col.X - 4 * u, col.Y + 42 * u, col.Width + 8 * u, 16 * u), fmtC);
                }
                y += HourlyH * u;
            }

            if (c.ShowRain && hours.Count > 0)
            {
                y += (c.ShowHourly ? 2 : Gap) * u;
                if (!c.ShowHourly) Rule(g, th, x, y - Gap * u / 2, w);
                PaintRain(g, th, sub, hours, x, y, w, colW);
                y += RainH * u;
            }

            var today = Today;
            if (c.ShowSun && today != null && today.Rise != DateTime.MinValue)
            {
                y += Gap * u;
                Rule(g, th, x, y - Gap * u / 2, w);
                PaintSun(g, th, text, sub, today, x, y, w);
                y += SunH * u;
            }

            var days = ShownDays();
            if (c.ShowDaily && days.Count > 0)
            {
                y += Gap * u;
                Rule(g, th, x, y - Gap * u / 2, w);
                PaintDays(g, th, text, sub, days, x, y, w);
            }
        }
    }

    void Rule(Graphics g, Theme th, float x, float y, float w) { using (var pen = new Pen(th.Line, 1)) g.DrawLine(pen, x, y, x + w, y); }

    Color RainCol(Theme th) { return th.Light ? Color.FromArgb(40, 120, 230) : WxIcon.Rain; }

    // a big icon and temperature, what it's like, and the day's high and low with the wind
    void PaintNow(Graphics g, Theme th, Brush text, Brush sub, float x, float y, float w)
    {
        float isz = 52 * u;
        WxIcon.Paint(g, new RectangleF(x - 2 * u, y + (NowH * u - isz) / 2, isz, isz), data.Code, data.IsDay, th.Light);
        float tx = x + isz + 8 * u;
        var big = F(27);
        var t = Temp(data.Temp);
        var tw = g.MeasureString(t, big, 400, fmtL).Width;
        g.DrawString(t, big, text, new RectangleF(tx, y, tw + 4 * u, NowH * u), fmtL);

        float rx = tx + tw + 8 * u, rw = x + w - rx;
        var today = Today;
        string hl = today != null ? "H " + Temp(today.Max) + "  L " + Temp(today.Min) : "";
        string wind = WindText(data.Wind);
        if (wind != "") wind = "Wind " + wind + " " + Weather.Compass(data.WindDir);
        float lh = 17 * u, top = y + (NowH * u - lh * 3) / 2;
        g.DrawString(Weather.Describe(data.Code, data.IsDay), F(10.5f, FontStyle.Bold), text, new RectangleF(rx, top, rw, lh), fmtR);
        g.DrawString("Feels " + Temp(data.Feels) + (hl != "" ? "  ·  " + hl : ""), F(8.5f), sub, new RectangleF(rx, top + lh, rw, lh), fmtR);
        g.DrawString(wind + (double.IsNaN(data.Humidity) ? "" : "  ·  " + Math.Round(data.Humidity) + "%"), F(8.5f), sub, new RectangleF(rx, top + lh * 2, rw, lh), fmtR);
    }

    // a line saying when rain is most likely, over a bar per hour for its chance
    void PaintRain(Graphics g, Theme th, Brush sub, List<Hour> hours, float x, float y, float w, float colW)
    {
        var rain = RainCol(th);
        var best = hours.Where(h => !double.IsNaN(h.Pop)).OrderByDescending(h => h.Pop).FirstOrDefault();
        string span = c.Hours > 12 ? "24 hours" : c.Hours + " hours";
        string line = best == null ? "No chance of rain given"
            : best.Pop >= 50 ? "Rain likely around " + (best == hours[0] ? "now" : HourText(best.T)) + " · " + best.Pop + "%"
            : best.Pop >= 20 ? "Chance of rain around " + (best == hours[0] ? "now" : HourText(best.T)) + " · " + best.Pop + "%"
            : "No rain expected in the next " + span;
        float d = 9 * u;
        using (var b = new SolidBrush(rain))
        using (var p = new GraphicsPath())
        {
            // a small drop
            float dx = x + 1 * u, dy = y + 4 * u;
            p.AddBezier(dx + d / 2, dy, dx + d * 0.1f, dy + d * 0.45f, dx, dy + d * 0.6f, dx, dy + d * 0.72f);
            p.AddArc(dx, dy + d * 0.44f, d, d * 0.56f, 180, -180);
            p.AddBezier(dx + d, dy + d * 0.72f, dx + d, dy + d * 0.6f, dx + d * 0.9f, dy + d * 0.45f, dx + d / 2, dy);
            g.FillPath(b, p);
        }
        g.DrawString(line, F(8.5f), sub, new RectangleF(x + d + 6 * u, y, w - d - 6 * u, 16 * u), fmtL);

        float barTop = y + 20 * u, barH = RainH * u - 22 * u;
        using (var track = new SolidBrush(Color.FromArgb(th.Light ? 18 : 22, th.Text)))
        using (var fill = new SolidBrush(rain))
            for (int i = 0; i < hours.Count; i++)
            {
                float bw = Math.Max(3 * u, Math.Min(colW * 0.5f, 12 * u)), bx = x + i * colW + (colW - bw) / 2;
                using (var p = Draw.Round(new RectangleF(bx, barTop, bw, barH), bw / 2)) g.FillPath(track, p);
                double pop = hours[i].Pop;
                if (double.IsNaN(pop) || pop < 10) continue;
                float bh = Math.Max(bw, barH * (float)Math.Min(1, pop / 100));
                using (var p = Draw.Round(new RectangleF(bx, barTop + barH - bh, bw, bh), bw / 2))
                using (var b = new SolidBrush(Color.FromArgb((int)(110 + 145 * Math.Min(1, pop / 100)), rain))) g.FillPath(b, p);
            }
    }

    // the sun's path across today, with where it is now, sunrise and sunset under the ends
    void PaintSun(Graphics g, Theme th, Brush text, Brush sub, DayF today, float x, float y, float w)
    {
        var now = data.PlaceNow;
        float arcW = w * 0.46f, ax = x + (w - arcW) / 2, ay = y + 4 * u, ah = SunH * u - 22 * u;
        var arc = new RectangleF(ax, ay, arcW, ah * 2);
        using (var pen = new Pen(Color.FromArgb(th.Light ? 60 : 70, th.Sub), 1.4f * u) { DashStyle = DashStyle.Dot })
            g.DrawArc(pen, arc, 180, 180);
        using (var pen = new Pen(th.Line, 1)) g.DrawLine(pen, ax - 10 * u, ay + ah, ax + arcW + 10 * u, ay + ah);

        double f = (now - today.Rise).TotalMinutes / Math.Max(1, (today.Set - today.Rise).TotalMinutes);
        if (f > 0 && f < 1)
        {
            // the part already travelled, then the sun itself
            using (var pen = new Pen(Color.FromArgb(200, WxIcon.Sun), 1.8f * u)) g.DrawArc(pen, arc, 180, (float)(180 * f));
            double a = Math.PI * (1 - f);
            float sx = ax + arcW / 2 + (float)Math.Cos(a) * arcW / 2, sy = ay + ah - (float)Math.Sin(a) * ah, r = 5 * u;
            using (var b = new SolidBrush(Color.FromArgb(60, WxIcon.Sun))) g.FillEllipse(b, sx - r * 1.8f, sy - r * 1.8f, r * 3.6f, r * 3.6f);
            using (var b = new SolidBrush(WxIcon.Sun)) g.FillEllipse(b, sx - r, sy - r, r * 2, r * 2);
        }

        float lw = (w - arcW) / 2 - 6 * u;
        var lf = F(8);
        g.DrawString("Sunrise", lf, sub, new RectangleF(x, y + 2 * u, lw, 15 * u), fmtL);
        g.DrawString(ClockText(today.Rise), F(10, FontStyle.Bold), text, new RectangleF(x, y + 17 * u, lw, 18 * u), fmtL);
        g.DrawString("Sunset", lf, sub, new RectangleF(x + w - lw, y + 2 * u, lw, 15 * u), fmtR);
        g.DrawString(ClockText(today.Set), F(10, FontStyle.Bold), text, new RectangleF(x + w - lw, y + 17 * u, lw, 18 * u), fmtR);

        var len = today.Set - today.Rise;
        string note;
        if (now < today.Rise) note = "Sunrise in " + Span(today.Rise - now);
        else if (now < today.Set) note = Span(today.Set - now) + " of daylight left";
        else note = Span(len) + " of daylight today";
        g.DrawString(note, lf, sub, new RectangleF(x, y + SunH * u - 16 * u, w, 15 * u), fmtC);
    }

    static string Span(TimeSpan t)
    {
        int h = (int)t.TotalHours, m = t.Minutes;
        return h > 0 ? h + " h " + m + " min" : m + " min";
    }

    // one row a day: name, icon, chance of rain, and the low-to-high range against the whole week's
    void PaintDays(Graphics g, Theme th, Brush text, Brush sub, List<DayF> days, float x, float y, float w)
    {
        double lo = days.Where(d => !double.IsNaN(d.Min)).Select(d => d.Min).DefaultIfEmpty(0).Min();
        double hi = days.Where(d => !double.IsNaN(d.Max)).Select(d => d.Max).DefaultIfEmpty(1).Max();
        if (hi - lo < 1) hi = lo + 1;
        var rain = RainCol(th);
        float nameW = 62 * u, iconW = 26 * u, popW = 36 * u, tW = 32 * u;
        float barX = x + nameW + iconW + popW + tW, barW = Math.Max(20 * u, w - (nameW + iconW + popW + tW * 2) - 8 * u);
        var today = data.PlaceNow.Date;
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            float ry = y + i * DayRowH * u, rh = DayRowH * u;
            string name = d.Date == today ? "Today" : d.Date.ToString("ddd");
            g.DrawString(name, F(9.5f, d.Date == today ? FontStyle.Bold : FontStyle.Regular), text, new RectangleF(x, ry, nameW, rh), fmtL);
            float isz = 18 * u;
            WxIcon.Paint(g, new RectangleF(x + nameW, ry + (rh - isz) / 2, isz, isz), d.Code, true, th.Light);
            if (!double.IsNaN(d.Pop) && d.Pop >= 20)
                using (var b = new SolidBrush(rain)) g.DrawString(Math.Round(d.Pop) + "%", F(8.5f, FontStyle.Bold), b, new RectangleF(x + nameW + iconW, ry, popW, rh), fmtL);
            g.DrawString(Temp(d.Min), F(9.5f), sub, new RectangleF(barX - tW - 4 * u, ry, tW, rh), fmtR);
            g.DrawString(Temp(d.Max), F(9.5f, FontStyle.Bold), text, new RectangleF(barX + barW + 4 * u, ry, tW, rh), fmtL);

            float bh = 5 * u, by = ry + (rh - bh) / 2;
            using (var p = Draw.Round(new RectangleF(barX, by, barW, bh), bh / 2))
            using (var b = new SolidBrush(Color.FromArgb(th.Light ? 22 : 26, th.Text))) g.FillPath(b, p);
            if (double.IsNaN(d.Min) || double.IsNaN(d.Max)) continue;
            float s0 = barX + barW * (float)((d.Min - lo) / (hi - lo)), s1 = barX + barW * (float)((d.Max - lo) / (hi - lo));
            if (s1 - s0 < bh) s1 = s0 + bh;
            var seg = new RectangleF(s0, by, s1 - s0, bh);
            using (var p = Draw.Round(seg, bh / 2))
            using (var b = new LinearGradientBrush(new RectangleF(barX - 1, by, barW + 2, bh), TempColour(lo), TempColour(hi), 0f))
                g.FillPath(b, p);
            if (d.Date == today && !double.IsNaN(data.Temp))
            {
                float nx = barX + barW * (float)Math.Max(0, Math.Min(1, (data.Temp - lo) / (hi - lo)));
                using (var b = new SolidBrush(th.Text)) g.FillEllipse(b, nx - bh * 0.8f, by - bh * 0.3f, bh * 1.6f, bh * 1.6f);
                using (var pen = new Pen(th.Bg, 1.2f * u)) g.DrawEllipse(pen, nx - bh * 0.8f, by - bh * 0.3f, bh * 1.6f, bh * 1.6f);
            }
        }
    }

    // cold blue through mild green to hot orange-red, by °C
    static Color TempColour(double celsius)
    {
        var stops = new[] { Tuple.Create(-5.0, Color.FromArgb(120, 150, 255)), Tuple.Create(8.0, Color.FromArgb(90, 190, 240)), Tuple.Create(17.0, Color.FromArgb(120, 210, 140)),
                            Tuple.Create(25.0, Color.FromArgb(255, 196, 64)), Tuple.Create(35.0, Color.FromArgb(250, 110, 80)) };
        if (celsius <= stops[0].Item1) return stops[0].Item2;
        for (int i = 1; i < stops.Length; i++)
            if (celsius <= stops[i].Item1)
                return Draw.Mix(stops[i - 1].Item2, stops[i].Item2, (float)((celsius - stops[i - 1].Item1) / (stops[i].Item1 - stops[i - 1].Item1)));
        return stops[stops.Length - 1].Item2;
    }

    // ---- menu
    ToolStripMenuItem Item(string text, bool check, Action click)
    {
        var it = new ToolStripMenuItem(text) { Checked = check };
        it.Click += (s, e) => click();
        return it;
    }

    void Changed() { c.Save(); Render(); }

    // The look, placement, startup and backup options, shared word for word with the other Kinetik apps.
    CommonMenu Common()
    {
        return new CommonMenu
        {
            Title = "Kinetik Weather", Version = Program.Version,
            OpenTheme = () =>
            {
                if (picker != null && !picker.IsDisposed) { picker.Activate(); return; }
                picker = new ThemePicker(c.ThemeName, n => { c.ThemeName = n; Changed(); });
                picker.Show();
            },
            Scale = () => c.Scale, SetScale = v => { c.Scale = v; Changed(); },
            Widths = new[] { 300, 340, 400 }, CardWidth = () => c.Width, SetCardWidth = v => { c.Width = v; Changed(); },
            FontName = () => c.FontName, SetFontName = v => { c.FontName = v; Changed(); },
            Opacity = () => c.Opacity, SetOpacity = v => { c.Opacity = v; Changed(); },
            Layer = () => c.Layer, SetLayer = SetLayer,
            Locked = () => c.Locked, SetLocked = v => { c.Locked = v; Changed(); },
            ClickThrough = () => c.ClickThrough, SetClickThrough = v => { c.ClickThrough = v; Changed(); },
            Snap = () => c.Snap, SetSnap = v => { c.Snap = v; Changed(); },
            RunAtStartup = () => Startup.IsOn, SetRunAtStartup = Startup.Set,
            BackUp = () => { c.Save(); Backup.SaveNow(); }, Restore = Backup.RestoreNow, LastBackup = () => Backup.SavedText,
            Exit = () => { tray.Visible = false; Application.Exit(); },
        };
    }

    void BuildMenu()
    {
        var old = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var it in old) it.Dispose();
        menu.Items.Add(Item(fetching ? "Updating…" : "Refresh now", false, Fetch));
        menu.Items[0].Enabled = !fetching && c.HasPlace && !Program.Demo;
        menu.Items.Add(Item("Set location…", false, ChoosePlace));
        menu.Items.Add(Item("Open the radar map", false, OpenMap));
        menu.Items.Add(Item("Weather data by Open-Meteo.com", false, () => { try { System.Diagnostics.Process.Start("https://open-meteo.com/"); } catch { } }));
        menu.Items.Add(new ToolStripSeparator());

        var show = new ToolStripMenuItem("Show");
        show.DropDownItems.Add(Item("Next hours", c.ShowHourly, () => { c.ShowHourly = !c.ShowHourly; Changed(); }));
        show.DropDownItems.Add(Item("Chance of rain", c.ShowRain, () => { c.ShowRain = !c.ShowRain; Changed(); }));
        show.DropDownItems.Add(Item("Sunrise and sunset", c.ShowSun, () => { c.ShowSun = !c.ShowSun; Changed(); }));
        show.DropDownItems.Add(Item("Days ahead", c.ShowDaily, () => { c.ShowDaily = !c.ShowDaily; Changed(); }));
        show.DropDownItems.Add(new ToolStripSeparator());
        foreach (var n in new[] { 6, 8, 12, 24 }) { int v = n; show.DropDownItems.Add(Item("Next " + v + " hours" + (v == 24 ? " (every other hour)" : ""), c.Hours == v, () => { c.Hours = v; Changed(); })); }
        show.DropDownItems.Add(new ToolStripSeparator());
        foreach (var n in new[] { 3, 5, 7 }) { int v = n; show.DropDownItems.Add(Item(v + " days", c.Days == v, () => { c.Days = v; Changed(); })); }
        menu.Items.Add(show);

        var units = new ToolStripMenuItem("Units");
        units.DropDownItems.Add(Item("°C", !c.Fahrenheit, () => { c.Fahrenheit = false; Changed(); }));
        units.DropDownItems.Add(Item("°F", c.Fahrenheit, () => { c.Fahrenheit = true; Changed(); }));
        units.DropDownItems.Add(new ToolStripSeparator());
        units.DropDownItems.Add(Item("km/h", c.Wind == 0, () => { c.Wind = 0; Changed(); }));
        units.DropDownItems.Add(Item("mph", c.Wind == 1, () => { c.Wind = 1; Changed(); }));
        units.DropDownItems.Add(Item("m/s", c.Wind == 2, () => { c.Wind = 2; Changed(); }));
        units.DropDownItems.Add(new ToolStripSeparator());
        units.DropDownItems.Add(Item("24-hour time", c.Use24h, () => { c.Use24h = !c.Use24h; Changed(); }));
        menu.Items.Add(units);

        var every = new ToolStripMenuItem("Update every");
        foreach (var m in new[] { 15, 30, 60, 120 }) { int v = m; every.DropDownItems.Add(Item(v < 60 ? v + " minutes" : v / 60 + (v == 60 ? " hour" : " hours"), c.Refresh == v, () => { c.Refresh = v; c.Save(); if (data != null) ScheduleFetchSeconds((int)Math.Max(5, v * 60 - (DateTime.UtcNow - data.Fetched).TotalSeconds)); })); }
        menu.Items.Add(every);
        menu.Items.Add(new ToolStripSeparator());

        Common().AddAll(menu.Items);
    }

    void SetLayer(int l)
    {
        c.Layer = layer = l; c.Save();
        RecreateHandle();
        Render();
    }

    void ChoosePlace()
    {
        using (var dlg = new PlaceDialog(c.Place))
            if (dlg.ShowDialog() == DialogResult.OK && dlg.Result != null)
            {
                bool newPlace = dlg.Result.Lat != c.Lat || dlg.Result.Lon != c.Lon;
                c.Place = dlg.Result.Name; c.Lat = dlg.Result.Lat; c.Lon = dlg.Result.Lon;
                c.Save();
                if (newPlace && !Program.Demo) { data = Weather.LoadCache(c.Lat, c.Lon); lastError = null; Fetch(); }
                Render();
            }
    }
}

// Search Open-Meteo's place names (or type coordinates) and pick one.
class PlaceDialog : Form
{
    public Weather.Spot Result;
    readonly TextBox search, name;
    readonly ListBox list;
    readonly Label status;
    readonly Button find;
    int searchId;
    CancellationTokenSource searchCancel;

    public PlaceDialog(string current)
    {
        Text = "Kinetik Weather location";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(460, 420);
        BackColor = Color.FromArgb(27, 31, 44); ForeColor = Color.FromArgb(232, 236, 244);
        Color field = Color.FromArgb(40, 47, 66);

        Controls.Add(new Label { Text = "Search for a town or city, or type coordinates (-37.81, 144.96):", Left = 12, Top = 12, AutoSize = true });
        search = new TextBox { Left = 12, Top = 36, Width = 350, BackColor = field, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle, Text = current };
        find = new Button { Text = "Search", Left = 372, Top = 34, Width = 76, FlatStyle = FlatStyle.Flat };
        list = new ListBox { Left = 12, Top = 68, Width = 436, Height = 230, BackColor = field, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
        status = new Label { Left = 12, Top = 302, Width = 436, Height = 20, ForeColor = Color.FromArgb(150, 158, 180) };
        Controls.Add(new Label { Text = "Name shown on the widget:", Left = 12, Top = 326, AutoSize = true });
        name = new TextBox { Left = 12, Top = 348, Width = 436, BackColor = field, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "Use this place", Left = 252, Top = 384, Width = 115, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 373, Top = 384, Width = 75, FlatStyle = FlatStyle.Flat };
        Controls.AddRange(new Control[] { search, find, list, status, name, ok, cancel });
        CancelButton = cancel;

        find.Click += (s, e) => DoSearch();
        search.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { DoSearch(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); list.SelectedIndex = 0; e.Handled = true; }
        };
        list.SelectedIndexChanged += (s, e) => { var sp = list.SelectedItem as Weather.Spot; if (sp != null) name.Text = sp.Name; };
        list.DoubleClick += (s, e) => ok.PerformClick();
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ok.PerformClick(); e.Handled = true; } };
        ok.Click += (s, e) =>
        {
            var sp = list.SelectedItem as Weather.Spot;
            if (sp == null) { status.Text = "Search, then pick a place from the list."; search.Focus(); return; }
            Result = new Weather.Spot { Name = string.IsNullOrWhiteSpace(name.Text) ? sp.Name : name.Text.Trim(), Lat = sp.Lat, Lon = sp.Lon, Label = sp.Label };
            DialogResult = DialogResult.OK;
        };
        status.Text = "Place names come from Open-Meteo's free geocoding service.";
    }

    void DoSearch()
    {
        int id = ++searchId;
        if (searchCancel != null) { searchCancel.Cancel(); searchCancel = null; }
        find.Enabled = true;
        var q = search.Text.Trim();
        if (q == "") { list.Items.Clear(); status.Text = ""; return; }
        list.Items.Clear();
        var coords = Weather.ParseCoords(q);
        if (coords != null) { list.Items.Add(coords); list.SelectedIndex = 0; status.Text = ""; return; }
        if (q.Length < 2) { status.Text = "Type at least two letters."; return; }
        status.Text = "Searching…";
        find.Enabled = false;
        var cancellation = new CancellationTokenSource(); searchCancel = cancellation;
        var token = cancellation.Token;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<Weather.Spot> got = null; string err = null;
            try { got = Weather.Search(q, token); } catch (Exception ex) { err = WeatherForm.Friendly(ex); }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (id != searchId || IsDisposed) { cancellation.Dispose(); return; }
                    searchCancel = null; cancellation.Dispose();
                    find.Enabled = true;
                    if (err != null) { status.Text = "✗ " + err; return; }
                    foreach (var sp in got) list.Items.Add(sp);
                    status.Text = got.Count == 0 ? "No places found by that name." : got.Count + (got.Count == 1 ? " place" : " places") + " found.";
                    if (got.Count > 0) { list.SelectedIndex = 0; list.Focus(); }
                }));
            }
            catch { }
        });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    { searchId++; if (searchCancel != null) { searchCancel.Cancel(); searchCancel = null; } base.OnFormClosed(e); }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        search.Focus(); search.SelectAll();
    }
}

// ======================================================================= Shared with the other Kinetik apps
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string Name { get { return Backup.AppTitle; } }
    public static string Args = ""; // added after the exe path, e.g. " --startup"

    public static bool IsOn
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Name) != null; }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"" + Args);
                else k.DeleteValue(Name, false);
        }
        catch { }
    }
}

// ======================================================================= Themes (same set as Kinetik and Kinetik Calendar)
class Theme
{
    public readonly string Name;
    public readonly Color Bg, Text, Sub, Accent;
    public readonly bool Light;

    public Theme(string name, Color bg, Color text, Color sub, Color accent, bool light)
    {
        Name = name; Bg = bg; Text = text; Sub = sub; Accent = accent; Light = light;
    }

    public Color Line { get { return Light ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255); } }

    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

    public static readonly Theme[] All =
    {
        new Theme("Midnight", C(22, 22, 26), C(242, 242, 246), C(168, 168, 180), C(240, 88, 88), false),
        new Theme("Navy", C(22, 27, 40), C(236, 240, 248), C(138, 150, 176), C(77, 166, 255), false),
        new Theme("Graphite", C(40, 40, 44), C(236, 236, 236), C(160, 160, 166), C(255, 159, 10), false),
        new Theme("Nord", C(46, 52, 64), C(236, 239, 244), C(152, 162, 182), C(136, 192, 208), false),
        new Theme("Dracula", C(40, 42, 54), C(248, 248, 242), C(150, 156, 192), C(255, 121, 198), false),
        new Theme("Solarized", C(0, 43, 54), C(238, 232, 213), C(131, 148, 150), C(203, 75, 22), false),
        new Theme("Forest", C(22, 33, 27), C(228, 240, 231), C(138, 166, 148), C(108, 201, 124), false),
        new Theme("Ocean", C(12, 29, 45), C(228, 242, 250), C(128, 160, 186), C(56, 198, 232), false),
        new Theme("Sunset", C(40, 23, 35), C(250, 236, 240), C(192, 150, 172), C(255, 138, 92), false),
        new Theme("Mono", C(10, 10, 10), C(245, 245, 245), C(140, 140, 140), C(245, 245, 245), false),
        new Theme("Light", C(246, 246, 248), C(24, 24, 28), C(96, 96, 106), C(230, 70, 70), true),
        new Theme("Paper", C(250, 246, 236), C(52, 42, 32), C(132, 116, 96), C(204, 92, 52), true),
        new Theme("Frost", C(236, 242, 250), C(20, 30, 48), C(92, 106, 132), C(40, 120, 230), true),
    };

    public static Theme Get(string name)
    {
        if (name == "Auto")
        {
            bool light = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1;
            }
            catch { }
            return Get(light ? "Light" : "Midnight");
        }
        foreach (var t in All) if (t.Name == name) return t;
        return All[0];
    }
}

// ======================================================================= Drawing helpers
static class Draw
{
    public static string Family = HasFont("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    public static readonly string[] FontChoices = { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Inter", "Cascadia Code", "Consolas", "Verdana", "Georgia" };

    public static bool HasFont(string name)
    {
        try { using (var f = new FontFamily(name)) return f.Name == name; } catch { return false; }
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Max(0, Math.Min(1, t));
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// A premultiplied-alpha bitmap backed by a DIB, pushed to a layered window with UpdateLayeredWindow (as in Kinetik).
sealed class DibSurface : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLEND b, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    IntPtr memDC, dib, oldObj;
    Bitmap bmp;
    int w, h;

    public Bitmap Canvas(int width, int height)
    {
        if (dib != IntPtr.Zero && width == w && height == h) return bmp;
        Free();
        memDC = CreateCompatibleDC(IntPtr.Zero);
        var bmi = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        IntPtr bits;
        dib = CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
        oldObj = SelectObject(memDC, dib);
        bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        w = width; h = height;
        return bmp;
    }

    public void Push(IntPtr hwnd, int x, int y)
    {
        var size = new SIZE { CX = w, CY = h };
        var src = new POINT();
        var pos = new POINT { X = x, Y = y };
        var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        UpdateLayeredWindow(hwnd, IntPtr.Zero, ref pos, ref size, memDC, ref src, 0, ref blend, 2);
    }

    void Free()
    {
        if (bmp != null) { bmp.Dispose(); bmp = null; }
        if (memDC != IntPtr.Zero) { SelectObject(memDC, oldObj); DeleteDC(memDC); memDC = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
    }

    // The last frame, and the same as a PNG (for --shot screenshots).
    public Bitmap Current { get { return bmp; } }
    public void Save(string path) { if (bmp != null) bmp.Save(path, ImageFormat.Png); }

    public void Dispose() { Free(); }
}

// A card's soft shadow: stacked, slightly larger translucent shapes. Those anti-aliased fills over the whole card cost
// more than the rest of a frame put together, so they're drawn once into a bitmap and copied while nothing about them changes.
sealed class ShadowCache : IDisposable
{
    Bitmap bmp; string key;

    // Lays the shadow into the (cleared) w × h target. look sums up everything draw depends on.
    public void Paint(Graphics target, int w, int h, string look, Action<Graphics> draw)
    {
        var k = w + "x" + h + "|" + look + "|" + (int)target.SmoothingMode + (int)target.PixelOffsetMode;
        if (k != key || bmp == null)
        {
            if (bmp == null || bmp.Width != w || bmp.Height != h) { if (bmp != null) bmp.Dispose(); bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb); }
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = target.SmoothingMode; g.PixelOffsetMode = target.PixelOffsetMode;
                draw(g);
            }
            key = k;
        }
        bmp.SetResolution(target.DpiX, target.DpiY); // so it's copied 1:1
        var mode = target.CompositingMode;
        target.CompositingMode = CompositingMode.SourceCopy;
        target.DrawImageUnscaled(bmp, 0, 0);
        target.CompositingMode = mode;
    }

    public void Dispose() { if (bmp != null) bmp.Dispose(); bmp = null; key = null; }
}

class DarkColors : ProfessionalColorTable
{
    static readonly Color Bg = Color.FromArgb(27, 31, 44), Hi = Color.FromArgb(40, 47, 66), Edge = Color.FromArgb(52, 60, 82);
    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuBorder { get { return Edge; } }
    public override Color MenuItemBorder { get { return Hi; } }
    public override Color MenuItemSelected { get { return Hi; } }
    public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
    public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
    public override Color MenuItemPressedGradientBegin { get { return Hi; } }
    public override Color MenuItemPressedGradientEnd { get { return Hi; } }
    public override Color SeparatorDark { get { return Edge; } }
    public override Color SeparatorLight { get { return Edge; } }
    public override Color CheckBackground { get { return Color.FromArgb(52, 96, 160); } }
    public override Color CheckSelectedBackground { get { return Color.FromArgb(62, 110, 180); } }
    public override Color CheckPressedBackground { get { return Color.FromArgb(62, 110, 180); } }
}

class DarkMenu : ToolStripProfessionalRenderer
{
    public DarkMenu() : base(new DarkColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Color.FromArgb(232, 236, 244) : Color.FromArgb(110, 118, 140);
        base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Color.FromArgb(180, 188, 205);
        base.OnRenderArrow(e);
    }
}

// A grid of theme tiles, the same picker as the other Kinetik apps.
class ThemePicker : Form
{
    public static readonly Color Page = Color.FromArgb(20, 23, 33), Card = Color.FromArgb(27, 31, 44), CardHi = Color.FromArgb(36, 41, 58), Accent = Color.FromArgb(77, 166, 255);
    public static readonly Color TextCol = Color.FromArgb(232, 236, 244), SubCol = Color.FromArgb(150, 158, 180);

    public ThemePicker(string current, Action<string> chosen)
    {
        Text = Backup.AppTitle + " theme";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BackColor = Page;
        float k = DeviceDpi / 96f;
        var flow = new FlowLayoutPanel { Left = (int)(16 * k), Top = (int)(16 * k), Width = (int)(3 * 118 * k), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size((int)(3 * 118 * k), 0), WrapContents = true, BackColor = Page };
        foreach (var name in new[] { "Auto" }.Concat(Theme.All.Select(t => t.Name)))
        {
            var tile = new ThemeTile(name, name == current, k);
            tile.Click += (s, e) =>
            {
                foreach (ThemeTile o in flow.Controls) o.SetSelected(o == tile);
                chosen(tile.ThemeName);
            };
            flow.Controls.Add(tile);
        }
        Controls.Add(flow);
        ClientSize = new Size(flow.Right + (int)(8 * k), flow.Bottom + (int)(8 * k));
    }
}

class ThemeTile : Control
{
    public readonly string ThemeName;
    bool selected, hot;
    float sel;
    readonly float k;
    readonly System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 15 };
    static readonly Color[] Bars = { Color.FromArgb(77, 166, 255), Color.FromArgb(56, 198, 180), Color.FromArgb(240, 88, 88) };

    public ThemeTile(string name, bool isSelected, float scale)
    {
        ThemeName = name; selected = isSelected; sel = isSelected ? 1 : 0; k = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(I(110), I(84));
        Margin = new Padding(0, 0, I(8), I(8));
        Cursor = Cursors.Hand;
        BackColor = ThemePicker.Page;
        t.Tick += (s, e) =>
        {
            float target = selected ? 1 : 0;
            sel += (target - sel) * 0.3f;
            if (Math.Abs(target - sel) < 0.02f) { sel = target; t.Stop(); }
            Invalidate();
        };
    }

    int I(float v) { return (int)Math.Round(v * k); }
    public void SetSelected(bool v) { if (selected == v) return; selected = v; t.Start(); }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }

    static void Fill(Graphics g, RectangleF r, float rad, Color c) { using (var p = Draw.Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var outer = new RectangleF(1, 1, Width - 3, Height - 3);
        Fill(g, outer, I(10), hot ? ThemePicker.CardHi : ThemePicker.Card);
        if (sel > 0) using (var p = Draw.Round(outer, I(10))) using (var pen = new Pen(Color.FromArgb((int)(255 * sel), ThemePicker.Accent), 2)) g.DrawPath(pen, p);
        var inner = new RectangleF(I(8), I(8), Width - I(16), Height - I(34));
        if (ThemeName == "Auto")
        {
            Mini(g, inner, Theme.Get("Midnight"));
            var st = g.Save();
            g.SetClip(new RectangleF(inner.X + inner.Width / 2, inner.Y, inner.Width, inner.Height));
            Mini(g, inner, Theme.Get("Light"));
            g.Restore(st);
        }
        else Mini(g, inner, Theme.Get(ThemeName));
        using (var f = new Font(Draw.Family, 9f))
            TextRenderer.DrawText(g, ThemeName == "Auto" ? "Match Windows" : ThemeName, f, new Rectangle(0, Height - I(26), Width, I(20)), selected ? ThemePicker.TextCol : ThemePicker.SubCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void Mini(Graphics g, RectangleF r, Theme th)
    {
        Fill(g, r, I(6), th.Bg);
        float x = r.X + I(7), w = r.Width - I(14), y = r.Y + I(7);
        using (var b = new SolidBrush(th.Accent)) g.FillEllipse(b, x, y, I(7), I(7));
        Fill(g, new RectangleF(x + I(11), y + I(1), w * 0.45f, I(4)), I(2), th.Text);
        for (int i = 0; i < 3; i++)
        {
            float yy = y + I(13) + i * I(9);
            Fill(g, new RectangleF(x, yy, I(3), I(6)), 1.5f * k, Bars[i]);
            Fill(g, new RectangleF(x + I(7), yy + I(1), w * (0.62f - i * 0.12f), I(4)), I(2), Color.FromArgb(150, th.Sub));
            Fill(g, new RectangleF(x + w - I(14), yy + I(1), I(14), I(4)), I(2), Color.FromArgb(200, th.Text));
        }
    }
}

// ======================================================================= Settings backup
// Copies the app's settings (HKCU\Software\<KeyName>) to "<KeyName>-settings.reg" next to the exe, so they survive a
// Windows reinstall when the app folder is kept (on OneDrive, a USB stick…). It's a normal .reg export, so
// double-clicking the file restores the settings as well. A restore runs after the app has closed, so nothing the app
// saves on the way out can overwrite it, and then the app starts again with the restored settings.
// Same code in every Kinetik app.
static class Backup
{
    public static string KeyName, AppTitle;
    public static Action BeforeExit; // e.g. hide the tray icon, as the app's own Exit does
    static bool restoreAfterExit;

    public static string FilePath
    {
        get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), KeyName + "-settings.reg"); }
    }
    public static bool Exists { get { return System.IO.File.Exists(FilePath); } }
    public static string SavedText
    {
        get { return Exists ? System.IO.File.GetLastWriteTime(FilePath).ToString("d MMM yyyy, h:mm tt") : "never"; }
    }

    static bool HasSettings
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\" + KeyName)) return k != null; }
            catch { return true; }
        }
    }

    static bool Reg(string args) { return KinetikSuite.RegistryFiles.Run(args); }

    static bool FileIsOurs()
    {
        try
        {
            string own = "[HKEY_CURRENT_USER\\Software\\" + KeyName;
            bool any = false;
            foreach (var line in System.IO.File.ReadAllLines(FilePath))
            {
                var l = line.Trim();
                if (!l.StartsWith("[")) continue;
                if (!l.StartsWith(own + "]", StringComparison.OrdinalIgnoreCase) && !l.StartsWith(own + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    static bool Import()
    {
        try { return FileIsOurs() && KinetikSuite.RegistryFiles.Replace(FilePath, new[] { KeyName }); }
        catch (Exception ex) { Say(ex.Message, MessageBoxIcon.Warning); return false; }
    }

    static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, AppTitle, MessageBoxButtons.OK, icon); }

    // At launch: no settings in the registry (a fresh Windows) but a backup beside the exe, so offer it.
    public static void OfferRestore()
    {
        if (HasSettings || !Exists) return;
        if (MessageBox.Show("There are saved " + AppTitle + " settings in this folder, from " + SavedText + ".\n\nRestore them?",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!Import()) Say("Couldn't restore the settings from\n" + FilePath, MessageBoxIcon.Warning);
    }

    public static void SaveNow()
    {
        if (Reg("export \"HKCU\\Software\\" + KeyName + "\" \"" + FilePath + "\" /y"))
            Say("Settings backed up to\n" + FilePath + "\n\nKeep this file with the app. After reinstalling Windows, " + AppTitle + " offers to restore it the first time it starts.", MessageBoxIcon.Information);
        else
            Say("Couldn't save the backup to\n" + FilePath + "\n\nCheck that the folder isn't read-only.", MessageBoxIcon.Warning);
    }

    public static void RestoreNow()
    {
        if (!Exists) { Say("There's no backup yet. Use \"Back up settings\" first.\n\nIt would be saved as\n" + FilePath, MessageBoxIcon.Information); return; }
        if (!FileIsOurs()) { Say("The backup file isn't a " + AppTitle + " settings file, so it wasn't used.", MessageBoxIcon.Warning); return; }
        if (MessageBox.Show("Replace the current settings with the backup from " + SavedText + "?\n\n" + AppTitle + " will restart.",
                AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        restoreAfterExit = true;
        if (BeforeExit != null) try { BeforeExit(); } catch { }
        Application.Exit();
    }

    // Called from Main once the app has closed and released its single-instance lock.
    public static void AfterExit()
    {
        if (!restoreAfterExit) return;
        if (!Import()) Say("Couldn't restore the settings from\n" + FilePath, MessageBoxIcon.Warning);
        try { System.Diagnostics.Process.Start(Application.ExecutablePath); } catch { }
    }
}

// ======================================================================= The right-click menu every Kinetik widget shares
// The look, placement, startup and backup options, in the same order and the same words in every Kinetik app, so they
// feel like one set. Each app adds its own items above. Same code in every Kinetik app.
#pragma warning disable 0649 // the hooks an app has no use for stay null
class CommonMenu
{
    public static readonly int[] Scales = { 80, 90, 100, 115, 125, 150 };
    public static readonly int[] Opacities = { 100, 95, 80, 65, 50, 30, 0 };
    public const int DefaultOpacity = 95;
    public static readonly string[] Fonts = { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Inter", "Cascadia Code", "Consolas", "Verdana", "Georgia" };

    // Settings saved before the shared default changed from 92% to 95% move with it; any other choice is kept.
    public static int Migrate(int opacity) { return opacity == 92 ? DefaultOpacity : opacity; }

    public string Title, Version;
    public Action OpenTheme;                                        // opens the theme picker (or the page with it)
    public Func<int> Scale; public Action<int> SetScale;
    public int[] Widths; public Func<int> CardWidth; public Action<int> SetCardWidth; // optional: Narrow, Normal, Wide
    public Func<string> FontName; public Action<string> SetFontName;   // optional; "" = the default font
    public Func<int> Opacity; public Action<int> SetOpacity;
    public Func<int> Layer; public Action<int> SetLayer;
    public Func<bool> Locked; public Action<bool> SetLocked;
    public Func<bool> ClickThrough; public Action<bool> SetClickThrough; // optional
    public Func<bool> Snap; public Action<bool> SetSnap;
    public string LockText = "Lock position";
    public Action OpenSettings;                                     // optional: the app's Settings window
    public Func<bool> RunAtStartup; public Action<bool> SetRunAtStartup;   // optional
    public Action BackUp, Restore; public Func<string> LastBackup; // optional
    public Action Exit;                                         // optional

    static ToolStripMenuItem Item(string text, bool check, Action click)
    {
        var it = new ToolStripMenuItem(text) { Checked = check };
        it.Click += (s, e) => click();
        return it;
    }

    static bool HasFont(string name)
    {
        try { using (var f = new FontFamily(name)) return f.Name == name; } catch { return false; }
    }

    // Appearance and placement: Theme, Size, Font, Background opacity, Placement.
    public void AddLook(ToolStripItemCollection items)
    {
        if (OpenTheme != null) items.Add(Item("Theme…", false, OpenTheme));

        var size = new ToolStripMenuItem("Size");
        foreach (var s in Scales) { int v = s; size.DropDownItems.Add(Item(v + "%", Scale() == v, () => SetScale(v))); }
        if (Widths != null)
        {
            size.DropDownItems.Add(new ToolStripSeparator());
            string[] names = { "Narrow", "Normal", "Wide" };
            for (int i = 0; i < Widths.Length && i < 3; i++) { int v = Widths[i]; size.DropDownItems.Add(Item(names[i], CardWidth() == v, () => SetCardWidth(v))); }
        }
        items.Add(size);

        if (FontName != null)
        {
            var font = new ToolStripMenuItem("Font");
            font.DropDownItems.Add(Item("Default", FontName() == "", () => SetFontName("")));
            foreach (var n in Fonts.Where(HasFont)) { string v = n; font.DropDownItems.Add(Item(v, FontName() == v, () => SetFontName(v))); }
            if (FontName() != "" && !Fonts.Contains(FontName())) font.DropDownItems.Add(Item(FontName(), true, () => { }));
            items.Add(font);
        }

        var opacity = new ToolStripMenuItem("Background opacity");
        foreach (var o in Opacities) { int v = o; opacity.DropDownItems.Add(Item(v + "%", Opacity() == v, () => SetOpacity(v))); }
        if (!Opacities.Contains(Opacity())) opacity.DropDownItems.Add(Item(Opacity() + "%", true, () => { }));
        items.Add(opacity);

        var place = new ToolStripMenuItem("Placement");
        string[] layers = { "On the desktop", "Normal window", "Always on top" };
        for (int i = 0; i < 3; i++) { int v = i; place.DropDownItems.Add(Item(layers[i], Layer() == v, () => SetLayer(v))); }
        place.DropDownItems.Add(new ToolStripSeparator());
        place.DropDownItems.Add(Item(LockText, Locked(), () => SetLocked(!Locked())));
        if (ClickThrough != null) place.DropDownItems.Add(Item("Click-through when locked", ClickThrough(), () => SetClickThrough(!ClickThrough())));
        place.DropDownItems.Add(Item("Snap to screen edges", Snap(), () => SetSnap(!Snap())));
        items.Add(place);
    }

    // The rest: Settings…, Run at startup, Backup, then the version and Exit.
    public void AddEnd(ToolStripItemCollection items)
    {
        items.Add(new ToolStripSeparator());
        if (OpenSettings != null) items.Add(Item("Settings…", false, OpenSettings));
        if (RunAtStartup != null) items.Add(Item("Run at startup", RunAtStartup(), () => SetRunAtStartup(!RunAtStartup())));
        if (BackUp != null)
        {
            var backup = new ToolStripMenuItem("Backup");
            backup.DropDownItems.Add(Item("Back up settings", false, BackUp));
            backup.DropDownItems.Add(Item("Restore settings…", false, Restore));
            backup.DropDownItems.Add(new ToolStripMenuItem("Last backup: " + LastBackup()) { Enabled = false });
            items.Add(backup);
        }
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(Title + " " + Version) { Enabled = false });
        if (Exit != null) items.Add(Item("Exit", false, Exit));
    }

    public void AddAll(ToolStripItemCollection items) { AddLook(items); AddEnd(items); }
}
#pragma warning restore 0649
