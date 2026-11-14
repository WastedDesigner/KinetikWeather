using System;
using System.Collections.Generic;
using System.Linq;

// Compile alongside the app with /main:WeatherFeatureTests (KinetikWeather.cs, SuiteSupport.cs, JsonShim.cs). Nothing is fetched or saved.
static class WeatherFeatureTests
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    static Forecast Blank(DateTime now)
    {
        var f = new Forecast { Offset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalSeconds, Fetched = DateTime.UtcNow };
        var start = f.PlaceNow.Date;
        for (int i = 0; i < 48; i++) f.Hours.Add(new Hour { T = start.AddHours(i), Temp = 20, Pop = 0, Code = 1, Rain = 0, Gust = 20 });
        for (int i = 0; i < 3; i++) f.Days.Add(new DayF { Date = start.AddDays(i), Max = 25, Min = 14 });
        return f;
    }

    static void Main()
    {
        // the new fields come out of Open-Meteo's JSON
        var json = "{\"utc_offset_seconds\":0,\"current\":{\"temperature_2m\":18.0,\"apparent_temperature\":16.5,\"relative_humidity_2m\":60,\"weather_code\":3,\"is_day\":1,\"wind_speed_10m\":10,\"wind_direction_10m\":200,\"precipitation\":0.3},"
            + "\"hourly\":{\"time\":[\"2026-11-14T09:00\",\"2026-11-14T10:00\"],\"temperature_2m\":[18,19],\"precipitation_probability\":[10,20],\"weather_code\":[3,95],\"is_day\":[1,1],\"precipitation\":[0.0,5.5],\"wind_gusts_10m\":[30,72.5]},"
            + "\"minutely_15\":{\"time\":[\"2026-11-14T09:15\",\"2026-11-14T09:30\"],\"precipitation\":[0.0,0.4]},"
            + "\"daily\":{\"time\":[\"2026-11-14\"],\"weather_code\":[3],\"temperature_2m_max\":[22],\"temperature_2m_min\":[12],\"precipitation_probability_max\":[40],\"sunrise\":[\"2026-11-14T06:00\"],\"sunset\":[\"2026-11-14T19:30\"],\"uv_index_max\":[8]}}";
        var parsed = Weather.Parse(json);
        Check(parsed.RainNow == 0.3 && parsed.Hours[1].Gust == 72.5 && parsed.Hours[1].Rain == 5.5 && parsed.Mins.Count == 2 && parsed.Mins[1].Mm == 0.4, "Rain, gusts and 15-minute rain are read");
        var old = Weather.Parse(json.Replace(",\"precipitation\":0.3", "").Replace(",\"precipitation\":[0.0,5.5],\"wind_gusts_10m\":[30,72.5]", "").Replace("\"minutely_15\":{\"time\":[\"2026-11-14T09:15\",\"2026-11-14T09:30\"],\"precipitation\":[0.0,0.4]},", ""));
        Check(double.IsNaN(old.RainNow) && double.IsNaN(old.Hours[0].Gust) && old.Mins.Count == 0, "A forecast saved by an older version still loads");

        // alerts
        var f = Blank(DateTime.Now);
        Check(Weather.Warnings(f).Count == 0, "A calm forecast has no alerts");
        var soon = f.Hours.First(h => h.T.AddHours(1) > f.PlaceNow);
        soon.Code = 95; soon.Gust = 70;
        var w = Weather.Warnings(f);
        Check(w.Count == 2 && w[0].Kind == "storm" && w[0].Level == 1 && w[1].Kind == "wind" && w[1].Level == 1, "Thunderstorm and strong wind are noted, soonest and worst first");
        soon.Code = 99; soon.Gust = 95; soon.Rain = 12;
        w = Weather.Warnings(f);
        Check(w.All(x => x.Level == 2) && w.Count == 3, "Hail, damaging gusts and torrential rain are severe");
        var later = f.Hours.First(h => h.T >= f.PlaceNow.AddHours(30));
        later.Code = 99;
        Check(Weather.Warnings(f).Count == 3, "Nothing beyond 24 hours is announced");
        soon.Code = 1; soon.Gust = 20; soon.Rain = 0;
        f.Days[1].Max = 43; f.Days[2].Max = 45;
        w = Weather.Warnings(f);
        Check(w.Count == 1 && w[0].Kind == "heat" && w[0].Level == 2 && w[0].Value == 43, "Extreme heat counts today and tomorrow only");
        f.Days[1].Max = 25; f.Days[0].Min = -12;
        Check(Weather.Warnings(f).Single().Kind == "cold", "Hard frost is noted");

        // rain in the next hour
        var r = Blank(DateTime.Now);
        var now = r.PlaceNow;
        DateTime start;
        r.Mins = new List<Slot> { new Slot { T = now.AddMinutes(10), Mm = 0 }, new Slot { T = now.AddMinutes(25), Mm = 0 }, new Slot { T = now.AddMinutes(40), Mm = 0.6 }, new Slot { T = now.AddMinutes(55), Mm = 1 } };
        int mins = Weather.RainSoon(r, out start);
        Check(mins >= 24 && mins <= 26 && Math.Abs((start - now.AddMinutes(25)).TotalSeconds) < 1, "Rain due in 25 minutes is announced as such");
        r.Mins[0].Mm = 0.5;
        Check(Weather.RainSoon(r, out start) == -1, "No alert when it is raining now");
        r.Mins[0].Mm = 0; r.Mins[2].Mm = 0.05; r.Mins[3].Mm = 0.05;
        Check(Weather.RainSoon(r, out start) == -1, "A drizzle under 0.1 mm is ignored");
        r.Mins = new List<Slot> { new Slot { T = now.AddMinutes(10), Mm = 0 }, new Slot { T = now.AddMinutes(100), Mm = 2 } };
        Check(Weather.RainSoon(r, out start) == -1, "Rain more than an hour away is not announced yet");
        r.Mins = new List<Slot> { new Slot { T = now.AddMinutes(14), Mm = 1 } };
        r.RainNow = 0.4; r.Fetched = DateTime.UtcNow;
        Check(Weather.RainSoon(r, out start) == -1, "Rain at the last update means no alert");

        // air quality
        var air = Weather.ParseAir("{\"current\":{\"us_aqi\":42,\"pm2_5\":7.1}}");
        Check(air.Aqi == 42 && Weather.AqiWord(42) == "Good" && Weather.AqiWord(101) == "Sens." && Weather.AqiWord(350) == "Hazard", "Air quality index and its words");
        try { Weather.ParseAir("{\"current\":{\"us_aqi\":null}}"); Check(false, "Missing index is refused"); } catch (FormatException) { }

        Console.WriteLine("PASS: new forecast fields, alerts, rain in the next hour and air quality.");
    }
}
