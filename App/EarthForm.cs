using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ии;

namespace ии
{
    // 🌍 МИКРО-МИР: фрактальные Питомцы-Страны.
    // Каждая Клетка-Страна = Профильный Питомец офиса; внутри — Младшие Питомцы,
    // ГосДума (голосования по весам фракций) и Книга жалоб.
    // Лог — через SpeechEngine: [ГОВОРЯЩИЙ]: жирным+подчёркнутым.
    internal class EarthForm : Form
    {
        private readonly ChatBot _bot;
        private readonly ListView _grid;
        private readonly RichTextBox _log;
        private readonly CheckBox _auto;
        private readonly System.Windows.Forms.Timer _tick = new() { Interval = 4000 };

        public EarthForm(ChatBot bot)
        {
            _bot = bot;
            Text = "🌍 Микро-мир — фрактальные Питомцы-Страны";
            Size = new Size(1180, 660);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(13, 20, 32);

            // Guarded-подписки: до создания контролов события просто копятся в мир.db
            HierarchySim.Speak += (who, text) => { try { if (!IsHandleCreated || _log == null) return; BeginInvoke(() => SpeechEngine.Speak(_log, who, text)); } catch { } };
            HierarchySim.BigEvent += (text) => { try { if (!IsHandleCreated || _log == null) return; BeginInvoke(() => SpeechEngine.Event(_log, "⚡ " + text, Color.FromArgb(242, 163, 127))); } catch { } };
            HierarchySim.SyncFromOffice(_bot.GetOffice());
            HierarchySim.Load();

            var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

            _grid = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = Color.FromArgb(13, 20, 32),
                ForeColor = Color.FromArgb(232, 238, 245),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.5F),
            };
            _grid.Columns.Add("Страна-Питомец", 150);
            _grid.Columns.Add("Роль (сфера)", 150);
            _grid.Columns.Add("ВВП", 70);
            _grid.Columns.Add("Казна", 70);
            _grid.Columns.Add("Ресурсы", 75);
            _grid.Columns.Add("Воен.", 55);
            _grid.Columns.Add("Недов.", 55);
            _grid.Columns.Add("Лидер", 95);
            _grid.Columns.Add("Младшие", 95);
            _grid.Columns.Add("HitRate", 65);
            left.Controls.Add(_grid);

