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

namespace ии
{
internal class Program
    {
        private static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                Database.Initialize();
                var bot = new ChatBot();
                bot.Greet();

                while (!bot.ShouldExit)
                {
                    Ui.Prompt();
                    string? line = Console.ReadLine();
                    if (line == null) break;
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    Ui.Bot(bot.Respond(line));
                }

                bot.SaveModel();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("=== КРИТИЧЕСКАЯ ОШИБКА ===");
                Console.WriteLine(ex.ToString());
                Console.WriteLine("===========================");
                Console.ForegroundColor = ConsoleColor.Gray;
                
                // Гарантированная запись ошибки в файл, если консоль закроется
                try 
                {
                    File.WriteAllText("error_log.txt", ex.ToString());
                    Console.WriteLine("[!] Подробная ошибка сохранена в файл: error_log.txt");
                } 
                catch {}
            }
            
            Console.WriteLine();
            Console.WriteLine("Нажмите любую клавишу для выхода...");
            Console.ReadKey();
        }
    }
}
