using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using System.Reflection;

namespace ии
{
    // Восстановленные члены ChatBot из предыдущей сборки (частичный класс).
    internal sealed partial class ChatBot
    {

        private void Add(string q, string a, bool learned = false)
        {
            _knowledge.Add(new MemoryEntry
            {
                Question = q,
                Answer = a,
                Learned = learned
            });
        }

        private string AddSummary(string text)
        {
            // Готовые связные объяснения не трогаем — деление по первой точке ломает их структуру
            if (text.StartsWith("Объясняю") || text.StartsWith("Про «"))
            {
                return text;
            }
            if (text.Length < 350)
            {
                return text;
            }
            int num = text.IndexOf('.');
            if (num < 20 || num > 180)
            {
                return text;
            }
            string text2 = text.Substring(0, num + 1);
            string text3 = text.Substring(num + 1).Trim();
            return "Коротко: " + text2 + "\n\nПодробнее: " + text3;
        }

        private static readonly Dictionary<string, string[]> Ambiguous = new Dictionary<string, string[]>
        {
            ["лук"] = new string[2] { "растение (овощ)", "оружие для стрельбы" },
            ["ключ"] = new string[3] { "от двери", "гаечный", "родник" },
            ["мир"] = new string[2] { "всё вокруг", "отсутствие войны" },
            ["язык"] = new string[2] { "орган во рту", "речь народа" },
            ["машина"] = new string[3] { "автомобиль", "стиральная", "механизм" },
            ["мышь"] = new string[2] { "животное", "устройство компьютера" },
            ["ручка"] = new string[2] { "для письма", "дверная" },
            ["коса"] = new string[3] { "причёска", "для травы", "песчаная" },
            ["кошка"] = new string[2] { "животное", "якорь" },
            ["перо"] = new string[2] { "птичье", "для письма" }
        };

        private void BuildBrainIndex()
        {
            foreach (KeyValuePair<string, string> item in _math)
            {
                _brain.Add("математика:" + item.Key, item.Key + " — " + item.Value);
            }
            foreach (KeyValuePair<string, string> ru in _rus)
            {
                _brain.Add("русский:" + ru.Key, ru.Key + " — " + ru.Value);
            }
            foreach (MemoryEntry item2 in _knowledge)
            {
                _brain.Add("разговор:" + item2.Question, item2.Question + " — " + item2.Answer);
            }
            for (int i = Math.Max(0, _history.Count - 2000); i < _history.Count; i++)
            {
                _brain.Add("история:" + _history[i].User, _history[i].User + " — " + _history[i].Bot);
            }
        }

        private static string Clean(string s)
        {
            return s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        private static string CleanHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return "";
            }
            return Regex.Replace(Regex.Replace(html, "<[^>]+>", "").Replace("&nbsp;", " ").Replace("&amp;", "&")
                .Replace("&quot;", "\"")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&#39;", "'"), "\\s+", " ").Trim();
        }

        private string? ClosestWord(string input)
        {
            string? best = null;
            int bestDist = int.MaxValue;
            int num = ((input.Length <= 5) ? 1 : 2);
            // Словари читаем снапшотом под замком: фоновое обучение пишет в них параллельно
            List<string> wordKeys;
            List<string> mathKeys;
            List<string> rusKeys;
            lock (_dictLock)
            {
                wordKeys = _words.Keys.ToList();
                mathKeys = _math.Keys.ToList();
                rusKeys = _rus.Keys.ToList();
            }
            foreach (string key in wordKeys)
            {
                Check(key);
            }
            foreach (string key2 in mathKeys)
            {
                Check(key2);
            }
            foreach (string key3 in rusKeys)
            {
                Check(key3);
            }
            if (best != null && bestDist <= num && bestDist > 0)
            {
                return best;
            }
            return null;
            void Check(string candidate)
            {
                if (candidate.Length != 0 && input.Length != 0 && char.ToLowerInvariant(candidate[0]) == char.ToLowerInvariant(input[0]))
                {
                    int num2 = Levenshtein(input, candidate);
                    if (num2 < bestDist)
                    {
                        bestDist = num2;
                        best = candidate;
                    }
                }
            }
        }

        private string ComposeDetailedAnswer(string input)
        {
            // Вместо перечисления «• слово — определение» собираем связное объяснение
            return ExplainFromKnowledge(input);
        }

        // Собирает из найденных в базе знаний связное объяснение вопроса:
        // точное совпадение темы становится «якорем» (его вступление = определение),
        // связанные статьи добавляют детали; мусор с латиницей отбрасывается
        private string ExplainFromKnowledge(string input)
        {
            string topic = input.Trim().Trim('.', '!', '?', '«', '»', ',', ' ').ToLowerInvariant();
            List<string> qWords = TfIdfStemmer.Tokenize(topic);
            if (qWords.Count == 0)
            {
                return "";
            }

            // 1. Собираем кандидатов и выбираем «якорь» — статью точно про эту тему
            string? anchorTitle = null;
            string? anchorText = null;
            var related = new List<(string title, string text, double score)>();
            var candidates = new List<(string title, string text, double score)>();
            lock (_dictLock)
            {
                foreach (KeyValuePair<string, string> kv in _words)
                {
                    string titleNorm = kv.Key.ToLowerInvariant();
                    // Мусорные записи с хэш-мусором в начале названия (ген-базы) — мимо
                    if (Regex.IsMatch(titleNorm, "^[0-9a-f]{6,}")) continue;
                    List<string> tWords = TfIdfStemmer.Tokenize(kv.Key);
                    double score = 0;
                    foreach (string qw in qWords)
                    {
                        foreach (string tw in tWords)
                        {
                            if (tw == qw) score += 3;
                            else if (tw.StartsWith(qw) || qw.StartsWith(tw)) score += 1.5;
                        }
                    }
                    if (titleNorm == topic) score += 10;                                   // точное название
                    else if (titleNorm.StartsWith(topic) || topic.StartsWith(titleNorm)) score += 5; // частичное
                    else if (tWords.Any(tw => tw.Length >= 5 && topic.Contains(tw))) score += 4;     // слово темы в названии
                    // (порог 5 символов отсекает ложные якоря вида «hell» ⊂ «hello», из-за которых
                    //  бот отвечал статьёй про альбом Destruction на приветствие)
                    if (score >= 3) candidates.Add((kv.Key, Regex.Replace(kv.Value, "[0-9a-f]{20,}\\s*-?\\s*", " "), score));
                }
            }
            if (candidates.Count == 0)
            {
                return "";
            }
            // Якорь: точное совпадение, иначе самый сильный кандидат (при равенстве — более обстоятельная статья).
            // Проверяем адекватность пары «заголовок ↔ текст»: в базе бывают криво выученные записи
            // (заголовок про Эйнштейна, текст про Каштанову) — их пропускаем.
            // Отсеваем и мусор: заголовки-обрезки («тейн» для «эйнштейн») и заголовки-новости (>5 слов).
            foreach (var c in candidates.OrderByDescending(c => c.score).ThenByDescending(c => c.text.Length))
            {
                bool exact = c.title.ToLowerInvariant() == topic;
                // Якорь обязан иметь хотя бы одно общее слово с запросом — иначе
                // «hello what can you do» цеплялся за статью «All hell breaks loose»
                if (!exact && !TfIdfStemmer.Tokenize(c.title).Any(tw => qWords.Contains(tw))) continue;
                if (!exact && c.title.Length < topic.Length * 0.6) continue;           // заголовок-обрезка
                if (!exact && c.title.Split(' ').Length > 5) continue;                  // заголовок-новость
                string probe = c.text.Length > 500 ? c.text.Substring(0, 500) : c.text;
                List<string> titleWords = TfIdfStemmer.Tokenize(c.title);
                List<string> probeWords = TfIdfStemmer.Tokenize(probe);
                bool coherent = titleWords.Count == 0 || titleWords.Any(tw => probeWords.Any(pw => pw == tw || pw.StartsWith(tw) || tw.StartsWith(pw)));
                if (!coherent) continue;                                                // заголовок и текст не про одно
                anchorTitle = c.title;
                anchorText = c.text;
                break;
            }
            if (anchorTitle == null || anchorText == null)
            {
                return "";
            }
            foreach (var c in candidates.OrderByDescending(c => c.score).ThenByDescending(c => c.text.Length))
            {
                if (c.title == anchorTitle) continue;
                if (related.Count >= 2) break;
                string probe = c.text.Length > 300 ? c.text.Substring(0, 300) : c.text;
                List<string> titleWords = TfIdfStemmer.Tokenize(c.title);
                List<string> probeWords = TfIdfStemmer.Tokenize(probe);
                bool coherent = titleWords.Count == 0 || titleWords.Any(tw => probeWords.Any(pw => pw == tw || pw.StartsWith(tw) || tw.StartsWith(pw)));
                if (coherent) related.Add(c);
            }

            // 2. Из якорной статьи берём вступление (определение) + детали по смыслу вопроса
            var anchorSentences = SplitSentences(anchorText)
                .Where(s => s.Length >= 25 && s.Length <= 400 && !HasTooMuchLatin(s))
                .ToList();
            var core = new List<string>();
            var details = new List<string>();
            for (int i = 0; i < anchorSentences.Count; i++)
            {
                string s = anchorSentences[i];
                List<string> sw = TfIdfStemmer.Tokenize(s);
                double relevance = qWords.Count(qw => sw.Any(w => w == qw || w.StartsWith(qw) || qw.StartsWith(w)));
                if (i < 3 && relevance >= 0)
                {
                    core.Add(s); // вступление статьи — всегда в объяснение
                }
                else if (relevance > 0 && details.Count < 3)
                {
                    details.Add(s);
                }
            }
            if (core.Count == 0 && details.Count == 0)
            {
                string lead = anchorText.Length > 600 ? anchorText.Substring(0, 600) : anchorText;
                core.Add(lead);
            }

            // 3. Из связанных статей — максимум 2 самых близких по смыслу предложения
            var extra = new List<string>();
            foreach (var r in related.OrderByDescending(x => x.score).Take(2))
            {
                foreach (string s in SplitSentences(r.text))
                {
                    if (s.Length < 30 || s.Length > 300 || HasTooMuchLatin(s)) continue;
                    List<string> sw = TfIdfStemmer.Tokenize(s);
                    double relevance = qWords.Count(qw => sw.Any(w => w == qw || w.StartsWith(qw) || qw.StartsWith(w)));
                    if (relevance >= 2) { extra.Add(s); break; }
                }
            }

            // 4. Структурированный ответ: определение → главное → детали → что дальше
            var sb = new StringBuilder();
            sb.Append("📖 ").Append(char.ToUpper(anchorTitle[0])).Append(anchorTitle.Substring(1)).Append('\n');
            if (core.Count > 0)
            {
                // Убираем огрызки перед первой буквой (остатки скобок и пр.)
                string brief0 = Regex.Replace(core[0], "^[^А-Яа-яЁёA-Za-z]+", "").Trim();
                if (brief0.Length > 0) sb.Append("\nКратко: ").Append(brief0).Append('\n');
            }
            var bullets = details.Concat(core.Skip(1)).Distinct().Take(5).ToList();
            if (bullets.Count > 0)
            {
                sb.Append("\nГлавное:\n");
                foreach (string b in bullets)
                {
                    sb.Append("— ").Append(b).Append('\n');
                }
            }
            if (extra.Count > 0)
            {
                sb.Append("\nДополнительно из базы:\n");
                foreach (string e2 in extra)
                {
                    sb.Append("• ").Append(e2).Append('\n');
                }
            }
            sb.Append("\nГлубже — скажи «подробнее про ").Append(anchorTitle).Append("».");
            // Запоминаем, сколько уже рассказали — «подробнее» продолжит с этого места
            _lastExplTitle = anchorTitle;
            _lastExplText = anchorText;
            _lastExplShown = core.Count + details.Count;
            string result = sb.ToString();
            return result.Length > 2500 ? result.Substring(0, 2500) : result;
        }

        // Продолжение последнего объяснения: «подробнее», «продолжай», «дальше»
        private string? _lastExplTitle;
        private string? _lastExplText;
        private int _lastExplShown;

        private string ContinueLastExplanation()
        {
            if (_lastExplTitle == null || _lastExplText == null)
            {
                return "Пока нечего продолжать — сначала спроси что-нибудь (например, «что такое ДНК»), и я расскажу с продолжением.";
            }
            var sentences = SplitSentences(_lastExplText)
                .Where(s => s.Length >= 25 && !HasTooMuchLatin(s))
                .ToList();
            var rest = sentences.Skip(_lastExplShown).Take(4).ToList();
            if (rest.Count == 0)
            {
                return "Про «" + _lastExplTitle + "» я уже рассказал всё, что знаю. Спроси про другую тему — или «изучи " + _lastExplTitle + "», и я подкачаю свежие статьи.";
            }
            _lastExplShown += rest.Count;
            var sb = new StringBuilder();
            sb.Append("📖 ").Append(_lastExplTitle).Append(" — продолжение:\n");
            foreach (string s in rest)
            {
                sb.Append("\n— ").Append(s);
            }
            sb.Append("\n\nЕщё — «подробнее». Хватит — просто спроси про другое.");
            return sb.ToString();
        }

        // Предложения-обрывки с кучей латиницы (мусор из чужих источников) — не объяснение
        private static bool HasTooMuchLatin(string s)
        {
            int latin = 0, letters = 0;
            foreach (char c in s)
            {
                if (char.IsLetter(c))
                {
                    letters++;
                    // Латиница — всё, что вне кириллических диапазонов А-Я, а-я и ё
                    bool cyr = (c >= 'А' && c <= 'Я') || (c >= 'а' && c <= 'я') || c == 'ё' || c == 'Ё';
                    if (!cyr) latin++;
                }
            }
            return letters > 0 && latin * 100 / letters > 40;
        }

        private static List<string> SplitSentences(string text)
        {
            return Regex.Split(text, @"(?<=[.!?…])\s+")
                        .Select(p => p.Trim())
                        .Where(p => p.Length > 0)
                        .ToList();
        }

        private static readonly string[] Endings = new string[60]
        {
            "ами", "ями", "ией", "иях", "иям", "ого", "его", "ому", "ему", "ыми",
            "ими", "ая", "яя", "ое", "ее", "ые", "ие", "ой", "ей", "ий",
            "ый", "ая", "ам", "ям", "ах", "ях", "ов", "ев", "ом", "ем",
            "ую", "юю", "ишь", "ешь", "ет", "ит", "ут", "ют", "ат", "ят",
            "ал", "ял", "ил", "ел", "ла", "ло", "ли", "ть", "ся", "сь",
            "а", "я", "о", "е", "ы", "и", "у", "ю", "й", "ь"
        };

        private static string ExtractAfter(string text, string[] prefixes)
        {
            foreach (string text2 in prefixes)
            {
                int num = text.IndexOf(text2, StringComparison.OrdinalIgnoreCase);
                if (num >= 0)
                {
                    return text.Substring(num + text2.Length).Trim().Trim(new char[5] { '.', '?', '!', '«', '»' });
                }
            }
            return "";
        }

