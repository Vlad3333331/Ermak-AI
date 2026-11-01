using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Reflection;

namespace ии
{
    public static class Database
    {
        public static string DbPath = Path.Combine(AppContext.BaseDirectory, "ai.db");
        // Прогресс загрузки нейронов (0-100) — читает GUI во время старта
        public static int LoadProgress;

        // Серверный режим (MONSTER_SERVER=1): экономим память для хостингов с 512 МБ ОЗУ —
        // грузим только самые частые связи модели и укорачиваем статьи
        public static bool ServerMode = Environment.GetEnvironmentVariable("MONSTER_SERVER") == "1";

        public static void Initialize()
        {
            using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=false");
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = ServerMode
                ? @"
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA journal_size_limit=268435456;
                PRAGMA cache_size=-8000;
                CREATE TABLE IF NOT EXISTS Words (word TEXT PRIMARY KEY, definition TEXT);
                CREATE INDEX IF NOT EXISTS idx_words ON Words(word);
                CREATE TABLE IF NOT EXISTS Brain (type TEXT, q TEXT, a TEXT);
                CREATE TABLE IF NOT EXISTS History (time TEXT, user TEXT, bot TEXT);
                CREATE TABLE IF NOT EXISTS OwnModel (prefix TEXT, key TEXT, next_word TEXT, count INTEGER, UNIQUE(prefix, key, next_word));
            "
                : @"
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=OFF;
                PRAGMA journal_size_limit=268435456;
                PRAGMA cache_size=-100000;
                CREATE TABLE IF NOT EXISTS Words (word TEXT PRIMARY KEY, definition TEXT);
                CREATE INDEX IF NOT EXISTS idx_words ON Words(word);
                CREATE TABLE IF NOT EXISTS Brain (type TEXT, q TEXT, a TEXT);
                CREATE TABLE IF NOT EXISTS History (time TEXT, user TEXT, bot TEXT);
                CREATE TABLE IF NOT EXISTS OwnModel (prefix TEXT, key TEXT, next_word TEXT, count INTEGER, UNIQUE(prefix, key, next_word));
            ";
            command.ExecuteNonQuery();

            // Миграция старых баз: у OwnModel могло не быть ограничения UNIQUE — тогда
            // инкрементальные вставки плодили дубликаты (база разрасталась в разы).
            // Пересобираем таблицу с дедупликацией (одноразовая операция).
            try
            {
                var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='OwnModel'";
                string? tableSql = checkCmd.ExecuteScalar() as string;
                if (tableSql != null && !tableSql.Contains("UNIQUE"))
                {
                    Out.Learn("[🛠 База] OwnModel без UNIQUE — пересобираю с дедупликацией (одноразово, наберись терпения)...");
                    var migrate = connection.CreateCommand();
                    migrate.CommandTimeout = 0;
                    migrate.CommandText = @"
                        CREATE TABLE OwnModel_new (prefix TEXT, key TEXT, next_word TEXT, count INTEGER, UNIQUE(prefix, key, next_word));
                        INSERT INTO OwnModel_new (prefix, key, next_word, count) SELECT prefix, key, next_word, MAX(count) FROM OwnModel GROUP BY prefix, key, next_word;
                        DROP TABLE OwnModel;
                        ALTER TABLE OwnModel_new RENAME TO OwnModel;";
                    migrate.ExecuteNonQuery();
                    Out.Learn("[🛠 База] OwnModel пересобран: дубликаты удалены, вставки больше не размножаются.");
                }
            }
            catch (Exception ex)
            {
                Out.Learn("[⚠️ База] Миграция OwnModel не удалась: " + ex.Message);
            }

            BackupDaily();
        }

        // Ежедневная страховка базы (важно, когда база живёт в папке синхронизации):
        // снимаем консистентный снимок через штатный SQLite backup API (учитывает WAL).
        // Копии хранятся В ДВУХ местах: рядом с базой (ai.backup.db) и ВНЕ OneDrive —
        // в C:\ЕрмакБэкап (последние 3), чтобы синхронизация не могла уничтожить всё сразу.
        private static void BackupDaily()
        {
            try
            {
                string localDir = @"C:\ЕрмакБэкап";
                Directory.CreateDirectory(localDir);
                string localPath = Path.Combine(localDir, "ai_backup.db");

                // Вне OneDrive — раз в сутки
                bool localFresh = File.Exists(localPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(localPath)).TotalHours < 24;
                if (!localFresh)
                {
                    using (var source = new SqliteConnection("Data Source=" + DbPath + ";Pooling=false"))
                    using (var destination = new SqliteConnection("Data Source=" + localPath + ";Pooling=false"))
                    {
                        source.Open();
                        destination.Open();
                        source.BackupDatabase(destination);
                    }
                    Out.Learn("[💾 Бэкап] Суточная копия базы (вне OneDrive): " + localPath);
                    // оставляем последние 3 копии с датами
                    var old = Directory.GetFiles(localDir, "ai_backup_*.db").OrderBy(f => f).ToList();
                    while (old.Count > 2) { File.Delete(old[0]); old.RemoveAt(0); }
                    try { File.Copy(localPath, Path.Combine(localDir, $"ai_backup_{DateTime.Now:yyyy-MM-dd}.db"), true); } catch { }
                }

                // Рядом с базой — тоже раз в сутки
                string backupPath = Path.Combine(AppContext.BaseDirectory, "ai.backup.db");
                if (File.Exists(backupPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(backupPath)).TotalHours < 24)
                {
                    return;
                }
                using (var source = new SqliteConnection("Data Source=" + DbPath + ";Pooling=false"))
                using (var destination = new SqliteConnection("Data Source=" + backupPath + ";Pooling=false"))
                {
                    source.Open();
                    destination.Open();
                    source.BackupDatabase(destination);
                }
                Out.Learn("[💾 Бэкап] Суточная копия базы сохранена: " + backupPath);
            }
            catch (Exception ex)
            {
                Out.Learn("[⚠️ Бэкап] Не удалось сделать копию базы: " + ex.Message);
            }
        }
    }

    

