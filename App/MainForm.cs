using System.Diagnostics;
using System.Text.Json;
using ии;

namespace ии
{
    // === ЕРМАКЪ — интерфейс в стиле ZCode ===
    // Слева: панель задач (New task, список задач с поиском, Automations, Skills).
    // Центр: чат (пузыри пользователя справа, ответы с разметкой слева).
    // Справа: карточка прогресса. Внизу: строка ввода с выбором режима и модели.
    internal class MainForm : Form
    {
        // ── Палитра ZCode ──
        private static readonly Color ColBg      = Color.FromArgb(30, 31, 36);
        private static readonly Color ColSide    = Color.FromArgb(22, 23, 27);
        private static readonly Color ColCard    = Color.FromArgb(40, 41, 48);
        private static readonly Color ColCardHi  = Color.FromArgb(52, 53, 61);
        private static readonly Color ColBorder  = Color.FromArgb(62, 63, 72);
        private static readonly Color ColText    = Color.FromArgb(240, 240, 243);
        private static readonly Color ColMuted   = Color.FromArgb(158, 160, 170);
        private static readonly Color ColAccent  = Color.FromArgb(235, 130, 60);
        private static readonly Color ColGreen   = Color.FromArgb(126, 231, 135);
        private static readonly Color ColBlue    = Color.FromArgb(130, 180, 245);
        private static readonly Color ColYellow  = Color.FromArgb(210, 153, 34);
        private static readonly Color ColRed     = Color.FromArgb(240, 100, 95);
        private static readonly Color ColUser    = Color.FromArgb(56, 58, 68);
        private static readonly Color ColInput   = Color.FromArgb(45, 46, 54);

        private ChatBot? _bot;
        private bool _botReady;
        private bool _busy;
        private readonly System.Diagnostics.Stopwatch _answerWatch = new();
        private readonly System.Windows.Forms.Timer _thinkTimer = new();
        private DateTime _lastUserActivity = DateTime.Now;

        // ── Сессии (задачи) ──
        private bool _currentDeleted; // текущий диалог удалён — при закрытии НЕ сохранять его
        private string _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        private string _sessionTitle = "Новый диалог";
        private readonly List<(string who, string text)> _messages = new();
        private readonly List<(string id, string title, DateTime started)> _sessionList = new();
        private string SessionsDir => Path.Combine(AppContext.BaseDirectory, "sessions");

        // ── Элементы ──
        private readonly ListBox _taskList = new();
        private readonly TextBox _searchBox = new();
        private readonly Label _headerTitle = new();
        private readonly RichTextBox _chat = new();
        private readonly TextBox _input = new();
        private Panel? _inputZone; // строка ввода: растёт по мере набора, как в ZCode
        private readonly RoundButton _send = new();
        private readonly RoundButton _modeBtn = new();
        private readonly RoundButton _modelBtn = new();
        private readonly RoundButton _petBtn = new();
        private readonly RoundButton _muteBtn = new();
        private int _petIdx = 0; // первый клик сразу выбирает первого питомца
        private readonly List<string> _models = new();
        private int _modelIndex;
        private readonly RichTextBox _progressText = new();
        private readonly Label _dot = new();
        private readonly Label _statusLbl = new();
        private readonly System.Windows.Forms.Timer _timer = new();
        private readonly Queue<(DateTime t, long pairs)> _rateWindow = new();
        private double _ratePerHour;
        private System.Windows.Forms.Timer? _rateTimer;

        public MainForm()
        {
            Text = "ЕРМАКЪ";
            Width = 1420;
            Height = 880;
            MinimumSize = new Size(1150, 700);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ColBg;
            ForeColor = ColText;
            Font = new Font("Segoe UI", 9.75F);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            LoadSessions();
            RenderTaskList();
            UpdateHeaderTitle();

            SetStatus(ColYellow, "Загрузка базы знаний…");
            Task.Run(() =>
            {
                try { _bot = new ChatBot(); }
                catch (Exception ex) { BeginInvoke(() => SetStatus(ColRed, "Ошибка загрузки: " + ex.Message)); return; }
                ChatBot.StreamHook += OnStreamDelta;
                ChatBot.ThinkHook += OnThinkDelta;
                ChatBot.WorkHook += s => BeginInvoke(() => SetStatus(ColYellow, s));
                AgentChat.Start(_bot);
                BeginInvoke(() =>
                {
                    _botReady = true;
                    AppendBot("Здравствуйте! Я **Ермакъ** — конфиденциальный локальный ИИ-ассистент для анализа документов, книг и страниц.\nРаботаю полностью на вашем компьютере: тексты никуда не уходят.\nЧем могу помочь?", save: false);
                });
            });

            _timer.Interval = 1000;
            _timer.Tick += (s, e) => RefreshProgress();
            _timer.Start();

            // Эйнштейн САМ решает, когда писать: тик каждые 3 с и вероятность —
            // как у живого человека. Иногда через 5 минут, иногда через 20. Сам.
            _thinkTimer.Interval = 3000;
            _thinkTimer.Tick += (s, e) =>
            {
                if (!_botReady || _busy) return;
                // Он человек: когда писать, когда молчать и когда «спать» — решает только он сам
                if (Random.Shared.Next(100) >= 2) return;
                _busy = true;
                Task.Run(() =>
                {
                    string? act;
                    try { act = _bot!.SelfEinsteinAct(); }
                    catch { _busy = false; return; }
                    if (act == null) { _busy = false; return; } // решил молчать
                    BeginInvoke(() =>
                    {
                        AppendBot("💭 " + act);
                        _busy = false;
                    });
                });
            };
            _thinkTimer.Start();
        }