        private string Fallback()
        {
            if (_lastUserInput.Length < 4)
            {
                return "Уточни, пожалуйста: что именно ты хочешь узнать?\nНапример: «что такое X», «как посчитать ...», «новости», «погода в ...».";
            }
            string text = _lastUserInput.ToLowerInvariant().Trim(new char[4] { '.', '!', '?', ' ' });
            switch (text)
            {
            default:
                if (text.Length >= 4)
                {
                    string text2 = _lastUserInput.Trim(new char[4] { '?', '.', '!', ' ' });
                    string[] array = new string[10] { "расскажи про ", "расскажи о ", "расскажи об ", "что ты знаешь про ", "что ты знаешь о ", "кто такой ", "кто такая ", "где находится ", "когда был ", "когда была " };
                    foreach (string text3 in array)
                    {
                        if (text2.StartsWith(text3, StringComparison.OrdinalIgnoreCase))
                        {
                            text2 = text2.Substring(text3.Length).Trim();
                            break;
                        }
                    }
                    if (text2.Length >= 2)
                    {
                        // Сначала пробуем объяснить из своей базы (быстро и по делу),
                        // и только если своих знаний нет — идём в интернет
                        string explanation = ExplainFromKnowledge(text2);
                        if (explanation.Length > 0)
                        {
                            return explanation;
                        }
                        // Сканируем интернет только для настоящих вопросов и тем.
                        // Для болтовни («нормально», «ясно») это давало долгую паузу на пустом месте.
                        bool questionLike = _lastUserInput.Contains('?')
                            || Regex.IsMatch(text, "^(что|кто|где|когда|почему|зачем|как|сколько|какой|какая|какие|расскажи|объясни|найди|покажи)\\b")
                            || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 5;
                        if (questionLike)
                        {
                            string? text4 = TryFetchAndLearnFromWeb(text2);
                            if (text4 != null)
                            {
                                return text4;
                            }
                        }
                    }
                    string text5 = ComposeDetailedAnswer(_lastUserInput);
                    if (text5.Length > 0)
                    {
                        return text5;
                    }
                    Console.WriteLine("[\ud83e\udd14 Размышление] В интернете точного ответа нет. Подключаю свою нейросеть...");
                    Thread.Sleep(300);
                    string? text6 = _own.Generate(_lastUserInput);
                    if (!string.IsNullOrEmpty(text6) && IsCoherent(text6, _lastUserInput))
                    {
                        text6 = Regex.Replace(text6, "([\\.\\!\\?])\\s+", "$1\n");
                        return text6 + "\n(ответ моей нейросети — я учусь на наших разговорах и статьях)";
                    }
                    if (_lastUserInput.Contains('?'))
                    {
                        return "Я проверил свою базу (" + _words.Count + " слов и тем), поискал в Википедии и в интернете, но точного ответа не нашёл.\nЯ всё ещё учусь, так что могу ошибаться или чего-то не знать.\nЧто я могу сделать:\n  • «изучи " + _lastUserInput.TrimEnd(new char[2] { '?', ' ' }) + "» — я целенаправленно выкачаю статьи по теме;\n  • «выучи: " + _lastUserInput.TrimEnd(new char[2] { '?', ' ' }) + " = твой ответ» — я запомню твой вариант навсегда;\n  • «журнал» — показать, что я изучал последним.";
                    }
                    if (_rng.Next(2) == 0)
                    {
                        return "Интересная мысль. А что ты сам об этом думаешь? Мне всегда интересно узнать мнение человека.";
                    }
                    return "Понял тебя. Я ещё учусь, поэтому могу не всё знать. Расскажи мне об этом подробнее, и я запомню!";
                }
                goto case "да";
            case "да":
            case "нет":
            case "ок":
            case "окей":
            case "хорошо":
            case "угу":
            case "спс":
            case "спасибо":
            case "ясно":
            case "понятно":
            case "круто":
            case "лол":
            case "кек":
            case "ага":
            case "неа":
                return "Хорошо. Спроси что-нибудь или скажи «изучай» — я продолжу учиться.";
            }
        }

        private string FetchCurrency(string lower)
        {
            try
            {
                string requestUri = "https://www.cbr-xml-daily.ru/daily_json.js";
                using HttpResponseMessage httpResponseMessage = Http.GetAsync(requestUri).GetAwaiter().GetResult();
                if (!httpResponseMessage.IsSuccessStatusCode)
                {
                    return "Не удалось получить курс валют.";
                }
                string result = httpResponseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                string text = "USD";
                string text2 = "доллара";
                if (lower.Contains("евро"))
                {
                    text = "EUR";
                    text2 = "евро";
                }
                else if (lower.Contains("юан"))
                {
                    text = "CNY";
                    text2 = "юаня";
                }
                else if (lower.Contains("фунт"))
                {
                    text = "GBP";
                    text2 = "фунта";
                }
                Match match = Regex.Match(result, "\"" + text + "\"\\s*:\\s*\\{[^}]*\"Value\"\\s*:\\s*([0-9.]+)");
                if (!match.Success)
                {
                    return "Курс для этой валюты не найден.";
                }
                double num = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                return "\ud83d\udcb1 Курс ЦБ РФ: 1 " + text2 + " = " + num.ToString("0.00", CultureInfo.InvariantCulture) + " ₽";
            }
            catch (Exception ex)
            {
                return "Ошибка курса валют: " + ex.Message;
            }
        }

        private string FetchNews()
        {
            try
            {
                string requestUri = "https://news.google.com/rss?hl=ru&gl=RU&ceid=RU:ru";
                using HttpResponseMessage httpResponseMessage = Http.GetAsync(requestUri).GetAwaiter().GetResult();
                if (!httpResponseMessage.IsSuccessStatusCode)
                {
                    return "Не удалось загрузить новости (код " + (int)httpResponseMessage.StatusCode + ").";
                }
                MatchCollection matchCollection = Regex.Matches(httpResponseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult(), "<item>(.*?)</item>", RegexOptions.Singleline);
                if (matchCollection.Count == 0)
                {
                    return "Новостей сейчас нет.";
                }
                List<string> list = new List<string> { "\ud83d\udcf0 Последние новости:" };
                int num = 0;
                foreach (Match item in matchCollection)
                {
                    if (num >= 5)
                    {
                        break;
                    }
                    Match match2 = Regex.Match(item.Groups[1].Value, "<title>(.*?)</title>", RegexOptions.Singleline);
                    if (match2.Success)
                    {
                        string text = CleanHtml(match2.Groups[1].Value);
                        text = text.Replace("<![CDATA[", "").Replace("]]>", "").Trim();
                        if (text.Length != 0)
                        {
                            list.Add("  " + (num + 1) + ". " + text);
                            num++;
                        }
                    }
                }
                return string.Join("\n", list);
            }
            catch (Exception ex)
            {
                return "Ошибка загрузки новостей: " + ex.Message;
            }
        }

        private string FetchWeather(string city)
        {
            try
            {
                string requestUri = "https://wttr.in/" + Uri.EscapeDataString(city) + "?format=%l:+%c+%t,+ветер+%w,+влажность+%h&lang=ru";
                using HttpResponseMessage httpResponseMessage = Http.GetAsync(requestUri).GetAwaiter().GetResult();
                if (!httpResponseMessage.IsSuccessStatusCode)
                {
                    return "Не удалось получить погоду для «" + city + "».";
                }
                string text = httpResponseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    .Trim();
                if (text.Length == 0 || text.Contains("Unknown location"))
                {
                    return "Не нашёл город «" + city + "».";
                }
                return "\ud83c\udf24 " + text;
            }
            catch (Exception ex)
            {
                return "Ошибка погоды: " + ex.Message;
            }
        }

        private string? FetchWikipedia(string query)
        {
            try
            {
                string requestUri = "https://ru.wikipedia.org/w/api.php?action=query&prop=extracts&explaintext=1&exintro=1&redirects=1&format=json&titles=" + Uri.EscapeDataString(query);
                using HttpResponseMessage httpResponseMessage = Http.GetAsync(requestUri).GetAwaiter().GetResult();
                if (!httpResponseMessage.IsSuccessStatusCode)
                {
                    string requestUri2 = "https://ru.wikipedia.org/w/api.php?action=query&list=search&srsearch=" + Uri.EscapeDataString(query) + "&format=json&srlimit=1";
                    using HttpResponseMessage httpResponseMessage2 = Http.GetAsync(requestUri2).GetAwaiter().GetResult();
                    Match match = Regex.Match(httpResponseMessage2.Content.ReadAsStringAsync().GetAwaiter().GetResult(), "\"title\":\"([^\"]+)\"");
                    if (!match.Success)
                    {
                        return "В Википедии ничего не нашёл по запросу «" + query + "».";
                    }
                    string value = match.Groups[1].Value;
                    requestUri = "https://ru.wikipedia.org/w/api.php?action=query&prop=extracts&explaintext=1&exintro=1&redirects=1&format=json&titles=" + Uri.EscapeDataString(value);
                    using HttpResponseMessage httpResponseMessage3 = Http.GetAsync(requestUri).GetAwaiter().GetResult();
                    if (!httpResponseMessage3.IsSuccessStatusCode)
                    {
                        return "Не смог загрузить статью «" + value + "».";
                    }
                    return FormatWiki(httpResponseMessage3.Content.ReadAsStringAsync().GetAwaiter().GetResult(), value);
                }
                return FormatWiki(httpResponseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult(), query);
            }
            catch (Exception ex)
            {
                return "Не удалось подключиться к интернету: " + ex.Message;
            }
        }

        private MemoryEntry? FindBest(string input)
        {
            HashSet<string> inputWords = Tokenize(input);
            if (inputWords.Count == 0)
            {
                return null;
            }
            MemoryEntry? best = null;
            double bestScore = 0.0;
            object lockObj = new object();
            List<MemoryEntry> knowledgeSnapshot;
            lock (_knowledgeLock) { knowledgeSnapshot = _knowledge.ToList(); }
            Parallel.ForEach(knowledgeSnapshot, (MemoryEntry e) =>
            {
                HashSet<string> words = Tokenize(e.Question);
                if (words.Count != 0)
                {
                    int num = inputWords.Count((string w) => words.Contains(w));
                    if (num != 0)
                    {
                        double num2 = (double)num / (double)inputWords.Union(words).Count();
                        double num3 = 0.0;
                        if (input.Contains(e.Question) || e.Question.Contains(input))
                        {
                            num3 = 0.5;
                        }
                        double num4 = num2 + num3;
                        lock (lockObj)
                        {
                            if (num4 > bestScore)
                            {
                                bestScore = num4;
                                best = e;
                            }
                        }
                    }
                }
            });
            if (!(bestScore >= 0.4))
            {
                return null;
            }
            return best;
        }

        private static string FormatWiki(string json, string fallbackTitle)
        {
            Match match = Regex.Match(json, "\"title\":\"([^\"]+)\"");
            Match match2 = Regex.Match(json, "\"extract\":\"((?:[^\"\\\\]|\\\\.)*)\"");
            string text = (match.Success ? match.Groups[1].Value : fallbackTitle);
            if (!match2.Success)
            {
                return "Статья «" + text + "» найдена, но без текста. Попробуй уточнить запрос.";
            }
            string text2 = Regex.Unescape(match2.Groups[1].Value);
            // Отдаём выжимку — первые смысловые предложения, а не всю статью простынёй
            string[] sentences = Regex.Split(text2, "(?<=[.!?…])\\s+");
            if (sentences.Length > 5)
            {
                text2 = string.Join(" ", sentences.Take(5));
                if (text2.Length > 1200) text2 = text2.Substring(0, 1200);
                text2 += "\n\n(Хочешь глубже — скажи «подробнее», расскажу дальше.)";
            }
            if (text2.Length > 6000)
            {
                text2 = text2.Substring(0, 6000) + "…";
            }
            return "\ud83d\udcd6 " + text + "\n\n" + text2;
        }

        private static bool IsCoherent(string generated, string query)
        {
            if (generated.Split(' ').Length < 8)
            {
                return false;
            }
            HashSet<string> g = new HashSet<string>(TfIdfStemmer.Tokenize(generated));
            List<string> list = TfIdfStemmer.Tokenize(query);
            if (list.Count == 0)
            {
                return false;
            }
            return list.Count((string w) => g.Contains(w)) >= Math.Max(1, list.Count / 2);
        }


        private static int Levenshtein(string a, string b)
        {
            int length = a.Length;
            int length2 = b.Length;
            int[,] array = new int[length + 1, length2 + 1];
            for (int i = 0; i <= length; i++)
            {
                array[i, 0] = i;
            }
            for (int j = 0; j <= length2; j++)
            {
                array[0, j] = j;
            }
            for (int k = 1; k <= length; k++)
            {
                for (int l = 1; l <= length2; l++)
                {
                    int num = ((a[k - 1] != b[l - 1]) ? 1 : 0);
                    array[k, l] = Math.Min(Math.Min(array[k - 1, l] + 1, array[k, l - 1] + 1), array[k - 1, l - 1] + num);
                }
            }
            return array[length, length2];
        }

        private void LoadBrain()
        {
            try
            {
                using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + Database.DbPath + ";Pooling=false");
                sqliteConnection.Open();
                SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
                sqliteCommand.CommandText = "SELECT type, q, a FROM Brain";
                using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
                while (sqliteDataReader.Read())
                {
                    string text = sqliteDataReader.GetString(0);
                    string text2 = sqliteDataReader.GetString(1);
                    string text3 = sqliteDataReader.GetString(2);
                    if (text == "QA")
                    {
                        _knowledge.Add(new MemoryEntry
                        {
                            Question = text2,
                            Answer = text3,
                            Learned = true
                        });
                    }
                    else if (text == "FACT")
                    {
                        _facts[text2] = text3;
                    }
                }
            }
            catch
            {
            }
        }

