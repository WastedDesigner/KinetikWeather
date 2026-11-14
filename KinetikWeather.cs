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
[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]

static class Program
{
    public const string Version = "2.1.0";
    // --demo: a made-up forecast, nothing fetched, read from or saved to this PC. "--theme Name" picks its theme.
    // "--shot file.png" draws the demo card into a PNG and quits (for the README screenshots). "--view 0|1|2" picks how
    // the demo's four places are shown (see Settings.View).
    // "--alerts" adds a thunderstorm to the demo forecast, so the alert banner shows.
    public static bool Demo, DemoAlerts;
    public static int DemoView = 1;
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
        DemoAlerts = args.Contains("--alerts");
        int pi = Array.IndexOf(args, "--place");
        if (pi >= 0 && pi + 1 < args.Length) DemoPlace = args[pi + 1];
        int vi = Array.IndexOf(args, "--view");
        if (vi >= 0 && vi + 1 < args.Length) int.TryParse(args[vi + 1], out DemoView);
        bool created;
        using (new Mutex(true, Demo ? @"Local\KinetikWeatherDemo" + (ShotPath != null ? Guid.NewGuid().ToString("N") : "") : @"Local\KinetikWeather", out created))
        {
            if (!created) return; // already running
            SetProcessDPIAware();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ToolStripManager.Renderer = KinetikSuite.Look.Current.MenuRenderer();
            Backup.KeyName = "KinetikWeather"; Backup.AppTitle = "Kinetik Weather";
            if (!Demo) Backup.OfferRestore();
            var app = new WeatherForm();
            // --settings opens the settings window once the app has started.
            if (args.Contains("--settings")) { EventHandler once = null; once = (s2, e2) => { Application.Idle -= once; app.ShowSettings(); }; Application.Idle += once; }
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
    public bool Locked, Snap = true, ClickThrough, Fahrenheit = !Metric, Animate = true;
    public bool ShowHourly = true, ShowRain = true, ShowSun = true, ShowDaily = true, ShowDetails = true;
    public bool RainAlert = true, ShowWarnings = true, ShowAir = true, FeelsHero; // FeelsHero: the big temperature is the feels-like one
    public int View = 1; // 0 just the main place, 1 the main place in full with the others as rows below, 2 every place as a row
    public bool Use24h = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H");
    public string ThemeName = "Midnight", FontName = ""; // "" = Segoe UI Variable Text
    public string Place = "", Lat = "", Lon = "";        // the main place; Lat/Lon as invariant-culture decimals
    public string Others = "";                           // the other places, "Name|lat|lon" joined by ";"

    public bool HasPlace { get { return Lat != "" && Lon != ""; } }
    public Place Main { get { return new Place { Name = Place == "" ? "Weather" : Place, Lat = Lat, Lon = Lon }; } }

    public List<Place> OtherPlaces()
    {
        var list = new List<Place>();
        foreach (var part in (Others ?? "").Split(';'))
        {
            var f = part.Split('|');
            double a, b;
            if (f.Length != 3 || !double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out a) || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b)
                || Math.Abs(a) > 90 || Math.Abs(b) > 180) continue;
            var p = new Place { Name = f[0].Trim() == "" ? f[1] + ", " + f[2] : f[0].Trim(), Lat = f[1], Lon = f[2] };
            if (p.Key != Main.Key && !list.Any(o => o.Key == p.Key)) list.Add(p);
        }
        return list;
    }

    public void SetOthers(IEnumerable<Place> places)
    {
        Others = string.Join(";", places.Select(p => p.Name.Replace("|", " ").Replace(";", " ").Trim() + "|" + p.Lat + "|" + p.Lon));
    }

    // The main place first, then the others; empty until a place is set.
    public List<Place> AllPlaces()
    {
        var all = new List<Place>();
        if (!HasPlace) return all;
        all.Add(Main);
        all.AddRange(OtherPlaces());
        return all;
    }

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
                Place = "Melbourne", Lat = "-37.81", Lon = "144.96", Fahrenheit = false, Wind = 0, Use24h = false,
                Others = "Sydney|-33.87|151.21;Tokyo|35.68|139.69;London|51.51|-0.13", View = Math.Max(0, Math.Min(2, Program.DemoView))
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
        KinetikSuite.SettingsValidation.Sanitize(s); s.View = Math.Max(0, Math.Min(2, s.View)); return s;
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
class Hour { public DateTime T; public double Temp, Pop, Rain = double.NaN, Gust = double.NaN; public int Code; public bool Day; } // Rain mm in the hour, Gust km/h
class Slot { public DateTime T; public double Mm; }   // rain in the 15 minutes up to T
class AirQ { public double Aqi, Pm25; }               // US air quality index and PM2.5
class Warn { public int Level; public string Kind; public DateTime When; public double Value; } // Level 1 = watch out, 2 = severe
class DayF { public DateTime Date, Rise, Set; public double Max, Min, Pop, Uv = double.NaN; public int Code; }
class Place { public string Name, Lat, Lon; public string Key { get { return Lat + "," + Lon; } } }

class Forecast
{
    public double Temp, Feels, Wind, WindDir, Humidity; // °C, km/h, degrees, %
    public double RainNow = double.NaN;                 // mm in the current 15 minutes
    public int Code, Offset;                            // WMO weather code; the place's offset from UTC in seconds
    public bool IsDay;
    public DateTime Fetched;                            // UTC
    public List<Hour> Hours = new List<Hour>();          // the place's own local times
    public List<DayF> Days = new List<DayF>();
    public List<Slot> Mins = new List<Slot>();           // the next two hours in 15-minute steps

    public DateTime PlaceNow { get { return DateTime.UtcNow.AddSeconds(Offset); } }
}

static class Weather
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Url(string lat, string lon)
    {
        return "https://api.open-meteo.com/v1/forecast?latitude=" + lat + "&longitude=" + lon +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m,wind_direction_10m,precipitation" +
            "&hourly=temperature_2m,precipitation_probability,weather_code,is_day,precipitation,wind_gusts_10m" +
            "&minutely_15=precipitation&forecast_minutely_15=8" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,uv_index_max" +
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
        f.RainNow = D(Val(cur, "precipitation"));

        var h = Obj(root, "hourly");
        object[] ht = Arr(h, "time"), temp = Arr(h, "temperature_2m"), pop = Arr(h, "precipitation_probability"), hc = Arr(h, "weather_code"), day = Arr(h, "is_day"),
                 rn = Arr(h, "precipitation"), gs = Arr(h, "wind_gusts_10m");
        for (int i = 0; i < ht.Length; i++)
            f.Hours.Add(new Hour { T = Time(ht[i]), Temp = D(temp, i), Pop = D(pop, i), Code = I(hc, i), Day = D(day, i) == 1, Rain = D(rn, i), Gust = D(gs, i) });
        var mn15 = Obj(root, "minutely_15");
        object[] mt = Arr(mn15, "time"), mp = Arr(mn15, "precipitation");
        for (int i = 0; i < mt.Length && i < mp.Length; i++)
            try { f.Mins.Add(new Slot { T = Time(mt[i]), Mm = D(mp, i) }); } catch { }

        var d = Obj(root, "daily");
        object[] dt = Arr(d, "time"), dc = Arr(d, "weather_code"), mx = Arr(d, "temperature_2m_max"), mn = Arr(d, "temperature_2m_min"),
                 dp = Arr(d, "precipitation_probability_max"), rise = Arr(d, "sunrise"), set = Arr(d, "sunset"), uv = Arr(d, "uv_index_max");
        for (int i = 0; i < dt.Length; i++)
        {
            var x = new DayF { Date = Time(dt[i]), Code = I(dc, i), Max = D(mx, i), Min = D(mn, i), Pop = D(dp, i), Uv = D(uv, i) };
            try { x.Rise = Time(rise[i]); x.Set = Time(set[i]); } catch { } // polar day or night has none
            f.Days.Add(x);
        }
        if (f.Hours.Count == 0 || double.IsNaN(f.Temp)) throw new FormatException("The forecast came back empty");
        return f;
    }

    // ---- alerts worked out from the forecast itself. Open-Meteo has no official warnings, so these are "watch out" notes,
    // not government warnings. They cover the next 24 hours, plus today's and tomorrow's temperature extremes.
    // shortcut: the thresholds are fixed (and are sensible for most places); make them settings if they're wrong for yours.
    public static List<Warn> Warnings(Forecast f)
    {
        var list = new List<Warn>();
        var now = f.PlaceNow;
        Action<string, int, DateTime, double> add = (kind, level, when, value) =>
        {
            var have = list.FirstOrDefault(w => w.Kind == kind);
            if (have == null) list.Add(new Warn { Kind = kind, Level = level, When = when, Value = value });
            else if (level > have.Level) { have.Level = level; have.When = when; have.Value = value; } // keeps the first time at the worst level
        };
        foreach (var h in f.Hours.Where(h => h.T.AddHours(1) > now && h.T < now.AddHours(24)))
        {
            if (h.Code >= 96) add("storm", 2, h.T, 0); else if (h.Code == 95) add("storm", 1, h.T, 0);
            if (!double.IsNaN(h.Rain)) { if (h.Rain >= 10) add("rain", 2, h.T, h.Rain); else if (h.Rain >= 4) add("rain", 1, h.T, h.Rain); }
            if (!double.IsNaN(h.Gust)) { if (h.Gust >= 90) add("wind", 2, h.T, h.Gust); else if (h.Gust >= 65) add("wind", 1, h.T, h.Gust); }
            if (h.Code == 75 || h.Code == 86) add("snow", 1, h.T, 0);
            if (h.Code == 66 || h.Code == 67) add("ice", 2, h.T, 0);
        }
        foreach (var d in f.Days.Where(d => d.Date >= now.Date && d.Date <= now.Date.AddDays(1)))
        {
            if (d.Max >= 42) add("heat", 2, d.Date, d.Max); else if (d.Max >= 38) add("heat", 1, d.Date, d.Max);
            if (d.Min <= -20) add("cold", 2, d.Date, d.Min); else if (d.Min <= -10) add("cold", 1, d.Date, d.Min);
        }
        return list.OrderByDescending(w => w.Level).ThenBy(w => w.When).ToList();
    }

    // Minutes until rain starts, when it's dry now and 0.1 mm or more is due within the hour (start is when it begins); -1 otherwise.
    // shortcut: Open-Meteo's 15-minute rain is interpolated outside Europe, North America and Japan, so the timing is approximate.
    public static int RainSoon(Forecast f, out DateTime start)
    {
        start = DateTime.MinValue;
        var now = f.PlaceNow;
        if (!double.IsNaN(f.RainNow) && f.RainNow >= 0.1 && (DateTime.UtcNow - f.Fetched).TotalMinutes < 15) return -1; // raining at the last update
        foreach (var s in f.Mins.Where(s => s.T > now).OrderBy(s => s.T))
        {
            if (double.IsNaN(s.Mm) || s.Mm < 0.1) continue;
            var from = s.T.AddMinutes(-15);
            if (from <= now || from > now.AddMinutes(60)) return -1; // already raining, or not for an hour yet
            start = from;
            return (int)Math.Ceiling((from - now).TotalMinutes);
        }
        return -1;
    }

    public static string AirUrl(string lat, string lon)
    {
        return "https://air-quality-api.open-meteo.com/v1/air-quality?latitude=" + lat + "&longitude=" + lon + "&current=us_aqi,pm2_5&timezone=auto";
    }

    public static AirQ ParseAir(string json)
    {
        var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
        var cur = Obj(root, "current");
        var a = new AirQ { Aqi = D(Val(cur, "us_aqi")), Pm25 = D(Val(cur, "pm2_5")) };
        if (double.IsNaN(a.Aqi)) throw new FormatException("No air quality for this place");
        return a;
    }

    public static Color AqiColour(double aqi)
    {
        return aqi <= 50 ? Color.FromArgb(0, 200, 90) : aqi <= 100 ? Color.FromArgb(240, 200, 0) : aqi <= 150 ? Color.FromArgb(255, 140, 0)
             : aqi <= 200 ? Color.FromArgb(235, 60, 60) : aqi <= 300 ? Color.FromArgb(150, 60, 200) : Color.FromArgb(130, 20, 40);
    }

    // The US AQI bands, short enough for a small tile.
    public static string AqiWord(double aqi) { return aqi <= 50 ? "Good" : aqi <= 100 ? "Fair" : aqi <= 150 ? "Sens." : aqi <= 200 ? "Poor" : aqi <= 300 ? "V.poor" : "Hazard"; }

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

    // ---- the last forecasts, kept so the widget fills in at once at startup and keeps working offline. The main place's
    // is forecast.json (Kinetik Screen reads it); the others are in places\<lat>_<lon>.json, listed in places.tsv.
    static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KinetikWeather"); } }
    static string CacheFile(string lat, string lon, bool main)
    {
        return main ? Path.Combine(Folder, "forecast.json") : Path.Combine(Folder, "places", (lat + "_" + lon).Replace("-", "m") + ".json");
    }

    public static void SaveCache(string lat, string lon, string json, bool main)
    {
        try
        {
            var file = CacheFile(lat, lon, main);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, lat + "," + lon + "\n" + json);
        }
        catch { }
    }

    public static Forecast LoadCache(string lat, string lon, bool main, out string json)
    {
        json = null;
        try
        {
            var file = CacheFile(lat, lon, main);
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file);
            int nl = text.IndexOf('\n');
            if (nl < 0 || text.Substring(0, nl) != lat + "," + lon) return null;
            json = text.Substring(nl + 1);
            var f = Parse(json);
            f.Fetched = File.GetLastWriteTimeUtc(file);
            return f;
        }
        catch { json = null; return null; }
    }

    // One line per place, the main one first: name, latitude, longitude and its forecast file, tab-separated.
    public static void SaveIndex(List<Place> places)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var lines = places.Select((p, i) => p.Name.Replace("\t", " ") + "\t" + p.Lat + "\t" + p.Lon + "\t" + CacheFile(p.Lat, p.Lon, i == 0));
            var file = Path.Combine(Folder, "places.tsv");
            var text = string.Join("\r\n", lines);
            if (!File.Exists(file) || File.ReadAllText(file) != text) File.WriteAllText(file, text);
        }
        catch { }
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

    // A made-up forecast for --demo: a mild spring day with showers in the afternoon. Other variants are warmer, cooler
    // or wetter, and some are in another time zone, for the demo's other places.
    public static Forecast Demo(int variant = 0)
    {
        int v = variant % 4;
        double[] shift = { 0, 4, -3, -8 }; int[] codeNow = { 2, 0, 61, 3 }; int[] zone = { 0, 0, -2, -11 };
        var f = new Forecast { Temp = 18.4 + shift[v], Feels = 17.1 + shift[v], Humidity = 62 + v * 6, Wind = 14 + v * 3, WindDir = 320 - v * 70, Code = codeNow[v], IsDay = true, Fetched = DateTime.UtcNow.AddMinutes(-6) };
        f.Offset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalSeconds + zone[v] * 3600;
        f.IsDay = f.PlaceNow.Hour >= 6 && f.PlaceNow.Hour < 20;
        var now = f.PlaceNow;
        var start = now.Date;
        for (int i = 0; i < 24 * 8; i++)
        {
            var t = start.AddHours(i);
            double hr = t.Hour + 0;
            double temp = 14 + shift[v] + 6 * Math.Sin((hr - 9) / 24 * 2 * Math.PI) + (i / 24) * 0.6;
            double since = (t - now).TotalHours;
            double pop = since > 2 && since < 9 ? Math.Max(0, 70 - Math.Abs(since - 5) * 18) : 5 + (i * 37 % 11);
            int code = pop >= 50 ? 80 : pop >= 25 ? 3 : (t.Hour > 7 && t.Hour < 18 ? 2 : 1);
            f.Hours.Add(new Hour { T = t, Temp = temp, Pop = Math.Round(pop / 5) * 5, Code = code, Day = t.Hour >= 6 && t.Hour < 20, Rain = pop >= 50 ? 1.2 : 0, Gust = 24 + (i * 7) % 15 });
        }
        if (Program.DemoAlerts && v == 0) // a storm this evening, and a gusty hour
        {
            var storm = f.Hours.FirstOrDefault(h => h.T >= now.AddHours(5));
            if (storm != null) { storm.Code = 95; storm.Gust = 78; storm.Rain = 6; }
        }
        int[] codes = { 80, 61, 3, 1, 2, 0, 95, 2 };
        double[] max = { 21, 17, 16, 19, 23, 26, 22, 20 }, min = { 11, 10, 9, 8, 12, 14, 15, 12 }, pops = { 70, 85, 30, 5, 10, 0, 60, 15 };
        for (int i = 0; i < 8; i++)
            f.Days.Add(new DayF { Date = start.AddDays(i), Code = codes[(i + v * 3) % 8], Max = max[i] + shift[v], Min = min[i] + shift[v], Pop = pops[(i + v * 3) % 8], Uv = 7 - i % 4 + v, Rise = start.AddDays(i).AddHours(6).AddMinutes(24 - i), Set = start.AddDays(i).AddHours(19).AddMinutes(41 + i) });
        return f;
    }
}