        // ══════════ ИНТЕРФЕЙС ══════════
        private void BuildUi()
        {
            // ── Левая панель ──
            var sidebar = new Panel { Dock = DockStyle.Left, Width = 252, BackColor = ColSide, Padding = new Padding(10, 12, 8, 8) };

            var newTask = MakeSideItem("＋  New task", "Ctrl+N", () => StartNewSession());
            newTask.Location = new Point(0, 8);
            newTask.Size = new Size(232, 34);

            var automations = MakeSideItem("⚙  Automations", "", () => ShowAutomations());
            automations.Location = new Point(0, 46);
            automations.Size = new Size(232, 32);

            var market = MakeSideItem("🧩  Skills — навыки", "", () => ShowSkills());
            market.Location = new Point(0, 80);
            market.Size = new Size(232, 32);

            var organogram = MakeSideItem("🌳  Органограмма", "", () =>
            {
                if (_bot == null) { SetStatus(ColYellow, "Ещё загружаюсь…"); return; }
                new OrganogramForm(_bot).Show();
            });
            organogram.Location = new Point(0, 114);
            organogram.Size = new Size(232, 32);

            var agentChat = MakeSideItem("💬  Чат агентов", "", () =>
            {
                if (_bot == null) { SetStatus(ColYellow, "Ещё загружаюсь…"); return; }
                new AgentChatForm().Show();
            });
            agentChat.Location = new Point(0, 148);
            agentChat.Size = new Size(232, 32);

            var micro = MakeSideItem("🌍  Микро-мир", "", () =>
            {
                if (_bot == null) { SetStatus(ColYellow, "Ещё загружаюсь…"); return; }
                new EarthForm(_bot).Show();
            });
            micro.Location = new Point(0, 182);
            micro.Size = new Size(232, 32);

            var tasksLbl = new Label { Text = "TASK LIST", Font = new Font("Segoe UI", 7.5F, FontStyle.Bold), ForeColor = ColMuted, BackColor = ColSide, Location = new Point(6, 222), AutoSize = true };
            var delBtn = new RoundButton { Text = "🗑", Size = new Size(28, 22), Location = new Point(206, 218), Fill = ColSide, FillHover = Color.FromArgb(80, 30, 30), Fore = ColMuted, Radius = 6, Font = new Font("Segoe UI", 9F) };
            delBtn.Click += (s, e) => DeleteSelectedTask();

            _searchBox.Location = new Point(4, 240);
            _searchBox.Width = 226;
            _searchBox.Height = 26;
            _searchBox.BackColor = ColInput;
            _searchBox.ForeColor = ColText;
            _searchBox.BorderStyle = BorderStyle.FixedSingle;
            _searchBox.TextChanged += (s, e) => RenderTaskList();
            _searchBox.PlaceholderText = "Поиск по задачам…";

            _taskList.Location = new Point(0, 268);
            _taskList.Size = new Size(236, 999);
            _taskList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _taskList.BackColor = ColSide;
            _taskList.ForeColor = ColText;
            _taskList.BorderStyle = BorderStyle.None;
            _taskList.DrawMode = DrawMode.OwnerDrawFixed;
            _taskList.ItemHeight = 46;
            _taskList.DrawItem += DrawTaskItem;
            // удаление: правый клик → меню, или клавиша Delete
            var taskMenu = new ContextMenuStrip();
            taskMenu.Items.Add("🗑 Удалить диалог", null, (s, e) => DeleteSelectedTask());
            taskMenu.Items.Add("🗑 Удалить все диалоги", null, (s, e) =>
            {
                if (MessageBox.Show("Удалить ВСЕ диалоги без возврата?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    try { foreach (string f in Directory.GetFiles(SessionsDir, "*.json")) File.Delete(f); } catch { }
                    _sessionList.Clear();
                    RenderTaskList();
                    StartNewSession();
                    AppendBot("🗑 Все диалоги удалены.");
                }
            });
            _taskList.ContextMenuStrip = taskMenu;
            _taskList.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; DeleteSelectedTask(); }
            };
            _taskList.SelectedIndexChanged += (s, e) =>
            {
                if (_taskList.SelectedIndex < 0 || _taskList.Items.Count == 0) return;
                if (_taskList.Tag is List<(string id, string title, DateTime started)> visible && _taskList.SelectedIndex < visible.Count)
                    OpenSession(visible[_taskList.SelectedIndex].id);
            };

            var avatar = new Label { Text = "V", Font = new Font("Segoe UI", 11F, FontStyle.Bold), ForeColor = Color.White, BackColor = ColAccent, Size = new Size(30, 30), TextAlign = ContentAlignment.MiddleCenter, Location = new Point(6, sidebar.Height - 42) };
            avatar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            var gear = new Label { Text = "⚙", Font = new Font("Segoe UI", 12F), ForeColor = ColMuted, BackColor = ColSide, AutoSize = true, Cursor = Cursors.Hand, Location = new Point(208, sidebar.Height - 40) };
            gear.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            gear.Click += (s, e) => ShowAutomations();

            sidebar.Controls.AddRange(new Control[] { newTask, automations, market, organogram, agentChat, micro, tasksLbl, delBtn, _searchBox, _taskList, avatar, gear });

