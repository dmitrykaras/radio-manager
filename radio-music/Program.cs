using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    private static readonly Dictionary<string, string> Stations = new()
    {
        { "Radio_Cafe", "https://ru.volna.top/pls/downloadcafe.php?file={0:D2}" },
        { "Horoshee_Radio", "https://ru.volna.top/pls/downloadhr.php?file={0:D2}" },
        { "Russkiy_Rok", "https://ru.volna.top/pls/downloadrusrock.php?file={0:D2}" }
    };

    private const string OutputDir = @"D:\git\radio-music\radio";
    private const string LogPath = @"D:\git\radio-music\log.txt";

    private const int HoursBack = 23; // сколько последних завершённых часов качать
    private const int KeepDays = 3; // хранить папки за N последних дней
    private const int MaxAttempts = 3; // попыток на один файл
    private const int MaxParallel = 4; // одновременных загрузок
    private const long MinValidSize = 1_000_000; // 1 МБ; подберите под реальный размер часа
    private const bool CheckMp3Header = true; // поставьте false, если нормальные файлы считаются битыми

    private static readonly TimeZoneInfo ServerTz = GetServerTz();
    private static readonly SemaphoreSlim Limiter = new(MaxParallel);
    private static readonly HttpClient Client = CreateClient();
    private static readonly object LogLock = new();

    private static int _downloaded, _skipped, _failed;

    static async Task<int> Main()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        Directory.CreateDirectory(OutputDir);

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, ServerTz).DateTime;
        Log($"Запуск. Время сервера: {now:yyyy-MM-dd HH:mm}");

        DeleteTmpFiles();
        await DownloadAllAsync(now);
        CleanOldFolders(now);

        Log($"Итог: скачано {_downloaded}, уже было {_skipped}, ошибок {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task DownloadAllAsync(DateTime now)
    {
        var tasks = new List<Task>();

        foreach (var station in Stations)
        {
            for (int back = 1; back <= HoursBack; back++)
            {
                var slot = now.AddHours(-back);
                string dateStr = slot.ToString("yyyy-MM-dd");
                string hourStr = slot.Hour.ToString("D2");
                string label = $"[{station.Key}] {dateStr} {hourStr}:00";

                string dayDir = Path.Combine(OutputDir, station.Key, dateStr);
                Directory.CreateDirectory(dayDir);
                string filePath = Path.Combine(dayDir, $"{hourStr}-00.mp3");

                if (File.Exists(filePath))
                {
                    if (IsValidMp3(filePath, out string reason))
                    {
                        Interlocked.Increment(ref _skipped);
                        continue;
                    }

                    Log($"  [~] {label} файл повреждён ({reason}), перекачиваю");
                    try { File.Delete(filePath); } catch (Exception ex) { Log($"  [x] {label} не удалось удалить: {ex.Message}"); continue; }
                }

                string url = string.Format(station.Value, slot.Hour);
                tasks.Add(DownloadWithRetriesAsync(url, filePath, label));
            }
        }

        Log($"Задач на скачивание: {tasks.Count}");
        await Task.WhenAll(tasks);
    }

    private static async Task DownloadWithRetriesAsync(string url, string filePath, string label)
    {
        await Limiter.WaitAsync();
        try
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                string tmpPath = filePath + ".tmp";
                try
                {
                    using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"  [!] {label} HTTP {(int)response.StatusCode} (попытка {attempt}/{MaxAttempts})");
                        if ((int)response.StatusCode == 404) break; // повторять смысла нет
                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                        continue;
                    }

                    long? expected = response.Content.Headers.ContentLength;

                    await using (var src = await response.Content.ReadAsStreamAsync())
                    await using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await src.CopyToAsync(dst);
                    }

                    long actual = new FileInfo(tmpPath).Length;

                    if (expected.HasValue && actual != expected.Value)
                    {
                        Log($"  [!] {label} получено {actual} из {expected} байт (попытка {attempt}/{MaxAttempts})");
                        File.Delete(tmpPath);
                        continue;
                    }

                    if (!IsValidMp3(tmpPath, out string reason))
                    {
                        Log($"  [!] {label} невалидный файл: {reason} (попытка {attempt}/{MaxAttempts})");
                        File.Delete(tmpPath);
                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                        continue;
                    }

                    File.Move(tmpPath, filePath, overwrite: true);
                    Interlocked.Increment(ref _downloaded);
                    Log($"  [+] {label} скачан ({actual / 1024 / 1024} МБ)");
                    return;
                }
                catch (Exception ex)
                {
                    Log($"  [x] {label} {ex.GetType().Name}: {ex.Message} (попытка {attempt}/{MaxAttempts})");
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                }
            }

            Interlocked.Increment(ref _failed);
            Log($"  [FAIL] {label} не удалось скачать");
        }
        finally
        {
            Limiter.Release();
        }
    }

    private static bool IsValidMp3(string path, out string reason)
    {
        reason = "";
        try
        {
            var info = new FileInfo(path);
            if (info.Length < MinValidSize)
            {
                reason = $"размер {info.Length} байт";
                return false;
            }

            if (CheckMp3Header)
            {
                var buf = new byte[3];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Read(buf, 0, 3) < 3)
                {
                    reason = "не читается заголовок";
                    return false;
                }

                bool id3 = buf[0] == 'I' && buf[1] == 'D' && buf[2] == '3';
                bool sync = buf[0] == 0xFF && (buf[1] & 0xE0) == 0xE0;
                if (!id3 && !sync)
                {
                    reason = "нет заголовка mp3 (возможно, HTML)";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static void DeleteTmpFiles()
    {
        foreach (var tmp in Directory.GetFiles(OutputDir, "*.tmp", SearchOption.AllDirectories))
        {
            try { File.Delete(tmp); Log($"  [-] Удалён хвост: {tmp}"); } catch { }
        }
    }

    private static void CleanOldFolders(DateTime now)
    {
        Log("[Очистка старых записей...]");
        var cutoff = now.Date.AddDays(-KeepDays);

        foreach (var stationDir in Directory.GetDirectories(OutputDir))
        {
            foreach (var dayDir in Directory.GetDirectories(stationDir))
            {
                string name = Path.GetFileName(dayDir);
                if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                            DateTimeStyles.None, out var date))
                    continue;

                if (date < cutoff)
                {
                    try
                    {
                        Directory.Delete(dayDir, recursive: true);
                        Log($"  [-] Удалено: {Path.GetFileName(stationDir)}\\{name}");
                    }
                    catch (Exception ex)
                    {
                        Log($"  [x] Не удалось удалить {dayDir}: {ex.Message}");
                    }
                }
            }
        }
    }

    private static void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (LogLock)
        {
            Console.WriteLine(line);
            try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        c.DefaultRequestHeaders.Referrer = new Uri("https://ru.volna.top/");
        return c;
    }

    private static TimeZoneInfo GetServerTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time"); } // Windows
        catch { }
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"); } // Linux/macOS
        catch { return TimeZoneInfo.Local; }
    }
}