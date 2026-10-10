// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var appConfig = new AppConfig();
bool errorOccuredDuringStartup = false;
string welcomeMessage = "Welcome to Anna's File Transfer System, C# Edition! This is my first C# program so sorry if its not great";
Console.WriteLine(welcomeMessage);

var videoFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "avi", "flv", "m4a", "mka", "mkv", "mov", "mp3", "mp4", "ogg", "opus", "webm"
};
var audioFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "aac", "alac", "flac", "m4a", "mp3", "ogg", "opus", "wav"
};

async Task checkSelfForUpdates() 
{
    using var client = new HttpClient();
    string json = await client.GetStringAsync("https://annaspannertechsolutions.uk/annas-youtube-downloader/latest");
    UpdateInfo updateInfo = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions {PropertyNameCaseInsensitive = true})!;
    
    if (Version.Parse(updateInfo.LatestVersion) <= Version.Parse(appConfig.Version)) {return;}

    Console.Write("An update was found to this app! Would you like to install it? (Y/n) ->");
    if (Console.ReadLine()!.ToLower() == "n") {return;}
    Console.WriteLine("Downloading patch...");
    
    string url = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? updateInfo.WindowsUpdaterUrl :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? updateInfo.MacUpdaterUrl : updateInfo.LinuxUpdaterUrl;
    
    string updaterName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "updater.exe" : "updater";

    string updaterPath = AppConfig.ResolveAppPath(updaterName);
    await File.WriteAllBytesAsync(updaterPath, await client.GetByteArrayAsync(url));
    Console.WriteLine("File downloaded! This app will now close to self-update");
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        File.SetUnixFileMode(updaterPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    Process.Start(new ProcessStartInfo
    {
        FileName = updaterPath,
        Arguments = appConfig.Version,
        WorkingDirectory = AppConfig.ApplicationDirectory,
        UseShellExecute = true
    });
    Environment.Exit(0);
}

async Task updateYtDlp()
{
    if (!File.Exists(appConfig.YtDlp)) {throw new RequiredDependancyMissing($"Yt-dlp is missing, couldn't find it at {Path.GetFullPath(appConfig.YtDlp)}");}
    ProcessStartInfo psi = new ProcessStartInfo
    {
        FileName = appConfig.YtDlp,
        Arguments = "-U",
        WorkingDirectory = AppConfig.ApplicationDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    Process? updateInfo = Process.Start(psi);
    if (updateInfo == null)
    {
        Console.WriteLine("Failed to start the yt-dlp process.");
        errorOccuredDuringStartup = true;
        return;
    }
    string output = await updateInfo.StandardOutput.ReadToEndAsync();
    string errors = await updateInfo.StandardError.ReadToEndAsync();
    await updateInfo.WaitForExitAsync();

    if (updateInfo.ExitCode != 0)
    {
        Console.WriteLine("Update failed, see below for details");
        Console.WriteLine($"{output}\n\nErrors: \n{errors}");
        errorOccuredDuringStartup = true;
    }
}

async Task updateFfmpeg()
{
    string? ffmpegFolder = Directory.GetParent(appConfig.Ffmpeg)?.ToString();
    ArgumentNullException.ThrowIfNullOrEmpty(ffmpegFolder);
    string ffmpegZip = "ffmpeg.zip";
    string baseUrl = "https://api.github.com/repos/yt-dlp/FFmpeg-Builds/releases/latest";
    using HttpClient client = new HttpClient();
    client.DefaultRequestHeaders.Add("User-Agent", "AnnaSpanner C# Applicaiton");
    
    string json = await client.GetStringAsync(baseUrl);
    string latestReleaseDate = "";
    
    using (JsonDocument document = JsonDocument.Parse(json))
    {
        latestReleaseDate = document.RootElement.GetProperty("published_at").GetString() ?? "";
    }
    if (appConfig.FfmpegLatestVersion == latestReleaseDate) {return;}

    string url;
    bool isZip = true;
    baseUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-";

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {url = baseUrl + "win64-gpl.zip";}
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {url = baseUrl + "macos64-gpl.zip";}
    else
    {
        url = baseUrl + "linux64-gpl.tar.xz";
        ffmpegZip = "ffmpeg.tar.xz";
        isZip = false;
    }
    byte[] fileBytes = await client.GetByteArrayAsync(url);
    string tempPath = AppConfig.ResolveAppPath("temp");
    string downloadPath = Path.Combine(tempPath, ffmpegZip);
    Directory.CreateDirectory(tempPath);
    await File.WriteAllBytesAsync(downloadPath, fileBytes);

    if (Directory.Exists(ffmpegFolder)) {Directory.Delete(ffmpegFolder, true);}
    Directory.CreateDirectory(ffmpegFolder);

    if (isZip)
    {
        ZipFile.ExtractToDirectory(downloadPath, ffmpegFolder);
    }
    else
    {
        var psi = new ProcessStartInfo("tar", $"-xf \"{downloadPath}\" -C \"{ffmpegFolder}\"")
        {
            CreateNoWindow = true,
            WorkingDirectory = AppConfig.ApplicationDirectory
        };
        using Process? process = Process.Start(psi);
        ArgumentNullException.ThrowIfNull(process);
        process.WaitForExit();
    }
    File.Delete(downloadPath);

    string ffmpegExtractTemp = Directory.GetDirectories(ffmpegFolder)[0];
    string bin = Path.Combine(ffmpegExtractTemp, "bin");

    foreach (string file in Directory.GetFiles(bin))
    {
        File.Move(file, Path.Combine(ffmpegFolder, Path.GetFileName(file)));
    }
    Directory.Delete(ffmpegExtractTemp, true);
    Directory.Delete(tempPath);

    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) 
    {foreach (string file in Directory.GetFiles(ffmpegFolder))
        {
         File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }}
    
    appConfig.FfmpegLatestVersion = latestReleaseDate;
    appConfig.Save();
}