    internal sealed class MemoryEntry
    {
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
        public bool Learned { get; set; }
        public HashSet<string>? TokenCache; // ленивый кеш токенов — FindBest не токенизирует вопрос на каждом запросе
    }

    /// <summary>Одна реплика в истории разговора.</summary>
    internal sealed class DialogueTurn
    {
        public DateTime Time { get; set; }
        public string User { get; set; } = "";
        public string Bot { get; set; } = "";
    }

internal static class Ui
{
	private const string Sep = "────────────────────────────────────────────────────────────";

	public static readonly object Lock = new object();

	public static void Banner()
	{
		try
		{
			Console.Title = "ЕРМАКЪ AI — разговорный ИИ";
		}
		catch
		{
		}
		Console.ForegroundColor = ConsoleColor.Magenta;
		Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
		Console.WriteLine("║                   Е Р М А К Ъ   A I                      ║");
		Console.WriteLine("║              разговорный ИИ на C# (без LLM)              ║");
		Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
		Console.ResetColor();
		Console.ForegroundColor = ConsoleColor.DarkGray;
		Console.WriteLine("Свои знания + свой TF-IDF мозг + Википедия.");
		Console.WriteLine("Пиши вопрос или «что ты умеешь». Выход — «пока».");
		Console.ResetColor();
	}

	public static void Prompt()
	{
		lock (Lock)
		{
			Console.WriteLine();
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.WriteLine("────────────────────────────────────────────────────────────");
			Console.ResetColor();
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.Write("Вы › ");
			Console.ResetColor();
		}
	}

	public static void Bot(string text)
	{
		lock (Lock)
		{
			Console.ForegroundColor = ConsoleColor.Green;
			Console.Write("Monster › ");
			Console.ResetColor();
			int num = 100;
			try
			{
				num = Math.Max(40, Console.WindowWidth - 12);
			}
			catch
			{
			}
			string[] array = (text ?? "").Replace("\r", "").Split('\n');
			for (int i = 0; i < array.Length; i++)
			{
				string text2 = array[i];
				bool flag = true;
				while (text2.Length > num)
				{
					int num2 = text2.LastIndexOf(' ', Math.Min(num, text2.Length - 1));
					if (num2 < 20)
					{
						num2 = num;
					}
					Console.WriteLine((flag ? "" : "          ") + text2.Substring(0, num2).TrimEnd());
					text2 = text2.Substring(num2).TrimStart();
					flag = false;
				}
				Console.WriteLine((flag ? "" : "          ") + text2);
			}
		}
	}

	public static void Info(string text)
	{
		lock (Lock)
		{
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.WriteLine(text);
			Console.ResetColor();
		}
	}

	public static void SetStatusBar(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		lock (Lock)
		{
			int cursorLeft = Console.CursorLeft;
			int cursorTop = Console.CursorTop;
			try
			{
				Console.SetCursorPosition(0, 0);
				Console.BackgroundColor = ConsoleColor.DarkBlue;
				Console.ForegroundColor = ConsoleColor.White;
				int windowWidth = Console.WindowWidth;
				string text2 = " " + text.Replace("\r", "").Replace("\n", " ") + " ";
				if (text2.Length > windowWidth - 2)
				{
					text2 = text2.Substring(0, windowWidth - 2);
				}
				Console.Write(text2.PadRight(windowWidth - 1));
				Console.ResetColor();
			}
			catch
			{
			}
			try
			{
				Console.SetCursorPosition(cursorLeft, cursorTop);
			}
			catch
			{
			}
		}
	}
}
internal static class Out
{
	private static readonly object _lock = new object();

	private static StreamWriter? _writer;

	public static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "monster_learn.log");

	// Последние события для веб-чата (лента «что я сейчас делаю»)
	private static readonly List<string> _recent = new List<string>();

	public static IReadOnlyList<string> RecentEvents
	{
		get { lock (_lock) { return _recent.ToList(); } }
	}

	public static void Learn(string msg)
	{
		lock (_lock)
		{
			_recent.Add(DateTime.Now.ToString("HH:mm") + " " + msg);
			if (_recent.Count > 200) _recent.RemoveRange(0, _recent.Count - 200);
			try
			{
				if (_writer == null)
				{
					_writer = new StreamWriter(LogPath, append: true, Encoding.UTF8)
					{
						AutoFlush = true
					};
				}
				_writer.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + msg);
			}
			catch
			{
			}
			if (msg.StartsWith("[Мир]") || msg.StartsWith("[Интернет]") || msg.StartsWith("[Вики]") || msg.StartsWith("[Люди]") || msg.StartsWith("[История]") || msg.StartsWith("[Словарь]") || msg.StartsWith("[Самообучение]"))
			{
				try
				{
					Console.Title = "Monster — " + msg;
				}
				catch
				{
				}
				try
				{
					Ui.SetStatusBar(msg);
					return;
				}
				catch
				{
					return;
				}
			}
		}
	}
}
internal sealed class TfIdfBrain
{
	private sealed class Doc
	{
		public string Kind = "";

		public string Text = "";

		public Dictionary<string, double> Vec = new Dictionary<string, double>();

	public double Norm; // кеш нормы IDF-весов — Search не пересчитывает её
	}

	private readonly object _lock = new object();

	private readonly List<Doc> _docs = new List<Doc>();

	private readonly Dictionary<string, int> _df = new Dictionary<string, int>();

	private int _n;

	public int Count
	{
		get
		{
			lock (_lock)
			{
				return _docs.Count;
			}
		}
	}

