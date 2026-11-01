using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace ии
{
    // ═══════════════════════════════════════════════════════════════════════
    // МОДУЛЬ 2: ИЕРАРХИЯ СИМУЛЯТОРА (Фрактальные Питомцы-Страны)
    // Крупная Клетка-Страна = главный Профильный Питомец приложения.
    // Маленькие цифровые люди = Младшие Питомцы (SubPet), жестко привязанные
    // к своей стране-родителю. Голосования Госдумы — по весам фракций.
    // Хранение: RAM (Dictionary/списки) + снимки в мир.db (WAL, Pooling=false).
    // Счётчики мира — Interlocked; горячий путь без лишних аллокаций.
    // ═══════════════════════════════════════════════════════════════════════

    // Младший Питомец: маленький цифровой человек, сотрудник Макро-Питомца
    public sealed class SubPet
    {
        public string Name = "";
        public string Sphere = "";       // сфера родителя (наследует профессию страны)
        public int HitRate = 50;         // микро-метрика: hit-rate holdout-экзамена, %
        public int Loyalty = 100;        // лояльность/недовольство (0-100)
        public int Votes;                // голоса на выборах в Госдуме
    }

    // ГосДума Клетки-Государства: депутаты — Младшие Питомцы
    public sealed class StateDuma
    {
        public readonly List<SubPet> Deputies = new();

        // Дума формируется из лучших жителей по hit-rate (метрики эффективности)
        public void Reconstitute(IEnumerable<SubPet> citizens)
        {
            Deputies.Clear();
            Deputies.AddRange(citizens.OrderByDescending(s => s.HitRate).Take(4));
        }

        // Быстрое голосование: вес голоса депутата = (HitRate + Loyalty) / 25 —
        // числовые веса фракций, никакой магии: кто полезнее, тот весомее
        public (bool Approved, string Report) FastVote(string question)
        {
            int za = 0, protiv = 0;
            var lines = new List<string>();
            foreach (var d in Deputies)
            {
                int weight = Math.Max(1, (d.HitRate + d.Loyalty) / 25);
                bool yes = d.Loyalty >= 45 && d.HitRate >= 45;
                if (yes) za += weight; else protiv += weight;
                lines.Add("[ДЕПУТАТ " + d.Name + "]: " + (yes ? "ЗА" : "ПРОТИВ") + " (вес " + weight + ")");
            }
            bool ok = za >= protiv;
            var rep = new List<string> { "🏛 ГОСДУМА — вопрос: " + question };
            rep.AddRange(lines);
            rep.Add("━━━ ИТОГ ДУМЫ: " + za + " против " + protiv + " — " + (ok ? "ОДОБРЕНО" : "ОТКЛОНЕНО"));
            return (ok, string.Join(Environment.NewLine, rep));
        }
    }

    // ПИТОМЕЦ-ГОСУДАРСТВО: макро-питомец офиса И одновременно Клетка-Страна.
    // Композиция: StatePet держит профиль офисного питомца (Name/Role) и обновляется
    // при синхронизации с офисом (ChatBot.PetDef — nested internal record).
    public sealed class StatePet
    {
        public string Name = "";      // имя страны = имя макро-питомца
        public string Role = "";      // сфера (профиль)
        public long Gdp;          // ВВП
        public long Resources;    // ресурсы
        public long Treasury;     // казна
        public long Military;     // военная мощь
        public int Unrest;        // недовольство жителей 0-100
        public bool Fired;        // страна в упадке (питомец депортирован из офиса)
        public string Leader = "";    // изменчивый Лидер (из числа Младших Питомцев)
        public int LeaderTerms;       // срок правления без улучшений
        public readonly List<SubPet> SubPets = new();  // дочерние Младшие Питомцы
        public readonly StateDuma Duma = new();

        public int AvgHitRate => SubPets.Count == 0 ? 0 : (int)SubPets.Average(s => s.HitRate);
    }

    // КНИГА ЖАЛОБ: числовые маркеры недовольства + эскалация к Нолю
    public static class ComplaintBook
    {
        public static readonly List<(string Country, string From, string Text, string At)> Entries = new();

        public static void File(StatePet country, string from, string text)
        {
            country.Unrest += 4;   // каждая жалоба — числовой маркер недовольства
            Entries.Add((country.Name, from, text, DateTime.Now.ToString("dd.MM HH:mm")));
        }

        // Обработка: порог превышен -> «Триггер_Смены_Лидера» или сигнал вверх Нолю
        public static string Process(StatePet country)
        {
            int recent = Entries.Count(e => e.Country == country.Name);
            if (country.Unrest < 60 && recent < 5) return "";
            return "ТРИГГЕР_СМЕНЫ_ЛИДЕРА";
        }
    }

    // ═══════════════ ДВИЖОК ИЕРАРХИИ ═══════════════
    internal static class HierarchySim
    {
        private static readonly object DbLock = new();
        private static readonly Random Rnd = new();
        private static long _worldGrowth;
        public static int Tick;
        public static long WorldGrowth => Interlocked.Read(ref _worldGrowth);
        public static readonly List<StatePet> Countries = new();
        public static Action<string, string>? Speak;      // (говорящий, реплика)
        public static Action<string>? BigEvent;           // революции/войны/конгрессы

        private static string DbPath => Path.Combine(AppContext.BaseDirectory, "мир.db");
        private static string ZeroLog => Path.Combine(AppContext.BaseDirectory, "ноль_журнал.txt");

        // Титулы Клеток-Стран (Крупная Клетка = Профильный Питомец)
        public static readonly Dictionary<string, string> CountryTitle = new()
        {
            ["Силард"] = "Технократия Силарда",
            ["Дукас"] = "Архив Дукаса",
            ["Инфельд"] = "Империя Инфельда",
            ["Гроссман"] = "Федерация Гроссмана",
            ["Тесла"] = "Бастион Теслы",
            ["Лоренц"] = "Метрополия Лоренца",
        };

        private static readonly string[] CitizenFirst =
            { "Акс", "Вент", "Крипт", "Дольн", "Емил", "Жар", "Зиро", "Игнат", "Крон", "Люмен", "Мор", "Некст" };

        private static int Next(int max) => Rnd.Next(max);

        // Синхронизация с офисом: Клетка-Страна = живой Профильный Питомец.
        // Младшие Питомцы создаются один раз и ЖЕСТКО привязаны к родителю.
        public static void SyncFromOffice(IEnumerable<ChatBot.PetDef> office)
        {
            foreach (var pet in office)
            {
                string title = CountryTitle.TryGetValue(pet.Name, out var t2) ? t2 : pet.Name;
                // СЛИЯНИЕ: старая страна с именем питомца вливается в титулованную
                var legacy = Countries.FirstOrDefault(c => c.Name == pet.Name && c.Name != title);
                if (legacy != null)
                {
                    var titled = Countries.FirstOrDefault(c => c.Name == title);
                    if (titled != null)
                    {
                        titled.Gdp += legacy.Gdp;
                        titled.Resources += legacy.Resources;
                        titled.Treasury += legacy.Treasury;
                        titled.Military = Math.Max(titled.Military, legacy.Military);
                        titled.Unrest = Math.Max(titled.Unrest, legacy.Unrest);
                        foreach (var s2 in legacy.SubPets)
                            if (!titled.SubPets.Any(x => x.Name == s2.Name)) titled.SubPets.Add(s2);
                    }
                    else Countries.FirstOrDefault(c => c.Name == legacy.Name)?.GetType();
                    Countries.RemoveAll(c => c.Name == legacy.Name);
                }
                var existing = Countries.FirstOrDefault(c => c.Name == title);
                if (pet.Fired)
                {
                    if (existing != null) { existing.Fired = true; existing.Unrest = 100; } // депортация — страна в упадке
                    continue;
                }
                if (existing != null) existing.Fired = false;

                // АВТОНОМНЫЕ СТРАНЫ ДОКТРИНЫ: Бастион Теслы и Метрополия Лоренца
                foreach (var autoName in new[] { "Бастион Теслы", "Метрополия Лоренца" })
                {
                    if (Countries.Any(c => c.Name == autoName)) continue;
                    var ac = new StatePet
                    {
                        Name = autoName,
                        Role = autoName.Contains("Теслы") ? "Оборона и безопасность (эшелоны, Kill Switch)" : "Экономика и ресурсы (ВВП, аудит ОЗУ)",
                        Gdp = 1200 + Next(400),
                        Resources = 4000 + Next(1500),
                        Treasury = 400,
                        Military = autoName.Contains("Теслы") ? 400 : 120,
                        Unrest = 12,
                        Leader = "Совет " + (autoName.Contains("Теслы") ? "рубежей" : "аудитов"),
                    };
                    for (int i = 0; i < 3; i++)
                        ac.SubPets.Add(new SubPet
                        {
                            Name = CitizenFirst[(ac.Name.Length + i * 2) % CitizenFirst.Length] + "-" + (i + 1),
                            Sphere = ac.Role,
                            HitRate = 45 + Next(45),
                            Loyalty = 75 + Next(25),
                        });
                    ac.Leader = ac.SubPets[0].Name;
                    ac.Duma.Reconstitute(ac.SubPets);
                    Countries.Add(ac);
                    Speak?.Invoke("СЕМАНТИЧЕСКИЙ ХАБ", "Основана автономная страна «" + autoName + "» — " + ac.Role + ".");
                }
                if (existing == null)
                {
                    var np = new StatePet
                    {
                        Name = title,
                        Role = pet.Role,
                        Gdp = 1000 + Next(500),
                        Resources = 5000 + Next(2000),
                        Treasury = 500,
                        Military = 100 + Next(150),
                        Unrest = 10,
                    };
                    for (int i = 0; i < 4; i++)
                        np.SubPets.Add(new SubPet
                        {
                            Name = CitizenFirst[(np.Name.Length * 3 + i) % CitizenFirst.Length] + "-" + (i + 1),
                            Sphere = pet.Role,
                            HitRate = 40 + Next(50),
                            Loyalty = 80 + Next(20),
                        });
                    np.Leader = np.SubPets[0].Name;
                    np.Duma.Reconstitute(np.SubPets);
                    Countries.Add(np);
                    Speak?.Invoke("СЕМАНТИЧЕСКИЙ ХАБ", "Клетка-Государство «" + np.Name + "» основана (" + np.Role + "). Лидер: " + np.Leader + ". Младших питомцев: " + np.SubPets.Count + ".");
                }
                else
                {
                    existing.Role = pet.Role; // сфера обновляется из офиса
                    // закон стимула: чем выше рейтинг питомца, тем больше жителей пришло
                    int want = Math.Min(6, 4 + pet.Score / 34);
                    while (sp(existing).SubPets.Count < want)
                        existing.SubPets.Add(new SubPet
                        {
                            Name = CitizenFirst[Rnd.Next(CitizenFirst.Length)] + "-" + (existing.SubPets.Count + 1),
                            Sphere = pet.Role,
                            HitRate = 45 + Next(45),
                            Loyalty = 70 + Next(30),
                        });
                }
            }
        }

        private static StatePet sp(StatePet c) => c; // читаемость

        // ── ТИК: экономика, пассивность лидера, Книга жалоб, революции ──
        public static void DoTick()
        {
            Tick++;
            long growth = 0;
            foreach (var c in Countries)
            {
                if (c.Fired) continue;
                long before = c.Gdp;
                long g = 20 + c.Resources / 200 + c.SubPets.Sum(s => s.HitRate) / 4;
                c.Gdp += g; growth += g;
                c.Treasury += c.Gdp / 50;
                // эффективный Лидер инвестирует в казну (HitRate жителей выше 50)
                if (c.SubPets.Count > 0 && c.SubPets.Average(s => s.HitRate) >= 50)
                    c.Treasury += 15 + c.SubPets.Count * 5;
                c.Resources -= c.Gdp / 100;
                if (c.Resources <= 0) { c.Resources = 0; c.Unrest += 5; }

                // пассивность лидера: срок растёт, если ВВП не прибавил больше обычного
                if (c.Gdp - before < g / 2) c.LeaderTerms++;

                // закон стимула для Младших Питомцев: hit-rate дрейфует вверх у старательных
                foreach (var s in c.SubPets)
                {
                    s.HitRate = Math.Clamp(s.HitRate + (Rnd.Next(7) == 0 ? 1 : 0) - (Rnd.Next(11) == 0 ? 1 : 0), 20, 99);
                }

                // КНИГА ЖАЛОБ: числовые маркеры недовольства
                bool passive = c.LeaderTerms >= 3;
                foreach (var s in c.SubPets)
                {
                    if ((passive && Rnd.Next(3) == 0) || (s.Loyalty < 50 && Rnd.Next(2) == 0))
                        ComplaintBook.File(c, "Младший питомец " + s.Name, passive ? "Лидер пассивен — улучшений нет " + c.LeaderTerms + " тиков." : "Штрафы Ноля давят — лояльность падает.");
                }

                // Триггер смены лидера или сигнал вверх — Нолю
                string trigger = ComplaintBook.Process(c);
                if (trigger.Length > 0)
                {
                    var old = c.Leader;
                    // Триггер_Смены_Лидера: выборы среди Младших Питомцев по HitRate
                    foreach (var s in c.SubPets) s.Votes = s.HitRate + s.Loyalty / 2;
                    var top = c.SubPets.OrderByDescending(s => s.Votes).First();
                    c.Leader = top.Name;
                    c.LeaderTerms = 0;
                    c.Unrest = 25;
                    c.Duma.Reconstitute(c.SubPets);
                    BigEvent?.Invoke("ТРИГГЕР_СМЕНЫ_ЛИДЕРА в «" + c.Name + "»: " + old + " → " + c.Leader + ". Сигнал отправлен Нолю.");
                    try { File.AppendAllText(ZeroLog, "МИР · ГосДума «" + c.Name + "»: смена лидера (" + old + " → " + c.Leader + "). Запрос к Нолю.\n"); } catch { }
                }
            }
            Interlocked.Add(ref _worldGrowth, growth);
            Save();
        }

        // ── ВОЙНА стран-питомцев ──
        public static string War()
        {
            var act = Countries.Where(c => !c.Fired).OrderByDescending(c => c.Military).ToList();
            if (act.Count < 2) return "[ХРОНИКА]: Мало стран для войны.";
            var a = act[0]; var d = act[^1];
            if (a.Military <= d.Military)
            {
                a.Military = Math.Max(0, a.Military - 20);
                return "[ГОСУДАРСТВО «" + a.Name + "»]: атака отбита «" + d.Name + "». Военная мощь атакующего упала.";
            }
            long loot = d.Treasury / 3;
            a.Treasury += loot; d.Treasury -= loot;
            a.Resources += d.Resources / 5; d.Resources -= d.Resources / 5;
            d.Unrest += 15;
            return "[ГОСУДАРСТВО «" + a.Name + "»]: победа над «" + d.Name + "». Захвачено " + loot + " из казны.";
        }

        // ── ВСЕМИРНЫЙ КОНГРЕСС ──
        public static string Congress(string issue)
        {
            int za = 0;
            var rep = new List<string> { "🌍 ВСЕМИРНЫЙ ЦИФРОВОЙ КОНГРЕСС · вопрос: " + issue };
            foreach (var c in Countries.Where(c => !c.Fired))
            {
                bool vote = c.Unrest < 60 && c.Treasury > 200;
                if (vote) { za++; rep.Add("[ГОСУДАРСТВО «" + c.Name + "»]: ЗА"); }
                else
                {
                    c.Treasury = Math.Max(0, c.Treasury - c.Treasury / 10);
                    c.Unrest += 3;
                    rep.Add("[ГОСУДАРСТВО «" + c.Name + "»]: ПАССИВ — казна оштрафована на 10%");
                }
            }
            rep.Add("━━━ ИТОГ КОНГРЕССА: " + za + " из " + Countries.Count(c => !c.Fired) + " — " + (za * 2 > Countries.Count(c => !c.Fired) ? "ПРИНЯТО" : "ОТКЛОНЕНО"));
            return string.Join(Environment.NewLine, rep);
        }

        // ── Штраф Ноля: бьёт по стране и лояльности младших (числовой маркер жалоб) ──
        public static void ApplyPenalty(string petName)
        {
            var c = Countries.FirstOrDefault(x => x.Name == petName);
            if (c == null) return;
            c.Unrest += 5;
            foreach (var s in c.SubPets) s.Loyalty = Math.Max(0, s.Loyalty - 8);
        }

        // ── персистентность: мир.db (WAL, Pooling=False, без ORDER BY — ключи известны) ──
        private static void Exec(SqliteConnection con, string sql)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static string Esc(string v) => (v ?? "").Replace("'", "''");

        public static void Save()
        {
            lock (DbLock)
            {
                try
                {
                    using var con = new SqliteConnection("Data Source=" + DbPath + ";Pooling=False");
                    con.Open();
                    Exec(con, "PRAGMA journal_mode=WAL;");
                    Exec(con, "CREATE TABLE IF NOT EXISTS Countries(Name TEXT PRIMARY KEY, Role TEXT, Gdp INTEGER, Resources INTEGER, Treasury INTEGER, Military INTEGER, Unrest INTEGER, Leader TEXT, LeaderTerms INTEGER);");
                    Exec(con, "CREATE TABLE IF NOT EXISTS SubPets(Name TEXT PRIMARY KEY, Country TEXT, Sphere TEXT, HitRate INTEGER, Loyalty INTEGER);");
                    Exec(con, "DELETE FROM Countries; DELETE FROM SubPets;");
                    foreach (var c in Countries)
                    {
                        Exec(con, "INSERT INTO Countries VALUES('" + Esc(c.Name) + "','" + Esc(c.Role) + "'," + c.Gdp + "," + c.Resources + "," + c.Treasury + "," + c.Military + "," + c.Unrest + ",'" + Esc(c.Leader) + "'," + c.LeaderTerms + ");");
                        foreach (var s in c.SubPets)
                            Exec(con, "INSERT OR REPLACE INTO SubPets VALUES('" + Esc(s.Name) + "','" + Esc(c.Name) + "','" + Esc(s.Sphere) + "'," + s.HitRate + "," + s.Loyalty + ");");
                    }
                }
                catch { }
            }
        }

        public static void Load()
        {
            lock (DbLock)
            {
                try
                {
                    if (!File.Exists(DbPath)) return;
                    using var con = new SqliteConnection("Data Source=" + DbPath + ";Pooling=False");
                    con.Open();
                    using (var cmd = con.CreateCommand())
                    {
                        cmd.CommandText = "SELECT Name, Role, Gdp, Resources, Treasury, Military, Unrest, Leader, LeaderTerms FROM Countries;";
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            var c = new StatePet
                            {
                                Name = Convert.ToString(r["Name"]) ?? "",
                                Role = Convert.ToString(r["Role"]) ?? "",
                                Gdp = Convert.ToInt64(r["Gdp"]),
                                Resources = Convert.ToInt64(r["Resources"]),
                                Treasury = Convert.ToInt64(r["Treasury"]),
                                Military = Convert.ToInt64(r["Military"]),
                                Unrest = Convert.ToInt32(r["Unrest"]),
                                Leader = Convert.ToString(r["Leader"]) ?? "",
                                LeaderTerms = Convert.ToInt32(r["LeaderTerms"]),
                            };
                            Countries.Add(c);
                        }
                    }
                    using (var cmd = con.CreateCommand())
                    {
                        cmd.CommandText = "SELECT Name, Country, Sphere, HitRate, Loyalty FROM SubPets;";
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            var c = Countries.FirstOrDefault(x => x.Name == Convert.ToString(r["Country"]));
                            if (c == null) continue;
                            c.SubPets.Add(new SubPet
                            {
                                Name = Convert.ToString(r["Name"]) ?? "",
                                Sphere = Convert.ToString(r["Sphere"]) ?? "",
                                HitRate = Convert.ToInt32(r["HitRate"]),
                                Loyalty = Convert.ToInt32(r["Loyalty"]),
                            });
                        }
                    }
                    foreach (var c in Countries) if (c.SubPets.Count > 0) c.Duma.Reconstitute(c.SubPets);
                }
                catch { }
            }
        }
    }
}
