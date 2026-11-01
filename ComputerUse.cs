using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace ии
{
    // === УПРАВЛЕНИЕ КОМПЬЮТЕРОМ ===
    // По умолчанию ВЫКЛЮЧЕНО. Включается:
    //   • файлом computer_use.txt рядом с exe (enabled = 1)
    //   • командой «компьютер вкл» (и выключается «компьютер выкл»)
    // Умеет: открывать/закрывать программы и сайты, работать с файлами и папками,
    // показывать нагрузку системы, выключать/перезагружать ПК (с подтверждением).
    internal static class ComputerUse
    {
        private static bool _enabled;

        public static bool Enabled => _enabled;

        // Хук описания изображений: ChatBot подставляет разбор картинки через LM Studio
        public static Func<string, string, string>? DescribeImage;
        // Хук анализа текста (для «прочитай выделенное»)
        public static Func<string, string>? AnalyzeTextHook;

        public static void Load()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "computer_use.txt");
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string t = line.Trim();
                        if (t.StartsWith("#") || !t.Contains('=')) continue;
                        string key = t.Substring(0, t.IndexOf('=')).Trim().ToLowerInvariant();
                        string val = t.Substring(t.IndexOf('=') + 1).Trim();
                        if (key == "enabled") _enabled = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch { }
        }

        public static void SetEnabled(bool on)
        {
            _enabled = on;
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "computer_use.txt");
                File.WriteAllText(path, "# Управление компьютером\n# 1 = разрешить ЕРМАКЪ управлять ПК, 0 = запретить\nenabled = " + (on ? "1" : "0") + "\n");
            }
            catch { }
        }

        private static string ConfirmPending = ""; // "shutdown" — ждём подтверждения

        // Возвращает true, если команда распознана (в т.ч. «компьютер вкл/выкл» — всегда)
        public static bool TryHandle(string input, string lower, out string reply)
        {
            reply = "";

            // Управление самим разрешением — доступно всегда
            if (lower.Contains("компьютер") && (lower.Contains("вкл") || lower.Contains("разреши") || lower.Contains("доступ")))
            {
                SetEnabled(true);
                reply = "🖥 Доступ к компьютеру ВКЛЮЧЁН. Умею:\n" +
                        "• «открой блокнот/калькулятор/сайт», «закрой X»\n" +
                        "• «сделай скриншот» — снимок экрана + описание\n" +
                        "• «прочитай выделенное» — выдели текст где угодно, я его проанализирую\n" +
                        "• «двинь мышь в X,Y», «кликни», «двойной клик», «набери текст …», «нажми клавиши ctrl+s»\n" +
                        "• цепочки: «открой блокнот и затем набери текст привет»\n" +
                        "• «создай папку/файл», «прочитай файл», «сколько памяти/места»\n" +
                        "• «выключи компьютер» (с подтверждением). Выключить доступ: «компьютер выкл».";
                return true;
            }
            if (lower.Contains("компьютер") && (lower.Contains("выкл доступ") || lower.Contains("запрети") || (lower.Contains("выкл") && !lower.Contains("выключи компьютер"))))
            {
                SetEnabled(false);
                reply = "🖥 Доступ к компьютеру ВЫКЛЮЧЕН. Включить: «компьютер вкл».";
                return true;
            }

            if (!_enabled) return false;

            // Подтверждение выключения ПК
            if (ConfirmPending == "shutdown")
            {
                if (lower.Contains("да") || lower.Contains("подтверждаю") || lower.Contains("выключи"))
                {
                    ConfirmPending = "";
                    try
                    {
                        Process.Start(new ProcessStartInfo("shutdown", "/s /t 30") { CreateNoWindow = true, UseShellExecute = false });
                        reply = "⚠ Компьютер выключится через 30 секунд. Отмена: «отмена выключения».";
                    }
                    catch (Exception ex) { reply = "Не смог: " + ex.Message; }
                    return true;
                }
                ConfirmPending = "";
                reply = "Выключение отменено.";
                return true;
            }

            // Выключение / перезагрузка (двухшаговое подтверждение)
            if (lower.Contains("выключи компьютер") || lower.Contains("выключить компьютер"))
            {
                ConfirmPending = "shutdown";
                reply = "⚠ Точно выключить компьютер? Подтверди: «да, выключи». Отмена — напиши что угодно другое.";
                return true;
            }
            if (lower.Contains("отмена выключения"))
            {
                ConfirmPending = "";
                try { Process.Start(new ProcessStartInfo("shutdown", "/a") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
                reply = "✅ Выключение отменено.";
                return true;
            }
            if (lower.Contains("перезагрузи компьютер"))
            {
                ConfirmPending = "";
                try { Process.Start(new ProcessStartInfo("shutdown", "/r /t 15") { CreateNoWindow = true, UseShellExecute = false }); reply = "⚠ Перезагрузка через 15 секунд."; }
                catch (Exception ex) { reply = "Не смог: " + ex.Message; }
                return true;
            }

            // === «ВИДИШЬ X?» — проверка запущенных программ ===
            System.Text.RegularExpressions.Match seeM = System.Text.RegularExpressions.Regex.Match(lower, @"^(?:видишь|видно ли|запущен ли|открыт ли|есть ли)\s+(.+)$");
            if (seeM.Success)
            {
                string name = seeM.Groups[1].Value.Trim().Trim('.', '!');
                string proc = AppAlias(name).Replace(".exe", "");
                var procs = Process.GetProcessesByName(proc);
                string info;
                if (procs.Length > 0)
                    info = "Да — «" + name + "» запущено.";
                else
                {
                    var hits = Process.GetProcesses()
                        .Where(pr => pr.ProcessName.ToLowerInvariant().Contains(proc) || proc.Contains(pr.ProcessName.ToLowerInvariant()))
                        .ToList();
                    info = hits.Count > 0
                        ? "Похоже, да — нашёл процесс «" + hits[0].ProcessName + "»."
                        : "Нет — «" + name + "» сейчас не запущено. Могу открыть: «открой " + name + "».";
                }
                reply = "🖥 " + info;
                return true;
            }

            // === «НАПИШИ В X ТЕКСТ» — фокус на окно программы и печать ===
            System.Text.RegularExpressions.Match wm = System.Text.RegularExpressions.Regex.Match(input, @"^(?:напиши|напечатай)\s+в\s+(\S+)\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (wm.Success)
            {
                string appRaw = wm.Groups[1].Value.Trim();
                string app = AppAlias(appRaw);
                string text = wm.Groups[2].Value.Trim();
                var procs = Process.GetProcessesByName(app);
                if (procs.Length == 0 || procs[0].MainWindowHandle == IntPtr.Zero)
                {
                    reply = $"🖥 «{appRaw}» не запущено (или без окна). Открой его — и скажи «напиши в {appRaw} {text}» ещё раз.";
                    return true;
                }
                SetForegroundWindow(procs[0].MainWindowHandle);
                System.Threading.Thread.Sleep(600);
                SendKeys.SendWait(EscapeForSendKeys(text));
                reply = $"⌨ Написал в «{appRaw}»: {text}";
                return true;
            }

            // Открыть программу / сайт
            Match om = Regex.Match(lower, @"^(?:открой|запусти)\s+(.+)$");
            if (om.Success)
            {
                string what = om.Groups[1].Value.Trim();
                string target = what.ToLowerInvariant() switch
                {
                    "блокнот" => "notepad.exe",
                    "калькулятор" => "calc.exe",
                    "проводник" => "explorer.exe",
                    "пейнт" or "паинт" => "mspaint.exe",
                    "диспетчер задач" => "taskmgr.exe",
                    "cmd" or "консоль" or "терминал" => "cmd.exe",
                    _ => ""
                };
                try
                {
                    if (target != "")
                    {
                        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                        reply = $"🖥 Открыл {what}.";
                    }
                    else if (what.Contains('.') || what.StartsWith("http"))
                    {
                        string url = what.StartsWith("http") ? what : "https://" + what;
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        reply = $"🌐 Открыл {url}.";
                    }
                    else
                    {
                        string alias = AppAlias(what);
                        try
                        {
                            Process.Start(new ProcessStartInfo(alias) { UseShellExecute = true });
                            reply = $"🖥 Открыл {what}.";
                        }
                        catch
                        {
                            // последний шанс: протокол вида «дискорд» → discord://
                            Process.Start(new ProcessStartInfo(alias + "://") { UseShellExecute = true });
                            reply = $"🖥 Пытаюсь открыть «{what}» через протокол.";
                        }
                    }
                }
                catch (Exception ex) { reply = "Не смог открыть: " + ex.Message; }
                return true;
            }

            // Закрыть программу
            Match km = Regex.Match(lower, @"^(?:закрой|убей процесс)\s+(.+)$");
            if (km.Success)
            {
                string name = km.Groups[1].Value.Trim().Replace(".exe", "");
                try
                {
                    var procs = Process.GetProcessesByName(name);
                    foreach (var pr in procs) pr.Kill();
                    reply = procs.Length > 0 ? $"🖥 Закрыл {name} ({procs.Length} процесс(ов))." : $"Процесс «{name}» не запущен.";
                }
                catch (Exception ex) { reply = "Не смог: " + ex.Message; }
                return true;
            }

            // Системная информация
            if (lower.Contains("сколько памяти") || lower.Contains("нагрузк") && lower.Contains("систем"))
            {
                try
                {
                    var ms = MemoryStatus.Get();
                    long myMb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
                    reply = $"🖥 ОЗУ: свободно {ms.FreeGb:0.#} ГБ из {ms.TotalGb:0.#} ГБ. Мой процесс: {myMb} МБ.";
                }
                catch { reply = "🖥 Не смог посчитать память."; }
                return true;
            }
            if (lower.Contains("места на диск"))
            {
                try
                {
                    var sb = new System.Text.StringBuilder("🖥 Диски:\n");
                    foreach (var d in System.IO.DriveInfo.GetDrives())
                    {
                        if (!d.IsReady) continue;
                        sb.Append($"  {d.Name} — свободно {d.AvailableFreeSpace / 1024 / 1024 / 1024} ГБ из {d.TotalSize / 1024 / 1024 / 1024} ГБ\n");
                    }
                    reply = sb.ToString();
                }
                catch (Exception ex) { reply = "Не смог: " + ex.Message; }
                return true;
            }

            // Файлы и папки
            Match fm = Regex.Match(lower, @"создай папку\s+(.+)$");
            if (fm.Success)
            {
                try { Directory.CreateDirectory(fm.Groups[1].Value.Trim()); reply = "📁 Папка создана: " + fm.Groups[1].Value.Trim(); }
                catch (Exception ex) { reply = "Не смог: " + ex.Message; }
                return true;
            }

            Match ff = Regex.Match(input, @"создай файл\s+(.+?)\s+с текстом\s+(.+)$", RegexOptions.IgnoreCase);
            if (ff.Success)
            {
                try { File.WriteAllText(ff.Groups[1].Value.Trim(), ff.Groups[2].Value.Trim()); reply = "📄 Файл создан: " + ff.Groups[1].Value.Trim(); }
                catch (Exception ex) { reply = "Не смог: " + ex.Message; }
                return true;
            }

            Match fr = Regex.Match(lower, @"прочитай файл\s+(.+)$");
            if (fr.Success)
            {
                try { reply = "📄 Содержимое:\n" + File.ReadAllText(fr.Groups[1].Value.Trim()); }
                catch (Exception ex) { reply = "Не смог: " + ex.Message; }
                return true;
            }

            Match fd = Regex.Match(lower, @"удали файл\s+(.+)$");
            if (fd.Success)
            {
                try { File.Delete(fd.Groups[1].Value.Trim()); reply = "🗑 Файл удалён: " + fd.Groups[1].Value.Trim(); }
                catch (Exception ex) { reply = "Не смог: " + ex.Message; }
                return true;
            }

            // === МЫШЬ И КЛАВИАТУРА ===
            Match mv = Regex.Match(lower, @"(?:двинь|перемести|поставь)\s+мышь\s+(?:в\s+|на\s+)?(\d{1,5})\s*[,\sxх ]\s*(\d{1,5})");
            if (mv.Success)
            {
                SetCursorPos(int.Parse(mv.Groups[1].Value), int.Parse(mv.Groups[2].Value));
                reply = "🖱 Мышь перемещена.";
                return true;
            }
            if (lower.Contains("двойной клик") || lower.Contains("дважды кликни"))
            {
                MouseClick(left: true, times: 2);
                reply = "🖱 Двойной клик сделал.";
                return true;
            }
            if (lower.Contains("кликни правой"))
            {
                MouseClick(left: false);
                reply = "🖱 Кликнул правой кнопкой.";
                return true;
            }
            if (lower.Contains("кликни") || lower.Contains("нажми мышку"))
            {
                MouseClick(left: true);
                reply = "🖱 Кликнул.";
                return true;
            }
            Match tm = Regex.Match(input, @"(?:набери|напечатай|впиши|напиши)\s+текст\s*[:\-]?\s*(.+)$", RegexOptions.IgnoreCase);
            if (tm.Success)
            {
                SendKeys.SendWait(EscapeForSendKeys(tm.Groups[1].Value));
                reply = "⌨ Текст набран.";
                return true;
            }
            Match km2 = Regex.Match(lower, @"нажми\s+(?:кнопку|клавишу)?\s*(ентер|enter|эскейп|esc|таб|tab|пробел|space|плюс|plus)");
            if (km2.Success)
            {
                string key = km2.Groups[1].Value switch
                {
                    "ентер" or "enter" => "{ENTER}",
                    "эскейп" or "esc" => "{ESC}",
                    "таб" or "tab" => "{TAB}",
                    "пробел" or "space" => " ",
                    _ => "{ENTER}"
                };
                SendKeys.SendWait(key);
                reply = "⌨ Нажал " + km2.Groups[1].Value + ".";
                return true;
            }

            // === ЦЕПОЧКИ ДЕЙСТВИЙ: «открой блокнот и затем набери текст привет» ===
            if (lower.Contains(" и затем ") || lower.Contains(" потом ") || lower.Contains(" и набери ") || lower.Contains(", затем ") || lower.Contains(" и напиши "))
            {
                var steps = System.Text.RegularExpressions.Regex.Split(input, @" и затем |, затем | потом | и набери ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (steps.Length > 1)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (string step in steps)
                    {
                        string st = step.Trim().Trim(',', '.');
                        if (st.Length < 2) continue;
                        if (TryHandle(st, st.ToLowerInvariant(), out string stepReply))
                            sb.AppendLine("• " + stepReply);
                        else
                            sb.AppendLine("• Не понял шаг: «" + st + "»");
                    }
                    reply = sb.ToString();
                    return true;
                }
            }

            // === ЧТЕНИЕ ВЫДЕЛЕННОГО (Ctrl+C → буфер → анализ) ===
            if (lower.Contains("прочитай выделенное") || lower.Contains("что я выделил") || lower.Contains("проанализируй выделенн"))
            {
                SendKeys.SendWait("^c");
                System.Threading.Thread.Sleep(400);
                string text = "";
                try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
                if (text.Trim().Length < 5)
                {
                    reply = "🖱 Ничего не выделено. Выдели текст мышкой в любой программе и повтори команду.";
                    return true;
                }
                reply = "📋 Прочитал выделенное (" + text.Length.ToString("N0") + " знаков):\n\n" +
                        (AnalyzeTextHook != null ? AnalyzeTextHook(text) : text.Substring(0, Math.Min(1500, text.Length)));
                return true;
            }

            // === КОПИРОВАНИЕ В БУФЕР ===
            System.Text.RegularExpressions.Match cm = System.Text.RegularExpressions.Regex.Match(input, @"скопируй\s+в\s+буфер\s+текст\s*[:\-]?\s*(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (cm.Success)
            {
                try { Clipboard.SetText(cm.Groups[1].Value.Trim()); reply = "📋 Текст скопирован в буфер."; }
                catch (Exception ex) { reply = "Не смог: " + ex.Message; }
                return true;
            }

            // === СОЧЕТАНИЯ КЛАВИШ: «нажми клавиши ctrl+s» ===
            System.Text.RegularExpressions.Match keyCombo = System.Text.RegularExpressions.Regex.Match(lower, @"нажми\s+клавиши\s+([a-zа-я0-9+ ]+)$");
            if (keyCombo.Success)
            {
                string combo = keyCombo.Groups[1].Value.Trim();
                reply = "⌨ Нажал: " + combo;
                try { SendKeys.SendWait(KeysToSendKeys(combo)); } catch (Exception ex) { reply = "Не смог нажать: " + ex.Message; }
                return true;
            }

            // === СВЕРНУТЬ ВСЕ ОКНА ===
            if (lower.Contains("сверни все окна") || lower.Contains("покажи рабочий стол"))
            {
                try
                {
                    using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "powershell", "-NoProfile -Command \"(New-Object -ComObject Shell.Application).MinimizeAll()\"")
                    { CreateNoWindow = true, UseShellExecute = false });
                    reply = "🖥 Свернул все окна.";
                }
                catch (Exception ex) { reply = "Не смог: " + ex.Message; }
                return true;
            }

            // === СЛУХ ===
            if (lower.Contains("что ты слышишь") || lower.Contains("что слышишь") ||
                lower.Contains("играет ли звук") || lower.Contains("есть ли звук"))
            {
                int lvl = SystemSoundLevel();
                string win = ForegroundWindowTitle();
                string about = win.Length > 3 ? " Влад сейчас смотрит: «" + win + "»." : "";
                reply = lvl < 0 ? "🔊 Слух не работает: аудиоустройство не определилось."
                    : lvl == 0 ? "🔊 Тишина — звуков вокруг не слышу." + about
                    : lvl < 10 ? "🔊 Еле слышны тихие звуки (" + lvl + "%)." + about
                    : lvl < 45 ? "🔊 Слышу умеренные звуки, громкость примерно " + lvl + "%." + about
                    : "🔊 Слышу громкие звуки — похоже, играет музыка или видео (" + lvl + "%)." + about;
                return true;
            }

            // === СКРИНШОТ ===
            if (lower.Contains("скриншот") || lower.Contains("скрин экрана") || lower.Contains("что у меня на экране"))
            {
                try
                {
                    string dir = Path.Combine(AppContext.BaseDirectory, "images");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                    var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                    using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    reply = "🖥 Скриншот сохранил: " + path;
                    if (DescribeImage != null)
                    {
                        string desc = DescribeImage(path, "что на экране? опиши кратко");
                        reply += "\n\n" + desc;
                    }
                }
                catch (Exception ex) { reply = "Не смог сделать скриншот: " + ex.Message; }
                return true;
            }

            return false;
        }

        // === СЛУХ: уровень системного звука (WASAPI loopback пик-метр), 0–100, -1 = не работает ===
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            int OpenPropertyStore(int stgmAccess, IntPtr store);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int GetState(out int state);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            int GetPeakValue(out float peak);
            int GetMeteringChannelCount(out int channels);
            int GetChannelsPeakValues(int channels, [In] float[] peaks);
            int QueryHardwareSupport(out int hardware);
        }

        private static readonly Guid IidIaudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

        /// <summary>Пик системного звука за ~150 мс (0–100). 0 = тишина, -1 = слух не работает.</summary>
        public static int SystemSoundLevel()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out IMMDevice device);
                device.Activate(IidIaudioMeterInformation, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object obj);
                var meter = (IAudioMeterInformation)obj;
                float peak = 0f;
                for (int i = 0; i < 6; i++)
                {
                    meter.GetPeakValue(out float p);
                    if (p > peak) peak = p;
                    Thread.Sleep(25);
                }
                return Math.Clamp((int)Math.Round(peak * 100f), 0, 100);
            }
            catch { return -1; }
        }

        /// <summary>Снимок экрана → JPEG data-URL для зрительной модели (или null).</summary>
        public static string? CaptureScreenDataUrl(int maxW = 1280, int quality = 60)
        {
            try
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                int nw = Math.Min(maxW, bmp.Width);
                int nh = bmp.Height * nw / bmp.Width;
                using var small = new System.Drawing.Bitmap(bmp, nw, nh);
                using var ms = new MemoryStream();
                var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                    .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                var encParams = new System.Drawing.Imaging.EncoderParameters(1);
                encParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                small.Save(ms, jpeg, encParams);
                return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            }
            catch { return null; }
        }

        private static void MouseClick(bool left, int times = 1)
        {
            for (int i = 0; i < times; i++)
            {
                mouse_event(left ? 0x02u : 0x08u, 0, 0, 0, UIntPtr.Zero);
                mouse_event(left ? 0x04u : 0x10u, 0, 0, 0, UIntPtr.Zero);
                if (times > 1) System.Threading.Thread.Sleep(80);
            }
        }

        // Псевдонимы программ: русские названия → имена процессов/протоколов
        private static string AppAlias(string name)
        {
            string n = name.Trim().ToLowerInvariant();
            return n switch
            {
                "дискорд" => "discord",
                "телеграм" or "телеграмм" => "telegram",
                "стим" => "steam",
                "вк" or "вконтакте" => "vk",
                "ютуб" => "youtube",
                "браузер" or "интернет" => "msedge",
                "блокнот" => "notepad",
                "калькулятор" => "calc",
                "проводник" => "explorer",
                "пейнт" or "паинт" => "mspaint",
                _ => n
            };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // «ctrl+s», «win+m», «alt+tab» → формат SendKeys
        private static string KeysToSendKeys(string combo)
        {
            string result = "";
            foreach (string partRaw in combo.Split('+'))
            {
                string part = partRaw.Trim().ToLowerInvariant();
                result += part switch
                {
                    "ctrl" or "контрол" => "^",
                    "alt" or "альт" => "%",
                    "shift" or "шифт" => "+",
                    "win" or "вин" => "#",
                    "enter" or "энтер" => "{ENTER}",
                    "esc" or "эскейп" => "{ESC}",
                    "tab" or "таб" => "{TAB}",
                    "space" or "пробел" => " ",
                    "backspace" => "{BACKSPACE}",
                    "delete" or "делете" => "{DEL}",
                    var s when s.Length == 1 => s.ToUpperInvariant(),
                    var s => "{" + s.ToUpperInvariant() + "}"
                };
            }
            return result;
        }

        // SendKeys воспринимает +^%~(){}[] как служебные — оборачиваем их в фигурные скобки
        private static string EscapeForSendKeys(string text)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if ("+^%~(){}[]".Contains(c)) sb.Append('{').Append(c).Append('}');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        // Заголовок активного окна — что сейчас делает Влад
        public static string ForegroundWindowTitle()
        {
            try
            {
                var sb = new System.Text.StringBuilder(512);
                GetWindowText(GetForegroundWindow(), sb, 512);
                return sb.ToString();
            }
            catch { return ""; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    }
}

    internal static class MemoryStatus
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static (double TotalGb, double FreeGb) Get()
        {
            var ms = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref ms);
            return (ms.ullTotalPhys / 1073741824.0, ms.ullAvailPhys / 1073741824.0);
        }
    }
