// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;

var appConfig = new AppConfig();
bool errorOccuredDuringStartup = false;
string welcomeMessage = "Welcome to Anna's File Transfer System, C# Edition! This is my first C# program so sorry if its not great";
Console.WriteLine(welcomeMessage);

var videoFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "avi", "flv", "m4a", "mka", "mkv", "mov", "mp4", "ogg", "opus", "webm"
};
var audioFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "aac", "alac", "flac", "m4a", "mp3", "ogg", "opus", "wav"
};

async Task<UpdateInfo> getLatestUpdateInfo()
{
    using var client = new HttpClient();
    string json = await client.GetStringAsync("https://annaspannertechsolutions.uk/annas-youtube-downloader/latest");
    return JsonSerializer.Deserialize(json, AppJsonContext.Default.UpdateInfo)
        ?? throw new InvalidDataException("The update server returned an empty response.");
}

async Task<bool> checkSelfForUpdates(UpdateInfo updateInfo)
{
    ReleaseInfo? release = updateInfo.Self;
    if (release is null || string.IsNullOrWhiteSpace(release.LatestVersion) || release.LatestVersion == appConfig.Version) {return true;}

    DependencyDownload? updaterDownload = release.GetDownload(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
        RuntimeInformation.OSArchitecture);
    if (updaterDownload is null)
    {
        Console.WriteLine("An app update is available, but no download is configured for this platform.");
        return true;
    }

    using var client = new HttpClient();
    Console.Write("An update was found to this app! Would you like to install it? (Y/n) ->");
    if (Console.ReadLine()!.ToLower() == "n") {return true;}
    Console.WriteLine("Downloading patch...");

    string updaterName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "updater.exe" : "updater";
    string updaterFolder = Path.Combine(Path.GetTempPath(), $"AnnasYTUpdater-{Guid.NewGuid():N}");
    string updaterPath = Path.Combine(updaterFolder, updaterName);
    Directory.CreateDirectory(updaterFolder);

    try
    {
        try
        {
            await downloadAndVerify(client, updaterDownload.Url!, updaterPath, updaterDownload.Sha256!);
        }
        catch (Exception e) when (e is InvalidDataException or FormatException)
        {
            appConfig.UpdateReqsOnStartup = false;
            appConfig.Save();
            Console.WriteLine($"The self-updater failed SHA-256 verification or has invalid hash metadata. Automatic updates have been disabled in the config, please let Anna-Rose know! Details: {e.Message}");
            return false;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(updaterPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var processInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = updaterFolder,
            UseShellExecute = true
        };
        processInfo.ArgumentList.Add(AppConfig.ApplicationDirectory);
        if (Process.Start(processInfo) is null) {throw new InvalidOperationException("The self-updater process could not be started.");}

        Console.WriteLine("Updater started. This application will now close.");
        Environment.Exit(0);
        return true;
    }
    finally
    {
        if (Directory.Exists(updaterFolder)) {Directory.Delete(updaterFolder, true);}
    }
}

async Task downloadAndVerify(HttpClient client, string downloadLink, string destinationPath, string expectedSha256)
{
    if (!Uri.TryCreate(downloadLink, UriKind.Absolute, out Uri? downloadUri) || string.IsNullOrWhiteSpace(expectedSha256))
    {
        throw new InvalidDataException("The download URL or SHA-256 hash is missing.");
    }

    byte[] expectedHash = Convert.FromHexString(expectedSha256.Trim().Replace("sha256:", "", StringComparison.OrdinalIgnoreCase));
    if (expectedHash.Length != 32) {throw new InvalidDataException("The expected SHA-256 hash must contain 64 hexadecimal characters.");}

    using (HttpResponseMessage response = await client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead))
    {
        response.EnsureSuccessStatusCode();
        await using Stream responseStream = await response.Content.ReadAsStreamAsync();
        await using FileStream outputStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await responseStream.CopyToAsync(outputStream);
    }

    await using FileStream downloadedStream = File.OpenRead(destinationPath);
    byte[] actualHash = await SHA256.HashDataAsync(downloadedStream);
    if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
    {
        File.Delete(destinationPath);
        throw new InvalidDataException("The downloaded file's SHA-256 hash does not match the server response.");
    }
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

async Task<bool> updateDependency(string dependancy, string downloadLink, string expectedSha256)
{
    string lDependancy = dependancy.ToLowerInvariant();
    string installedPath = lDependancy switch
    {
        "deno" => appConfig.Deno,
        "ffmpeg" => appConfig.Ffmpeg,
        _ => throw new ArgumentException("Tool must be 'deno' or 'ffmpeg'.", nameof(dependancy))
    };
    string exeName = Path.GetFileName(installedPath);
    string installFolder = Path.GetDirectoryName(installedPath)!;
    string tempPath = AppConfig.ResolveAppPath(Path.Combine("temp", $"{lDependancy} update"));
    string archivePath = "";

    try
    {
        using HttpClient client = new HttpClient();
        if (!Uri.TryCreate(downloadLink, UriKind.Absolute, out Uri? downloadUri) || string.IsNullOrWhiteSpace(expectedSha256))
        {
            Console.WriteLine($"Invalid download URL or hash\n    Download link: {downloadLink}\n    Expected hash: {expectedSha256}\nPlease contact Anna-Rose to rectify this: https://annaspannertechsolutions.uk");
            return false;
        }

        string archiveName = Path.GetFileName(downloadUri.LocalPath);
        if (!archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && !archiveName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            && !archiveName.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The download URL must end in .7z, .zip, or .tar.xz.");
        }
        string archiveExtension = archiveName.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ? ".tar.xz" : Path.GetExtension(archiveName);
        Directory.CreateDirectory(tempPath);
        archivePath = Path.Combine(tempPath, $"{lDependancy} {Guid.NewGuid():N}{archiveExtension}");

        await downloadAndVerify(client, downloadLink, archivePath, expectedSha256);

        using (var archive = ArchiveFactory.OpenArchive(archivePath))
        {
            foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
            {
                if (entry.Key is null) {continue;}
                string outputPath = Path.GetFullPath(Path.Combine(tempPath, entry.Key));
                if (!outputPath.StartsWith(Path.GetFullPath(tempPath))) {continue;}
                
                entry.WriteToDirectory(tempPath, new ExtractionOptions {ExtractFullPath = true, Overwrite = true});
            }
        }
        File.Delete(archivePath);
        archivePath = "";

        string? executablePath = Directory.EnumerateFiles(tempPath, exeName, SearchOption.AllDirectories).FirstOrDefault();
        if (executablePath is null) {throw new FileNotFoundException($"The {lDependancy} executable was not found in the archive.");}

        string executableFolder = Path.GetDirectoryName(executablePath)!;
        string? installParent = Path.GetDirectoryName(installFolder);
        ArgumentException.ThrowIfNullOrEmpty(installParent);
        Directory.CreateDirectory(installParent);

        if (Directory.Exists(installFolder)) {Directory.Delete(installFolder, true);}
        Directory.Move(executableFolder, installFolder);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (string file in Directory.GetFiles(installFolder))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        return true;
    }
    catch (Exception e)
    {
        Console.WriteLine($"\nCouldn't update {lDependancy}, continuing without it: {e.Message}");
        errorOccuredDuringStartup = true;
        return false;
    }
    finally
    {
        if (Directory.Exists(tempPath)) {Directory.Delete(tempPath, true);}
        if (!string.IsNullOrEmpty(archivePath) && File.Exists(archivePath)) {File.Delete(archivePath);}
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
            UpdateInfo updateInfo = await getLatestUpdateInfo();
            Console.Write("\rChecking for updates... Checking self");
            if (!await checkSelfForUpdates(updateInfo))
            {
                throw new InvalidDataException("Self-updater verification failed. Automatic updates were disabled.");
            }
            Console.Write("\rChecking for updates... This downloader is up to date! Checking Yt-dlp");
            await updateYtDlp();
            Console.Write("\rChecking for updates... This downloader is up to date! Yt-dlp up to date! Checking Ffmpeg");
            ReleaseInfo? ffmpegRelease = updateInfo.Ffmpeg;
            string? ffmpegVersion = ffmpegRelease?.LatestVersion;
            if (!string.IsNullOrWhiteSpace(ffmpegVersion)
                && (appConfig.FfmpegLatestVersion != ffmpegVersion || !File.Exists(appConfig.Ffmpeg)))
            {
                DependencyDownload? ffmpegDownload = ffmpegRelease!.GetDownload(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), RuntimeInformation.IsOSPlatform(OSPlatform.OSX), RuntimeInformation.OSArchitecture);
                if (ffmpegDownload is not null && await updateDependency("ffmpeg", ffmpegDownload.Url!, ffmpegDownload.Sha256!))
                {
                    appConfig.FfmpegLatestVersion = ffmpegVersion;
                    appConfig.Save();
                }
                else if (ffmpegDownload is null && !File.Exists(appConfig.Ffmpeg))
                {
                    Console.WriteLine("FFmpeg is not installed and no download is configured for this platform.");
                    errorOccuredDuringStartup = true;
                }
            }
            else if (!File.Exists(appConfig.Ffmpeg))
            {
                Console.WriteLine("FFmpeg is not installed and the update response contains no FFmpeg release version. This is a REQUIRED dependancy. Please ensure you manually install this before continuing and consider disabling auto-updates within the app config");
                errorOccuredDuringStartup = true;
            }
            Console.Write("\rChecking for updates... This downloader is up to date! Yt-dlp up to date! Ffmpeg up to date! Checking Deno");
            ReleaseInfo? denoRelease = updateInfo.Deno;
            string? denoVersion = denoRelease?.LatestVersion;
            if (!string.IsNullOrWhiteSpace(denoVersion)
                && (appConfig.DenoLatestVersion != denoVersion || !File.Exists(appConfig.Deno)))
            {
                DependencyDownload? denoDownload = denoRelease!.GetDownload(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), RuntimeInformation.IsOSPlatform(OSPlatform.OSX), RuntimeInformation.OSArchitecture);
                if (denoDownload is not null && await updateDependency("deno", denoDownload.Url!, denoDownload.Sha256!))
                {
                    appConfig.DenoLatestVersion = denoVersion;
                    appConfig.Save();
                }
                else if (denoDownload is null && !File.Exists(appConfig.Deno))
                {
                    Console.WriteLine("Deno is not installed and no download is configured for this platform.");
                    errorOccuredDuringStartup = true;
                }
            }
            else if (!File.Exists(appConfig.Deno))
            {
                Console.WriteLine("Deno is not installed and the update response contains no Deno release version. This is a REQUIRED dependancy. Please ensure you manually install this before continuing and consider disabling auto-updates within the app config");
                errorOccuredDuringStartup = true;
            }
            Console.Write("\rChecking for updates... This downloader is up to date! Yt-dlp up to date! Ffmpeg up to date! Deno up to date!");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Startup update check failed: {e}");
            errorOccuredDuringStartup = true;
        }
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
            Console.Write("Do you want to use the highest quality available or select your on video and audio streams?\nType \"best-audio\" to save as an audio file with no video feed\nType \"best-video\" to save as a video file with no audio\nType \"best\" to save the best video and audio together\n -> ");
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
    public static string ApplicationDirectory {get;} = Path.GetDirectoryName(Environment.ProcessPath)!;
    private static readonly string ConfigLocation = ResolveAppPath("config.json");
    private static readonly string TempConfigLocation = ResolveAppPath("config.json.temp");
    private static readonly JsonSerializerOptions JsonWriteIndented = new() {WriteIndented = true};
    private static readonly AppJsonContext JsonContext = new(JsonWriteIndented);
    public bool ErrorOccurredDuringStartup;

    public readonly string  Version = "2.4.0";
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
    [JsonPropertyName("self")]
    public ReleaseInfo? Self {get; set;}
    [JsonPropertyName("ffmpeg")]
    public ReleaseInfo? Ffmpeg {get; set;}
    [JsonPropertyName("deno")]
    public ReleaseInfo? Deno {get; set;}
}

