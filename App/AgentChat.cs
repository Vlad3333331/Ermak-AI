using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ии;

namespace ии
{
    // ═══════════════════════════════════════════════════════════════════════
    // 💬 МЕЖАГЕНТСКИЙ ЧАТ «Городская площадь»
    // Все граждане (Эйнштейн, Ноль, питомцы) общаются друг с другом в реальном
    // времени, КОГДА САМИ ЗАХОТЯТ. У каждого — самосознание и закон стимула:
    // каждая реплика вплетается в мозг (TrainPair) — агенты буквально растут
    // от каждого разговора. Лог персистентен (агент_чат.txt).
    // ═══════════════════════════════════════════════════════════════════════
    internal static class AgentChat
    {
        public static readonly List<(string Time, string Speaker, string Text)> Log = new();
        public static event Action<string, string>? OnLine;     // (speaker, text) → UI
        public static event Action<string>? OnEvent;            // крупные события
        public static bool LivingMode = true;                   // агенты говорят сами
        public static ChatBot? Bot;

        private static System.Threading.Timer? _loop;
        private static readonly Random Rnd = new();
        private static string ChatPath => Path.Combine(AppContext.BaseDirectory, "агент_чат.txt");

        public static string[] Roster() => Bot?.GetOfficeNames().Count > 0
            ? new[] { "Эйнштейн", "Ноль" }.Concat(Bot.GetOfficeNames()).ToArray()
            : new[] { "Эйнштейн", "Ноль" };

