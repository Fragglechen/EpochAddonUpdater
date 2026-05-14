using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace EpochAddonUpdater;

public partial class MainWindow : Window
{
    private const string ExpectedInterface = "30300";
    private readonly ObservableCollection<AddonInfo> _addons = [];
    private readonly ObservableCollection<AddonInfo> _recommendedAddons = [];
    private readonly ObservableCollection<AddonInfo> _visibleAddons = [];
    private readonly HttpClient _http = new();
    private AppSettings _settings = new();
    private string _settingsPath = "";
    private string _normalFooterTitle = "Scanning addons...";
    private string _normalFooterSubtitle = "";
    private bool _launchTargetRunning;
    private bool _launcherRunning;
    private bool _wowRunning;
    private bool _scanInProgress;
    private DispatcherTimer? _processTimer;

    public MainWindow()
    {
        InitializeComponent();
        ApplyRandomBackground();
        AddonList.ItemsSource = _visibleAddons;
        Loaded += async (_, _) => await InitializeAsync();
    }

    private void ApplyRandomBackground()
    {
        const int backgroundCount = 10;
        var index = Random.Shared.Next(1, backgroundCount + 1);
        LauncherBackgroundBrush.ImageSource = new BitmapImage(new Uri($"pack://application:,,,/Assets/LauncherBackgrounds/launcher-background-{index:00}.webp", UriKind.Absolute));
    }

