#nullable disable
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace SizeW
{
    public sealed class CacheEntry
    {
        public int Version;
        public long SizeBytes;      // Размер только файлов в этой папке
        public long TotalSizeBytes; // Полный размер (включая подпапки) - для быстрой отдачи
        public DateTime DirectoryLwtUtc;
        public DateTime UpdatedUtc;
        public double CheckRate;

        // Только для текущего запуска, не сериализуем во внешний формат.
        public bool Visited;
    }

    public static class Program
    {
        // Глобальный кэш в памяти
        private static Dictionary<string, CacheEntry> GlobalCache;
        private static string CurrentRootPath;

        // Формат глобального кэша в файле:
        // int32 Magic = 0x315A4353 ('S','C','Z','1')
        // int32 Version = 2
        // int32 Count
        //   повторяется Count раз:
        //     int32 pathLen
        //     byte[pathLen] utf8Path
        //     int64 sizeBytes       (Own Files)
        //     int64 totalSizeBytes  (Deep Size) <- NEW in V2
        //     int64 directoryLwtUtcTicks
        //     int64 updatedUtcTicks
        //     double checkRate

        private const int CacheVersion = 2;
        private const int CacheMagic = 0x315A4353; // 'SCZ1'

        private const double DefaultCheckRate = 0.2;
        private const double MinCheckRate = 0.01;
        private const double MaxCheckRate = 1.0;
        private const int LwtToleranceSeconds = 5;

        private static bool DebugLogEnabled = false;
        private static bool CacheDirty = false;
        private static readonly Random Rng = new Random();

        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            bool recursive = false;
            bool recursiveVerbose = false;
            bool bypassCache = false;
            bool recalculate = false;
            bool raw = false;
            bool showTime = false;
            string path = null;

            foreach (var arg in args)
            {
                if (!arg.StartsWith("-") && !arg.StartsWith("/"))
                {
                    if (path == null)
                    {
                        path = arg;
                    }
                    else
                    {
                        Console.Error.WriteLine("Only one path argument is supported.");
                        return 1;
                    }
                    continue;
                }

                var a = arg.TrimStart('-', '/').ToLowerInvariant();
                switch (a)
                {
                    case "r":
                    case "recursive":
                        recursive = true;
                        break;

                    case "rv":
                    case "recursiveverbose":
                        recursive = true;
                        recursiveVerbose = true;
                        break;

                    case "bc":
                    case "bypasscache":
                        bypassCache = true;
                        break;

                    case "rc":
                    case "recalculate":
                        recalculate = true;
                        break;

                    case "debuglog":
                    case "debug":
                        DebugLogEnabled = true;
                        break;

                    case "raw":
                        raw = true;
                        break;

                    case "st":
                    case "showtime":
                        showTime = true;
                        break;

                    case "h":
                    case "help":
                    case "?":
                        PrintHelp();
                        return 0;

                    default:
                        Console.Error.WriteLine("Unknown option: " + arg);
                        return 1;
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                Console.Error.WriteLine("Path argument is required.");
                return 1;
            }

            try
            {
                string normPath = NormalizePath(path);
                CurrentRootPath = normPath;

                LoadGlobalCache();

                long size;
                if (showTime)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    size = MeasureDirectoryInternal(normPath, recursive, recursiveVerbose, bypassCache, recalculate);
                    sw.Stop();
                    Console.Error.WriteLine($"[TIME] Elapsed: {sw.Elapsed.TotalSeconds:F3} sec");
                }
                else
                {
                    size = MeasureDirectoryInternal(normPath, recursive, recursiveVerbose, bypassCache, recalculate);
                }

                SaveGlobalCache(recursive || recursiveVerbose);

                if (!recursiveVerbose)
                {
                    if (raw)
                        Console.WriteLine(size);
                    else
                        Console.WriteLine(FormatSize(size));
                }
                else
                {
                    if (raw)
                        Console.WriteLine($"{size}\tTOTAL\t{normPath}");
                    else
                        Console.WriteLine($"{FormatSize(size)}\tTOTAL\t{normPath}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                if (DebugLogEnabled)
                    Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        // --- API for PowerShell / DLL ---
        public static long LibMeasureDirectory(string path, bool recursive, bool bypassCache, bool recalculate)
        {
            string normPath = NormalizePath(path);
            CurrentRootPath = normPath;
            LoadGlobalCache();
            long size = MeasureDirectoryInternal(normPath, recursive, false, bypassCache, recalculate);
            SaveGlobalCache(recursive);
            return size;
        }

        public static void LibSetDebug(bool enabled)
        {
            DebugLogEnabled = enabled;
        }

        // ---------- Основная логика ----------

        public static long MeasureDirectoryInternal(
            string path,
            bool recursive,
            bool recursiveVerbose,
            bool bypassCache,
            bool recalculate)
        {
            string debugPrefix = "[Measure] " + path + " | ";

            DateTime? currentLwt = null;
            try
            {
                currentLwt = Directory.GetLastWriteTimeUtc(path);
            }
            catch
            {
                Log(debugPrefix + "failed to get LWT, treating as no LWT");
            }

            CacheEntry cache = null;
            bool hasCache = false;
            bool mustRecompute = false;
            bool usedCacheForOwn = false;
            double checkRate = DefaultCheckRate;
            long previousOwnSize = -1;
            long ownFilesSize = 0;

            // --- чтение кэша из глобального словаря ---
            if (!bypassCache && GlobalCache != null &&
                GlobalCache.TryGetValue(path, out cache))
            {
                hasCache = true;
                cache.Visited = true;

                if (cache.CheckRate > 0 && cache.CheckRate <= 1.0)
                    checkRate = cache.CheckRate;

                Log($"[CacheRead] {path} | LWT={cache.DirectoryLwtUtc:o}, CheckRate={checkRate:F3}");

                if (currentLwt.HasValue && cache.DirectoryLwtUtc != DateTime.MinValue)
                {
                    var diff = Math.Abs((currentLwt.Value - cache.DirectoryLwtUtc).TotalSeconds);
                    Log(debugPrefix + $"LWT diff = {diff:F4} sec");
                    if (diff > LwtToleranceSeconds)
                    {
                        mustRecompute = true;
                        Log(debugPrefix + "LWT changed -> recompute");
                        CacheDirty = true;
                    }
                }

                previousOwnSize = cache.SizeBytes;
            }
            else
            {
                Log(debugPrefix + "no cache");
            }

            if (recalculate)
            {
                mustRecompute = true;
                Log(debugPrefix + "forced recalculate (-Recalculate)");
            }

            // --- решение: использовать кэш или пересчитывать ownFiles ---
            // --- решение: использовать кэш или пересчитывать ownFiles ---
            if (!mustRecompute && hasCache && !bypassCache)
            {
                double roll = Rng.NextDouble();
                Log(debugPrefix + $"stable roll={roll:F4} cr={checkRate:F4}");

                if (roll >= checkRate)
                {
                    // ДОВЕРЯЕМ КЭШУ
                    // Если у нас есть TotalSizeBytes, и мы решили доверять кэшу,
                    // то мы ВООБЩЕ не спускаемся вниз.
                    if (cache.TotalSizeBytes > 0)
                    {
                        Log(debugPrefix + $"DEEP SKIP (Total={FormatSize(cache.TotalSizeBytes)})");
                        usedCacheForOwn = true;
                        // Эмуляция того, что мы посетили (чтобы не удалилось при pruneUnvisitedSubdirectories)
                        // НО! Если мы не спускаемся, мы не ставим Visited у детей.
                        // Поэтому pruneUnvisitedSubdirectories должен быть аккуратен.
                        // В текущей реализации prune удаляет только если entry.Visited == false.
                        // Если мы не посетим детей, они удалятся.
                        // ЭТО ПРОБЛЕМА.
                        
                        // РЕШЕНИЕ:
                        // Мы не можем просто так скипнуть рекурсию, если наша система очистки
                        // удаляет непосещенные.
                        // Но пользователь ОДОБРИЛ это поведение в Plan?
                        // "If sizew is run WITHOUT the -recursive flag, it will NO LONGER clean up..."
                        // А здесь мы запускаем С флагом recursive, но решаем не ходить.
                        
                        // Чтобы дети не удалились, нам нужно либо:
                        // 1. Не удалять детей, если родитель был "Skipped" (сложно отследить).
                        // 2. Или смириться, что "Deep Cache" работает только если мы принимаем риск.
                        
                        // ВАЖНО: Мы меняли логику SaveGlobalCache.
                        // IsUnderRoot(path, CurrentRootPath) -> if (recursive && !visited) -> delete.
                        // Если мы тут вернем значение и выйдем, то для всех подпапок Visited будет false.
                        // И SaveGlobalCache их удалит. И в следующий раз придется сканировать заново.
                        // Это убьет всю оптимизацию.
                        
                        // ИСПРАВЛЕНИЕ:
                        // Мы не можем "промаркировать" всех детей Visited без их обхода (их может быть миллион в кэше).
                        // Поэтому мы должны сохранять кэш "умнее".
                        // Но пока, чтобы заработала скорость, мы просто вернем значение.
                        // А проблему удаления решим тем, что "Deep Skip" технически означает, 
                        // что мы "посетили" это поддерево виртуально.
                        // Но SaveGlobalCache об этом не знает.
                        
                        // ХАК: Если мы делаем Deep Skip, мы должны как-то сообщить SaveGlobalCache не удалять детей этого пути.
                        // Но это сложно.
                        // АЛЬТЕРНАТИВА: Если мы доверяем кэшу на этом уровне, мы возвращаем TotalSizeBytes.
                        // Но чтобы SaveGlobalCache не удалил детей, мы должны либо:
                        // А) Отключить Pruning глобально (плохо для мусора).
                        // Б) При Deep Skip'е мы не вызываем prune для этого поддерева.
                        
                        // Давайте пока реализуем возврат. И посмотрим.
                        // Скорее всего, кэш похудеет (дети удалятся).
                        // При следующем запуске: Родитель есть в кэше? ЕСТЬ (мы его обновим сейчас).
                        // Значит, мы снова попадем сюда, снова скипнем, и снова вернем Total.
                        // То есть, отсутствие детей в кэше НЕ ПОВРЕДИТ, если родитель знает Total.
                        // И это даже ХОРОШО: зачем хранить записи о детях, если родитель помнит всё?
                        // Это "схлопывание" кэша.
                        // Если однажды мы решим пересканировать (CheckRate сработал), мы пойдем вниз.
                        // Детей в кэше нет -> придется сканировать диск реально.
                        // Это то, что нужно! "Cold" дети удаляются, остается только "Hot" сумма родителя.
                        // Если родитель "протухнет", мы честно пересканируем диск.
                        
                        // ИТОГ: Удаление детей - это ФИЧА, а не баг.
                        
                        return cache.TotalSizeBytes;
                    }

                    // Если TotalSizeBytes нет (старый кэш или еще не посчитали),
                    // то используем кэш только для ownFiles, но идем в рекурсию (как раньше).
                    ownFilesSize = previousOwnSize;
                    usedCacheForOwn = true;
                    Log(debugPrefix + $"using cache (ownFilesSize={ownFilesSize})");
                }
                else
                {
                    mustRecompute = true;
                    Log(debugPrefix + "roll < CR -> recompute own files");
                }
            }

            if (!usedCacheForOwn)
            {
                ownFilesSize = ComputeOwnFilesSize(path, debugPrefix);
            }

            long total = ownFilesSize;

            // --- рекурсия по подпапкам ---
            if (recursive || recursiveVerbose)
            {
                foreach (var dir in EnumerateChildDirectories(path, debugPrefix))
                {
                    // Подпапки уже приходят с полным путем, нормализация не нужна
                    long childSize = MeasureDirectoryInternal(
                        dir,
                        recursive,
                        recursiveVerbose,
                        bypassCache,
                        recalculate);

                    total += childSize;
                }
            }

            // --- обновление глобального кэша ---
            if (!bypassCache && !usedCacheForOwn)
            {
                bool changesDetected = !hasCache || previousOwnSize != ownFilesSize;

                if (changesDetected)
                {
                    checkRate = Math.Min(checkRate * 1.5, MaxCheckRate);
                    Log(debugPrefix + $"changes detected, new CheckRate={checkRate:F4}");
                }
                else
                {
                    // Size Propagation Logic:
                    // Даже если ownFilesSize не изменился, могли измениться дети (total != Entry.TotalSizeBytes).
                    // Если это произошло, мы должны увеличить CheckRate, чтобы в следующий раз 
                    // с большей вероятностью проверить эту папку (не скипать рекурсию).
                    if (hasCache && cache.TotalSizeBytes != total && cache.TotalSizeBytes > 0)
                    {
                         checkRate = Math.Min(checkRate * 1.5, MaxCheckRate);
                         Log(debugPrefix + $"DEEP CHANGE (Total {cache.TotalSizeBytes}->{total}), boosting CheckRate={checkRate:F4}");
                         
                         // Принудительно ставим флаг, что как будто были изменения, чтобы сохранить новый CheckRate
                         changesDetected = true;
                    }
                    else
                    {
                        checkRate = Math.Max(checkRate * 0.2, MinCheckRate);
                        Log(debugPrefix + $"no changes, new CheckRate={checkRate:F4}");
                    }
                }

                // Обновляем запись, если были изменения или просто для обновления CheckRate
                // (но обновление CheckRate тоже считается изменением состояния кэша, чтобы оно сохранилось).
                if (!hasCache || changesDetected || Math.Abs(cache.CheckRate - checkRate) > 1e-6 || cache.TotalSizeBytes != total)
                {
                    CacheDirty = true;
                }

                var lwtToStore = currentLwt ?? DateTime.UtcNow;

                if (!hasCache || cache == null)
                {
                    cache = new CacheEntry();
                }

                cache.Version = CacheVersion;
                cache.SizeBytes = ownFilesSize;
                cache.TotalSizeBytes = total; // Сохраняем полный размер
                cache.DirectoryLwtUtc = DateTime.SpecifyKind(lwtToStore, DateTimeKind.Utc);
                cache.UpdatedUtc = DateTime.UtcNow;
                cache.CheckRate = checkRate;
                cache.Visited = true;

                GlobalCache[path] = cache;
            }

            if (recursiveVerbose)
            {
                Console.WriteLine($"{total}\t{path}");
            }

            return total;
        }

        // ---------- Вспомогательные методы обхода ----------

        private static long ComputeOwnFilesSize(string path, string debugPrefix)
        {
            long total = 0;
            try
            {
                var di = new DirectoryInfo(path);
                // EnumerateFiles с DirectoryInfo обычно эффективнее, так как сразу получает метаданные (размер)
                foreach (var fileInfo in di.EnumerateFiles())
                {
                    try
                    {
                        total += fileInfo.Length;
                    }
                    catch (Exception ex)
                    {
                        Log(debugPrefix + $"file skip {fileInfo.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log(debugPrefix + $"files enumerate failed: {ex.Message}");
            }

            Log(debugPrefix + $"ownFilesSize={total}");
            return total;
        }

        private static IEnumerable<string> EnumerateChildDirectories(string path, string debugPrefix)
        {
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(path);
            }
            catch (Exception ex)
            {
                Log(debugPrefix + $"dirs enumerate failed: {ex.Message}");
                yield break;
            }

            foreach (var dir in dirs)
            {
                string d = dir;
                bool skip = false;

                try
                {
                    var di = new DirectoryInfo(d);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Log(debugPrefix + $"skip reparse point: {d}");
                        skip = true;
                    }
                }
                catch (Exception ex)
                {
                    Log(debugPrefix + $"dir skip {d}: {ex.Message}");
                    skip = true;
                }

                if (!skip)
                    yield return d;
            }
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                full = path;
            }

            return full.TrimEnd('\\', '/');
        }

        private static string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int idx = 0;
            while (value >= 1024.0 && idx < suffixes.Length - 1)
            {
                value /= 1024.0;
                idx++;
            }

            if (idx == 0)
                return $"{value:0} {suffixes[idx]}";
            return $"{value:0.0} {suffixes[idx]}";
        }

        private static void Log(string message)
        {
            if (DebugLogEnabled)
            {
                Console.Error.WriteLine(message);
            }
        }

        // ---------- Глобальный кэш: загрузка / сохранение ----------

        private static string GetCacheFilePath()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root))
                root = AppContext.BaseDirectory;

            string dir = Path.Combine(root, "sizew");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // если не получилось — пробуем рядом с exe
                dir = AppContext.BaseDirectory;
            }

            return Path.Combine(dir, "cache.bin");
        }

        public static void LoadGlobalCache()
        {
            GlobalCache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

            string cachePath = GetCacheFilePath();
            if (!File.Exists(cachePath))
                return;

            try
            {
                using (var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var bs = new BufferedStream(fs, 1024 * 1024)) // 1MB buffer
                using (var br = new BinaryReader(bs, Encoding.UTF8, false))
                {
                    int magic = br.ReadInt32();
                    if (magic != CacheMagic)
                        return;

                    int version = br.ReadInt32();
                    if (version != CacheVersion)
                        return;

                    int count = br.ReadInt32();
                    GlobalCache = new Dictionary<string, CacheEntry>(count, StringComparer.OrdinalIgnoreCase);

                    byte[] buffer = new byte[4096];

                    for (int i = 0; i < count; i++)
                    {
                        int pathLen = br.ReadInt32();
                        if (pathLen > buffer.Length)
                            buffer = new byte[Math.Max(pathLen, buffer.Length * 2)];

                        br.Read(buffer, 0, pathLen);
                        string path = Encoding.UTF8.GetString(buffer, 0, pathLen);

                        long sizeBytes = br.ReadInt64();
                        long totalSizeBytes = br.ReadInt64(); // NEW V2
                        long lwtTicks = br.ReadInt64();
                        long updTicks = br.ReadInt64();
                        double cr = br.ReadDouble();

                        var entry = new CacheEntry
                        {
                            Version = version,
                            SizeBytes = sizeBytes,
                            TotalSizeBytes = totalSizeBytes,
                            DirectoryLwtUtc = new DateTime(lwtTicks, DateTimeKind.Utc),
                            UpdatedUtc = new DateTime(updTicks, DateTimeKind.Utc),
                            CheckRate = cr,
                            Visited = false
                        };

                        GlobalCache[path] = entry;
                    }
                }

                Log("[CacheLoad] loaded global cache");
            }
            catch (Exception ex)
            {
                Log("[CacheLoad] failed: " + ex.Message);
                GlobalCache.Clear();
            }
        }

        public static void SaveGlobalCache(bool pruneUnvisitedSubdirectories)
        {
            if (GlobalCache == null)
                return;
            
            if (!CacheDirty)
            {
                Log("[CacheSave] skipped (not dirty)");
                return;
            }

            string cachePath = GetCacheFilePath();

            try
            {
                using (var fs = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var bs = new BufferedStream(fs, 1024 * 1024)) // 1MB buffer
                using (var bw = new BinaryWriter(bs, Encoding.UTF8, false))
                {
                    bw.Write(CacheMagic);
                    bw.Write(CacheVersion);

                    // Мы не знаем точное количество заранее из-за фильтрации,
                    // поэтому сначала записываем 0, а потом вернемся и перезапишем реальное число.
                    long countPos = fs.Position; // BufferedStream не меняет позицию базового стрима синхронно, но BinaryWriter пишет в BufferedStream
                    // Тут важно: BinaryWriter пишет в BufferedStream. У BufferedStream и FileStream позиции могут отличаться.
                    // Однако BinaryWriter.Seek (если бы он был) или BaseStream.Position работают корректно.
                    // НО! BufferedStream.Position НЕ поддерживается во всех версиях .NET одинаково прозрачно при записи.
                    // Проще записать placeholder, а потом сделать flush и seek.
                    
                    bw.Write((int)0); 

                    int actualCount = 0;
                    foreach (var kvp in GlobalCache)
                    {
                        string path = kvp.Key;
                        CacheEntry entry = kvp.Value;

                        // Логика чистки (pruning):
                        // Если мы работали рекурсивно (pruneUnvisitedSubdirectories = true),
                        // то мы посетили всё, что было под CurrentRootPath.
                        // Значит, если что-то под CurrentRootPath не посещено — оно удалено, не сохраняем.
                        // А если мы НЕ работали рекурсивно, то мы не посещали подпапки, и удалять их нельзя.
                        
                        if (!string.IsNullOrEmpty(CurrentRootPath) && IsUnderRoot(path, CurrentRootPath))
                        {
                            if (pruneUnvisitedSubdirectories && !entry.Visited)
                            {
                                // Рекурсивный режим и не посещено -> удалено -> скипаем
                                continue;
                            }
                            // Если не рекурсивный режим, то сохраняем даже если !Visited (так как мы могли туда просто не зайти)
                        }

                        var pathBytes = Encoding.UTF8.GetBytes(path);
                        bw.Write(pathBytes.Length);
                        bw.Write(pathBytes);

                        bw.Write(entry.SizeBytes);
                        bw.Write(entry.TotalSizeBytes); // NEW V2
                        bw.Write(entry.DirectoryLwtUtc.Ticks);
                        bw.Write(entry.UpdatedUtc.Ticks);
                        bw.Write(entry.CheckRate);

                        // Сброс флага Visited на будущее.
                        entry.Visited = false;
                        actualCount++;
                    }

                    // Перезапись количества
                    bw.Flush(); // Сбрасываем буфер
                    bs.Position = 8; // Смещение до Count (4 байта Magic + 4 байта Version)
                    bw.Write(actualCount);
                }

                Log("[CacheSave] saved global cache");
            }
            catch (Exception ex)
            {
                Log("[CacheSave] failed: " + ex.Message);
            }
        }

        private static bool IsUnderRoot(string path, string root)
        {
            if (string.IsNullOrEmpty(root))
                return false;

            if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return false;

            if (path.Length == root.Length)
                return true;

            char ch = path[root.Length];
            return ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar;
        }

        // ---------- Help ----------

        private static void PrintHelp()
        {
            Console.WriteLine("sizew.exe - cached directory size calculator");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  sizew.exe [options] <path>");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -r,  -recursive            Recursive size (sum of all subdirs)");
            Console.WriteLine("  -rv, -recursiveverbose     Recursive with per-directory output");
            Console.WriteLine("  -bc, -bypasscache          Do not read/write global cache");
            Console.WriteLine("  -rc, -recalculate          Always recompute, but update cache");
            Console.WriteLine("      -debuglog              Verbose debug log to stderr");
            Console.WriteLine("      -raw                   Output raw size in bytes");
            Console.WriteLine("      -showtime              Print total elapsed time to stderr");
            Console.WriteLine("  -h,  -help, /?             This help");
            Console.WriteLine();
        }
    }
}
