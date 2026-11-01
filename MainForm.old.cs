using ии;
using System.ComponentModel;

namespace ии
{
    // === ЕРМАКЪ AI — оконное приложение ===
    // Тёмная тема в духе современных ИИ-чатов: слева чат сообщениями-карточками,
    // справа панель обучения (текущая задача, прогресс, лента событий).
    // Дисциплина «одна задача»: выбор новой задачи отменяет предыдущую,
    // панель «Текущее задание» и кнопки-пилюли это подсвечивают.
    internal class MainForm : Form
    {
        // ── Палитра ──
        private static readonly Color ColWindow  = Color.FromArgb(19, 12, 13);    // тёмно-алый фон окна
        private static readonly Color ColCard    = Color.FromArgb(34, 19, 21);    // багровые карточки
        private static readonly Color ColCardAlt = Color.FromArgb(46, 25, 27);    // поля ввода, блоки сообщений
        private static readonly Color ColBorder  = Color.FromArgb(96, 36, 40);    // алые рамки
        private static readonly Color ColText    = Color.FromArgb(242, 232, 230); // основной текст
        private static readonly Color ColMuted   = Color.FromArgb(198, 154, 156); // вторичный тёплый текст
        private static readonly Color ColAccent  = Color.FromArgb(229, 72, 77);   // алый акцент (кнопки, прогресс)
        private static readonly Color ColAccentHi = Color.FromArgb(246, 110, 112); // алый при наведении
        private static readonly Color ColGreen   = Color.FromArgb(126, 231, 135);
        private static readonly Color ColPurple  = Color.FromArgb(210, 168, 255);
        private static readonly Color ColRed     = Color.FromArgb(248, 81, 73);
        private static readonly Color ColYellow  = Color.FromArgb(233, 180, 76);
        private static readonly Color ColBlue    = Color.FromArgb(233, 196, 136);   // тёплый янтарный
        private static readonly Color ColGreenBar = Color.FromArgb(46, 160, 67);

        private ChatBot? _bot;
        private bool _botReady;
        private bool _busy;
        private int _lastEventCount = -1;

        private readonly RichTextBox _chat = new();
        private readonly TextBox _input = new();
        private readonly RoundButton _send = new() { Fill = ColAccent, FillHover = ColAccentHi, Fore = Color.White, Radius = 10, Text = "Отправить" };
        private readonly Label _dot = new();
        private readonly Label _status = new();
        private readonly Label _taskValue = new();
        private readonly RoundButton _stopTask = new() { Fill = Color.FromArgb(84, 16, 19), FillHover = Color.FromArgb(112, 22, 26), Fore = Color.FromArgb(255, 154, 156), Border = Color.FromArgb(150, 40, 44), Radius = 10, Text = "⏹ Остановить" };
        private readonly Label _pairsValue = new();
        private Label _articlesValue = new();
        private Label _vocabValue = new();
        private Label _rateValue = new();
        private readonly Label _stageValue = new();
        private readonly Label _systemValue = new();
        private readonly ProgressPanel _bar = new();
        private readonly ListBox _feed = new();
        private readonly System.Windows.Forms.Timer _timer = new();
        private readonly Dictionary<string, RoundButton> _quick = new();

