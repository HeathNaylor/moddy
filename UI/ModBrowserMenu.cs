#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Moddy.Data;
using Moddy.Services;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace Moddy.UI
{

    public class ModBrowserMenu : IClickableMenu
    {
        // Data
        private List<CatalogEntry> _catalog = new();
        private List<CatalogEntry> _filteredCatalog = new();
        private Dictionary<string, ScannedMod> _installedMods = new();
        private ModRegistry _registry = null!;
        private readonly Dictionary<string, GitHubRelease?> _latestReleases = new();
        private readonly Dictionary<int, NexusModInfo?> _nexusModDetails = new();
        private readonly Dictionary<int, NexusFileInfo[]?> _nexusModFiles = new();
        private readonly ConcurrentQueue<Action> _gameThreadActions = new();

        // UI state
        private SearchBar _searchBar = null!;
        private int _currentPage;
        private int _selectedIndex = -1;
        private int _hoveredIndex = -1;
        private bool _isBusy;
        private string _statusMessage = "";
        private DateTime _statusExpiry = DateTime.MinValue;
        private ModSource? _sourceFilter;
        private int _refreshTicks;
        private int _detailButtonY;

        // Source filter tab labels
        private static readonly string[] TabLabels = { "All", "GitHub", "Nexus" };

        // Layout
        private Rectangle _panelBounds;
        private Rectangle _listBounds;
        private Rectangle _detailBounds;
        private Rectangle _searchBounds;
        private Rectangle[] _tabBounds = Array.Empty<Rectangle>();
        private ClickableTextureComponent _closeButton = null!;
        private ClickableTextureComponent _prevButton = null!;
        private ClickableTextureComponent _nextButton = null!;

        public ModBrowserMenu()
            : base(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height, showUpperRightCloseButton: false)
        {
            Initialize();
        }

        private void Initialize()
        {
            // Calculate layout
            int margin = 64;
            _panelBounds = new Rectangle(
                margin, margin,
                width - margin * 2, height - margin * 2
            );

            int innerX = _panelBounds.X + UIConstants.PanelPadding;
            int innerY = _panelBounds.Y + UIConstants.PanelPadding + 60; // space for title + search
            int innerWidth = _panelBounds.Width - UIConstants.PanelPadding * 2;
            int innerHeight = _panelBounds.Height - UIConstants.PanelPadding * 2 - 60;

            int listWidth = (int)(innerWidth * UIConstants.ListWidthRatio);
            int detailWidth = innerWidth - listWidth - UIConstants.PanelPadding;

            _listBounds = new Rectangle(innerX, innerY, listWidth, innerHeight);
            _detailBounds = new Rectangle(innerX + listWidth + UIConstants.PanelPadding, innerY, detailWidth, innerHeight);

            // Search bar at top of list area
            _searchBounds = new Rectangle(innerX, _panelBounds.Y + UIConstants.PanelPadding + 48, listWidth, 48);
            _searchBar = new SearchBar(_searchBounds);

            // Source filter tabs — positioned between title and search bar
            int tabY = _panelBounds.Y + UIConstants.PanelPadding + 16;
            int tabStartX = _panelBounds.X + UIConstants.PanelPadding + 220;
            _tabBounds = new Rectangle[TabLabels.Length];
            int tabX = tabStartX;
            for (int i = 0; i < TabLabels.Length; i++)
            {
                int tabWidth = (int)Game1.smallFont.MeasureString(TabLabels[i]).X + 24;
                _tabBounds[i] = new Rectangle(tabX, tabY, tabWidth, 28);
                tabX += tabWidth + 8;
            }

            // Close button (top right of panel)
            _closeButton = new ClickableTextureComponent(
                new Rectangle(_panelBounds.Right - 48, _panelBounds.Y, 48, 48),
                Game1.mouseCursors,
                new Rectangle(337, 494, 12, 12),
                4f
            );

            // Pagination buttons
            _prevButton = new ClickableTextureComponent(
                "prev",
                new Rectangle(_listBounds.X, _listBounds.Bottom - 44, 48, 44),
                null, "Previous page",
                Game1.mouseCursors,
                new Rectangle(352, 495, 12, 11),
                4f
            );

            _nextButton = new ClickableTextureComponent(
                "next",
                new Rectangle(_listBounds.Right - 48, _listBounds.Bottom - 44, 48, 44),
                null, "Next page",
                Game1.mouseCursors,
                new Rectangle(365, 495, 12, 11),
                4f
            );

            // Load data
            LoadCatalog();
            _registry = new ModRegistry(ModEntry.ModDirectoryPath);
            RefreshInstalledMods();
            MergeInstalledMods();
            ApplyFilter();
        }

        private void LoadCatalog()
        {
            // Load GitHub catalog
            try
            {
                var catalogPath = Path.Combine(ModEntry.ModDirectoryPath, "Data", "catalog.json");
                if (!File.Exists(catalogPath))
                    catalogPath = Path.Combine(ModEntry.ModDirectoryPath, "catalog.json");

                if (File.Exists(catalogPath))
                {
                    var json = File.ReadAllText(catalogPath);
                    var githubEntries = JsonSerializer.Deserialize<List<CatalogEntry>>(json) ?? new();
                    foreach (var e in githubEntries)
                        e.Source = ModSource.GitHub;
                    _catalog.AddRange(githubEntries);
                }
                else
                {
                    ModEntry.Logger.Log("catalog.json not found", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Failed to load GitHub catalog: {ex.Message}", LogLevel.Error);
            }

            // Load Nexus catalog
            if (ModEntry.Config.NexusBrowsingEnabled)
            {
                try
                {
                    var nexusPath = Path.Combine(ModEntry.ModDirectoryPath, "Data", "nexus_catalog.json");
                    if (!File.Exists(nexusPath))
                        nexusPath = Path.Combine(ModEntry.ModDirectoryPath, "nexus_catalog.json");

                    if (File.Exists(nexusPath))
                    {
                        var json = File.ReadAllText(nexusPath);
                        var nexusEntries = JsonSerializer.Deserialize<List<CatalogEntry>>(json) ?? new();
                        _catalog.AddRange(nexusEntries);
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.Logger.Log($"Failed to load Nexus catalog: {ex.Message}", LogLevel.Error);
                }
            }

            // Sort by popularity (endorsements for Nexus, stars for GitHub)
            _catalog = _catalog.OrderByDescending(c => c.Popularity).ToList();
        }

        private void RefreshInstalledMods()
        {
            var scanner = new InstalledModScanner();
            _installedMods = scanner.Scan();
        }

        private void MergeInstalledMods()
        {
            // Build a set of UniqueIDs already in the catalog
            var catalogUniqueIDs = new HashSet<string>(
                _catalog.Where(c => !string.IsNullOrEmpty(c.UniqueID)).Select(c => c.UniqueID),
                StringComparer.OrdinalIgnoreCase
            );

            // Add installed mods that aren't in the catalog
            foreach (var kvp in _installedMods)
            {
                if (catalogUniqueIDs.Contains(kvp.Key))
                    continue;

                var mod = kvp.Value;
                _catalog.Add(new CatalogEntry
                {
                    Source = ModSource.Local,
                    DisplayName = !string.IsNullOrEmpty(mod.Name) ? mod.Name : mod.FolderName,
                    Description = !string.IsNullOrEmpty(mod.Description) ? mod.Description : "Manually installed mod.",
                    UniqueID = mod.UniqueID,
                    Owner = !string.IsNullOrEmpty(mod.Author) ? mod.Author : "Local",
                    Repo = "",
                    Stars = -1,
                    IsFromCatalog = false
                });
            }
        }

        private void ApplyFilter()
        {
            var query = _searchBar.Text.Trim();
            _filteredCatalog = _catalog.Where(c =>
            {
                // Source filter
                if (_sourceFilter.HasValue && c.Source != _sourceFilter.Value)
                    return false;

                // Text filter
                if (!string.IsNullOrEmpty(query))
                {
                    return c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           c.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
                }

                return true;
            }).ToList();

            _currentPage = 0;
            _selectedIndex = -1;
        }

        private int TotalPages => Math.Max(1, (int)Math.Ceiling(_filteredCatalog.Count / (float)UIConstants.ItemsPerPage));

        private List<CatalogEntry> GetPageItems()
        {
            return _filteredCatalog
                .Skip(_currentPage * UIConstants.ItemsPerPage)
                .Take(UIConstants.ItemsPerPage)
                .ToList();
        }

        private CatalogEntry? SelectedEntry =>
            _selectedIndex >= 0 && _selectedIndex < _filteredCatalog.Count
                ? _filteredCatalog[_selectedIndex]
                : null;

        private void FetchDetailsForPage()
        {
            var items = GetPageItems();
            foreach (var item in items)
            {
                if (!item.IsFromCatalog)
                    continue;

                if (item.Source == ModSource.GitHub)
                {
                    // Fetch GitHub releases
                    if (_latestReleases.ContainsKey(item.CatalogKey))
                        continue;

                    var key = item.CatalogKey;
                    var owner = item.Owner;
                    var repo = item.Repo;

                    Task.Run(async () =>
                    {
                        try
                        {
                            var releases = await GitHubApiClient.GetReleasesAsync(owner, repo);
                            var latest = releases?.FirstOrDefault(r => !r.Draft && !r.Prerelease);
                            _gameThreadActions.Enqueue(() => _latestReleases[key] = latest);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.Logger.Log($"Failed to fetch releases for {key}: {ex.Message}", LogLevel.Trace);
                        }
                    });
                }
                else if (item.Source == ModSource.Nexus && NexusApiClient.IsValidated)
                {
                    // Fetch Nexus mod info + files
                    var modId = item.NexusModId;
                    if (!_nexusModDetails.ContainsKey(modId))
                    {
                        Task.Run(async () =>
                        {
                            try
                            {
                                var modInfo = await NexusApiClient.GetModInfoAsync(modId);
                                _gameThreadActions.Enqueue(() => _nexusModDetails[modId] = modInfo);
                            }
                            catch (Exception ex)
                            {
                                ModEntry.Logger.Log($"Failed to fetch Nexus mod {modId}: {ex.Message}", LogLevel.Trace);
                            }
                        });
                    }

                    if (!_nexusModFiles.ContainsKey(modId))
                    {
                        Task.Run(async () =>
                        {
                            try
                            {
                                var files = await NexusApiClient.GetModFilesAsync(modId);
                                _gameThreadActions.Enqueue(() => _nexusModFiles[modId] = files);
                            }
                            catch (Exception ex)
                            {
                                ModEntry.Logger.Log($"Failed to fetch Nexus files for mod {modId}: {ex.Message}", LogLevel.Trace);
                            }
                        });
                    }
                }
            }
        }

        // --- IClickableMenu overrides ---

        public override void update(GameTime time)
        {
            base.update(time);

            // Process game thread actions from background tasks
            while (_gameThreadActions.TryDequeue(out var action))
                action();

            _searchBar.Update();
            if (_searchBar.HasChanged)
                ApplyFilter();

            // Periodically refresh installed mod state (every ~3 seconds)
            // so nxm:// installs are reflected without reopening the menu
            _refreshTicks++;
            if (_refreshTicks >= 180)
            {
                _refreshTicks = 0;
                _registry.Load();
                RefreshInstalledMods();
            }

            // Clear expired status
            if (!string.IsNullOrEmpty(_statusMessage) && DateTime.UtcNow > _statusExpiry)
                _statusMessage = "";
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            // Close button
            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                TitleMenu.subMenu = null;
                return;
            }

            // Exit button (shown when mods are pending)
            if (ModEntry.PendingInstalls.Count > 0)
            {
                var exitRect = GetExitButtonRect();
                if (exitRect.Contains(x, y))
                {
                    Game1.playSound("select");
                    RestartGame();
                    return;
                }
            }

            // Source filter tabs
            for (int i = 0; i < _tabBounds.Length; i++)
            {
                if (_tabBounds[i].Contains(x, y))
                {
                    _sourceFilter = i switch
                    {
                        1 => ModSource.GitHub,
                        2 => ModSource.Nexus,
                        _ => null
                    };
                    Game1.playSound("smallSelect");
                    ApplyFilter();
                    FetchDetailsForPage();
                    return;
                }
            }

            // Search bar
            _searchBar.ReceiveLeftClick(x, y);

            // Pagination
            if (_prevButton.containsPoint(x, y) && _currentPage > 0)
            {
                _currentPage--;
                _selectedIndex = -1;
                Game1.playSound("shwip");
                FetchDetailsForPage();
                return;
            }
            if (_nextButton.containsPoint(x, y) && _currentPage < TotalPages - 1)
            {
                _currentPage++;
                _selectedIndex = -1;
                Game1.playSound("shwip");
                FetchDetailsForPage();
                return;
            }

            // List item click
            var items = GetPageItems();
            int listContentY = _listBounds.Y + 56; // below search
            for (int i = 0; i < items.Count; i++)
            {
                var rowRect = new Rectangle(_listBounds.X, listContentY + i * UIConstants.RowHeight, _listBounds.Width, UIConstants.RowHeight - 4);
                if (rowRect.Contains(x, y))
                {
                    _selectedIndex = _currentPage * UIConstants.ItemsPerPage + i;
                    Game1.playSound("smallSelect");
                    FetchDetailsForPage();
                    return;
                }
            }

            // Detail panel buttons (only for catalog mods)
            if (SelectedEntry != null && SelectedEntry.IsFromCatalog && !_isBusy)
            {
                if (SelectedEntry.Source == ModSource.Nexus)
                    HandleNexusDetailClick(x, y);
                else
                    HandleDetailPanelClick(x, y);
            }
        }

        private void HandleDetailPanelClick(int x, int y)
        {
            var entry = SelectedEntry!;
            if (!entry.IsFromCatalog)
                return;

            var info = _registry.Get(entry.CatalogKey);
            bool isInstalled = info != null || _installedMods.ContainsKey(entry.UniqueID);
            _latestReleases.TryGetValue(entry.CatalogKey, out var latest);
            bool hasUpdate = info != null && latest != null &&
                UpdateChecker.IsUpdateAvailable(entry.CatalogKey, info.InstalledVersion);

            int btnY = _detailButtonY;
            int btnWidth = 200;
            int btnHeight = 48;
            int btnX = _detailBounds.X + (_detailBounds.Width - btnWidth) / 2;

            // Install / Update button
            var installBtnRect = new Rectangle(btnX, btnY, btnWidth, btnHeight);
            if (installBtnRect.Contains(x, y))
            {
                if (!isInstalled && latest != null)
                {
                    DoInstall(entry, latest);
                    return;
                }
                if (hasUpdate && latest != null)
                {
                    DoInstall(entry, latest);
                    return;
                }
            }

            // Uninstall button
            var uninstallBtnRect = new Rectangle(btnX, btnY + 56, btnWidth, btnHeight);
            if (isInstalled && uninstallBtnRect.Contains(x, y))
            {
                DoUninstall(entry);
                return;
            }

            // Auto-update toggle
            if (info != null)
            {
                var autoUpdateRect = new Rectangle(_detailBounds.X + 16, btnY + 124, _detailBounds.Width - 32, 36);
                if (autoUpdateRect.Contains(x, y))
                {
                    info.AutoUpdate = !info.AutoUpdate;
                    _registry.Save();
                    Game1.playSound("drumkit6");
                    return;
                }

                // Lock version toggle
                var lockRect = new Rectangle(_detailBounds.X + 16, btnY + 164, _detailBounds.Width - 32, 36);
                if (lockRect.Contains(x, y))
                {
                    info.LockedVersion = info.LockedVersion != null ? null : info.InstalledVersion;
                    _registry.Save();
                    Game1.playSound("drumkit6");
                    return;
                }
            }
        }

        private void HandleNexusDetailClick(int x, int y)
        {
            var entry = SelectedEntry!;

            var info = _registry.Get(entry.CatalogKey);
            bool isInstalled = info != null || _installedMods.ContainsKey(entry.UniqueID);

            int btnY = _detailButtonY;
            int btnWidth = 200;
            int btnHeight = 48;
            int btnX = _detailBounds.X + (_detailBounds.Width - btnWidth) / 2;

            // Main action button
            var actionBtnRect = new Rectangle(btnX, btnY, btnWidth, btnHeight);
            if (actionBtnRect.Contains(x, y))
            {
                if (!isInstalled || HasNexusUpdate(entry))
                {
                    if (NexusApiClient.IsPremium)
                    {
                        DoNexusInstall(entry);
                    }
                    else
                    {
                        // Open in browser for free users
                        var url = $"https://www.nexusmods.com/stardewvalley/mods/{entry.NexusModId}?tab=files";
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                            SetStatus("Opened in browser. Click 'Download with Manager'.");
                        }
                        catch (Exception ex)
                        {
                            ModEntry.Logger.Log($"Failed to open browser: {ex.Message}", LogLevel.Error);
                            SetStatus("Failed to open browser.");
                        }
                    }
                    return;
                }
            }

            // Uninstall button
            var uninstallBtnRect = new Rectangle(btnX, btnY + 56, btnWidth, btnHeight);
            if (isInstalled && uninstallBtnRect.Contains(x, y))
            {
                DoUninstall(entry);
                return;
            }

            // Auto-update toggle
            if (info != null)
            {
                var autoUpdateRect = new Rectangle(_detailBounds.X + 16, btnY + 124, _detailBounds.Width - 32, 36);
                if (autoUpdateRect.Contains(x, y))
                {
                    info.AutoUpdate = !info.AutoUpdate;
                    _registry.Save();
                    Game1.playSound("drumkit6");
                    return;
                }

                var lockRect = new Rectangle(_detailBounds.X + 16, btnY + 164, _detailBounds.Width - 32, 36);
                if (lockRect.Contains(x, y))
                {
                    info.LockedVersion = info.LockedVersion != null ? null : info.InstalledVersion;
                    _registry.Save();
                    Game1.playSound("drumkit6");
                    return;
                }
            }
        }

        private bool HasNexusUpdate(CatalogEntry entry)
        {
            var info = _registry.Get(entry.CatalogKey);
            if (info == null)
                return false;
            if (_nexusModFiles.TryGetValue(entry.NexusModId, out var files) && files != null)
            {
                var mainFile = files.FirstOrDefault(f =>
                    "MAIN".Equals(f.CategoryName, StringComparison.OrdinalIgnoreCase));
                if (mainFile != null)
                {
                    var latestVer = UpdateChecker.NormalizeVersion(mainFile.Version);
                    var installedVer = UpdateChecker.NormalizeVersion(info.InstalledVersion);
                    return latestVer != null && installedVer != null && latestVer > installedVer;
                }
            }
            return false;
        }

        private void DoInstall(CatalogEntry entry, GitHubRelease release)
        {
            _isBusy = true;
            SetStatus("Installing...");

            Task.Run(async () =>
            {
                var success = await ModInstaller.InstallAsync(entry, release, _registry);
                _gameThreadActions.Enqueue(() =>
                {
                    _isBusy = false;
                    if (success)
                    {
                        ModEntry.PendingInstalls.Add(entry.DisplayName);
                        SetStatus($"Installed {entry.DisplayName}.");
                        RefreshInstalledMods();
                        _registry.Load();
                    }
                    else
                    {
                        SetStatus("Install failed.");
                    }
                });
            });
        }

        private void DoNexusInstall(CatalogEntry entry)
        {
            if (!_nexusModFiles.TryGetValue(entry.NexusModId, out var files) || files == null)
            {
                SetStatus("File info not loaded yet.");
                return;
            }

            var mainFile = files.FirstOrDefault(f =>
                "MAIN".Equals(f.CategoryName, StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault();

            if (mainFile == null)
            {
                SetStatus("No files available.");
                return;
            }

            _isBusy = true;
            SetStatus("Installing from Nexus...");

            Task.Run(async () =>
            {
                var success = await ModInstaller.InstallFromNexusDirectAsync(entry, mainFile, _registry);
                _gameThreadActions.Enqueue(() =>
                {
                    _isBusy = false;
                    if (success)
                    {
                        ModEntry.PendingInstalls.Add(entry.DisplayName);
                        SetStatus($"Installed {entry.DisplayName}.");
                        RefreshInstalledMods();
                        _registry.Load();
                    }
                    else
                    {
                        SetStatus("Install failed.");
                    }
                });
            });
        }

        private void DoUninstall(CatalogEntry entry)
        {
            _isBusy = true;
            SetStatus("Uninstalling...");

            var success = ModInstaller.Uninstall(entry, _registry);
            _isBusy = false;

            if (success)
            {
                SetStatus("Uninstalled.");
                RefreshInstalledMods();
            }
            else
            {
                SetStatus("Uninstall failed.");
            }
        }

        private void SetStatus(string message, int durationSeconds = 5)
        {
            _statusMessage = message;
            _statusExpiry = DateTime.UtcNow.AddSeconds(durationSeconds);
        }

        public override void performHoverAction(int x, int y)
        {
            _hoveredIndex = -1;
            var items = GetPageItems();
            int listContentY = _listBounds.Y + 56;

            for (int i = 0; i < items.Count; i++)
            {
                var rowRect = new Rectangle(_listBounds.X, listContentY + i * UIConstants.RowHeight, _listBounds.Width, UIConstants.RowHeight - 4);
                if (rowRect.Contains(x, y))
                {
                    _hoveredIndex = _currentPage * UIConstants.ItemsPerPage + i;
                    break;
                }
            }

            _closeButton.tryHover(x, y);
            _prevButton.tryHover(x, y);
            _nextButton.tryHover(x, y);
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                TitleMenu.subMenu = null;
                return;
            }
        }

        public override void draw(SpriteBatch b)
        {
            // Dark overlay
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.75f);

            // Panel background — texture box border, then dark inner fill
            IClickableMenu.drawTextureBox(
                b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                _panelBounds.X, _panelBounds.Y, _panelBounds.Width, _panelBounds.Height,
                Color.White, 1f, false
            );
            var innerRect = new Rectangle(
                _panelBounds.X + 12, _panelBounds.Y + 12,
                _panelBounds.Width - 24, _panelBounds.Height - 24
            );
            b.Draw(Game1.fadeToBlackRect, innerRect, UIConstants.PanelBackground);

            // Title
            var titleText = "Mod Browser";
            b.DrawString(Game1.dialogueFont, titleText,
                new Vector2(_panelBounds.X + UIConstants.PanelPadding, _panelBounds.Y + 12),
                UIConstants.TextWhite);

            // Source filter tabs
            DrawSourceTabs(b);

            // Search bar
            _searchBar.Draw(b);

            // Mod list
            DrawModList(b);

            // Detail panel
            if (SelectedEntry != null)
                DrawDetailPanel(b);

            // Status message / pending installs
            {
                string? displayText = null;
                Color textColor = UIConstants.TextGold;

                if (ModEntry.PendingInstalls.Count > 0)
                {
                    var modWord = ModEntry.PendingInstalls.Count == 1 ? "mod" : "mods";
                    displayText = $"{ModEntry.PendingInstalls.Count} {modWord} installed \u2014 exit & relaunch to activate";
                    textColor = UIConstants.BadgeUpdate;
                }
                else if (!string.IsNullOrEmpty(_statusMessage))
                {
                    displayText = _statusMessage;
                }

                if (displayText != null)
                {
                    var statusSize = Game1.smallFont.MeasureString(displayText);
                    var statusX = _panelBounds.X + _panelBounds.Width / 2f - statusSize.X / 2f;
                    var statusY = _panelBounds.Bottom - 36;
                    b.DrawString(Game1.smallFont, displayText, new Vector2(statusX, statusY), textColor);
                }
            }

            // Exit button (top right, next to close — only when mods pending)
            if (ModEntry.PendingInstalls.Count > 0)
            {
                var exitRect = GetExitButtonRect();
                b.Draw(Game1.fadeToBlackRect, exitRect, UIConstants.ButtonRestart);
                var btnText = "Exit Game";
                var btnSize = Game1.smallFont.MeasureString(btnText);
                b.DrawString(Game1.smallFont, btnText,
                    new Vector2(
                        exitRect.X + (exitRect.Width - btnSize.X) / 2f,
                        exitRect.Y + (exitRect.Height - btnSize.Y) / 2f),
                    Color.White);
            }

            // Close button
            _closeButton.draw(b);

            // Mouse cursor
            drawMouse(b);
        }

        private void DrawSourceTabs(SpriteBatch b)
        {
            for (int i = 0; i < TabLabels.Length; i++)
            {
                bool isActive = i switch
                {
                    0 => _sourceFilter == null,
                    1 => _sourceFilter == ModSource.GitHub,
                    2 => _sourceFilter == ModSource.Nexus,
                    _ => false
                };

                var tabColor = isActive ? UIConstants.TabActive : UIConstants.TabInactive;
                b.Draw(Game1.fadeToBlackRect, _tabBounds[i], tabColor);
                var labelSize = Game1.smallFont.MeasureString(TabLabels[i]);
                b.DrawString(Game1.smallFont, TabLabels[i],
                    new Vector2(
                        _tabBounds[i].X + (_tabBounds[i].Width - labelSize.X) / 2f,
                        _tabBounds[i].Y + (_tabBounds[i].Height - labelSize.Y) / 2f),
                    isActive ? Color.White : UIConstants.TextGray);
            }
        }

        private void DrawModList(SpriteBatch b)
        {
            var items = GetPageItems();
            int listContentY = _listBounds.Y + 56;

            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                int globalIndex = _currentPage * UIConstants.ItemsPerPage + i;
                var rowRect = new Rectangle(_listBounds.X, listContentY + i * UIConstants.RowHeight, _listBounds.Width, UIConstants.RowHeight - 4);

                // Row background
                Color rowColor;
                if (globalIndex == _selectedIndex)
                    rowColor = UIConstants.RowSelected;
                else if (globalIndex == _hoveredIndex)
                    rowColor = UIConstants.RowHover;
                else
                    rowColor = UIConstants.RowNormal;

                b.Draw(Game1.fadeToBlackRect, rowRect, rowColor);

                // Name
                var nameText = entry.DisplayName;
                if (nameText.Length > 28)
                    nameText = nameText[..25] + "...";
                b.DrawString(Game1.smallFont, nameText,
                    new Vector2(rowRect.X + 12, rowRect.Y + 8), UIConstants.TextWhite);

                // Description (truncated)
                var descText = entry.Description;
                if (descText.Length > 50)
                    descText = descText[..47] + "...";
                b.DrawString(Game1.smallFont, descText,
                    new Vector2(rowRect.X + 12, rowRect.Y + 32), UIConstants.TextGray * 0.8f);

                // Popularity indicator (source-aware)
                if (entry.IsFromCatalog)
                {
                    string popText;
                    Color popColor;
                    if (entry.Source == ModSource.Nexus)
                    {
                        popText = $"\u2665 {entry.Endorsements}";
                        popColor = UIConstants.BadgeNexus;
                    }
                    else
                    {
                        popText = $"\u2605 {entry.Stars}";
                        popColor = UIConstants.TextGold;
                    }

                    var popSize = Game1.smallFont.MeasureString(popText);
                    b.DrawString(Game1.smallFont, popText,
                        new Vector2(rowRect.Right - popSize.X - 12, rowRect.Y + 8), popColor);
                }

                // Badges
                var info = entry.IsFromCatalog ? _registry.Get(entry.CatalogKey) : null;
                bool isInstalled = info != null || _installedMods.ContainsKey(entry.UniqueID);

                if (isInstalled)
                {
                    bool hasUpdate = false;
                    if (entry.IsFromCatalog && info != null)
                    {
                        if (entry.Source == ModSource.GitHub)
                            hasUpdate = UpdateChecker.IsUpdateAvailable(entry.CatalogKey, info.InstalledVersion);
                        else if (entry.Source == ModSource.Nexus)
                            hasUpdate = HasNexusUpdate(entry);
                    }

                    var badgeText = hasUpdate ? "Update" : "Installed";
                    var badgeColor = hasUpdate ? UIConstants.BadgeUpdate : UIConstants.BadgeInstalled;
                    var badgeSize = Game1.smallFont.MeasureString(badgeText);

                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle((int)(rowRect.Right - badgeSize.X - 20), rowRect.Y + 40,
                            (int)badgeSize.X + 12, 24),
                        badgeColor);
                    b.DrawString(Game1.smallFont, badgeText,
                        new Vector2(rowRect.Right - badgeSize.X - 14, rowRect.Y + 42), Color.White);
                }
            }

            // Pagination
            if (TotalPages > 1)
            {
                if (_currentPage > 0)
                    _prevButton.draw(b);
                if (_currentPage < TotalPages - 1)
                    _nextButton.draw(b);

                var pageText = $"Page {_currentPage + 1} / {TotalPages}";
                var pageSize = Game1.smallFont.MeasureString(pageText);
                b.DrawString(Game1.smallFont, pageText,
                    new Vector2(_listBounds.X + _listBounds.Width / 2f - pageSize.X / 2f, _listBounds.Bottom - 36),
                    UIConstants.TextGray);
            }
        }

        private void DrawDetailPanel(SpriteBatch b)
        {
            var entry = SelectedEntry!;

            // Detail panel background (darker inset)
            b.Draw(Game1.fadeToBlackRect, _detailBounds, new Color(40, 40, 40));

            if (entry.Source == ModSource.Nexus)
                DrawNexusDetailPanel(b, entry);
            else
                DrawGitHubDetailPanel(b, entry);
        }

        private void DrawGitHubDetailPanel(SpriteBatch b, CatalogEntry entry)
        {
            int textX = _detailBounds.X + 16;
            int textY = _detailBounds.Y + 16;

            // Mod name
            b.DrawString(Game1.dialogueFont, entry.DisplayName,
                new Vector2(textX, textY), UIConstants.TextWhite);
            textY += 48;

            // Author
            b.DrawString(Game1.smallFont, $"by {entry.Owner}",
                new Vector2(textX, textY), UIConstants.TextGray);
            textY += 28;

            // Stars (only for catalog entries)
            if (entry.IsFromCatalog)
            {
                b.DrawString(Game1.smallFont, $"\u2605 {entry.Stars} stars",
                    new Vector2(textX, textY), UIConstants.TextGold);
                textY += 32;
            }
            else
            {
                textY += 4;
            }

            // Description (word wrapped, max 3 lines)
            textY = DrawWrappedText(b, entry.Description, textX, textY, _detailBounds.Width - 32, UIConstants.TextGray, maxLines: 3);
            textY += 8;

            // Installed version
            var info = entry.IsFromCatalog ? _registry.Get(entry.CatalogKey) : null;
            bool isInstalled = info != null || _installedMods.ContainsKey(entry.UniqueID);
            GitHubRelease? latest = null;
            if (entry.IsFromCatalog)
                _latestReleases.TryGetValue(entry.CatalogKey, out latest);

            if (isInstalled)
            {
                var version = info?.InstalledVersion ?? _installedMods.GetValueOrDefault(entry.UniqueID)?.Version ?? "?";
                b.DrawString(Game1.smallFont, $"Installed: v{version}",
                    new Vector2(textX, textY), UIConstants.BadgeInstalled);
                textY += 28;
            }

            if (entry.IsFromCatalog)
            {
                // Latest version from GitHub
                if (latest != null)
                {
                    b.DrawString(Game1.smallFont, $"Latest: {latest.TagName}",
                        new Vector2(textX, textY), UIConstants.TextGray);
                    textY += 28;
                }
                else
                {
                    b.DrawString(Game1.smallFont, "Fetching latest...",
                        new Vector2(textX, textY), UIConstants.TextGray * 0.6f);
                    textY += 28;
                }

                // Buttons
                _detailButtonY = textY + 8;
                if (!_isBusy)
                    DrawGitHubDetailButtons(b, entry, info, isInstalled, latest);
                else
                    DrawBusyIndicator(b);
            }
            else
            {
                // Non-catalog mod — show UniqueID and a note
                b.DrawString(Game1.smallFont, $"ID: {entry.UniqueID}",
                    new Vector2(textX, textY), UIConstants.TextGray);
                textY += 28;

                DrawWrappedText(b, "This mod was installed manually. Manage it through your Mods folder.",
                    textX, textY, _detailBounds.Width - 32, UIConstants.TextGray * 0.8f);
            }
        }

        private void DrawNexusDetailPanel(SpriteBatch b, CatalogEntry entry)
        {
            int textX = _detailBounds.X + 16;
            int textY = _detailBounds.Y + 16;

            // Mod name
            b.DrawString(Game1.dialogueFont, entry.DisplayName,
                new Vector2(textX, textY), UIConstants.TextWhite);
            textY += 48;

            // Author
            var authorName = !string.IsNullOrEmpty(entry.Author) ? entry.Author : entry.Owner;
            b.DrawString(Game1.smallFont, $"by {authorName}",
                new Vector2(textX, textY), UIConstants.TextGray);
            textY += 28;

            // Endorsements + Downloads (from live data or catalog)
            _nexusModDetails.TryGetValue(entry.NexusModId, out var modInfo);
            var endorsements = modInfo?.EndorsementCount ?? entry.Endorsements;
            var downloads = modInfo?.ModDownloads ?? 0;

            b.DrawString(Game1.smallFont, $"\u2665 {endorsements} endorsements",
                new Vector2(textX, textY), UIConstants.BadgeNexus);
            textY += 24;

            if (downloads > 0)
            {
                b.DrawString(Game1.smallFont, $"{downloads:N0} downloads",
                    new Vector2(textX, textY), UIConstants.TextGray);
                textY += 28;
            }
            else
            {
                textY += 4;
            }

            // Description (word wrapped, max 3 lines)
            var desc = modInfo?.Summary ?? entry.Description;
            textY = DrawWrappedText(b, desc, textX, textY, _detailBounds.Width - 32, UIConstants.TextGray, maxLines: 3);
            textY += 8;

            // Installed version
            var info = _registry.Get(entry.CatalogKey);
            bool isInstalled = info != null || _installedMods.ContainsKey(entry.UniqueID);

            if (isInstalled)
            {
                var version = info?.InstalledVersion ?? _installedMods.GetValueOrDefault(entry.UniqueID)?.Version ?? "?";
                b.DrawString(Game1.smallFont, $"Installed: v{version}",
                    new Vector2(textX, textY), UIConstants.BadgeInstalled);
                textY += 28;
            }

            // Latest file version
            _nexusModFiles.TryGetValue(entry.NexusModId, out var files);
            var mainFile = files?.FirstOrDefault(f =>
                "MAIN".Equals(f.CategoryName, StringComparison.OrdinalIgnoreCase));
            if (mainFile != null)
            {
                var sizeText = mainFile.SizeKb > 1024
                    ? $"{mainFile.SizeKb / 1024f:F1} MB"
                    : $"{mainFile.SizeKb} KB";
                b.DrawString(Game1.smallFont, $"Latest: v{mainFile.Version} ({sizeText})",
                    new Vector2(textX, textY), UIConstants.TextGray);
                textY += 28;
            }
            else if (NexusApiClient.IsValidated)
            {
                b.DrawString(Game1.smallFont, "Fetching file info...",
                    new Vector2(textX, textY), UIConstants.TextGray * 0.6f);
                textY += 28;
            }

            // API key warning
            if (!NexusApiClient.IsValidated)
            {
                b.DrawString(Game1.smallFont, "Set NexusApiKey in config",
                    new Vector2(textX, textY), UIConstants.BadgeNexus);
                textY += 28;
            }

            // Buttons
            _detailButtonY = textY + 8;
            if (!_isBusy)
                DrawNexusDetailButtons(b, entry, info, isInstalled);
            else
                DrawBusyIndicator(b);
        }

        private void DrawGitHubDetailButtons(SpriteBatch b, CatalogEntry entry, InstalledModInfo? info,
            bool isInstalled, GitHubRelease? latest)
        {
            int btnWidth = 200;
            int btnHeight = 48;
            int btnX = _detailBounds.X + (_detailBounds.Width - btnWidth) / 2;
            int btnY = _detailButtonY;

            bool hasUpdate = info != null && latest != null &&
                UpdateChecker.IsUpdateAvailable(entry.CatalogKey, info.InstalledVersion);

            // Install / Update button
            if (!isInstalled)
            {
                DrawButton(b, new Rectangle(btnX, btnY, btnWidth, btnHeight),
                    "Install", UIConstants.ButtonInstall, latest != null);
            }
            else if (hasUpdate)
            {
                DrawButton(b, new Rectangle(btnX, btnY, btnWidth, btnHeight),
                    "Update", UIConstants.ButtonUpdate, true);
            }
            else
            {
                DrawButton(b, new Rectangle(btnX, btnY, btnWidth, btnHeight),
                    "Up to date", UIConstants.RowNormal, false);
            }

            // Uninstall button
            if (isInstalled)
            {
                DrawButton(b, new Rectangle(btnX, btnY + 56, btnWidth, btnHeight),
                    "Uninstall", UIConstants.ButtonUninstall, true);
            }

            // Per-mod settings
            if (info != null)
            {
                int settingsY = btnY + 124;

                // Auto-update toggle
                var autoText = info.AutoUpdate ? "\u2611 Auto-update" : "\u2610 Auto-update";
                b.DrawString(Game1.smallFont, autoText,
                    new Vector2(_detailBounds.X + 16, settingsY), UIConstants.TextWhite);

                // Lock version toggle
                var lockText = info.LockedVersion != null
                    ? $"\u2611 Locked to v{info.LockedVersion}"
                    : "\u2610 Lock to current version";
                b.DrawString(Game1.smallFont, lockText,
                    new Vector2(_detailBounds.X + 16, settingsY + 40), UIConstants.TextWhite);
            }
        }

        private void DrawNexusDetailButtons(SpriteBatch b, CatalogEntry entry, InstalledModInfo? info,
            bool isInstalled)
        {
            int btnWidth = 200;
            int btnHeight = 48;
            int btnX = _detailBounds.X + (_detailBounds.Width - btnWidth) / 2;
            int btnY = _detailButtonY;

            bool hasUpdate = HasNexusUpdate(entry);

            // Main action button
            if (!isInstalled)
            {
                if (NexusApiClient.IsPremium)
                {
                    DrawButton(b, new Rectangle(btnX, btnY, btnWidth, btnHeight),
                        "Install", UIConstants.ButtonInstall, NexusApiClient.IsValidated);
                }
                else
                {
                    DrawButton(b, new Rectangle(btnX, btnY, btnWidth, btnHeight),
                        "Open in Browser", UIConstants.ButtonBrowse, true);
                }
            }
            else if (hasUpdate)
            {
                if (NexusApiClient.IsPremium)
                {
                    DrawButton(b, new Rectangle(btnX, btnY, btnWidth, btnHeight),
                        "Update", UIConstants.ButtonUpdate, true);
                }
                else
                {
                    DrawButton(b, new Rectangle(btnX, btnY, btnWidth, btnHeight),
                        "Open in Browser", UIConstants.ButtonBrowse, true);
                }
            }
            else
            {
                DrawButton(b, new Rectangle(btnX, btnY, btnWidth, btnHeight),
                    "Up to date", UIConstants.RowNormal, false);
            }

            // Uninstall button
            if (isInstalled)
            {
                DrawButton(b, new Rectangle(btnX, btnY + 56, btnWidth, btnHeight),
                    "Uninstall", UIConstants.ButtonUninstall, true);
            }

            // Per-mod settings
            if (info != null)
            {
                int settingsY = btnY + 124;

                var autoText = info.AutoUpdate ? "\u2611 Auto-update" : "\u2610 Auto-update";
                b.DrawString(Game1.smallFont, autoText,
                    new Vector2(_detailBounds.X + 16, settingsY), UIConstants.TextWhite);

                var lockText = info.LockedVersion != null
                    ? $"\u2611 Locked to v{info.LockedVersion}"
                    : "\u2610 Lock to current version";
                b.DrawString(Game1.smallFont, lockText,
                    new Vector2(_detailBounds.X + 16, settingsY + 40), UIConstants.TextWhite);
            }
        }

        private void DrawButton(SpriteBatch b, Rectangle rect, string text, Color color, bool enabled)
        {
            b.Draw(Game1.fadeToBlackRect, rect, enabled ? color : color * 0.4f);

            var textSize = Game1.smallFont.MeasureString(text);
            b.DrawString(Game1.smallFont, text,
                new Vector2(rect.X + rect.Width / 2f - textSize.X / 2f, rect.Y + rect.Height / 2f - textSize.Y / 2f),
                enabled ? Color.White : Color.Gray);
        }

        private void DrawBusyIndicator(SpriteBatch b)
        {
            int btnY = _detailButtonY;
            b.DrawString(Game1.smallFont, _statusMessage,
                new Vector2(_detailBounds.X + 16, btnY + 16), UIConstants.TextGold);
        }

        private int DrawWrappedText(SpriteBatch b, string text, int x, int y, int maxWidth, Color color, int maxLines = 0)
        {
            var font = Game1.smallFont;
            var words = text.Split(' ');
            var line = "";
            int lineY = y;
            int lineCount = 0;

            foreach (var word in words)
            {
                var test = string.IsNullOrEmpty(line) ? word : line + " " + word;
                if (font.MeasureString(test).X > maxWidth && !string.IsNullOrEmpty(line))
                {
                    lineCount++;
                    if (maxLines > 0 && lineCount >= maxLines)
                    {
                        // Truncate on last allowed line
                        if (line.Length > 3)
                            line = line[..^3] + "...";
                        b.DrawString(font, line, new Vector2(x, lineY), color);
                        return lineY + 24;
                    }
                    b.DrawString(font, line, new Vector2(x, lineY), color);
                    lineY += 24;
                    line = word;
                }
                else
                {
                    line = test;
                }
            }

            if (!string.IsNullOrEmpty(line))
            {
                b.DrawString(font, line, new Vector2(x, lineY), color);
                lineY += 24;
            }

            return lineY;
        }

        private Rectangle GetExitButtonRect()
        {
            // Positioned to the left of the close (X) button at the top right of the panel
            int btnWidth = 120;
            int btnHeight = 36;
            return new Rectangle(
                _panelBounds.Right - 48 - btnWidth - 8,
                _panelBounds.Y + 6,
                btnWidth, btnHeight);
        }

        private static void RestartGame()
        {
            Game1.game1.Exit();
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            width = Game1.uiViewport.Width;
            height = Game1.uiViewport.Height;
            Initialize();
        }
    }

}
