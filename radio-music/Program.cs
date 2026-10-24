using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

class Program
{
    private static readonly Dictionary<string, string> Stations = new()
    {
        { "Radio_Cafe", "https://ru.volna.top/pls/downloadcafe.php?file={0:D2}" },
        { "Horoshee_Radio", "https://ru.volna.top/pls/downloadhr.php?file={0:D2}" },
        { "Russkiy_Rok", "https://ru.volna.top/pls/downloadrusrock.php?file={0:D2}" }
    };

    private static readonly string OutputDir = @"D:\radio-music\radio";
    private static readonly HttpClient Client = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" } }
    };

    static async Task Main(string[] args)
    {
        Console.WriteLine("[Запуск параллельного скачивания радио...]");
        await DownloadCurrentDayAsync();

        CleanOldFiles();

        Console.WriteLine("\nВсе задачи успешно выполнены!");
    }

    private static async Task DownloadCurrentDayAsync()
    {
        Directory.CreateDirectory(OutputDir);
        string todayStr = DateTime.Now.ToString("yyyy-MM-dd");

        var downloadTasks = new List<Task>();

        foreach (var station in Stations)
        {
            string dayDir = Path.Combine(OutputDir, station.Key, todayStr);
            Directory.CreateDirectory(dayDir);

            for (int hour = 0; hour < 24; hour++)
            {
                int h = hour;
                string hourStr = h.ToString("D2");
                string filePath = Path.Combine(dayDir, $"{hourStr}-00.mp3");

                // Если файл уже есть — пропускаем
                if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                    continue;

                string url = string.Format(station.Value, h);
                downloadTasks.Add(DownloadHourAsync(url, filePath, station.Key, hourStr));
            }
        }

        // Ждем завершения всех загрузок одновременно
        await Task.WhenAll(downloadTasks);
    }

    private static async Task DownloadHourAsync(string url, string filePath, string stationName, string hourStr)
    {
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode)
            {
                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
                await contentStream.CopyToAsync(fileStream);
                Console.WriteLine($"  [+] [{stationName}] Скачан час {hourStr}:00");
            }
        }
        catch
        {
            // Пропускаем недоступные часы
        }
    }

    private static void CleanOldFiles()
    {
        Console.WriteLine("\n[Очистка старых архивов...]");
        if (!Directory.Exists(OutputDir)) return;

        var cutoff = DateTime.Now.AddDays(-3);

        foreach (var file in Directory.GetFiles(OutputDir, "*.*", SearchOption.AllDirectories))
        {
            try
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoff)
                {
                    fileInfo.Delete();
                    Console.WriteLine($"  [-] Удален устаревший файл: {fileInfo.Name}");
                }
            }
            catch
            {
                // Игнорируем ошибки доступа к файлам
            }
        }
    }
}