	public void Add(string kind, string text)
	{
		if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
		{
			return;
		}
		List<string> list = TfIdfStemmer.Tokenize(text);
		if (list.Count == 0)
		{
			return;
		}
		Dictionary<string, int> dictionary = new Dictionary<string, int>();
		foreach (string item in list)
		{
			dictionary[item] = ((!dictionary.TryGetValue(item, out var value)) ? 1 : (value + 1));
		}
		lock (_lock)
		{
			foreach (string key in dictionary.Keys)
			{
				_df[key] = ((!_df.TryGetValue(key, out var value2)) ? 1 : (value2 + 1));
			}
			Doc doc = new Doc
			{
				Kind = kind,
				Text = text
			};
			// веса сразу масштабируем IDF на момент добавления и кешируем норму:
			// Search больше не гоняет Math.Log по каждому термину каждого документа
			double norm2 = 0.0;
			foreach (KeyValuePair<string, int> item2 in dictionary)
			{
				double idf2 = Math.Log((double)(_n + 1) / (double)(_df[item2.Key] + 1)) + 1.0;
				double w2 = (1.0 + Math.Log(item2.Value)) * idf2;
				doc.Vec[item2.Key] = w2;
				norm2 += w2 * w2;
			}
			doc.Norm = Math.Sqrt(norm2);
			_docs.Add(doc);
			_n++;
		}
	}

	public (string kind, string text, double score)? Search(string query)
	{
		List<string> list = TfIdfStemmer.Tokenize(query);
		if (list.Count == 0)
		{
			return null;
		}
		Dictionary<string, int> dictionary = new Dictionary<string, int>();
		foreach (string item in list)
		{
			dictionary[item] = ((!dictionary.TryGetValue(item, out var value)) ? 1 : (value + 1));
		}
		lock (_lock)
		{
			if (_docs.Count == 0)
			{
				return null;
			}
			Dictionary<string, double> dictionary2 = new Dictionary<string, double>();
			foreach (KeyValuePair<string, int> item2 in dictionary)
			{
				double num = Math.Log(((double)_n + 1.0) / ((double)(_df.TryGetValue(item2.Key, out var value2) ? value2 : 0) + 1.0)) + 1.0;
				dictionary2[item2.Key] = (1.0 + Math.Log(item2.Value)) * num;
			}
			double num2 = Norm(dictionary2);
			if (num2 == 0.0)
			{
				return null;
			}
			double num3 = 0.0;
			Doc? doc = null;
			// быстрый проход: только пересечение с запросом, норма документа уже в кеше
			foreach (Doc doc2 in _docs)
			{
				if (doc2.Norm <= 0.0) continue;
				double num4 = 0.0;
				foreach (KeyValuePair<string, double> item3 in doc2.Vec)
				{
					if (dictionary2.TryGetValue(item3.Key, out var value4))
					{
						num4 += item3.Value * value4;
					}
				}
				if (num4 <= 0.0) continue;
				double num8 = num4 / (doc2.Norm * num2);
				if (num8 > num3)
				{
					num3 = num8;
					doc = doc2;
				}
			}
			if (doc == null)
			{
				return null;
			}
			return (doc.Kind, doc.Text, num3);
		}
	}

	private static double Norm(Dictionary<string, double> v)
	{
		double num = 0.0;
		foreach (double value in v.Values)
		{
			num += value * value;
		}
		return Math.Sqrt(num);
	}
}
internal static class TfIdfStemmer
{
	private static readonly HashSet<string> Stop = new HashSet<string>
	{
		"а", "и", "в", "во", "не", "на", "с", "со", "как", "что",
		"это", "ты", "я", "мне", "меня", "у", "к", "по", "из", "или",
		"же", "ли", "бы", "то", "вот", "ну", "да", "но", "за", "для",
		"про", "при", "над", "под", "без", "через", "его", "её", "ее", "их",
		"мы", "вы", "он", "она", "они", "оно", "был", "была", "было", "были",
		"есть", "будет", "можно", "нужно", "так", "там", "тут"
	};