// ======================================================================= Weather icons, drawn rather than bitmaps so they're sharp at any size
// Soft gradients normally. With Mono set (the terminal themes) they're line drawings in that one colour, the clouds
// filled with MonoBg so whatever is behind them stays hidden.
static class WxIcon
{
    public static readonly Color Sun = Color.FromArgb(255, 196, 64), Rain = Color.FromArgb(77, 166, 255), Bolt = Color.FromArgb(255, 214, 90);
    public static Color Mono = Color.Empty, MonoBg = Color.Black;
    public static double T = -1; // seconds on the animation clock; negative draws the still icons
    static float Sway;           // how far this icon's clouds have drifted sideways, in pixels

    public static void Paint(Graphics g, RectangleF r, int code, bool day, bool light)
    {
        float s = Math.Min(r.Width, r.Height), x = r.X + (r.Width - s) / 2, y = r.Y + (r.Height - s) / 2;
        bool mono = !Mono.IsEmpty, an = T >= 0;
        Sway = an ? (float)Math.Sin(T * 0.8) * s * 0.025f : 0;
        Color cloud = mono ? Mono : light ? Color.FromArgb(160, 168, 184) : Color.FromArgb(226, 231, 241);
        Color cloudDark = mono ? Mono : light ? Color.FromArgb(112, 120, 138) : Color.FromArgb(150, 158, 178);
        Color snow = mono ? Mono : light ? Color.FromArgb(120, 160, 215) : Color.FromArgb(236, 242, 255);
        Color rain = mono ? Mono : Rain, bolt = mono ? Mono : Bolt;

        if (code == 0) { Orb(g, x + s / 2, y + s / 2, s * 0.24f, day, s, light); return; }
        if (code == 1) { Orb(g, x + s * 0.44f, y + s * 0.44f, s * 0.22f, day, s, light); Cloud(g, new RectangleF(x + s * 0.42f, y + s * 0.56f, s * 0.5f, s * 0.32f), cloud, s); return; }
        if (code == 2) { Orb(g, x + s * 0.36f, y + s * 0.36f, s * 0.2f, day, s, light); Cloud(g, new RectangleF(x + s * 0.22f, y + s * 0.4f, s * 0.7f, s * 0.44f), cloud, s); return; }
        if (code == 3) { Cloud(g, new RectangleF(x + s * 0.3f, y + s * 0.18f, s * 0.6f, s * 0.38f), cloudDark, s); Cloud(g, new RectangleF(x + s * 0.08f, y + s * 0.34f, s * 0.78f, s * 0.48f), cloud, s); return; }
        if (code == 45 || code == 48)
        {
            Cloud(g, new RectangleF(x + s * 0.12f, y + s * 0.12f, s * 0.76f, s * 0.46f), cloud, s);
            float fs = an ? (float)Math.Sin(T * 0.9) * s * 0.05f : 0; // the banks of fog slide past each other
            using (var pen = new Pen(cloudDark, s * (mono ? 0.05f : 0.065f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, x + s * 0.16f + fs, y + s * 0.7f, x + s * 0.84f + fs, y + s * 0.7f);
                g.DrawLine(pen, x + s * 0.26f - fs, y + s * 0.84f, x + s * 0.74f - fs, y + s * 0.84f);
            }
            return;
        }
        bool showers = code >= 80 && code <= 86;
        bool thunder = code >= 95;
        bool snowy = (code >= 71 && code <= 77) || code == 85 || code == 86;
        if (showers) Orb(g, x + s * 0.3f, y + s * 0.3f, s * 0.17f, day, s, light);
        Cloud(g, new RectangleF(x + s * 0.12f, y + s * (showers ? 0.24f : 0.1f), s * 0.76f, s * 0.46f), thunder ? cloudDark : cloud, s);
        float top = y + s * (showers ? 0.72f : 0.62f);
        if (thunder)
        {
            var p = new[] { new PointF(x + s * 0.54f, top - s * 0.06f), new PointF(x + s * 0.38f, top + s * 0.17f), new PointF(x + s * 0.5f, top + s * 0.17f),
                            new PointF(x + s * 0.42f, top + s * 0.34f), new PointF(x + s * 0.64f, top + s * 0.1f), new PointF(x + s * 0.52f, top + s * 0.1f), new PointF(x + s * 0.6f, top - s * 0.06f) };
            double ph = T % 3.2; // two quick flashes, then dim until the next storm beat
            int ba = !an || ph < 0.12 || (ph > 0.25 && ph < 0.33) ? 255 : 110;
            using (var b = new SolidBrush(Color.FromArgb(ba, bolt))) g.FillPolygon(b, p);
            return;
        }
        int n = code == 51 || code == 61 || code == 71 || code == 80 || code == 85 ? 2 : 3;
        float span = s * 0.5f, x0 = x + s * 0.5f - span / 2;
        for (int i = 0; i < n; i++)
        {
            float dx = x0 + span * (n == 1 ? 0.5f : i / (float)(n - 1)), dy = top + (i % 2) * s * 0.08f;
            int a = 255;
            if (an)
            {
                // each drop falls from the cloud and fades out, staggered so there's always one coming
                double ph = (T * (snowy ? 0.5 : 1.2) + i / (double)n) % 1.0;
                dy += (float)ph * s * (showers ? 0.12f : 0.2f);
                a = Math.Max(0, (int)(255 * Math.Sin(ph * Math.PI)));
                if (snowy) dx += (float)Math.Sin(T * 2 + i * 2) * s * 0.03f;
            }
            if (snowy)
                using (var b = new SolidBrush(Color.FromArgb(a, snow))) g.FillEllipse(b, dx - s * 0.05f, dy, s * 0.1f, s * 0.1f);
            else
                using (var pen = new Pen(Color.FromArgb(a, rain), s * 0.07f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, dx + s * 0.04f, dy, dx - s * 0.03f, dy + (code >= 51 && code <= 57 ? s * 0.08f : s * 0.16f));
        }
    }

    // The sun (with rays when there's room) or a crescent moon.
    static void Orb(Graphics g, float cx, float cy, float rad, bool day, float s, bool light)
    {
        bool mono = !Mono.IsEmpty;
        if (day)
        {
            var disc = new RectangleF(cx - rad, cy - rad, rad * 2, rad * 2);
            if (mono)
                using (var pen = new Pen(Mono, Math.Max(1, s * 0.05f))) g.DrawEllipse(pen, disc);
            else
            {
                using (var halo = new SolidBrush(Color.FromArgb(40, Sun))) g.FillEllipse(halo, RectangleF.Inflate(disc, rad * 0.35f, rad * 0.35f));
                using (var b = new LinearGradientBrush(RectangleF.Inflate(disc, 1, 1), Color.FromArgb(255, 222, 110), Color.FromArgb(255, 160, 40), 60f)) g.FillEllipse(b, disc);
            }
            if (s >= 18)
                using (var pen = new Pen(mono ? Mono : Sun, Math.Max(1, s * 0.05f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = 0; i < 8; i++)
                    {
                        // the rays turn slowly and each breathes in and out
                        double a = i * Math.PI / 4 + (T >= 0 ? T * 0.35 : 0);
                        float c = (float)Math.Cos(a), sn = (float)Math.Sin(a), br = T >= 0 ? 0.1f * (float)Math.Sin(T * 2.2 + i) : 0;
                        g.DrawLine(pen, cx + c * rad * (1.45f + br), cy + sn * rad * (1.45f + br), cx + c * rad * (1.8f + br), cy + sn * rad * (1.8f + br));
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
            if (mono) using (var pen = new Pen(Mono, Math.Max(1, s * 0.05f))) g.DrawPath(pen, p);
            else using (var b = new SolidBrush(light ? Color.FromArgb(150, 160, 190) : Color.FromArgb(222, 226, 240))) g.FillPath(b, p);
        }
    }

    // A cloud filling r: three bumps on a rounded base.
    static void Cloud(Graphics g, RectangleF r, Color col, float s)
    {
        r.X += Sway;
        using (var p = new GraphicsPath { FillMode = FillMode.Winding })
        {
            float w = r.Width, h = r.Height;
            p.AddPath(Draw.Round(new RectangleF(r.X, r.Y + h * 0.42f, w, h * 0.58f), h * 0.29f), false);
            p.AddEllipse(r.X + w * 0.12f, r.Y + h * 0.28f, h * 0.62f, h * 0.62f);
            p.AddEllipse(r.X + w * 0.32f, r.Y, h * 0.86f, h * 0.86f);
            p.AddEllipse(r.X + w * 0.6f, r.Y + h * 0.3f, h * 0.58f, h * 0.58f);
            if (!Mono.IsEmpty)
            {
                // the union's outline: fill it with the background, then trace it by widening
                using (var b = new SolidBrush(MonoBg)) g.FillPath(b, p);
                using (var region = new Region(p))
                using (var pen = new Pen(col, Math.Max(1, s * 0.05f) * 2))
                using (var clip = g.Clip)
                {
                    g.SetClip(region, CombineMode.Exclude);
                    using (var wide = (GraphicsPath)p.Clone()) { wide.Widen(pen); using (var b = new SolidBrush(col)) g.FillPath(b, wide); }
                    g.Clip = clip;
                }
                return;
            }
            using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, w, h + 2), col, Draw.Mix(col, Color.FromArgb(120, 130, 150), 0.28f), 90f)) g.FillPath(b, p);
        }
    }
}

// ======================================================================= The widget
// One card. The main place can be shown in full (a big temperature, details, the next hours as a curve, the sun and
// the days ahead) and the other places as compact rows under it; or every place as a compact row. Clicking a row
// makes that place the main one.
class WeatherForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    const int MaxPlaces = 8;

    readonly Settings c = Settings.Load();
    readonly DibSurface surface = new DibSurface();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer(), fetchTimer = new System.Windows.Forms.Timer(), anim = new System.Windows.Forms.Timer { Interval = 50 };
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = KinetikSuite.MenuFocus.Attach(new ContextMenuStrip());
    int layer, winW, winH, margin;
    float u = 1;
    bool dragging, moved, fetching;
    Point grab, downAt;
    ThemePicker picker;
    string lastError; // the main place's last fetch error, or null
    readonly Dictionary<string, AirQ> air = new Dictionary<string, AirQ>(); // air quality by place, main place only
    DateTime rainAlerted = DateTime.MinValue; // when the rain we last announced was due to start

    // Every place's forecast and the JSON it came from, by "lat,lon"; and the error from the last try for any that failed.
    readonly Dictionary<string, Forecast> got = new Dictionary<string, Forecast>();
    readonly Dictionary<string, string> raw = new Dictionary<string, string>();
    readonly Dictionary<string, string> errs = new Dictionary<string, string>();
    // Where each compact row was drawn (window coordinates), for clicks.
    readonly List<KeyValuePair<RectangleF, Place>> rowHits = new List<KeyValuePair<RectangleF, Place>>();

    Forecast data { get { Forecast f; return c.HasPlace && got.TryGetValue(c.Main.Key, out f) ? f : null; } }

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
        tick.Tick += (s, e) => { Tick(); CheckAlerts(); Render(); };
        anim.Tick += (s, e) => { if (!dragging) Render(); }; // 20 frames a second, only while animation is on
        fetchTimer.Tick += (s, e) => { fetchTimer.Stop(); Fetch(); };

        if (Program.Demo && Program.DemoPlace != null)
        {
            var sp = Weather.ParseCoords(Program.DemoPlace);
            if (sp != null) { c.Lat = sp.Lat; c.Lon = sp.Lon; c.Place = "Live demo"; c.Others = ""; }
            try { var f = Weather.Parse(Weather.Get(Weather.Url(c.Lat, c.Lon))); f.Fetched = DateTime.UtcNow; got[c.Main.Key] = f; }
            catch (Exception ex) { lastError = Friendly(ex); }
        }
        else if (Program.Demo)
        {
            int i = 0;
            foreach (var p in c.AllPlaces()) got[p.Key] = Weather.Demo(i++);
            air[c.Main.Key] = new AirQ { Aqi = 38, Pm25 = 9 };
        }
        else
            foreach (var p in c.AllPlaces())
            {
                string json;
                var f = Weather.LoadCache(p.Lat, p.Lon, p.Key == c.Main.Key, out json);
                if (f != null) { got[p.Key] = f; raw[p.Key] = json; }
            }

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
                if (e.Reason == SessionSwitchReason.SessionLock) { tick.Stop(); anim.Stop(); }
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
        try { BeginInvoke((Action)(() => { if (lastError != null || errs.Count > 0 || Stale) ScheduleFetchSeconds(5); })); } catch { }
    }