async Task updateDeno()
{

    string? denoFolder = Directory.GetParent(appConfig.Deno)?.ToString();
    ArgumentNullException.ThrowIfNullOrEmpty(denoFolder);
    string tempPath = AppConfig.ResolveAppPath("temp_deno");

    try
    {
        using HttpClient client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "AnnaSpanner C# Applicaiton");

        // Query the latest version before downloading anything
        string json = await client.GetStringAsync("https://api.github.com/repos/denoland/deno/releases/latest");
        string latestVersion;
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            latestVersion = document.RootElement.GetProperty("tag_name").GetString() ?? "";
        }

        if (appConfig.DenoLatestVersion == latestVersion && File.Exists(appConfig.Deno))
        {
            return;
        }

        string assetName;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {assetName = "deno-x86_64-pc-windows-msvc.zip";}
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            assetName = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "deno-aarch64-apple-darwin.zip" : "deno-x86_64-apple-darwin.zip";
        }
        else
        {
            assetName = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "deno-aarch64-unknown-linux-gnu.zip" : "deno-x86_64-unknown-linux-gnu.zip";
        }

        string url = $"https://github.com/denoland/deno/releases/download/{latestVersion}/{assetName}";
        string downloadPath = Path.Combine(tempPath, assetName);

        byte[] fileBytes = await client.GetByteArrayAsync(url);
        Directory.CreateDirectory(tempPath);
        await File.WriteAllBytesAsync(downloadPath, fileBytes);

        if (Directory.Exists(denoFolder)) {Directory.Delete(denoFolder, true);}
        Directory.CreateDirectory(denoFolder);
        ZipFile.ExtractToDirectory(downloadPath, denoFolder);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(appConfig.Deno, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        appConfig.DenoLatestVersion = latestVersion;
        appConfig.Save();
    }
    catch (Exception e)
    {
        // Deno is optional, so failures here shouldn't block the rest of the app
        Console.WriteLine($"\nCouldn't update Deno, continuing without it: {e.Message}");
        errorOccuredDuringStartup = true;
    }
    finally
    {
        if (Directory.Exists(tempPath)) {Directory.Delete(tempPath, true);}
    }
}