        public MainForm()
        {
            Text = "ЕРМАКЪ AI";
            Width = 1320;
            Height = 840;
            MinimumSize = new Size(1080, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ColWindow;
            ForeColor = ColText;
            Font = new Font("Segoe UI", 9.75F);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();

            SetStatus(ColYellow, "Загрузка базы знаний (до минуты)…");
            Task.Run(() =>
            {
                try
                {
                    _bot = new ChatBot();
                }
                catch (Exception ex)
                {
                    BeginInvoke(() => SetStatus(ColRed, "Ошибка загрузки: " + ex.Message));
                    return;
                }
                BeginInvoke(() =>
                {
                    _botReady = true;
                    AppendMessage("ЕРМАКЪ AI", "Привет! Я ЕРМАКЪ AI — самообучающийся ИИ. Пиши что угодно, а справа видишь, как я учусь в реальном времени.\nЯ выполняю только одну задачу за раз: выберешь новую — предыдущая отменится.\nУчусь всему — сперва лёгкие темы, с приоритетом точных наук и познания мышления и речи человека. «что ты умеешь» — список команд.", ColAccent, ColCard);
                });
            });

            _timer.Interval = 1000;
            _timer.Tick += (s, e) => RefreshPanel();
            _timer.Start();
        }

        private void BuildUi()
        {
            // ── Шапка: индикатор + статус ──
            var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = ColWindow, Padding = new Padding(16, 0, 16, 0) };
            header.Paint += (s, e) => e.Graphics.DrawLine(new Pen(ColBorder), 0, header.Height - 1, header.Width, header.Height - 1);
            _dot.AutoSize = true;
            _dot.Text = "●";
            _dot.Font = new Font("Segoe UI", 11F);
            _dot.ForeColor = ColYellow;
            _dot.BackColor = ColWindow;
            _dot.Dock = DockStyle.Left;
            _dot.Padding = new Padding(0, 0, 8, 0);
            _dot.TextAlign = ContentAlignment.MiddleCenter;
            var title = new Label { Text = "ЕРМАКЪ AI", Font = new Font("Segoe UI", 11.5F, FontStyle.Bold), ForeColor = ColText, BackColor = ColWindow, Dock = DockStyle.Left, AutoSize = true, Padding = new Padding(0, 0, 14, 0), TextAlign = ContentAlignment.MiddleCenter };
            _status.Text = "Запуск…";
            _status.ForeColor = ColMuted;
            _status.BackColor = ColWindow;
            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleCenter;
            _status.Padding = new Padding(4, 0, 0, 0);
            header.Controls.Add(_status);
            header.Controls.Add(title);
            header.Controls.Add(_dot);

            // ── Левая половина: чат ──
            var chatPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 8, 10), BackColor = ColWindow };