    // Old when any place has no forecast yet, or the oldest one is due for a refresh.
    bool Stale
    {
        get
        {
            foreach (var p in c.AllPlaces())
            {
                Forecast f;
                if (!got.TryGetValue(p.Key, out f) || (DateTime.UtcNow - f.Fetched).TotalMinutes >= c.Refresh) return true;
            }
            return false;
        }
    }

    DateTime OldestFetch { get { var all = c.AllPlaces().Where(p => got.ContainsKey(p.Key)).Select(p => got[p.Key].Fetched).ToList(); return all.Count == 0 ? DateTime.MinValue : all.Min(); } }

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
            WriteIndex();
            if (!c.HasPlace) BeginInvoke((Action)ChoosePlace);
            else if (Stale) Fetch();
            else { FetchAir(); ScheduleFetchSeconds((int)Math.Max(5, c.Refresh * 60 - (DateTime.UtcNow - OldestFetch).TotalSeconds)); }
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
            tick.Dispose(); fetchTimer.Dispose(); anim.Dispose(); tray.Dispose(); menu.Dispose(); DisposeFonts(); shadow.Dispose(); if (textLayer != null) textLayer.Dispose();
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

    // ---- fetching: every place in turn, on a worker thread
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
        var places = c.AllPlaces();
        if (data == null) Render();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var results = new List<Tuple<Place, Forecast, string, string>>();
            foreach (var p in places)
            {
                Forecast f = null; string json = null, err = null;
                try { json = Weather.Get(Weather.Url(p.Lat, p.Lon)); f = Weather.Parse(json); f.Fetched = DateTime.UtcNow; }
                catch (Exception ex) { err = Friendly(ex); }
                results.Add(Tuple.Create(p, f, json, err));
            }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    fetching = false;
                    foreach (var r in results)
                    {
                        var key = r.Item1.Key;
                        if (r.Item2 != null) { got[key] = r.Item2; raw[key] = r.Item3; errs.Remove(key); }
                        else errs[key] = r.Item4;
                    }
                    SaveCaches();
                    FetchAir(); CheckAlerts();
                    var main = c.HasPlace ? c.Main.Key : "";
                    lastError = errs.ContainsKey(main) ? errs[main] : null;
                    // a place added while this ran gets its turn straight away
                    if (c.AllPlaces().Any(p => !got.ContainsKey(p.Key) && !errs.ContainsKey(p.Key))) { Fetch(); return; }
                    ScheduleFetchSeconds(errs.Count == 0 ? c.Refresh * 60 : Math.Min(5, c.Refresh) * 60);
                    Render();
                }));
            }
            catch { }
        });
    }

    // The main place's air quality, from Open-Meteo's air quality service. A place it has no data for just shows no tile.
    void FetchAir()
    {
        if (Program.Demo || !c.ShowAir || !c.HasPlace) return;
        var p = c.Main;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            AirQ a = null;
            try { a = Weather.ParseAir(Weather.Get(Weather.AirUrl(p.Lat, p.Lon))); } catch { }
            try { BeginInvoke((Action)(() => { if (a != null) air[p.Key] = a; Render(); })); } catch { }
        });
    }

    // "Rain in about 20 minutes", once per spell of rain: the same rain is not announced again within 90 minutes.
    void CheckAlerts()
    {
        var f = data;
        if (Program.Demo || !c.RainAlert || f == null || (DateTime.UtcNow - f.Fetched).TotalMinutes > 120) return;
        DateTime start;
        int mins = Weather.RainSoon(f, out start);
        if (mins < 0 || Math.Abs((start - rainAlerted).TotalMinutes) < 90) return;
        rainAlerted = start;
        tray.ShowBalloonTip(8000, mins <= 1 ? "Rain starting now" : "Rain in about " + mins + " minutes", c.Place == "" ? "Kinetik Weather" : c.Place, ToolTipIcon.None);
    }

    // The main place's forecast goes to forecast.json (Kinetik Screen reads it); the others go beside it, listed in places.tsv.
    void SaveCaches()
    {
        if (Program.Demo) return;
        foreach (var p in c.AllPlaces())
        {
            string json;
            if (raw.TryGetValue(p.Key, out json)) Weather.SaveCache(p.Lat, p.Lon, json, p.Key == c.Main.Key);
        }
        WriteIndex();
    }

    void WriteIndex() { if (!Program.Demo) Weather.SaveIndex(c.AllPlaces()); }

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

    // ---- mouse: drag to move, click a place's row to show it in full, double-click for the radar map, right-click for the menu
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
        else if (!dragging)
            Cursor = RowAt(e.Location) != null ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (dragging && moved) c.Save();
            else
            {
                var p = RowAt(e.Location);
                if (p != null && p.Key != c.Main.Key) MakeMain(p);
            }
            dragging = false;
            Cursor = Cursors.Default;
        }
        else if (e.Button == MouseButtons.Right) menu.Show(Cursor.Position);
        base.OnMouseUp(e);
    }

    Place RowAt(Point pt) { foreach (var h in rowHits) if (h.Key.Contains(pt)) return h.Value; return null; }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !moved && RowAt(e.Location) == null) OpenMap();
        base.OnMouseDoubleClick(e);
    }

    void OpenMap()
    {
        if (!c.HasPlace) { ChoosePlace(); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.windy.com/" + c.Lat + "/" + c.Lon + "/radar?radar," + c.Lat + "," + c.Lon + ",8") { UseShellExecute = true }); } catch { }
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
        if (double.IsNaN(kmh)) return "–";
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
    List<Hour> ShownHours(Forecast f, int want)
    {
        if (f == null) return new List<Hour>();
        var now = f.PlaceNow;
        var first = f.Hours.FindIndex(h => h.T.AddHours(1) > now);
        if (first < 0) return new List<Hour>();
        int step = want > 12 ? 2 : 1, n = Math.Min(12, want / step);
        var list = new List<Hour>();
        for (int i = first; i < f.Hours.Count && list.Count < n; i += step) list.Add(f.Hours[i]);
        return list;
    }

    List<DayF> ShownDays(Forecast f)
    {
        if (f == null) return new List<DayF>();
        var today = f.PlaceNow.Date;
        return f.Days.Where(d => d.Date >= today).Take(c.Days).ToList();
    }

    static DayF TodayOf(Forecast f) { if (f == null) return null; var t = f.PlaceNow.Date; return f.Days.FirstOrDefault(d => d.Date == t); }

    // ---- layout: every block's height (at 100%), so the card is sized before it's drawn
    const float HeaderH = 24, HeroH = 96, ChipsH = 50, IconsH = 24, CurveH = 54, LabelsH = 16, RainLineH = 20, RainOnlyH = 26,
                SunH = 50, DayRowH = 26, RowH = 60, Gap = 10, TilePad = 10, BannerH = 26, BannerGap = 6;

    bool Detailed { get { return c.View != 2; } }
    List<Place> Rows { get { var all = c.AllPlaces(); return c.View == 2 ? all : c.View == 1 ? all.Skip(1).ToList() : new List<Place>(); } }

    float HourlyTileH()
    {
        if (!c.ShowHourly && !c.ShowRain) return 0;
        float h = TilePad * 2;
        if (c.ShowRain) h += RainLineH;
        h += c.ShowHourly ? IconsH + CurveH + LabelsH : RainOnlyH;
        return h;
    }

    float ContentHeight()
    {
        float h = HeaderH;
        if (!c.HasPlace) return h + 48;
        var f = data;
        if (Detailed)
        {
            if (f == null) h += 48;
            else
            {
                if (Alerts(f).Count > 0) h += BannerH + BannerGap;
                h += HeroH;
                if (c.ShowDetails) h += Gap + ChipsH;
                if ((c.ShowHourly || c.ShowRain) && ShownHours(f, c.Hours).Count > 0) h += Gap + HourlyTileH();
                var today = TodayOf(f);
                if (c.ShowSun && today != null && today.Rise != DateTime.MinValue) h += Gap + SunH + TilePad * 2;
                if (c.ShowDaily) { var n = ShownDays(f).Count; if (n > 0) h += Gap + n * DayRowH + TilePad * 2; }
            }
        }
        var rows = Rows.Count;
        if (rows > 0) h += (Detailed ? Gap + 18 : 4) + rows * RowH + (rows - 1) * 8;
        return h;
    }

    // ---- painting
    public void Render()
    {
        if (IsDisposed || !IsHandleCreated) return;
        u = DeviceDpi / 96f * c.Scale / 100f;
        margin = (int)(14 * u);
        float pad = 16 * u;
        int cardW = (int)(c.Width * u), cardH = (int)(pad * 2 + ContentHeight() * u);
        winW = cardW + margin * 2; winH = cardH + margin * 2;

        bool wantThrough = c.Locked && c.ClickThrough;
        int ex = GetWindowLong(Handle, -20);
        SetWindowLong(Handle, -20, wantThrough ? ex | 0x20 : ex & ~0x20); // TRANSPARENT: clicks fall through

        EnsurePosition();
        bool animate = c.Animate && Program.ShotPath == null;
        WxIcon.T = animate ? clock.Elapsed.TotalSeconds : -1;
        anim.Enabled = animate && data != null;
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
    string Fam { get { return c.FontName != "" && Draw.HasFont(c.FontName) ? c.FontName : Theme.IsTerm(c.ThemeName) ? Theme.Mono : Draw.Family; } }

    // Fonts by size and style, made again only when the font or scale changes.
    readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    string fontKey;

    Font F(float size, FontStyle style = FontStyle.Regular)
    {
        var key = Fam + "|" + u + "|" + DeviceDpi;
        if (key != fontKey) { DisposeFonts(); fontKey = key; }
        var k = size + "/" + (int)style;
        Font f;
        if (!fonts.TryGetValue(k, out f)) fonts[k] = f = new Font(Fam, size * u / (DeviceDpi / 96f), style, GraphicsUnit.Point);
        return f;
    }

    // The big temperature: Segoe UI Variable Display's light weight when the default font is in use.
    Font Big(float size)
    {
        bool display = c.FontName == "" && !Theme.IsTerm(c.ThemeName) && Draw.HasFont("Segoe UI Variable Display Light");
        if (!display) return F(size);
        var key = Fam + "|" + u + "|" + DeviceDpi;
        if (key != fontKey) { DisposeFonts(); fontKey = key; }
        var k = "big" + size;
        Font f;
        if (!fonts.TryGetValue(k, out f)) fonts[k] = f = new Font("Segoe UI Variable Display Light", size * u / (DeviceDpi / 96f), FontStyle.Regular, GraphicsUnit.Point);
        return f;
    }

    void DisposeFonts() { KinetikSuite.Resources.DisposeValues(fonts); fontKey = null; }

    static readonly StringFormat fmtL = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat fmtR = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat fmtC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

    float CardRadius(Theme th) { return (th.Term ? 6 : 18) * u; }

    void PaintCard(Graphics target, int cardW, int cardH, float pad)
    {
        var th = Theme.Get(c.ThemeName);
        target.Clear(Color.Transparent);
        target.SmoothingMode = SmoothingMode.AntiAlias;
        target.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var card = new RectangleF(margin, margin, cardW, cardH);
        float rad = CardRadius(th);
        shadow.Paint(target, winW, winH, card + "|" + u + "|" + c.Opacity + "|" + rad, sg =>
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
        WxIcon.Mono = th.Term ? th.Text : Color.Empty;
        WxIcon.MonoBg = th.Bg;
        try { PaintContent(g, th, card, rad, crisp ? 255 : alpha, pad); }
        finally
        {
            WxIcon.Mono = Color.Empty;
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

    // The sky's colour for a condition, used to tint the top of the card.
    static Color Sky(int code, bool day)
    {
        if (code >= 95) return Color.FromArgb(124, 92, 214);
        if ((code >= 71 && code <= 77) || code == 85 || code == 86) return Color.FromArgb(170, 205, 255);
        if (code >= 51) return Color.FromArgb(48, 120, 220);
        if (code == 45 || code == 48) return Color.FromArgb(150, 156, 170);
        if (code == 3) return day ? Color.FromArgb(120, 136, 164) : Color.FromArgb(70, 80, 110);
        return day ? Color.FromArgb(255, 160, 60) : Color.FromArgb(70, 76, 190);
    }

    void PaintContent(Graphics g, Theme th, RectangleF card, float rad, int alpha, float pad)
    {
        rowHits.Clear();
        var f = data;
        using (var p = Draw.Round(card, rad))
        {
            using (var b = new SolidBrush(Color.FromArgb(alpha, th.Bg))) g.FillPath(b, p);
            if (!th.Term && f != null && Detailed)
            {
                // a soft wash of the sky's colour, strongest at the top
                var sky = Sky(f.Code, f.IsDay);
                var wash = new RectangleF(card.X, card.Y, card.Width, Math.Min(card.Height, 260 * u));
                using (var b = new LinearGradientBrush(new RectangleF(wash.X, wash.Y - 1, wash.Width, wash.Height + 2),
                    Color.FromArgb(th.Light ? 46 : 70, sky), Color.FromArgb(0, sky), 90f))
                using (var clip = new Region(p))
                {
                    var old = g.Clip; g.Clip = clip;
                    g.FillRectangle(b, wash);
                    g.Clip = old; old.Dispose();
                }
            }
            using (var pen = new Pen(th.Term ? Color.FromArgb(150, th.Sub) : th.Line, th.Term ? Math.Max(1, u) : 1)) g.DrawPath(pen, p);
        }

        using (var text = new SolidBrush(th.Text))
        using (var sub = new SolidBrush(th.Sub))
        {
            float x = card.X + pad, w = card.Width - pad * 2, y = card.Y + pad;

            // header: the place (or "Weather" when every place is a row), and how fresh the forecast is
            string status = fetching && f == null ? "Updating…"
                : lastError != null ? (f == null ? "" : "Offline · " + Ago(f.Fetched))
                : f != null ? "Updated " + Ago(c.View == 2 ? OldestFetch : f.Fetched) : "";
            var sw = status == "" ? 0 : g.MeasureString(status, F(8.5f), 400, fmtR).Width + 4 * u;
            using (var warn = new SolidBrush(lastError != null ? th.Accent : th.Sub))
                g.DrawString(status, F(8.5f), warn, new RectangleF(x + w - sw, y, sw, HeaderH * u), fmtR);
            string title = !c.HasPlace ? "Kinetik Weather" : c.View == 2 ? "Weather" : (c.Place == "" ? "Weather" : c.Place);
            float tx = x;
            if (th.Term) title = "> " + title;
            else if (c.HasPlace && c.View != 2) { Pin(g, th, x, y + HeaderH * u / 2); tx += 14 * u; }
            g.DrawString(title, F(11, FontStyle.Bold), text, new RectangleF(tx, y, w - sw - 6 * u - (tx - x), HeaderH * u), fmtL);
            y += HeaderH * u;

            if (!c.HasPlace || (Detailed && f == null))
            {
                string msg = !c.HasPlace ? "Right-click → Places → Add a place… to choose one."
                    : lastError != null ? lastError + ". Trying again in a few minutes." : "Getting the forecast…";
                using (var wrap = new StringFormat { Trimming = StringTrimming.EllipsisWord })
                    g.DrawString(msg, F(9.5f), sub, new RectangleF(x, y + 6 * u, w, 40 * u), wrap);
                if (!c.HasPlace) return;
                y += 48 * u;
            }
            else if (Detailed)
            {
                var alerts = Alerts(f);
                if (alerts.Count > 0) { PaintBanner(g, th, text, f, alerts, new RectangleF(x, y, w, BannerH * u)); y += (BannerH + BannerGap) * u; }
                PaintHero(g, th, text, sub, f, x, y, w);
                y += HeroH * u;
                if (c.ShowDetails) { y += Gap * u; PaintChips(g, th, text, sub, f, x, y, w); y += ChipsH * u; }

                var hours = ShownHours(f, c.Hours);
                if ((c.ShowHourly || c.ShowRain) && hours.Count > 0)
                {
                    y += Gap * u;
                    float h = HourlyTileH() * u;
                    Tile(g, th, new RectangleF(x, y, w, h));
                    PaintHourly(g, th, text, sub, f, hours, x + TilePad * u, y + TilePad * u, w - TilePad * u * 2);
                    y += h;
                }

                var today = TodayOf(f);
                if (c.ShowSun && today != null && today.Rise != DateTime.MinValue)
                {
                    y += Gap * u;
                    Tile(g, th, new RectangleF(x, y, w, (SunH + TilePad * 2) * u));
                    PaintSun(g, th, text, sub, f, today, x + TilePad * u, y + TilePad * u, w - TilePad * u * 2);
                    y += (SunH + TilePad * 2) * u;
                }

                var days = ShownDays(f);
                if (c.ShowDaily && days.Count > 0)
                {
                    y += Gap * u;
                    Tile(g, th, new RectangleF(x, y, w, (days.Count * DayRowH + TilePad * 2) * u));
                    PaintDays(g, th, text, sub, f, days, x + TilePad * u, y + TilePad * u, w - TilePad * u * 2);
                    y += (days.Count * DayRowH + TilePad * 2) * u;
                }
            }

            var rows = Rows;
            if (rows.Count > 0)
            {
                if (Detailed)
                {
                    y += Gap * u;
                    g.DrawString(th.Term ? "-- other places --" : "Other places", F(8, FontStyle.Bold), sub, new RectangleF(x, y, w, 18 * u), fmtL);
                    y += 18 * u;
                }
                else y += 4 * u;
                foreach (var p in rows)
                {
                    var r = new RectangleF(x, y, w, RowH * u);
                    PaintRow(g, th, text, sub, p, r);
                    rowHits.Add(new KeyValuePair<RectangleF, Place>(r, p));
                    y += (RowH + 8) * u;
                }
            }
        }
    }

    // A section's background: a faint rounded panel, or a thin box in the terminal themes.
    void Tile(Graphics g, Theme th, RectangleF r)
    {
        using (var p = Draw.Round(r, (th.Term ? 3 : 12) * u))
        {
            if (th.Term) { using (var pen = new Pen(Color.FromArgb(110, th.Sub), Math.Max(1, u))) g.DrawPath(pen, p); return; }
            using (var b = new SolidBrush(Color.FromArgb(th.Light ? 12 : 16, th.Light ? Color.Black : Color.White))) g.FillPath(b, p);
            using (var pen = new Pen(Color.FromArgb(th.Light ? 14 : 18, th.Light ? Color.Black : Color.White), 1)) g.DrawPath(pen, p);
        }
    }

    // A map pin before the place's name.
    void Pin(Graphics g, Theme th, float x, float cy)
    {
        float d = 9 * u, top = cy - d * 0.75f;
        using (var p = new GraphicsPath())
        {
            p.AddArc(x, top, d, d, 135, 270);
            p.AddLine(x + d * 0.85f, top + d * 0.85f, x + d / 2, top + d * 1.45f);
            p.CloseFigure();
            using (var b = new SolidBrush(th.Accent)) g.FillPath(b, p);
        }
        using (var b = new SolidBrush(th.Bg)) g.FillEllipse(b, x + d * 0.32f, top + d * 0.32f, d * 0.36f, d * 0.36f);
    }

    Color RainCol(Theme th) { return th.Term ? th.Text : th.Light ? Color.FromArgb(40, 120, 230) : WxIcon.Rain; }
    Color SunCol(Theme th) { return th.Term ? th.Text : WxIcon.Sun; }
    Color TempCol(Theme th, double celsius) { return th.Term ? th.Text : TempColour(celsius); }

    // the big temperature with the condition and the day's range under it, and a large icon on the right
    void PaintHero(Graphics g, Theme th, Brush text, Brush sub, Forecast f, float x, float y, float w)
    {
        float isz = 78 * u;
        WxIcon.Paint(g, new RectangleF(x + w - isz + 4 * u, y + 2 * u, isz, isz), f.Code, f.IsDay, th.Light);
        var t = Temp(c.FeelsHero ? f.Feels : f.Temp);
        g.DrawString(t, Big(40), text, new RectangleF(x - 4 * u, y - 2 * u, w - isz, 62 * u), fmtL);
        var today = TodayOf(f);
        g.DrawString(Weather.Describe(f.Code, f.IsDay), F(11.5f, FontStyle.Bold), text, new RectangleF(x, y + 60 * u, w - 8 * u, 18 * u), fmtL);
        string hl = today != null ? "H " + Temp(today.Max) + "   L " + Temp(today.Min) : "";
        if (c.FeelsHero) hl = "Feels like  ·  actual " + Temp(f.Temp) + (hl != "" ? "   ·   " + hl : "");
        else if (!c.ShowDetails) hl += (hl != "" ? "   ·   " : "") + "Feels " + Temp(f.Feels);
        if (TimeZoneDiffers(f)) hl += (hl != "" ? "   ·   " : "") + ClockText(f.PlaceNow) + " there";
        g.DrawString(hl, F(9), sub, new RectangleF(x, y + 78 * u, w, 17 * u), fmtL);
    }

    static bool TimeZoneDiffers(Forecast f) { return Math.Abs(f.Offset - TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalSeconds) >= 60; }

    // four small tiles: feels like, wind (with an arrow showing where it blows), humidity, and the UV index (or rain)
    void PaintChips(Graphics g, Theme th, Brush text, Brush sub, Forecast f, float x, float y, float w)
    {
        var today = TodayOf(f);
        float gap = 6 * u, h = ChipsH * u;
        bool uv = today != null && !double.IsNaN(today.Uv);
        AirQ aq;
        bool hasAir = c.ShowAir && c.HasPlace && air.TryGetValue(c.Main.Key, out aq) && !double.IsNaN(aq.Aqi);
        if (hasAir) aq = air[c.Main.Key]; else aq = null;
        // label (and a shorter one for when it doesn't fit), value, and a unit drawn small after the value
        var wind = WindText(f.Wind).Split(' ');
        var items = new List<string[]>
        {
            c.FeelsHero ? new[] { "Actual", "Actual", Temp(f.Temp), "" } : new[] { "Feels like", "Feels", Temp(f.Feels), "" },
            new[] { "Wind " + Weather.Compass(f.WindDir), Weather.Compass(f.WindDir), wind[0], wind.Length > 1 ? wind[1] : "" },
            new[] { "Humidity", "Humid.", double.IsNaN(f.Humidity) ? "–" : Math.Round(f.Humidity).ToString("0"), double.IsNaN(f.Humidity) ? "" : "%" },
            uv ? new[] { "UV index", "UV", Math.Round(today.Uv).ToString("0"), UvWord(today.Uv) }
               : new[] { "Rain today", "Rain", today == null || double.IsNaN(today.Pop) ? "–" : Math.Round(today.Pop).ToString("0"), "%" },
        };
        if (aq != null) items.Add(new[] { "Air quality", "Air", Math.Round(aq.Aqi).ToString("0"), "" }); // the band is a coloured dot: the tile is narrow
        float cw = (w - gap * (items.Count - 1)) / items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            var r = new RectangleF(x + i * (cw + gap), y, cw, h);
            Tile(g, th, r);
            float room = r.Width - 12 * u;
            var label = g.MeasureString(items[i][0], F(7.5f)).Width <= room ? items[i][0] : items[i][1];
            g.DrawString(label, F(7.5f), sub, new RectangleF(r.X + 8 * u, r.Y + 6 * u, room, 16 * u), fmtL);
            float vx = r.X + 8 * u;
            if (i == 1 && !double.IsNaN(f.WindDir))
            {
                Arrow(g, th.Text, vx + 5 * u, r.Y + 33 * u, 5.5f * u, (float)f.WindDir + 180);
                vx += 13 * u;
            }
            if (aq != null && i == items.Count - 1)
                using (var b = new SolidBrush(Weather.AqiColour(aq.Aqi))) g.FillEllipse(b, r.Right - 14 * u, r.Y + 10 * u, 6 * u, 6 * u);
            var vf = F(10.5f, FontStyle.Bold);
            float vw = g.MeasureString(items[i][2], vf, 400, fmtL).Width - 1 * u;
            g.DrawString(items[i][2], vf, text, new RectangleF(vx, r.Y + 23 * u, r.Right - vx - 4 * u, 20 * u), fmtL);
            if (items[i][3] != "" && vx + vw < r.Right - 8 * u)
                g.DrawString(items[i][3], F(7.5f), sub, new RectangleF(vx + vw, r.Y + 25 * u, r.Right - vx - vw - 2 * u, 18 * u), fmtL);
        }
    }

    List<Warn> Alerts(Forecast f) { return c.ShowWarnings && f != null ? Weather.Warnings(f) : new List<Warn>(); }

    string WarnText(Warn w, Forecast f)
    {
        string day = w.When.Date == f.PlaceNow.Date ? "today" : "tomorrow";
        switch (w.Kind)
        {
            case "storm": return (w.Level == 2 ? "Severe thunderstorms" : "Thunderstorms") + " around " + HourText(w.When);
            case "rain": return "Heavy rain around " + HourText(w.When) + " · " + Math.Round(w.Value) + " mm/h";
            case "wind": return (w.Level == 2 ? "Damaging winds" : "Strong winds") + " around " + HourText(w.When) + " · gusts " + WindText(w.Value);
            case "snow": return "Heavy snow around " + HourText(w.When);
            case "ice": return "Freezing rain around " + HourText(w.When);
            case "heat": return (w.Level == 2 ? "Extreme heat " : "Very hot ") + day + " · up to " + Temp(w.Value);
            default: return (w.Level == 2 ? "Extreme cold " : "Very cold ") + day + " · down to " + Temp(w.Value);
        }
    }

    // A strip under the header for the worst alert, amber or red, with "+2" when there are more.
    void PaintBanner(Graphics g, Theme th, Brush text, Forecast f, List<Warn> alerts, RectangleF r)
    {
        var col = alerts[0].Level == 2 ? Color.FromArgb(235, 72, 72) : Color.FromArgb(255, 170, 60);
        if (th.Term) col = th.Text;
        using (var p = Draw.Round(r, (th.Term ? 3 : 9) * u))
        {
            using (var b = new SolidBrush(Color.FromArgb(th.Light ? 40 : 46, col))) g.FillPath(b, p);
            using (var pen = new Pen(Color.FromArgb(110, col), Math.Max(1, u))) g.DrawPath(pen, p);
        }
        float s = 12 * u, ix = r.X + 9 * u, iy = r.Y + (r.Height - s) / 2;
        using (var tri = new GraphicsPath())
        {
            tri.AddPolygon(new[] { new PointF(ix + s / 2, iy), new PointF(ix + s, iy + s), new PointF(ix, iy + s) });
            using (var b = new SolidBrush(col)) g.FillPath(b, tri);
        }
        using (var b = new SolidBrush(th.Bg)) g.FillRectangle(b, ix + s / 2 - 0.7f * u, iy + s * 0.35f, 1.4f * u, s * 0.35f);
        using (var b = new SolidBrush(th.Bg)) g.FillRectangle(b, ix + s / 2 - 0.7f * u, iy + s * 0.78f, 1.4f * u, 1.4f * u);
        string msg = WarnText(alerts[0], f) + (alerts.Count > 1 ? "   +" + (alerts.Count - 1) : "");
        g.DrawString(msg, F(9, FontStyle.Bold), text, new RectangleF(ix + s + 8 * u, r.Y, r.Right - ix - s - 14 * u, r.Height), fmtL);
    }

    static string UvWord(double uv) { return uv < 3 ? "Low" : uv < 6 ? "Mod" : uv < 8 ? "High" : uv < 11 ? "V.high" : "Extr"; }

    // a small arrow pointing toward bearing (degrees clockwise from north)
    static void Arrow(Graphics g, Color col, float cx, float cy, float r, float bearing)
    {
        double a = (bearing - 90) * Math.PI / 180;
        Func<double, float, PointF> at = (ang, len) => new PointF(cx + (float)Math.Cos(ang) * len, cy + (float)Math.Sin(ang) * len);
        var pts = new[] { at(a, r), at(a + 2.5, r), at(a + Math.PI, r * 0.35f), at(a - 2.5, r) };
        using (var b = new SolidBrush(col)) g.FillPolygon(b, pts);
    }

    // The next hours: icons along the top, the temperature as a smooth curve with each hour's value, the chance of rain
    // as soft bars behind it, and the hours underneath; a line above says when rain is most likely.
    void PaintHourly(Graphics g, Theme th, Brush text, Brush sub, Forecast f, List<Hour> hours, float x, float y, float w)
    {
        var rain = RainCol(th);
        if (c.ShowRain)
        {
            var best = hours.Where(h => !double.IsNaN(h.Pop)).OrderByDescending(h => h.Pop).FirstOrDefault();
            string span = c.Hours > 12 ? "24 hours" : c.Hours + " hours";
            string line = best == null ? "No chance of rain given"
                : best.Pop >= 50 ? "Rain likely around " + (best == hours[0] ? "now" : HourText(best.T)) + " · " + best.Pop + "%"
                : best.Pop >= 20 ? "Chance of rain around " + (best == hours[0] ? "now" : HourText(best.T)) + " · " + best.Pop + "%"
                : "No rain expected in the next " + span;
            Drop(g, rain, x + 1 * u, y + 4 * u, 9 * u);
            g.DrawString(line, F(8.5f), sub, new RectangleF(x + 15 * u, y, w - 15 * u, 16 * u), fmtL);
            y += RainLineH * u;
        }

        int n = hours.Count;
        float colW = w / n;
        if (!c.ShowHourly)
        {
            PaintRainBars(g, th, hours, x, y, colW, RainOnlyH * u - 4 * u, true);
            return;
        }

        // icons
        float isz = Math.Min(20 * u, colW - 4 * u);
        for (int i = 0; i < n; i++)
            WxIcon.Paint(g, new RectangleF(x + i * colW + (colW - isz) / 2, y + (IconsH * u - isz) / 2, isz, isz), hours[i].Code, hours[i].Day, th.Light);
        y += IconsH * u;

        // the curve: each hour's temperature between the area's top (with room for its label) and bottom
        float top = y + 16 * u, bottom = y + CurveH * u - 6 * u;
        var temps = hours.Select((h, i) => i == 0 && !double.IsNaN(f.Temp) ? f.Temp : h.Temp).ToList();
        var valid = temps.Where(t => !double.IsNaN(t)).ToList();
        double lo = valid.Count > 0 ? valid.Min() : 0, hi = valid.Count > 0 ? valid.Max() : 1;
        if (hi - lo < 4) { double mid = (hi + lo) / 2; lo = mid - 2; hi = mid + 2; }
        if (c.ShowRain) PaintRainBars(g, th, hours, x, y + 6 * u, colW, CurveH * u - 6 * u, false);
        var pts = new List<PointF>();
        for (int i = 0; i < n; i++)
        {
            double t = double.IsNaN(temps[i]) ? (lo + hi) / 2 : temps[i];
            pts.Add(new PointF(x + i * colW + colW / 2, bottom - (float)((t - lo) / (hi - lo)) * (bottom - top)));
        }
        if (n >= 2)
        {
            var arr = pts.ToArray();
            // the area under the curve fades out downwards
            using (var area = new GraphicsPath())
            {
                area.AddCurve(arr, 0.5f);
                area.AddLine(arr[n - 1].X, arr[n - 1].Y, arr[n - 1].X, bottom + 6 * u);
                area.AddLine(arr[n - 1].X, bottom + 6 * u, arr[0].X, bottom + 6 * u);
                area.CloseFigure();
                Color warm = TempCol(th, hi);
                using (var b = new LinearGradientBrush(new RectangleF(x, top - 1, w, bottom - top + 8 * u), Color.FromArgb(th.Term ? 40 : 70, warm), Color.FromArgb(0, warm), 90f))
                    g.FillPath(b, area);
            }
            using (var b = new LinearGradientBrush(new RectangleF(x, top - 2, w, bottom - top + 4), TempCol(th, hi), TempCol(th, lo), 90f))
            using (var pen = new Pen(b, 2.2f * u) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawCurve(pen, arr, 0.5f);
        }
        for (int i = 0; i < n; i++)
        {
            var pt = pts[i];
            float r = (i == 0 ? 3.6f : 2.4f) * u;
            using (var b = new SolidBrush(i == 0 ? th.Text : TempCol(th, double.IsNaN(temps[i]) ? lo : temps[i]))) g.FillEllipse(b, pt.X - r, pt.Y - r, r * 2, r * 2);
            if (i == 0) using (var pen = new Pen(th.Bg, 1.4f * u)) g.DrawEllipse(pen, pt.X - r, pt.Y - r, r * 2, r * 2);
            g.DrawString(Temp(temps[i]), F(8, i == 0 ? FontStyle.Bold : FontStyle.Regular), i == 0 ? text : sub,
                new RectangleF(pt.X - colW / 2 - 6 * u, pt.Y - 17 * u, colW + 12 * u, 14 * u), fmtC);
        }
        y += CurveH * u;

        // hours
        for (int i = 0; i < n; i++)
        {
            var h = hours[i];
            bool full = (i == 1 && colW >= 30 * u) || (i > 1 && (hours[i - 1].T.Hour < 12) != (h.T.Hour < 12)) || n <= 8;
            g.DrawString(i == 0 ? "Now" : HourText(h.T, full), F(7.5f, i == 0 ? FontStyle.Bold : FontStyle.Regular), i == 0 ? text : sub,
                new RectangleF(x + i * colW - 6 * u, y, colW + 12 * u, LabelsH * u), fmtC);
        }
    }

    // a bar per hour for its chance of rain: on its own (tracks shown), or faint behind the temperature curve
    void PaintRainBars(Graphics g, Theme th, List<Hour> hours, float x, float y, float colW, float h, bool alone)
    {
        var rain = RainCol(th);
        float bw = Math.Max(3 * u, Math.Min(colW * (alone ? 0.5f : 0.62f), (alone ? 12 : 16) * u));
        for (int i = 0; i < hours.Count; i++)
        {
            float bx = x + i * colW + (colW - bw) / 2;
            if (alone)
                using (var track = new SolidBrush(Color.FromArgb(th.Light ? 18 : 22, th.Text)))
                using (var p = Draw.Round(new RectangleF(bx, y, bw, h), bw / 2)) g.FillPath(track, p);
            double pop = hours[i].Pop;
            if (double.IsNaN(pop) || pop < 10) continue;
            float bh = Math.Max(bw / 2, h * (float)Math.Min(1, pop / 100) * (alone ? 1 : 0.7f));
            using (var p = Draw.Round(new RectangleF(bx, y + h - bh, bw, bh), Math.Min(bw / 2, (alone ? bw / 2 : 3 * u))))
            using (var b = new SolidBrush(Color.FromArgb(alone ? (int)(110 + 145 * Math.Min(1, pop / 100)) : (int)((th.Term ? 30 : 40) + 70 * Math.Min(1, pop / 100)), rain)))
                g.FillPath(b, p);
        }
    }

    static void Drop(Graphics g, Color col, float dx, float dy, float d)
    {
        using (var b = new SolidBrush(col))
        using (var p = new GraphicsPath())
        {
            p.AddBezier(dx + d / 2, dy, dx + d * 0.1f, dy + d * 0.45f, dx, dy + d * 0.6f, dx, dy + d * 0.72f);
            p.AddArc(dx, dy + d * 0.44f, d, d * 0.56f, 180, -180);
            p.AddBezier(dx + d, dy + d * 0.72f, dx + d, dy + d * 0.6f, dx + d * 0.9f, dy + d * 0.45f, dx + d / 2, dy);
            g.FillPath(b, p);
        }
    }

    // the sun's path across today, with where it is now, sunrise and sunset under the ends
    void PaintSun(Graphics g, Theme th, Brush text, Brush sub, Forecast fc, DayF today, float x, float y, float w)
    {
        var now = fc.PlaceNow;
        var sun = SunCol(th);
        float arcW = w * 0.46f, ax = x + (w - arcW) / 2, ay = y + 4 * u, ah = SunH * u - 22 * u;
        var arc = new RectangleF(ax, ay, arcW, ah * 2);
        using (var pen = new Pen(Color.FromArgb(th.Light ? 60 : 70, th.Sub), 1.4f * u) { DashStyle = DashStyle.Dot })
            g.DrawArc(pen, arc, 180, 180);
        using (var pen = new Pen(th.Line, 1)) g.DrawLine(pen, ax - 10 * u, ay + ah, ax + arcW + 10 * u, ay + ah);

        double f = (now - today.Rise).TotalMinutes / Math.Max(1, (today.Set - today.Rise).TotalMinutes);
        if (f > 0 && f < 1)
        {
            // the part already travelled, then the sun itself
            using (var pen = new Pen(Color.FromArgb(200, sun), 1.8f * u)) g.DrawArc(pen, arc, 180, (float)(180 * f));
            double a = Math.PI * (1 - f);
            float sx = ax + arcW / 2 + (float)Math.Cos(a) * arcW / 2, sy = ay + ah - (float)Math.Sin(a) * ah, r = 5 * u;
            using (var b = new SolidBrush(Color.FromArgb(60, sun))) g.FillEllipse(b, sx - r * 1.8f, sy - r * 1.8f, r * 3.6f, r * 3.6f);
            using (var b = new SolidBrush(sun)) g.FillEllipse(b, sx - r, sy - r, r * 2, r * 2);
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
    void PaintDays(Graphics g, Theme th, Brush text, Brush sub, Forecast f, List<DayF> days, float x, float y, float w)
    {
        double lo = days.Where(d => !double.IsNaN(d.Min)).Select(d => d.Min).DefaultIfEmpty(0).Min();
        double hi = days.Where(d => !double.IsNaN(d.Max)).Select(d => d.Max).DefaultIfEmpty(1).Max();
        if (hi - lo < 1) hi = lo + 1;
        var rain = RainCol(th);
        float nameW = 58 * u, iconW = 26 * u, popW = 36 * u, tW = 32 * u;
        float barX = x + nameW + iconW + popW + tW, barW = Math.Max(20 * u, w - (nameW + iconW + popW + tW * 2) - 8 * u);
        var today = f.PlaceNow.Date;
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            float ry = y + i * DayRowH * u, rh = DayRowH * u;
            string name = d.Date == today ? "Today" : d.Date.ToString("ddd");
            g.DrawString(name, F(9.5f, d.Date == today ? FontStyle.Bold : FontStyle.Regular), text, new RectangleF(x, ry, nameW, rh), fmtL);
            float isz = 19 * u;
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
            using (var b = new LinearGradientBrush(new RectangleF(barX - 1, by, barW + 2, bh), TempCol(th, lo), TempCol(th, hi), 0f))
                g.FillPath(b, p);
            if (d.Date == today && !double.IsNaN(f.Temp))
            {
                float nx = barX + barW * (float)Math.Max(0, Math.Min(1, (f.Temp - lo) / (hi - lo)));
                using (var b = new SolidBrush(th.Text)) g.FillEllipse(b, nx - bh * 0.8f, by - bh * 0.3f, bh * 1.6f, bh * 1.6f);
                using (var pen = new Pen(th.Bg, 1.2f * u)) g.DrawEllipse(pen, nx - bh * 0.8f, by - bh * 0.3f, bh * 1.6f, bh * 1.6f);
            }
        }
    }

    // A compact row for one place: its name, local time and conditions, the next 12 hours as a little line, an icon,
    // and the temperature with the day's range.
    void PaintRow(Graphics g, Theme th, Brush text, Brush sub, Place p, RectangleF r)
    {
        Forecast f;
        got.TryGetValue(p.Key, out f);
        bool main = c.View == 2 && p.Key == c.Main.Key;
        Tile(g, th, r);
        if (main && !th.Term)
            using (var b = new SolidBrush(th.Accent))
            using (var path = Draw.Round(new RectangleF(r.X + 4 * u, r.Y + 14 * u, 3 * u, r.Height - 28 * u), 1.5f * u)) g.FillPath(b, path);
        float x = r.X + 12 * u, right = r.Right - 10 * u;
        string err;
        if (f == null)
        {
            g.DrawString(p.Name, F(10.5f, FontStyle.Bold), text, new RectangleF(x, r.Y + 8 * u, right - x, 22 * u), fmtL);
            string msg = errs.TryGetValue(p.Key, out err) ? err : fetching ? "Updating…" : "Waiting for the forecast";
            g.DrawString(msg, F(8.5f), sub, new RectangleF(x, r.Y + 30 * u, right - x, 18 * u), fmtL);
            return;
        }

        var today = TodayOf(f);
        float tempW = 58 * u;
        g.DrawString(Temp(f.Temp), Big(19), text, new RectangleF(right - tempW, r.Y + 6 * u, tempW + 2 * u, 30 * u), fmtR);
        if (today != null)
            g.DrawString(Temp(today.Max) + " / " + Temp(today.Min), F(8), sub, new RectangleF(right - tempW - 10 * u, r.Y + 36 * u, tempW + 12 * u, 16 * u), fmtR);
        float isz = 34 * u, ix = right - tempW - isz - 2 * u;
        WxIcon.Paint(g, new RectangleF(ix, r.Y + (r.Height - isz) / 2, isz, isz), f.Code, f.IsDay, th.Light);

        string line = Weather.Describe(f.Code, f.IsDay);
        if (TimeZoneDiffers(f)) line = ClockText(f.PlaceNow) + "  ·  " + line;
        if (errs.ContainsKey(p.Key)) line = "Offline · " + Ago(f.Fetched);
        string name = (th.Term && main ? "* " : "") + p.Name;
        float need = Math.Max(g.MeasureString(line, F(8.5f)).Width, g.MeasureString(name, F(10.5f, FontStyle.Bold)).Width);

        // the next 12 hours' temperatures as a little line, when there's room for it
        float sparkW = 64 * u, textRight = ix - 6 * u;
        if (textRight - x - need > sparkW + 12 * u)
        {
            var hrs = ShownHours(f, 12);
            if (hrs.Count >= 3)
            {
                float sx = textRight - sparkW, sy = r.Y + 18 * u, sh = 22 * u;
                double lo = hrs.Min(h => double.IsNaN(h.Temp) ? double.MaxValue : h.Temp), hi = hrs.Max(h => double.IsNaN(h.Temp) ? double.MinValue : h.Temp);
                if (hi - lo < 3) { double mid = (hi + lo) / 2; lo = mid - 1.5; hi = mid + 1.5; }
                var pts = hrs.Select((h, i) => new PointF(sx + sparkW * i / (hrs.Count - 1), sy + sh - (float)(((double.IsNaN(h.Temp) ? lo : h.Temp) - lo) / (hi - lo)) * sh)).ToArray();
                using (var b = new LinearGradientBrush(new RectangleF(sx, sy - 1, sparkW, sh + 2), TempCol(th, hi), TempCol(th, lo), 90f))
                using (var pen = new Pen(b, 1.6f * u) { LineJoin = LineJoin.Round })
                    g.DrawCurve(pen, pts, 0.5f);
                using (var b = new SolidBrush(th.Text)) g.FillEllipse(b, pts[0].X - 2.4f * u, pts[0].Y - 2.4f * u, 4.8f * u, 4.8f * u);
                textRight = sx - 8 * u;
            }
        }

        g.DrawString(name, F(10.5f, FontStyle.Bold), text, new RectangleF(x, r.Y + 8 * u, textRight - x, 22 * u), fmtL);
        g.DrawString(line, F(8.5f), sub, new RectangleF(x, r.Y + 30 * u, textRight - x, 18 * u), fmtL);
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

    KinetikSuite.SettingsWindow settings;

    // Settings…: this app's own pages, then the Look, Placement, General and About pages every Kinetik widget shares.
    public void ShowSettings(string page = null)
    {
        if (settings != null && !settings.IsDisposed) { settings.Activate(); if (page != null) settings.ShowPage(page); return; }
        var pages = new List<KinetikSuite.SettingsWindow.Page>
        {
            new KinetikSuite.SettingsWindow.Page { Name = "Places", Glyph = "places", Sub = "Where the forecasts are for, and how many show at once.", Build = b =>
            {
                b.Section("Places");
                var all = c.AllPlaces();
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i]; int at = i;
                    if (i == 0) { b.Buttons(p.Name, "The main place, shown in full", b.Button("Change…", () => { ChoosePlace(); b.Refresh(); })); continue; }
                    var buttons = new List<KinetikSuite.FlatButton> { b.Button("Make main", () => { MakeMain(p); b.Refresh(); }) };
                    if (at > 1) buttons.Add(b.Small("↑", () => { MoveOther(at - 1, -1); b.Refresh(); }));
                    if (at < all.Count - 1) buttons.Add(b.Small("↓", () => { MoveOther(at - 1, 1); b.Refresh(); }));
                    buttons.Add(b.Button("Remove", () => { RemoveOther(at - 1); b.Refresh(); }));
                    b.Buttons(p.Name, null, buttons.ToArray());
                }
                if (all.Count < MaxPlaces) b.Buttons("Add a place", "Up to " + MaxPlaces + " places", b.Button("Add…", () => { AddPlace(); b.Refresh(); }, true));
                b.Section("Layout");
                b.Choice("Show", null, new[] { "Just the main place", "The main place, others below", "Every place, compact" }, c.View, SetView, 230);
            } },
            new KinetikSuite.SettingsWindow.Page { Name = "Forecast", Glyph = "cloud", Sub = "What the forecast shows, its units, and how often it updates.", Build = b =>
            {
                b.Section("Show");
                b.Switch("Details", "Feels like, wind, humidity and UV", c.ShowDetails, v => { c.ShowDetails = v; Changed(); });
                b.Switch("Next hours", null, c.ShowHourly, v => { c.ShowHourly = v; Changed(); });
                b.Switch("Chance of rain", null, c.ShowRain, v => { c.ShowRain = v; Changed(); });
                b.Switch("Sunrise and sunset", null, c.ShowSun, v => { c.ShowSun = v; Changed(); });
                b.Switch("Days ahead", null, c.ShowDaily, v => { c.ShowDaily = v; Changed(); });
                b.Switch("Air quality", "US air quality index, from Open-Meteo", c.ShowAir, v => { c.ShowAir = v; if (v) FetchAir(); Changed(); });
                b.Switch("Forecast alerts", "A banner for storms, heavy rain, strong wind and extreme heat or cold in the next day. Worked out from the forecast, not official warnings", c.ShowWarnings, v => { c.ShowWarnings = v; Changed(); });
                b.Switch("Animated icons", "Rain falls, the sun turns, clouds drift", c.Animate, v => { c.Animate = v; Changed(); });
                var hours = new[] { 6, 8, 12, 24 }; var days = new[] { 3, 5, 7 };
                b.Choice("Hours ahead", "24 shows every other hour", hours.Select(v => v + " hours").ToArray(), Array.IndexOf(hours, c.Hours), i => { c.Hours = hours[i]; Changed(); }, 140);
                b.Choice("Days ahead", null, days.Select(v => v + " days").ToArray(), Array.IndexOf(days, c.Days), i => { c.Days = days[i]; Changed(); }, 140);
                b.Section("Units");
                b.Choice("Temperature", null, new[] { "°C", "°F" }, c.Fahrenheit ? 1 : 0, i => { c.Fahrenheit = i == 1; Changed(); }, 120);
                b.Choice("Big temperature", "What the large number shows", new[] { "Actual", "Feels like" }, c.FeelsHero ? 1 : 0, i => { c.FeelsHero = i == 1; Changed(); }, 140);
                b.Choice("Wind speed", null, new[] { "km/h", "mph", "m/s" }, c.Wind, i => { c.Wind = i; Changed(); }, 120);
                b.Switch("24-hour time", null, c.Use24h, v => { c.Use24h = v; Changed(); });
                b.Section("Alerts");
                b.Switch("Rain in the next hour", "A notification when rain is about to start", c.RainAlert, v => { c.RainAlert = v; Changed(); });
                b.Section("Updates");
                var every = new[] { 15, 30, 60, 120 };
                b.Choice("Update every", null, every.Select(v => v < 60 ? v + " minutes" : v / 60 + (v == 60 ? " hour" : " hours")).ToArray(), Array.IndexOf(every, c.Refresh), i =>
                {
                    int v = every[i]; c.Refresh = v; c.Save();
                    if (data != null) ScheduleFetchSeconds((int)Math.Max(5, v * 60 - (DateTime.UtcNow - OldestFetch).TotalSeconds));
                }, 140);
                b.Buttons("Forecast", "Weather data by Open-Meteo.com", b.Button("Refresh now", Fetch), b.Button("Radar map", OpenMap));
            } },
        };
        pages.AddRange(Common().ForSettings().Pages());
        settings = new KinetikSuite.SettingsWindow("Kinetik Weather", Program.Version, pages, page);
        settings.Show();
    }

    // The look, placement, startup and backup options, shared word for word with the other Kinetik apps.
    CommonMenu Common()
    {
        return new CommonMenu
        {
            Title = "Kinetik Weather", Version = Program.Version,
            OpenSettings = () => ShowSettings(),
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
        menu.Items.Add(Item("Open the radar map", false, OpenMap));
        menu.Items.Add(Item("Weather data by Open-Meteo.com", false, () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://open-meteo.com/") { UseShellExecute = true }); } catch { } }));
        menu.Items.Add(new ToolStripSeparator());

        // Places: the main one first, then the others, each with its own options; then how many to show at once
        var places = new ToolStripMenuItem("Places");
        var all = c.AllPlaces();
        var add = Item("Add a place…", false, AddPlace);
        add.Enabled = all.Count < MaxPlaces;
        places.DropDownItems.Add(add);
        places.DropDownItems.Add(new ToolStripSeparator());
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            int at = i;
            var item = new ToolStripMenuItem(p.Name + (i == 0 ? "  (main)" : "")) { Checked = i == 0 };
            if (i == 0) item.DropDownItems.Add(Item("Change this place…", false, ChoosePlace));
            else
            {
                item.DropDownItems.Add(Item("Make it the main place", false, () => MakeMain(p)));
                if (at > 1) item.DropDownItems.Add(Item("Move up", false, () => MoveOther(at - 1, -1)));
                if (at < all.Count - 1) item.DropDownItems.Add(Item("Move down", false, () => MoveOther(at - 1, 1)));
                item.DropDownItems.Add(Item("Remove", false, () => RemoveOther(at - 1)));
            }
            places.DropDownItems.Add(item);
        }
        places.DropDownItems.Add(new ToolStripSeparator());
        places.DropDownItems.Add(Item("Show just the main place", c.View == 0, () => SetView(0)));
        places.DropDownItems.Add(Item("Show the main place, and the others below it", c.View == 1, () => SetView(1)));
        places.DropDownItems.Add(Item("Show every place in a compact list", c.View == 2, () => SetView(2)));
        menu.Items.Add(places);

        var show = new ToolStripMenuItem("Show");
        show.DropDownItems.Add(Item("Details (feels like, wind, humidity, UV)", c.ShowDetails, () => { c.ShowDetails = !c.ShowDetails; Changed(); }));
        show.DropDownItems.Add(Item("Next hours", c.ShowHourly, () => { c.ShowHourly = !c.ShowHourly; Changed(); }));
        show.DropDownItems.Add(Item("Chance of rain", c.ShowRain, () => { c.ShowRain = !c.ShowRain; Changed(); }));
        show.DropDownItems.Add(Item("Sunrise and sunset", c.ShowSun, () => { c.ShowSun = !c.ShowSun; Changed(); }));
        show.DropDownItems.Add(Item("Days ahead", c.ShowDaily, () => { c.ShowDaily = !c.ShowDaily; Changed(); }));
        show.DropDownItems.Add(Item("Air quality", c.ShowAir, () => { c.ShowAir = !c.ShowAir; if (c.ShowAir) FetchAir(); Changed(); }));
        show.DropDownItems.Add(Item("Forecast alerts", c.ShowWarnings, () => { c.ShowWarnings = !c.ShowWarnings; Changed(); }));
        show.DropDownItems.Add(Item("Animated icons", c.Animate, () => { c.Animate = !c.Animate; Changed(); }));
        show.DropDownItems.Add(new ToolStripSeparator());
        foreach (var n in new[] { 6, 8, 12, 24 }) { int v = n; show.DropDownItems.Add(Item("Next " + v + " hours" + (v == 24 ? " (every other hour)" : ""), c.Hours == v, () => { c.Hours = v; Changed(); })); }
        show.DropDownItems.Add(new ToolStripSeparator());
        foreach (var n in new[] { 3, 5, 7 }) { int v = n; show.DropDownItems.Add(Item(v + " days", c.Days == v, () => { c.Days = v; Changed(); })); }
        menu.Items.Add(show);

        var units = new ToolStripMenuItem("Units");
        units.DropDownItems.Add(Item("°C", !c.Fahrenheit, () => { c.Fahrenheit = false; Changed(); }));
        units.DropDownItems.Add(Item("°F", c.Fahrenheit, () => { c.Fahrenheit = true; Changed(); }));
        units.DropDownItems.Add(Item("Feels-like temperature, big", c.FeelsHero, () => { c.FeelsHero = !c.FeelsHero; Changed(); }));
        units.DropDownItems.Add(new ToolStripSeparator());
        units.DropDownItems.Add(Item("km/h", c.Wind == 0, () => { c.Wind = 0; Changed(); }));
        units.DropDownItems.Add(Item("mph", c.Wind == 1, () => { c.Wind = 1; Changed(); }));
        units.DropDownItems.Add(Item("m/s", c.Wind == 2, () => { c.Wind = 2; Changed(); }));
        units.DropDownItems.Add(new ToolStripSeparator());
        units.DropDownItems.Add(Item("24-hour time", c.Use24h, () => { c.Use24h = !c.Use24h; Changed(); }));
        menu.Items.Add(units);

        var every = new ToolStripMenuItem("Update every");
        foreach (var m in new[] { 15, 30, 60, 120 }) { int v = m; every.DropDownItems.Add(Item(v < 60 ? v + " minutes" : v / 60 + (v == 60 ? " hour" : " hours"), c.Refresh == v, () => { c.Refresh = v; c.Save(); if (data != null) ScheduleFetchSeconds((int)Math.Max(5, v * 60 - (DateTime.UtcNow - OldestFetch).TotalSeconds)); })); }
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

    void SetView(int v) { c.View = v; Changed(); }

    // Changing the main place: the forecasts are kept by place, so swapping needs no new fetch.
    void ChoosePlace()
    {
        using (var dlg = new PlaceDialog(c.Place, "Kinetik Weather location"))
            if (dlg.ShowDialog() == DialogResult.OK && dlg.Result != null)
            {
                c.Place = dlg.Result.Name; c.Lat = dlg.Result.Lat; c.Lon = dlg.Result.Lon;
                c.SetOthers(c.OtherPlaces().Where(p => p.Key != c.Main.Key));
                PlacesChanged();
            }
    }

    void AddPlace()
    {
        if (!c.HasPlace) { ChoosePlace(); return; }
        using (var dlg = new PlaceDialog("", "Add a place to Kinetik Weather"))
            if (dlg.ShowDialog() == DialogResult.OK && dlg.Result != null)
            {
                var p = new Place { Name = dlg.Result.Name, Lat = dlg.Result.Lat, Lon = dlg.Result.Lon };
                if (c.AllPlaces().Any(o => o.Key == p.Key)) return;
                var others = c.OtherPlaces(); others.Add(p);
                c.SetOthers(others);
                if (c.View == 0) c.View = 1; // so the new place shows
                PlacesChanged();
            }
    }

    void MakeMain(Place p)
    {
        var others = c.OtherPlaces();
        int i = others.FindIndex(o => o.Key == p.Key);
        if (i < 0) return;
        others[i] = c.Main;
        c.Place = p.Name; c.Lat = p.Lat; c.Lon = p.Lon;
        c.SetOthers(others);
        PlacesChanged();
    }

    void MoveOther(int i, int by)
    {
        var others = c.OtherPlaces();
        int j = i + by;
        if (i < 0 || j < 0 || i >= others.Count || j >= others.Count) return;
        var t = others[i]; others[i] = others[j]; others[j] = t;
        c.SetOthers(others);
        PlacesChanged();
    }

    void RemoveOther(int i)
    {
        var others = c.OtherPlaces();
        if (i < 0 || i >= others.Count) return;
        others.RemoveAt(i);
        c.SetOthers(others);
        PlacesChanged();
    }

    // Saves the list, puts each kept forecast in its place's cache, and fetches any place that has none yet.
    void PlacesChanged()
    {
        c.Save();
        foreach (var p in c.AllPlaces())
            if (!got.ContainsKey(p.Key) && !Program.Demo)
            {
                string json;
                var f = Weather.LoadCache(p.Lat, p.Lon, p.Key == c.Main.Key, out json);
                if (f == null) f = Weather.LoadCache(p.Lat, p.Lon, p.Key != c.Main.Key, out json);
                if (f != null) { got[p.Key] = f; raw[p.Key] = json; }
            }
            else if (Program.Demo && !got.ContainsKey(p.Key)) got[p.Key] = Weather.Demo(got.Count);
        foreach (var p in c.AllPlaces()) errs.Remove(p.Key);
        FetchAir();
        SaveCaches();
        lastError = null;
        if (c.AllPlaces().Any(p => !got.ContainsKey(p.Key)) || Stale) Fetch();
        Render();
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

    public PlaceDialog(string current, string title)
    {

        HandleCreated += (s, e) => KinetikSuite.Look.Current.Window(Handle); // title bar in the Kinetik apps' theme
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(460, 420);
        BackColor = KinetikSuite.Look.Current.Card; ForeColor = KinetikSuite.Look.Current.Text;
        Color field = KinetikSuite.Look.Current.Hover;

        Controls.Add(new Label { Text = "Search for a town or city, or type coordinates (-37.81, 144.96):", Left = 12, Top = 12, AutoSize = true });
        search = new TextBox { Left = 12, Top = 36, Width = 350, BackColor = field, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle, Text = current };
        find = new Button { Text = "Search", Left = 372, Top = 34, Width = 76, FlatStyle = FlatStyle.Flat };
        list = new ListBox { Left = 12, Top = 68, Width = 436, Height = 230, BackColor = field, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
        status = new Label { Left = 12, Top = 302, Width = 436, Height = 20, ForeColor = KinetikSuite.Look.Current.Sub };
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

    public Color Line { get { return Term ? Color.FromArgb(110, Sub) : Light ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255); } }

    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

    // The terminal themes (the same green, amber and yellow as Kinetik's dashboard and Kinetik Screen) draw in a
    // monospace font unless a font is picked, and their divider lines glow in the theme's colour.
    public bool Term { get { return IsTerm(Name); } }
    public static bool IsTerm(string name) { return name == "Terminal" || name == "Amber terminal" || name == "Yellow terminal"; }
    static string mono;
    public static string Mono
    {
        get
        {
            if (mono == null)
            {
                mono = "Consolas";
                try { using (var f = new Font("Cascadia Mono", 10f)) if (f.Name == "Cascadia Mono") mono = "Cascadia Mono"; } catch { }
            }
            return mono;
        }
    }

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
        new Theme("OLED black", C(0, 0, 0), C(242, 242, 245), C(136, 136, 146), C(76, 194, 255), false),
        new Theme("Mocha", C(30, 30, 46), C(205, 214, 244), C(147, 153, 178), C(203, 166, 247), false),
        new Theme("Terminal", C(3, 10, 5), C(90, 255, 120), C(46, 170, 80), C(205, 255, 215), false),
        new Theme("Amber terminal", C(12, 7, 0), C(255, 182, 40), C(185, 118, 22), C(255, 232, 180), false),
        new Theme("Yellow terminal", C(0, 0, 0), C(255, 226, 0), C(200, 178, 0), C(255, 255, 190), false),
        new Theme("Light", C(246, 246, 248), C(24, 24, 28), C(96, 96, 106), C(230, 70, 70), true),
        new Theme("Paper", C(250, 246, 236), C(52, 42, 32), C(132, 116, 96), C(204, 92, 52), true),
        new Theme("Frost", C(236, 242, 250), C(20, 30, 48), C(92, 106, 132), C(40, 120, 230), true),
    };

    public static Theme Get(string name)
    {
        // "App" follows the theme and accent colour every Kinetik app shares.
        if (name == "App") { var l = KinetikSuite.Look.Current; return new Theme("App", l.Card, l.Text, l.Sub, l.Accent, l.Light); }
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
    static Color Bg { get { return KinetikSuite.Look.Current.Card; } }
    static Color Hi { get { return KinetikSuite.Look.Current.Hover; } }
    static Color Edge { get { return KinetikSuite.Look.Current.Border; } }
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
    public override Color CheckBackground { get { return KinetikSuite.Look.Current.Hover; } }
    public override Color CheckSelectedBackground { get { return KinetikSuite.Look.Current.Hover; } }
    public override Color CheckPressedBackground { get { return KinetikSuite.Look.Current.Hover; } }
}

class DarkMenu : ToolStripProfessionalRenderer
{
    public DarkMenu() : base(new DarkColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? KinetikSuite.Look.Current.Text : KinetikSuite.Look.Current.Sub;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = KinetikSuite.Look.Current.Sub;
        base.OnRenderArrow(e);
    }
}

// A grid of theme tiles, the same picker as the other Kinetik apps.
class ThemePicker : Form
{
    public static Color Page { get { return KinetikSuite.Look.Current.Bg; } }
    public static Color Card { get { return KinetikSuite.Look.Current.Card; } }
    public static Color CardHi { get { return KinetikSuite.Look.Current.Hover; } }
    public static Color Accent { get { return KinetikSuite.Look.Current.Accent; } }
    public static Color TextCol { get { return KinetikSuite.Look.Current.Text; } }
    public static Color SubCol { get { return KinetikSuite.Look.Current.Sub; } }

    public ThemePicker(string current, Action<string> chosen)
    {

        HandleCreated += (s, e) => KinetikSuite.Look.Current.Window(Handle); // title bar in the Kinetik apps' theme
        Text = Backup.AppTitle + " theme";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BackColor = Page;
        float k = DeviceDpi / 96f;
        var flow = new FlowLayoutPanel { Left = (int)(16 * k), Top = (int)(16 * k), Width = (int)(3 * 118 * k), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size((int)(3 * 118 * k), 0), WrapContents = true, BackColor = Page };
        foreach (var name in new[] { "Auto", "App" }.Concat(Theme.All.Select(t => t.Name)))
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
            TextRenderer.DrawText(g, ThemeName == "Auto" ? "Match Windows" : ThemeName == "App" ? "Match app theme" : ThemeName, f, new Rectangle(0, Height - I(26), Width, I(20)), selected ? ThemePicker.TextCol : ThemePicker.SubCol,
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
    public Action LookChanged;                                      // optional: after the App theme changes
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
        if (OpenTheme != null) items.Add(Item("Widget theme…", false, OpenTheme));
        items.Add(KinetikSuite.Look.MenuItem(LookChanged)); // the theme of every Kinetik app's windows and menus

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

    // The same options as the shared pages of a settings window (Look, Placement, General, About).
    public KinetikSuite.SettingsWindow.Common ForSettings()
    {
        return new KinetikSuite.SettingsWindow.Common
        {
            Title = Title, Version = Version, OpenTheme = OpenTheme,
            Scale = Scale, SetScale = SetScale, Scales = Scales, Widths = Widths, CardWidth = CardWidth, SetCardWidth = SetCardWidth,
            FontName = FontName, SetFontName = SetFontName, Fonts = Fonts.Where(HasFont).ToArray(),
            Opacity = Opacity, SetOpacity = SetOpacity, Opacities = Opacities,
            Layer = Layer, SetLayer = SetLayer, Locked = Locked, SetLocked = SetLocked, LockText = LockText,
            ClickThrough = ClickThrough, SetClickThrough = SetClickThrough, Snap = Snap, SetSnap = SetSnap,
            RunAtStartup = RunAtStartup, SetRunAtStartup = SetRunAtStartup, BackUp = BackUp, Restore = Restore, LastBackup = LastBackup,
        };
    }

    public void AddAll(ToolStripItemCollection items) { AddLook(items); AddEnd(items); }
}
#pragma warning restore 0649