async Task<(bool, string)> isLinkPlaylist(string url)
{
    ProcessStartInfo psi = new ProcessStartInfo
    {
        FileName = appConfig.YtDlp,
        Arguments = $"-J --flat-playlist \"{url}\" --js-runtimes \"deno:{appConfig.Deno}\"",
        WorkingDirectory = AppConfig.ApplicationDirectory,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    string jsonOutput;
    using (Process playlistCheckerProcess = Process.Start(psi)!)
    {
        jsonOutput = await playlistCheckerProcess.StandardOutput.ReadToEndAsync();
        await playlistCheckerProcess.WaitForExitAsync();
    }
    using (JsonDocument document = JsonDocument.Parse(jsonOutput)) {
        JsonElement root = document.RootElement;

        bool isPlaylist = false;
        if (root.TryGetProperty("_type", out JsonElement typeProperty)) {isPlaylist = typeProperty.GetString() == "playlist";}

        string title = root.TryGetProperty("title", out JsonElement titleProperty) ? titleProperty.GetString() ?? "" : "";
        return (isPlaylist, title);
    }
}

async Task<(string videoID, string audioID)> promptStreams(string url)
{
    Console.Write("Loading codecs...\r");
    var psi = new ProcessStartInfo
    {
        FileName = appConfig.YtDlp,
        Arguments = $"-J \"{url}\" --js-runtimes \"deno:{appConfig.Deno}\"",
        WorkingDirectory = AppConfig.ApplicationDirectory,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    string jsonOutput;
    using (Process process = Process.Start(psi)!)
    {
        jsonOutput = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
    }

    var videoStreams = new List<StreamInfo>();
    var audioStreams = new List<StreamInfo>();

    using (JsonDocument document = JsonDocument.Parse(jsonOutput))
    {
        if (document.RootElement.TryGetProperty("formats", out JsonElement formats))
        {
            foreach (JsonElement format in formats.EnumerateArray())
            {
                string videoCodec = format.TryGetProperty("vcodec", out JsonElement v) ? v.GetString() ?? "none" : "none";
                string audioCodec = format.TryGetProperty("acodec", out JsonElement a) ? a.GetString() ?? "none" : "none";
                string formatID = format.GetProperty("format_id").GetString() ?? "";

                double bitRate = format.TryGetProperty("tbr", out JsonElement b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : 0;
                double sizeBytes = format.TryGetProperty("filesize", out JsonElement fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetDouble() :
                                  (format.TryGetProperty("filesize_approx", out JsonElement fsa) && fsa.ValueKind == JsonValueKind.Number ? fsa.GetDouble() : 0);

                double sizeMB = sizeBytes / 1048576.0;

                if (videoCodec != "none" && audioCodec == "none")
                {
                    string resolution = format.TryGetProperty("resolution", out JsonElement res) ? res.GetString() ?? "unknown" : "unknown";
                    videoStreams.Add(new StreamInfo
                    {
                        FormatId = formatID,
                        Resolution = resolution,
                        Bitrate = bitRate,
                        SizeMB = sizeMB
                    });
                }
                else if (videoCodec == "none" && audioCodec != "none")
                {
                    audioStreams.Add(new StreamInfo
                    {
                        FormatId = formatID,
                        Bitrate = bitRate,
                        SizeMB = sizeMB
                    });
                }
            }
        }
    }

    Console.WriteLine("---< Video Codecs >---");
    foreach (var v in videoStreams)
    {
        Console.WriteLine($"ID: {v.FormatId,-5} | Res: {v.Resolution,-10} | Bitrate: {v.Bitrate,6:F1} kbps | Est. Size: {v.SizeMB,6:F1} MB");
    }

    Console.Write("\nPlease pick a video stream by entering the ID, or 0/\"\" for none and go audio only -> ");
    string? videoInput = Console.ReadLine()?.Trim();
    string videoID = (videoInput == "0" || string.IsNullOrEmpty(videoInput)) ? "-" : videoInput;

    Console.WriteLine("---< Audio Codecs >---");
    foreach (var a in audioStreams)
    {
        Console.WriteLine($"ID: {a.FormatId,-5} | Bitrate: {a.Bitrate,6:F1} kbps | Est. Size: {a.SizeMB,6:F1} MB");
    }

    string extraFluff = videoID == "" ? "to use the best quality available" : "to have a silent video";
    Console.Write($"\nPlease pick an audio stream by entering the ID, or 0/\"\" for none and {extraFluff} -> ");
    string? audioInput = Console.ReadLine()?.Trim();
    string audioID = (audioInput == "0" || string.IsNullOrEmpty(audioInput)) ? "-" : audioInput;

    return (videoID, audioID);
}

async Task run()
{
    appConfig = AppConfig.Load();
    if (!errorOccuredDuringStartup && appConfig.ErrorOccurredDuringStartup) {errorOccuredDuringStartup = true;}
    if (!Path.Exists(appConfig.YtDlp))
    {
        Console.WriteLine("You don't have yt-dlp installed. This is required, any version that has the self-updating ability. Please ensure this is placed in the same directory as this app BEFORE proceeding. Press Enter to continue");
        Console.ReadLine();
    }
    if (appConfig.UpdateReqsOnStartup)
    {
        try
        {
            Console.Write("\rChecking for updates... Checking self");
            //await checkSelfForUpdates(); Not ready for beta deployment
            Console.Write("\rChecking for updates... This downloader is up to date! Checking Yt-dlp");
            await updateYtDlp();
            Console.Write("\rChecking for updates... This downloader is up to date! Yt-dlp up to date! Checking Ffmpeg");
            await updateFfmpeg();
            Console.Write("\rChecking for updates... This downloader is up to date! Yt-dlp up to date! Ffmpeg up to date! Checking Deno");
            await updateDeno();
            Console.Write("\rChecking for updates... This downloader is up to date! Yt-dlp up to date! Ffmpeg up to date! Deno up to date!");
        }
        catch (Exception) {errorOccuredDuringStartup = true;}
    }
    else if (!Path.Exists(appConfig.Ffmpeg) || !Path.Exists(appConfig.Deno))
    {
        Console.WriteLine($"One or more dependancies are missing. Since you have \"UpdateReqsOnStartup\" disabled, these cannot be auto-downloaded. Please either place these in the correct directories or re-enable UpdateReqsOnStartup in your app config. Press Enter to quit:\n    -> {appConfig.Ffmpeg}\n    -> {appConfig.Deno}");
        Console.ReadLine();
        Environment.Exit(1);
    }
    
    if (errorOccuredDuringStartup)
    {
        Console.WriteLine(new string('-', Console.WindowWidth) + "\nOne or more errors occured during startup. Are you sure you wish to continue? (Y/n)");
        if (!string.Equals(Console.ReadLine()?.ToLower(), "y")) {Environment.Exit(1);}
    }
    bool consoleWarned = false;
    while (true)
    {
        try 
        {
            Console.Clear();
        } catch (IOException) 
        {
            if (!consoleWarned) 
            {
                Console.WriteLine("It seems like you are using a faux console. Thats fine, but things might look weird as things like line clears or \"\\r\" doesn't work");
                consoleWarned = true;
            }
        }
        Console.WriteLine(welcomeMessage);

        MediaDownloader downloadManager = new MediaDownloader();
        downloadManager.SetAppConfig(appConfig);

        Console.Write("Please paste your link -> ");
        string? writtenLink = Console.ReadLine();
        if (writtenLink is null) {continue;}
        if (!writtenLink.Contains("youtu", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write("The provided link doesn't seem to be a YouTube Link. This may not work correctly with the current version. Are you sure you wish to continue? (Y/n) > ");
            string? proceed = Console.ReadLine();
            if (proceed is not null && !proceed.Contains('y', StringComparison.OrdinalIgnoreCase)) {continue;}
        }
        downloadManager.Url = writtenLink;
        (bool isPlaylist, string videoName) = await isLinkPlaylist(downloadManager.Url);
        
        if (isPlaylist)
        {
            Console.Write("The provided link is a playlist. Would you like to download all videos or just the selected one? (A for all, anything else for just the selected video) > ");
            string? allOrOne = Console.ReadLine(); 
            if (allOrOne is not null && allOrOne.Contains('a', StringComparison.OrdinalIgnoreCase))
            {
                downloadManager.IsPlaylist = isPlaylist;
            }
            
        }

        string fallbackName = appConfig.SaveNameEachTime && !string.IsNullOrWhiteSpace(appConfig.LastUsedName) ? appConfig.LastUsedName : videoName;
        Console.Write($"Please enter a name (null popuplates with {fallbackName}) -> ");
        string? fileNameToUse = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(fileNameToUse)) {fileNameToUse = fallbackName;}
        downloadManager.FileName = MediaDownloader.SanitiseFileName(fileNameToUse);

        async void promptWhichBest() 
        {
            Console.Write("Do you want to use the highest quality available or select your on video and audio streams?\nType \"best-audio\" to save as an audio file with no video feed\nType \"best-video\" to save as a video file with no audio\nType \"best\" to save the best video and auido together\n -> ");
            string? useBest = Console.ReadLine();
            switch (useBest?.Trim().ToLowerInvariant())
            {
                case "best-audio":
                    downloadManager.AudioOnly = true;
                    downloadManager.UseHighestQuality = true;
                    break;
                case "best-video":
                    downloadManager.VideoOnly = true;
                    downloadManager.UseHighestQuality = true;
                    break;
                case "best":
                    downloadManager.UseHighestQuality = true;
                    break;
                default:
                    (downloadManager.VideoStream, downloadManager.AudioStream) = await promptStreams(downloadManager.Url);
                    break;
            }

            string mainMessage = "Finally, what file format would you like to use? The supported formats are: ";
            switch (downloadManager.VideoStream, downloadManager.AudioStream) 
            {
                case ("-", "-"):
                    promptWhichBest();
                    return;
                case ("", not ""):
                    Console.Write($"\r{mainMessage}{string.Join(", ", audioFormats)} -> {downloadManager.FileName}.");
                    downloadManager.AudioOnly = true;
                    break;
                case (not "", ""):
                    Console.Write($"\r{mainMessage}{string.Join(", ", videoFormats)} -> {downloadManager.FileName}.");
                    downloadManager.VideoOnly = true;
                    break;
                default:
                    Console.Write($"\r{mainMessage}{string.Join(", ", videoFormats)} -> {downloadManager.FileName}.");
                    break;
            }
        }

        while  (true) 
        {
            promptWhichBest();
            string? formatToTest = Console.ReadLine()?.Replace(".", "");
            if (formatToTest is null) {continue;}
            if (downloadManager.AudioOnly)
            {
                if (audioFormats.Contains(formatToTest))
                {
                    downloadManager.OutputFormat = formatToTest;
                    break;
                }
            }
            else 
            {
                if (videoFormats.Contains(formatToTest))
                {
                    downloadManager.OutputFormat = formatToTest;
                    break;
                }
            }
        }
        Console.WriteLine("Begining download...");
        downloadManager.StartDownload();
        Console.Write($"Download successfully completed! You can find your download(/s) in \"{appConfig.GetDownloadsPath()}/Anna's YT Downloader\". Press Enter to restart the program. If you've downloaded all the videos you need, you may now close the application! ");
        Console.ReadLine();
    }
}

try
{
    await run();
} catch (Exception e)
{
    Console.WriteLine(e.ToString());
    Console.WriteLine("Something went horribly wrong... Please contact Anna for help fixing this if restarting doesn't do the trick.");
    Console.ReadLine();
}

class AppConfig
{
    public static string ApplicationDirectory {get;} = Path.GetFullPath(AppContext.BaseDirectory);
    private static readonly string ConfigLocation = ResolveAppPath("config.json");
    private static readonly string TempConfigLocation = ResolveAppPath("config.json.temp");
    private static readonly JsonSerializerOptions JsonWriteIndented = new() {WriteIndented = true};
    private static readonly AppJsonContext JsonContext = new(JsonWriteIndented);
    public bool ErrorOccurredDuringStartup;

    public readonly string  Version = "2.4";
    public bool UpdateReqsOnStartup {get; set;} = true;
    public string YtDlp {get; set;} = ResolveAppPath(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp");
    public string Ffmpeg {get; set;} = ResolveAppPath(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Path.Combine("ffmpeg", "ffmpeg.exe") : Path.Combine("ffmpeg", "ffmpeg"));
    public string FfmpegLatestVersion {get; set;} = "";
    public string Deno {get; set;} = ResolveAppPath(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Path.Combine("deno", "deno.exe") : Path.Combine("deno", "deno"));
    public string DenoLatestVersion {get; set;} = "";
    public bool SaveNameEachTime {get; set;} = true;
    public string LastUsedName {get; set;} = "";
    public string DownloadsFolder {get; set;} = GetDownloadsFolder();

    private readonly string AppendToDownloads = "Annas YT Downloader";

    public static string ResolveAppPath(string path)
    {
        return Path.GetFullPath(path, ApplicationDirectory);
    }

    private static string GetDownloadsFolder()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Downloads");
    }
    public string GetDownloadsPath()
    {
        return Path.Combine(DownloadsFolder, AppendToDownloads);
    }

    public void Save()
    {
        try
        {
            
            if (File.Exists(TempConfigLocation))
            {
                Console.WriteLine("Old config file found from failed overwrite... Deleting");
                File.Delete(TempConfigLocation);
            }
            if (File.Exists(ConfigLocation)) {MoveConfigBeforeOverwrite(ResolveAppPath(Path.Combine("Configs", "Old Config Files")));}

            File.WriteAllText(TempConfigLocation, JsonSerializer.Serialize(this, JsonContext.AppConfig));
            File.Move(TempConfigLocation, ConfigLocation);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.StackTrace);
            Console.WriteLine($"An error occured when saving the app config. You may want to manually save the changes to {Path.GetFullPath(ConfigLocation)}: {e.Message}");
            ErrorOccurredDuringStartup = true;
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigLocation)) {throw new NoConfigFileFound();}
            AppConfig loadedConfig = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigLocation), AppJsonContext.Default.AppConfig)
                ?? throw new NoConfigFileFound();
            loadedConfig.YtDlp = ResolveAppPath(loadedConfig.YtDlp);
            loadedConfig.Ffmpeg = ResolveAppPath(loadedConfig.Ffmpeg);
            loadedConfig.Deno = ResolveAppPath(loadedConfig.Deno);
            loadedConfig.DownloadsFolder = ResolveAppPath(loadedConfig.DownloadsFolder);
            return loadedConfig;
        }
        catch (NoConfigFileFound)
        {
            Console.WriteLine("No config file found, writing defaults");
        }
        catch (Exception e)
        {
            Console.WriteLine(e.StackTrace);
            Console.WriteLine($"An error occured when loading the app config, moving the file before writing defaults: {e.Message}");
            try
            {
                MoveConfigBeforeOverwrite(ResolveAppPath(Path.Combine("Configs", "Broken Config Files")));
            }
            catch (Exception innerE)
            {
                Console.WriteLine(innerE.StackTrace);
                Console.WriteLine($"Couldn't move before overwriting as an error occurred: {innerE.Message}");
            }
        }

        AppConfig defaultAppConfig = new();
        defaultAppConfig.Save();
        return defaultAppConfig;
    }

    private static void MoveConfigBeforeOverwrite(string dirToMoveTo)
    {
        Directory.CreateDirectory(dirToMoveTo);
        string[] files = Directory.GetFiles(dirToMoveTo, "config*.json");
        int highestNumber = 0;
        
        DirectoryInfo dirToCheck = new DirectoryInfo(dirToMoveTo);
        if ((20 * 1024 * 1024) <= GetDirSize(dirToCheck))
        {
            Directory.Delete(dirToMoveTo, true);
            Directory.CreateDirectory(dirToMoveTo);
        }

        string destinationName = Path.GetFileName(ConfigLocation);
        if (files.Length > 0)
        {
            foreach (string file in files)
            {
                Match match = Regex.Match(Path.GetFileNameWithoutExtension(file), @"\((\d+)\)$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int currentNumber))
                {
                    highestNumber = Math.Max(highestNumber, currentNumber);
                }
            }
            destinationName = $"{Path.GetFileNameWithoutExtension(ConfigLocation)}({highestNumber + 1}){Path.GetExtension(ConfigLocation)}";
        }

        File.Move(ConfigLocation, Path.Combine(dirToMoveTo, destinationName));
    }

    // Source - https://stackoverflow.com/a/468131

    // Posted by hao, modified by community. See post 'Timeline' for change history

    // Retrieved 2026-09-08, License - CC BY-SA 3.0
    private static long GetDirSize(DirectoryInfo directory)
    {
        long size = 0;
        FileInfo[] files = directory.GetFiles();
        foreach (FileInfo file in files)
        {
            size += file.Length;
        }

        DirectoryInfo[] subDirs = directory.GetDirectories();
        foreach (DirectoryInfo subDir in subDirs)
        {
            size += GetDirSize(subDir);
        }
        return size;
    }
}

