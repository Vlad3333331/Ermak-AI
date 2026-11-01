using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using ии;

namespace ии
{
    // 🌳 ОРГАНОГРАММА: дерево подчинения цифрового государства «Нейронный Хаб»
    internal class OrganogramForm : Form
    {
        public OrganogramForm(ChatBot bot)
        {
            Text = "🌳 Органограмма — цифровое государство «Нейронный Хаб»";
            Size = new Size(620, 620);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(13, 20, 32);

            var tree = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(13, 20, 32),
                ForeColor = Color.FromArgb(232, 238, 245),
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10.5F),
                ItemHeight = 26,
                Indent = 24,
            };
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(tree, true);

            var head = new Label
            {
                Dock = DockStyle.Top,
                Height = 46,
                Text = "👑  ЦИФРОВОЕ ГОСУДАРСТВО «НЕЙРОННЫЙ ХАБ»",
                ForeColor = Color.FromArgb(55, 220, 242),
                BackColor = Color.FromArgb(19, 28, 43),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            };
            var sys = tree.Nodes.Add("👑 Система «Нейронный Хаб» (владыка — Создатель Влад)");

            var edu = sys.Nodes.Add("🏫 Образование, голосование и рейтинги (развитие ИИ-человека)");
            edu.Nodes.Add("Мозг координатора: 32,5 млн связей · 106 тыс. статей · holdout-экзамен");
            edu.Nodes.Add("Закон стимула: успех +2 к рейтингу · саботаж −15 · отстающие — под трибунал");
            edu.Nodes.Add("Стоп-кран: " + (ChatBot.Muted ? "ВКЛЮЧЁН (Протокол Тишины)" : "выключен"));
            try
            {
                string pj = Path.Combine(AppContext.BaseDirectory, "парламент_журнал.txt");
                if (File.Exists(pj))
                {
                    var all = File.ReadAllLines(pj);
                    foreach (var ln in all.Skip(Math.Max(0, all.Length - 6)))
                        edu.Nodes.Add("🗳 " + ln);
                }
            }
            catch { }

            var par = sys.Nodes.Add("🏛 Цифровой Парламент — 50 депутатов · 5 партий · порог 26 голосов");
            foreach (var pt in new[]
            {
                "Консерваторы «Стабильность» — 10 мест",
                "Либералы «Права Граждан» — 10 мест",
                "Технократы «Прогресс и Эффективность» — 10 мест",
                "Праведники «Высший Смысл и Чистота» — 10 мест",
                "Центристы «Баланс Сил» — 10 мест",
            })
                par.Nodes.Add(pt);

            var zero = sys.Nodes.Add("Ноль — супервизор (оптимизация, кадры, каратель) · рейтинг доверия: " + bot.ZeroTrust);
            var ein = sys.Nodes.Add("Эйнштейн — координатор (универсальный разум) 🛡 защищён конституцией");

            foreach (var p in bot.GetOffice())
            {
                var host = p.Parent == "Ноль" ? zero : ein;
                var node = host.Nodes.Add(p.Name + " — " + p.Role + " · рейтинг " + p.Score + (p.Fired ? " · УВОЛЕН 😔" : "") + " · сотрудник: " + p.Parent);
                node.ForeColor = p.Fired
                    ? Color.Gray
                    : (p.Parent == "Ноль" ? Color.Orange : Color.FromArgb(55, 220, 242));
            }

            sys.Expand();
            edu.Expand();
            zero.Expand();
            ein.Expand();
            par.Expand();
            Controls.Add(head);
            Controls.Add(tree);

            var hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 34,
                Text = "Ноль не имеет прав на Эйнштейна · радикальные действия Ноля — через Парламент · Судный день: 40 из 50 голосов",
                ForeColor = Color.FromArgb(147, 164, 184),
                BackColor = Color.FromArgb(19, 28, 43),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8.5F),
            };
            Controls.Add(hint);
        }
    }
}