    private async Task InitializeAsync()
    {
        AppLogger.Info("Main window initialization started.");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EpochAddonUpdater/1.0");
        LoadSettings();
        VersionButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "\uE946", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 0, 5, 0) },
                new TextBlock { Text = $"v{GetAppVersion()}", FontSize = 12 }
            }
        };
        StartProcessWatcher();
        LoadAddonPlaceholders();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        RunLoggedAsync(ScanAsync(checkRemote: true), "Initial scan");
        AppLogger.Info("Main window initialization finished.");
    }

    private static async void RunLoggedAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"{operation} failed.", ex);
        }
    }

    private void LoadSettings()
    {
        AppLogger.Info("Loading settings.");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpochAddonUpdater");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");

        if (File.Exists(_settingsPath))
        {
            _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new AppSettings();
        }

        var defaultInstall = @"D:\spiele\Ascension-WOW\Launcher\resources\epoch-live";
        if (string.IsNullOrWhiteSpace(_settings.InstallLocation) && Directory.Exists(defaultInstall))
        {
            _settings.InstallLocation = defaultInstall;
        }

        if (string.IsNullOrWhiteSpace(_settings.LauncherExecutable))
        {
            _settings.LauncherExecutable = @"D:\spiele\Ascension-WOW\Launcher\Ascension Launcher.exe";
        }

        if (!_settings.FavoriteAuthorUrls.Contains("https://github.com/Fragglechen", StringComparer.OrdinalIgnoreCase))
        {
            _settings.FavoriteAuthorUrls.Add("https://github.com/Fragglechen");
        }

        SaveSettings();
    }

    private void SaveSettings()
    {
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        AppLogger.Info($"Settings saved to {_settingsPath}.");
    }

    private void LoadAddonPlaceholders()
    {
        _addons.Clear();
        var addonRoot = Path.Combine(_settings.InstallLocation, "Interface", "AddOns");
        if (!Directory.Exists(addonRoot))
        {
            RenderAddons();
            return;
        }

        foreach (var dir in GetAddonDirectories(addonRoot))
        {
            var name = Path.GetFileName(dir);
            _addons.Add(new AddonInfo
            {
                FolderName = name,
                FolderPath = dir,
                Title = name,
                Description = "Waiting for verification...",
                IgnoreUpdates = _settings.IgnoredAddons.Contains(name, StringComparer.OrdinalIgnoreCase)
            });
        }

        RenderAddons();
    }

    private static List<string> GetAddonDirectories(string addonRoot)
    {
        return Directory.GetDirectories(addonRoot)
            .Where(IsRealAddonDirectory)
            .OrderBy(Path.GetFileName)
            .ToList();
    }

    private static bool IsRealAddonDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return !name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ScanAsync(bool checkRemote)
    {
        AppLogger.Info($"Scan started. checkRemote={checkRemote}");
        _scanInProgress = true;
        SetFooterStatus(checkRemote ? "Verifying addons and checking remotes..." : "Verifying addons...");
        try
        {
            Progress.Value = 8;
            var addonRoot = Path.Combine(_settings.InstallLocation, "Interface", "AddOns");
            if (!Directory.Exists(addonRoot))
            {
                SetFooterStatus($"Addon folder not found: {addonRoot}");
                RenderAddons();
                return;
            }

            var dirs = GetAddonDirectories(addonRoot);
            var folderNames = dirs.Select(Path.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var removed in _addons.Where(a => !folderNames.Contains(a.FolderName)).ToList())
            {
                _addons.Remove(removed);
            }
            RenderAddons();

            var verified = 0;
            using var localGate = new System.Threading.SemaphoreSlim(Math.Min(8, Math.Max(2, Environment.ProcessorCount)));
            var readTasks = dirs.Select(async dir =>
            {
                await localGate.WaitAsync();
                try
                {
                    return await Task.Run(() => ReadAddon(dir));
                }
                finally
                {
                    localGate.Release();
                }
            }).ToList();

            while (readTasks.Count > 0)
            {
                var finishedTask = await Task.WhenAny(readTasks);
                readTasks.Remove(finishedTask);
                UpsertAddon(await finishedTask);
                verified++;
                Progress.Value = 10 + verified * 60.0 / Math.Max(1, dirs.Count);
            }

            var installedNames = _addons.Select(a => a.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var addon in _addons)
            {
                foreach (var dep in addon.Dependencies)
                {
                    dep.IsInstalled = installedNames.Contains(dep.Name);
                }

                if (addon.Status != AddonStatus.Failed && addon.Dependencies.Any(d => !d.Optional && !d.IsInstalled))
                {
                    addon.Status = AddonStatus.MissingDependencies;
                    addon.StatusText = "Missing dependencies";
                }
            }

            if (checkRemote)
            {
                var sourceAddons = _addons.Where(a => !a.IgnoreUpdates && (Directory.Exists(Path.Combine(a.FolderPath, ".git")) || File.Exists(Path.Combine(a.FolderPath, ".epoch-addon-updater.json")))).ToList();
                await Task.Run(() =>
                {
                    foreach (var addon in sourceAddons)
                    {
                        LoadLocalSourceInfo(addon);
                    }
                });

                var gitAddons = _addons.Where(a => !a.IgnoreUpdates && !string.IsNullOrWhiteSpace(a.RepositoryUrl)).ToList();
                var completed = 0;
                using var remoteGate = new System.Threading.SemaphoreSlim(4);
                var remoteTasks = gitAddons.Select(async addon =>
                {
                    await remoteGate.WaitAsync();
                    try
                    {
                        await CheckRemoteAsync(addon);
                    }
                    finally
                    {
                        remoteGate.Release();
                    }

                    var done = System.Threading.Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() => Progress.Value = 70 + done * 25.0 / Math.Max(1, gitAddons.Count));
                }).ToList();
                await Task.WhenAll(remoteTasks);
                await LoadRecommendedAddonsAsync(folderNames);
            }

            Progress.Value = 100;
            _scanInProgress = false;
            SetFooterStatus(_addons.Count(a => a.HasUpdate) > 0 ? "Updates available!" : "Everything up to date!");
            NotifyAllAddons();
            RenderAddons();
            AppLogger.Info($"Scan finished. Installed={_addons.Count}, Recommended={_recommendedAddons.Count}, Updates={_addons.Count(a => a.HasUpdate)}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Scan failed.", ex);
            SetFooterStatus("Scan failed.", ex.Message);
        }
        finally
        {
            if (_scanInProgress)
            {
                _scanInProgress = false;
                ApplyLaunchState();
            }
        }
    }

    private AddonInfo ReadAddon(string path)
    {
        var addon = new AddonInfo
        {
            FolderName = Path.GetFileName(path),
            FolderPath = path,
            IgnoreUpdates = _settings.IgnoredAddons.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        };
        LoadRepoMetadata(path, addon);

        var toc = Directory.GetFiles(path, "*.toc", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (toc is null)
        {
            addon.Status = AddonStatus.Failed;
            addon.StatusText = "Failed to verify";
            addon.Problem = "This addon is missing a .toc file. Make sure the addon folder is complete and references the correct repository.";
            addon.Title = addon.FolderName;
            addon.Description = "No valid addon metadata could be found.";
            return addon;
        }

        addon.TocPath = toc;
        var metadata = ParseToc(toc);
        addon.Title = CleanTitle(metadata.GetValueOrDefault("Title") ?? addon.FolderName);
        addon.Description = metadata.GetValueOrDefault("Notes") ?? "";
        addon.Author = metadata.GetValueOrDefault("Author") ?? metadata.GetValueOrDefault("X-Curse-Project-Name") ?? "";
        addon.Version = metadata.GetValueOrDefault("Version") ?? "";
        addon.Interface = metadata.GetValueOrDefault("Interface") ?? "";

        AddDependencies(addon, metadata.GetValueOrDefault("Dependencies"), optional: false);
        AddDependencies(addon, metadata.GetValueOrDefault("RequiredDeps"), optional: false);
        AddDependencies(addon, metadata.GetValueOrDefault("Deps"), optional: false);
        AddDependencies(addon, metadata.GetValueOrDefault("OptionalDeps"), optional: true);

        if (addon.Interface != ExpectedInterface)
        {
            addon.Status = AddonStatus.OutOfDate;
            addon.StatusText = "Out of date";
            addon.Problem = $"This addon seems to be made for different game version ({addon.Interface}) and it may not function correctly.";
        }

        return addon;
    }

    private void UpsertAddon(AddonInfo updated)
    {
        var existing = _addons.FirstOrDefault(a => a.FolderName.Equals(updated.FolderName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _addons.Add(updated);
            return;
        }

        var index = _addons.IndexOf(existing);
        _addons[index] = updated;
    }

    private static void LoadLocalSourceInfo(AddonInfo addon)
    {
        LoadGitMetadataFromFiles(addon);
    }

    private static void LoadGitMetadataFromFiles(AddonInfo addon)
    {
        var gitPath = Path.Combine(addon.FolderPath, ".git");
        if (!Directory.Exists(gitPath))
        {
            return;
        }

        var configPath = Path.Combine(gitPath, "config");
        if (string.IsNullOrWhiteSpace(addon.RepositoryUrl) && File.Exists(configPath))
        {
            addon.RepositoryUrl = ReadGitConfigValue(configPath, "remote \"origin\"", "url");
        }

        var headPath = Path.Combine(gitPath, "HEAD");
        if (File.Exists(headPath))
        {
            var head = File.ReadAllText(headPath).Trim();
            if (head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
            {
                var reference = head["ref:".Length..].Trim();
                addon.Branch = string.IsNullOrWhiteSpace(addon.Branch) ? reference.Split('/').Last() : addon.Branch;
                var refPath = Path.Combine(gitPath, reference.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(addon.LocalCommit) && File.Exists(refPath))
                {
                    addon.LocalCommit = File.ReadAllText(refPath).Trim();
                }
                if (string.IsNullOrWhiteSpace(addon.LocalCommit))
                {
                    addon.LocalCommit = ReadPackedRef(gitPath, reference);
                }
            }
            else if (string.IsNullOrWhiteSpace(addon.LocalCommit))
            {
                addon.LocalCommit = head;
            }
        }

        var headsPath = Path.Combine(gitPath, "refs", "heads");
        if (Directory.Exists(headsPath))
        {
            addon.Branches = Directory.GetFiles(headsPath, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(headsPath, p).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(b => b)
                .ToList();
        }
    }

    private static string ReadGitConfigValue(string configPath, string sectionName, string key)
    {
        var inSection = false;
        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                inSection = line.Equals($"[{sectionName}]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inSection && line.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase))
            {
                return line[(key.Length + 2)..].Trim();
            }
        }
        return "";
    }

    private static string ReadPackedRef(string gitPath, string reference)
    {
        var packedRefs = Path.Combine(gitPath, "packed-refs");
        if (!File.Exists(packedRefs))
        {
            return "";
        }
        foreach (var line in File.ReadLines(packedRefs))
        {
            if (line.StartsWith("#") || line.StartsWith("^"))
            {
                continue;
            }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].Equals(reference, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }
        return "";
    }

    private static Dictionary<string, string> ParseToc(string tocPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(tocPath))
        {
            var match = Regex.Match(line, @"^##\s*([^:]+):\s*(.*)$");
            if (match.Success)
            {
                result[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();
            }
        }

        return result;
    }

    private static void AddDependencies(AddonInfo addon, string? value, bool optional)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var name in value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!addon.Dependencies.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                addon.Dependencies.Add(new DependencyInfo { Name = name, Optional = optional });
            }
        }
    }

    private async Task CheckRemoteAsync(AddonInfo addon)
    {
        if (string.IsNullOrWhiteSpace(addon.RepositoryUrl))
        {
            return;
        }

        addon.Branch = string.IsNullOrWhiteSpace(addon.Branch) ? "main" : addon.Branch;
        var branches = await RunGitAsync(_settings.InstallLocation, $"ls-remote --heads {addon.RepositoryUrl}");
        var branchCommits = ParseRemoteHeads(branches);
        var parsedBranches = branches.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split("refs/heads/").LastOrDefault()?.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (parsedBranches.Count > 0)
        {
            addon.Branches = parsedBranches!;
        }

        if (branchCommits.TryGetValue(addon.Branch, out var commit))
        {
            addon.RemoteCommit = commit;
            addon.HasUpdate = !string.IsNullOrWhiteSpace(addon.LocalCommit) && !addon.LocalCommit.Equals(commit, StringComparison.OrdinalIgnoreCase);
            if (addon.HasUpdate && addon.Status == AddonStatus.Ok)
            {
                addon.StatusText = "Update";
            }
        }
    }

    private async Task LoadRecommendedAddonsAsync(HashSet<string> installedFolderNames)
    {
        AppLogger.Info("Loading recommended addons.");
        _recommendedAddons.Clear();
        var authorUrls = _settings.FavoriteAuthorUrls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (authorUrls.Count == 0)
        {
            RenderAddons();
            return;
        }

        SetFooterStatus("Checking favorite authors...");
        var installedRepos = _addons
            .Select(a => NormalizeRepo(a.RepositoryUrl))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var gate = new System.Threading.SemaphoreSlim(4);
        var tasks = authorUrls.Select(async url =>
        {
            await gate.WaitAsync();
            try
            {
                return await LoadAuthorRecommendationsAsync(url, installedFolderNames, installedRepos);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        foreach (var task in tasks)
        {
            List<AddonInfo> loaded;
            try
            {
                loaded = await task;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Loading recommendations for a favorite author failed.", ex);
                continue;
            }
            foreach (var addon in loaded)
            {
                if (_recommendedAddons.Any(a => NormalizeRepo(a.RepositoryUrl) == NormalizeRepo(addon.RepositoryUrl)))
                {
                    continue;
                }
                _recommendedAddons.Add(addon);
            }
        }
        AppLogger.Info($"Recommended addons loaded. Count={_recommendedAddons.Count}");
        RenderAddons();
    }

    private async Task<List<AddonInfo>> LoadAuthorRecommendationsAsync(string authorUrl, HashSet<string> installedFolderNames, HashSet<string> installedRepos)
    {
        AppLogger.Info($"Checking favorite author URL: {authorUrl}");
        var result = new List<AddonInfo>();
        var repos = await GetFavoriteAuthorRepositoriesAsync(authorUrl);
        foreach (var repo in repos)
        {
            if (installedRepos.Contains(NormalizeRepo(repo.Url)) || installedFolderNames.Contains(repo.Name))
            {
                continue;
            }
            var preview = await PreviewRepositoryAsync(repo.Url, repo.DefaultBranch, requireToc: true);
            if (preview is null || string.IsNullOrWhiteSpace(preview.Title))
            {
                continue;
            }
            if (installedFolderNames.Contains(preview.Title))
            {
                continue;
            }
            result.Add(new AddonInfo
            {
                FolderName = repo.Name,
                Title = preview.Title,
                Description = preview.Description,
                Author = repo.Author,
                Version = preview.Version,
                RepositoryUrl = repo.Url,
                Branch = preview.Branch,
                IsRecommended = true,
                Status = AddonStatus.Ok,
                StatusText = "\uE896"
            });
        }
        AppLogger.Info($"Favorite author URL checked: {authorUrl}. Recommendations={result.Count}");
        return result;
    }

    private async Task<List<FavoriteRepository>> GetFavoriteAuthorRepositoriesAsync(string url)
    {
        var result = new List<FavoriteRepository>();
        if (TryParseGitHub(url, out var owner, out var repo))
        {
            var repoInfo = await GetGitHubRepositoryInfo(owner, repo);
            if (repoInfo is null || !repoInfo.IsLua)
            {
                return result;
            }
            var branch = repoInfo.DefaultBranch;
            result.Add(new FavoriteRepository(owner, repo, $"https://github.com/{owner}/{repo}", branch));
            return result;
        }
        if (!TryParseGitHubOwner(url, out owner))
        {
            return result;
        }

        var json = await TryHttpString($"https://api.github.com/users/{owner}/repos?per_page=100&type=owner&sort=updated");
        if (json is null)
        {
            return result;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("fork", out var fork) && fork.GetBoolean())
                {
                    continue;
                }
                if (item.TryGetProperty("private", out var isPrivate) && isPrivate.GetBoolean())
                {
                    continue;
                }
                var language = item.TryGetProperty("language", out var languageElement) ? languageElement.GetString() ?? "" : "";
                if (!language.Equals("Lua", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var name = item.GetProperty("name").GetString() ?? "";
                var repoOwner = item.GetProperty("owner").GetProperty("login").GetString() ?? owner;
                var htmlUrl = item.GetProperty("html_url").GetString() ?? $"https://github.com/{repoOwner}/{name}";
                var defaultBranch = item.TryGetProperty("default_branch", out var branch) ? branch.GetString() ?? "main" : "main";
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(new FavoriteRepository(repoOwner, name, htmlUrl, defaultBranch));
                }
            }
        }
        catch
        {
            return [];
        }
        return result;
    }

    private async Task<GitHubRepositoryInfo?> GetGitHubRepositoryInfo(string owner, string repo)
    {
        var json = await TryHttpString($"https://api.github.com/repos/{owner}/{repo}");
        if (json is null)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("private", out var isPrivate) && isPrivate.GetBoolean())
            {
                return null;
            }
            var language = doc.RootElement.TryGetProperty("language", out var languageElement) ? languageElement.GetString() ?? "" : "";
            var defaultBranch = doc.RootElement.TryGetProperty("default_branch", out var branch) ? branch.GetString() ?? "main" : "main";
            return new GitHubRepositoryInfo(defaultBranch, language.Equals("Lua", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseRemoteHeads(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && parts[1].StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            {
                result[parts[1]["refs/heads/".Length..]] = parts[0];
            }
        }
        return result;
    }

    private void RenderAddons()
    {
        var filter = SearchBox.Text?.Trim() ?? "";
        var selected = AddonList.SelectedItem as AddonInfo;
        _visibleAddons.Clear();
        var installed = _addons
            .Where(a => MatchesFilter(a, filter))
            .OrderBy(a => string.IsNullOrWhiteSpace(a.Title) ? a.FolderName : a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var recommended = _recommendedAddons.Where(a => MatchesFilter(a, filter)).ToList();

        if (installed.Count > 0 || string.IsNullOrWhiteSpace(filter))
        {
            _visibleAddons.Add(AddonInfo.Section("INSTALLED"));
        }
        foreach (var addon in installed)
        {
            _visibleAddons.Add(addon);
        }

        if (recommended.Count > 0)
        {
            _visibleAddons.Add(AddonInfo.Section("RECOMMENDED"));
            foreach (var group in recommended.GroupBy(a => string.IsNullOrWhiteSpace(a.Author) ? "Unknown Author" : a.Author).OrderBy(g => g.Key))
            {
                _visibleAddons.Add(AddonInfo.AuthorHeader(group.Key));
                foreach (var addon in group.OrderBy(a => a.Title))
                {
                    _visibleAddons.Add(addon);
                }
            }
        }

        if (selected is not null && _visibleAddons.Contains(selected))
        {
            AddonList.SelectedItem = selected;
        }

        var updateCount = _addons.Count(a => a.HasUpdate && !a.IgnoreUpdates);
        UpdateBadge.Visibility = updateCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadgeText.Text = updateCount.ToString();
        UpdateAllButton.Content = updateCount > 0 ? "Update all" : "Everything is up to date.";
        UpdateAllButton.Foreground = updateCount > 0 ? Brush("#D9DD51") : Brush("#9B9A9A");
        UpdateAllButton.IsEnabled = updateCount > 0;
    }

    private static bool MatchesFilter(AddonInfo addon, string filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || addon.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || addon.FolderName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || addon.Author.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyAllAddons()
    {
        foreach (var addon in _addons)
        {
            addon.RefreshBindings();
        }
    }

    private async void AddonAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is AddonInfo addon && addon.HasUpdate)
        {
            await UpdateAddonAsync(addon, addon.RepositoryUrl, addon.Branch, replaceUrl: false);
        }
        else if ((sender as FrameworkElement)?.DataContext is AddonInfo recommended && recommended.IsRecommended)
        {
            await UpdateAddonAsync(null, recommended.RepositoryUrl, recommended.Branch, replaceUrl: true);
        }
    }

    private async void DeleteAddon_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not AddonInfo addon)
        {
            return;
        }

        if (await ShowDeleteConfirmationAsync(addon))
        {
            try
            {
                AppLogger.Info($"Deleting addon. Title={addon.Title}; Path={addon.FolderPath}");
                if (Directory.Exists(addon.FolderPath))
                {
                    DeleteDirectoryForce(addon.FolderPath);
                }
                _addons.Remove(addon);
                RenderAddons();
                await ScanAsync(checkRemote: false);
                AppLogger.Info($"Addon deleted. Title={addon.Title}");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Delete failed. Title={addon.Title}; Path={addon.FolderPath}", ex);
                var hint = "This usually means the addon folder is open in another app, WoW/the launcher is still using a file, or Windows marked one of the files as read-only.";
                SetFooterStatus("Delete failed.", ex.Message);
                await ShowInfoDialogAsync("DELETE FAILED", $"Could not delete {addon.Title}.\n\n{ex.Message}\n\n{hint}");
            }
        }
    }

    private static void DeleteDirectoryForce(string path)
    {
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }

    private Task ShowInfoDialogAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        Overlay.Children.Clear();
        Overlay.Visibility = Visibility.Visible;

        var panel = new Border
        {
            Width = 680,
            Height = 360,
            Background = Brush("#EA0F0D0C"),
            BorderBrush = Brush("#302C29"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });

        var header = new Grid { Margin = new Thickness(24, 0, 14, 0) };
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush("#FF9146"),
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = Brush("#F4F0EA"),
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (_, _) => { CloseOverlay(); tcs.TrySetResult(true); };
        header.Children.Add(close);
        root.Children.Add(header);

        var body = new Border
        {
            BorderBrush = Brush("#24201D"),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(24, 14, 24, 14),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new TextBox
                {
                    Text = message,
                    FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
                    FontSize = 15,
                    Foreground = Brush("#BDB8B2"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            }
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var ok = new Button
        {
            Content = "OK",
            Foreground = Brush("#D9DD51"),
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 28, 0)
        };
        ok.Click += (_, _) => { CloseOverlay(); tcs.TrySetResult(true); };
        Grid.SetRow(ok, 2);
        root.Children.Add(ok);

        panel.Child = root;
        Overlay.Children.Add(panel);
        return tcs.Task;
    }

    private Task<bool> ShowDeleteConfirmationAsync(AddonInfo addon)
    {
        var tcs = new TaskCompletionSource<bool>();
        Overlay.Children.Clear();
        Overlay.Visibility = Visibility.Visible;

        var panel = new Border
        {
            Width = 620,
            Height = 322,
            Background = Brush("#EA0F0D0C"),
            BorderBrush = Brush("#302C29"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(106) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(126) });
        root.RowDefinitions.Add(new RowDefinition());

        var header = new Grid { Margin = new Thickness(24, 0, 14, 0) };
        var titleBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(255, 118, 63), 0),
                new GradientStop(Color.FromRgb(255, 156, 62), 0.55),
                new GradientStop(Color.FromRgb(244, 194, 70), 1)
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = "ARE YOU SURE?",
            Foreground = titleBrush,
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 40,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = Brush("#F4F0EA"),
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (_, _) => { CloseOverlay(); tcs.TrySetResult(false); };
        header.Children.Add(close);
        root.Children.Add(header);

        var body = new Border
        {
            BorderBrush = Brush("#24201D"),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(24, 24, 24, 0)
        };
        var text = new TextBlock
        {
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 21,
            Foreground = Brush("#9B9A9A"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 34
        };
        text.Inlines.Add(new Run("Are you sure you want to delete "));
        text.Inlines.Add(new Run(addon.Title) { Foreground = Brush("#F4F0EA"), FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new Run(" addon?\nThis will delete all files in the addon folder."));
        body.Child = text;
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(24, 0, 28, 0) };
        var delete = new Button
        {
            Foreground = Brush("#FF3939"),
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var deleteContent = new StackPanel { Orientation = Orientation.Horizontal };
        deleteContent.Children.Add(new TextBlock { Text = "\uE74D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 28, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        deleteContent.Children.Add(new TextBlock { Text = "Delete", VerticalAlignment = VerticalAlignment.Center });
        delete.Content = deleteContent;
        delete.Click += (_, _) => { CloseOverlay(); tcs.TrySetResult(true); };
        footer.Children.Add(delete);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        panel.Child = root;
        Overlay.Children.Add(panel);
        return tcs.Task;
    }

    private void AddonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddonList.SelectedItem is AddonInfo addon)
        {
            AddonList.SelectedItem = null;
            if (addon.IsHeader)
            {
                return;
            }
            if (addon.IsRecommended)
            {
                return;
            }
            ShowAddonDetails(addon);
        }
    }

    private Border BuildAddonRow(AddonInfo addon)
    {
        var grid = new Grid { Height = 58, Margin = new Thickness(24, 0, 24, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var icon = new TextBlock
        {
            Text = addon.Status switch
            {
                AddonStatus.Failed => "!",
                AddonStatus.MissingDependencies or AddonStatus.OutOfDate => "!",
                _ => "?"
            },
            Foreground = addon.Status == AddonStatus.Failed ? Brush("#FF3939") : addon.Status == AddonStatus.Ok ? Brush("#9B9A9A") : Brush("#F0CC17"),
            FontWeight = FontWeights.Bold,
            FontSize = 21,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var name = new TextBlock { Text = addon.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var desc = new TextBlock { Text = addon.ShortDescription, FontSize = 17, Foreground = Brush("#979594"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(desc, 2);
        grid.Children.Add(desc);

        var action = new Button
        {
            Content = addon.HasUpdate && !addon.IgnoreUpdates ? "Update" : addon.StatusText,
            Foreground = addon.Status switch
            {
                AddonStatus.Failed => Brush("#FF3939"),
                AddonStatus.MissingDependencies or AddonStatus.OutOfDate => Brush("#F0CC17"),
                _ => addon.HasUpdate ? Brush("#F4F0EA") : Brush("#A8CE3A")
            },
            FontWeight = addon.HasUpdate ? FontWeights.Bold : FontWeights.Normal,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        action.Click += async (s, e) =>
        {
            e.Handled = true;
            if (addon.HasUpdate)
            {
                await UpdateAddonAsync(addon, addon.RepositoryUrl, addon.Branch, replaceUrl: false);
            }
        };
        Grid.SetColumn(action, 3);
        grid.Children.Add(action);

        var delete = new Button { Content = "Delete", Foreground = Brush("#B32929"), FontSize = 14 };
        delete.Click += async (s, e) =>
        {
            e.Handled = true;
            if (MessageBox.Show($"Delete addon folder '{addon.FolderName}'?", "Delete addon", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                Directory.Delete(addon.FolderPath, recursive: true);
                await ScanAsync(checkRemote: false);
            }
        };
        Grid.SetColumn(delete, 4);
        grid.Children.Add(delete);

        var border = new Border { Child = grid, BorderBrush = Brush("#181615"), BorderThickness = new Thickness(0, 0, 0, 1), Cursor = System.Windows.Input.Cursors.Hand };
        border.MouseLeftButtonUp += (_, _) => ShowAddonDetails(addon);
        return border;
    }

    private void ShowAddonDetails(AddonInfo addon)
    {
        EnsureReadmeLoaded(addon);
        var modal = CreateModal(addon.Title, width: 930, height: 600);
        var body = (Grid)modal.Tag;
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(76) });

        var content = new Grid { Margin = new Thickness(20) };
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });

        var stack = new StackPanel();
        if (addon.Status == AddonStatus.Failed)
        {
            stack.Children.Add(Notice("Failed to verify", "#FF3939"));
        }
        if (!string.IsNullOrWhiteSpace(addon.Problem))
        {
            stack.Children.Add(Notice(addon.Problem, "#F0CC17"));
        }
        stack.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(addon.Readme) ? addon.Description : SimplifyMarkdown(addon.Readme), FontSize = 18, TextWrapping = TextWrapping.Wrap, LineHeight = 28 });
        content.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var meta = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        meta.Children.Add(LinkButton($"Files: Open folder", () => OpenPath(addon.FolderPath)));
        if (!string.IsNullOrWhiteSpace(addon.RepositoryUrl))
        {
            meta.Children.Add(LinkButton("Source: Open URL", () => OpenPath(addon.RepositoryUrl)));
        }
        meta.Children.Add(new TextBlock { Text = $"Contributions: {addon.Author}", FontSize = 17, Foreground = Brush("#B8B2AC"), Margin = new Thickness(0, 10, 0, 0) });
        meta.Children.Add(new TextBlock { Text = $"Addon version: {addon.Version}", FontSize = 17, Foreground = Brush("#B8B2AC"), Margin = new Thickness(0, 10, 0, 0) });
        meta.Children.Add(new TextBlock { Text = "Dependencies:", FontSize = 17, Foreground = Brush("#9B9A9A"), Margin = new Thickness(0, 16, 0, 8) });
        foreach (var dep in addon.Dependencies)
        {
            var mark = dep.IsInstalled ? "✓" : "x";
            meta.Children.Add(new TextBlock { Text = $"{mark} {dep.Name}{(dep.Optional ? " (optional)" : "")}", Foreground = dep.IsInstalled ? Brush("#62C75D") : Brush("#FF3939"), FontSize = 17, Margin = new Thickness(12, 2, 0, 0) });
        }
        var metaBorder = new Border { Child = meta, BorderBrush = Brush("#2B2928"), BorderThickness = new Thickness(1), Padding = new Thickness(18) };
        Grid.SetColumn(metaBorder, 1);
        content.Children.Add(metaBorder);
        body.Children.Add(content);

        var bottom = new Grid { Margin = new Thickness(20, 0, 20, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        var changeUrl = new Button { Content = "Change URL", HorizontalAlignment = HorizontalAlignment.Left };
        changeUrl.Click += (_, _) => ShowRepoDialog("Change URL", addon);
        bottom.Children.Add(changeUrl);

        var branches = new ComboBox
        {
            ItemsSource = addon.Branches,
            SelectedItem = addon.Branch,
            Margin = new Thickness(8, 16, 8, 16),
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)FindResource("DarkComboBox")
        };
        branches.SelectionChanged += async (_, _) =>
        {
            if (branches.SelectedItem is string branch && !branch.Equals(addon.Branch, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(addon.RepositoryUrl))
            {
                await UpdateAddonAsync(addon, addon.RepositoryUrl, branch, replaceUrl: false);
                CloseOverlay();
            }
        };
        Grid.SetColumn(branches, 1);
        bottom.Children.Add(branches);

        var ignore = new CheckBox { Content = "Ignore updates", IsChecked = addon.IgnoreUpdates, VerticalAlignment = VerticalAlignment.Center };
        ignore.Checked += (_, _) => SetIgnored(addon, true);
        ignore.Unchecked += (_, _) => SetIgnored(addon, false);
        Grid.SetColumn(ignore, 2);
        bottom.Children.Add(ignore);
        Grid.SetRow(bottom, 1);
        body.Children.Add(bottom);
    }

    private static void EnsureReadmeLoaded(AddonInfo addon)
    {
        if (!string.IsNullOrWhiteSpace(addon.Readme))
        {
            return;
        }

        var readme = Directory.GetFiles(addon.FolderPath, "README*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (readme is not null)
        {
            addon.Readme = File.ReadAllText(readme);
        }
    }

    private void ShowRepoDialog(string title, AddonInfo? existing)
    {
        var modal = CreateModal(title, width: 760, height: 430);
        var body = (Grid)modal.Tag;
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(112) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });

        var info = new TextBlock
        {
            Text = "Paste a Git repository URL to preview the addon before installing it.",
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 13,
            Foreground = Brush("#9E9A96"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(26, 4, 26, 0),
            LineHeight = 22
        };
        body.Children.Add(new ScrollViewer { Content = info, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var inputGrid = new Grid { Margin = new Thickness(26, 10, 26, 0) };
        inputGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        inputGrid.RowDefinitions.Add(new RowDefinition());
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        var inputBorder = new Border
        {
            Background = Brush("#181210"),
            BorderBrush = Brush("#59504A"),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        var urlBox = new TextBox
        {
            Background = Brushes.Transparent,
            Foreground = Brush("#F4F0EA"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 7, 8, 7),
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 13,
            CaretBrush = Brush("#FF9146"),
            Text = ""
        };
        inputBorder.Child = urlBox;
        inputGrid.Children.Add(inputBorder);
        var clear = new Button
        {
            Content = "x",
            FontSize = 15,
            Foreground = Brush("#8F8B87"),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        clear.Click += (_, _) => urlBox.Clear();
        Grid.SetColumn(clear, 1);
        inputGrid.Children.Add(clear);
        var wiki = new TextBlock
        {
            Text = "Community addon list on Project Epoch Wiki",
            Foreground = Brush("#8E8B87"),
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        wiki.MouseLeftButtonUp += (_, _) => OpenPath("https://project-epoch-wow.fandom.com/wiki/AddOns");
        Grid.SetRow(wiki, 1);
        Grid.SetColumnSpan(wiki, 2);
        inputGrid.Children.Add(wiki);
        Grid.SetRow(inputGrid, 1);
        body.Children.Add(inputGrid);

        var install = new Button
        {
            Content = existing is null ? "INSTALL" : "CHANGE URL",
            IsEnabled = false,
            Foreground = Brush("#D9DD51"),
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 26, 0)
        };
        var status = new TextBlock
        {
            Text = "",
            Foreground = Brush("#8E8B87"),
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 132, 0)
        };
        var footer = new Grid();
        footer.Children.Add(status);
        footer.Children.Add(install);
        Grid.SetRow(footer, 2);
        body.Children.Add(footer);

        RepoPreview? preview = null;
        System.Windows.Threading.DispatcherTimer? debounce = null;
        urlBox.TextChanged += (_, _) =>
        {
            debounce?.Stop();
            debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            debounce.Tick += async (_, _) =>
            {
                debounce!.Stop();
                install.IsEnabled = false;
                status.Text = "Checking repository...";
                preview = await PreviewRepositoryAsync(urlBox.Text.Trim());
                if (preview is null)
                {
                    info.Text = "Paste a Git repository URL to preview the addon before installing it.";
                    status.Text = "No valid git repository found";
                    return;
                }

                info.Text = $"{preview.Title}\n\n{preview.Description}\n\nSource: {preview.Url}\nAuthor: {preview.Author}\nVersion: {preview.Version}";
                var same = existing is not null && NormalizeRepo(existing.RepositoryUrl) == NormalizeRepo(preview.Url);
                status.Text = same ? "Not the same addon as previous installation" : "Ready to install";
                install.IsEnabled = !same;
            };
            debounce.Start();
        };

        install.Click += async (_, _) =>
        {
            if (preview is not null)
            {
                await UpdateAddonAsync(existing, preview.Url, preview.Branch, replaceUrl: true);
                CloseOverlay();
            }
        };
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var modal = CreateModal("Settings", width: 930, height: 650);
        var body = (Grid)modal.Tag;
        body.RowDefinitions.Add(new RowDefinition());
        var root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var stack = new StackPanel { Margin = new Thickness(24) };
        root.Content = stack;
        body.Children.Add(root);

        stack.Children.Add(SectionTitle("Install Location:"));
        stack.Children.Add(PathEditor(_settings.InstallLocation, "Select folder", isFolder: true, v => { _settings.InstallLocation = v; SaveSettings(); RunLoggedAsync(ScanAsync(checkRemote: false), "Settings install location scan"); },
            "Please add the path until the folder where you see the subfolder \"Interface\" e.g. \"D:\\spiele\\Ascension-WOW\\Launcher\\resources\\epoch-live\\\""));

        stack.Children.Add(SectionTitle("Game Start:"));
        var radios = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var mode in new[] { "Launcher", "WOW", "Choose always" })
        {
            var radio = new RadioButton { Content = mode, IsChecked = _settings.GameStart == mode, GroupName = "GameStart" };
            radio.Checked += (_, _) => { _settings.GameStart = mode; SaveSettings(); };
            radios.Children.Add(radio);
        }
        stack.Children.Add(radios);
        stack.Children.Add(PathEditor(_settings.LauncherExecutable, "Select file", isFolder: false, v => { _settings.LauncherExecutable = v; SaveSettings(); }, "Launcher executable"));
        stack.Children.Add(PathEditor(_settings.WowExecutable, "Select file", isFolder: false, v => { _settings.WowExecutable = v; SaveSettings(); }, "WOW executable"));

        stack.Children.Add(SectionTitle("Favorite Authors"));
        stack.Children.Add(BuildFavoriteAuthorsSettings());

        stack.Children.Add(SectionTitle("Troubleshooting:"));
        stack.Children.Add(LinkButton("Open log file", () =>
        {
            var logs = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(logs);
            OpenPath(logs);
        }));

        stack.Children.Add(SectionTitle("General Settings:"));
        var clean = new CheckBox { Content = "Clean WDB on each launch", IsChecked = _settings.CleanWdbOnLaunch };
        clean.Checked += (_, _) => { _settings.CleanWdbOnLaunch = true; SaveSettings(); };
        clean.Unchecked += (_, _) => { _settings.CleanWdbOnLaunch = false; SaveSettings(); };
        stack.Children.Add(clean);
    }

    private FrameworkElement BuildFavoriteAuthorsSettings()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 22) };
        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition());
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var input = new TextBox { Style = (Style)FindResource("InputBox"), FontSize = 13 };
        var add = new Button
        {
            Content = "+",
            Foreground = Brush("#A8CE3A"),
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(add, 1);
        inputRow.Children.Add(input);
        inputRow.Children.Add(add);
        panel.Children.Add(inputRow);

        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(list);

        void Render()
        {
            list.Children.Clear();
            foreach (var url in _settings.FavoriteAuthorUrls.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                row.Children.Add(new TextBlock
                {
                    Text = url,
                    Foreground = Brush("#BDB8B2"),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                var remove = new Button
                {
                    Content = "-",
                    Foreground = Brush("#FF3939"),
                    FontSize = 24,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                remove.Click += (_, _) =>
                {
                    _settings.FavoriteAuthorUrls.RemoveAll(u => u.Equals(url, StringComparison.OrdinalIgnoreCase));
                    SaveSettings();
                    Render();
                    RunLoggedAsync(ScanAsync(checkRemote: true), "Favorite authors scan after remove");
                };
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                list.Children.Add(row);
            }
        }

        add.Click += (_, _) =>
        {
            var url = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(url) || _settings.FavoriteAuthorUrls.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            _settings.FavoriteAuthorUrls.Add(url);
            SaveSettings();
            input.Clear();
            Render();
            RunLoggedAsync(ScanAsync(checkRemote: true), "Favorite authors scan after add");
        };

        Render();
        panel.Children.Add(new TextBlock
        {
            Text = "Add GitHub author, organization, or repository URLs. Public repositories with addon metadata will appear as recommendations.",
            Foreground = Brush("#8E8B87"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        return panel;
    }

    private FrameworkElement PathEditor(string current, string chooserText, bool isFolder, Action<string> apply, string help)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 22) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        var box = new TextBox { Style = (Style)FindResource("InputBox"), Text = current, IsReadOnly = true };
        var button = new Button { Content = "✎  Change" };
        Grid.SetColumn(button, 1);
        grid.Children.Add(box);
        grid.Children.Add(button);
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock { Text = help, Foreground = Brush("#9B9A9A"), FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

        button.Click += (_, _) =>
        {
            if (isFolder)
            {
                using var dialog = new Forms.FolderBrowserDialog { SelectedPath = box.Text };
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    box.Text = dialog.SelectedPath;
                    apply(box.Text);
                }
            }
            else
            {
                var dialog = new OpenFileDialog { Filter = "Executables|*.exe|All files|*.*", FileName = box.Text };
                if (dialog.ShowDialog() == true)
                {
                    box.Text = dialog.FileName;
                    apply(box.Text);
                }
            }
        };
        return panel;
    }

    private async Task<RepoPreview?> PreviewRepositoryAsync(string url, string? branchHint = null, bool requireToc = false)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var branch = string.IsNullOrWhiteSpace(branchHint) ? "" : branchHint;
        if (string.IsNullOrWhiteSpace(branch))
        {
            var heads = await RunGitAsync(_settings.InstallLocation, $"ls-remote --heads {url}");
            branch = heads.Split("refs/heads/").Skip(1).Select(s => s.Split(['\r', '\n']).First().Trim()).FirstOrDefault() ?? "main";
            if (string.IsNullOrWhiteSpace(heads))
            {
                return null;
            }
        }

        var preview = new RepoPreview { Url = url, Branch = branch };
        if (TryParseGitHub(url, out var owner, out var repo))
        {
            var readme = await TryHttpString($"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/README.md");
            var toc = await FindRemoteToc(owner, repo, branch);
            if (requireToc && toc is null)
            {
                return null;
            }
            preview.Title = repo;
            preview.Description = FirstMarkdownParagraph(readme) ?? "Repository is available.";
            if (toc is not null)
            {
                var meta = ParseTocText(toc);
                preview.Title = CleanTitle(meta.GetValueOrDefault("Title") ?? preview.Title);
                preview.Description = meta.GetValueOrDefault("Notes") ?? preview.Description;
                preview.Author = meta.GetValueOrDefault("Author") ?? "";
                preview.Version = meta.GetValueOrDefault("Version") ?? "";
            }
        }
        return preview;
    }

    private async Task<string?> FindRemoteToc(string owner, string repo, string branch)
    {
        var api = $"https://api.github.com/repos/{owner}/{repo}/contents?ref={Uri.EscapeDataString(branch)}";
        var json = await TryHttpString(api);
        if (json is null)
        {
            return null;
        }
        var match = Regex.Match(json, @"""download_url"":\s*""([^""]+\.toc)""", RegexOptions.IgnoreCase);
        return match.Success ? await TryHttpString(match.Groups[1].Value.Replace("\\/", "/")) : null;
    }

    private async Task UpdateAddonAsync(AddonInfo? addon, string? repoUrl, string? branch, bool replaceUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            return;
        }

        branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch;
        AppLogger.Info($"Update/install started. Repo={repoUrl}; Branch={branch}; ExistingAddon={addon?.FolderName ?? "<new>"}");
        SetFooterStatus($"Downloading {repoUrl}...");
        if (!TryParseGitHub(repoUrl, out var owner, out var repo))
        {
            MessageBox.Show("Only GitHub repositories can be downloaded automatically in this version.", "Unsupported repository", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targetName = addon?.FolderName ?? repo;
        var addonRoot = Path.Combine(_settings.InstallLocation, "Interface", "AddOns");
        Directory.CreateDirectory(addonRoot);
        var target = Path.Combine(addonRoot, targetName);
        var temp = Path.Combine(Path.GetTempPath(), "EpochAddonUpdater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var zipPath = Path.Combine(temp, "repo.zip");
            var zipBytes = await _http.GetByteArrayAsync($"https://codeload.github.com/{owner}/{repo}/zip/refs/heads/{Uri.EscapeDataString(branch)}");
            await File.WriteAllBytesAsync(zipPath, zipBytes);
            ZipFile.ExtractToDirectory(zipPath, temp);
            var root = Directory.GetDirectories(temp).First(d => Path.GetFileName(d).StartsWith(repo, StringComparison.OrdinalIgnoreCase));
            var source = Directory.GetFiles(root, "*.toc", SearchOption.TopDirectoryOnly).Any()
                ? root
                : Directory.GetDirectories(root).FirstOrDefault(d => Directory.GetFiles(d, "*.toc", SearchOption.TopDirectoryOnly).Any()) ?? root;

            if (Directory.Exists(target))
            {
                DeleteDirectoryForce(target);
            }
            CopyDirectory(source, target);
            var installedCommit = await RunGitAsync(_settings.InstallLocation, $"ls-remote {repoUrl} refs/heads/{branch}");
            SaveRepoMetadata(target, repoUrl, branch, installedCommit.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
            SetFooterStatus("Addon updated.");
            await ScanAsync(checkRemote: true);
            AppLogger.Info($"Update/install finished. Repo={repoUrl}; Target={target}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Update/install failed. Repo={repoUrl}; Target={target}", ex);
            MessageBox.Show(ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SetFooterStatus("Update failed.");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                DeleteDirectoryForce(temp);
            }
        }
    }

    private static void LoadRepoMetadata(string path, AddonInfo addon)
    {
        var metadataPath = Path.Combine(path, ".epoch-addon-updater.json");
        if (!File.Exists(metadataPath))
        {
            return;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<RepoInstallMetadata>(File.ReadAllText(metadataPath));
            if (metadata is null)
            {
                return;
            }
            addon.RepositoryUrl = string.IsNullOrWhiteSpace(addon.RepositoryUrl) ? metadata.RepoUrl : addon.RepositoryUrl;
            addon.Branch = string.IsNullOrWhiteSpace(addon.Branch) ? metadata.Branch : addon.Branch;
            addon.LocalCommit = string.IsNullOrWhiteSpace(addon.LocalCommit) ? metadata.Commit : addon.LocalCommit;
        }
        catch
        {
            AppLogger.Warn($"Failed to read repo metadata: {metadataPath}");
            // Bad metadata should not make the addon fail verification.
        }
    }

    private static void SaveRepoMetadata(string target, string repoUrl, string branch, string commit)
    {
        File.WriteAllText(Path.Combine(target, ".epoch-addon-updater.json"), JsonSerializer.Serialize(new RepoInstallMetadata
        {
            RepoUrl = repoUrl,
            Branch = branch,
            Commit = commit,
            InstalledAt = DateTimeOffset.Now
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await ScanAsync(checkRemote: true);
    private void AddAddon_Click(object sender, RoutedEventArgs e) => ShowRepoDialog("Add New Addon", null);
    private async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var addon in _addons.Where(a => a.HasUpdate && !a.IgnoreUpdates).ToList())
        {
            await UpdateAddonAsync(addon, addon.RepositoryUrl, addon.Branch, replaceUrl: false);
        }
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        ApplyLaunchState();

        var exe = ResolveStartExecutable(out var targetKind);
        if (string.IsNullOrWhiteSpace(exe))
        {
            return;
        }

        var launcherStart = targetKind == LaunchTargetKind.Launcher;
        if (_launchTargetRunning && !_scanInProgress)
        {
            if (launcherStart)
            {
                await ShowLaunchInstanceDialogAsync(allowContinue: false);
                return;
            }

            if (!await ShowLaunchInstanceDialogAsync(allowContinue: true))
            {
                return;
            }
        }

        if (_settings.CleanWdbOnLaunch && !_launchTargetRunning)
        {
            var wdb = Path.Combine(_settings.InstallLocation, "WDB");
            if (Directory.Exists(wdb))
            {
                try
                {
                    Directory.Delete(wdb, recursive: true);
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Failed to clean WDB before launch. Path={wdb}", ex);
                    await ShowInfoDialogAsync("WDB CLEANUP FAILED", $"Could not delete the WDB folder before launch.\n\n{ex.Message}");
                    return;
                }
            }
        }
        else if (_settings.CleanWdbOnLaunch && _launchTargetRunning)
        {
            AppLogger.Warn("Skipped WDB cleanup because another WoW or launcher instance is running.");
        }

        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            await Task.Delay(500);
            ApplyLaunchState();
        }
        else
        {
            await ShowInfoDialogAsync("START OPTION", "Please configure a valid executable in Settings.");
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        ApplyLaunchState();
    }

    private void VersionButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private string ResolveStartExecutable(out LaunchTargetKind targetKind)
    {
        targetKind = _settings.GameStart switch
        {
            "WOW" => LaunchTargetKind.Wow,
            "Choose always" => LaunchTargetKind.Unknown,
            _ => LaunchTargetKind.Launcher
        };

        if (_settings.GameStart == "WOW")
        {
            return _settings.WowExecutable;
        }

        if (_settings.GameStart == "Choose always")
        {
            return ChooseStartExecutable(out targetKind);
        }

        return _settings.LauncherExecutable;
    }

    private string ChooseStartExecutable(out LaunchTargetKind targetKind)
    {
        var result = MessageBox.Show("Choose if you want to start the Launcher or WOW directly.\n\nYes = Launcher\nNo = WOW", "Start option", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        targetKind = result == MessageBoxResult.Yes ? LaunchTargetKind.Launcher : result == MessageBoxResult.No ? LaunchTargetKind.Wow : LaunchTargetKind.Unknown;
        return result == MessageBoxResult.Yes ? _settings.LauncherExecutable : result == MessageBoxResult.No ? _settings.WowExecutable : "";
    }

    private void StartProcessWatcher()
    {
        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _processTimer.Tick += (_, _) => ApplyLaunchState();
        _processTimer.Start();
        ApplyLaunchState();
    }

    private void SetFooterStatus(string title, string subtitle = "")
    {
        _normalFooterTitle = title;
        _normalFooterSubtitle = subtitle;
        ApplyLaunchState();
    }

    private void ApplyLaunchState()
    {
        (_launcherRunning, _wowRunning) = GetLaunchProcessState();
        _launchTargetRunning = _launcherRunning || _wowRunning;

        if (_launchTargetRunning && !_scanInProgress)
        {
            FooterStatus.Text = GetRunningInstanceTitle();
            FooterSubStatus.Text = "Please close the game and retry if you want to update.";
            FooterSubStatus.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Visible;
            PlayButton.Background = Brush("#77412B");
            PlayButton.Foreground = Brush("#FFB15F");
            return;
        }

        FooterStatus.Text = _normalFooterTitle;
        FooterSubStatus.Text = _normalFooterSubtitle;
        FooterSubStatus.Visibility = string.IsNullOrWhiteSpace(_normalFooterSubtitle) ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
        PlayButton.Background = Brush("#3E6E20");
        PlayButton.Foreground = Brush("#D8F06E");
    }

    private string GetRunningInstanceTitle()
    {
        if (_launcherRunning && _wowRunning)
        {
            return "Another instance of WoW or the launcher is running.";
        }

        return _launcherRunning
            ? "Another launcher instance is running."
            : "Another instance of WoW is running.";
    }

    private (bool launcherRunning, bool wowRunning) GetLaunchProcessState()
    {
        var launcherNames = CreateProcessNames(_settings.LauncherExecutable, "Ascension Launcher", "Ascension Launcher.exe");
        var wowNames = CreateProcessNames(_settings.WowExecutable, "WoW", "WoW.exe");
        var launcherRunning = false;
        var wowRunning = false;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var processName = process.ProcessName;
                var processExe = processName + ".exe";
                if (launcherNames.Contains(processName) || launcherNames.Contains(processExe))
                {
                    launcherRunning = true;
                }
                if (wowNames.Contains(processName) || wowNames.Contains(processExe))
                {
                    wowRunning = true;
                }
                if (launcherRunning && wowRunning)
                {
                    return (true, true);
                }
            }
            catch
            {
                // Some processes may exit while enumerating.
            }
            finally
            {
                process.Dispose();
            }
        }

        return (launcherRunning, wowRunning);
    }

    private static HashSet<string> CreateProcessNames(string? executable, params string[] defaults)
    {
        var names = new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase);
        AddProcessName(names, executable);
        return names;
    }

    private Task<bool> ShowLaunchInstanceDialogAsync(bool allowContinue)
    {
        var tcs = new TaskCompletionSource<bool>();
        Overlay.Children.Clear();
        Overlay.Visibility = Visibility.Visible;

        var panel = new Border
        {
            Width = 620,
            Height = allowContinue ? 350 : 322,
            Background = Brush("#EA0F0D0C"),
            BorderBrush = Brush("#302C29"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(106) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(allowContinue ? 154 : 126) });
        root.RowDefinitions.Add(new RowDefinition());

        var header = new Grid { Margin = new Thickness(24, 0, 14, 0) };
        var titleBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(255, 118, 63), 0),
                new GradientStop(Color.FromRgb(255, 156, 62), 0.55),
                new GradientStop(Color.FromRgb(244, 194, 70), 1)
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = "INSTANCE RUNNING",
            Foreground = titleBrush,
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 36,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = Brush("#F4F0EA"),
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (_, _) => { CloseOverlay(); tcs.TrySetResult(false); };
        header.Children.Add(close);
        root.Children.Add(header);

        var body = new Border
        {
            BorderBrush = Brush("#24201D"),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(24, 24, 24, 0)
        };
        var text = new TextBlock
        {
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 21,
            Foreground = Brush("#9B9A9A"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 32
        };
        text.Inlines.Add(new Run(GetRunningInstanceTitle()) { Foreground = Brush("#F4F0EA"), FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new Run("\n"));
        text.Inlines.Add(allowContinue
            ? new Run("You can still start another WoW instance if you want to.")
            : new Run("Please close the running game or launcher before starting the launcher again."));
        body.Child = text;
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(24, 0, 28, 0) };
        var cancel = new Button
        {
            Content = allowContinue ? "Cancel" : "OK",
            Foreground = Brush("#BDB8B2"),
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        cancel.Click += (_, _) => { CloseOverlay(); tcs.TrySetResult(false); };
        footer.Children.Add(cancel);

        if (allowContinue)
        {
            var launch = new Button
            {
                Foreground = Brush("#FFB15F"),
                FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 120, 0)
            };
            var launchContent = new StackPanel { Orientation = Orientation.Horizontal };
            launchContent.Children.Add(new TextBlock { Text = "\uE768", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 24, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            launchContent.Children.Add(new TextBlock { Text = "Launch anyway", VerticalAlignment = VerticalAlignment.Center });
            launch.Content = launchContent;
            launch.Click += (_, _) => { CloseOverlay(); tcs.TrySetResult(true); };
            footer.Children.Add(launch);
        }

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        panel.Child = root;
        Overlay.Children.Add(panel);
        return tcs.Task;
    }

    private enum LaunchTargetKind
    {
        Unknown,
        Launcher,
        Wow
    }

    private static void AddProcessName(HashSet<string> names, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var fileName = Path.GetFileName(executable);
        var withoutExtension = Path.GetFileNameWithoutExtension(executable);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            names.Add(fileName);
        }
        if (!string.IsNullOrWhiteSpace(withoutExtension))
        {
            names.Add(withoutExtension);
        }
    }
    private static string GetAppVersion()
    {
        return Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "1.0.0";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderAddons();

    private void SetIgnored(AddonInfo addon, bool ignored)
    {
        addon.IgnoreUpdates = ignored;
        if (ignored && !_settings.IgnoredAddons.Contains(addon.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            _settings.IgnoredAddons.Add(addon.FolderName);
        }
        if (!ignored)
        {
            _settings.IgnoredAddons.RemoveAll(n => n.Equals(addon.FolderName, StringComparison.OrdinalIgnoreCase));
        }
        SaveSettings();
        RenderAddons();
    }

    private Border CreateModal(string title, double width, double height)
    {
        Overlay.Children.Clear();
        Overlay.Visibility = Visibility.Visible;
        var panel = new Border { Width = width, Height = height, Background = Brush("#E20F0D0C"), BorderBrush = Brush("#302C29"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Margin = new Thickness(26, 0, 12, 0) };
        var titleBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(255, 117, 64), 0),
                new GradientStop(Color.FromRgb(255, 146, 70), 0.45),
                new GradientStop(Color.FromRgb(245, 196, 70), 1)
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Foreground = titleBrush,
            FontFamily = new FontFamily("Roboto Condensed, Segoe UI"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Foreground = Brush("#D8D2CB"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (_, _) => CloseOverlay();
        header.Children.Add(close);
        root.Children.Add(header);
        var body = new Grid();
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        panel.Child = root;
        panel.Tag = body;
        Overlay.Children.Add(panel);
        return panel;
    }

    private void CloseOverlay()
    {
        Overlay.Visibility = Visibility.Collapsed;
        Overlay.Children.Clear();
    }

    private static TextBlock SectionTitle(string text) => new() { Text = text, FontSize = 24, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 20, 0, 6) };
    private static Border Notice(string text, string color) => new()
    {
        BorderBrush = Brush(color),
        BorderThickness = new Thickness(1),
        Background = Brush(color, 0.12),
        Padding = new Thickness(12),
        Margin = new Thickness(0, 0, 0, 12),
        Child = new TextBlock { Text = text, Foreground = Brush(color), FontSize = 17, TextWrapping = TextWrapping.Wrap }
    };

    private static Button LinkButton(string text, Action action)
    {
        var button = new Button { Content = text + " ↗", FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => action();
        return button;
    }

    private static Brush Brush(string color, double opacity = 1)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Opacity = opacity;
        return brush;
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string TryRunGit(string workingDirectory, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            if (process is null) return "";
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);
            return process.ExitCode == 0 ? output : "";
        }
        catch
        {
            return "";
        }
    }

    private static async Task<string> RunGitAsync(string workingDirectory, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            if (process is null) return "";
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(3500)) != exitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                AppLogger.Warn($"Git timed out. WorkingDirectory={workingDirectory}; Args={args}");
                return "";
            }
            var output = await outputTask;
            if (process.ExitCode != 0)
            {
                AppLogger.Warn($"Git failed. ExitCode={process.ExitCode}; WorkingDirectory={workingDirectory}; Args={args}");
            }
            return process.ExitCode == 0 ? output.Trim() : "";
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Git execution failed. WorkingDirectory={workingDirectory}; Args={args}", ex);
            return "";
        }
    }

    private async Task<string?> TryHttpString(string url)
    {
        try { return await _http.GetStringAsync(url); }
        catch (Exception ex)
        {
            AppLogger.Warn($"HTTP GET failed. Url={url}; Error={ex.Message}");
            return null;
        }
    }

    private static bool TryParseGitHub(string url, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        var match = Regex.Match(url, @"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)(?:\.git)?", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        owner = match.Groups["owner"].Value;
        repo = match.Groups["repo"].Value;
        return true;
    }

    private static bool TryParseGitHubOwner(string url, out string owner)
    {
        owner = "";
        var match = Regex.Match(url.Trim(), @"github\.com[:/](?<owner>[^/\s]+)(?:/)?$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(url.Trim(), @"^(?<owner>[A-Za-z0-9_.-]+)$", RegexOptions.IgnoreCase);
        }
        if (!match.Success)
        {
            return false;
        }
        owner = match.Groups["owner"].Value.Trim();
        return !string.IsNullOrWhiteSpace(owner);
    }

    private static string NormalizeRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        return url.Trim().TrimEnd('/').Replace(".git", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    }

    private static string CleanTitle(string text) => Regex.Replace(text, @"\|c[0-9A-Fa-f]{8}|\|r|\|n", "").Trim();
    private static string SimplifyMarkdown(string text) => Regex.Replace(text, @"[#*_>`!\[\]\(\)]", "").Trim();
    private static string? FirstMarkdownParagraph(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries).SkipWhile(p => p.TrimStart().StartsWith("#")).FirstOrDefault()?.Trim();
    private static string? ReadFirstReadmeParagraph(string path)
    {
        var readme = Directory.GetFiles(path, "README*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        return readme is null ? null : FirstMarkdownParagraph(File.ReadAllText(readme));
    }

    private static Dictionary<string, string> ParseTocText(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^##\s*([^:]+):\s*(.*)$");
            if (match.Success) result[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();
        }
        return result;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, target));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target), overwrite: true);
        }
    }
}

public enum AddonStatus
{
    Ok,
    Failed,
    OutOfDate,
    MissingDependencies
}

public sealed class AddonInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string RowKind { get; set; } = "Addon";
    public string HeaderText { get; set; } = "";
    public bool IsHeader => RowKind != "Addon";
    public bool IsRecommended { get; set; }
    public string FolderName { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string TocPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Readme { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Interface { get; set; } = "";
    public string Problem { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string Branch { get; set; } = "";
    public string LocalCommit { get; set; } = "";
    public string RemoteCommit { get; set; } = "";
    public bool HasUpdate { get; set; }
    public bool IgnoreUpdates { get; set; }
    public AddonStatus Status { get; set; } = AddonStatus.Ok;
    public string StatusText { get; set; } = "";
    public List<string> Branches { get; set; } = [];
    public List<DependencyInfo> Dependencies { get; } = [];
    public string ShortDescription => string.IsNullOrWhiteSpace(Description) ? "" : Regex.Replace(Clean(Description), @"\s+", " ");
    public string IconText => IsRecommended ? "+" : Status is AddonStatus.Failed or AddonStatus.MissingDependencies or AddonStatus.OutOfDate ? "!" : "?";
    public Brush IconBrush => Status == AddonStatus.Failed ? Solid("#FF3939") : Status == AddonStatus.Ok ? Solid("#9B9A9A") : Solid("#F0CC17");
    public string ActionText => IsRecommended ? "\uE896" : HasUpdate && !IgnoreUpdates ? "Update" : StatusText;
    public Brush ActionBrush => Status switch
    {
        AddonStatus.Failed => Solid("#FF3939"),
        AddonStatus.MissingDependencies or AddonStatus.OutOfDate => Solid("#F0CC17"),
        _ => IsRecommended ? Solid("#D9DD51") : HasUpdate ? Solid("#F4F0EA") : Solid("#A8CE3A")
    };
    public FontWeight ActionWeight => HasUpdate || IsRecommended ? FontWeights.Bold : FontWeights.Normal;
    public string ActionFontFamily => IsRecommended ? "Segoe MDL2 Assets" : "Segoe UI";

    public static AddonInfo Section(string text) => new() { RowKind = "SectionHeader", HeaderText = text, Title = text };
    public static AddonInfo AuthorHeader(string text) => new() { RowKind = "AuthorHeader", HeaderText = text, Title = text };

    public void RefreshBindings()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static string Clean(string text) => Regex.Replace(text, @"\|c[0-9A-Fa-f]{8}|\|r|\|n", " ").Trim();
    private static Brush Solid(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
}

public sealed class DependencyInfo
{
    public string Name { get; set; } = "";
    public bool Optional { get; set; }
    public bool IsInstalled { get; set; }
}

public sealed class AppSettings
{
    public string InstallLocation { get; set; } = "";
    public string GameStart { get; set; } = "Choose always";
    public string LauncherExecutable { get; set; } = "";
    public string WowExecutable { get; set; } = "";
    public bool CleanWdbOnLaunch { get; set; }
    public List<string> IgnoredAddons { get; set; } = [];
    public List<string> FavoriteAuthorUrls { get; set; } = ["https://github.com/Fragglechen"];
}

public sealed class RepoPreview
{
    public string Url { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class RepoInstallMetadata
{
    public string RepoUrl { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Commit { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
}

public sealed record FavoriteRepository(string Author, string Name, string Url, string DefaultBranch);

public sealed record GitHubRepositoryInfo(string DefaultBranch, bool IsLua);