class MediaDownloader
{
    private AppConfig? appConfig = null;
    public string Url {get; set;} = "";
    public bool IsPlaylist {get; set;} = false;
    public bool UseHighestQuality {get; set;} = false;
    public string VideoStream {get; set;} = "";
    public string AudioStream {get; set;} = "";
    
    public string FileName {get; set;} = "";
    public string OutputFormat {get; set;} = "mp4";
    public bool AudioOnly {get; set;} = false;
    public bool VideoOnly {get; set;} = false;

    private int SpinnerIndex = 0;
    private readonly char[] spinner = {'-', '\\', '|', '/'};
    private readonly int TotalBarLength = 40;

    public void SetAppConfig(AppConfig appConfigToSet)
    {
        appConfig = appConfigToSet;
    }

    public void StartDownload()
    {
        ArgumentNullException.ThrowIfNull(appConfig, "appConfig");
        string formatSelection;
        string processingArgs;

        if (AudioOnly)
        {
            formatSelection = UseHighestQuality ? "bestaudio/best" : AudioStream;
            processingArgs = $"--extract-audio --audio-format {OutputFormat}";
        }
        else if (VideoOnly)
        {
            formatSelection = UseHighestQuality ? "bestvideo" : VideoStream;
            processingArgs = $"--remux-video {OutputFormat}";
        }
        else
        {
            formatSelection = UseHighestQuality || string.IsNullOrEmpty(VideoStream) || string.IsNullOrEmpty(AudioStream)
                ? "bestvideo+bestaudio/best" : $"{VideoStream}+{AudioStream}";
            processingArgs = $"--merge-output-format {OutputFormat}";
        }

        string playlistFlag = IsPlaylist ? "--yes-playlist" : "--no-playlist";
        string outputNamingTemplate = !IsPlaylist ? $"{FileName}.%(ext)s" : $"{FileName}s #%(playlist_index)s - %(title)s.%(ext)s";
        string progressTemplate = "PROGRESS:DL:%(progress.downloaded_bytes)s|TOT:%(progress.total_bytes)s|EST:%(progress.total_bytes_estimate)s|PCT:%(progress._percent_str)s|SPD:%(progress._speed_str)s|ETA:%(progress._eta_str)s";
        
        string downloadPath = appConfig.GetDownloadsPath();
        int index = 1;
        if (IsPlaylist)
        {
            if (Path.Exists(Path.Combine(appConfig.GetDownloadsPath(), FileName)))
            {while (true)
            {
                string pathToTest = Path.Combine(appConfig.GetDownloadsPath(), $"{FileName}({index})");
                if (!Path.Exists(pathToTest))
                {
                    downloadPath = pathToTest;
                    break;
                }
                index ++;
            }} else {
                downloadPath = Path.Combine(appConfig.GetDownloadsPath(), $"{FileName}");
            }
        } else {
            if (File.Exists(Path.Combine(appConfig.GetDownloadsPath(), $"{FileName}.{OutputFormat}")))
            {while (true)
            {
                string pathToTest = Path.Combine(appConfig.GetDownloadsPath(), $"{FileName}({index}).{OutputFormat}");
                if (!Path.Exists(pathToTest))
                {
                    outputNamingTemplate = $"{FileName}({index}).%(ext)s";
                    break;
                }
                index ++;
            }}
        }

        string args = $"-P \"{downloadPath}\" -f \"{formatSelection}\" {playlistFlag} {processingArgs} -o \"{outputNamingTemplate}\" --js-runtimes \"deno:{appConfig.Deno}\" --newline --progress-template \"{progressTemplate}\" --progress-delta 0.03 --ffmpeg-location \"{appConfig.Ffmpeg}\" \"{Url}\"";
        ExecuteDownload(appConfig.YtDlp, args);
    }