            // ── Правая карточка прогресса ──
            var progressWrap = new Panel { Dock = DockStyle.Right, Width = 248, BackColor = ColBg, Padding = new Padding(10, 52, 12, 10) };
            var progressCard = new Panel { Dock = DockStyle.Fill, BackColor = ColCard, Padding = new Padding(14, 12, 12, 12) };
            progressCard.Resize += (s, e) =>
            {
                using var path = UiFx.Rounded(new Rectangle(0, 0, progressCard.Width, progressCard.Height), 12);
                progressCard.Region = new Region(path);
                progressCard.Invalidate();
            };
            progressCard.Paint += (s, e) =>
            {
                using var path = UiFx.Rounded(new Rectangle(0, 0, progressCard.Width - 1, progressCard.Height - 1), 12);
                using var pen = new Pen(ColBorder, 1f);
                e.Graphics.DrawPath(pen, path);
            };
            var progressTitle = new Label { Text = "Progress", Dock = DockStyle.Top, Height = 22, ForeColor = ColText, BackColor = ColCard, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            _progressText.Dock = DockStyle.Fill;
            _progressText.BackColor = ColCard;
            _progressText.ForeColor = ColText;
            _progressText.BorderStyle = BorderStyle.None;
            _progressText.Font = new Font("Segoe UI", 9.25F);
            _progressText.ReadOnly = true;
            _progressText.Cursor = Cursors.Arrow;
            _progressText.HideSelection = true;
            _progressText.TabStop = false;
            _progressText.Text = "…";
            progressCard.Controls.Add(_progressText);
            progressCard.Controls.Add(progressTitle);
            progressWrap.Controls.Add(progressCard);

            // ── Центр ──
            var center = new Panel { Dock = DockStyle.Fill, BackColor = ColBg };

            var header = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = ColBg, Padding = new Padding(14, 0, 10, 0) };
            header.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Color.FromArgb(48, 48, 48)), 0, header.Height - 1, header.Width, header.Height - 1);
            _headerTitle.Font = new Font("Segoe UI", 10.5F);
            _headerTitle.ForeColor = ColText;
            _headerTitle.BackColor = ColBg;
            _headerTitle.Dock = DockStyle.Left;
            _headerTitle.AutoSize = true;
            _headerTitle.TextAlign = ContentAlignment.MiddleLeft;
            _dot.Text = "●";
            _dot.Font = new Font("Segoe UI", 10F);
            _dot.ForeColor = ColYellow;
            _dot.BackColor = ColBg;
            _dot.Dock = DockStyle.Right;
            _dot.Padding = new Padding(0, 0, 8, 0);
            _statusLbl.Text = "Запуск…";
            _statusLbl.ForeColor = ColMuted;
            _statusLbl.BackColor = ColBg;
            _statusLbl.Dock = DockStyle.Right;
            _statusLbl.Padding = new Padding(0, 0, 4, 0);
            _statusLbl.AutoSize = true;
            var copyBtn = new RoundButton { Text = "⧉ Копировать", Size = new Size(104, 26), Fill = ColBg, FillHover = ColCard, Fore = ColMuted, Radius = 8, Font = new Font("Segoe UI", 8.5F), Cursor = Cursors.Hand };
            copyBtn.Dock = DockStyle.Right;
            copyBtn.Padding = new Padding(0, 0, 6, 0);
            copyBtn.Click += (s, e) =>
            {
                for (int i = _messages.Count - 1; i >= 0; i--)
                    if (_messages[i].who == "bot") { Clipboard.SetText(_messages[i].text); SetStatus(ColGreen, "Ответ скопирован"); return; }
            };
            var regenBtn = new RoundButton { Text = "⟳ Заново", Size = new Size(84, 26), Fill = ColBg, FillHover = ColCard, Fore = ColMuted, Radius = 8, Font = new Font("Segoe UI", 8.5F), Cursor = Cursors.Hand };
            regenBtn.Dock = DockStyle.Right;
            regenBtn.Padding = new Padding(0, 0, 6, 0);
            regenBtn.Click += (s, e) =>
            {
                for (int i = _messages.Count - 1; i >= 0; i--)
                    if (_messages[i].who == "user") { _input.Text = _messages[i].text; Send(); return; }
            };
            header.Controls.Add(copyBtn);
            header.Controls.Add(regenBtn);
            header.Controls.Add(_statusLbl);
            header.Controls.Add(_dot);
            header.Controls.Add(_headerTitle);

            _chat.Dock = DockStyle.Fill;
            _chat.ReadOnly = true;
            _chat.BackColor = ColBg;
            _chat.ForeColor = ColText;
            _chat.RightToLeft = RightToLeft.No;
            _chat.BorderStyle = BorderStyle.None;
            _chat.Font = new Font("Segoe UI", 10F);
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(_chat, true);
            // Двойная буферизация нативного контрола: без неё печать дрожит
            SetComposited(_chat);
            _chat.HandleCreated += (s2, e2) => SetComposited(_chat);

            var inputZone = new Panel { Dock = DockStyle.Bottom, Height = 96, BackColor = ColBg, Padding = new Padding(150, 10, 16, 12) };
            _inputZone = inputZone;
            var inputShell = new Panel { Dock = DockStyle.Fill, BackColor = ColBorder, Padding = new Padding(1) };
            inputShell.Resize += (s, e) => { inputShell.Region = new Region(UiFx.Rounded(new Rectangle(0, 0, inputShell.Width, inputShell.Height), 16)); inputShell.Invalidate(); };
            var inputInner = new Panel { Dock = DockStyle.Fill, BackColor = ColInput, Padding = new Padding(10, 6, 10, 6) };

            var plusBtn = new RoundButton { Text = "＋", Size = new Size(34, 30), Location = new Point(2, 4), Fill = ColInput, FillHover = ColCardHi, Fore = ColMuted, Radius = 8 };
            plusBtn.Click += (s, e) => { PickImage(); _input.Focus(); };

            _input.Location = new Point(40, 9);
            _input.Width = 300;
            _input.Height = 26;
            _input.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _input.Multiline = true;
            _input.RightToLeft = RightToLeft.No;
            _input.WordWrap = true;
            _input.ScrollBars = ScrollBars.None;
            _input.PlaceholderText = "Спроси Ермака…";
            _input.TextChanged += (s, e) => UpdateInputHeight();
            _input.Resize += (s, e) => UpdateInputHeight();
            _input.BackColor = ColInput;
            _input.ForeColor = ColText;
            _input.BorderStyle = BorderStyle.None;
            _input.Font = new Font("Segoe UI", 10.5F);
            _input.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsImage()) { e.SuppressKeyPress = true; SaveClipboardImageAndAsk(); return; }
                if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); }
            };

            bool fullAccess = ComputerUse.Enabled;
            _modeBtn.Text = fullAccess ? "🔓 Полный доступ" : "🛡 Обычный режим";
            _modeBtn.Size = new Size(126, 22);
            _modeBtn.Location = new Point(12, 40);
            _modeBtn.Fill = ColInput; _modeBtn.FillHover = ColCardHi;
            _modeBtn.Fore = fullAccess ? ColAccent : ColMuted;
            _modeBtn.Radius = 12;
            _modeBtn.Font = new Font("Segoe UI", 8.25F);
            _modeBtn.TextAlign = ContentAlignment.MiddleLeft;
            _modeBtn.Padding = new Padding(8, 0, 0, 0);
            _modeBtn.Click += (s, e) =>
            {
                bool full = _modeBtn.Text.StartsWith("🛡");
                _modeBtn.Text = full ? "🔓 Полный доступ" : "🛡 Обычный режим";
                _modeBtn.Fore = full ? ColAccent : ColMuted;
                ComputerUse.SetEnabled(full);
                AppendBot(full ? "🔓 **Полный доступ включён**: могу управлять компьютером, делать скриншоты и запускать программы." : "🔒 **Полный доступ выключен** — управление компьютером запрещено.");
                _input.Focus();
            };

            _modelBtn.Text = "🧠 Ермакъ (свой мозг)";
            _modelBtn.Size = new Size(148, 22);
            _modelBtn.Location = new Point(150, 40);
            _modelBtn.Fill = ColInput; _modelBtn.FillHover = ColCardHi;
            _modelBtn.Fore = ColBlue;
            _modelBtn.Radius = 12;
            _modelBtn.Font = new Font("Segoe UI", 8.25F);
            _modelBtn.TextAlign = ContentAlignment.MiddleLeft;
            _modelBtn.Padding = new Padding(8, 0, 0, 0);
            _modelBtn.Click += (s, e) => { CycleModel(); _input.Focus(); };

            _petBtn.Text = "🐾 Питомец: авто";
            _petBtn.Size = new Size(128, 22);
            _petBtn.Location = new Point(306, 40);
            _petBtn.Fill = ColInput; _petBtn.FillHover = ColCardHi;
            _petBtn.Fore = ColMuted; _petBtn.Radius = 12;
            _petBtn.Font = new Font("Segoe UI", 8.25F);
            _petBtn.TextAlign = ContentAlignment.MiddleLeft;
            _petBtn.Padding = new Padding(8, 0, 0, 0);
            _petBtn.Click += (s, e) =>
            {
                var names = new List<string> { "Авто" };
                names.AddRange(_bot?.GetOfficeNames() ?? new List<string>());
                if (names.Count > 1)
                {
                    _petIdx = (_petIdx + 1) % names.Count;
                    string sel = names[_petIdx];
                    _bot?.SetSelectedPet(sel == "Авто" ? "" : sel);
                    _petBtn.Text = sel == "Авто" ? "🐾 Питомец: авто" : "🐾 " + sel;
                    _petBtn.Fore = sel == "Авто" ? ColMuted : ColAccent;
                }
                _input.Focus();
            };
            inputInner.Controls.Add(_petBtn);

            _muteBtn.Text = "🔇";
            _muteBtn.Size = new Size(34, 22);
            _muteBtn.Location = new Point(444, 40);
            _muteBtn.Padding = new Padding(2, 0, 0, 0);
            _muteBtn.Fill = ColInput; _muteBtn.FillHover = ColCardHi;
            _muteBtn.Fore = ColMuted; _muteBtn.Radius = 12;
            _muteBtn.Font = new Font("Segoe UI", 8.25F);
            _muteBtn.TextAlign = ContentAlignment.MiddleCenter;
            _muteBtn.Padding = new Padding(0);
            _muteBtn.Click += (s, e) =>
            {
                ChatBot.Muted = !ChatBot.Muted;
                _muteBtn.Text = ChatBot.Muted ? "🔊" : "🔇";
                _muteBtn.Fore = ChatBot.Muted ? ColRed : ColMuted;
                SetStatus(ChatBot.Muted ? ColRed : ColGreen, ChatBot.Muted ? "🔇 Протокол Тишины: агенты остановлены" : "Готов");
                _input.Focus();
            };
            inputInner.Controls.Add(_muteBtn);

            inputInner.Controls.Add(_input);
            inputInner.Controls.Add(_send);
            inputInner.Controls.Add(plusBtn);
            inputInner.Controls.Add(_modeBtn);
            inputInner.Controls.Add(_modelBtn);
            inputShell.Controls.Add(inputInner);

            _send.Text = "↑";
            _send.Size = new Size(36, 36);
            _send.Location = new Point(inputShell.Width - 50, 8);
            _send.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _send.Fill = Color.FromArgb(27, 107, 122); _send.FillHover = Color.FromArgb(37, 140, 160);
            _send.Fore = Color.White; _send.Radius = 18;
            _send.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            _send.Cursor = Cursors.Hand;
            _send.Click += (s, e) => { Send(); _input.Focus(); };
            inputZone.Controls.Add(inputShell);

            center.Controls.Add(_chat);
            center.Controls.Add(inputZone);
            center.Controls.Add(header);

            Controls.Add(center);
            Controls.Add(progressWrap);
            Controls.Add(sidebar);

            // плавность: форма, сайдбар и список задач без мерцания
            DoubleBuffered = true;
            var dbProp = typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            dbProp?.SetValue(sidebar, true);
            dbProp?.SetValue(_taskList, true);

            LoadModelsIntoCombo();
        }

        private RoundButton MakeSideItem(string text, string hotkey, Action onClick)
        {
            var b = new RoundButton
            {
                Text = text + (hotkey.Length > 0 ? "    " + hotkey : ""),
                TextAlign = ContentAlignment.MiddleLeft,
                Fill = ColSide,
                FillHover = ColCardHi,
                Border = ColSide,
                Fore = ColText,
                Radius = 8,
                Font = new Font("Segoe UI", 9.5F),
                Cursor = Cursors.Hand
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        private void DrawTaskItem(object? s, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var b = new SolidBrush(selected ? ColCard : ColSide);
            e.Graphics.FillRectangle(b, e.Bounds);
            if (_taskList.Tag is not List<(string id, string title, DateTime started)> list) return;
            if (e.Index >= list.Count) return;
            var item = list[e.Index];
            TextRenderer.DrawText(e.Graphics, item.title, new Font("Segoe UI", 9.25F),
                new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 6, e.Bounds.Width - 20, 18),
                ColText, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, RelativeTime(item.started), new Font("Segoe UI", 8F),
                new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 26, e.Bounds.Width - 20, 14),
                ColMuted, TextFormatFlags.EndEllipsis);
        }

        private static string RelativeTime(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "только что";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
            return $"{(int)d.TotalDays}d";
        }

        // ══════════ СЕССИИ ══════════
        private void LoadSessions()
        {
            try
            {
                Directory.CreateDirectory(SessionsDir);
                _sessionList.Clear();
                foreach (string f in Directory.GetFiles(SessionsDir, "*.json"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        // автоочистка: диалоги без сообщений пользователя — мусор
                        bool hasUser = false;
                        if (doc.RootElement.TryGetProperty("messages", out var msgs))
                            foreach (var m in msgs.EnumerateArray())
                                if (m.TryGetProperty("who", out var w) && w.GetString() == "user") { hasUser = true; break; }
                        if (!hasUser) { File.Delete(f); continue; }
                        string id = Path.GetFileNameWithoutExtension(f);
                        string title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "Диалог" : "Диалог";
                        string started = doc.RootElement.TryGetProperty("started", out var st) ? st.GetString() ?? "" : "";
                        DateTime dt = DateTime.TryParse(started, out var d) ? d : File.GetCreationTime(f);
                        _sessionList.Add((id, title, dt));
                    }
                    catch { }
                }
                _sessionList.Sort((a, b) => b.started.CompareTo(a.started));
            }
            catch { }
        }

        private void RenderTaskList()
        {
            string filter = _searchBox.Text.Trim().ToLowerInvariant();
            var visible = new List<(string id, string title, DateTime started)>();
            foreach (var s in _sessionList)
                if (filter.Length == 0 || s.title.ToLowerInvariant().Contains(filter)) visible.Add(s);
            _taskList.Tag = visible;
            _taskList.BeginUpdate();
            _taskList.Items.Clear();
            foreach (var v in visible) _taskList.Items.Add(v);
            _taskList.EndUpdate();
        }

        private void UpdateHeaderTitle()
        {
            _headerTitle.Text = _sessionTitle;
        }

        private void DeleteSelectedTask()
        {
            if (_taskList.Tag is not List<(string id, string title, DateTime started)> list || _taskList.SelectedIndex < 0) return;
            string id = list[_taskList.SelectedIndex].id;
            bool wasCurrent = id == _sessionId;

            // открытый диалог больше не сохраняем — иначе он воскреснет после удаления
            if (wasCurrent) { _sessionId = ""; _messages.Clear(); _chat.Clear(); _currentDeleted = true; }

            try { string f = Path.Combine(SessionsDir, id + ".json"); if (File.Exists(f)) File.Delete(f); } catch { }
            _sessionList.RemoveAll(s => s.id == id);
            RenderTaskList();

            if (wasCurrent)
            {
                // открыть последний оставшийся диалог; если пусто — чистый новый
                if (_sessionList.Count > 0) OpenSession(_sessionList[0].id);
                else { _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss"); _sessionTitle = "Новый диалог"; _messages.Clear(); _chat.Clear(); UpdateHeaderTitle(); }
            }
        }

        private void StartNewSession()
        {
            // пустой диалог не сохраняем (и убираем его пустой файл, если был)
            if (_messages.Count == 0)
            {
                try { string f = Path.Combine(SessionsDir, _sessionId + ".json"); if (File.Exists(f)) File.Delete(f); } catch { }
                _sessionList.RemoveAll(s => s.id == _sessionId);
            }
            else SaveCurrentSession();

            _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _sessionTitle = "Новый диалог";
            _messages.Clear();
            _chat.Clear();
            UpdateHeaderTitle();
            if (_botReady) AppendBot("Новый диалог начат. Я **Ермакъ** — ваш локальный ИИ-ассистент. Чем помочь?", save: false);
            LoadSessions();
            RenderTaskList();
        }

        private void OpenSession(string id)
        {
            try
            {
                string path = Path.Combine(SessionsDir, id + ".json");
                if (!File.Exists(path)) return;
                SaveCurrentSession();
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                _sessionId = id;
                _messages.Clear();
                _chat.Clear();
                _sessionTitle = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "Диалог" : "Диалог";
                UpdateHeaderTitle();
                foreach (var m in doc.RootElement.GetProperty("messages").EnumerateArray())
                {
                    string who = m.TryGetProperty("who", out var w) ? w.GetString() ?? "" : "";
                    string text = m.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";
                    _messages.Add((who, text));
                    if (who == "user") AppendUser(text, save: false);
                    else AppendBot(text, save: false, instant: true);
                }
            }
            catch (Exception ex) { AppendBot("Не смог открыть диалог: " + ex.Message); }
        }

        private void SaveCurrentSession()
        {
            try
            {
                if (_messages.Count == 0) return;
                _currentDeleted = false;
                Directory.CreateDirectory(SessionsDir);
                var msgs = _messages.Select(m => new Dictionary<string, string> { ["who"] = m.who, ["text"] = m.text }).ToList();
                var obj = new Dictionary<string, object>
                {
                    ["title"] = _sessionTitle,
                    ["started"] = DateTime.Now.ToString("o"),
                    ["messages"] = msgs
                };
                File.WriteAllText(Path.Combine(SessionsDir, _sessionId + ".json"),
                    JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch { }
        }

        private void TouchSessionTitle(string firstUserMessage)
        {
            if (_sessionTitle != "Новый диалог") return;
            _sessionTitle = firstUserMessage.Length > 44 ? firstUserMessage.Substring(0, 44) + "…" : firstUserMessage;
            UpdateHeaderTitle();
            int idx = _sessionList.FindIndex(s => s.id == _sessionId);
            if (idx >= 0) _sessionList[idx] = (_sessionId, _sessionTitle, _sessionList[idx].started);
            else _sessionList.Insert(0, (_sessionId, _sessionTitle, DateTime.Now));
            RenderTaskList();
        }

        // ══════════ ЧАТ ══════════
        private void AppendUser(string text, bool save = true)
        {
            if (save) { _messages.Add(("user", text)); TouchSessionTitle(text); SaveCurrentSession(); }

            // подпись справа: Ты · время
            _chat.SelectionStart = _chat.TextLength;
            _chat.SelectionLength = 0;
            _chat.SelectionIndent = 24;
            _chat.SelectionRightIndent = 356;
            _chat.SelectionAlignment = HorizontalAlignment.Right;
            _chat.SelectionColor = ColMuted;
            _chat.SelectionFont = new Font("Segoe UI", 8F);
            _chat.AppendText("Ты · " + DateTime.Now.ToString("HH:mm") + "  \n");
            _chat.SelectionFont = Font;

            // пузырь сообщения
            _chat.SelectionAlignment = HorizontalAlignment.Right;
            _chat.SelectionFont = new Font("Segoe UI", 10F);
            _chat.SelectionColor = ColText;
            _chat.SelectionBackColor = ColUser;
            _chat.SelectionIndent = 24;
            _chat.SelectionRightIndent = 356;
            _chat.AppendText("   " + text + "     \n");

            _chat.SelectionAlignment = HorizontalAlignment.Left;
            _chat.SelectionBackColor = _chat.BackColor;
            _chat.SelectionFont = Font;
            _chat.AppendText("\n");
            _chat.SelectionStart = _chat.TextLength;
            _chat.ScrollToCaret();
        }

        private void AppendBot(string text, bool save = true, bool instant = false)
        {
            CloseThink();
            bool streamed = FinishStreamRegion();
            StopWorkLine();
            if (save)
            {
                _messages.Add(("bot", text));
                SaveCurrentSession();
                if (_answerWatch.IsRunning)
                {
                    _answerWatch.Stop();
                    double sec = _answerWatch.Elapsed.TotalSeconds;
                    if (sec >= 1)
                    {
                        _chat.SelectionStart = _chat.TextLength;
                        _chat.SelectionIndent = 24;
                        _chat.SelectionRightIndent = 24;
                        _chat.SelectionColor = ColMuted;
                        _chat.SelectionFont = new Font("Segoe UI", 8.5F, FontStyle.Italic);
                        _chat.AppendText($"Работал над ответом {sec:0} с\n");
                        _chat.SelectionFont = Font;
                    }
                }
            }
            if (!streamed)
            {
                _chat.SelectionStart = _chat.TextLength;
                _chat.SelectionLength = 0;
                _chat.SelectionIndent = 24;
                _chat.SelectionRightIndent = 340;
                _chat.SelectionColor = ColAccent;
                _chat.SelectionFont = new Font("Segoe UI", 8.5F, FontStyle.Bold);
                _chat.AppendText("ЕРМАКЪ · " + DateTime.Now.ToString("HH:mm") + "\n");
                _chat.SelectionFont = Font;
            }
            bool pinBot = AtBottom();
            SaveScroll();
            if (instant || streamed)
            {
                SuspendPaint();
                try
                {
                    AppendMarkdown(_chat, text);
                    _chat.AppendText("\n\n");
                    RestoreScrollIfAway(pinBot);
                }
                finally { ResumePaint(); }
            }
            else StartTyping(text);
        }

        // === ЭФФЕКТ ПЕЧАТИ: ответ появляется посимвольно, как будто он печатает ===
        private System.Windows.Forms.Timer? _typeTimer;
        private readonly List<(string Text, bool Bold, bool Code)> _typeQueue = new();
        private int _typeSeg, _typePos, _typeStep;

        private void StartTyping(string text)
        {
            FinishTyping(instant: true); // предыдущий ответ дописываем мгновенно

            var parts = System.Text.RegularExpressions.Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)");
            _typeQueue.Clear();
            foreach (string part in parts)
            {
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                    _typeQueue.Add((part[2..^2], true, false));
                else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2)
                    _typeQueue.Add((part[1..^1], false, true));
                else if (part.Length > 0)
                    _typeQueue.Add((part, false, false));
            }
            _typeSeg = 0; _typePos = 0;
            int total = _typeQueue.Sum(q => q.Text.Length);
            _typeStep = Math.Max(2, total / 220); // длинные ответы печатаем быстрее
            if (_typeTimer == null)
            {
                _typeTimer = new System.Windows.Forms.Timer { Interval = 24 };
                _typeTimer.Tick += (s, e) => TypeTick();
            }
            _typeTimer.Start();
        }

        private void TypeTick()
        {
            if (_typeSeg >= _typeQueue.Count) { FinishTyping(instant: false); return; }
            bool pin = AtBottom();
            SaveScroll();
            SuspendPaint();
            try
            {
                for (int i = 0; i < _typeStep && _typeSeg < _typeQueue.Count; i++)
                {
                    var (t, bold, code) = _typeQueue[_typeSeg];
                    if (_typePos == 0) SetSegFormat(bold, code);
                    int step = Math.Min(_typeStep, t.Length - _typePos);
                    _chat.AppendText(t.Substring(_typePos, step));
                    _typePos += step;
                    if (_typePos >= t.Length) { _typePos = 0; _typeSeg++; }
                }
                RestoreScrollIfAway(pin);
            }
            finally { ResumePaint(); }
        }

        private void SetSegFormat(bool bold, bool code)
        {
            if (code)
            {
                _chat.SelectionColor = ColText;
                _chat.SelectionBackColor = ColCard;
                _chat.SelectionFont = new Font("Consolas", 9.25F);
            }
            else if (bold)
            {
                _chat.SelectionColor = ColText;
                _chat.SelectionBackColor = _chat.BackColor;
                _chat.SelectionFont = new Font(_chat.Font, FontStyle.Bold);
            }
            else
            {
                _chat.SelectionBackColor = _chat.BackColor;
                _chat.SelectionFont = _chat.Font;
                _chat.SelectionColor = ColText;
            }
        }

        private void FinishTyping(bool instant)
        {
            if (_typeTimer == null) return;
            _typeTimer.Stop();
            while (_typeSeg < _typeQueue.Count)
            {
                var (t, bold, code) = _typeQueue[_typeSeg];
                if (_typePos == 0) SetSegFormat(bold, code);
                _chat.AppendText(instant ? t.Substring(_typePos) : t.Substring(_typePos, Math.Min(_typeStep, t.Length - _typePos)));
                _typePos = 0; _typeSeg++;
            }
            _typeQueue.Clear();
            _chat.AppendText("\n\n");
            _chat.SelectionStart = _chat.TextLength;
            _chat.ScrollToCaret();
        }

        // Мини-разметка: **жирный**, `код` — как в ZCode
        private void AppendMarkdown(RichTextBox box, string text)
        {
            // ПРОТОКОЛ ИДЕНТИФИКАЦИИ РЕЧИ: строки вида [Имя]: реплика —
            // имя говорящего всегда ЖИРНОЕ + ПОДЧЁРКНУТОЕ
            var lines = text.Split('\n');
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                var mSp = System.Text.RegularExpressions.Regex.Match(line, @"^\[([^\]\[]{1,40})\]:\s?");
                if (mSp.Success)
                {
                    box.SelectionStart = box.TextLength;
                    box.SelectionLength = 0;
                    box.SelectionColor = ColAccent;
                    box.SelectionFont = new Font(box.Font, FontStyle.Bold | FontStyle.Underline);
                    box.AppendText("[" + mSp.Groups[1].Value + "]: ");
                    box.SelectionFont = box.Font;
                    line = line.Substring(mSp.Length);
                }
                AppendMarkdownLine(box, line);
                if (li < lines.Length - 1) box.AppendText("\n");
            }
            box.SelectionBackColor = box.BackColor;
            box.SelectionFont = Font;
            box.SelectionColor = ColText;
        }

        private void AppendMarkdownLine(RichTextBox box, string text)
        {
            var parts = System.Text.RegularExpressions.Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)");
            foreach (string part in parts)
            {
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                {
                    box.SelectionColor = ColText;
                    box.SelectionFont = new Font(box.Font, FontStyle.Bold);
                    box.AppendText(part[2..^2]);
                }
                else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2)
                {
                    box.SelectionColor = ColText;
                    box.SelectionBackColor = ColCard;
                    box.SelectionFont = new Font("Consolas", 9.25F);
                    box.AppendText(part[1..^1]);
                }
                else
                {
                    box.SelectionBackColor = box.BackColor;
                    box.SelectionFont = box.Font;
                    box.SelectionColor = ColText;
                    box.AppendText(part);
                }
            }
            box.SelectionBackColor = box.BackColor;
            box.SelectionFont = Font;
            box.SelectionColor = ColText;
        }

        // === ЖИВОЙ СТРИМИНГ: мысль показывается по мере генерации (как DeepSeek/ZCode) ===
        private int _streamAnchor = -1;
        private bool _streamActive;
        private int _thinkAnchor = -1;
        private bool _thinkActive;
        private string _thinkBuf = "";
        private int _thinkShownLen;
        private int _workAnchor = -1;
        private System.Windows.Forms.Timer? _workTimer;
        private DateTime _workStart;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_COMPOSITED = 0x02000000;

        private void SetComposited(Control c)
        {
            try
            {
                if (!c.IsHandleCreated) return;
                int ex = GetWindowLong(c.Handle, GWL_EXSTYLE);
                SetWindowLong(c.Handle, GWL_EXSTYLE, ex | WS_EX_COMPOSITED);
            }
            catch { }
        }

        // Заморозка/разморозка перерисовки RichTextBox — чтобы экран не дрожал при стриме
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WmSetredraw = 0x000B;

        private void SuspendPaint()
        {
            try { SendMessage(_chat.Handle, WmSetredraw, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        private void ResumePaint()
        {
            try
            {
                SendMessage(_chat.Handle, WmSetredraw, (IntPtr)1, IntPtr.Zero);
                _chat.Invalidate();
            }
            catch { }
        }

        // Удаление хвоста текста (стрим и строка Working): ReadOnly RichTextBox игнорирует SelectedText
        private void DeleteFrom(int anchor)
        {
            try
            {
                bool ro = _chat.ReadOnly;
                _chat.ReadOnly = false;
                try
                {
                    _chat.SelectionStart = anchor;
                    _chat.SelectionLength = Math.Max(0, _chat.TextLength - anchor);
                    if (_chat.SelectionLength > 0) _chat.SelectedText = string.Empty;
                    _chat.SelectionStart = anchor;
                    _chat.SelectionLength = 0;
                }
                finally { _chat.ReadOnly = ro; }
            }
            catch { }
        }

        private void OnThinkDelta(string think)
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(() => OnThinkDelta(think)); return; }
                StopWorkLine();
                if (!_thinkActive)
                {
                    _chat.SelectionStart = _chat.TextLength;
                    _chat.SelectionLength = 0;
                    _chat.SelectionIndent = 24;
                    _chat.SelectionRightIndent = 24;
                    _chat.SelectionColor = ColMuted;
                    _chat.SelectionFont = new Font("Segoe UI", 8.5F, FontStyle.Italic);
                    _chat.AppendText("🤔 Думаю вслух:\n");
                    _thinkAnchor = _chat.TextLength;
                    _thinkActive = true;
                    _thinkBuf = "";
                }
                bool pin = AtBottom();
                SaveScroll();
                SuspendPaint();
                try
                {
                    _thinkBuf += think;
                    _chat.SelectionStart = _chat.TextLength;
                    _chat.SelectionLength = 0;
                    _chat.SelectionColor = ColMuted;
                    _chat.SelectionFont = new Font("Segoe UI", 8.5F, FontStyle.Italic);
                    _chat.AppendText(think);
                    _chat.SelectionFont = Font;
                    RestoreScrollIfAway(pin);
                }
                finally { ResumePaint(); }
            }
            catch { }
        }

        // Мысли закончились: длинный блок сжимаем до последних 300 знаков и отделяем от ответа
        private void CloseThink()
        {
            try
            {
                if (!_thinkActive) return;
                _thinkActive = false;
                if (_thinkBuf.Length > 350)
                {
                    SuspendPaint();
                    try
                    {
                        DeleteFrom(_thinkAnchor);
                        _chat.SelectionColor = ColMuted;
                        _chat.SelectionFont = new Font("Segoe UI", 8.5F, FontStyle.Italic);
                        _chat.AppendText("…" + _thinkBuf[^300..]);
                        _chat.SelectionFont = Font;
                    }
                    finally { ResumePaint(); }
                }
                _thinkAnchor = -1;
                _chat.SelectionStart = _chat.TextLength;
                _chat.SelectionLength = 0;
                _chat.AppendText("\n\n");
            }
            catch { }
        }

        private const int EmGetScrollPos = 0x400 + 221;
        private const int EmSetScrollPos = 0x400 + 222;
        private Point _savedScroll;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessagePoint(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

        private void SaveScroll()
        {
            try { SendMessagePoint(_chat.Handle, EmGetScrollPos, IntPtr.Zero, ref _savedScroll); } catch { }
        }

        // Внизу — едем за текстом; читаем историю — возвращаем сохранённый вид (ничего не тянет вниз)
        private void RestoreScrollIfAway(bool pin)
        {
            try
            {
                if (pin) { _chat.SelectionStart = _chat.TextLength; _chat.ScrollToCaret(); }
                else SendMessagePoint(_chat.Handle, EmSetScrollPos, IntPtr.Zero, ref _savedScroll);
            }
            catch { }
        }

        // Пользователь у нижнего края? Если читает историю — прокрутку не трогаем
        private bool AtBottom()
        {
            try
            {
                if (_chat.TextLength == 0) return true;
                int lastY = _chat.GetPositionFromCharIndex(_chat.TextLength - 1).Y;
                return lastY <= _chat.ClientSize.Height - 4;
            }
            catch { return true; }
        }

        private void OnStreamDelta(string partial)
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(() => OnStreamDelta(partial)); return; }
                CloseThink();
                StopWorkLine();
                bool pin = AtBottom();
                SaveScroll();
                if (!_streamActive)
                {
                    _chat.SelectionStart = _chat.TextLength;
                    _chat.SelectionLength = 0;
                    _chat.SelectionIndent = 24;
                    _chat.SelectionRightIndent = 24;
                    _chat.SelectionColor = ColAccent;
                    _chat.SelectionFont = new Font("Segoe UI", 8.5F, FontStyle.Bold);
                    _chat.AppendText("ЕРМАКЪ · " + DateTime.Now.ToString("HH:mm") + "\n");
                    _chat.SelectionFont = Font;
                    _streamAnchor = _chat.TextLength;
                    _streamActive = true;
                }
                SuspendPaint();
                try
                {
                    SetSegFormat(false, false);
                    _chat.AppendText(partial); // дописываем только новый кусок
                    RestoreScrollIfAway(pin);
                }
                finally { ResumePaint(); }
            }
            catch { }
        }

        // Убирает черновик стрима перед финальным выводом; true = стрим был
        private bool FinishStreamRegion()
        {
            try
            {
                if (!_streamActive) return false;
                DeleteFrom(_streamAnchor);
                return true;
            }
            finally { _streamActive = false; _streamAnchor = -1; }
        }

        // Живая строка «Working N с» — как в ZCode
        private void StartWorkLine()
        {
            try
            {
                _workStart = DateTime.Now;
                if (_workTimer == null)
                {
                    _workTimer = new System.Windows.Forms.Timer { Interval = 200 };
                    _workTimer.Tick += (s, e) => UpdateWorkLine();
                }
                _workTimer.Start();
                UpdateWorkLine();
            }
            catch { }
        }

        private void UpdateWorkLine()
        {
            try
            {
                bool pin = AtBottom();
                SaveScroll();
                SuspendPaint();
                if (_workAnchor >= 0)
                {
                    DeleteFrom(_workAnchor);
                }
                else
                {
                    _chat.SelectionStart = _chat.TextLength;
                }
                try
                {
                    _chat.SelectionLength = 0;
                    _chat.SelectionIndent = 24;
                    _chat.SelectionRightIndent = 24;
                    _chat.SelectionColor = ColMuted;
                    _chat.SelectionFont = new Font("Segoe UI", 8.5F, FontStyle.Italic);
                    _workAnchor = _chat.TextLength;
                    double sec = (DateTime.Now - _workStart).TotalSeconds;
                    _chat.AppendText($"⏳ Working {sec:0.0} с\n");
                    _chat.SelectionFont = Font;
                    RestoreScrollIfAway(pin);
                }
                finally { ResumePaint(); }
            }
            catch { }
        }

        private void StopWorkLine()
        {
            try
            {
                if (_workTimer != null) _workTimer.Stop();
                if (_workAnchor >= 0)
                {
                    DeleteFrom(_workAnchor);
                    _chat.SelectionStart = _chat.TextLength;
                }
                _workAnchor = -1;
            }
            catch { }
        }

        // Строка ввода растёт по мере набора (до ~7 строк), как в ZCode; Shift+Enter — новая строка
        private void UpdateInputHeight()
        {
            try
            {
                int lines = _input.GetLineFromCharIndex(Math.Max(0, _input.TextLength)) + 1;
                int h = Math.Clamp(lines * 18 + 8, 26, 150);
                if (_input.Parent != null)
                {
                    // поле тянется от плюс-кнопки до круглой «↑» — всё по одной линии
                    _input.Width = Math.Max(120, _input.Parent.ClientSize.Width - 54 - _input.Left);
                    _send.Location = new Point(_input.Parent.ClientSize.Width - 44, 5);
                }
                if (h != _input.Height)
                {
                    _input.Height = h;
                    _modeBtn.Top = 9 + h + 3;
                    _modelBtn.Top = _modeBtn.Top;
                    _petBtn.Top = _modeBtn.Top;
                    _muteBtn.Top = _modeBtn.Top;
                    if (_inputZone != null) _inputZone.Height = 96 + Math.Max(0, h - 26);
                }
            }
            catch { }
        }

        private void SetStatus(Color dotColor, string text)
        {
            _dot.ForeColor = dotColor;
            _statusLbl.Text = text;
        }

        private void Send()
        {
            string text = _input.Text.Trim();
            if (text.Length == 0 || !_botReady || _busy) return;
            _input.Text = "";
            _lastUserActivity = DateTime.Now;
            _bot?.NoteUserActivity(); // площадь затихает, пока Создатель занят
            AppendUser(text);
            _busy = true;
            _send.Enabled = false;
            SetStatus(ColYellow, "Думаю…");
            StartWorkLine();
            _answerWatch.Restart();

            Task.Run(() =>
            {
                string reply;
                try { reply = _bot!.Respond(text); }
                catch (Exception ex) { reply = "Ошибка: " + ex.Message; }
                BeginInvoke(() =>
                {
                    AppendBot(reply);
                    _busy = false;
                    _send.Enabled = true;
                    RefreshProgress();
                });
            });
        }

        // ══════════ КАРТИНКИ ══════════
        private void PickImage()
        {
            using var dlg = new OpenFileDialog { Filter = "Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "images");
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss_") + Path.GetFileName(dlg.FileName));
                File.Copy(dlg.FileName, dest, true);
                AppendUser("[🖼 картинка: " + Path.GetFileName(dest) + "]");
                AskAboutImage(dest, "");
            }
            catch (Exception ex) { AppendBot("🖼 Не смог принять файл: " + ex.Message); }
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
                AppendUser("[🖼 картинка из буфера]");
                AskAboutImage(path, "");
            }
            catch (Exception ex) { AppendBot("🖼 Ошибка буфера: " + ex.Message); }
        }

        private void AskAboutImage(string path, string note)
        {
            if (_bot == null || !_botReady) { AppendBot("Ещё загружаюсь — секунду."); return; }
            _busy = true;
            _send.Enabled = false;
            SetStatus(ColYellow, "Смотрю на картинку…");
            _answerWatch.Restart();
            Task.Run(() =>
            {
                string reply;
                try { reply = _bot!.RespondImage(path, note); }
                catch (Exception ex) { reply = "Ошибка: " + ex.Message; }
                BeginInvoke(() =>
                {
                    AppendBot(reply);
                    _busy = false;
                    _send.Enabled = true;
                    RefreshProgress();
                });
            });
        }

        // ══════════ ПРОГРЕСС ══════════
        private string _lastProgressText = "";

        private void RefreshProgress()
        {
            if (_bot == null)
            {
                // до готовности бота — ход загрузки нейронов
                int lp = Database.LoadProgress;
                if (lp > 0) _progressText.Text = "Загрузка нейронов…\n\n" + lp + " %";
                return;
            }
            try
            {
                var st = _bot.GetStats();
                long target = _bot.GetTarget();
                string cur = _bot.GetCurrentTask();
                bool learning = _bot.LearningEnabled && !_bot.IsPaused();
                                var sb = new System.Text.StringBuilder();
                sb.Append("✔  Нейроны  ").Append(st.pairs.ToString("N0")).Append(target > 0 ? "  /  " + target.ToString("N0") : "").Append('\n');
                sb.Append("✔  Статьи  ").Append(st.words.ToString("N0")).Append('\n');
                sb.Append("✔  Корни  ").Append(st.vocab.ToString("N0")).Append('\n');
                sb.Append(learning ? "→  Обучение  включено" : "✔  Обучение  выключено").Append('\n');
                sb.Append("→  Задача  ").Append(cur == "нет" ? "нет активной" : cur).Append('\n');
                sb.Append('\n');
                sb.Append("Темп  ").Append(learning ? $"~{_ratePerHour:N0}/ч" : "—").Append('\n');
                sb.Append("База  ").Append(st.sizeMb.ToString("0.#")).Append(" МБ\\n");
                sb.Append("Модель  ").Append(_bot.LlmBadge);
                string final = sb.ToString();
                if (final == _lastProgressText) return; // не изменилось — не перерисовываем
                _lastProgressText = final;

                _progressText.Clear();
                AppendProgress("✔  ", ColGreen, "Нейроны  ", ColText, st.pairs.ToString("N0") + (target > 0 ? " / " + target.ToString("N0") : ""), ColMuted);
                AppendProgress("✔  ", ColGreen, "Статьи  ", ColText, st.words.ToString("N0"), ColMuted);
                AppendProgress("✔  ", ColGreen, "Корни  ", ColText, st.vocab.ToString("N0"), ColMuted);
                if (learning) AppendProgress("→  ", ColAccent, "Обучение  ", ColText, "включено", ColGreen);
                else AppendProgress("✔  ", ColGreen, "Обучение  ", ColText, "выключено", ColMuted);
                AppendProgress("→  ", ColAccent, "Задача  ", ColText, cur == "нет" ? "нет активной" : cur, ColText);
                _progressText.AppendText("\\n");
                AppendProgress("", ColText, "Темп  ", ColText, learning ? $"~{_ratePerHour:N0}/ч" : "—", ColBlue);
                AppendProgress("", ColText, "База  ", ColText, st.sizeMb.ToString("0.#") + " МБ", ColMuted);
                AppendProgress("", ColText, "Модель  ", ColText, _bot.LlmBadge, ColBlue);

if (!_botReady) SetStatus(ColYellow, "Загрузка…");
                else if (_busy) SetStatus(ColYellow, "Думаю…");
                else if (_bot.IsPaused() || !_bot.LearningEnabled) SetStatus(ColMuted, "Обучение выключено");
                else if (cur != "нет") SetStatus(ColGreen, "Выполняю: " + cur);
                else SetStatus(ColGreen, "Готов");
            }
            catch { }
        }

        private void AppendProgress(string mark, Color markColor, string label, Color labelColor, string value, Color valueColor)
        {
            _progressText.SelectionStart = _progressText.TextLength;
            _progressText.SelectionLength = 0;
            _progressText.SelectionColor = markColor;
            _progressText.SelectionFont = new Font("Segoe UI", 9F, FontStyle.Bold);
            _progressText.AppendText(mark);
            _progressText.SelectionColor = labelColor;
            _progressText.SelectionFont = Font;
            _progressText.AppendText(label);
            _progressText.SelectionColor = valueColor;
            _progressText.AppendText(value + "\n");
        }

        // ══════════ МОДЕЛИ ══════════
        // Цикл моделей: Ермакъ (свой мозг) → модели LM Studio → снова
        private void CycleModel()
        {
            if (_models.Count == 0) return;
            _modelIndex = (_modelIndex + 1) % (_models.Count + 1);
            if (_modelIndex == 0)
            {
                _modelBtn.Text = "🧠 Ермакъ (свой мозг)";
                if (_bot != null) { _bot.SetUseLlm(false); AppendBot("🧠 Режим **Ермакъ (свой мозг)**: отвечаю сам, из своей базы — без LM Studio."); }
            }
            else
            {
                string m = _models[_modelIndex - 1];
                _modelBtn.Text = "🤖 " + m;
                if (_bot != null) { _bot.SetUseLlm(true); _bot.SetLlmModel(m); AppendBot($"Модель переключена на **{m}**."); }
            }
        }

        private void LoadModelsIntoCombo()
        {
            Task.Run(() =>
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    string json = http.GetStringAsync("http://127.0.0.1:1234/v1/models").GetAwaiter().GetResult();
                    using var doc = JsonDocument.Parse(json);
                    var models = new List<string>();
                    foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                    {
                        if (m.TryGetProperty("id", out var id))
                        {
                            string name = id.GetString() ?? "";
                            if (!name.Contains("embed", StringComparison.OrdinalIgnoreCase)) models.Add(name);
                        }
                    }
                    BeginInvoke(() =>
                    {
                        _models.Clear();
                        _models.AddRange(models);
                        if (_bot != null)
                        {
                            string current = _bot.LlmBadge.Replace("LM: ", "").Replace("выключена", "");
                            int idx = _models.IndexOf(current);
                            if (idx >= 0) { _modelIndex = idx + 1; _modelBtn.Text = "🤖 " + current; }
                        }
                    });
                }
                catch { }
            });
        }

        // ══════════ AUTOMATIONS / SKILLS ══════════
        private void ShowAutomations()
        {
            bool on = _bot?.LearningEnabled ?? true;
            string msg = on
                ? "Обучение сейчас ВКЛЮЧЕНО — фоновое чтение страниц работает."
                : "Обучение сейчас ВЫКЛЮЧЕНО (запомнено) — комп не нагружается.";
            var r = MessageBox.Show(this, msg + "\n\nВключить фоновое обучение?", "Automations", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (_bot != null) _bot.SetLearningEnabled(r == DialogResult.Yes);
            RefreshProgress();
        }

        private void ShowSkills()
        {
            string skills = Path.Combine(AppContext.BaseDirectory, "skills");
            MessageBox.Show(this,
                "Навыки Ермака лежат в папке:\n" + skills + "\n\n" +
                "🗣 общение — файлы «вопрос = ответ»\n" +
                "🖼 картинки — учебные картинки\n" +
                "💻 программирование — примеры кода\n\n" +
                "Добавь файлы — Ермак выучит их при следующем запуске.",
                "Skills — навыки", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (Directory.Exists(skills)) Process.Start(new ProcessStartInfo(skills) { UseShellExecute = true });
        }

        // ══════════ ТЕМП ══════════
        private void UpdateRate(long pairs)
        {
            var now = DateTime.UtcNow;
            _rateWindow.Enqueue((now, pairs));
            while (_rateWindow.Count > 1 && (now - _rateWindow.Peek().t).TotalMinutes > 2) _rateWindow.Dequeue();
            if (_rateWindow.Count >= 2)
            {
                var first = _rateWindow.Peek();
                double minutes = (now - first.t).TotalMinutes;
                if (minutes >= 0.25) _ratePerHour = (pairs - first.pairs) / minutes * 60.0;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _rateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _rateTimer.Tick += (s, e2) => { if (_bot != null) UpdateRate(_bot.GetStats().pairs); };
            _rateTimer.Start();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.N)) { StartNewSession(); return true; }
            if (keyData == (Keys.Control | Keys.M)) { if (_bot != null) new EarthForm(_bot).Show(); return true; }
            if (keyData == (Keys.Control | Keys.T)) { new AgentChatForm().Show(); return true; }
            if (keyData == (Keys.Control | Keys.G)) { if (_bot != null) new OrganogramForm(_bot).Show(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // удалённый или пустой диалог при закрытии НЕ сохраняем — удаление навсегда
            if (_currentDeleted || _messages.Count == 0)
            {
                try { string f = Path.Combine(SessionsDir, _sessionId + ".json"); if (File.Exists(f)) File.Delete(f); } catch { }
            }
            else SaveCurrentSession();
            base.OnFormClosing(e);
        }
    }
}