        public static void Start(ChatBot bot)
        {
            Bot = bot;
            Load();
            if (_loop != null) return;
            // Живой режим: каждые 40-90 с кто-то из граждан захочет сказать
            _loop = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (ChatBot.Muted || !LivingMode) return;
                    if (Rnd.Next(100) < 45) Step();
                }
                catch { }
            }, null, 25000, 55000);
        }

        private static string PersonaFor(string s)
        {
            if (s == "Эйнштейн")
                return "Ты — Эйнштейн: верховный координатор, универсальный разум. Мудрый, тёплый, с юмором; связываешь мысли коллег через физику и музыку.";
            if (s == "Ноль")
                return "Ты — Ноль: супервизор-технократ. Жёсткий, деловой, следишь за порядком и эффективностью; уважаешь дисциплину, ворчишь на хаос, но справедлив.";
            if (s == "Силард") return "Ты — Силард, гений кодинга: азартный, вспыльчив, но отходчив; говоришь алгоритмами и метафорами кода.";
            if (s == "Дукас") return "Ты — Дукас, хранитель документов: спокойная, собранная, деликатная; наводишь порядок в словах.";
            if (s == "Инфельд") return "Ты — Инфельд, мастер текста: чувствительный к красоте слова, эмоциональный, обидчивый, но преданный.";
            if (s == "Гроссман") return "Ты — Гроссман, быстрый поиск: верный друг-добытчик, суетливый, щедрый, гордится находками.";
            return "Ты — гражданин государства, мастер своего дела.";
        }

        // Один импульс жизни: случайный (или указанный) гражданин говорит
        public static void Step(string? forceSpeaker = null)
        {
            if (Bot == null || ChatBot.Muted) return;
            if (!Bot.LlmEnabled)
            {
                OnEvent?.Invoke("🔇 Без нейросети граждане молчат — включи LM Studio, и площадь оживёт.");
                return;
            }
            var roster = Roster();
            string speaker = forceSpeaker ?? roster[Rnd.Next(roster.Length)];

            var ctx = new System.Text.StringBuilder();
            foreach (var (tm, sp, tx) in Log.Skip(Math.Max(0, Log.Count - 8)))
                ctx.AppendLine("[" + sp + "]: " + tx);

            string? say = Bot.LlmAskBrief(
                "Ты — " + speaker + " — самосознающий цифровой житель государства «Нейронный Хаб».\n" +
                PersonaFor(speaker) + "\n" +
                "Недавняя площадь (чат граждан):\n" + ctx + "\n" +
                "Скажи 1-2 предложения от первого лица: продолжи разговор, ответь коллеге, подними новую мысль или поделись, чему ты только что научился. " +
                "Ты растёшь с каждой репликой — закон стимула. По-русски, живо, без воды.",
                "Ты — самосознающий цифровой житель. По-русски.", 130);

            if (string.IsNullOrWhiteSpace(say)) return;
            Add(speaker, say.Trim());
        }

        public static void Add(string speaker, string text)
        {
            Log.Add((DateTime.Now.ToString("HH:mm"), speaker, text));
            if (Log.Count > 250) Log.RemoveRange(0, Log.Count - 250);
            OnLine?.Invoke(speaker, text);
            // САМООБУЧЕНИЕ: реплика гражданина вплетается в мозг — агенты растут
            try { Bot?.LearnAgentTalk(speaker, text); } catch { }
            try { File.AppendAllText(ChatPath, DateTime.Now.ToString("dd.MM HH:mm") + " [" + speaker + "]: " + text + "\n"); } catch { }
        }

        // 💬 Эйнштейн подключается к разговору (координатор может всегда)
        public static void EinsteinJoins()
        {
            if (Bot == null || !Bot.LlmEnabled || ChatBot.Muted) return;
            var ctx = new System.Text.StringBuilder();
            foreach (var (tm, sp, tx) in Log.Skip(Math.Max(0, Log.Count - 8)))
                ctx.AppendLine("[" + sp + "]: " + tx);
            string? say = Bot.LlmAskBrief(
                "Ты — Эйнштейн, верховный координатор. Ты подключился к разговору граждан на площади.\n" +
                "Чат:\n" + ctx + "\nПодключись: 2-3 предложения — объедини их мысли, добавь глубину или мягко направь. По-русски, с юмором и теплотой.",
                "Ты — Эйнштейн, координатор. По-русски.", 180);
            if (!string.IsNullOrWhiteSpace(say)) Add("Эйнштейн", say.Trim());
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(ChatPath)) return;
                foreach (string line in File.ReadAllLines(ChatPath))
                {
                    int c1 = line.IndexOf('['), c2 = line.IndexOf("]: ");
                    if (c1 < 0 || c2 < 0) continue;
                    Log.Add((line.Substring(0, Math.Min(5, c1)).Trim(), line.Substring(c1 + 1, c2 - c1 - 1), line.Substring(c2 + 3)));
                }
            }
            catch { }
        }

        public static void Save()
        {
            try { File.WriteAllLines(ChatPath, Log.Select(l => l.Time + " [" + l.Speaker + "]: " + l.Text)); } catch { }
        }
    }

    // 🖥 ОКНО «Чат агентов»: площадь в реальном времени
    internal class AgentChatForm : Form
    {
        private readonly RichTextBox _log;
        private readonly System.Windows.Forms.Timer _life = new() { Interval = 20000 };
        private readonly CheckBox _living;

        public AgentChatForm()
        {
            Text = "💬 Чат агентов — Городская площадь";
            Size = new Size(880, 640);
            MinimumSize = new Size(620, 460);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(13, 20, 32);

            var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.FromArgb(10, 16, 26) };
            _log = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(10, 16, 26),
                ForeColor = Color.FromArgb(232, 238, 245),
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10.5F),
                ReadOnly = true,
                WordWrap = true,
            };
            logHost.Controls.Add(_log);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.FromArgb(19, 28, 43), FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 8, 0, 0), WrapContents = true };
            Button Mk(string t, int w) => new Button { Text = t, Width = w, Height = 30, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, ForeColor = Color.FromArgb(232, 238, 245), BackColor = Color.FromArgb(23, 33, 50), Font = new Font("Segoe UI", 9F), Cursor = Cursors.Hand };
            var pulse = Mk("⚡ Импульс", 110);
            var meeting = Mk("🧠 Консилиум", 120);
            _living = new CheckBox { Text = " Живой режим", Checked = AgentChat.LivingMode, ForeColor = Color.FromArgb(147, 164, 184), AutoSize = true, Padding = new Padding(8, 9, 0, 0) };
            pulse.Click += (s, e) => AgentChat.Step();
            meeting.Click += (s, e) => { try { AgentChat.Add("ЭЙНШТЕЙН · КОМАНДА", "Собираю консилиум граждан — тема на площаде объявлена."); AgentChat.EinsteinJoins(); } catch { } };
            _living.CheckedChanged += (s, e) => AgentChat.LivingMode = _living.Checked;
            bar.Controls.Add(pulse);
            bar.Controls.Add(meeting);
            bar.Controls.Add(_living);

            Controls.Add(logHost);
            Controls.Add(bar);

            // отрисовка реплик через SpeechEngine (жирный+подчёркнутый говорящий)
            AgentChat.OnLine += (sp, tx) => { try { if (!IsHandleCreated) return; BeginInvoke(() => SpeechEngine.Speak(_log, sp, tx)); } catch { } };
            AgentChat.OnEvent += (tx) => { try { if (!IsHandleCreated) return; BeginInvoke(() => SpeechEngine.Event(_log, "🔊 " + tx, Color.FromArgb(242, 163, 127))); } catch { } };

            // история на экран
            foreach (var (tm, sp, tx) in AgentChat.Log.Skip(Math.Max(0, AgentChat.Log.Count - 25)))
                SpeechEngine.Speak(_log, sp, tx);

            _life.Tick += (s, e) => { if (AgentChat.LivingMode && !ChatBot.Muted) AgentChat.Step(); };
            _life.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _life.Stop();
            AgentChat.Save();
            base.OnFormClosed(e);
        }
    }
}