    private void ExecuteDownload(string executable, string args)
    {
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = args,
            WorkingDirectory = AppConfig.ApplicationDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using (Process process = new Process {StartInfo=psi})
        {
            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data) && e.Data.StartsWith("PROGRESS:"))
                {
                    DrawFancyBar(e.Data.Replace("PROGRESS:", ""));
                }
                else
                {
                    Console.WriteLine($"\nData:     {e.Data}"); //Keeping as idk what this does
                }
                
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.WriteLine($"\nWARN/ERR: {e.Data}");
                }
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            
            Console.WriteLine("\nDownload Process Finished.");
        }
    }

    private void DrawFancyBar(string data)
    {
        var parts = new Dictionary<string, string>();
        foreach (var segment in data.Split('|'))
        {
            var pair = segment.Split(':', 2);
            if (pair.Length == 2) {parts[pair[0]] = pair[1];}
        }

        double downloadBytes = double.TryParse(parts.GetValueOrDefault("DL", "0"), out double d) ? d : 0;

        double totalBytes = double.TryParse(parts.GetValueOrDefault("TOT", "0"), out double t) 
            ? t : (double.TryParse(parts.GetValueOrDefault("EST", "0"), out double e) ? e : 0);
            // Do total bytes if available, else do estimated bytes

        double downloadMB = downloadBytes / 1048576.0;
        double totalMB = totalBytes / 1048576.0;

        string percentageStr = parts.GetValueOrDefault("PCT", "0%").Trim();
        string speedStr = parts.GetValueOrDefault("SPD", "0MB/s").Trim();
        string etaStr = parts.GetValueOrDefault("ETA", "00:00").Trim();

        double percentageAsFloat = totalBytes > 0 ? downloadBytes / totalBytes : 0;
        int filled = (int)Math.Min(TotalBarLength, Math.Max(0, percentageAsFloat * TotalBarLength));

        string bar = new string('█', filled) + new string('-', TotalBarLength - filled);
        SpinnerIndex = (SpinnerIndex + 1) % spinner.Length;
        char spin = spinner[SpinnerIndex];

        Console.Write($"\r{spin} <{downloadMB:F2} / {totalMB:F2}> {percentageStr} _/{bar}\\_ ({speedStr}) ETA: {etaStr}");

    }

    // Source - https://stackoverflow.com/a/847251
    // Posted by Andre, modified by community. See post 'Timeline' for change history
    // Retrieved 2026-09-08, License - CC BY-SA 3.0
    public static string SanitiseFileName(string name)
    {
       string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
       string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

       return Regex.Replace(name, invalidRegStr, "_").Replace(" ", "_");
    }
}

class StreamInfo
{
    public string FormatId { get; set; } = "";
    public string Resolution { get; set; } = "";
    public double Bitrate { get; set; }
    public double SizeMB { get; set; }
}

class UpdateInfo
{
    public string LatestVersion {get; set;} = "";
    public string WindowsUpdaterUrl {get; set;} = "";
    public string LinuxUpdaterUrl {get; set;} = "";
    public string MacUpdaterUrl {get; set;} = "";
}

class NoConfigFileFound : Exception
{
    public NoConfigFileFound() : base() {}
}
class RequiredDependancyMissing : Exception
{
    public RequiredDependancyMissing(string message) : base(message) {}
}

[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(UpdateInfo))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