            _chat.Dock = DockStyle.Fill;
            _chat.ReadOnly = true;
            _chat.AllowDrop = true;
            _chat.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            _chat.DragDrop += (s, e) => HandleIncomingImages((string[])e.Data.GetData(DataFormats.FileDrop));
            _chat.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsImage()) { e.SuppressKeyPress = true; SaveClipboardImageAndAsk(); } };
            _chat.BackColor = ColWindow;
            _chat.ForeColor = ColText;
            _chat.BorderStyle = BorderStyle.None;
            _chat.Font = new Font("Segoe UI", 10.5F);

            // Строка ввода: рамка-«пилюля» + кнопка отправки
            var inputRow = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = ColWindow, Padding = new Padding(14, 8, 14, 10) };
            var sendHost = new Panel { Dock = DockStyle.Right, Width = 122, BackColor = ColWindow, Padding = new Padding(10, 1, 0, 1) };
            _send.Dock = DockStyle.Fill;
            _send.Click += (s, e) => Send();
            sendHost.Controls.Add(_send);

            var inputShell = new BorderedBox { Dock = DockStyle.Fill, BackColor = ColBorder, Padding = new Padding(1), Radius = 12 };
            var inputInner = new Panel { Dock = DockStyle.Fill, BackColor = ColCardAlt, Padding = new Padding(10, 8, 10, 8) };
            _input.Dock = DockStyle.Fill;
            _input.BackColor = ColCardAlt;
            _input.ForeColor = ColText;
            _input.BorderStyle = BorderStyle.None;
            _input.Font = new Font("Segoe UI", 10.5F);
            _input.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsImage()) { e.SuppressKeyPress = true; SaveClipboardImageAndAsk(); return; }
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Send(); }
            };
            inputInner.Controls.Add(_input);
            inputShell.Controls.Add(inputInner);
            inputRow.Controls.Add(inputShell);
            inputRow.Controls.Add(sendHost);

            // Быстрые команды — кнопки-пилюли; активная задача подсвечивается
            var quickRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, BackColor = ColWindow, Padding = new Padding(10, 5, 10, 5) };
            foreach (var (label, cmd) in new[] { ("🎯 Обучение 100м", "обучение 100м"), ("⏸ Пауза", "обучение стоп"), ("▶ Старт", "обучение старт"), ("📊 Статус", "обучение"), ("🧠 Датасет 50", "датасет 50") })
            {
                var b = new RoundButton
                {
                    Text = label,
                    AutoSize = true,
                    MinimumSize = new Size(0, 32),
                    Padding = new Padding(11, 5, 11, 5),
                    Fill = ColCard,
                    FillHover = ColBorder,
                    Fore = ColMuted,
                    Radius = 16,
                };
                b.Click += (s, e) => { _input.Text = cmd; Send(); };
                _quick[cmd] = b;
                quickRow.Controls.Add(b);
            }

            chatPanel.Controls.Add(_chat);
            chatPanel.Controls.Add(quickRow);
            chatPanel.Controls.Add(inputRow);

            // ── Правая половина: панель обучения ──
            var side = new Panel { Dock = DockStyle.Right, Width = 400, Padding = new Padding(4, 10, 12, 10), BackColor = ColWindow };

            // Карточка «Текущее задание»
            var taskCard = new Card { Dock = DockStyle.Top, Height = 92, BackColor = ColCard, Padding = new Padding(14, 10, 14, 12) };
            var taskTitle = new Label { Text = "ТЕКУЩЕЕ ЗАДАНИЕ", Dock = DockStyle.Top, Height = 20, ForeColor = ColMuted, BackColor = ColCard, Font = new Font("Segoe UI", 8F, FontStyle.Bold) };
            _taskValue.Text = "— нет —";
            _taskValue.ForeColor = ColMuted;
            _taskValue.BackColor = ColCard;
            _taskValue.Dock = DockStyle.Fill;
            _taskValue.Font = new Font("Segoe UI", 11.5F, FontStyle.Bold);
            _taskValue.TextAlign = ContentAlignment.MiddleLeft;
            var stopHost = new Panel { Dock = DockStyle.Right, Width = 132, BackColor = ColCard, Padding = new Padding(10, 0, 0, 0) };
            _stopTask.Dock = DockStyle.Fill;
            _stopTask.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _stopTask.Enabled = false;
            _stopTask.Click += (s, e) => { _input.Text = "обучение стоп"; Send(); };
            stopHost.Controls.Add(_stopTask);
            taskCard.Controls.Add(_taskValue);
            taskCard.Controls.Add(stopHost);
            taskCard.Controls.Add(taskTitle);

            // Карточка «Прогресс»
            var progressCard = new Card { Dock = DockStyle.Top, Height = 264, BackColor = ColCard, Padding = new Padding(14, 10, 14, 12) };
            var progressTitle = new Label { Text = "ПРОГРЕСС ОБУЧЕНИЯ", Dock = DockStyle.Top, Height = 20, ForeColor = ColMuted, BackColor = ColCard, Font = new Font("Segoe UI", 8F, FontStyle.Bold) };
            _pairsValue.Text = "—";
            _pairsValue.ForeColor = ColAccent;
            _pairsValue.BackColor = ColCard;
            _pairsValue.Dock = DockStyle.Top;
            _pairsValue.Height = 38;
            _pairsValue.Font = new Font("Segoe UI", 15.5F, FontStyle.Bold);
            _pairsValue.TextAlign = ContentAlignment.MiddleLeft;
            _bar.Dock = DockStyle.Top;
            _bar.Height = 24;
            _bar.BackColor = ColCard;
            _bar.Margin = new Padding(0, 4, 0, 4);

            var statsRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 58, BackColor = ColCard, ColumnCount = 3, RowCount = 1 };
            for (int i = 0; i < 3; i++) statsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            statsRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            statsRow.Controls.Add(MiniStat("СТАТЕЙ", ColText, out _articlesValue), 0, 0);
            statsRow.Controls.Add(MiniStat("КОРНЕЙ", ColText, out _vocabValue), 1, 0);
            statsRow.Controls.Add(MiniStat("ТЕМП", ColBlue, out _rateValue), 2, 0);

            var sysBox = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = ColCard, Padding = new Padding(0, 6, 0, 0) };
            var sysCaption = new Label { Text = "СИСТЕМА", Dock = DockStyle.Top, Height = 15, ForeColor = ColMuted, BackColor = ColCard, Font = new Font("Segoe UI", 7F, FontStyle.Bold) };
            _systemValue.Text = "—";
            _systemValue.ForeColor = ColText;
            _systemValue.BackColor = ColCard;
            _systemValue.Dock = DockStyle.Fill;
            _systemValue.Font = new Font("Segoe UI", 8.75F);
            _systemValue.TextAlign = ContentAlignment.TopLeft;
            sysBox.Controls.Add(_systemValue);
            sysBox.Controls.Add(sysCaption);

            var stageBox = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = ColCard, Padding = new Padding(0, 6, 0, 0) };
            var stageCaption = new Label { Text = "ПЛАН", Dock = DockStyle.Top, Height = 15, ForeColor = ColMuted, BackColor = ColCard, Font = new Font("Segoe UI", 7F, FontStyle.Bold) };
            _stageValue.Text = "—";
            _stageValue.ForeColor = ColPurple;
            _stageValue.BackColor = ColCard;
            _stageValue.Dock = DockStyle.Fill;
            _stageValue.Font = new Font("Segoe UI", 8.75F);
            _stageValue.TextAlign = ContentAlignment.TopLeft;
            stageBox.Controls.Add(_stageValue);
            stageBox.Controls.Add(stageCaption);

            progressCard.Controls.Add(stageBox);
            progressCard.Controls.Add(sysBox);
            progressCard.Controls.Add(statsRow);
            progressCard.Controls.Add(_bar);
            progressCard.Controls.Add(_pairsValue);
            progressCard.Controls.Add(progressTitle);

            // Карточка «Лента» — заполняет остаток
            var feedCard = new Card { Dock = DockStyle.Fill, BackColor = ColCard, Padding = new Padding(10, 10, 10, 10) };
            var feedTitle = new Label { Text = "ЛЕНТА ОБУЧЕНИЯ", Dock = DockStyle.Top, Height = 20, ForeColor = ColMuted, BackColor = ColCard, Font = new Font("Segoe UI", 8F, FontStyle.Bold) };
            _feed.Dock = DockStyle.Fill;
            _feed.BackColor = ColWindow;
            _feed.ForeColor = ColMuted;
            _feed.BorderStyle = BorderStyle.None;
            _feed.Font = new Font("Consolas", 8.5F);
            _feed.DrawMode = DrawMode.OwnerDrawFixed;
            _feed.ItemHeight = 18;
            _feed.HorizontalScrollbar = false;
            _feed.DrawItem += DrawFeedItem;
            feedCard.Controls.Add(_feed);
            feedCard.Controls.Add(feedTitle);

            // Порядок добавления важен: Dock обрабатывается с конца коллекции
            side.Controls.Add(feedCard);
            side.Controls.Add(Spacer());
            side.Controls.Add(progressCard);
            side.Controls.Add(Spacer());
            side.Controls.Add(taskCard);

            Controls.Add(chatPanel);
            Controls.Add(side);
            Controls.Add(header);
        }

        private static Panel Spacer() => new() { Dock = DockStyle.Top, Height = 8, BackColor = ColWindow };

        private static Panel MiniStat(string caption, Color valueColor, out Label value)
        {
            var box = new Panel { Dock = DockStyle.Fill, BackColor = ColCardAlt, Padding = new Padding(8, 6, 8, 6), Margin = new Padding(0, 6, 8, 0) };
            var cap = new Label { Text = caption, Dock = DockStyle.Top, Height = 15, ForeColor = ColMuted, BackColor = Color.Transparent, Font = new Font("Segoe UI", 7F, FontStyle.Bold) };
            value = new Label { Text = "—", Dock = DockStyle.Fill, ForeColor = valueColor, BackColor = Color.Transparent, Font = new Font("Segoe UI", 11F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            box.Controls.Add(value);
            box.Controls.Add(cap);
            return box;
        }

        private void DrawFeedItem(object? s, DrawItemEventArgs e)
        {
            if (e.Index < 0 || s is not ListBox lb) return;
            string text = lb.Items[e.Index].ToString() ?? "";
            Color color =
                text.Contains("отменена") || text.Contains("Начата") || text.Contains("прервано") || text.Contains("отменено") ? ColYellow :
                text.Contains("[🎓") ? ColPurple :
                text.Contains("[📈") ? ColBlue :
                text.Contains("✅") || text.Contains("[🏁") ? ColGreen :
                text.Contains("[⏹") || text.Contains("[▶") ? ColYellow :
                ColMuted;
            e.DrawBackground();
            TextRenderer.DrawText(e.Graphics, text, lb.Font, e.Bounds, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void Send()
        {
            string text = _input.Text.Trim();
            if (text.Length == 0 || !_botReady || _busy) return;
            _input.Text = "";
            AppendMessage("Ты", text, ColYellow, ColCardAlt);
            _busy = true;
            _send.Enabled = false;
            SetStatus(ColYellow, "Думаю…");

            Task.Run(() =>
            {
                string reply;
                try { reply = _bot!.Respond(text); }
                catch (Exception ex) { reply = "Ошибка: " + ex.Message; }
                BeginInvoke(() =>
                {
                    AppendMessage("ЕРМАКЪ AI", reply, ColAccent, ColCard);
                    _busy = false;
                    _send.Enabled = true;
                    RefreshPanel();
                    if (_bot!.ShouldExit) Close();
                });
            });
        }

        // Сообщение-карточка: серая метка времени + цветное имя, текст на плашке
        private void AppendMessage(string who, string text, Color nameColor, Color blockColor)
        {
            AppendRaw("  " + DateTime.Now.ToString("HH:mm") + "   ", Color.FromArgb(110, 118, 129), blockColor);
            AppendRaw(who, nameColor, blockColor);
            AppendRaw("\n  " + text.Replace("\n", "\n  ") + "\n", ColText, blockColor);
            AppendRaw("\n", ColText, _chat.BackColor);
            _chat.SelectionStart = _chat.TextLength;
            _chat.ScrollToCaret();
        }

        private void AppendRaw(string text, Color color, Color back)
        {
            _chat.SelectionStart = _chat.TextLength;
            _chat.SelectionLength = 0;
            _chat.SelectionColor = color;
            _chat.SelectionBackColor = back;
            _chat.AppendText(text);
        }

        private void SetStatus(Color dotColor, string text)
        {
            _dot.ForeColor = dotColor;
            _status.Text = text;
        }

        // === КАРТИНКИ: drag&drop и Ctrl+V ===
        private void HandleIncomingImages(string[] paths)
        {
            foreach (string p in paths)
            {
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                {
                    try
                    {
                        string dir = Path.Combine(AppContext.BaseDirectory, "images");
                        Directory.CreateDirectory(dir);
                        string dest = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss_") + Path.GetFileName(p));
                        File.Copy(p, dest, true);
                        AppendMessage("Ты", "[🖼 картинка: " + Path.GetFileName(dest) + "]", ColYellow, ColCardAlt);
                        AskAboutImage(dest, "");
                    }
                    catch (Exception ex)
                    {
                        AppendMessage("ЕРМАКЪ AI", "🖼 Не смог принять файл: " + ex.Message, ColAccent, ColCard);
                    }
                }
                else
                {
                    AppendMessage("Ты", "[файл: " + Path.GetFileName(p) + "]", ColYellow, ColCardAlt);
                    AppendMessage("ЕРМАКЪ AI", "Файл «" + Path.GetFileName(p) + "» принял, но пока разбираю только картинки (png/jpg). Текст скажи словами.", ColAccent, ColCard);
                }
            }
        }

        private void SaveClipboardImageAndAsk()
        {
            try
            {
                var img = Clipboard.GetImage();
                if (img == null) return;
                string dir = Path.Combine(AppContext.BaseDirectory, "images");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "paste_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                AppendMessage("Ты", "[🖼 картинка из буфера]", ColYellow, ColCardAlt);
                AskAboutImage(path, "");
            }
            catch (Exception ex)
            {
                AppendMessage("ЕРМАКЪ AI", "🖼 Не смог сохранить картинку из буфера: " + ex.Message, ColAccent, ColCard);
            }
        }

        private void AskAboutImage(string path, string note)
        {
            if (_bot == null || !_botReady) { AppendMessage("ЕРМАКЪ AI", "Ещё загружаюсь — секунду.", ColAccent, ColCard); return; }
            _busy = true;
            _send.Enabled = false;
            SetStatus(ColYellow, "Смотрю на картинку…");
            Task.Run(() =>
            {
                string reply;
                try { reply = _bot!.RespondImage(path, note); }
                catch (Exception ex) { reply = "Ошибка: " + ex.Message; }
                BeginInvoke(() =>
                {
                    AppendMessage("ЕРМАКЪ AI", reply, ColAccent, ColCard);
                    _busy = false;
                    _send.Enabled = true;
                    RefreshPanel();
                });
            });
        }

        private void RefreshPanel()
        {
            if (_bot == null) return;
            try
            {
                var st = _bot.GetStats();
                long target = _bot.GetTarget();
                long pairs = st.pairs;
                _pairsValue.Text = target > 0 ? $"{pairs:N0} / {target:N0}" : $"{pairs:N0}";
                double frac = target > 0 ? 1.0 * pairs / target : 1.0 * pairs / 100_000_000;
                _bar.Set(Math.Clamp(frac, 0, 1), $"{Math.Clamp(frac, 0, 1) * 100:0.#} %");
                _articlesValue.Text = st.words.ToString("N0");
                _vocabValue.Text = st.vocab.ToString("N0");
                _rateValue.Text = _bot.IsPaused() ? "—" : $"~{(long)_ratePerHour:N0}/ч";
                _stageValue.Text = _bot.CurriculumOn ? _bot.CurriculumStatus() : "выключен (свободный режим)";
                _systemValue.Text = $"база {st.sizeMb:0.#} МБ · LM: {_bot.LlmBadge}";

                // Текущая задача: карточка + подсветка кнопки + статус в шапке
                string cur = _bot.GetCurrentTask();
                bool has = cur != "нет";
                _taskValue.Text = has ? cur : "— нет —";
                _taskValue.ForeColor = has ? ColAccent : ColMuted;
                _stopTask.Enabled = has;
                foreach (var kv in _quick) kv.Value.Active = false;
                if (cur.StartsWith("спринт", StringComparison.Ordinal) && _quick.TryGetValue("обучение 100м", out var sprintBtn)) sprintBtn.Active = true;
                else if (cur.StartsWith("датасет", StringComparison.Ordinal) && _quick.TryGetValue("датасет 50", out var dsBtn)) dsBtn.Active = true;

                if (!_botReady) SetStatus(ColYellow, "Загрузка базы знаний…");
                else if (_busy) SetStatus(ColYellow, "Думаю…");
                else if (has) SetStatus(ColGreen, "Выполняю: " + cur);
                else if (_bot.IsPaused() || !_bot.LearningEnabled) SetStatus(ColMuted, "Готов · обучение выключено (кнопка «Старт» включит)");
                else SetStatus(ColGreen, "Готов · фоновое обучение активно");

                var events = Out.RecentEvents;
                if (events.Count != _lastEventCount)
                {
                    _lastEventCount = events.Count;
                    _feed.BeginUpdate();
                    _feed.Items.Clear();
                    foreach (string ev in events.TakeLast(60)) _feed.Items.Add(ev);
                    if (_feed.Items.Count > 0) _feed.TopIndex = _feed.Items.Count - 1;
                    _feed.EndUpdate();
                }
            }
            catch { }
        }

        // Темп считаем по скользящему окну 2 минуты и показываем только когда
        // обучение реально идёт: если связей не прибавлялось ~минуту — показываем «—».
        private readonly Queue<(DateTime t, long pairs)> _rateWindow = new();
        private double _ratePerHour;
        private DateTime _lastGrowthUtc = DateTime.MinValue;
        private long _lastPairsSnapshot = -1;
        private bool _rateActive;

        private void UpdateRate(long pairs)
        {
            var now = DateTime.UtcNow;
            if (_lastPairsSnapshot >= 0 && pairs > _lastPairsSnapshot) _lastGrowthUtc = now;
            _lastPairsSnapshot = pairs;
            _rateWindow.Enqueue((now, pairs));
            while (_rateWindow.Count > 1 && (now - _rateWindow.Peek().t).TotalMinutes > 2) _rateWindow.Dequeue();
            if (_rateWindow.Count >= 2)
            {
                var first = _rateWindow.Peek();
                double minutes = (now - first.t).TotalMinutes;
                if (minutes >= 0.25)
                    _ratePerHour = (pairs - first.pairs) / minutes * 60.0;
            }
            _rateActive = (now - _lastGrowthUtc).TotalSeconds <= 60;
        }

        // Обновляем темп отдельным тиком, чтобы окно было длиннее 1 секунды
        private System.Windows.Forms.Timer? _rateTimer;
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _rateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _rateTimer.Tick += (s, e2) => { if (_bot != null) UpdateRate(_bot.GetStats().pairs); };
            _rateTimer.Start();
        }
    }

    // ── Элементы тёмной темы ──

    // Кнопка-пилюля со скруглением и подсветкой при наведении; Active — акцентная рамка
    internal class RoundButton : Button
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Radius { get; set; } = 10;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color Fill { get; set; } = Color.FromArgb(33, 38, 45);
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color FillHover { get; set; } = Color.FromArgb(48, 54, 61);
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color Fore { get; set; } = Color.FromArgb(230, 237, 243);
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color Border { get; set; } = Color.FromArgb(48, 54, 61);
        private bool _active;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Active
        {
            get => _active;
            set { _active = value; Invalidate(); }
        }

        private bool _hover;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Color.Black);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = UiFx.Rounded(rect, Radius);
            Color fill = Active ? Color.FromArgb(96, 20, 24) : _hover ? FillHover : Fill;
            Color border = Active ? Color.FromArgb(246, 110, 112) : Border;
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var p = new Pen(border, 1f)) g.DrawPath(p, path);
            Color textColor = !Enabled ? Color.FromArgb(110, 118, 129) : Fore;
            TextRenderer.DrawText(g, Text, Font, rect, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // Прогресс-бар: скруглённая дорожка + зелёная заливка + процент
    internal class ProgressPanel : Panel
    {
        private double _value;
        private string _caption = "";

        public ProgressPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }

        public void Set(double value, string caption)
        {
            _value = value;
            _caption = caption;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Color.Black);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var track = UiFx.Rounded(rect, 8);
            using (var b = new SolidBrush(Color.FromArgb(56, 30, 32))) g.FillPath(b, track);
            int w = (int)(Width * Math.Clamp(_value, 0, 1));
            if (w > 14)
            {
                using var fillPath = UiFx.Rounded(new Rectangle(0, 0, w - 1, Height - 1), 8);
                using var green = new SolidBrush(Color.FromArgb(216, 54, 60));
                g.FillPath(green, fillPath);
            }
            TextRenderer.DrawText(g, _caption, Font, rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // Карточка со скруглёнными углами и тонкой рамкой
    internal class Card : Panel
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Radius { get; set; } = 12;

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using var path = UiFx.Rounded(new Rectangle(0, 0, Width, Height), Radius);
            Region = new Region(path);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = UiFx.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
            using var p = new Pen(Color.FromArgb(96, 36, 40), 1f);
            e.Graphics.DrawPath(p, path);
            // алый акцент слева на каждой карточке
            using (var stripe = new SolidBrush(Color.FromArgb(150, 229, 72, 77)))
                e.Graphics.FillRectangle(stripe, 1, Radius / 2, 3, Height - Radius);
        }
    }

    // Прямоугольник со скруглением (рамка строки ввода)
    internal class BorderedBox : Panel
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Radius { get; set; } = 12;

        public BorderedBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using var path = UiFx.Rounded(new Rectangle(0, 0, Width, Height), Radius);
            Region = new Region(path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        }
    }

    internal static class UiFx
    {
        public static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            if (radius <= 0) { path.AddRectangle(r); return path; }
            int d = Math.Min(Math.Min(r.Width, r.Height) / 2, radius) * 2;
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
