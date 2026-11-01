using System.ComponentModel;

namespace ии
{
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
        private float _anim; // 0..1 — плавный переход наведения
        private readonly System.Windows.Forms.Timer _animT = new System.Windows.Forms.Timer { Interval = 15 };

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            TabStop = false;
            _animT.Tick += (s, e) => StepAnim();
        }

        // плавный hover: цвет катится к цели за ~120 мс, а не щёлкает мгновенно
        private void StepAnim()
        {
            float target = _hover ? 1f : 0f;
            if (Math.Abs(_anim - target) < 0.12f) { _anim = target; _animT.Stop(); }
            else _anim += target > _anim ? 0.12f : -0.12f;
            Invalidate();
        }

        private static Color Lerp(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        protected override void OnMouseEnter(EventArgs e) { _hover = true; _animT.Start(); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _animT.Start(); Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Color.Black);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = UiFx.Rounded(rect, Radius);
            Color fill = Active ? Color.FromArgb(96, 20, 24) : Lerp(Fill, FillHover, _anim);
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