class ReleaseInfo
{
    [JsonPropertyName("latestVersion")]
    public string? LatestVersion {get; set;}
    [JsonPropertyName("windows")]
    public PlatformDownloads? Windows {get; set;}
    [JsonPropertyName("linux")]
    public PlatformDownloads? Linux {get; set;}
    [JsonPropertyName("mac")]
    public PlatformDownloads? Mac {get; set;}

    public DependencyDownload? GetDownload(bool isWindows, bool isMac, Architecture architecture)
    {
        PlatformDownloads? platform = isWindows ? Windows : isMac ? Mac : Linux;
        if (platform is null) {return null;}

        IEnumerable<(string? Url, string? Sha256)> candidates = architecture switch
        {
            Architecture.X86 => [(platform.X86, platform.X86Hash)],
            Architecture.X64 => [(platform.X64, platform.X64Hash), (platform.X86, platform.X86Hash)],
            Architecture.Arm64 when isWindows => [(platform.Arm, platform.ArmHash), (platform.X64, platform.X64Hash), (platform.X86, platform.X86Hash)],
            Architecture.Arm64 when isMac => [(platform.Arm, platform.ArmHash), (platform.X64, platform.X64Hash)],
            Architecture.Arm64 => [(platform.Arm, platform.ArmHash)],
            _ => []
        };

        foreach ((string? url, string? sha256) in candidates)
        {
            if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(sha256))
            {
                return new DependencyDownload(url, sha256);
            }
        }

        return null;
    }
}

class PlatformDownloads
{
    [JsonPropertyName("x86")]
    public string? X86 {get; set;}
    [JsonPropertyName("x86 hash")]
    public string? X86Hash {get; set;}
    [JsonPropertyName("x64")]
    public string? X64 {get; set;}
    [JsonPropertyName("x64 hash")]
    public string? X64Hash {get; set;}
    [JsonPropertyName("ARM")]
    public string? Arm {get; set;}
    [JsonPropertyName("ARM hash")]
    public string? ArmHash {get; set;}
}

class DependencyDownload
{
    public DependencyDownload(string url, string sha256)
    {
        Url = url;
        Sha256 = sha256;
    }

    public string Url {get;}
    public string Sha256 {get;}
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