            Button Mk(string text) => new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                ForeColor = Color.FromArgb(232, 238, 245),
                BackColor = Color.FromArgb(23, 33, 50),
                Font = new Font("Segoe UI", 9.5F),
                Cursor = Cursors.Hand,
                Margin = new Padding(3),
            };
            var tickBtn = Mk("⏩ Тик мира");
            var warBtn = Mk("⚔️ Война");
            var dumaBtn = Mk("🏛 ГосДума");
            var congBtn = Mk("🌍 Конгресс");
            var saveBtn = Mk("💾 Сохранить");
            _auto = new CheckBox { Text = " Авто-тик", Checked = true, ForeColor = Color.FromArgb(147, 164, 184), AutoSize = true, Padding = new Padding(8, 10, 0, 0) };
            tickBtn.Click += (s, e) => DoTick();
            warBtn.Click += (s, e) => HierarchySim.Speak?.Invoke("ХРОНИКА ВОЙНЫ", HierarchySim.War());
            dumaBtn.Click += (s, e) => DumaSession();
            congBtn.Click += (s, e) => HierarchySim.Speak?.Invoke("ВСЕМИРНЫЙ КОНГРЕСС", HierarchySim.Congress("глобальный союз и распределение ресурсов"));
            saveBtn.Click += (s, e) => HierarchySim.Save();
            _auto.CheckedChanged += (s, e) => _tick.Enabled = _auto.Checked;
            foreach (var b in new[] { tickBtn, warBtn, dumaBtn, congBtn, saveBtn })
            {
                b.MouseEnter += (s2, e2) => b.BackColor = Color.FromArgb(35, 50, 75);
                b.MouseLeave += (s2, e2) => b.BackColor = Color.FromArgb(23, 33, 50);
            }
            var buttons = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 40, BackColor = Color.FromArgb(13, 20, 32), ColumnCount = 6, RowCount = 1 };
            for (int i = 0; i < 6; i++) buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, i == 0 ? 14F : 17.2F));
            buttons.Controls.Add(_auto, 0, 0);
            buttons.Controls.Add(tickBtn, 1, 0);
            buttons.Controls.Add(warBtn, 2, 0);
            buttons.Controls.Add(dumaBtn, 3, 0);
            buttons.Controls.Add(congBtn, 4, 0);
            buttons.Controls.Add(saveBtn, 5, 0);
            left.Controls.Add(buttons);

            _log = new RichTextBox
            {
                Dock = DockStyle.Right,
                Width = 440,
                BackColor = Color.FromArgb(10, 16, 26),
                ForeColor = Color.FromArgb(232, 238, 245),
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10F),
                ReadOnly = true,
                WordWrap = true,
            };

            var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 10, 6), BackColor = Color.FromArgb(13, 20, 32) };
            logHost.Controls.Add(_log);
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, BackColor = Color.FromArgb(13, 20, 32) };
            split.Panel1.BackColor = Color.FromArgb(13, 20, 32);
            split.Panel2.BackColor = Color.FromArgb(13, 20, 32);
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(logHost);

            var hdr = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                Name = "hdr",
                Text = "🌍 МИКРО-МИР",
                ForeColor = Color.FromArgb(55, 220, 242),
                BackColor = Color.FromArgb(19, 28, 43),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
            };
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(_grid, true);
            Controls.Add(hdr);
            Controls.Add(split);

            _tick.Tick += (s, e) => DoTick();
            _tick.Start();
            RefreshGrid();
            SpeechEngine.Event(_log, "🌍 МИКРО-МИР СОТВОРЁН. Стран-питомцев: " + HierarchySim.Countries.Count + ". Младшие питомцы жестко привязаны к своим странам.", Color.FromArgb(55, 220, 242));
        }

        private void DoTick()
        {
            HierarchySim.SyncFromOffice(_bot.GetOffice());
            HierarchySim.DoTick();
            RefreshGrid();
        }

        private void DumaSession()
        {
            var act = HierarchySim.Countries.Where(c => !c.Fired).ToList();
            if (act.Count == 0) return;
            var c = act[DateTime.Now.Second % act.Count];
            var (_, report) = c.Duma.FastVote("улучшение инфраструктуры страны «" + c.Name + "»");
            HierarchySim.Speak?.Invoke("ГОСДУМА «" + c.Name + "»", report);
        }

        private void RefreshGrid()
        {
            _grid.BeginUpdate();
            _grid.Items.Clear();
            foreach (var c in HierarchySim.Countries)
            {
                var it = new ListViewItem("🌍 " + c.Name);
                it.SubItems.Add(c.Role);
                it.SubItems.Add(c.Gdp.ToString("N0"));
                it.SubItems.Add(c.Treasury.ToString("N0"));
                it.SubItems.Add(c.Resources.ToString("N0"));
                it.SubItems.Add(c.Military.ToString("N0"));
                it.SubItems.Add(c.Unrest + "%");
                it.SubItems.Add(c.Leader);
                it.SubItems.Add(c.SubPets.Count + " чел.");
                it.SubItems.Add(c.AvgHitRate + "%");
                it.ForeColor = c.Fired ? Color.Gray : (c.Unrest >= 60 ? Color.FromArgb(242, 163, 127) : Color.FromArgb(232, 238, 245));
                _grid.Items.Add(it);
            }
            _grid.EndUpdate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tick.Stop();
            HierarchySim.Save();
            base.OnFormClosed(e);
        }
    }
}