	public static List<string> Tokenize(string text)
	{
		List<string> list = new List<string>();
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
				AddToken(list, stringBuilder.ToString());
				stringBuilder.Clear();
			}
		}
		if (stringBuilder.Length > 0)
		{
			AddToken(list, stringBuilder.ToString());
		}
		return list;
	}

	private static void AddToken(List<string> list, string w)
	{
		if (w.Length >= 3 && !Stop.Contains(w))
		{
			list.Add(Stem(w));
		}
	}

	private static string Stem(string w)
	{
		string[] array = new string[60]
		{
			"иями", "ями", "ами", "ией", "иях", "иям", "ого", "его", "ому", "ему",
			"ыми", "ими", "ая", "яя", "ое", "ее", "ые", "ие", "ой", "ей",
			"ий", "ый", "ам", "ям", "ах", "ях", "ов", "ев", "ом", "ем",
			"ую", "юю", "ишь", "ешь", "ет", "ит", "ут", "ют", "ат", "ят",
			"ал", "ял", "ил", "ел", "ла", "ло", "ли", "ть", "ся", "сь",
			"а", "я", "о", "е", "ы", "и", "у", "ю", "й", "ь"
		};
		foreach (string text in array)
		{
			if (w.Length - text.Length >= 3 && w.EndsWith(text))
			{
				w = w.Substring(0, w.Length - text.Length);
				break;
			}
		}
		return w;
	}
}
internal sealed class LlmClient
{
	// Своя сеть для LLM: большая модель отвечает дольше 20 секунд,
	// поэтому общий Http с коротким тайм-аутом ей не подходит
	private static readonly HttpClient _llmHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
	// Локальная модель отвечает быстрее без «размышлений» — таймаут с запасом
	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(60L)
	};

	private readonly bool _enabled;

	private readonly string _url = "";

	private string _model = "";

	public bool Enabled => _enabled;

	public LlmClient()
	{
		try
		{
			string path = Path.Combine(AppContext.BaseDirectory, "llm.txt");
			if (File.Exists(path))
			{
				string[] array = File.ReadAllLines(path);
				for (int i = 0; i < array.Length; i++)
				{
					string text = array[i].Trim();
					if (text.Length != 0 && !text.StartsWith("#") && text.Contains('='))
					{
						int num = text.IndexOf('=');
						string text2 = text.Substring(0, num).Trim().ToLowerInvariant();
						string text3 = text.Substring(num + 1).Trim();
						switch (text2)
						{
						case "enabled":
							_enabled = text3 == "1" || text3.Equals("true", StringComparison.OrdinalIgnoreCase);
							break;
						case "url":
							_url = text3;
							break;
						case "model":
							_model = text3;
							break;
						}
					}
				}
			}
		}
		catch
		{
		}
		if (string.IsNullOrEmpty(_url))
		{
			_url = "http://127.0.0.1:1234/v1/chat/completions";
		}
		if (string.IsNullOrEmpty(_model))
		{
			_model = "local-model";
		}
	}

	// Имя модели для живой панели статуса
	public string ModelName => _model;

	// Переключить модель на ходу (селектор модели в интерфейсе)
	public void SetModel(string model)
	{
		// эмбеддинг-модели не умеют разговаривать — их в чат не ставим
		if (!string.IsNullOrWhiteSpace(model) && !model.Contains("embed", StringComparison.OrdinalIgnoreCase)) _model = model;
	}

	// history — последние реплики разговора: модель отвечает связно с учётом контекста
	public string? Ask(string prompt, string system, int maxTokens = 700, List<DialogueTurn>? history = null)
	{
		if (!_enabled)
		{
			return null;
		}
		try
		{
			var messages = new List<object>
			{
				new { role = "system", content = system }
			};
			if (history != null)
			{
				foreach (DialogueTurn turn in history)
				{
					if (string.IsNullOrWhiteSpace(turn.User) || string.IsNullOrWhiteSpace(turn.Bot)) continue;
					messages.Add(new { role = "user", content = turn.User });
					messages.Add(new { role = "assistant", content = turn.Bot });
				}
			}
			messages.Add(new { role = "user", content = prompt });
			// Пустой закрытый блок размышлений: Qwen3/3.5 после него отвечает сразу,
			// без длинного reasoning — ответ в ~8 раз быстрее и без мусора в тексте
			messages.Add(new { role = "assistant", content = "<think>\n\n</think>\n\n" });
			using StringContent content = new StringContent(JsonSerializer.Serialize(new
			{
				model = _model,
				temperature = 0.7,
				max_tokens = maxTokens,
				messages
			}), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
			using HttpResponseMessage httpResponseMessage = _llmHttp.PostAsync(_url, content).GetAwaiter().GetResult();
			if (!httpResponseMessage.IsSuccessStatusCode)
			{
				return null;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(httpResponseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult());
			JsonElement property = jsonDocument.RootElement.GetProperty("choices");
			if (property.GetArrayLength() == 0)
			{
				return null;
			}
			JsonElement message = property[0].GetProperty("message");
			string? text = message.TryGetProperty("content", out JsonElement contentElement) ? contentElement.GetString() : null;
			if (!string.IsNullOrWhiteSpace(text))
			{
				// Некоторые шаблоны кладут блок размышлений прямо в текст ответа
				int start = text.IndexOf("<think>", StringComparison.Ordinal);
				if (start >= 0)
				{
					int end = text.IndexOf("</think>", StringComparison.Ordinal);
					if (end >= 0) text = text[(end + 8)..].Trim();
				}
			}
			return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
		}
		catch
		{
			return null;
		}
	}

	// Потоковый запрос: текст приходит по мере генерации (onDelta получает свежий накопленный текст).
	// Как DeepSeek/ZCode: пользователь видит мысль сразу. При сбое вернёт null — вызывающий код падает на обычный Ask.
	public string? AskStream(string prompt, string system, int maxTokens = 700, Action<string>? onDelta = null, List<DialogueTurn>? history = null, Action<string>? onThink = null)
	{
		if (!_enabled)
		{
			return null;
		}
		try
		{
			var messages = new List<object>
			{
				new { role = "system", content = system }
			};
			if (history != null)
			{
				foreach (DialogueTurn turn in history)
				{
					if (string.IsNullOrWhiteSpace(turn.User) || string.IsNullOrWhiteSpace(turn.Bot)) continue;
					messages.Add(new { role = "user", content = turn.User });
					messages.Add(new { role = "assistant", content = turn.Bot });
				}
			}
			messages.Add(new { role = "user", content = prompt });
			// Без пустого prefill: qwen размышляет, и мы показываем мысли вживую (как ZCode)
			using StringContent content = new StringContent(JsonSerializer.Serialize(new
			{
				model = _model,
				temperature = 0.7,
				max_tokens = maxTokens,
				stream = true,
				messages
			}), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _url);
			request.Content = content;
			using HttpResponseMessage httpResponseMessage = _llmHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
			if (!httpResponseMessage.IsSuccessStatusCode)
			{
				return null;
			}
			using StreamReader reader = new StreamReader(httpResponseMessage.Content.ReadAsStream());
			var sb = new StringBuilder();
			var thinkSb = new StringBuilder();
			string? line;
			while ((line = reader.ReadLine()) != null)
			{
				if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
				string data = line[5..].Trim();
				if (data == "[DONE]") break;
				try
				{
					using JsonDocument doc = JsonDocument.Parse(data);
					JsonElement choices = doc.RootElement.GetProperty("choices");
					if (choices.GetArrayLength() == 0) continue;
					JsonElement delta = choices[0].GetProperty("delta");
					// размышления qwen идут отдельным полем reasoning_content — в эфир «Думаю вслух»
					if (delta.TryGetProperty("reasoning_content", out JsonElement rc) && rc.ValueKind == JsonValueKind.String)
					{
						string? rp = rc.GetString();
						if (!string.IsNullOrEmpty(rp))
						{
							thinkSb.Append(rp);
							onThink?.Invoke(thinkSb.ToString());
						}
					}
					if (delta.TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.String)
					{
						string? piece = c.GetString();
						if (!string.IsNullOrEmpty(piece))
						{
							sb.Append(piece);
							string acc = sb.ToString();
							int tStart = acc.IndexOf("<think>", StringComparison.Ordinal);
							int tEnd = acc.IndexOf("</think>", StringComparison.Ordinal);
							if (tStart >= 0 && tEnd < 0)
							{
								// размышление идёт прямо сейчас — в эфир (серая строка «Думаю вслух»)
								onThink?.Invoke(acc[(tStart + 7)..]);
							}
							else
							{
								string shown = tEnd >= 0 ? acc[(tEnd + 8)..] : acc;
								onDelta?.Invoke(shown);
							}
						}
					}
				}
				catch { }
			}
			string result = sb.ToString().Trim();
			int te = result.IndexOf("</think>", StringComparison.Ordinal);
			if (te >= 0) result = result[(te + 8)..].Trim();
			return string.IsNullOrWhiteSpace(result) ? null : result;
		}
		catch
		{
			return null;
		}
	}

	// Запрос с изображением (OpenAI-совместимый image_url с data:URL).
	// Работает с моделями со зрением в LM Studio (qwen2-vl, moondream и т.п.).
	public string? AskWithImage(string prompt, string system, string imageDataUrl, int maxTokens = 700)
	{
		if (!_enabled)
		{
			return null;
		}
		try
		{
			var content = new object[]
			{
				new { type = "text", text = prompt },
				new { type = "image_url", image_url = new { url = imageDataUrl } }
			};
			var messages = new List<object>
			{
				new { role = "system", content = system },
				new { role = "user", content = content },
				new { role = "assistant", content = "<think>\n\n</think>\n\n" }
			};
			using StringContent body = new StringContent(JsonSerializer.Serialize(new
			{
				model = _model,
				temperature = 0.5,
				max_tokens = maxTokens,
				messages
			}), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
			using HttpResponseMessage httpResponseMessage = _llmHttp.PostAsync(_url, body).GetAwaiter().GetResult();
			if (!httpResponseMessage.IsSuccessStatusCode)
			{
				return null;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(httpResponseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult());
			JsonElement property = jsonDocument.RootElement.GetProperty("choices");
			if (property.GetArrayLength() == 0)
			{
				return null;
			}
			JsonElement message = property[0].GetProperty("message");
			string? text = message.TryGetProperty("content", out JsonElement contentElement) ? contentElement.GetString() : null;
			if (!string.IsNullOrWhiteSpace(text))
			{
				int start = text.IndexOf("<think>", StringComparison.Ordinal);
				if (start >= 0)
				{
					int end = text.IndexOf("</think>", StringComparison.Ordinal);
					if (end >= 0) text = text[(end + 8)..].Trim();
				}
			}
			return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
		}
		catch
		{
			return null;
		}
	}

	// Убедиться, что модель загружена в LM Studio. Если уже загружена — ничего не делаем:
	// повторная загрузка создаёт НОВЫЙ экземпляр и съедает память (было 3 llama-server).
	public bool EnsureModelLoaded()
	{
		if (!_enabled)
		{
			return false;
		}
		try
		{
			int v1 = _url.IndexOf("/v1/");
			string root = v1 > 0 ? _url.Substring(0, v1) : "http://127.0.0.1:1234";
			// 1. Проверяем список загруженных экземпляров
			try
			{
				using HttpResponseMessage st = _llmHttp.GetAsync(root + "/api/v1/models").GetAwaiter().GetResult();
				if (st.IsSuccessStatusCode)
				{
					string json = st.Content.ReadAsStringAsync().GetAwaiter().GetResult();
					using JsonDocument doc = JsonDocument.Parse(json);
					if (doc.RootElement.TryGetProperty("models", out JsonElement models))
					{
						foreach (JsonElement m in models.EnumerateArray())
						{
							if (m.TryGetProperty("key", out JsonElement k) && k.GetString() == _model
								&& m.TryGetProperty("loaded_instances", out JsonElement insts)
								&& insts.ValueKind == JsonValueKind.Array && insts.GetArrayLength() > 0)
							{
								return true; // модель уже загружена — не создаём дубликат
							}
						}
					}
				}
			}
			catch { }
			// 2. Модели нет — JIT-загрузка с настройками GPU пользователя
			using StringContent content = new StringContent(
				JsonSerializer.Serialize(new { model = _model }),
				Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
			using HttpResponseMessage resp = _llmHttp.PostAsync(root + "/api/v1/models/load", content).GetAwaiter().GetResult();
			return resp.IsSuccessStatusCode;
		}
		catch
		{
			return false;
		}
	}
}
internal sealed class OwnModel
{
	public const long MaxBytes = 107374182400L;

	private readonly Dictionary<string, Dictionary<string, int>> _next = new Dictionary<string, Dictionary<string, int>>();

	private readonly Dictionary<string, Dictionary<string, int>> _tri = new Dictionary<string, Dictionary<string, int>>();

	private readonly Random _rng = Random.Shared;

	private readonly object _trainLock = new object();
	// Инкрементальные счётчики: PairCount больше не суммирует все словари
	// (при миллионах связей это подтормаживало интерфейс)
	private long _pairTotal;
	private long _bigramTotal;
	private long _trigramTotal;

	private volatile bool _dirty;

	private long _lastSaveTicks;

	private readonly object _saveLock = new object();
	private readonly KeyValuePair<string, int>[] _topBuf = new KeyValuePair<string, int>[5]; // без аллокаций в горячем цикле

	// Инкрементальное сохранение: связи, изменившиеся с последнего FlushPending().
	// Позволяет модели расти до миллионов строк без полной перезаписи таблицы на каждом цикле обучения.
	// INSERT OR REPLACE трогает только известные нам строки — остаток базы (серверный режим) не затрагивается.
	private List<(char P, string K, string W, int C)> _pending = new List<(char, string, string, int)>();
	private int _flushCount;
	private readonly object _flushDbLock = new object();

	public int PendingCount
	{
		get
		{
			lock (_trainLock)
			{
				return _pending.Count;
			}
		}
	}

	public int VocabSize
	{
		get
		{
			lock (_trainLock)
			{
				return _next.Count;
			}
		}
	}

	public int PairCount
	{
		get
		{
			return (int)System.Threading.Interlocked.Read(ref _pairTotal);
		}
	}

	public long FileSize
	{
		get
		{
			if (!File.Exists(Database.DbPath))
			{
				return 0L;
			}
			return new FileInfo(Database.DbPath).Length;
		}
	}

	public int BigramCount
	{
		get
		{
			return (int)System.Threading.Interlocked.Read(ref _bigramTotal);
		}
	}

	public int TrigramCount
	{
		get
		{
			return (int)System.Threading.Interlocked.Read(ref _trigramTotal);
		}
	}

	private int TrigramCountOld
	{
		get
		{
			lock (_trainLock)
			{
				return _tri.Values.Sum((Dictionary<string, int> d) => d.Count);
			}
		}
	}

	public OwnModel(bool autoLoad = true)
	{
		if (autoLoad)
		{
			Load();
		}
	}

	public void Train(string text)
	{
		List<string> list = Tokenize(text);
		if (list.Count < 2)
		{
			return;
		}
		lock (_trainLock)
		{
			for (int i = 0; i < list.Count - 1; i++)
			{
				if (!_next.TryGetValue(list[i], out Dictionary<string, int>? value))
				{
					value = new Dictionary<string, int>();
					_next[list[i]] = value;
				}
				if (!value.TryGetValue(list[i + 1], out var value2))
				{
					value[list[i + 1]] = 1;
					_bigramTotal++;
					_pairTotal++;
				}
				else
				{
					value[list[i + 1]] = value2 + 1;
				}
				_pending.Add(('B', list[i], list[i + 1], value[list[i + 1]]));
			}
			for (int j = 0; j < list.Count - 2; j++)
			{
				string key = list[j] + " " + list[j + 1];
				if (!_tri.TryGetValue(key, out Dictionary<string, int>? value3))
				{
					value3 = new Dictionary<string, int>();
					_tri[key] = value3;
				}
				if (!value3.TryGetValue(list[j + 2], out var value4))
				{
					value3[list[j + 2]] = 1;
					_trigramTotal++;
					_pairTotal++;
				}
				else
				{
					value3[list[j + 2]] = value4 + 1;
				}
				_pending.Add(('T', key, list[j + 2], value3[list[j + 2]]));
			}
		}
	}

	public void TrainPair(string question, string answer)
	{
		Train(question);
		Train(answer);
		Train(question + " " + answer);
	}

	public string? Generate(string seed, int maxWords = 1000)
	{
		List<string> list = Tokenize(seed);
		if (list.Count == 0)
		{
			return null;
		}
		// Чтение _next/_tri идёт параллельно с фоновым обучением — берём тот же замок, что и Train
		lock (_trainLock)
		{
			return GenerateLocked(list, maxWords);
		}
	}
	// Самое вероятное следующее слово после данного (биграммная модель) —
	// метрика качества на отложенном (тестовом) наборе статей
	public string? PredictNext(string word)
	{
		lock (_trainLock)
		{
			if (_next.TryGetValue(word, out Dictionary<string, int>? d) && d.Count > 0)
			{
				string best = "";
				int bestCount = -1;
				foreach (KeyValuePair<string, int> kv in d)
				{
					if (kv.Value > bestCount)
					{
						bestCount = kv.Value;
						best = kv.Key;
					}
				}
				return best;
			}
			return null;
		}
	}



	private string? GenerateLocked(List<string> list, int maxWords)
	{
		string? text = null;
		for (int num = list.Count - 1; num >= 0; num--)
		{
			if (_next.ContainsKey(list[num]))
			{
				text = list[num];
				break;
			}
		}
		if (text == null)
		{
			if (_next.Count == 0)
			{
				return null;
			}
			text = _next.Keys.ElementAt(_rng.Next(_next.Count));
		}
		Out.Learn("    [Нейросеть] генерация со слова: \"" + text + "\"");
		List<string> list2 = new List<string> { text };
		for (int i = 0; i < maxWords; i++)
		{
			Dictionary<string, int>? value = null;
			if (list2.Count >= 2)
			{
				string key = list2[list2.Count - 2] + " " + list2[list2.Count - 1];
				if (_tri.TryGetValue(key, out Dictionary<string, int>? value2) && value2.Count > 0)
				{
					value = value2;
				}
			}
			if (value == null)
			{
				value = _next.GetValueOrDefault(text);
			}
			if (value == null || value.Count == 0)
			{
				break;
			}
			// ручной top-5 без LINQ-аллокаций на каждом шаге генерации
			var list3 = _topBuf;
			int len3 = 0, maxValue = 0;
			foreach (KeyValuePair<string, int> kv in value)
			{
				if (len3 < 5)
				{
					list3[len3++] = kv; maxValue += kv.Value;
					if (len3 == 5)
					for (int a = 4; a > 0; a--)
					if (list3[a].Value > list3[a - 1].Value) (list3[a], list3[a - 1]) = (list3[a - 1], list3[a]);
				}
				else if (kv.Value > list3[4].Value)
				{
					maxValue += kv.Value - list3[4].Value;
					list3[4] = kv;
					for (int a = 4; a > 0 && list3[a].Value > list3[a - 1].Value; a--) (list3[a], list3[a - 1]) = (list3[a - 1], list3[a]);
				}
			}
			int num2 = _rng.Next(maxValue);
			string key2 = list3[0].Key;
			foreach (KeyValuePair<string, int> item in list3)
			{
				num2 -= item.Value;
				if (num2 <= 0)
				{
					key2 = item.Key;
					break;
				}
			}
			int num3;
			if (key2.Length <= 2)
			{
				switch (key2)
				{
				default:
					num3 = ((key2 == "но") ? 1 : 0);
					break;
				case "и":
				case "в":
				case "на":
				case "с":
				case "а":
				case "у":
				case "к":
				case "о":
				case "по":
				case "не":
					num3 = 1;
					break;
				}
			}
			else
			{
				num3 = 0;
			}
			int num4;
			if (list2.Count >= 1)
			{
				num4 = ((list2[list2.Count - 1].Length <= 2) ? 1 : 0);
			}
			else
			{
				num4 = 0;
			}
			bool flag = (byte)num4 != 0;
			bool flag2 = value.Count == 1;
			if (((uint)num3 & (flag ? 1u : 0u)) != 0 && !flag2)
			{
				List<KeyValuePair<string, int>> list4 = value.Where((KeyValuePair<string, int> kv) => kv.Key.Length > 2).ToList();
				if (list4.Count > 0)
				{
					int maxValue2 = list4.Sum((KeyValuePair<string, int> x) => x.Value);
					int num5 = _rng.Next(maxValue2);
					foreach (KeyValuePair<string, int> item2 in list4)
					{
						num5 -= item2.Value;
						if (num5 <= 0)
						{
							key2 = item2.Key;
							break;
						}
					}
				}
			}
			list2.Add(key2);
			text = key2;
			if (key2.EndsWith(".") || key2.EndsWith("!") || key2.EndsWith("?"))
			{
				break;
			}
		}
		if (list2.Count == 0)
		{
			return null;
		}
		string text2 = string.Join(" ", list2);
		if (text2.Length > 0)
		{
			text2 = char.ToUpper(text2[0]) + text2.Substring(1);
		}
		if (!text2.EndsWith(".") && !text2.EndsWith("!") && !text2.EndsWith("?"))
		{
			text2 += ".";
		}
		return Regex.Replace(text2, "([\\.\\!\\?])\\s+", "$1\n");
	}

	public void RequestSave()
	{
		_dirty = true;
		if (DateTime.UtcNow.Ticks - Interlocked.Read(in _lastSaveTicks) > 200000000)
		{
			FlushIfDirty();
		}
	}

	public void FlushIfDirty()
	{
		if (!_dirty)
		{
			return;
		}
		lock (_saveLock)
		{
			if (_dirty)
			{
				_dirty = false;
				Interlocked.Exchange(ref _lastSaveTicks, DateTime.UtcNow.Ticks);
				Save();
			}
		}
	}

	// Быстрое инкрементальное сохранение: upsert только новых/изменившихся связей за один транзакцией.
	// O(новых связей), а не O(всей модели) — так модель может расти до миллионов строк.
	// Вызывается на горячем пути обучения; полная Save() остаётся для выхода и «сохранить всё».
	public void FlushPending()
	{
		List<(char P, string K, string W, int C)> batch;
		lock (_trainLock)
		{
			if (_pending.Count == 0)
			{
				return;
			}
			batch = _pending;
			_pending = new List<(char, string, string, int)>();
		}
		lock (_flushDbLock)
		try
		{
			using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + Database.DbPath + ";Pooling=false");
			sqliteConnection.Open();
			using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
			SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			sqliteCommand.Transaction = sqliteTransaction;
			sqliteCommand.CommandText = "INSERT OR REPLACE INTO OwnModel (prefix, key, next_word, count) VALUES ($p, $k, $n, $c)";
			SqliteParameter sqliteParameter = sqliteCommand.Parameters.Add("$p", SqliteType.Text);
			SqliteParameter sqliteParameter2 = sqliteCommand.Parameters.Add("$k", SqliteType.Text);
			SqliteParameter sqliteParameter3 = sqliteCommand.Parameters.Add("$n", SqliteType.Text);
			SqliteParameter sqliteParameter4 = sqliteCommand.Parameters.Add("$c", SqliteType.Integer);
			foreach (var item in batch)
			{
				sqliteParameter.Value = item.P;
				sqliteParameter2.Value = item.K;
				sqliteParameter3.Value = item.W;
				sqliteParameter4.Value = item.C;
				sqliteCommand.ExecuteNonQuery();
			}
			sqliteTransaction.Commit();
			// Журнал WAL без сброса вырастает до гигабайтов и забивает диск:
			// каждые ~20 сохранений ужимаем его до нуля
			if (++_flushCount % 20 == 0)
			{
				try { using SqliteCommand cp = sqliteConnection.CreateCommand(); cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; cp.ExecuteNonQuery(); } catch { }
			}
		}
		catch
		{
			// База временно недоступна — вернём пакет в буфер и повторим в следующем цикле
			lock (_trainLock)
			{
				if (_pending.Count < 2000000)
				{
					batch.AddRange(_pending);
					_pending = batch;
				}
			}
		}
	}

	public void Save()
	{
		Interlocked.Exchange(ref _lastSaveTicks, DateTime.UtcNow.Ticks);
		// Серверный режим: в памяти только часть модели (650 тыс. из 3,2 млн) —
		// перезапись таблицы стёрла бы остальную базу. Модель живёт в ОЗУ сессии.
		if (Database.ServerMode)
		{
			Out.Learn("[💾 Save] server-mode: запись модели в базу пропущена (защита от стирания)");
			return;
		}
		try
		{
			using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + Database.DbPath + ";Pooling=false");
			sqliteConnection.Open();
			using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
			SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			sqliteCommand.CommandText = "DELETE FROM OwnModel";
			sqliteCommand.ExecuteNonQuery();
			SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand();
			sqliteCommand2.CommandText = "INSERT INTO OwnModel (prefix, key, next_word, count) VALUES ($p, $k, $n, $c)";
			SqliteParameter sqliteParameter = sqliteCommand2.Parameters.Add("$p", SqliteType.Text);
			SqliteParameter sqliteParameter2 = sqliteCommand2.Parameters.Add("$k", SqliteType.Text);
			SqliteParameter sqliteParameter3 = sqliteCommand2.Parameters.Add("$n", SqliteType.Text);
			SqliteParameter sqliteParameter4 = sqliteCommand2.Parameters.Add("$c", SqliteType.Integer);
			lock (_trainLock)
			{
				foreach (KeyValuePair<string, Dictionary<string, int>> item in _next)
				{
					foreach (KeyValuePair<string, int> item2 in item.Value)
					{
						sqliteParameter.Value = "B";
						sqliteParameter2.Value = item.Key;
						sqliteParameter3.Value = item2.Key;
						sqliteParameter4.Value = item2.Value;
						sqliteCommand2.ExecuteNonQuery();
					}
				}
				foreach (KeyValuePair<string, Dictionary<string, int>> item3 in _tri)
				{
					foreach (KeyValuePair<string, int> item4 in item3.Value)
					{
						sqliteParameter.Value = "T";
						sqliteParameter2.Value = item3.Key;
						sqliteParameter3.Value = item4.Key;
						sqliteParameter4.Value = item4.Value;
						sqliteCommand2.ExecuteNonQuery();
					}
				}
			}
			sqliteTransaction.Commit();
			lock (_trainLock)
			{
				_pending.Clear();
			}
		}
		catch
		{
		}
	}

	public void Load()
	{
		try
		{
			using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + Database.DbPath + ";Pooling=false");
			sqliteConnection.Open();
			try { using var mm = sqliteConnection.CreateCommand(); mm.CommandText = "PRAGMA mmap_size=4294967296;"; mm.ExecuteNonQuery(); } catch { }
			SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			// БЕЗ ORDER BY: порядок строк словарям не нужен, а сортировка 50+ млн строк
			// съедала минуты на каждом старте. LIMIT — только предохранитель ОЗУ.
			sqliteCommand.CommandText = Database.ServerMode
				? "SELECT prefix, key, next_word, count FROM OwnModel LIMIT 650000"
				: "SELECT prefix, key, next_word, count FROM OwnModel LIMIT 200000000";
			long totalRows = 0; long loadedRows = 0; long nextMilestone = 2000000;
try { using var cnt = sqliteConnection.CreateCommand(); cnt.CommandText = "SELECT COUNT(*) FROM OwnModel"; totalRows = Convert.ToInt64(cnt.ExecuteScalar()); } catch { }
using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
			while (sqliteDataReader.Read())
			{
				string text = sqliteDataReader.GetString(0);
				string key = sqliteDataReader.GetString(1);
				string key2 = sqliteDataReader.GetString(2);
				int @int = sqliteDataReader.GetInt32(3);
				loadedRows++;
				if (loadedRows >= nextMilestone)
				{
					nextMilestone += 2000000;
					Database.LoadProgress = totalRows > 0 ? (int)(loadedRows * 100 / totalRows) : 0;
					try { Out.Learn($"[⚡ Загрузка] Нейронов: {loadedRows:N0} из {totalRows:N0} ({Database.LoadProgress}%)"); } catch { }
				}
				if (text == "T")
				{
					if (!_tri.TryGetValue(key, out Dictionary<string, int>? value))
					{
						value = new Dictionary<string, int>();
						_tri[key] = value;
					}
					if (!value.TryAdd(key2, @int)) value[key2] = @int; else { _trigramTotal++; _pairTotal++; }
				}
				else
				{
					if (!_next.TryGetValue(key, out Dictionary<string, int>? value2))
					{
						value2 = new Dictionary<string, int>();
						_next[key] = value2;
					}
					if (!value2.TryAdd(key2, @int)) value2[key2] = @int; else { _bigramTotal++; _pairTotal++; }
				}
			}
		}
		catch
		{
		}
	}

	private static List<string> Tokenize(string text)
	{
		List<string> list = new List<string>();
		StringBuilder stringBuilder = new StringBuilder();
		string text2 = text.ToLowerInvariant();
		for (int i = 0; i < text2.Length; i++)
		{
			char c = text2[i];
			if (char.IsLetterOrDigit(c) || c == '-')
			{
				stringBuilder.Append(c);
			}
			else if (stringBuilder.Length > 0)
			{
				list.Add(stringBuilder.ToString());
				stringBuilder.Clear();
				if (c == '.' || c == '!' || c == '?')
				{
					list.Add(c.ToString());
				}
			}
		}
		if (stringBuilder.Length > 0)
		{
			list.Add(stringBuilder.ToString());
		}
		return list;
	}
}
internal sealed class ExpressionParser
{
	private readonly string _s;

	private int _pos;

	public ExpressionParser(string s)
	{
		_s = s;
	}

	public double Parse()
	{
		double result = ParseExpr();
		Skip();
		if (_pos < _s.Length)
		{
			throw new FormatException();
		}
		return result;
	}

	private void Skip()
	{
		while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos]))
		{
			_pos++;
		}
	}

	private double ParseExpr()
	{
		double num = ParseTerm();
		while (true)
		{
			Skip();
			if (_pos >= _s.Length || (_s[_pos] != '+' && _s[_pos] != '-'))
			{
				break;
			}
			char c = _s[_pos++];
			double num2 = ParseTerm();
			num = ((c == '+') ? (num + num2) : (num - num2));
		}
		return num;
	}

	private double ParseTerm()
	{
		double num = ParseFactor();
		while (true)
		{
			Skip();
			if (_pos >= _s.Length || (_s[_pos] != '*' && _s[_pos] != '/'))
			{
				break;
			}
			char c = _s[_pos++];
			double num2 = ParseFactor();
			num = ((c == '*') ? (num * num2) : (num / num2));
		}
		return num;
	}

	private double ParseFactor()
	{
		Skip();
		if (_pos < _s.Length && _s[_pos] == '-')
		{
			_pos++;
			return 0.0 - ParseFactor();
		}
		if (_pos < _s.Length && _s[_pos] == '(')
		{
			_pos++;
			double result = ParseExpr();
			Skip();
			if (_pos < _s.Length && _s[_pos] == ')')
			{
				_pos++;
			}
			return result;
		}
		int pos = _pos;
		while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == '.'))
		{
			_pos++;
		}
		if (pos == _pos)
		{
			throw new FormatException();
		}
		return double.Parse(_s.Substring(pos, _pos - pos), CultureInfo.InvariantCulture);
	}
}
}