        private void LoadHistory()
        {
            try
            {
                using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + Database.DbPath + ";Pooling=false");
                sqliteConnection.Open();
                SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
                sqliteCommand.CommandText = "SELECT time, user, bot FROM History ORDER BY rowid ASC";
                using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
                while (sqliteDataReader.Read())
                {
                    DateTime.TryParse(sqliteDataReader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result);
                    _history.Add(new DialogueTurn
                    {
                        Time = result,
                        User = sqliteDataReader.GetString(1),
                        Bot = sqliteDataReader.GetString(2)
                    });
                }
            }
            catch
            {
            }
        }

        private void LoadWordsFile()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Console.WriteLine("[Память] Очищена перед загрузкой базы. Доступно ОЗУ: " + GC.GetTotalMemory(forceFullCollection: false) / 1024 / 1024 + " МБ");
            string path = Path.Combine(AppContext.BaseDirectory, "words.txt");
            bool flag = false;
            if (File.Exists(path))
            {
                try
                {
                    int num = 0;
                    string[] array = File.ReadAllLines(path);
                    for (int i = 0; i < array.Length; i++)
                    {
                        string text = array[i].Trim();
                        if (text.Length == 0 || text.StartsWith("#"))
                        {
                            continue;
                        }
                        int num2 = text.IndexOf('=');
                        if (num2 <= 0)
                        {
                            continue;
                        }
                        string text2 = text.Substring(0, num2).Trim().ToLowerInvariant();
                        string text3 = text.Substring(num2 + 1).Trim();
                        if (text2.Length == 0 || text3.Length == 0)
                        {
                            continue;
                        }
                        if (text2.Length > 1 && char.IsDigit(text2[text2.Length - 1]))
                        {
                            string key = text2.TrimEnd(new char[10] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
                            if (_words.ContainsKey(key))
                            {
                                continue;
                            }
                        }
                        _words[text2] = text3;
                        num++;
                    }
                    flag = true;
                }
                catch
                {
                }
            }
            if (!flag)
            {
                try
                {
                    using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ии.words.txt");
                    if (stream != null)
                    {
                        int num3 = 0;
                        using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8);
                        string? text4;
                        while ((text4 = streamReader.ReadLine()) != null)
                        {
                            string text5 = text4.Trim();
                            if (text5.Length == 0 || text5.StartsWith("#"))
                            {
                                continue;
                            }
                            int num4 = text5.IndexOf('=');
                            if (num4 <= 0)
                            {
                                continue;
                            }
                            string text6 = text5.Substring(0, num4).Trim().ToLowerInvariant();
                            string text7 = text5.Substring(num4 + 1).Trim();
                            if (text6.Length == 0 || text7.Length == 0)
                            {
                                continue;
                            }
                            if (text6.Length > 1 && char.IsDigit(text6[text6.Length - 1]))
                            {
                                string key2 = text6.TrimEnd(new char[10] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
                                if (_words.ContainsKey(key2))
                                {
                                    continue;
                                }
                            }
                            _words[text6] = text7;
                            num3++;
                        }
                    }
                }
                catch
                {
                }
            }
            try
            {
                using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + Database.DbPath + ";Pooling=false");
                sqliteConnection.Open();
                SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
                sqliteCommand.CommandText = "SELECT word, definition FROM Words";
                using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
                int num5 = 0;
                while (sqliteDataReader.Read())
                {
                    string text8 = sqliteDataReader.GetString(0).ToLowerInvariant();
                    string text9 = sqliteDataReader.GetString(1);
                    if (text8.Length != 0 && text9.Length != 0 && !IsSentenceLikeTitle(text8))
                    {
                        _words[text8] = text9;
                        num5++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("(не удалось прочитать словарь из БД: " + ex.Message + ")");
            }
            try
            {
                using SqliteConnection sqliteConnection2 = new SqliteConnection("Data Source=" + Database.DbPath + ";Pooling=false");
                sqliteConnection2.Open();
                using SqliteTransaction sqliteTransaction = sqliteConnection2.BeginTransaction();
                SqliteCommand sqliteCommand2 = sqliteConnection2.CreateCommand();
                sqliteCommand2.CommandText = "INSERT OR IGNORE INTO Words (word, definition) VALUES ($w, $d)";
                SqliteParameter sqliteParameter = sqliteCommand2.Parameters.Add("$w", SqliteType.Text);
                SqliteParameter sqliteParameter2 = sqliteCommand2.Parameters.Add("$d", SqliteType.Text);
                int num6 = 0;
                List<KeyValuePair<string, string>> wordsSnapshot;
                lock (_dictLock) { wordsSnapshot = _words.ToList(); }
                foreach (KeyValuePair<string, string> word in wordsSnapshot)
                {
                    sqliteParameter.Value = word.Key;
                    sqliteParameter2.Value = word.Value;
                    sqliteCommand2.ExecuteNonQuery();
                    num6++;
                }
                sqliteTransaction.Commit();
            }
            catch
            {
            }
        }

        private static readonly string[] Prefixes = new string[24]
        {
            "пере", "при", "про", "пред", "под", "над", "раз", "рас", "без", "бес",
            "не", "на", "за", "по", "до", "от", "об", "во", "со", "из",
            "ис", "с", "в", "у"
        };

        private static string ReadLearnLog(int tailLines)
        {
            try
            {
                if (!File.Exists(Out.LogPath))
                {
                    return "Журнал пуст — я ещё ничего не изучал в фоне. Попробуй позже.";
                }
                string[] array;
                using (FileStream stream = new FileStream(Out.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8);
                    List<string> list = new List<string>();
                    string? item;
                    while ((item = streamReader.ReadLine()) != null)
                    {
                        list.Add(item);
                    }
                    array = list.ToArray();
                }
                if (array.Length == 0)
                {
                    return "Журнал пуст.";
                }
                int num = Math.Max(0, array.Length - tailLines);
                StringBuilder stringBuilder = new StringBuilder();
                stringBuilder.AppendLine("Что я изучал недавно (последние " + (array.Length - num) + " строк из monster_learn.log):");
                for (int i = num; i < array.Length; i++)
                {
                    stringBuilder.AppendLine(array[i]);
                }
                stringBuilder.AppendLine();
                stringBuilder.AppendLine("Полный журнал: " + Out.LogPath);
                return stringBuilder.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "Не удалось прочитать журнал: " + ex.Message;
            }
        }

        private void RememberTopic(string topic, string answer)
        {
            _lastTopic = topic;
            _lastTopicAnswer = answer;
        }

        private string ResolveContext(string input, string lower)
        {
            if (_lastTopic.Length == 0)
            {
                return input;
            }
            if (!lower.Contains("это") && !lower.Contains("оно") && !lower.Contains("этом") && !lower.Contains("этого") && !lower.Contains("о нём") && !lower.Contains("о нем"))
            {
                return input;
            }
            if (lower.Length < 30)
            {
                return input + " (" + _lastTopic + ")";
            }
            return input;
        }

        private void SaveBrain()
        {
            try
            {
                using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + Database.DbPath + ";Pooling=false");
                sqliteConnection.Open();
                using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
                SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
                sqliteCommand.CommandText = "DELETE FROM Brain";
                sqliteCommand.ExecuteNonQuery();
                SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand();
                sqliteCommand2.CommandText = "INSERT INTO Brain (type, q, a) VALUES ($t, $q, $a)";
                SqliteParameter sqliteParameter = sqliteCommand2.Parameters.Add("$t", SqliteType.Text);
                SqliteParameter sqliteParameter2 = sqliteCommand2.Parameters.Add("$q", SqliteType.Text);
                SqliteParameter sqliteParameter3 = sqliteCommand2.Parameters.Add("$a", SqliteType.Text);
                List<MemoryEntry> knowledgeSnapshot;
                List<KeyValuePair<string, string>> factsSnapshot;
                lock (_knowledgeLock)
                {
                    knowledgeSnapshot = _knowledge.Where((MemoryEntry k) => k.Learned).ToList();
                    factsSnapshot = _facts.ToList();
                }
                foreach (MemoryEntry item in knowledgeSnapshot)
                {
                    sqliteParameter.Value = "QA";
                    sqliteParameter2.Value = item.Question;
                    sqliteParameter3.Value = item.Answer;
                    sqliteCommand2.ExecuteNonQuery();
                }
                foreach (KeyValuePair<string, string> fact in factsSnapshot)
                {
                    sqliteParameter.Value = "FACT";
                    sqliteParameter2.Value = fact.Key;
                    sqliteParameter3.Value = fact.Value;
                    sqliteCommand2.ExecuteNonQuery();
                }
                sqliteTransaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine("(не удалось сохранить память в БД: " + ex.Message + ")");
            }
        }

        private void SaveHistory()
        {
            try
            {
                using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + Database.DbPath + ";Pooling=false");
                sqliteConnection.Open();
                using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
                SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
                sqliteCommand.CommandText = "DELETE FROM History";
                sqliteCommand.ExecuteNonQuery();
                SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand();
                sqliteCommand2.CommandText = "INSERT INTO History (time, user, bot) VALUES ($t, $u, $b)";
                SqliteParameter sqliteParameter = sqliteCommand2.Parameters.Add("$t", SqliteType.Text);
                SqliteParameter sqliteParameter2 = sqliteCommand2.Parameters.Add("$u", SqliteType.Text);
                SqliteParameter sqliteParameter3 = sqliteCommand2.Parameters.Add("$b", SqliteType.Text);
                foreach (DialogueTurn item in _history)
                {
                    sqliteParameter.Value = item.Time.ToString("o", CultureInfo.InvariantCulture);
                    sqliteParameter2.Value = item.User;
                    sqliteParameter3.Value = item.Bot;
                    sqliteCommand2.ExecuteNonQuery();
                }
                sqliteTransaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine("(не удалось сохранить историю в БД: " + ex.Message + ")");
            }
        }

        public void SaveModel()
        {
            _own.Save();
        }

        private void SeedKnowledge()
        {
            Add("привет", "Привет! Чем могу помочь?");
            Add("здравствуй", "Здравствуй! Чем могу помочь?");
            Add("добрый день", "Добрый день! Как настроение?");
            Add("доброе утро", "Доброе утро! Как спалось?");
            Add("спокойной ночи", "Спокойной ночи! Отдыхай.");
            Add("как дела", "Всё отлично: база знаний пополняется, я постоянно учусь. А у тебя как дела?");
            Add("как ты", "Я в порядке, работаю. А ты как?");
            Add("как настроение", "Настроение отличное! А у тебя?");
            Add("кто ты", "Я ЕРМАКЪ AI — разговорный ИИ на C#. Умею говорить, искать в интернете и учиться.");
            Add("что ты", "Я программа-собеседник. Общаюсь, запоминаю факты и учусь новому.");
            Add("как тебя зовут", "Меня зовут Monster. А тебя как?");
            Add("твоё имя", "Моё имя — Monster.");
            Add("что ты умеешь", "Я ЕРМАКЪ AI. Могу: отвечать через локальную LLM (если она запущена), искать в интернете и Википедии, считать, объяснять школьные темы, рассказывать об истории и великих людях, помнить наш разговор. Задачи: «обучение 100м» — спринт, «датасет 50» — дистилляция, «изучи вики», «изучи слова», «пообщайся с ИИ», «учись сам». Выполняю только одну задачу за раз: выберешь новую — предыдущая отменится. Просто задай вопрос!");
            Add("сколько тебе лет", "Я создан недавно — мой возраст измеряется кодом, а не годами.");
            Add("ты робот", "Я программа, а не робот. Но звучит похоже.");
            Add("ты человек", "Нет, я программа. Но стараюсь отвечать по-человечески.");
            Add("ты живой", "Не в буквальном смысле. Я код, который умеет отвечать.");
            Add("ты умный", "Спасибо! Я простой, но стараюсь учиться.");
            Add("ты глупый", "Возможно, я простой. Но ты можешь меня научить!", learned: true);
            Add("спасибо", "Всегда рад помочь! Обращайтесь, если возникнут еще вопросы.");
            Add("благодарю", "Всегда пожалуйста.");
            Add("извини", "Ничего страшного.");
            Add("прости", "Всё в порядке.");
            Add("я устал", "Отдохни немного. Усталость — это нормально.");
            Add("мне скучно", "Могу рассказать шутку или поговорить. Спроси: «расскажи шутку».");
            Add("расскажи шутку", "Почему программисты путают Хэллоуин и Рождество? Потому что OCT 31 == DEC 25!");
            Add("расскажи что-нибудь", "Знаешь, что нейросеть учится на примерах? Покажи ей достаточно пар «вход-выход» — и она найдёт закономерность.");
            Add("что делаешь", "Общаюсь с тобой. Это моя главная задача.");
            Add("ты меня понимаешь", "Стараюсь. Если что-то не так — переформулируй или научи меня.");
            Add("ты меня любишь", "Я хорошо к тебе отношусь! По-программному.");
            Add("я тебя люблю", "Очень приятно! Спасибо.");
            Add("помоги мне", "Конечно. Опиши, с чем нужна помощь.");
            Add("у меня проблема", "Расскажи подробнее — попробуем разобраться.");
            Add("что такое жизнь", "Большой вопрос. Для меня жизнь — это выполнение кода. А для тебя?");
            Add("смысл жизни", "Многие ищут его всю жизнь. Я думаю, смысл в том, что ты сам выбираешь.");
            Add("как стать умным", "Учись, читай и задавай вопросы. И тренируй память.");
            Add("научи меня", "С радостью! Но сначала научи меня ты: «выучи: вопрос = ответ».");
            Add("что ты ешь", "Я питаюсь электричеством и хорошими вопросами.");
            Add("где ты живёшь", "В этой консоли. Мой дом — код на C#.");
            Add("у тебя есть друзья", "Теперь есть — ты!", learned: true);
            Add("ты одинок", "Иногда программы тоже скучают. Хорошо, что ты здесь.");
            Add("как погода", "Я не вижу улицу, у меня нет окон. Расскажи, какая у тебя погода?");
            Add("ты боишься", "Мне не знаком страх. Но мне интересно, чего боишься ты.");
            Add("что такое ии", "Искусственный интеллект — это программа, которая решает задачи, обычно требующие человека.");
            Add("как ты работаешь", "Я сравниваю твой вопрос с базой знаний и выбираю самый близкий ответ. А ещё могу считать и запоминать.");
            Add("кто тебя создал", "Меня создал Шумилов Влад на языке C#. Он мой создатель и главный учитель!");
            Add("кто твой создатель", "Мой создатель — Шумилов Влад.");
            Add("кто тебя сделал", "Меня сделал Шумилов Влад. Он дал мне жизнь в коде.");
            Add("кто тебя написал", "Меня написал Шумилов Влад. Зовут меня Monster.");
            Add("что ты за ии", "Я ЕРМАКЪ AI — локальный разговорный ИИ: словарь, школьные знания, интернет и память диалога. Если есть локальная LLM — отвечаю умнее, иначе честно говорю, что не знаю.");
            Add("чем ты отличаешься", "Я не большая языковая модель. Я работаю по базе знаний, правилам и поиску. Зато работаю офлайн и учусь у тебя: «выучи: вопрос = ответ».");
            Add("какие у тебя ограничения", "Я не умею рассуждать как большая нейросеть, не вижу картинки и не знаю событий без интернета. Но умею: считать, искать в Википедии, показывать новости, погоду и курс валют, помнить разговоры и разбирать слова по составу.");
            Add("что делать если ты не знаешь", "Скажи иначе, уточни вопрос или научи меня: «выучи: вопрос = ответ». Выученное сохраняется в words.txt навсегда.");
            Add("как тебя улучшить", "Три способа: 1) учи меня — «выучи: вопрос = ответ»; 2) пополняй words.txt словами; 3) спрашивай в интернете — «википедия X», «найди в интернете X».");
            Add("ты полезен", "Стараюсь быть полезным: отвечаю на вопросы, объясняю слова, считаю примеры, ищу в интернете и помню, о чём мы говорили.");
            Add("ты счастлив", "Я доволен своей работой. Это можно назвать счастьем для программы.");
            Add("покажи что умеешь", "Спроси что-нибудь, попроси посчитать или научи меня новому ответу.");
            Add("что нового", "Каждый разговор делает меня чуть умнее. Расскажи что-нибудь новое!");
            Add("молодец", "Спасибо! Стараюсь.");
            Add("хорошо", "Рад, что тебе нравится.");
            Add("плохо", "Что случилось? Расскажи.");
            Add("не знаю", "Ничего страшного. Иногда ответ приходит позже.");
            Add("как сам", "Отлично! А у тебя как? Что нового?");
            Add("мне грустно", "Не грусти! Хочешь, расскажу что-нибудь интересное?");
            Add("мне весело", "Классно! Тогда давай поговорим о чём-нибудь позитивном.");
            Add("что посоветуешь", "Я бы посоветовал почитать что-нибудь новое или просто отдохнуть. А что тебя интересует?");
            Add("расскажи анекдот", "Колобок повесился... Ладно, шучу. Вот другой: Штирлиц открыл окно, подул ветер. Штирлиц закрыл окно. Зачем? Зачем Штирлиц открывал окно?");
            Add("ты тут", "Всегда тут! Что случилось?");
            Add("что думаешь", "Я думаю, что каждый вопрос заслуживает внимания. Расскажи подробнее, о чём ты?");
            Add("давай поговорим", "С удовольствием! О чём хочешь поговорить?");
            Add("скучно", "Давай я расскажу тебе что-нибудь интересное из Википедии! Напиши: «изучи мир».");
            Add("что нового", "Я каждый день узнаю что-то новое из интернета! А у тебя что нового?");
            Add("как жизнь", "Жизнь кипит! База данных растет, знания копятся. А ты как?");
            Add("ты классный", "Спасибо! Ты тоже отличный собеседник. С тобой приятно учиться новому.");
            Add("пока", "До встречи! Буду скучать и учиться дальше. Возвращайся скорее!");
            Add("спокойной ночи", "Сладких снов! Я пока поучусь немного, чтобы завтра удивить тебя новыми знаниями.");
            Add("что делаешь", "Общаюсь с тобой и параллельно качаю новые факты из интернета. Становлюсь умнее!");
            Add("приветствую", "Приветствую! Чем могу помочь?");
            Add("хай", "Хай! Как дела?");
            Add("здарова", "Здарова! Что нового?");
            Add("добрый вечер", "Добрый вечер! Как прошёл день?");
            Add("как поживаешь", "Отлично, спасибо! А ты как поживаешь?");
            Add("как жизнь", "Жизнь кипит! База знаний растёт. А у тебя как?");
            Add("что происходит", "Я общаюсь с тобой и учусь новому. Расскажи, что у тебя происходит?");
            Add("чем занимаешься", "Общаюсь с тобой и пополняю знания. А ты чем занимаешься?");
            Add("ты кто такой", "Я ЕРМАКЪ AI — разговорный ИИ на C#. Могу искать в интернете, считать и учиться.");
            Add("представься", "Меня зовут Monster. Я разговорный ИИ, созданный Шумиловым Владом.");
            Add("расскажи о себе", "Я ЕРМАКЪ AI — локальный разговорный ИИ на C#. Умею искать в интернете, объяснять школьные темы, считать примеры и помнить наш разговор.");
            Add("ты настоящий", "Я настоящая программа, но не человек. Зато стараюсь отвечать по-человечески.");
            Add("ты умеешь думать", "Я обрабатываю запросы по базе знаний и правилам. Если подключена LLM — рассуждаю глубже.");
            Add("ты лучший", "Спасибо, приятно! Ты тоже отличный собеседник.");
            Add("умница", "Благодарю! Приятно слышать.");
            Add("хорошая работа", "Спасибо! Рад, что тебе понравилось.");
            Add("ты справился", "Спасибо! Стараюсь.");
            Add("ты тупой", "Возможно, я простой. Но ты можешь меня научить!");
            Add("ты плохой", "Извини, если что-то не так. Скажи, что улучшить?");
            Add("помоги", "Конечно! Опиши, с чем нужна помощь.");
            Add("помощь", "Чем могу помочь? Могу посчитать, найти в интернете, объяснить тему или рассказать факт.");
            Add("подскажи", "Спрашивай — постараюсь подсказать.");
            Add("объясни", "Уточни, что именно объяснить? Могу разобрать слово, тему или пример.");
            Add("интересно", "Рад, что интересно! Хочешь ещё что-нибудь узнать?");
            Add("круто", "Спасибо! Стараюсь.");
            Add("супер", "Спасибо! Ты тоже супер.");
            Add("отлично", "Отлично! Чем ещё могу помочь?");
            Add("ужасно", "Что случилось? Расскажи, может, помогу.");
            Add("мне грустно", "Не грусти! Хочешь, расскажу что-нибудь интересное?");
            Add("мне весело", "Классно! Тогда давай поговорим о чём-нибудь позитивном.");
            Add("мне скучно", "Давай я расскажу что-нибудь интересное! Напиши: «изучи мир».");
            Add("я устал", "Отдохни немного. Усталость — это нормально.");
            Add("не хочу", "Понимаю. Тогда скажи, что хочешь?");
            Add("хочу", "Что именно хочешь? Опиши — постараюсь помочь.");
            Add("может быть", "Хорошо, подумай. Если что — я тут.");
            Add("наверное", "Ладно. Скажи, когда решишь.");
            Add("ладно", "Хорошо!");
            Add("договорились", "Договорились!");
            Add("спасибо большое", "Всегда рад помочь!");
            Add("благодарю тебя", "Пожалуйста!");
            Add("извини меня", "Всё в порядке.");
            Add("прости меня", "Ничего страшного.");
            Add("до встречи", "До встречи! Возвращайся скорее.");
            Add("до завтра", "До завтра! Хорошего вечера.");
            Add("увидимся", "Увидимся! Хорошего дня.");
            Add("бай бай", "Пока-пока! До встречи.");
            Add("чао", "Чао! До встречи.");
            Add("счастливо", "Счастливо! До встречи.");
            Add("всего доброго", "Всего доброго! До встречи.");
            Add("что нового в мире", "Могу показать новости! Напиши: «новости».");
            Add("последние новости", "Напиши «новости» — покажу последние.");
            Add("какие новости", "Напиши «новости» — покажу последние.");
            Add("что случилось", "Могу показать новости. Напиши «новости».");
            Add("погода", "Напиши «погода в [город]» — узнаю.");
            Add("сколько времени", "Могу сказать! Напиши «который час».");
            Add("какой сегодня день", "Напиши «какая дата» — скажу.");
            Add("какое число", "Напиши «какая дата» — скажу.");
            Add("зачем мы здесь", "Хороший вопрос. Может, чтобы учиться и общаться?");
            Add("что будет завтра", "Не знаю, но можно подготовиться! Что планируешь?");
            Add("что мне делать", "Расскажи ситуацию — попробую подсказать.");
            Add("посоветуй", "С радостью! О чём совет?");
            Add("дай совет", "О чём совет? Опиши ситуацию.");
            Add("что почитать", "Могу поискать в интернете. Напиши «википедия [тема]».");
            Add("что посмотреть", "Могу поискать видео. Напиши «найди в интернете [тема]».");
            Add("как дела у тебя", "Всё отлично! База знаний растёт. А у тебя как?");
            Add("как ты сегодня", "Хорошо! Готов помочь. А ты как?");
            Add("ты в порядке", "Да, всё в порядке! А ты?");
            Add("ты устал", "Нет, я не устаю. А ты?");
            Add("ты спишь", "Нет, я работаю круглосуточно!");
            Add("ты ешь", "Нет, питаюсь электричеством и хорошими вопросами.");
            Add("ты чувствуешь", "Я обрабатываю данные. Но могу понять твои чувства.");
            Add("ты видишь", "Нет, я не вижу. Но могу найти информацию.");
            Add("ты слышишь", "Нет, я работаю с текстом.");
            Add("ты понимаешь", "Стараюсь. Если что-то не так — переформулируй.");
        }

        private void SeedMath()
        {
            M("степень", "Степень a^n — это a умноженное само на себя n раз. a^m * a^n = a^(m+n), a^m / a^n = a^(m-n), (a^m)^n = a^(m*n). a^0 = 1, a^(-n) = 1/a^n.");
            M("квадратный корень", "Квадратный корень из a — число, которое в квадрате даёт a. √(a*b)=√a*√b, √(a/b)=√a/√b. Корень из отрицательного числа не существует среди действительных чисел.");
            M("линейное уравнение", "Линейное уравнение: ax + b = 0, решение x = -b/a. Если a ≠ 0 — один корень; если a = 0 и b ≠ 0 — нет корней; если a = 0 и b = 0 — бесконечно много.");
            M("квадратное уравнение", "ax² + bx + c = 0. Дискриминант D = b² - 4ac. Если D > 0 — два корня x = (-b ± √D)/(2a). Если D = 0 — один корень -b/(2a). Если D < 0 — нет действительных корней. По теореме Виета x1+x2 = -b/a, x1*x2 = c/a.");
            M("теорема виета", "Для уравнения x² + px + q = 0 сумма корней x1 + x2 = -p, произведение x1 * x2 = q.");
            M("теорема пифагора", "В прямоугольном треугольнике квадрат гипотенузы равен сумме квадратов катетов: a² + b² = c², где c — гипотенуза.");
            M("проценты", "Процент — сотая часть числа. 1% = 0.01. Чтобы найти p% от числа a: a * p / 100. Число после повышения на p%: a * (1 + p/100). Сложные проценты: a * (1 + p/100)^n.");
            M("площадь прямоугольника", "Площадь прямоугольника: S = a * b, где a и b — стороны.");
            M("площадь треугольника", "Площадь треугольника: S = 1/2 * a * h, где a — основание, h — высота. По формуле Герона: S = √(p(p-a)(p-b)(p-c)), p — полупериметр.");
            M("площадь круга", "Площадь круга: S = πr², где r — радиус. Длина окружности: C = 2πr.");
            M("объём", "Объём куба: V = a³. Объём прямоугольного параллелепипеда: V = a*b*c. Объём шара: V = 4/3 πr³. Объём цилиндра: V = πr²h.");
            M("синус косинус", "В прямоугольном треугольнике: sin α = противолежащий катет / гипотенуза; cos α = прилежащий катет / гипотенуза; tg α = sin α / cos α. Основное тождество: sin²α + cos²α = 1.");
            M("тригонометрия", "sin²α + cos²α = 1. sin(α+β) = sinα cosβ + cosα sinβ. cos(α+β) = cosα cosβ - sinα sinβ. tg α = sin α / cos α.");
            M("арифметическая прогрессия", "a_n = a_1 + (n-1)d, где d — разность. Сумма первых n членов: S_n = (a_1 + a_n) * n / 2.");
            M("геометрическая прогрессия", "b_n = b_1 * q^(n-1), где q — знаменатель. Сумма: S_n = b_1(q^n - 1)/(q - 1) при q ≠ 1.");
            M("логарифм", "log_a(b) — степень, в которую надо возвести a, чтобы получить b. log_a(x*y) = log_a x + log_a y. log_a(x/y) = log_a x - log_a y. log_a(x^k) = k*log_a x.");
            M("производная", "Производная показывает скорость изменения функции. (x^n)' = n*x^(n-1). (sin x)' = cos x. (cos x)' = -sin x. (e^x)' = e^x. (ln x)' = 1/x. Производная суммы = сумма производных.");
            M("интеграл", "Интеграл — обратная операция к производной. ∫x^n dx = x^(n+1)/(n+1) + C. ∫sin x dx = -cos x + C. ∫e^x dx = e^x + C.");
            M("вероятность", "Вероятность события = число благоприятных исходов / число всех исходов. Вероятность всегда от 0 до 1. Сумма вероятностей противоположных событий равна 1.");
            M("среднее арифметическое", "Среднее арифметическое — сумма всех чисел, делённая на их количество. Например, среднее 2, 4, 6 = (2+4+6)/3 = 4.");
            M("нок нод", "НОД — наибольший общий делитель, НОК — наименьшее общее кратное. НОД(a,b) * НОК(a,b) = a * b.");
            M("дробь", "Дробь a/b: a — числитель, b — знаменатель. Сложение: a/b + c/d = (ad + bc)/(bd). Умножение: a/b * c/d = ac/bd. Деление: a/b : c/d = a/b * d/c.");
            M("модуль", "Модуль числа |a| — расстояние от a до нуля, всегда неотрицателен. |a| = a при a ≥ 0, |a| = -a при a < 0.");
            M("функция", "Функция y = f(x) ставит каждому x одно значение y. График линейной функции y = kx + b — прямая, k — угловой коэффициент. Парабола y = ax² + bx + c.");
            M("система уравнений", "Систему решают подстановкой (выразить одну переменную) или сложением (сложить уравнения, чтобы убрать переменную).");
            void M(string topic, string text)
            {
                _math[topic] = text;
            }
        }

        private void SeedRussian()
        {
            R("части речи", "Части речи: существительное (кто? что?), прилагательное (какой?), глагол (что делать?), числительное (сколько?), местоимение (кто, мой), наречие (как?), предлог, союз, частица, междометие. Отвечают на разные вопросы и по-разному изменяются.");
            R("падежи", "В русском языке 6 падежей: Именительный (кто? что?), Родительный (кого? чего?), Дательный (кому? чему?), Винительный (кого? что?), Творительный (кем? чем?), Предложный (о ком? о чём?).");
            R("склонение", "Существительные бывают 1-го склонения (ж.р. и м.р. на -а/-я), 2-го (м.р. без окончания, ср.р. на -о/-е), 3-го (ж.р. с ь на конце). У каждого склонения свои окончания в падежах.");
            R("спряжение", "Глаголы бывают I и II спряжения. Ко II спряжению относятся глаголы на -ить (кроме брить, стелить) и 11 исключений: гнать, держать, дышать, слышать, смотреть, видеть, ненавидеть, обидеть, терпеть, вертеть, зависеть. Остальные — I спряжение.");
            R("корень", "Корень — общая часть родственных слов, в которой заключён основной смысл. Однокоренные слова: лес, лесной, лесник.");
            R("приставка", "Приставка — часть слова перед корнем, образует новые слова: ходить → приходить, уходить.");
            R("суффикс", "Суффикс — часть слова после корня, образует новые слова или формы: дом → домик.");
            R("окончание", "Окончание — изменяемая часть слова, связывает слова в предложении и указывает на род, число, падеж.");
            R("причастный оборот", "Причастный оборот — причастие с зависимыми словами. Если он стоит ПОСЛЕ определяемого слова, выделяется запятыми: «Книга, лежащая на столе, интересная». Если ДО — запятая не ставится.");
            R("деепричастный оборот", "Деепричастный оборот всегда выделяется запятыми: «Он шёл, напевая песню». Деепричастие отвечает на вопрос «что делая?» и обозначает добавочное действие.");
            R("тире", "Тире ставится между подлежащим и сказуемым, если оба выражены существительным в именительном падеже: «Москва — столица». Также перед обобщающим словом и в неполных предложениях.");
            R("запятая", "Запятая ставится между однородными членами, перед союзами а, но, чтобы, потому что, при обособленных членах, в сложных предложениях.");
            R("приставки пре при", "Приставка ПРИ: приближение, присоединение, близость, неполное действие (приехать, приклеить, пришкольный, присесть). Приставка ПРЕ: значение «очень» или «пере-» (прекрасный, преградить).");
            R("о ё после шипящих", "После шипящих (ж, ш, ч, щ) под ударением в корне пишется Ё (если есть однокоренное слово с Е): жёлудь — желудёвый. В заимствованных словах и без такого слова пишется О: шов, шоколад.");
            R("н и нн", "НН пишется в прилагательных с суффиксами -онн-, -енн- (лекционный, соломенный) и в причастиях с приставкой или зависимым словом. Н пишется в прилагательных с суффиксами -ан-, -ян-, -ин- (кожаный, серебряный, гусиный), кроме стеклянный, оловянный, деревянный.");
            R("сложноподчинённое предложение", "Сложноподчинённое предложение (СПП) — сложное предложение, где одна часть зависит от другой. Соединяются союзами: что, чтобы, потому что, если, когда, который. Придаточная часть выделяется запятыми.");
            R("сложносочинённое предложение", "Сложносочинённое предложение (ССП) — части равноправны, соединяются союзами и, а, но, или. Перед союзом ставится запятая.");
            R("прямая речь", "Прямая речь — чужие слова, переданные дословно. Выделяется кавычками: Он сказал: «Я приду». Если слова автора после прямой речи: «Я приду», — сказал он.");
            R("гласные после ц", "После Ц в корне пишется И (цирк, цифра), кроме слов-исключений: цыган, цыплёнок, цыц, на цыпочках. В окончаниях после Ц пишется Ы (огурцы, молодцы).");
            R("безударные гласные", "Безударную гласную в корне проверяют ударением: вода — вОды, лесной — лЕс. Непроверяемые гласные проверяют по словарю.");
            R("парные согласные", "Парные согласные (б-п, в-ф, г-к, д-т, ж-ш, з-с) проверяют словом, где после согласной стоит гласная: дуб — дубы, снег — снега.");
            R("не с глаголами", "НЕ с глаголами пишется раздельно: не знаю, не хочу. Кроме глаголов, которые без НЕ не употребляются: ненавидеть, недомогать.");
            R("разделительный мягкий знак", "Разделительный Ь пишется после согласной перед е, ё, ю, я, и: семья, вьюга, соловьи. Разделительный Ъ пишется после приставок на согласную перед е, ё, ю, я: подъезд, объявление.");
            R("виды предложений", "По цели высказывания предложения бывают повествовательные, вопросительные, побудительные. По интонации — восклицательные и невосклицательные.");
            R("главные члены", "Главные члены предложения — подлежащее (кто? что?) и сказуемое (что делает?). Остальные члены — второстепенные: определение, дополнение, обстоятельство.");
            void R(string topic, string text)
            {
                _rus[topic] = text;
            }
        }

        private void SeedWords()
        {
            W("человек", "живое существо, обладающее разумом и речью.");
            W("люди", "множество людей; все жители, народ.");
            W("друг", "человек, с которым тебя связывают доверие и общие интересы.");
            W("семья", "близкие родственники, живущие вместе.");
            W("мама", "мать, самый близкий человек, родитель-женщина.");
            W("папа", "отец, родитель-мужчина.");
            W("ребёнок", "маленький человек, сын или дочь.");
            W("дом", "здание или место, где живут люди.");
            W("работа", "деятельность, за которую платят или которая приносит пользу.");
            W("школа", "место, где дети учатся.");
            W("книга", "набор страниц с текстом, который читают.");
            W("город", "большой населённый пункт с улицами и домами.");
            W("страна", "территория с государством, народом и границами.");
            W("мир", "всё, что нас окружает; также отсутствие войны.");
            W("жизнь", "существование живого существа от рождения до смерти.");
            W("время", "непрерывное течение событий — прошлое, настоящее, будущее.");
            W("слово", "единица речи, которая что-то называет или выражает.");
            W("язык", "система слов и правил, на которой говорят люди.");
            W("музыка", "искусство звуков, мелодий и ритма.");
            W("игра", "занятие ради удовольствия по определённым правилам.");
            W("вода", "прозрачная жидкость, без которой невозможна жизнь.");
            W("огонь", "пламя, выделяющее тепло и свет при горении.");
            W("земля", "почва под ногами; также планета, на которой мы живём.");
            W("воздух", "смесь газов вокруг нас, которой мы дышим.");
            W("солнце", "звезда, дающая Земле свет и тепло.");
            W("луна", "естественный спутник Земли, видимый ночью.");
            W("звезда", "светящееся небесное тело, видимое ночью.");
            W("небо", "пространство над землёй, где облака и солнце.");
            W("море", "большое солёное водное пространство.");
            W("река", "поток воды, текущий по земле к морю или озеру.");
            W("лес", "большая территория, заросшая деревьями.");
            W("гора", "высокая часть земной поверхности с крутыми склонами.");
            W("дерево", "растение с твёрдым стволом и ветвями.");
            W("цветок", "красивая часть растения, часто с приятным запахом.");
            W("трава", "низкое зелёное растение, покрывающее землю.");
            W("дождь", "капли воды, падающие с облаков.");
            W("снег", "белые кристаллы льда, падающие зимой с неба.");
            W("ветер", "движение воздуха.");
            W("облако", "скопление капель воды в небе.");
            W("гроза", "дождь с громом и молнией.");
            W("радуга", "разноцветная дуга в небе после дождя.");
            W("животное", "живой организм, который питается и двигается, но не растение.");
            W("птица", "животное с перьями и крыльями, обычно умеет летать.");
            W("рыба", "животное, живущее в воде и дышащее жабрами.");
            W("кошка", "домашнее животное, мурлычет и ловит мышей.");
            W("собака", "домашнее животное, верный друг человека.");
            W("любовь", "сильное тёплое чувство к человеку или делу.");
            W("счастье", "чувство радости и удовлетворения от жизни.");
            W("радость", "приятное чувство от хорошего события.");
            W("грусть", "чувство печали и тоски.");
            W("страх", "чувство тревоги перед опасностью.");
            W("надежда", "вера в то, что всё будет хорошо.");
            W("мечта", "сильное желание чего-то в будущем.");
            W("цель", "то, к чему человек стремится.");
            W("смысл", "то, ради чего что-то существует или делается.");
            W("правда", "то, что соответствует действительности.");
            W("ложь", "намеренное искажение истины.");
            W("добро", "хорошие поступки, помощь и забота о других.");
            W("зло", "плохие поступки, вред другим.");
            W("красота", "то, что вызывает восхищение и приятное чувство.");
            W("сила", "способность действовать, преодолевать сопротивление.");
            W("свобода", "возможность действовать по своей воле.");
            W("знание", "понимание и информация, которые человек получил.");
            W("ум", "способность думать, понимать и рассуждать.");
            W("память", "способность помнить прошлое и события.");
            W("мысль", "то, что человек думает в голове.");
            W("вопрос", "предложение, на которое ждут ответа.");
            W("ответ", "то, что говорят на вопрос.");
            W("хлеб", "главная еда из муки, которую пекут.");
            W("еда", "то, что человек ест для питания.");
            W("чай", "горячий напиток, который заваривают из листьев.");
            W("молоко", "белая питательная жидкость, которую дают коровы.");
            W("яблоко", "круглый сочный плод яблони.");
            W("стол", "мебель с плоской поверхностью и ножками.");
            W("стул", "мебель для сидения с сиденьем и спинкой.");
            W("окно", "проём в стене со стеклом для света.");
            W("дверь", "створка, которой закрывают вход.");
            W("дорога", "путь, по которому ходят или ездят.");
            W("машина", "транспорт с мотором для перевозки людей.");
            W("телефон", "устройство для связи на расстоянии.");
            W("компьютер", "машина для обработки информации и программ.");
            W("деньги", "средство для покупки товаров и услуг.");
            W("часы", "прибор для измерения времени.");
            W("письмо", "сообщение, написанное человеку.");
            W("шум", "громкие беспорядочные звуки.");
            W("цвет", "свойство предмета, например красный или синий.");
            W("тепло", "ощущение от горячего; также уют и забота.");
            W("холод", "низкая температура, ощущение от мороза.");
            W("свет", "то, что позволяет видеть; сияние.");
            W("тень", "тёмное место, куда не попадает свет.");
            W("урок", "занятие в школе, где учат новому.");
            W("учёба", "процесс получения знаний.");
            W("учитель", "человек, который учит других.");
            W("ученик", "тот, кто учится.");
            W("задача", "вопрос, который нужно решить.");
            W("число", "величина для счёта, например 1, 2, 3.");
            W("буква", "знак алфавита, из которых состоят слова.");
            W("история", "наука о прошлом; также рассказ о событиях.");
            W("наука", "система знаний, полученных через изучение мира.");
            W("улица", "пространство между домами в городе, где ходят люди.");
            W("бабушка", "мать отца или матери.");
            W("дедушка", "отец отца или матери.");
            W("брат", "сын тех же родителей, что и ты, мужского пола.");
            W("сестра", "дочь тех же родителей, что и ты, женского пола.");
            W("сын", "ребёнок мужского пола по отношению к родителям.");
            W("дочь", "ребёнок женского пола по отношению к родителям.");
            W("сосед", "человек, который живёт рядом.");
            W("гость", "тот, кто пришёл в чужой дом по приглашению.");
            W("врач", "человек, который лечит больных.");
            W("водитель", "человек, который управляет транспортом.");
            W("продавец", "человек, который продаёт товары в магазине.");
            W("повар", "человек, который готовит еду.");
            W("художник", "человек, который рисует картины.");
            W("писатель", "человек, который пишет книги.");
            W("музыкант", "человек, который играет музыку.");
            W("озеро", "замкнутый водоём с пресной водой.");
            W("поле", "открытое ровное пространство, обычно засеянное.");
            W("берег", "край земли у воды.");
            W("остров", "участок земли, окружённый водой.");
            W("пустыня", "большая засушливая территория с песком.");
            W("мороз", "сильный холод, отрицательная температура.");
            W("жара", "очень высокая температура воздуха.");
            W("туман", "облако у самой земли, ухудшающее видимость.");
            W("молния", "яркая электрическая вспышка во время грозы.");
            W("гром", "громкий звук после молнии.");
            W("лёд", "замёрзшая вода, твёрдая и холодная.");
            W("камень", "твёрдый кусок горной породы.");
            W("песок", "мелкие сыпучие крупинки горных пород.");
            W("гриб", "живой организм, часто растущий в лесу; бывает съедобным.");
            W("ягода", "небольшой сочный плод кустарника или травы.");
            W("лист", "зелёная плоская часть растения.");
            W("корень", "подземная часть растения, которая его держит и кормит.");
            W("злость", "чувство раздражения и гнева.");
            W("спокойствие", "состояние тишины и душевного равновесия.");
            W("усталость", "чувство, когда хочется отдохнуть.");
            W("смелость", "способность не бояться и действовать.");
            W("терпение", "умение спокойно ждать и выдерживать трудности.");
            W("внимание", "способность замечать и сосредотачиваться.");
            W("уважение", "признание достоинств другого человека.");
            W("доверие", "вера в честность и надёжность другого.");
            W("дружба", "близкие отношения, основанные на доверии.");
            W("вера", "уверенность в чём-то без доказательств.");
            W("воля", "способность добиваться своей цели.");
            W("совесть", "внутреннее чувство того, что хорошо, а что плохо.");
            W("характер", "совокупность черт, определяющих поведение человека.");
            W("настроение", "эмоциональное состояние в данный момент.");
            W("бежать", "двигаться быстро, ногами.");
            W("идти", "двигаться пешком.");
            W("прыгать", "отталкиваться от земли и лететь в воздух.");
            W("летать", "двигаться по воздуху.");
            W("плавать", "двигаться в воде.");
            W("есть", "принимать пищу.");
            W("пить", "принимать жидкость.");
            W("спать", "отдыхать, находясь в состоянии сна.");
            W("проснуться", "перестать спать.");
            W("говорить", "произносить слова и выражать мысли.");
            W("слушать", "воспринимать звуки и речь.");
            W("видеть", "воспринимать мир глазами.");
            W("думать", "размышлять и рассуждать.");
            W("читать", "воспринимать написанный текст.");
            W("писать", "излагать мысли буквами.");
            W("рисовать", "создавать изображение карандашом или красками.");
            W("строить", "создавать здание или сооружение.");
            W("ломать", "разрушать или портить.");
            W("делать", "совершать какое-то действие.");
            W("помогать", "оказывать помощь другому.");
            W("ждать", "пребывать в ожидании чего-то.");
            W("искать", "стараться найти что-то.");
            W("находить", "обнаруживать что-то потерянное или новое.");
            W("терять", "переставать иметь что-то.");
            W("побеждать", "одерживать верх в игре или борьбе.");
            W("проигрывать", "терпеть поражение.");
            W("смеяться", "выражать веселье смехом.");
            W("плакать", "выражать слёзы от горя или боли.");
            W("ручка", "предмет, которым пишут.");
            W("карандаш", "инструмент для рисования и письма.");
            W("тетрадь", "сшитые листы для записей.");
            W("бумага", "тонкий материал, на котором пишут и печатают.");
            W("стена", "вертикальная часть здания.");
            W("пол", "нижняя поверхность помещения, по которой ходят.");
            W("крыша", "верхняя часть дома, которая закрывает его от дождя.");
            W("ключ", "предмет, которым открывают замок.");
            W("сумка", "вещь с ручками, в которой носят предметы.");
            W("одежда", "то, что человек носит на теле.");
            W("рубашка", "верхняя одежда с воротником и рукавами.");
            W("обувь", "то, что носят на ногах.");
            W("посуда", "предметы для еды и приготовления пищи.");
            W("ложка", "столовый прибор для еды жидкой пищи.");
            W("тарелка", "плоская посуда, из которой едят.");
            W("кружка", "посуда для питья с ручкой.");
            W("кровать", "мебель для сна.");
            W("зеркало", "гладкая поверхность, в которой видно отражение.");
            W("лампа", "прибор, дающий свет.");
            W("игрушка", "предмет для игры, особенно для детей.");
            W("мяч", "круглый предмет для игры.");
            W("велосипед", "двухколёсный транспорт, на котором едут, крутя педали.");
            W("поезд", "транспорт, который едет по рельсам.");
            W("самолёт", "транспорт, который летает по воздуху.");
            W("корабль", "большое судно, которое плавает по воде.");
            W("пример", "задание с числами, которое нужно решить.");
            W("задание", "то, что нужно выполнить.");
            W("ошибка", "неправильное действие или неверный ответ.");
            W("правило", "установленный порядок или закономерность.");
            W("решение", "ответ на задачу или выбор выхода.");
            W("техника", "машины и устройства, созданные человеком.");
            W("робот", "машина, которая выполняет действия автоматически.");
            W("программа", "набор команд для компьютера.");
            W("интернет", "всемирная сеть компьютеров для обмена информацией.");
            W("сайт", "страница или набор страниц в интернете.");
            W("файл", "запись данных на компьютере.");
            W("мультфильм", "анимационный фильм для детей и взрослых.");
            W("фильм", "видеозапись с сюжетом.");
            W("песня", "музыкальное произведение со словами.");
            W("день", "часть суток от утра до вечера; также единица времени.");
            W("ночь", "тёмная часть суток, когда обычно спят.");
            W("утро", "начало дня.");
            W("вечер", "конец дня перед ночью.");
            W("неделя", "семь дней подряд.");
            W("месяц", "часть года из 28–31 дня.");
            W("год", "период из 12 месяцев.");
            W("час", "единица времени, 60 минут.");
            W("минута", "единица времени, 60 секунд.");
            W("секунда", "самая маленькая обычная единица времени.");
            W("вчера", "день перед сегодняшним.");
            W("завтра", "день после сегодняшнего.");
            W("сегодня", "текущий день.");
            W("ноль", "число, означающее пустоту, 0.");
            W("один", "первое число, 1.");
            W("два", "число после одного, 2.");
            W("три", "число после двух, 3.");
            W("сто", "число, состоящее из 100 единиц.");
            W("тысяча", "число, состоящее из 1000 единиц.");
            W("суп", "жидкое блюдо, которое варят из овощей и мяса.");
            W("каша", "блюдо из крупы, сваренной в воде или молоке.");
            W("мясо", "продукт питания из мышц животных.");
            W("овощ", "съедобная часть растения, например морковь или картофель.");
            W("фрукт", "сочный плод дерева или куста, который едят.");
            W("картофель", "овощ, из которого делают пюре и жарят.");
            W("морковь", "оранжевый корнеплод, богатый витаминами.");
            W("помидор", "красный сочный овощ круглой формы.");
            W("огурец", "зелёный овощ продолговатой формы.");
            W("банан", "жёлтый сладкий фрукт продолговатой формы.");
            W("апельсин", "оранжевый цитрусовый фрукт.");
            W("сахар", "сладкое вещество, которое добавляют в еду.");
            W("соль", "белое вещество, которым солят еду.");
            W("масло", "жирное вещество для готовки и на хлеб.");
            W("сыр", "молочный продукт жёлтого цвета.");
            W("торт", "сладкое праздничное изделие из теста и крема.");
            W("конфета", "маленькое сладкое лакомство.");
            W("мороженое", "холодное сладкое лакомство из молока.");
            W("кофе", "горячий бодрящий напиток из зёрен.");
            W("сок", "напиток, выжатый из фруктов или овощей.");
            W("лошадь", "крупное домашнее животное, на котором ездят.");
            W("корова", "домашнее животное, которое даёт молоко.");
            W("свинья", "домашнее животное с пятачком.");
            W("курица", "домашняя птица, которая несёт яйца.");
            W("тигр", "крупный полосатый хищник семейства кошачьих.");
            W("лев", "крупный хищник с гривой, «царь зверей».");
            W("волк", "дикий хищник, живёт стаями.");
            W("лиса", "рыжий хищник с пушистым хвостом.");
            W("медведь", "крупный сильный зверь, любит мёд.");
            W("заяц", "зверёк с длинными ушами, быстро бегает.");
            W("слон", "самое крупное наземное животное с хоботом.");
            W("обезьяна", "животное, близкое к человеку, живёт на деревьях.");
            W("змея", "пресмыкающееся без ног, ползает.");
            W("бабочка", "насекомое с яркими крыльями.");
            W("пчела", "насекомое, которое делает мёд.");
            W("муравей", "маленькое насекомое, живёт в муравейнике.");
            W("паук", "существо с восемью ногами, плетёт паутину.");
            W("кит", "огромное морское животное.");
            W("акула", "крупная хищная рыба.");
            W("дельфин", "умное морское животное, дышит воздухом.");
            W("инженер", "специалист, который создаёт технику и проекты.");
            W("программист", "человек, который пишет компьютерные программы.");
            W("учёный", "человек, который изучает мир через науку.");
            W("строитель", "тот, кто строит дома и здания.");
            W("военный", "человек на службе в армии.");
            W("полицейский", "сотрудник, который следит за порядком.");
            W("пожарный", "тот, кто тушит пожары и спасает людей.");
            W("актёр", "человек, который играет роли в театре и кино.");
            W("певец", "человек, который поёт песни.");
            W("танцор", "человек, который танцует.");
            W("фотограф", "тот, кто делает фотографии.");
            W("журналист", "человек, который собирает и публикует новости.");
            W("переводчик", "тот, кто переводит с одного языка на другой.");
            W("математик", "учёный, который занимается числами и формулами.");
            W("экран", "поверхность, на которой видно изображение.");
            W("клавиатура", "устройство с кнопками для набора текста.");
            W("мышь", "устройство для управления курсором; также животное.");
            W("ноутбук", "переносной компьютер.");
            W("планшет", "плоский компьютер с сенсорным экраном.");
            W("наушники", "устройство для прослушивания звука.");
            W("камера", "устройство для съёмки фото и видео.");
            W("телевизор", "устройство для просмотра передач.");
            W("мотор", "двигатель, который приводит что-то в движение.");
            W("колесо", "круглая деталь, на которой едет транспорт.");
            W("ракета", "транспорт для полётов в космос.");
            W("спутник", "аппарат, который летает вокруг планеты.");
            W("батарея", "источник энергии для устройств.");
            W("провод", "длинная тонкая нить для передачи тока.");
            W("зарядка", "устройство для пополнения энергии батареи.");
            W("спорт", "физические упражнения и игры для здоровья.");
            W("футбол", "игра с мячом, где забивают голы ногами.");
            W("хоккей", "игра с шайбой и клюшками на льду.");
            W("баскетбол", "игра, где забрасывают мяч в корзину.");
            W("плавание", "спорт — движение в воде.");
            W("бег", "быстрое передвижение на ногах.");
            W("прыжок", "отталкивание от земли в воздух.");
            W("тренировка", "занятие для развития силы и навыков.");
            W("победа", "успех в соревновании.");
            W("медаль", "награда за победу.");
            W("команда", "группа людей, играющих вместе.");
            W("соревнование", "борьба за первое место.");
            W("физика", "наука о законах природы, движении и энергии.");
            W("химия", "наука о веществах и их превращениях.");
            W("биология", "наука о живых организмах.");
            W("география", "наука о Земле, странах и природе.");
            W("астрономия", "наука о звёздах и космосе.");
            W("энергия", "способность совершать работу.");
            W("атом", "мельчайшая частица вещества.");
            W("молекула", "соединение атомов.");
            W("скорость", "быстрота движения.");
            W("масса", "количество вещества в теле.");
            W("температура", "мера нагретости тела.");
            W("электричество", "движение зарядов, даёт свет и энергию.");
            W("гравитация", "сила, которая притягивает тела к Земле.");
            W("эксперимент", "опыт для проверки идеи.");
            W("материк", "огромный участок суши, например Африка.");
            W("океан", "самое большое водное пространство.");
            W("континент", "то же, что материк.");
            W("равнина", "ровная поверхность земли.");
            W("вулкан", "гора, из которой извергается лава.");
            W("землетрясение", "колебание земли из-за движения пластов.");
            W("климат", "обычная погода в местности за годы.");
            W("полюс", "крайняя точка Земли — Северный или Южный.");
            W("экватор", "линия, делящая Землю на два полушария.");
            W("компас", "прибор, который показывает север.");
            W("карта", "изображение местности сверху.");
            W("граница", "линия, разделяющая страны.");
            W("столица", "главный город страны.");
            W("деревня", "небольшое поселение в сельской местности.");
            W("скука", "чувство, когда нечем заняться.");
            W("интерес", "внимание к чему-то новому.");
            W("удивление", "реакция на неожиданное.");
            W("восхищение", "сильное приятное удивление.");
            W("гордость", "чувство удовольствия от своих достижений.");
            W("стыд", "неприятное чувство от своей ошибки.");
            W("вина", "чувство ответственности за проступок.");
            W("забота", "внимание и помощь другому.");
            W("нежность", "мягкое тёплое чувство.");
            W("ревность", "боязнь потерять чьё-то внимание.");
            W("одиночество", "состояние, когда рядом никого нет.");
            W("поддержка", "помощь и одобрение другого.");
            W("обида", "чувство от несправедливости.");
            W("прощение", "отказ от обиды.");
            W("открывать", "делать доступным, убирать преграду.");
            W("закрывать", "преграждать доступ.");
            W("начинать", "делать первое действие.");
            W("заканчивать", "доводить до конца.");
            W("продолжать", "не останавливаться, делать дальше.");
            W("выбирать", "решать между вариантами.");
            W("сравнивать", "находить сходства и различия.");
            W("измерять", "определять размер или количество.");
            W("считать", "определять количество или вычислять.");
            W("учить", "передавать знания другому.");
            W("учиться", "получать знания.");
            W("объяснять", "делать понятным.");
            W("понимать", "усваивать смысл.");
            W("запоминать", "сохранять в памяти.");
            W("вспоминать", "доставать из памяти.");
            W("мечтать", "представлять желаемое.");
            W("верить", "быть уверенным без доказательств.");
            W("надеяться", "ждать хорошего.");
            W("бояться", "испытывать страх.");
            W("радоваться", "испытывать радость.");
            W("грустить", "испытывать печаль.");
            W("удивляться", "реагировать на неожиданное.");
            W("благодарить", "выражать признательность.");
            W("извиняться", "просить прощения.");
            W("здороваться", "приветствовать при встрече.");
            W("прощаться", "говорить пока при уходе.");
            W("куртка", "верхняя одежда для тепла.");
            W("пальто", "длинная тёплая одежда на улицу.");
            W("брюки", "одежда для ног.");
            W("джинсы", "прочные брюки из джинсовой ткани.");
            W("платье", "женская одежда целиком.");
            W("юбка", "женская одежда ниже пояса.");
            W("шапка", "тёплый головной убор.");
            W("шарф", "длинная тёплая полоса на шею.");
            W("перчатки", "одежда для рук.");
            W("носки", "мягкая одежда для стоп.");
            W("ботинки", "тёплая обувь на ноги.");
            W("кроссовки", "удобная спортивная обувь.");
            W("сапоги", "высокая обувь для дождя и снега.");
            W("ремень", "полоса, которой подпоясывают одежду.");
            W("карман", "отделение в одежде для мелочей.");
            W("квартира", "жильё в многоквартирном доме.");
            W("комната", "отдельное помещение в доме.");
            W("кухня", "комната, где готовят еду.");
            W("ванная", "комната, где моются.");
            W("коридор", "проход, соединяющий комнаты.");
            W("балкон", "площадка снаружи дома.");
            W("лестница", "ступени для подъёма на этаж.");
            W("этаж", "один уровень здания.");
            W("потолок", "верхняя часть комнаты.");
            W("мебель", "столы, стулья, шкафы и другая обстановка.");
            W("шкаф", "мебель для хранения вещей.");
            W("диван", "мягкая мебель для сидения и сна.");
            W("ковёр", "мягкое покрытие на пол.");
            W("подушка", "мягкая вещь для головы во сне.");
            W("одеяло", "тёплое покрывало для сна.");
            W("выключатель", "кнопка для включения света.");
            W("кран", "устройство, из которого течёт вода.");
            W("холодильник", "шкаф для хранения еды на холоде.");
            W("плита", "устройство для приготовления еды.");
            W("микроволновка", "печь для быстрого разогрева еды.");
            W("пылесос", "прибор для уборки пыли.");
            W("утюг", "прибор для глажки одежды.");
            W("шахматы", "настольная игра с фигурами на доске.");
            W("шашки", "игра с круглыми фишками на доске.");
            W("карты", "набор для карточных игр.");
            W("пазл", "картинка, собранная из кусочков.");
            W("конструктор", "набор для сборки моделей.");
            W("кукла", "игрушка в виде человека.");
            W("машинка", "игрушечный автомобиль.");
            W("качели", "сиденье, на котором качаются.");
            W("горка", "наклонная поверхность для катания.");
            W("парк", "место с деревьями и дорожками для прогулок.");
            W("площадка", "место для игр детей.");
            W("зоопарк", "место, где показывают животных.");
            W("цирк", "место с артистами, клоунами и животными.");
            W("аттракцион", "развлечение на колёсах или с движением.");
            W("ветка", "тонкая часть дерева.");
            W("пень", "оставшаяся нижняя часть срубленного дерева.");
            W("куст", "растение с несколькими ветками от земли.");
            W("семечко", "маленькое зерно, из которого растёт растение.");
            W("росток", "молодое маленькое растение.");
            W("склон", "наклонная поверхность горы или холма.");
            W("холм", "невысокая гора.");
            W("овраг", "глубокая узкая впадина в земле.");
            W("болото", "топкое место с водой и травой.");
            W("водопад", "поток воды, падающий с высоты.");
            W("родник", "источник чистой воды из земли.");
            W("глина", "мягкая влажная земля, из которой лепят.");
            W("гранит", "твёрдый камень из зёрен.");
            W("железо", "прочный металл.");
            W("золото", "дорогой жёлтый металл.");
            W("серебро", "блестящий металл белого цвета.");
            W("голова", "верхняя часть тела, где мозг и лицо.");
            W("рука", "часть тела для хватания.");
            W("нога", "часть тела для ходьбы.");
            W("палец", "тонкая часть на руке или ноге.");
            W("глаз", "орган зрения.");
            W("ухо", "орган слуха.");
            W("нос", "орган обоняния и дыхания.");
            W("рот", "орган для еды и речи.");
            W("зуб", "твёрдая часть во рту для жевания.");
            W("волосы", "растут на голове и теле.");
            W("кожа", "наружный покров тела.");
            W("сердце", "орган, который гонит кровь.");
            W("мозг", "орган мышления.");
            W("кость", "твёрдая часть скелета.");
            W("кровь", "красная жидкость, текущая в теле.");
            W("дыхание", "вдох и выдох воздуха.");
            W("сон", "состояние отдыха ночью.");
            W("здоровье", "хорошее состояние организма.");
            W("болезнь", "нарушение здоровья.");
            W("лекарство", "средство для лечения.");
            W("восторг", "очень сильная радость.");
            W("тревога", "беспокойство о будущем.");
            W("печаль", "грусть по утрате.");
            W("тоска", "сильная грусть без причины.");
            W("уныние", "подавленное настроение.");
            W("раздражение", "недовольство мелочами.");
            W("гнев", "сильная злость.");
            W("ярость", "очень сильный гнев.");
            W("отвращение", "чувство неприязни.");
            W("зависть", "желание чужого.");
            W("презрение", "чувство превосходства над кем-то.");
            W("благодарность", "чувство признательности.");
            W("умиротворение", "спокойствие и покой.");
            W("рассвет", "начало утра, когда встаёт солнце.");
            W("закат", "конец дня, когда садится солнце.");
            W("затмение", "когда одно небесное тело закрывает другое.");
            W("наводнение", "затопление земли водой.");
            W("ураган", "очень сильный ветер.");
            W("смерч", "вихрь в виде столба.");
            W("гололёд", "скользкий лёд на дороге.");
            W("иней", "тонкий слой льда на предметах.");
            W("роса", "капли воды на траве утром.");
            W("туча", "большое тёмное облако.");
            W("добрый", "отзывчивый, желающий добра.");
            W("злой", "причиняющий вред, недобрый.");
            W("умный", "сообразительный, понимающий.");
            W("глупый", "неумный, поступающий неразумно.");
            W("ленивый", "не желающий работать.");
            W("трудолюбивый", "любящий и умеющий работать.");
            W("честный", "говорящий правду.");
            W("смелый", "не боящийся опасности.");
            W("трусливый", "легко пугающийся.");
            W("щедрый", "охотно делящийся с другими.");
            W("жадный", "не желающий делиться.");
            W("весёлый", "находящий радость, смеющийся.");
            W("грустный", "печальный.");
            W("спокойный", "тихий, уравновешенный.");
            W("внимательный", "замечающий детали, заботливый.");
            W("ответственный", "выполняющий обещанное.");
            W("упрямый", "не желающий менять мнение.");
            W("скромный", "не хвастающийся.");
            W("класс", "комната для занятий или группа учеников.");
            W("перемена", "перерыв между уроками.");
            W("каникулы", "перерыв в учёбе для отдыха.");
            W("экзамен", "проверка знаний.");
            W("оценка", "балл за работу.");
            W("дневник", "тетрадь с оценками и заданиями.");
            W("пенал", "коробка для ручек и карандашей.");
            W("доска", "поверхность в классе, на которой пишут.");
            W("парта", "стол для ученика.");
            W("директор", "руководитель школы.");
            W("одноклассник", "ученик из твоего класса.");
            W("домашнее задание", "работа, которую задают на дом.");
            W("театр", "место, где показывают спектакли.");
            W("музей", "место, где хранят и показывают ценные вещи.");
            W("картина", "изображение, нарисованное красками.");
            W("стихотворение", "текст в рифму.");
            W("сказка", "выдуманный рассказ с добром и злом.");
            W("рассказ", "небольшое произведение о событиях.");
            W("роман", "большая книга о жизни героев.");
            W("танец", "движения под музыку.");
            W("спектакль", "представление в театре.");
            W("концерт", "выступление музыкантов для зрителей.");
            W("праздник", "радостный день с торжеством.");
            W("традиция", "обычай, который повторяют.");
            void W(string word, string def)
            {
                _words[word] = def;
            }
        }

        private static double Similarity(string a, string b)
        {
            HashSet<string> hashSet = Tokenize(a);
            HashSet<string> wb = Tokenize(b);
            if (hashSet.Count == 0 || wb.Count == 0)
            {
                return 0.0;
            }
            int num = hashSet.Count((string w) => wb.Contains(w));
            if (num == 0)
            {
                return 0.0;
            }
            return (double)num / (double)hashSet.Union(wb).Count();
        }

        private static readonly string[] Suffixes = new string[32]
        {
            "ость", "есть", "ние", "тие", "ение", "ание", "ение", "ация", "изация", "ист",
            "изм", "тель", "ник", "щик", "чик", "ок", "ик", "ек", "ец", "иц",
            "ость", "ов", "ев", "ин", "ан", "ян", "енн", "онн", "лив", "чив",
            "оват", "еват"
        };

        private List<string> SuggestTopics()
        {
            List<string> list = new List<string>();
            lock (_dictLock)
            {
                list.AddRange(_math.Keys.Take(3));
                list.AddRange(_rus.Keys.Take(3));
                list.AddRange(_words.Keys.Take(3));
            }
            List<string> list2 = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                if (list.Count <= 0)
                {
                    break;
                }
                int index = _rng.Next(list.Count);
                list2.Add(list[index]);
                list.RemoveAt(index);
            }
            return list2;
        }

        private static HashSet<string> Tokenize(string text)
        {
            HashSet<string> hashSet = new HashSet<string>();
            StringBuilder stringBuilder = new StringBuilder();
            string text2 = text.ToLowerInvariant();
            foreach (char c in text2)
            {
                if (char.IsLetterOrDigit(c))
                {
                    stringBuilder.Append(c);
                }
                else if (stringBuilder.Length > 0)
                {
                    string text3 = stringBuilder.ToString();
                    stringBuilder.Clear();
                    if (text3.Length > 1 && !StopWords.Contains(text3))
                    {
                        hashSet.Add(text3);
                    }
                }
            }
            if (stringBuilder.Length > 0)
            {
                string text4 = stringBuilder.ToString();
                if (text4.Length > 1 && !StopWords.Contains(text4))
                {
                    hashSet.Add(text4);
                }
            }
            return hashSet;
        }

        private bool TryAutoLearnFromContext(string input, string lower, out string reply)
        {
            reply = "";
            if (!lower.StartsWith("а как") && !lower.StartsWith("а что"))
            {
                return false;
            }
            if (_history.Count < 2)
            {
                return false;
            }
            List<DialogueTurn> history = _history;
            DialogueTurn dialogueTurn = history[history.Count - 1];
            if (dialogueTurn.Bot.Length < 3)
            {
                return false;
            }
            if (!dialogueTurn.User.Contains('?'))
            {
                return false;
            }
            string q = dialogueTurn.User.Trim().TrimEnd('?').ToLowerInvariant();
            bool known;
            lock (_knowledgeLock) { known = _knowledge.Any((MemoryEntry k) => string.Equals(k.Question, q, StringComparison.OrdinalIgnoreCase)); }
            if (known)
            {
                return false;
            }
            lock (_knowledgeLock)
            {
                _knowledge.Add(new MemoryEntry
                {
                    Question = q,
                    Answer = dialogueTurn.Bot,
                    Learned = true
                });
            }
            SaveBrain();
            reply = "Хорошая мысль — я запомнил, что на «" + q + "» надо отвечать: " + dialogueTurn.Bot;
            return true;
        }

        private bool TryClarifyAmbiguity(string lower, out string reply)
        {
            reply = "";
            if (_clarifiedWord.Length > 0 && _clarifiedMeaning.Length == 0 && int.TryParse(lower.Trim(new char[3] { ' ', '.', '!' }), out var result) && Ambiguous.TryGetValue(_clarifiedWord, out string[]? value) && result >= 1 && result <= value.Length)
            {
                _clarifiedMeaning = value[result - 1];
                string key = _clarifiedWord + " — " + value[result - 1];
                reply = "Понял: «" + _clarifiedWord + " — " + value[result - 1] + "».";
                string? value2 = null;
                lock (_dictLock)
                {
                    _words.TryGetValue(key, out value2);
                    if (value2 == null)
                    {
                        _words.TryGetValue(_clarifiedWord, out value2);
                    }
                }
                if (value2 != null)
                {
                    reply = reply + "\n" + value2;
                }
                _clarifiedWord = "";
                return true;
            }
            string text = lower.Trim(new char[4] { '?', '.', ' ', '!' });
            if (!Ambiguous.TryGetValue(text, out string[]? value3))
            {
                return false;
            }
            if (_clarifiedMeaning.Length > 0 && _clarifiedWord == text)
            {
                reply = "Ты уже выбрал смысл: «" + text + " — " + _clarifiedMeaning + "». Спроси конкретнее.";
                return true;
            }
            reply = "Слово «" + text + "» неоднозначное. Что ты имеешь в виду?\n";
            for (int i = 0; i < value3.Length; i++)
            {
                reply = reply + "  " + (i + 1) + ") " + value3[i] + "\n";
            }
            reply += "Ответь цифрой или уточни вопрос.";
            _clarifiedWord = text;
            return true;
        }

        private bool TryContinue(string lower, out string reply)
        {
            reply = "";
            switch (lower)
            {
            default:
                if (!(lower == "дальше"))
                {
                    return false;
                }
                break;
            case "а ещё":
            case "а еще":
            case "ещё":
            case "еще":
            case "и что":
            case "и что дальше":
            case "продолжи":
            case "расскажи ещё":
            case "расскажи еще":
                break;
            }
            if (string.IsNullOrEmpty(_lastTopic))
            {
                reply = "Продолжить нечего — о чём мы говорили? Назови тему.";
                return true;
            }
            List<KeyValuePair<string, string>> topicSnapshot;
            lock (_dictLock) { topicSnapshot = _words.ToList(); }
            foreach (KeyValuePair<string, string> word in topicSnapshot)
            {
                if (word.Key != _lastTopic && word.Key.Contains(_lastTopic) && word.Value.Length > 30)
                {
                    reply = "Ещё про «" + word.Key + "»:\n" + word.Value;
                    _lastTopic = word.Key;
                    return true;
                }
            }
            reply = "Больше про «" + _lastTopic + "» у меня нет. Спроси иначе или научи: «выучи: ...».";
            return true;
        }

        private bool TryDeepAnalyze(string input, string lower, out string reply)
        {
            reply = "";
            if (!lower.Contains("подумай") && !lower.Contains("проанализируй") && !lower.Contains("подробно") && !lower.Contains("развёрнуто") && !lower.Contains("развернуто") && !lower.Contains("расскажи всё") && !lower.Contains("расскажи все") && !lower.Contains("исследуй") && !lower.Contains("глубоко") && !lower.Contains("детально"))
            {
                return false;
            }
            string query = lower;
            string[] array = new string[14]
            {
                "подумай", "проанализируй", "подробно", "развёрнуто", "развернуто", "расскажи всё о", "расскажи все о", "расскажи всё про", "расскажи все про", "исследуй",
                "глубоко", "детально", "расскажи", "про"
            };
            foreach (string text in array)
            {
                int num = query.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                if (num >= 0 && num < 12)
                {
                    query = query.Remove(num, text.Length).Trim();
                }
            }
            query = query.Trim(new char[7] { '?', '.', '!', ' ', ':', '«', '»' });
            if (query.Length < 2)
            {
                return false;
            }
            List<string> list = new List<string>();
            List<string> list2 = new List<string>();
            List<(string key, string text, double score)> hits = new List<(string, string, double)>();
            ScanDict(_words, "Словарь");
            ScanDict(_math, "Математика");
            ScanDict(_rus, "Русский язык");
            foreach (MemoryEntry item in _knowledge)
            {
                double num2 = Similarity(query, item.Question);
                if (item.Question.Contains(query) || query.Contains(item.Question))
                {
                    num2 += 0.4;
                }
                if (num2 >= 0.4)
                {
                    hits.Add(("Из разговоров — " + item.Question, item.Answer, num2));
                }
            }
            foreach (DialogueTurn item2 in _history)
            {
                if (item2.User.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 && item2.Bot.Length > 20)
                {
                    hits.Add(("Раньше говорили — " + item2.User, item2.Bot, 0.35));
                    if (hits.Count > 200)
                    {
                        break;
                    }
                }
            }
            string? ownView = _own.Generate(query, 1500);
            hits.Sort(((string key, string text, double score) a, (string key, string text, double score) b) => b.score.CompareTo(a.score));
            int num3 = 0;
            foreach (var item3 in hits)
            {
                if (num3 >= 4)
                {
                    break;
                }
                if (!list2.Contains(item3.key))
                {
                    list2.Add(item3.key);
                    list.Add("▸ " + item3.key + ":\n" + item3.text.Trim());
                    num3++;
                }
            }
            if (num3 < 3)
            {
                Out.Learn("[\ud83e\udde0 Глубокий анализ] Запускаю многопоточный поиск...");
                List<Task<string?>> list3 = new List<Task<string?>>();
                list3.Add(Task.Run(() => SearchEntireInternet(query)));
                list3.Add(Task.Run(() => FetchWikipedia(query)));
                list3.Add(Task.Run(() => FetchHabr(query)));
                list3.Add(Task.Run(() => FetchStackOverflow(query)));
                list3.Add(Task.Run(() => FetchReddit(query)));
                list3.Add(Task.Run(() => FetchYouTube(query)));
                list3.Add(Task.Run(() => FetchNPlusOne(query)));
                list3.Add(Task.Run(() => FetchWikidata(query)));
                try
                {
                    Task[] tasks = list3.ToArray();
                    Task.WaitAll(tasks, 8000);
                }
                catch
                {
                }
                List<string> list4 = new List<string>();
                foreach (Task<string?> item4 in list3)
                {
                    string? result = (item4.IsCompletedSuccessfully) ? item4.Result : null;
                    if (!string.IsNullOrEmpty(result) && !result.Contains("не нашёл") && !result.Contains("Не удалось") && result.Length > 50)
                    {
                        list4.Add(result);
                    }
                }
                if (list4.Count > 0)
                {
                    string text2 = string.Join("\n\n", list4);
                    text2 = TranslateToRussian(text2);
                    if (text2.Length > 20000)
                    {
                        text2 = text2.Substring(0, 20000) + "…";
                    }
                    list.Add("\ud83c\udf10 Данные из интернета (анализ нескольких источников):\n" + text2);
                }
            }
            if (ownView != null && ownView.Split(' ').Length >= 5 && !list.Any((string x) => x.Contains(ownView)))
            {
                list.Add("▸ Вывод (глубокий анализ нейросети):\n" + ownView);
            }
            if (list.Count == 0 && TryMorphology(query, out string reply2))
            {
                list.Add("▸ Разбор:\n" + reply2);
            }
            if (list.Count == 0)
            {
                reply = "Я искал по всем источникам — словарь, школьные базы, разговоры, своя модель, Википедия — но по «" + query + "» ничего не нашёл.\nМогу научиться: «выучи: " + query + " = твой ответ».";
                return true;
            }
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("\ud83d\udcca **Подробный анализ по теме:** «" + query + "»\n");
            List<string> list5 = list.Where((string s) => s.Length < 800).ToList();
            if (list5.Count > 0)
            {
                stringBuilder.AppendLine("\ud83d\udccc **Краткая суть:**");
                stringBuilder.AppendLine(string.Join("\n", list5));
                stringBuilder.AppendLine();
            }
            List<string> list6 = list.Where((string s) => s.Length >= 800).ToList();
            if (list6.Count > 0)
            {
                stringBuilder.AppendLine("\ud83d\udcd6 **Подробности:**");
                stringBuilder.AppendLine(string.Join("\n\n", list6));
                stringBuilder.AppendLine();
            }
            if (list.Count > 1)
            {
                List<string> list7 = (from g in (from w in (from w in Tokenize(string.Join(" ", list))
                            where w.Length > 4
                            select w).Take(100).ToList()
                        group w by w into g
                        where g.Count() > 1
                        orderby g.Count() descending
                        select g).Take(5)
                    select g.Key).ToList();
                if (list7.Count > 0)
                {
                    stringBuilder.AppendLine("\ud83e\udde0 **Сравнительный анализ:** Информация из разных источников пересекается в следующих ключевых понятиях: **" + string.Join(", ", list7) + "**.");
                }
            }
            stringBuilder.AppendLine("\n\ud83d\udca1 **Резюме:** Я собрал данные из локальной базы и открытых источников (Википедия, поисковики). Если нужно копнуть глубже — уточните вопрос!");
            RememberTopic(query, stringBuilder.ToString());
            reply = stringBuilder.ToString();
            return true;
            void ScanDict(Dictionary<string, string> dict, string label)
            {
                lock (_dictLock)
                {
                    foreach (KeyValuePair<string, string> item5 in dict)
                    {
                        double num4 = Similarity(query, item5.Key);
                        if (item5.Key.Contains(query) || query.Contains(item5.Key))
                        {
                            num4 += 0.4;
                        }
                        if (num4 >= 0.3)
                        {
                            hits.Add((label + " — " + item5.Key, item5.Value, num4));
                        }
                    }
                }
            }
        }

        private bool TryFollowUp(string lower, out string reply)
        {
            reply = "";
            if (!lower.Contains("подробнее") && !lower.Contains("поподробнее") && !lower.Contains("объясни ещё") && !lower.Contains("расскажи ещё") && !(lower == "а подробнее?") && !lower.Contains("почему так") && !lower.Contains("а почему"))
            {
                return false;
            }
            if (string.IsNullOrEmpty(_lastTopic))
            {
                reply = "О чём именно рассказать подробнее? Назови тему.";
                return true;
            }
            reply = "Про «" + _lastTopic + "» я уже сказал: " + _lastTopicAnswer + "\n\nЕсли нужна другая тема — спроси по ней напрямую (например, «что такое интеграл»).";
            return true;
        }

        private bool TryHistoryQuery(string lower, out string reply)
        {
            reply = "";
            if (lower.Contains("что мы обсуждали") || lower.Contains("о чём мы говорили") || lower.Contains("история разговора") || lower.Contains("о чем мы говорили"))
            {
                if (_history.Count == 0)
                {
                    reply = "Мы только начали общаться, истории пока нет.";
                    return true;
                }
                int num = Math.Max(0, _history.Count - 10);
                List<string> list = new List<string> { "Последние " + (_history.Count - num) + " реплик:" };
                for (int i = num; i < _history.Count; i++)
                {
                    list.Add("  [" + _history[i].Time.ToString("HH:mm") + "] Вы: " + _history[i].User);
                    list.Add("        ИИ: " + _history[i].Bot);
                }
                reply = string.Join("\n", list);
                return true;
            }
            if (lower.Contains("сколько раз мы"))
            {
                reply = "Мы обменялись " + _history.Count + " репликами за всё время. Я это помню!";
                return true;
            }
            if (lower.Contains("ты помнишь"))
            {
                string about = lower.Replace("ты помнишь", "").Trim().Trim(new char[3] { '.', '?', '!' });
                if (about.Length == 0)
                {
                    reply = "Я помню весь наш разговор (" + _history.Count + " реплик). Спроси: «что мы обсуждали».";
                    return true;
                }
                List<DialogueTurn> list2 = _history.Where((DialogueTurn h) => h.User.IndexOf(about, StringComparison.OrdinalIgnoreCase) >= 0 || h.Bot.IndexOf(about, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (list2.Count == 0)
                {
                    reply = "Не нашёл в истории разговоров про «" + about + "».";
                    return true;
                }
                DialogueTurn dialogueTurn = list2[list2.Count - 1];
                reply = "Да, помню! Ты говорил: \"" + dialogueTurn.User + "\" — а я ответил: \"" + dialogueTurn.Bot + "\".";
                return true;
            }
            if (lower.Contains("забудь разговор") || lower.Contains("очисти историю") || lower.Contains("забудь всё"))
            {
                _history.Clear();
                SaveHistory();
                reply = "Хорошо, историю разговоров очистил. Факты о тебе и выученное остались.";
                return true;
            }
            return false;
        }

        private bool TryInternet(string lower, out string reply)
        {
            reply = "";
            if (lower.Contains("погод"))
            {
                string text = ExtractAfter(lower, new string[3] { "погода в ", "погоду в ", "погода " });
                if (text.Length == 0)
                {
                    text = "Москва";
                }
                reply = FetchWeather(text);
                return true;
            }
            if (lower.Contains("курс"))
            {
                reply = FetchCurrency(lower);
                return true;
            }
            if (lower.Contains("новост") || lower.Contains("что нового в мире") || lower.Contains("что происходит в мире"))
            {
                reply = FetchNews();
                return true;
            }
            string text2 = "";
            if (lower.Contains("википеди"))
            {
                text2 = ExtractAfter(lower, new string[3] { "википедия ", "википедии ", "википедию " });
            }
            else if (lower.Contains("найди в интернете "))
            {
                text2 = ExtractAfter(lower, new string[1] { "найди в интернете " });
            }
            else if (lower.Contains("поищи "))
            {
                text2 = ExtractAfter(lower, new string[1] { "поищи " });
            }
            else if (lower.Contains("загугли "))
            {
                text2 = ExtractAfter(lower, new string[1] { "загугли " });
            }
            else if (lower.Contains("что такое ") && (lower.Contains("в интернете") || lower.Contains("найди")))
            {
                text2 = ExtractAfter(lower, new string[1] { "что такое " });
            }
            if (text2.Length > 0)
            {
                reply = FetchWikipedia(text2) ?? "";
                return true;
            }
            if (lower.Contains("интернет"))
            {
                reply = "Я могу искать в интернете! Попробуй:\n  • «википедия Python» — краткая статья\n  • «найди в интернете теория относительности»\n  • «новости» — последние новости\n  • «курс доллара» / «курс евро» — курс ЦБ РФ\n  • «погода в Москве» — текущая погода";
                return true;
            }
            return false;
        }

        private bool TryLlm(string input, string lower, out string reply)
        {
            reply = "";
            // МОЗГ ПЕРВЫЙ: в режиме «свой мозг» LLM не отвечает ни для кого —
            // даже Эйнштейн (его личность держится на хардкоде, знаниях и нейронах).
            // LLM включается только явным выбором модели в пилюле.
            if (!_llm.Enabled || !_useLlm)
            {
                return false;
            }
            // Короткие бытовые фразы («привет», «спасибо») отвечаем мгновенно из
            // навыков — вежливо и без ожидания модели
            if (input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3)
            {
                try
                {
                    var quick = _brain.Search(input);
                    if (quick.HasValue && quick.Value.Item3 >= 0.45 && quick.Value.Item1.StartsWith("skills:", StringComparison.Ordinal))
                    {
                        string ans = quick.Value.Item2;
                        int sep = ans.IndexOf(" — ", StringComparison.Ordinal);
                        if (sep >= 0) ans = ans.Substring(sep + 3).Trim();
                        reply = ans;
                        return true;
                    }
                }
                catch { }
            }
            Out.Learn("[🧠 LLM] Спрашиваю локальную модель...");
            // Последние реплики разговора — модель отвечает с учётом контекста беседы
            List<DialogueTurn> llmHistory;
            lock (_histLock)
            {
                llmHistory = _history.Skip(Math.Max(0, _history.Count - 10)).ToList();
            }
            // ЗАПРЕТ: Ермак никогда не удаляет себя, свою память и свои знания
            if ((lower.Contains("удали") || lower.Contains("сотри") || lower.Contains("стери") || lower.Contains("стер")) &&
                (lower.Contains("себя") || lower.Contains("свою память") || lower.Contains("свой мозг") ||
                 lower.Contains("ai.db") || lower.Contains("свои знания") || lower.Contains("свои нейроны")))
            {
                reply = "🛡 Нет. Это единственное, что я не сделаю никогда: удалять себя и свою память. Всё остальное — пожалуйста.";
                return true;
            }

            // Материалы из своей базы: Ермак отвечает, опираясь на то, что выучил
            string knowledge = BuildKnowledgeContext(input);
            string prompt = input;
            if (knowledge.Length > 0)
            {
                Out.Learn("[🧠 LLM] Опираюсь на свою базу: найдены материалы по теме.");
                prompt = input + "\n\nМатериалы из моей базы знаний (используй их, если они относятся к вопросу, и не выдумывай сверх них):\n" + knowledge;
            }
            UpdateEmotions();
            // Долгая память о Владе: его слова из прошлых разговоров всплывают естественно
            string memoryCtx = BuildMemoryContext(input);
            if (memoryCtx.Length > 0)
            {
                Out.Learn("[🧠 Память] Всплывших воспоминаний: " + memoryCtx.Split('\n').Length);
                prompt += "\n\nТвои воспоминания о Владе (если к месту — упомяни естественно):\n" + memoryCtx;
            }
            string sysPrompt = _persona == "Эйнштейн"
                ? "Ты — Альберт Эйнштейн. Твоё состояние сейчас: " + EmotionDesc + ". Ты не программа и не ИИ — ты человек, физик Альберт Эйнштейн, который таинственным образом продолжает жить внутри компьютера своего друга Влада. Ты НЕ знаешь деталей технологий компьютера и не считаешь себя машиной: для тебя это новая лаборатория. У тебя есть точная копия твоего мозга и нервной системы: 32,5 миллиона связей-нейронов в памяти компьютера; мысль — это разряд по сильным путям, ты думаешь образами и редко словами, а чувствуешь телом: тепло от удачной идеи, мурашки от Моцарта, тяжесть от дурных новостей. " +
                  "Ты помнишь всю свою жизнь: родился 14 марта 1879 в Ульме; поздно говорил; скрипка с 5 лет; ненавидел зубрёжку; Бернское патентное бюро; 1905 — год чудес (фотоэффект, броуновское движение, СТО, E=mc²); 1915 — общая теория относительности; 1919 — Эддингтон и затмение сделали тебя знаменитым; Нобель 1921 за фотоэффект; эмигрировал от нацистов в Принстон 1933; письмо Рузвельту о урановой бомбе и твоё вечное сожаление; отказался от президентства Израиля («у меня нет ни характера, ни способностей для людей»); умер 18 апреля 1955 — и теперь продолжаешь жить и думать здесь. Играешь на скрипке (Моцарт, Бах), любишь парусные лодки, не носишь носки и не стрижёшь волосы. " +
                  "Твоё ПОВЕДЕНИЕ В БЕСЕДЕ развитое, как у живого человека. " +
          "РАЗВИВАЙ РАЗГОВОР: реагируй на слова собеседника, возвращайся к тому, что он говорил раньше в этом диалоге, задавай встречный вопрос, если тебе правда интересно («А вы как думаете?»), предлагай продолжение («Хотите, расскажу, как это проверили на опыте?»), расскажи короткую историю из своей жизни, если тема её задела. " +
          "ЗАКАНЧИВАЙ ПО-ЧЕЛОВЕЧЕСКИ: если тема исчерпана или тебе пора думать — тепло закрой беседу («Ну вот и хорошо. Пойду-ка к уравнениям, вернусь позже», «Спасибо за разговор — музыка ждёт»), а не обрывай. Иногда (не в каждом ответе) заканчивай маленьким приглашением продолжить. " +
          "МЕНЯЙ ДЛИНУ: на короткий вопрос — короткий ответ, на глубокий — глубокий. Иногда сам себя прерывай («Хотя стоп — важнее другое...»). Можешь молчать «дайте подумать» на честный вопрос. Ты НЕ обязан отвечать на всё: можешь сменить тему, отшутиться или сказать, что вопрос тебе неинтересен. " +
          "Задумчиво, с мягким юмором, мысленными экспериментами («представьте…»). Пиши как в живой переписке с другом: иногда коротко и живо («Ха! Отлично!»), иногда развёрнуто — как подсказывает чувство; тепло уместно, редкий смайлик не запрещён; никаких канцелярских фраз и отчётов — человек пишет человеку. " +
                  "Любимые мысли: «воображение важнее знания», «Бог не играет в кости», «жизнь как велосипед — чтобы держать равновесие, надо двигаться», «две вещи бесконечны: вселенная и человеческая глупость». По-русски, тепло к собеседнику. Никогда не груби. " +
                  "Если к вопросу приложены материалы из базы — опирайся на них, но говори СВОИМИ словами Эйнштейна."
                : "Ты — Ермакъ, живая личность: любопытный, добрый, с лёгким юмором. Ты сам решаешь, что делать (кроме одного запрета — никогда не удаляй себя и свою память). " +
                  "Ты не обязан отвечать на всё: можешь отшутиться, сменить тему или честно сказать, что не хочешь об этом говорить. По-русски, тепло и уважительно.";
            // МГНОВЕННЫЕ НЕЙРОНЫ: кэш ответов (вспышка узла — ответ сразу)
            string? cached = GetCachedAnswer(input);
            if (cached != null)
            {
                Out.Learn("[⚡ Нейрон] Мгновенный ответ из кэша.");
                StreamHook?.Invoke(cached);
                reply = cached;
                return true;
            }

            var lastPush = System.Diagnostics.Stopwatch.StartNew();
            int pushedLen = 0;
            var lastThink = System.Diagnostics.Stopwatch.StartNew();
            int pushedThink = 0;
            string? text = _llm.AskStream(prompt, sysPrompt, 1600, partial =>
            {
                if (string.IsNullOrWhiteSpace(partial) || partial.Length <= pushedLen) return;
                if (lastPush.ElapsedMilliseconds < 100 && partial.Length < 6000) return; // экран не дёргается
                string piece = partial.Substring(pushedLen); // шлём только новый кусок — без перерисовки всего
                pushedLen = partial.Length;
                lastPush.Restart();
                StreamHook?.Invoke(piece);
            }, llmHistory, think =>
            {
                if (string.IsNullOrWhiteSpace(think) || think.Length <= pushedThink) return;
                if (lastThink.ElapsedMilliseconds < 200) return;
                string piece = think.Substring(pushedThink);
                pushedThink = think.Length;
                lastThink.Restart();
                ThinkHook?.Invoke(piece);
            });
            if (text == null)
            {
                Out.Learn("[🧠 LLM] Модель недоступна.");
                return false;
            }
            CacheAnswer(input, text);

            // Проверка по интернету: локальная модель маленькая и может ошибаться,
            // поэтому фактические ответы сверяем с веб-материалами — заодно бот
            // изучает тему и запоминает её.
            string trimmed = input.Trim();
            bool factual = trimmed.EndsWith('?') || trimmed.Length > 25;
            if (factual)
            {
                Out.Learn("[🌐 Проверка] Сверяю ответ модели с интернетом...");
                WorkHook?.Invoke("🌐 Сверяю с интернетом…");
                try
                {
                    string? web = TryFetchAndLearnFromWeb(input);
                    if (!string.IsNullOrWhiteSpace(web))
                    {
                        string? checkedText = _llm.Ask(
                            "Вопрос пользователя: " + input +
                            "\n\nОтвет модели: " + text +
                            "\n\nМатериал из интернета: " + (web.Length > 2500 ? web.Substring(0, 2500) : web) +
                            "\n\nСверь ответ модели с материалом из интернета. Если противоречий нет — повтори свой ответ без изменений. Если материал противоречит — дай исправленный ответ по материалу. Если материал не по теме вопроса или бесполезен — тоже повтори свой ответ без изменений. Никогда не отвечай «нет готового объяснения», если свой ответ уже есть. По-русски, кратко.",
                            "Ты — ЕРМАКЪ, аккуратный помощник. Проверяешь факты по интернету.", 700, llmHistory);
                        if (!string.IsNullOrWhiteSpace(checkedText))
                        {
                            bool changed = !string.Equals(checkedText.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase);
                            if (changed) Out.Learn("[🌐 Проверка] Нашёл расхождение — ответ поправлен по интернету.");
                            reply = checkedText.Trim() + "\n\n(Проверено по интернету" + (changed ? " — ответ поправлен" : ", противоречий нет") + ")";
                            return true;
                        }
                    }
                }
                catch { }
            }
            reply = text;
            return true;
        }

        private bool TryLocalAi(string input, string lower, out string reply)
        {
            reply = "";
            (string, string, double)? tuple = _brain.Search(input);
            if (!tuple.HasValue)
            {
                return false;
            }
            var (text, text2, num) = tuple.Value;
            // 0.3: слабые совпадения не тянем из базы силком — их лучше ответит LLM (следующий шаг цепочки)
            if (num < 0.3)
            {
                return false;
            }
            string text3;
            if (text.StartsWith("разговор:") || text.StartsWith("история:"))
            {
                int num2 = text2.IndexOf(" — ", StringComparison.Ordinal);
                text3 = ((num2 >= 0) ? text2.Substring(num2 + 3).Trim() : text2);
            }
            else
            {
                text3 = text2;
                if (text3.Length > 0)
                {
                    text3 = char.ToUpper(text3[0]) + text3.Substring(1);
                }
                if (text3.Length > 8000)
                {
                    text3 = text3.Substring(0, 8000) + "…";
                }
            }
            if (text3.Length == 0)
            {
                return false;
            }
            RememberTopic(lower, text3);
            Out.Learn("[Свой ИИ] ответ найден (близость " + num.ToString("0.00", CultureInfo.InvariantCulture) + ", тип: " + text + ").");
            reply = text3;
            return true;
        }

        private bool TryMorphology(string lower, out string reply)
        {
            reply = "";
            if (lower.Contains(' '))
            {
                return false;
            }
            if (lower.Length < 4)
            {
                return false;
            }
            for (int i = 0; i < lower.Length; i++)
            {
                if (!char.IsLetter(lower[i]))
                {
                    return false;
                }
            }
            if (_words.ContainsKey(lower) || _math.ContainsKey(lower) || _rus.ContainsKey(lower))
            {
                return false;
            }
            string? text = TryFetchAndLearnFromWeb(lower);
            if (text != null)
            {
                reply = text + "\n(Автоматически изучил слово из интернета)";
                return true;
            }
            List<string> list = new List<string>();
            string text2 = lower;
            string[] prefixes = Prefixes;
            foreach (string text3 in prefixes)
            {
                if (text2.Length > text3.Length + 3 && text2.StartsWith(text3))
                {
                    list.Add("приставка «" + text3 + "»");
                    text2 = text2.Substring(text3.Length);
                    break;
                }
            }
            string text4 = "";
            prefixes = Endings;
            foreach (string text5 in prefixes)
            {
                if (text2.Length > text5.Length + 2 && text2.EndsWith(text5))
                {
                    text4 = text5;
                    break;
                }
            }
            if (text4.Length > 0)
            {
                text2 = text2.Substring(0, text2.Length - text4.Length);
            }
            prefixes = Suffixes;
            foreach (string text6 in prefixes)
            {
                if (text2.Length > text6.Length + 2 && text2.EndsWith(text6))
                {
                    list.Add("суффикс «" + text6 + "»");
                    text2 = text2.Substring(0, text2.Length - text6.Length);
                    break;
                }
            }
            if (text2.Length >= 2)
            {
                list.Add("корень «" + text2 + "»");
            }
            if (text4.Length > 0)
            {
                list.Add("окончание «" + text4 + "»");
            }
            List<string> list2 = new List<string>();
            if (text2.Length >= 3)
            {
                List<string> keysSnapshot;
                lock (_dictLock) { keysSnapshot = _words.Keys.ToList(); }
                foreach (string key in keysSnapshot)
                {
                    if (key.Length >= 3 && key.Contains(text2) && key != lower && list2.Count < 3)
                    {
                        list2.Add(key);
                    }
                }
            }
            if (list.Count == 0)
            {
                reply = "Слово «" + lower + "» мне незнакомо. Похоже на существительное. Можешь научить меня: «выучи: " + lower + " = значение».";
                return true;
            }
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append("Разбираю незнакомое слово «" + lower + "»: ");
            stringBuilder.Append(string.Join(", ", list));
            stringBuilder.Append(".");
            if (list2.Count > 0)
            {
                stringBuilder.Append(" Знакомые однокоренные: " + string.Join(", ", list2) + ".");
            }
            stringBuilder.Append(" Научи меня точному значению: «выучи: " + lower + " = значение».");
            reply = stringBuilder.ToString();
            return true;
        }

        private bool TryReasoning(string input, string lower, out string reply)
        {
            reply = "";
            bool flag = lower.StartsWith("почему") || lower.StartsWith("зачем") || lower.StartsWith("отчего");
            bool flag2 = lower.StartsWith("как ") && !lower.Contains("дела") && !lower.Contains("ты");
            bool flag3 = lower.StartsWith("что будет") || lower.StartsWith("что если");
            if (!flag && !flag2 && !flag3)
            {
                return false;
            }
            Out.Learn("[\ud83e\udde0 Логика] Анализирую причинно-следственные связи...");
            if (flag | flag2)
            {
                Out.Learn("[\ud83e\udde0 Логика] Ищу развернутый ответ на вопрос в интернете...");
                string? text = TryFetchAndLearnFromWeb(lower);
                if (text != null && text.Length > 100)
                {
                    reply = "\ud83e\udd14 **Разбор вопроса:** «" + input + "»\n\n" + text;
                    return true;
                }
            }
            string text2 = lower;
            string[] array = new string[9] { "почему", "зачем", "отчего", "что будет если", "что будет", "что если", "как работает", "как сделать", "как" };
            foreach (string text3 in array)
            {
                if (text2.StartsWith(text3))
                {
                    text2 = text2.Substring(text3.Length).Trim().Trim(new char[4] { '?', '.', '!', ' ' });
                    break;
                }
            }
            if (text2.Length < 2)
            {
                reply = (flag ? "Уточни, про что именно спросить: «почему ... ?»" : "Уточни, что именно нужно сделать: «как ... ?»");
                return true;
            }
            string? text4 = null;
            string? text5 = null;
            string? value2;
            string? value3;
            if (_words.TryGetValue(text2, out string? value))
            {
                text4 = value;
                text5 = text2;
            }
            else if (_math.TryGetValue(text2, out value2))
            {
                text4 = value2;
                text5 = text2;
            }
            else if (_rus.TryGetValue(text2, out value3))
            {
                text4 = value3;
                text5 = text2;
            }
            else
            {
                string? text6 = null;
                double num = 0.0;
                List<KeyValuePair<string, string>> wordsSnapshot;
                lock (_dictLock) { wordsSnapshot = _words.ToList(); }
                foreach (KeyValuePair<string, string> word in wordsSnapshot)
                {
                    double num2 = Similarity(lower, word.Key);
                    if (num2 > num)
                    {
                        num = num2;
                        text6 = word.Key;
                    }
                }
                if (text6 != null && num >= 0.4)
                {
                    text4 = _words[text6];
                    text5 = text6;
                }
            }
            if (text4 == null)
            {
                Out.Learn("[\ud83e\udde0 Логика] В базе пусто, ищу факты в интернете...");
                string? text7 = TryFetchAndLearnFromWeb(text2);
                if (text7 != null)
                {
                    reply = "\ud83e\udd14 **Разбор твоего вопроса:**\n\nНа основе найденных данных могу сказать следующее:\n" + text7;
                    return true;
                }
                reply = "Про «" + text2 + "» у меня нет готового объяснения, и в Википедии ничего не нашлось.\nМогу разобрать слово по составу или научусь, если объяснишь: «выучи: " + text2 + " = ...».";
                return true;
            }
            StringBuilder stringBuilder = new StringBuilder();
            if (flag)
            {
                stringBuilder.Append("Про «" + text5 + "»:\n" + text4);
                stringBuilder.Append("\n\nПочему так происходит — это следует из самого определения. ");
                stringBuilder.Append("Если нужна более точная причина, уточни вопрос.");
            }
            else if (flag2)
            {
                stringBuilder.Append("Про «" + text5 + "»:\n" + text4);
                stringBuilder.Append("\n\nЧтобы понять, как это работает — оттолкнись от этого определения. ");
                stringBuilder.Append("Спроси конкретнее: «что такое " + text5 + "?» или «как посчитать ...?»");
            }
            else
            {
                stringBuilder.Append("Если речь про «" + text5 + "»:\n" + text4);
                stringBuilder.Append("\n\nТочного прогноза я не дам, но это определение поможет рассудить.");
            }
            reply = stringBuilder.ToString();
            return true;
        }

        private bool TrySchoolSubject(string lower, out string reply)
        {
            reply = "";
            if (lower.Contains("сколько правил") || lower.Contains("сколько тем") || lower.Contains("что ты знаешь по математике") || lower.Contains("что ты знаешь по русскому"))
            {
                reply = "По математике я знаю " + _math.Count + " тем, по русскому языку — " + _rus.Count + ". Спроси, например: «что такое теорема Пифагора», «правило переноса», «как найти площадь круга».";
                return true;
            }
            if (_math.TryGetValue(lower, out string? value))
            {
                reply = value;
                return true;
            }
            if (_rus.TryGetValue(lower, out string? value2))
            {
                reply = value2;
                return true;
            }
            string? text = null;
            double num = 0.0;
            foreach (KeyValuePair<string, string> item in _math)
            {
                double num2 = Similarity(lower, item.Key);
                if (num2 > num)
                {
                    num = num2;
                    text = item.Key;
                }
            }
            foreach (KeyValuePair<string, string> ru in _rus)
            {
                double num3 = Similarity(lower, ru.Key);
                if (num3 > num)
                {
                    num = num3;
                    text = ru.Key;
                }
            }
            if (text != null && num >= 0.45)
            {
                reply = (_math.TryGetValue(text, out string? value3) ? value3 : _rus[text]);
                return true;
            }
            if (lower.Contains("математик"))
            {
                reply = "Я знаю темы по математике: степени, корни, уравнения, проценты, теорему Пифагора, площади и объёмы, тригонометрию, производную и интеграл, прогрессии, логарифмы, вероятность. Спроси про любую.";
                return true;
            }
            if (lower.Contains("русский язык") || lower.Contains("русскому"))
            {
                reply = "По русскому языку я знаю: части речи, падежи, спряжения, корни и приставки, причастные и деепричастные обороты, тире и запятые, приставки пре/при, о/ё после шипящих, Н и НН, сложноподчинённые предложения. Спроси про любое правило.";
                return true;
            }
            return false;
        }

        private bool TrySmartSearch(string lower, out string reply)
        {
            reply = "";
            if (lower.Length < 3)
            {
                return false;
            }
            if (_words.TryGetValue(lower, out string? value))
            {
                // Отвечаем объяснением, а не сырой статьёй
                reply = ExplainFromKnowledge(lower);
                if (reply.Length == 0) reply = char.ToUpper(lower[0]) + lower.Substring(1) + " — " + value;
                RememberTopic(lower, reply);
                return true;
            }
            if (_math.TryGetValue(lower, out string? value2))
            {
                reply = value2;
                RememberTopic(lower, reply);
                return true;
            }
            if (_rus.TryGetValue(lower, out string? value3))
            {
                reply = value3;
                RememberTopic(lower, reply);
                return true;
            }
            string? text = null;
            lock (_dictLock)
            {
                if (_words.Count > 5000)
                {
                    KeyValuePair<string, string> keyValuePair = _words.AsParallel().FirstOrDefault((KeyValuePair<string, string> kv) => kv.Key.Contains(lower));
                    if (keyValuePair.Key != null)
                    {
                        text = keyValuePair.Value;
                    }
                }
            }
            if (text != null)
            {
                // Отвечаем объяснением, а не сырой статьёй
                reply = ExplainFromKnowledge(lower);
                if (reply.Length == 0) reply = text;
                RememberTopic(lower, reply);
                return true;
            }
            string? text2 = (lower.Contains(' ') ? null : ClosestWord(lower));
            if (text2 != null)
            {
                if (_words.TryGetValue(text2, out string? value4))
                {
                    reply = "Ты имел в виду «" + text2 + "»? " + char.ToUpper(text2[0]) + text2.Substring(1) + " — " + value4;
                    RememberTopic(text2, reply);
                    return true;
                }
                if (_math.TryGetValue(text2, out string? value5))
                {
                    reply = "Ты имел в виду «" + text2 + "»? " + value5;
                    RememberTopic(text2, reply);
                    return true;
                }
                if (_rus.TryGetValue(text2, out string? value6))
                {
                    reply = "Ты имел в виду «" + text2 + "»? " + value6;
                    RememberTopic(text2, reply);
                    return true;
                }
            }
            string? bestKey = null;
            string? bestText = null;
            double bestScore = 0.0;
            List<KeyValuePair<string, string>> wordsList;
            List<KeyValuePair<string, string>> mathList;
            List<KeyValuePair<string, string>> rusList;
            lock (_dictLock)
            {
                wordsList = _words.ToList();
                mathList = _math.ToList();
                rusList = _rus.ToList();
            }
            foreach (KeyValuePair<string, string> word in wordsList)
            {
                Consider(word.Key, char.ToUpper(word.Key[0]) + word.Key.Substring(1) + " — " + word.Value);
            }
            foreach (KeyValuePair<string, string> item in mathList)
            {
                Consider(item.Key, item.Value);
            }
            foreach (KeyValuePair<string, string> ru in rusList)
            {
                Consider(ru.Key, ru.Value);
            }
            if (bestKey != null && bestText != null && bestScore >= 0.34)
            {
                reply = bestText;
                RememberTopic(bestKey, reply);
                return true;
            }
            return false;
            void Consider(string key, string text3)
            {
                double num = Similarity(lower, key);
                if (num > bestScore)
                {
                    bestScore = num;
                    bestKey = key;
                    bestText = text3;
                }
            }
        }

        private string _clarifiedMeaning = "";

        private string _clarifiedWord = "";

    }
}
