using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ии
{
    // ═══════════════════════════════════════════════════════════════════════
    // МОДУЛЬ 1: РЕЧЕВОЙ ДВИЖОК (SpeechEngine)
    // Управляет выводом диалогов самосознающих агентов в RichTextBox.
    // ПРОТОКОЛ ИДЕНТИФИКАЦИИ РЕЧИ: имя говорящего всегда ЖИРНОЕ + ПОДЧЁРКНУТОЕ.
    // Оптимизация: кусочный append, WM_SETREDRAW (без промежуточных перерисовок),
    // EM_GET/SETSCROLLPOS (прокрутка не дёргается), минимум аллокаций в GUI-потоке.
    // ═══════════════════════════════════════════════════════════════════════
    internal static class SpeechEngine
    {
        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETSCROLLPOS = 0x0400 + 221;
        private const int EM_SETSCROLLPOS = 0x0400 + 222;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessagePoint(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

        // Заморозка отрисовки: нативный RichTextBox без этого мигает на каждый append
        public static void SuspendPaint(RichTextBox box)
        {
            try { SendMessage(box.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        public static void ResumePaint(RichTextBox box)
        {
            try
            {
                SendMessage(box.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                box.Invalidate();
            }
            catch { }
        }

        public static Point GetScroll(RichTextBox box)
        {
            var p = new Point();
            try { SendMessagePoint(box.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref p); } catch { }
            return p;
        }

        public static void SetScroll(RichTextBox box, Point p)
        {
            try { SendMessagePoint(box.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref p); } catch { }
        }

        // Пользователь у нижнего края? Если читает историю — прокрутку не трогаем.
        public static bool AtBottom(RichTextBox box)
        {
            try
            {
                if (box.TextLength == 0) return true;
                int lastY = box.GetPositionFromCharIndex(box.TextLength - 1).Y;
                return lastY <= box.ClientSize.Height - 4;
            }
            catch { return true; }
        }

        // Реплика агента: [ИМЯ]: — жирный+подчёркнутый, текст реплики — обычным шрифтом.
        // speakerColor — фирменный цвет гражданина (Ноль — оранжевый, Эйнштейн — циан, и т.д.)
        public static void Speak(RichTextBox box, string speaker, string text, Color? speakerColor = null)
        {
            bool pin = AtBottom(box);
            Point scroll = GetScroll(box);
            SuspendPaint(box);
            try
            {
                box.SelectionStart = box.TextLength;
                box.SelectionLength = 0;
                box.SelectionColor = speakerColor ?? Color.FromArgb(55, 220, 242);
                box.SelectionFont = new Font(box.Font, FontStyle.Bold | FontStyle.Underline);
                box.AppendText("[" + speaker + "]: ");
                box.SelectionColor = Color.FromArgb(232, 238, 245);
                box.SelectionFont = box.Font;
                box.AppendText(text);
                box.AppendText(Environment.NewLine);
                if (pin) { box.SelectionStart = box.TextLength; box.ScrollToCaret(); }
                else SetScroll(box, scroll);
            }
            finally { ResumePaint(box); }
        }

        // Заголовок системного события (Ноль/Парламент/Хаб) — жирная цветная строка
        public static void Event(RichTextBox box, string title, Color color)
        {
            bool pin = AtBottom(box);
            Point scroll = GetScroll(box);
            SuspendPaint(box);
            try
            {
                box.SelectionStart = box.TextLength;
                box.SelectionLength = 0;
                box.SelectionColor = color;
                box.SelectionFont = new Font(box.Font, FontStyle.Bold);
                box.AppendText(title + Environment.NewLine);
                box.SelectionFont = box.Font;
                if (pin) { box.SelectionStart = box.TextLength; box.ScrollToCaret(); }
                else SetScroll(box, scroll);
            }
            finally { ResumePaint(box); }
        }
    }
}
