using Newtonsoft.Json;
using System.IO.Compression;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;
using MCLauncher.WPFDataTypes;
using Path = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace MCLauncher
{
    public partial class ModernMainWindow : Window, ICommonVersionCommands
    {
        private static readonly string PREFS_PATH = @"preferences.json";
        private static readonly string IMPORTED_VERSIONS_PATH = @"imported_versions";
        private static readonly string VERSIONS_API_UWP = "https://mrarm.io/r/w10-vdb";
        private static readonly string VERSIONS_API_GDK = "https://raw.githubusercontent.com/MinecraftBedrockArchiver/GdkLinks/refs/heads/master/urls.min.json";

        private VersionList _versions;
        public Preferences UserPrefs { get; }

        private readonly VersionDownloader _anonVersionDownloader = new VersionDownloader();
        private readonly VersionDownloader _userVersionDownloader = new VersionDownloader();
        private readonly Task _userVersionDownloaderLoginTask;
        private volatile bool _hasLaunchTask = false;

        private ObservableCollection<ModernVersionViewModel> _installedVersions;
        private ObservableCollection<ModernVersionViewModel> _availableVersions;
        private List<ModernVersionViewModel> _allVersions;

        // Download pause/resume tracking
        private Dictionary<WPFDataTypes.Version, CancellationTokenSource> _downloadCancelTokens = new Dictionary<WPFDataTypes.Version, CancellationTokenSource>();
        private Dictionary<WPFDataTypes.Version, bool> _downloadPausedState = new Dictionary<WPFDataTypes.Version, bool>();
        private volatile bool _hasGdkExtractTask = false;

        // Auto-scroll support
        private bool _isAutoScrolling = false;
        private Point _autoScrollStartPoint;
        private ScrollViewer _autoScrollViewer;
        private DispatcherTimer _autoScrollTimer;
        private Ellipse _autoScrollIndicator;
        private DispatcherTimer _searchDebounceTimer;

        public ModernMainWindow()
        {
            InitializeComponent();

            // Load embedded icon
            var icon = EmbeddedIcon.GetIcon();
            if (icon != null)
            {
                HeaderIcon.Source = icon;
            }

            // Load preferences
            if (File.Exists(PREFS_PATH))
            {
                UserPrefs = JsonConvert.DeserializeObject<Preferences>(File.ReadAllText(PREFS_PATH));
            }
            else
            {
                UserPrefs = new Preferences();
                RewritePrefs();
            }

            var versionsApiUWP = UserPrefs.VersionsApiUWP != "" ? UserPrefs.VersionsApiUWP : VERSIONS_API_UWP;
            var versionsApiGDK = UserPrefs.VersionsApiGDK != "" ? UserPrefs.VersionsApiGDK : VERSIONS_API_GDK;
            _versions = new VersionList("versions_uwp.json", IMPORTED_VERSIONS_PATH, versionsApiUWP, this, VersionEntryPropertyChanged, "versions_gdk.json", versionsApiGDK);

            _installedVersions = new ObservableCollection<ModernVersionViewModel>();
            _availableVersions = new ObservableCollection<ModernVersionViewModel>();
            _allVersions = new List<ModernVersionViewModel>();

            InstalledVersionsList.ItemsSource = _installedVersions;
            AvailableVersionsList.ItemsSource = _availableVersions;

            _userVersionDownloaderLoginTask = new Task(() =>
            {
                _userVersionDownloader.EnableUserAuthorization();
            });

            // Initialize auto-scroll timer
            _autoScrollTimer = new DispatcherTimer();
            _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 FPS
            _autoScrollTimer.Tick += AutoScrollTimer_Tick;

            _searchDebounceTimer = new DispatcherTimer();
            _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(250);
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

            Loaded += ModernMainWindow_Loaded;
        }

        private async void ModernMainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Load saved language preference
            SetLanguage(UserPrefs.Language ?? "en");
            
            // Check status indicators
            UpdateStatusIndicators();
            

            await LoadVersionList();
        }

        private async Task LoadVersionList()
        {
            LoadingIndicator.Visibility = Visibility.Visible;
            AvailableVersionsList.Visibility = Visibility.Collapsed;

            _versions.PrepareForReload();

            LoadingText.Text = "Loading cached versions...";
            Debug.WriteLine("Loading cached versions...");
            try
            {
                await _versions.LoadFromCacheGDK();
                await _versions.LoadFromCacheUWP();
                Debug.WriteLine($"Loaded {_versions.Count} versions from cache");
            }
            catch (Exception e)
            {
                Debug.WriteLine("List cache load failed:\n" + e.ToString());
            }

            _versions.PrepareForReload();

            LoadingText.Text = "Downloading latest version list...";
            Debug.WriteLine("Downloading version lists...");
            try
            {
                await _versions.DownloadVersionsGDK();
                await _versions.DownloadVersionsUWP();
                Debug.WriteLine($"Downloaded versions, total count: {_versions.Count}");
            }
            catch (Exception e)
            {
                Debug.WriteLine("List download failed:\n" + e.ToString());
                ShowFriendlyError(Localization.Get("CouldntUpdateList"), 
                    Localization.Get("CouldntUpdateListMessage"));
            }

            LoadingText.Text = "Loading imported versions...";
            Debug.WriteLine("Loading imported versions...");
            await _versions.LoadImported();
            Debug.WriteLine($"Final version count: {_versions.Count}");

            LoadingIndicator.Visibility = Visibility.Collapsed;
            AvailableVersionsList.Visibility = Visibility.Visible;

            RefreshVersionLists();
            Debug.WriteLine($"Refreshed lists - Installed: {_installedVersions.Count}, Available: {_availableVersions.Count}");
        }

        private void RefreshVersionLists()
        {
            _installedVersions.Clear();
            _allVersions.Clear();

            Debug.WriteLine($"RefreshVersionLists called - Total versions: {_versions.Count}");

            foreach (var version in _versions)
            {
                var viewModel = new ModernVersionViewModel(version, this);

                if (version.IsInstalled)
                {
                    _installedVersions.Add(viewModel);
                }

                _allVersions.Add(viewModel);
            }

            // Show empty state
            EmptyState.Visibility = _installedVersions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Initial display - show all
            ICollectionView view = CollectionViewSource.GetDefaultView(_allVersions);
            AvailableVersionsList.ItemsSource = view;
            ApplyFilters();

            Debug.WriteLine($"Final counts - Installed: {_installedVersions.Count}, Available: {_allVersions.Count}");
        }

        private void VersionEntryPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsInstalled" || e.PropertyName == "IsImported" || e.PropertyName == "Name")
            {
                Dispatcher.InvokeAsync(() => RefreshVersionLists());
            }
        }

        private System.Version ParseVersionNumber(string versionName)
        {
            try
            {
                // Try to parse version like "1.21.50.07" or "1.20.0.1"
                // Remove any non-numeric prefixes and suffixes
                var parts = versionName.Split('.');
                if (parts.Length >= 2)
                {
                    // Build a proper Version object (max 4 parts)
                    int major = 0, minor = 0, build = 0, revision = 0;
                    
                    if (parts.Length > 0 && int.TryParse(parts[0], out major) &&
                        parts.Length > 1 && int.TryParse(parts[1], out minor))
                    {
                        if (parts.Length > 2) int.TryParse(parts[2], out build);
                        if (parts.Length > 3) int.TryParse(parts[3], out revision);
                        
                        return new System.Version(major, minor, build, revision);
                    }
                }
            }
            catch
            {
                // If parsing fails, return a minimal version
            }
            
            return new System.Version(0, 0, 0, 0);
        }

        // Get the versions folder path in AppData
        private static string GetVersionsFolder()
        {
            string appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MinecraftVersionLauncher",
                "Versions");
            
            // Create directory if it doesn't exist
            if (!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }
            
            return appDataPath;
        }

        // Open the versions folder in Windows Explorer
        private void ViewFiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string versionsPath = GetVersionsFolder();
                Process.Start("explorer.exe", versionsPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to open versions folder: " + ex.ToString());
                MessageBox.Show(
                    "Could not open the versions folder.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // Tab Navigation
        private void TabChanged(object sender, RoutedEventArgs e)
        {
            // Null checks in case this fires during initialization
            if (MyVersionsContent == null || AvailableContent == null)
                return;

            MyVersionsContent.Visibility = MyVersionsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            AvailableContent.Visibility = AvailableTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // Custom Window Controls
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeButton_Click(sender, e);
            }
            else
            {
                if (WindowState == WindowState.Maximized)
                {
                    // Get mouse position relative to screen
                    var mousePos = PointToScreen(e.GetPosition(this));
                    
                    // Restore window
                    WindowState = WindowState.Normal;
                    
                    // Position window under cursor
                    Left = mousePos.X - (RestoreBounds.Width / 2);
                    Top = mousePos.Y - 20;
                }
                
                try
                {
                    DragMove();
                }
                catch
                {
                    // Ignore any drag exceptions
                }
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Language switching
        private void SwitchToEnglish_Click(object sender, RoutedEventArgs e)
        {
            SetLanguage("en", showMessage: true);
        }

        private void SwitchToArabic_Click(object sender, RoutedEventArgs e)
        {
            SetLanguage("ar", showMessage: true);
        }

        private void SetLanguage(string languageCode, bool showMessage = false)
        {
            // Set localization language
            Localization.SetLanguage(languageCode);
            
            // Update button styles to highlight selected language
            if (languageCode == "en")
            {
                EnglishButton.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)); // Green
                EnglishButton.FontWeight = FontWeights.SemiBold;
                ArabicButton.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)); // Gray
                ArabicButton.FontWeight = FontWeights.Normal;
                
                // Set LTR flow direction for English
                this.FlowDirection = FlowDirection.LeftToRight;
            }
            else if (languageCode == "ar")
            {
                ArabicButton.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)); // Green
                ArabicButton.FontWeight = FontWeights.SemiBold;
                EnglishButton.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)); // Gray
                EnglishButton.FontWeight = FontWeights.Normal;
                
                // Set RTL flow direction for Arabic
                this.FlowDirection = FlowDirection.RightToLeft;
            }

            // Update all UI text
            UpdateUIText();

            // Save preference
            UserPrefs.Language = languageCode;
            RewritePrefs();

            // Show message only when user manually switches
            if (showMessage)
            {
                MessageBox.Show(
                    Localization.Get("LanguageChangedMessage"),
                    Localization.Get("LanguageChanged"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void UpdateUIText()
        {
            // Update window title
            this.Title = Localization.Get("AppTitle");
            
            // Update header
            if (HeaderTitle != null)
                HeaderTitle.Text = Localization.Get("AppTitle");
            
            // Update navigation
            if (NavigationLabel != null)
                NavigationLabel.Text = Localization.Get("Navigation");
            
            MyVersionsTab.Content = Localization.Get("MyVersions");
            AvailableTab.Content = Localization.Get("Browse");
            
            // Update My Versions section
            if (MyVersionsHeaderText != null)
                MyVersionsHeaderText.Text = Localization.Get("MyVersionsHeader");
            if (MyVersionsSubtitleText != null)
                MyVersionsSubtitleText.Text = Localization.Get("MyVersionsSubtitle");
            if (ImportButton != null)
                ImportButton.Content = Localization.Get("ImportFile");
            
            // Update empty state
            if (EmptyStateTitle != null)
                EmptyStateTitle.Text = Localization.Get("NoVersionsInstalled");
            if (EmptyStateSubtitle != null)
                EmptyStateSubtitle.Text = Localization.Get("NoVersionsSubtitle");
            if (BrowseVersionsButton != null)
                BrowseVersionsButton.Content = Localization.Get("BrowseVersionsButton");
            
            // Update Browse section
            if (BrowseHeaderText != null)
                BrowseHeaderText.Text = Localization.Get("BrowseVersionsHeader");
            if (BrowseSubtitleText != null)
                BrowseSubtitleText.Text = Localization.Get("BrowseVersionsSubtitle");
            if (ViewFilesButton != null)
                ViewFilesButton.Content = Localization.Get("ViewFiles");
            if (RefreshButton != null)
                RefreshButton.Content = Localization.Get("Refresh");
            
            // Update filter buttons
            FilterAll.Content = Localization.Get("All");
            FilterRelease.Content = Localization.Get("Release");
            FilterPreview.Content = Localization.Get("Preview");
            
            // Update loading text if visible
            if (LoadingText != null)
            {
                LoadingText.Text = Localization.Get("LoadingVersions");
            }
            
            // Update search box placeholder by clearing and resetting focus
            if (SearchBox != null)
            {
                var currentText = SearchBox.Text;
                SearchBox.Text = "";
                SearchBox.Text = currentText;
            }
            
            // Update status indicators
            UpdateStatusIndicators();
        }

        private void UpdateStatusIndicators()
        {
            // Update Developer Mode indicator text
            if (DevModeText != null)
                DevModeText.Text = Localization.Get("DeveloperMode");
            
            // Update Decryption Keys indicator text
            if (DecryptKeysText != null)
                DecryptKeysText.Text = Localization.Get("DecryptionKeys");
            
            // Check and update status
            CheckDeveloperModeStatus();
            CheckDecryptionKeysStatus();
        }

        private void CheckDeveloperModeStatus()
        {
            bool isEnabled = IsDeveloperModeEnabled();
            
            if (DevModeStatus != null && DevModeIndicator != null)
            {
                if (isEnabled)
                {
                    DevModeStatus.Text = "✓";
                    DevModeStatus.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)); // Green
                    DevModeIndicator.BorderBrush = new SolidColorBrush(Color.FromRgb(42, 42, 42));
                }
                else
                {
                    DevModeStatus.Text = "✗";
                    DevModeStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                    DevModeIndicator.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red border to highlight
                }
            }
        }

        private void CheckDecryptionKeysStatus()
        {
            bool hasKeys = HasDecryptionKeys();
            
            if (DecryptKeysStatus != null && DecryptKeysIndicator != null)
            {
                if (hasKeys)
                {
                    DecryptKeysStatus.Text = "✓";
                    DecryptKeysStatus.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)); // Green
                    DecryptKeysIndicator.BorderBrush = new SolidColorBrush(Color.FromRgb(42, 42, 42));
                }
                else
                {
                    DecryptKeysStatus.Text = "✗";
                    DecryptKeysStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                    DecryptKeysIndicator.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red border to highlight
                }
            }
        }

        private bool IsDeveloperModeEnabled()
        {
            try
            {
                // Check both possible registry locations
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"))
                {
                    if (key != null)
                    {
                        var value = key.GetValue("AllowDevelopmentWithoutDevLicense");
                        if (value != null && (int)value == 1)
                        {
                            Debug.WriteLine("Developer Mode: Enabled (AllowDevelopmentWithoutDevLicense = 1)");
                            return true;
                        }
                    }
                }
                
                // Also check AllowAllTrustedApps as fallback
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"))
                {
                    if (key != null)
                    {
                        var value = key.GetValue("AllowAllTrustedApps");
                        if (value != null && (int)value == 1)
                        {
                            Debug.WriteLine("Developer Mode: Enabled (AllowAllTrustedApps = 1)");
                            return true;
                        }
                    }
                }
                
                Debug.WriteLine("Developer Mode: Disabled");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Developer Mode check failed: {ex.Message}");
            }
            return false;
        }

        private bool HasDecryptionKeys()
        {
            try
            {
                // Check if Minecraft license/keys exist by looking for the actual decryption key storage
                // Keys are stored in: C:\ProgramData\Microsoft\Windows\AppRepository\Packages\Microsoft.MinecraftUWP_*
                string appRepoPath = @"C:\ProgramData\Microsoft\Windows\AppRepository\Packages";
                
                if (Directory.Exists(appRepoPath))
                {
                    var minecraftDirs = Directory.GetDirectories(appRepoPath, "Microsoft.MinecraftUWP_*");
                    if (minecraftDirs.Length > 0)
                    {
                        Debug.WriteLine($"Decryption Keys: Found {minecraftDirs.Length} Minecraft package(s) in AppRepository");
                        return true;
                    }
                }
                
                // Alternative check: Look for installed Minecraft package
                var packageManager = new Windows.Management.Deployment.PackageManager();
                var packages = packageManager.FindPackagesForUser("");
                
                foreach (var package in packages)
                {
                    if (package.Id.FamilyName == "Microsoft.MinecraftUWP_8wekyb3d8bbwe")
                    {
                        Debug.WriteLine($"Decryption Keys: Found installed Minecraft package - {package.Id.FullName}");
                        return true;
                    }
                }
                
                Debug.WriteLine("Decryption Keys: Not found");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Decryption Keys check failed: {ex.Message}");
            }
            return false;
        }

        private void DevModeIndicator_Click(object sender, MouseButtonEventArgs e)
        {
            if (!IsDeveloperModeEnabled())
            {
                var result = MessageBox.Show(
                    Localization.Get("DevModeRequiredMessage"),
                    Localization.Get("DevModeRequired"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);
                
                if (result == MessageBoxResult.OK)
                {
                    EnableDeveloperMode();
                }
            }
        }

        private void DecryptKeysIndicator_Click(object sender, MouseButtonEventArgs e)
        {
            if (!HasDecryptionKeys())
            {
                var result = MessageBox.Show(
                    Localization.Get("DecryptKeysRequiredMessage"),
                    Localization.Get("DecryptKeysRequired"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);
                
                if (result == MessageBoxResult.OK)
                {
                    // Open Microsoft Store to Minecraft page
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "ms-windows-store://pdp/?ProductId=9NBLGGH2JHXJ",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to open Store: {ex.Message}");
                    }
                }
            }
        }

        private void EnableDeveloperMode()
        {
            try
            {
                // Try to enable Developer Mode via registry
                // This requires admin privileges
                var psi = new ProcessStartInfo
                {
                    FileName = "reg",
                    Arguments = "add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock\" /v AllowDevelopmentWithoutDevLicense /t REG_DWORD /d 1 /f",
                    Verb = "runas", // Request admin elevation
                    UseShellExecute = true,
                    CreateNoWindow = true
                };
                
                var process = Process.Start(psi);
                process.WaitForExit();
                
                if (process.ExitCode == 0)
                {
                    MessageBox.Show(
                        Localization.Get("DevModeEnabledMessage"),
                        Localization.Get("DevModeEnabled"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    
                    // Update status indicator
                    CheckDeveloperModeStatus();
                }
                else
                {
                    ShowDevModeManualInstructions();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to enable Developer Mode: {ex.Message}");
                ShowDevModeManualInstructions();
            }
        }

        private void ShowDevModeManualInstructions()
        {
            MessageBox.Show(
                Localization.Get("DevModeEnableFailedMessage"),
                Localization.Get("DevModeEnableFailed"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // Native Windows mouse scrolling support
        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer scrollViewer = sender as ScrollViewer;
            if (scrollViewer != null)
            {
                double scrollAmount = e.Delta * 0.5; // Adjust multiplier for scroll speed
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - scrollAmount);
                e.Handled = true;
            }
        }

        // Auto-scroll support - Middle mouse button
        private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && e.ButtonState == MouseButtonState.Pressed)
            {
                ScrollViewer scrollViewer = sender as ScrollViewer;
                if (scrollViewer != null && !_isAutoScrolling)
                {
                    _isAutoScrolling = true;
                    _autoScrollViewer = scrollViewer;
                    _autoScrollStartPoint = e.GetPosition(scrollViewer);
                    
                    // Create and show scroll indicator
                    CreateAutoScrollIndicator(scrollViewer);
                    
                    // Capture mouse
                    scrollViewer.CaptureMouse();
                    
                    // Start auto-scroll timer
                    _autoScrollTimer.Start();
                    
                    e.Handled = true;
                }
            }
        }

        private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && _isAutoScrolling)
            {
                StopAutoScroll();
                e.Handled = true;
            }
        }

        private void ScrollViewer_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isAutoScrolling && _autoScrollViewer != null)
            {
                // Update indicator position if needed
                Point currentPos = e.GetPosition(_autoScrollViewer);
                UpdateAutoScrollIndicator(currentPos);
            }
        }

        private void CreateAutoScrollIndicator(ScrollViewer scrollViewer)
        {
            // Create visual indicator for auto-scroll
            _autoScrollIndicator = new Ellipse
            {
                Width = 40,
                Height = 40,
                Fill = new SolidColorBrush(Color.FromArgb(180, 74, 74, 74)),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                StrokeThickness = 2,
                IsHitTestVisible = false
            };

            // Add directional arrows
            var canvas = new Canvas
            {
                Width = 40,
                Height = 40,
                IsHitTestVisible = false
            };

            canvas.Children.Add(_autoScrollIndicator);

            // Add arrow indicators
            var upArrow = new ShapePath
            {
                Data = Geometry.Parse("M 20,12 L 16,18 L 24,18 Z"),
                Fill = Brushes.White,
                IsHitTestVisible = false
            };
            var downArrow = new ShapePath
            {
                Data = Geometry.Parse("M 20,28 L 16,22 L 24,22 Z"),
                Fill = Brushes.White,
                IsHitTestVisible = false
            };

            canvas.Children.Add(upArrow);
            canvas.Children.Add(downArrow);

            // Position the indicator
            Canvas.SetLeft(canvas, _autoScrollStartPoint.X - 20);
            Canvas.SetTop(canvas, _autoScrollStartPoint.Y - 20);
            Canvas.SetZIndex(canvas, 9999);

            // Add to the scroll viewer's parent grid
            var grid = scrollViewer.Parent as Grid;
            if (grid != null)
            {
                grid.Children.Add(canvas);
                _autoScrollIndicator.Tag = canvas; // Store reference for removal
            }
        }

        private void UpdateAutoScrollIndicator(Point currentPos)
        {
            // Visual feedback could be added here if needed
        }

        private void AutoScrollTimer_Tick(object sender, EventArgs e)
        {
            if (!_isAutoScrolling || _autoScrollViewer == null)
            {
                StopAutoScroll();
                return;
            }

            try
            {
                Point currentPos = Mouse.GetPosition(_autoScrollViewer);
                double deltaY = currentPos.Y - _autoScrollStartPoint.Y;
                
                // Calculate scroll speed based on distance from start point
                double scrollSpeed = 0;
                double deadZone = 10; // Pixels of dead zone around start point
                
                if (Math.Abs(deltaY) > deadZone)
                {
                    // Exponential scaling for more natural feel
                    scrollSpeed = Math.Sign(deltaY) * Math.Pow(Math.Abs(deltaY) - deadZone, 1.2) * 0.05;
                    
                    // Clamp maximum speed
                    scrollSpeed = Math.Max(-50, Math.Min(50, scrollSpeed));
                }

                if (scrollSpeed != 0)
                {
                    double newOffset = _autoScrollViewer.VerticalOffset + scrollSpeed;
                    _autoScrollViewer.ScrollToVerticalOffset(newOffset);
                }
            }
            catch
            {
                StopAutoScroll();
            }
        }

        private void StopAutoScroll()
        {
            _isAutoScrolling = false;
            _autoScrollTimer.Stop();

            if (_autoScrollViewer != null)
            {
                _autoScrollViewer.ReleaseMouseCapture();
                
                // Remove indicator
                if (_autoScrollIndicator != null && _autoScrollIndicator.Tag is Canvas canvas)
                {
                    var grid = _autoScrollViewer.Parent as Grid;
                    if (grid != null && grid.Children.Contains(canvas))
                    {
                        grid.Children.Remove(canvas);
                    }
                }
                
                _autoScrollViewer = null;
            }

            _autoScrollIndicator = null;
        }

        private void GoToAvailable_Click(object sender, RoutedEventArgs e)
        {
            AvailableTab.IsChecked = true;
        }

        // Filter and Search
        private void FilterChanged(object sender, RoutedEventArgs e)
        {
            if (_allVersions == null || _allVersions.Count == 0)
                return;
                
            ApplyFilters();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_searchDebounceTimer != null)
            {
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
            }
        }

        private void SearchDebounceTimer_Tick(object sender, EventArgs e)
        {
            _searchDebounceTimer?.Stop();
            ApplyFilters();
        }

        private async void ApplyFilters()
        {
            if (_allVersions == null || _allVersions.Count == 0)
                return;

            var searchText = SearchBox.Text?.ToLower() ?? "";
            VersionType? filterType = null;

            if (FilterRelease.IsChecked == true)
                filterType = VersionType.Release;
            else if (FilterPreview.IsChecked == true)
                filterType = VersionType.Preview;

            var filteredList = await Task.Run(() =>
            {
                var list = new List<ModernVersionViewModel>();
                foreach (var v in _allVersions)
                {
                    if (filterType.HasValue && v.Version.VersionType != filterType.Value)
                        continue;
                    if (!string.IsNullOrWhiteSpace(searchText) && !v.Version.DisplayName.ToLower().Contains(searchText))
                        continue;
                    list.Add(v);
                }
                return list;
            });

            AvailableVersionsList.ItemsSource = filteredList;
        }

        private async void RefreshVersions_Click(object sender, RoutedEventArgs e)
        {
            await LoadVersionList();
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDlg = new Microsoft.Win32.OpenFileDialog();
            openFileDlg.Filter = "Minecraft Packages (*.msixvc, *.appx)|*.msixvc;*.appx|All Files|*.*";
            openFileDlg.Title = "Choose a Minecraft package file";
            
            Nullable<bool> result = openFileDlg.ShowDialog();
            if (result == true)
            {
                string directory = Path.Combine(IMPORTED_VERSIONS_PATH, openFileDlg.SafeFileName);
                
                // Check if already exists
                if (Directory.Exists(directory))
                {
                    var existingVersion = _versions.FirstOrDefault(v => v.IsImported && v.GameDirectory == directory);
                    if (existingVersion != null)
                    {
                        if (existingVersion.IsStateChanging)
                        {
                            ShowFriendlyError(Localization.Get("PleaseWait"), Localization.Get("PleaseWaitMessage"));
                            return;
                        }

                        var confirmResult = MessageBox.Show(
                            Localization.Get("ReplaceExistingMessage"),
                            Localization.Get("ReplaceExisting"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (confirmResult == MessageBoxResult.Yes)
                        {
                            var removeResult = await RemoveVersion(existingVersion);
                            if (!removeResult)
                            {
                                ShowFriendlyError(Localization.Get("CouldntRemoveOld"), Localization.Get("CouldntRemoveOldMessage"));
                                return;
                            }
                        }
                        else
                        {
                            return;
                        }
                    }
                }

                var extension = Path.GetExtension(openFileDlg.FileName).ToLowerInvariant();
                PackageType packageType;
                
                if (extension == ".msixvc")
                {
                    packageType = PackageType.GDK;
                }
                else if (extension == ".appx")
                {
                    packageType = PackageType.UWP;
                }
                else
                {
                    ShowFriendlyError(Localization.Get("WrongFileType"), Localization.Format("WrongFileTypeMessage", extension));
                    return;
                }

                var versionEntry = _versions.AddEntry(openFileDlg.SafeFileName, directory, packageType);
                
                bool success = false;

                if (packageType == PackageType.UWP)
                {
                    success = await ExtractAppx(openFileDlg.FileName, directory, versionEntry);
                }
                else if (packageType == PackageType.GDK)
                {
                    if (!ShowGDKFirstUseWarning())
                    {
                        success = false;
                    }
                    else
                    {
                        success = await ExtractMsixvc(openFileDlg.FileName, directory, versionEntry, isPreview: false);
                    }
                }

                if (success)
                {
                    versionEntry.StateChangeInfo = null;
                    versionEntry.UpdateInstallStatus();
                    ShowFriendlySuccess(Localization.Get("VersionAdded"), Localization.Format("VersionAddedMessage", versionEntry.DisplayName));
                    MyVersionsTab.IsChecked = true;
                }
                else
                {
                    _versions.Remove(versionEntry);
                }
            }
        }

        // Helper methods from original MainWindow
        private void ShowFriendlyError(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ShowFriendlySuccess(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private bool ShowGDKFirstUseWarning()
        {
            if (!UserPrefs.HasPreviouslyUsedGDK)
            {
                var result = MessageBox.Show(
                    Localization.Get("GDKWarningMessage"),
                    Localization.Get("FirstTimeSetup"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.OK)
                {
                    UserPrefs.HasPreviouslyUsedGDK = true;
                    RewritePrefs();
                    return true;
                }
                return false;
            }
            return true;
        }

        private void RewritePrefs()
        {
            File.WriteAllText(PREFS_PATH, JsonConvert.SerializeObject(UserPrefs, Formatting.Indented));
        }

        // ICommonVersionCommands implementation
        public ICommand LaunchCommand => new RelayCommand((v) => InvokeLaunch((WPFDataTypes.Version)v));
        public ICommand RemoveCommand => new RelayCommand((v) => InvokeRemove((WPFDataTypes.Version)v));
        public ICommand DownloadCommand => new RelayCommand((v) => InvokeDownload((WPFDataTypes.Version)v));
        public ICommand PauseResumeCommand => new RelayCommand((v) => InvokePauseResume((WPFDataTypes.Version)v));

        private void InvokePauseResume(WPFDataTypes.Version v)
        {
            if (!v.IsStateChanging || v.StateChangeInfo == null)
                return;

            if (v.StateChangeInfo.IsPaused)
            {
                // Resume download
                Debug.WriteLine("Resuming download for: " + v.DisplayName);
                v.StateChangeInfo.IsPaused = false;
                InvokeDownload(v);
            }
            else
            {
                // Pause download by canceling it (file will be kept for resume)
                Debug.WriteLine("Pausing download for: " + v.DisplayName);
                v.StateChangeInfo.IsPaused = true;
                if (_downloadCancelTokens.ContainsKey(v))
                {
                    _downloadCancelTokens[v].Cancel();
                }
            }
        }

        private void InvokeLaunch(WPFDataTypes.Version v)
        {
            if (_hasLaunchTask)
                return;

            _hasLaunchTask = true;
            Task.Run(async () =>
            {
                try
                {
                    v.StateChangeInfo = new VersionStateChangeInfo(VersionState.MovingData);
                    if (!MoveMinecraftData(v.GamePackageFamily, v.PackageType))
                    {
                        Debug.WriteLine("Data restore error, aborting launch");
                        v.StateChangeInfo = null;
                        _hasLaunchTask = false;
                        Dispatcher.Invoke(() =>
                        {
                            ShowFriendlyError(Localization.Get("CouldntPrepareWorlds"),
                                Localization.Get("CouldntPrepareWorldsMessage"));
                        });
                        return;
                    }

                    v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Registering);
                    string gameDir = Path.GetFullPath(v.GameDirectory);
                    try
                    {
                        await ReRegisterPackage(v.GamePackageFamily, gameDir, v);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine("App re-register failed:\n" + e.ToString());
                        Dispatcher.Invoke(() =>
                        {
                            ShowFriendlyError(Localization.Get("CouldntRegister"),
                                Localization.Get("CouldntRegisterMessage"));
                        });
                        _hasLaunchTask = false;
                        v.StateChangeInfo = null;
                        return;
                    }

                    v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Launching);
                    try
                    {
                        if (v.PackageType == PackageType.GDK)
                        {
                            await Task.Run(() => 
                            {
                                var psi = new ProcessStartInfo
                                {
                                    FileName = Path.Combine(gameDir, "Minecraft.Windows.exe"),
                                    WorkingDirectory = gameDir,
                                    UseShellExecute = false
                                };
                                Process.Start(psi);
                            });
                        }
                        else
                        {
                            var pkg = await Windows.System.AppDiagnosticInfo.RequestInfoForPackageAsync(v.GamePackageFamily);
                            if (pkg.Count > 0)
                            {
                                if (pkg.Count > 1)
                                {
                                    Debug.WriteLine("Multiple packages found");
                                }
                                var result = await pkg[0].LaunchAsync();
                                if (result.ExtendedError != null)
                                {
                                    throw result.ExtendedError;
                                }
                            }
                            else
                            {
                                throw new Exception("No packages found for package family " + v.GamePackageFamily);
                            }
                        }
                        Debug.WriteLine("App launch finished!");
                        
                        Dispatcher.Invoke(() =>
                        {
                            ShowFriendlySuccess(Localization.Get("MinecraftStarting"),
                                Localization.Format("MinecraftStartingMessage", v.DisplayName));
                        });
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine("App launch failed:\n" + e.ToString());
                        Dispatcher.Invoke(() =>
                        {
                            ShowFriendlyError(Localization.Get("CouldntLaunch"),
                                Localization.Get("CouldntLaunchMessage"));
                        });
                    }
                }
                finally
                {
                    _hasLaunchTask = false;
                    v.StateChangeInfo = null;
                }
            });
        }

        private void InvokeRemove(WPFDataTypes.Version v)
        {
            var result = MessageBox.Show(
                Localization.Format("RemoveVersionMessage", v.DisplayName),
                Localization.Get("RemoveVersion"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                Task.Run(async () =>
                {
                    var success = await RemoveVersion(v);
                    if (success)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ShowFriendlySuccess(Localization.Get("VersionRemoved"), Localization.Format("VersionRemovedMessage", v.DisplayName));
                        });
                    }
                });
            }
        }

        private async Task<bool> RemoveVersion(WPFDataTypes.Version v)
        {
            try
            {
                v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Unregistering);
                
                // TODO: Implement full removal logic from original MainWindow
                await Task.Delay(500);

                if (v.IsImported && Directory.Exists(v.GameDirectory))
                {
                    v.StateChangeInfo = new VersionStateChangeInfo(VersionState.CleaningUp);
                    Directory.Delete(v.GameDirectory, true);
                }

                _versions.Remove(v);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Remove failed: " + ex.ToString());
                Dispatcher.Invoke(() =>
                {
                    ShowFriendlyError(Localization.Get("CouldntRemove"), 
                        Localization.Get("CouldntRemoveMessage"));
                });
                return false;
            }
            finally
            {
                v.StateChangeInfo = null;
            }
        }

        private void InvokeDownload(WPFDataTypes.Version v)
        {
            if (v.IsStateChanging && !v.StateChangeInfo?.IsPaused == true)
                return;

            CancellationTokenSource cancelSource = new CancellationTokenSource();
            _downloadCancelTokens[v] = cancelSource;
            
            v.IsNew = false;
            v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Initializing);
            v.StateChangeInfo.CancelCommand = new RelayCommand((o) =>
            {
                cancelSource.Cancel();
                // Delete partial file on cancel
                string versionsFolder = GetVersionsFolder();
                string fileName = (v.VersionType == VersionType.Preview ? "Minecraft-Preview-" : "Minecraft-") + v.Name + (v.PackageType == PackageType.UWP ? ".Appx" : ".msixvc");
                string dlPath = Path.Combine(versionsFolder, fileName);
                try
                {
                    if (File.Exists(dlPath))
                        File.Delete(dlPath);
                }
                catch { }
            });

            Debug.WriteLine("Download start for version: " + v.DisplayName);
            
            Task.Run(async () =>
            {
                // Use AppData versions folder instead of current directory
                string versionsFolder = GetVersionsFolder();
                string fileName = (v.VersionType == VersionType.Preview ? "Minecraft-Preview-" : "Minecraft-") + v.Name + (v.PackageType == PackageType.UWP ? ".Appx" : ".msixvc");
                string dlPath = Path.Combine(versionsFolder, fileName);
                
                VersionDownloader downloader = _anonVersionDownloader;

                VersionDownloader.DownloadProgress dlProgressHandler = (current, total) =>
                {
                    if (v.StateChangeInfo.VersionState != VersionState.Downloading)
                    {
                        Debug.WriteLine("Actual download started");
                        v.StateChangeInfo.VersionState = VersionState.Downloading;
                        if (total.HasValue)
                            v.StateChangeInfo.MaxProgress = total.Value;
                    }
                    v.StateChangeInfo.Progress = current;
                };

                try
                {
                    if (v.PackageType == PackageType.UWP)
                    {
                        await downloader.DownloadAppx(v.UUID, "1", dlPath, dlProgressHandler, cancelSource.Token);
                    }
                    else if (v.PackageType == PackageType.GDK)
                    {
                        if (!ShowGDKFirstUseWarning())
                        {
                            v.StateChangeInfo = null;
                            v.UpdateInstallStatus();
                            return;
                        }
                        await downloader.DownloadMsixvc(v.DownloadURLs, dlPath, dlProgressHandler, cancelSource.Token);
                    }
                    else
                    {
                        throw new Exception("Unknown package type");
                    }
                    Debug.WriteLine("Download complete");
                }
                catch (BadUpdateIdentityException)
                {
                    Debug.WriteLine("Download failed due to failure to fetch download URL");
                    Dispatcher.Invoke(() =>
                    {
                        ShowFriendlyError(Localization.Get("DownloadFailed"),
                            "Unable to fetch download URL for version." +
                            (v.VersionType == VersionType.Beta ? "\nFor beta versions, please make sure your account is subscribed to the Minecraft beta programme in the Xbox Insider Hub app." : ""));
                    });
                    v.StateChangeInfo = null;
                    return;
                }
                catch (Exception e)
                {
                    Debug.WriteLine("Download failed:\n" + e.ToString());
                    if (!(e is TaskCanceledException))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ShowFriendlyError(Localization.Get("DownloadFailed"),
                                Localization.Get("DownloadFailedMessage") + "\n\nError: " + e.Message);
                        });
                    }
                    v.StateChangeInfo = null;
                    return;
                }

                // Extract the downloaded package
                try
                {
                    string dirPath = v.GameDirectory;
                    if (Directory.Exists(dirPath))
                        Directory.Delete(dirPath, true);
                    
                    if (v.PackageType == PackageType.UWP)
                    {
                        await ExtractAppx(dlPath, dirPath, v);
                    }
                    else if (v.PackageType == PackageType.GDK)
                    {
                        await ExtractMsixvc(dlPath, dirPath, v, isPreview: v.VersionType == VersionType.Preview);
                    }
                    else
                    {
                        throw new Exception("Unknown package type");
                    }
                    
                    if (UserPrefs.DeleteAppxAfterDownload)
                    {
                        Debug.WriteLine("Deleting package to reduce disk usage");
                        File.Delete(dlPath);
                    }
                    else
                    {
                        Debug.WriteLine("Not deleting package due to user preferences");
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine("Extraction failed:\n" + e.ToString());
                    Dispatcher.Invoke(() =>
                    {
                        ShowFriendlyError(Localization.Get("ExtractionFailed"),
                            Localization.Get("ExtractionFailedMessage") + "\n\nError: " + e.Message);
                    });
                    v.StateChangeInfo = null;
                    return;
                }

                v.StateChangeInfo = null;
                v.UpdateInstallStatus();

                Dispatcher.Invoke(() =>
                {
                    ShowFriendlySuccess(Localization.Get("DownloadComplete"),
                        Localization.Format("DownloadCompleteMessage", v.DisplayName));
                    RefreshVersionLists();
                });
            });
        }

        private async Task<bool> ExtractAppx(string filePath, string directory, WPFDataTypes.Version versionEntry)
        {
            versionEntry.StateChangeInfo = new VersionStateChangeInfo(VersionState.Extracting);
            try
            {
                await Task.Run(() =>
                {
                    string fullDestDir = Path.GetFullPath(directory);
                    if (!fullDestDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    {
                        fullDestDir += Path.DirectorySeparatorChar;
                    }
                    
                    using (var archive = System.IO.Compression.ZipFile.OpenRead(filePath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            string destPath = Path.GetFullPath(Path.Combine(directory, entry.FullName));
                            if (!destPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new IOException("Zip Slip vulnerability detected: " + entry.FullName);
                            }
                            if (string.IsNullOrEmpty(entry.Name))
                            {
                                Directory.CreateDirectory(destPath);
                            }
                            else
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                                entry.ExtractToFile(destPath, true);
                            }
                        }
                    }
                    string signaturePath = Path.Combine(directory, "AppxSignature.p7x");
                    if (File.Exists(signaturePath))
                        File.Delete(signaturePath);
                });

                versionEntry.UpdateInstallStatus();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Extract failed: " + ex.ToString());
                ShowFriendlyError(Localization.Get("ExtractionFailed"), 
                    Localization.Get("ExtractionFailedMessage"));
                return false;
            }
            finally
            {
                versionEntry.StateChangeInfo = null;
            }
        }

        private void RecursiveCopyDirectory(string from, string to, HashSet<string> skip)
        {
            Directory.CreateDirectory(to);
            foreach (var source in Directory.EnumerateFiles(from))
            {
                if (skip.Contains(source))
                {
                    continue;
                }
                string destination = Path.Combine(to, Path.GetFileName(source));
                Debug.WriteLine(source + " -> " + destination);
                File.Copy(source, destination);
            }
            foreach (var source in Directory.EnumerateDirectories(from))
            {
                string destination = Path.Combine(to, Path.GetFileName(source));
                RecursiveCopyDirectory(source, destination, skip);
            }
        }

        private async Task<bool> ExtractMsixvc(string filePath, string directory, WPFDataTypes.Version versionEntry, bool isPreview)
        {
            if (_hasGdkExtractTask)
            {
                ShowFriendlyError(
                    "Concurrent installation",
                    "Can't install multiple MSIXVC packages at the same time. Please wait for the current installation to finish before starting a new one.");
                return false;
            }
            _hasGdkExtractTask = true;
            try
            {
                Debug.WriteLine("=== ExtractMsixvc Debug Info ===");
                Debug.WriteLine($"File path: {filePath}");
                Debug.WriteLine($"File exists: {File.Exists(filePath)}");
                Debug.WriteLine($"File size: {(File.Exists(filePath) ? new FileInfo(filePath).Length : 0)} bytes");
                Debug.WriteLine($"Destination directory: {directory}");
                Debug.WriteLine($"Package family: {versionEntry.GamePackageFamily}");
                Debug.WriteLine($"Is Preview: {isPreview}");
                
                directory = Path.GetFullPath(directory);
                versionEntry.StateChangeInfo = new VersionStateChangeInfo(VersionState.Staging);

                var packageManager = new Windows.Management.Deployment.PackageManager();

                Debug.WriteLine("Step 1: Clearing existing XboxGames Minecraft installation");
                try
                {
                    await UnregisterPackage(versionEntry.GamePackageFamily, versionEntry, skipBackup: false);
                    Debug.WriteLine("Step 1: Complete - Existing packages cleared");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Step 1: FAILED - {ex.GetType().Name}: {ex.Message}");
                    Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                    ShowFriendlyError(
                        "Failed clearing XboxGames",
                        "The existing XboxGames Minecraft installation could not be removed. Please make sure Minecraft is not running and try again.\n\nError: " + ex.Message);
                    return false;
                }

                Debug.WriteLine("Step 2: Staging MSIXVC package");
                Debug.WriteLine($"Staging URI: {new Uri(filePath).AbsoluteUri}");
                try
                {
                    await DeploymentProgressWrapper(packageManager.StagePackageAsync(new Uri(filePath), null), versionEntry);
                    Debug.WriteLine("Step 2: Complete - Package staged successfully");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Step 2: FAILED - {ex.GetType().Name}: {ex.Message}");
                    Debug.WriteLine($"HRESULT: {ex.HResult:X8}");
                    Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                    if (ex.InnerException != null)
                    {
                        Debug.WriteLine($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    }
                    ShowFriendlyError(
                        "Failed to stage package",
                        $"Failed to stage package.\n" +
                            $"This may mean that the file is damaged, not an MSIXVC file. Please check the integrity of the file.\n\n" +
                            $"However, this error might also happen if you've never installed a GDK version of Minecraft from the Store before,\n" +
                            $"as the launcher relies on the Store to install the keys needed to decrypt the installation package.\n" +
                            $"Please ensure that you've installed " + (isPreview ? "Minecraft Preview" : "Minecraft") + $" from the Store before installing GDK versions using the launcher.\n\n" +
                            $"Error: {ex.Message}\n" +
                            $"HRESULT: 0x{ex.HResult:X8}\n\n" +
                            $"Check Log.txt for detailed debug information.");
                    return false;
                }

                Debug.WriteLine("Step 3: Finding staged package location");
                string installPath = "";
                foreach (var pkg in new Windows.Management.Deployment.PackageManager().FindPackages(versionEntry.GamePackageFamily))
                {
                    Debug.WriteLine($"Found package: {pkg.Id.FullName} at {pkg.InstalledLocation?.Path ?? "NULL"}");
                    if (installPath != "")
                    {
                        ShowFriendlyError(
                            "Multiple locations found",
                            "Minecraft is installed in multiple places, and the launcher doesn't know where to copy files from.\n" +
                            "This is probably because another user has the game installed.");
                        return false;
                    }
                    installPath = pkg.InstalledLocation.Path;
                }
                Debug.WriteLine("Detected staging path: " + installPath);
                string resolvedPath = LinkResolver.Resolve(installPath);
                Debug.WriteLine("Symlink resolved as " + resolvedPath);
                installPath = resolvedPath;

                var exeSrcPath = Path.Combine(installPath, "Minecraft.Windows.exe");
                if (!Directory.Exists(installPath))
                {
                    ShowFriendlyError(
                        "Installation directory not found",
                        "Didn't find installation expected at " + installPath + "\nMaybe your XboxGames folder is in a different location?");
                    return false;
                }
                if (!File.Exists(exeSrcPath))
                {
                    ShowFriendlyError(
                        "Minecraft executable not found",
                        "Didn't find Minecraft executable at " + exeSrcPath);
                    return false;
                }

                versionEntry.StateChangeInfo.VersionState = VersionState.Decrypting;

                var exeTmpDir = Path.GetFullPath(@"tmp");
                if (!Directory.Exists(exeTmpDir))
                {
                    try
                    {
                        Directory.CreateDirectory(exeTmpDir);
                    }
                    catch (IOException ex)
                    {
                        ShowFriendlyError(
                            "Failed to create tmp dir",
                            "The temporary directory for extracting the Minecraft executable could not be created at " + exeTmpDir + "\n\nError: " + ex.Message);
                        return false;
                    }
                }
                var uuid = Guid.NewGuid().ToString();
                var exeTmpPath = Path.Combine(exeTmpDir, "Minecraft.Windows_" + uuid + ".exe");
                var exePartialTmpPath = exeTmpPath + ".tmp";

                var exeDstPath = Path.Combine(Path.GetFullPath(directory), "Minecraft.Windows.exe");

                // Prevent Command Injection by encoding the inner PowerShell payload
                string innerPayload = $"Copy-Item -LiteralPath '{exeSrcPath.Replace("'", "''")}' -Destination '{exePartialTmpPath.Replace("'", "''")}' -Force; Move-Item -LiteralPath '{exePartialTmpPath.Replace("'", "''")}' -Destination '{exeTmpPath.Replace("'", "''")}'";
                string encodedInner = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(innerPayload));
                
                string outerPayload = $"Invoke-CommandInDesktopPackage -PackageFamilyName '{versionEntry.GamePackageFamily.Replace("'", "''")}' -App Game -Command 'powershell.exe' -Args '-WindowStyle Hidden -EncodedCommand {encodedInner}'";
                string encodedOuter = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(outerPayload));

                Debug.WriteLine("Decrypt command (encoded): " + outerPayload);

                var processInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedOuter}",
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };

                Debug.WriteLine("Copying decrypted exe");
                try
                {
                    var process = Process.Start(processInfo);
                    process.WaitForExit();
                    Debug.WriteLine("Process output:" + process.StandardOutput.ReadToEnd());
                    Debug.WriteLine("Process errors:" + process.StandardError.ReadToEnd());
                }
                catch (Exception ex)
                {
                    ShowFriendlyError(
                        "PowerShell failed",
                        "Failed to run PowerShell to copy the Minecraft executable out of the staged package\n\nError: " + ex.Message);
                    return false;
                }

                for (int i = 0; i < 300 && !File.Exists(exeTmpPath); i++)
                {
                    await Task.Delay(100);
                }

                if (!File.Exists(exeTmpPath))
                {
                    Debug.WriteLine("Src path: " + exeSrcPath);
                    Debug.WriteLine("Tmp path: " + exeTmpPath);
                    ShowFriendlyError(
                        "Exe extraction failed",
                        "The Minecraft executable could not be copied out of the staged package.\n" +
                            "This is usually due to the game license not being installed for your Windows user account.\n\n" +
                            "Please ensure that you've installed " + (isPreview ? "Minecraft Preview" : "Minecraft") + " from the Store before using this launcher.");
                    return false;
                }
                Debug.WriteLine("Minecraft executable decrypted successfully");

                versionEntry.StateChangeInfo.VersionState = VersionState.Moving;
                try
                {
                    Debug.WriteLine("Moving staged files: " + installPath + " -> " + directory);
                    if (Path.GetPathRoot(installPath) == Path.GetPathRoot(directory))
                    {
                        Debug.WriteLine("Destination for extraction is on the same drive as the installation location - moving files for speed");
                        Directory.Move(installPath, directory);
                    }
                    else
                    {
                        Debug.WriteLine("Destination for extraction is on a different drive than staged - copying files");
                        HashSet<string> skip = new HashSet<string>();
                        skip.Add(exeSrcPath);
                        RecursiveCopyDirectory(installPath, directory, skip);
                    }

                    Debug.WriteLine("Moving decrypted exe into place");
                    File.Delete(exeDstPath);
                    File.Move(exeTmpPath, exeDstPath);
                }
                catch (Exception ex)
                {
                    ShowFriendlyError(
                        "Failed moving game files",
                        "Failed copying/moving game files to the destination folder\n\nError: " + ex.Message);
                    return false;
                }

                Debug.WriteLine("Cleaning up XboxGames");
                await UnregisterPackage(versionEntry.GamePackageFamily, versionEntry, skipBackup: true);

                Debug.WriteLine("Done importing msixvc: " + filePath);
                return true;
            }
            finally
            {
                _hasGdkExtractTask = false;
            }
        }

        private void FixGDKManifest(string path)
        {
            XDocument doc = XDocument.Load(path);
            XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            XNamespace rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

            var apps = doc.Descendants(ns + "Application");
            foreach (var app in apps)
            {
                var executable = app.Attribute("Executable");
                if (executable != null && executable.Value == "GameLaunchHelper.exe")
                {
                    executable.Value = "Minecraft.Windows.exe";
                }
            }

            var extensions = doc.Root.Elements(ns + "Extensions").ToList();
            foreach (var ext in extensions)
            {
                ext.Remove();
            }

            var capabilities = doc.Descendants(ns + "Capabilities");
            var customInstall = capabilities
                .Elements(rescap + "Capability")
                .Where(c => c.Attribute("Name")?.Value == "customInstallActions")
                .ToList();
            foreach (var cap in customInstall)
            {
                cap.Remove();
            }

            doc.Save(path);
        }

        private async Task DeploymentProgressWrapper(Windows.Foundation.IAsyncOperationWithProgress<Windows.Management.Deployment.DeploymentResult, Windows.Management.Deployment.DeploymentProgress> t, WPFDataTypes.Version version)
        {
            TaskCompletionSource<int> src = new TaskCompletionSource<int>();
            t.Progress += (v, p) =>
            {
                Debug.WriteLine("Deployment progress: " + p.state + " " + p.percentage + "%");
            };
            t.Completed += (v, p) =>
            {
                if (p == Windows.Foundation.AsyncStatus.Error)
                {
                    Debug.WriteLine("Deployment failed: " + v.GetResults().ErrorText + " (error code " + v.GetResults().ExtendedErrorCode.HResult + ")");
                    src.SetException(new Exception("Deployment failed: " + v.GetResults().ErrorText));
                }
                else
                {
                    Debug.WriteLine("Deployment done: " + p);
                    src.SetResult(1);
                }
            };
            await src.Task;
        }

        private string GetPackagePath(Windows.ApplicationModel.Package pkg)
        {
            try
            {
                return pkg.InstalledLocation.Path;
            }
            catch (FileNotFoundException)
            {
                return "";
            }
        }

        private async Task RemovePackage(Windows.ApplicationModel.Package pkg, string packageFamily, WPFDataTypes.Version version, bool skipBackup)
        {
            Debug.WriteLine("Removing package: " + pkg.Id.FullName);
            if (!pkg.IsDevelopmentMode)
            {
                if (!skipBackup)
                {
                    if (!BackupMinecraftDataForRemoval(packageFamily))
                    {
                        throw new Exception("Failed backing up Minecraft data before uninstalling package");
                    }
                }
                await DeploymentProgressWrapper(new Windows.Management.Deployment.PackageManager().RemovePackageAsync(pkg.Id.FullName, Windows.Management.Deployment.RemovalOptions.RemoveForAllUsers), version);
            }
            else
            {
                Debug.WriteLine("Package is in development mode");
                await DeploymentProgressWrapper(new Windows.Management.Deployment.PackageManager().RemovePackageAsync(pkg.Id.FullName, Windows.Management.Deployment.RemovalOptions.PreserveApplicationData | Windows.Management.Deployment.RemovalOptions.RemoveForAllUsers), version);
            }
            Debug.WriteLine("Removal of package done: " + pkg.Id.FullName);
        }

        private bool BackupMinecraftDataForRemoval(string packageFamily)
        {
            Windows.Storage.ApplicationData data;
            try
            {
                data = Windows.Management.Core.ApplicationDataManager.CreateForPackageFamily(packageFamily);
            }
            catch (FileNotFoundException e)
            {
                Debug.WriteLine("BackupMinecraftDataForRemoval: Application data not found for package family " + packageFamily + ": " + e.ToString());
                Debug.WriteLine("This should mean the package isn't installed, so we don't need to backup the data");
                return true;
            }
            if (!Directory.Exists(data.LocalFolder.Path))
            {
                Debug.WriteLine("LocalState folder " + data.LocalFolder.Path + " doesn't exist, so it can't be backed up");
                return true;
            }
            string tmpDir = GetBackupMinecraftDataDir();
            if (Directory.Exists(tmpDir))
            {
                if (GetWorldCountInDataDir(tmpDir) > 0)
                {
                    Debug.WriteLine("BackupMinecraftDataForRemoval error: " + tmpDir + " already exists");
                    Process.Start("explorer.exe", tmpDir);
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show("The temporary directory for backing up MC data already exists. This probably means that we failed last time backing up the data. Please back the directory up manually.");
                    });
                    return false;
                }
                Directory.Delete(tmpDir, recursive: true);
            }
            Debug.WriteLine("Moving Minecraft data to: " + tmpDir);
            Directory.Move(data.LocalFolder.Path, tmpDir);

            return true;
        }

        private int GetWorldCountInDataDir(string dataDir)
        {
            var worldsFolder = Path.Combine(dataDir, "games", "com.mojang", "minecraftWorlds");
            if (!Directory.Exists(worldsFolder))
            {
                return 0;
            }
            return Directory.GetDirectories(worldsFolder).Length;
        }

        private async Task UnregisterPackage(string packageFamily, WPFDataTypes.Version version, bool skipBackup)
        {
            foreach (var pkg in new Windows.Management.Deployment.PackageManager().FindPackages(packageFamily))
            {
                string location = GetPackagePath(pkg);
                Debug.WriteLine("Removing package: " + pkg.Id.FullName + " " + location);
                await RemovePackage(pkg, packageFamily, version, skipBackup);
            }
        }

        private async Task ReRegisterPackage(string packageFamily, string gameDir, WPFDataTypes.Version version)
        {
            foreach (var pkg in new Windows.Management.Deployment.PackageManager().FindPackages(packageFamily))
            {
                string location = GetPackagePath(pkg);
                if (location == gameDir)
                {
                    Debug.WriteLine("Skipping package removal - same path: " + pkg.Id.FullName + " " + location);
                    return;
                }
                await RemovePackage(pkg, packageFamily, version, skipBackup: false);
            }
            Debug.WriteLine("Registering package");
            string manifestPath = Path.Combine(gameDir, "AppxManifest.xml");

            if (version.PackageType == PackageType.GDK)
            {
                string originalPath = Path.Combine(gameDir, "AppxManifest_original.xml");
                if (!File.Exists(originalPath))
                {
                    File.Copy(manifestPath, originalPath);
                    FixGDKManifest(manifestPath);
                }
            }
            Debug.WriteLine("Manifest path: " + manifestPath);
            await DeploymentProgressWrapper(new Windows.Management.Deployment.PackageManager().RegisterPackageAsync(new Uri(manifestPath), null, Windows.Management.Deployment.DeploymentOptions.DevelopmentMode), version);
            Debug.WriteLine("App re-register done!");
        }

        // Helper methods for launch functionality
        private bool MoveMinecraftData(string packageFamily, PackageType destinationType)
        {
            var dataLocations = LocateMinecraftWorlds(packageFamily);
            if (dataLocations.Count == 0)
            {
                Debug.WriteLine("No Minecraft data found to restore or link");
                return true;
            }

            string gdkRoot = GetMinecraftGDKRootDir(packageFamily);
            string uwpDataDir = GetMinecraftUWPDataDir(packageFamily);

            if (dataLocations.Count > 1)
            {
                var messageString = "";
                foreach (var loc in dataLocations)
                {
                    messageString += $"\n - {loc.Key}: {loc.Value} worlds";
                }

                if (destinationType == PackageType.GDK)
                {
                    bool gdkOnly = true;
                    foreach (var loc in dataLocations)
                    {
                        if (!loc.Key.StartsWith(gdkRoot))
                        {
                            gdkOnly = false;
                            break;
                        }
                    }

                    if (gdkOnly)
                    {
                        Debug.WriteLine("Worlds found in multiple GDK locations, this is fine");
                        return true;
                    }
                }

                Debug.WriteLine("Multiple world locations found:" + messageString);
                string destinationFolder = destinationType == PackageType.UWP ? uwpDataDir : Path.Combine(gdkRoot, "Users");
                
                var result = MessageBox.Show(
                    "Worlds were found in multiple locations:\n" + messageString +
                    "\n\nThe version will look for worlds in: " + destinationFolder +
                    "\n\nSome worlds may not be visible. Continue anyway?",
                    "Multiple World Locations",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);
                return result == MessageBoxResult.OK;
            }

            string dataLocation = dataLocations.Keys.First();
            string tmpDir = GetBackupMinecraftDataDir();
            string uwpParent = GetMinecraftUWPRootDir(packageFamily);

            if (dataLocation == tmpDir)
            {
                Debug.WriteLine("Restoring from backup");
                if (!RestoreUWPData(tmpDir, uwpDataDir, uwpParent))
                {
                    return false;
                }
                dataLocation = uwpDataDir;
            }

            if (destinationType == PackageType.GDK && dataLocation == uwpDataDir)
            {
                Debug.WriteLine("Preparing GDK migration from UWP");
                var uwpMigrationDat = Path.Combine(gdkRoot, "games", "com.mojang", "uwpMigration.dat");
                try
                {
                    if (File.Exists(uwpMigrationDat))
                    {
                        File.Delete(uwpMigrationDat);
                    }
                    return true;
                }
                catch (Exception e)
                {
                    Debug.WriteLine("Failed deleting uwpMigration.dat: " + e.ToString());
                    return true; // Continue anyway
                }
            }
            else if (destinationType == PackageType.UWP && dataLocation != uwpDataDir)
            {
                var gdkDataDir = dataLocations.Keys.First();
                if (!RestoreUWPData(gdkDataDir, uwpDataDir, uwpParent))
                {
                    return false;
                }
                return true;
            }

            Debug.WriteLine("Data already in correct location");
            return true;
        }

        private Dictionary<string, int> LocateMinecraftWorlds(string packageFamily)
        {
            List<string> candidates = new List<string>();

            var uwpDataDir = GetMinecraftUWPDataDir(packageFamily);
            if (uwpDataDir != "")
            {
                candidates.Add(uwpDataDir);
            }

            candidates.AddRange(GetMinecraftGDKDataDirs(packageFamily));
            candidates.Add(GetBackupMinecraftDataDir());

            var worldLocations = new Dictionary<string, int>();
            foreach (var dataDir in candidates)
            {
                var worldsFolder = Path.Combine(dataDir, "games", "com.mojang", "minecraftWorlds");
                if (!Directory.Exists(worldsFolder))
                {
                    continue;
                }
                int worlds = Directory.GetDirectories(worldsFolder).Length;
                if (worlds > 0)
                {
                    worldLocations[dataDir] = worlds;
                }
            }

            return worldLocations;
        }

        private string GetBackupMinecraftDataDir()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "TmpMinecraftLocalState");
        }

        private string GetMinecraftUWPRootDir(string packageFamily)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                packageFamily);
        }

        private string GetMinecraftUWPDataDir(string packageFamily)
        {
            return Path.Combine(GetMinecraftUWPRootDir(packageFamily), "LocalState");
        }

        private string GetMinecraftGDKRootDir(string packageFamily)
        {
            string infix;
            switch (packageFamily)
            {
                case MinecraftPackageFamilies.MINECRAFT:
                    infix = "Minecraft Bedrock";
                    break;
                case MinecraftPackageFamilies.MINECRAFT_PREVIEW:
                    infix = "Minecraft Bedrock Preview";
                    break;
                default:
                    infix = "Minecraft Bedrock";
                    break;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), infix);
        }

        private List<string> GetMinecraftGDKDataDirs(string packageFamily)
        {
            var parentDir = Path.Combine(GetMinecraftGDKRootDir(packageFamily), "Users");
            var results = new List<string>();

            if (!Directory.Exists(parentDir))
            {
                return results;
            }

            results.AddRange(Directory.EnumerateDirectories(parentDir));
            return results;
        }

        private bool RestoreUWPData(string src, string uwpDataDir, string uwpParent)
        {
            try
            {
                if (!Directory.Exists(uwpParent))
                {
                    Directory.CreateDirectory(uwpParent);
                }
                if (!Directory.Exists(uwpDataDir))
                {
                    Directory.CreateDirectory(uwpDataDir);
                }

                RestoreMove(src, uwpDataDir);
                return true;
            }
            catch (Exception e)
            {
                Debug.WriteLine("Failed restoring UWP data: " + e.ToString());
                return false;
            }
        }

        private void RestoreMove(string from, string to)
        {
            foreach (var f in Directory.EnumerateFiles(from))
            {
                string ft = Path.Combine(to, Path.GetFileName(f));
                if (File.Exists(ft))
                {
                    File.Delete(ft);
                }
                File.Move(f, ft);
            }
            foreach (var f in Directory.EnumerateDirectories(from))
            {
                string tp = Path.Combine(to, Path.GetFileName(f));
                if (!Directory.Exists(tp))
                {
                    Directory.CreateDirectory(tp);
                }
                RestoreMove(f, tp);
            }
        }
    }

    // ViewModel for modern UI
    public class ModernVersionViewModel : INotifyPropertyChanged
    {
        public WPFDataTypes.Version Version { get; }
        private ICommonVersionCommands _commands;

        public ModernVersionViewModel(WPFDataTypes.Version version, ICommonVersionCommands commands)
        {
            Version = version;
            _commands = commands;
            
            // Subscribe to Version property changes to update progress
            Version.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "IsStateChanging" || e.PropertyName == "StateChangeInfo")
                {
                    OnPropertyChanged("ProgressVisibility");
                    OnPropertyChanged("DownloadButtonVisibility");
                    OnPropertyChanged("InstalledVisibility");
                    OnPropertyChanged("ProgressText");
                    OnPropertyChanged("MaxProgress");
                    OnPropertyChanged("CurrentProgress");
                    OnPropertyChanged("CancelCommand");
                    OnPropertyChanged("CancelButtonVisibility");
                    OnPropertyChanged("PauseResumeButtonVisibility");
                    OnPropertyChanged("PauseResumeButtonText");
                }
            };
            
            // Subscribe to StateChangeInfo property changes
            if (Version.StateChangeInfo != null)
            {
                Version.StateChangeInfo.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == "Progress" || e.PropertyName == "MaxProgress" || e.PropertyName == "DisplayStatus")
                    {
                        OnPropertyChanged("ProgressText");
                        OnPropertyChanged("MaxProgress");
                        OnPropertyChanged("CurrentProgress");
                    }
                    if (e.PropertyName == "IsPaused")
                    {
                        OnPropertyChanged("PauseResumeButtonText");
                    }
                };
            }
        }

        public string DisplayName => Version.DisplayName;
        
        public string FriendlyStatus
        {
            get
            {
                if (Version.IsStateChanging && Version.StateChangeInfo != null)
                {
                    return Version.StateChangeInfo.DisplayStatus;
                }
                return Version.IsInstalled ? Localization.Get("ReadyToPlay") : Localization.Get("NotInstalled");
            }
        }

        public string PackageTypeDisplay
        {
            get
            {
                return Version.PackageType == PackageType.GDK ? Localization.Get("TypeXboxGDK") : Localization.Get("TypeWindowsStore");
            }
        }

        public string VersionTypeDisplay
        {
            get
            {
                switch (Version.VersionType)
                {
                    case VersionType.Release:
                        return Localization.Get("StableRelease");
                    case VersionType.Preview:
                        return Localization.Get("PreviewVersion");
                    case VersionType.Beta:
                        return Localization.Get("BetaVersion");
                    case VersionType.Imported:
                        return Localization.Get("ImportedVersion");
                    default:
                        return "";
                }
            }
        }

        public string Icon
        {
            get
            {
                switch (Version.VersionType)
                {
                    case VersionType.Release:
                        return "⭐";
                    case VersionType.Preview:
                        return "✨";
                    case VersionType.Beta:
                        return "🧪";
                    case VersionType.Imported:
                        return "📦";
                    default:
                        return "🎮";
                }
            }
        }

        // Progress properties
        public string ProgressText
        {
            get
            {
                if (Version.IsStateChanging && Version.StateChangeInfo != null)
                {
                    return Version.StateChangeInfo.DisplayStatus;
                }
                return "";
            }
        }

        public long MaxProgress
        {
            get
            {
                if (Version.IsStateChanging && Version.StateChangeInfo != null)
                {
                    return Version.StateChangeInfo.MaxProgress;
                }
                return 100;
            }
        }

        public long CurrentProgress
        {
            get
            {
                if (Version.IsStateChanging && Version.StateChangeInfo != null)
                {
                    return Version.StateChangeInfo.Progress;
                }
                return 0;
            }
        }

        public ICommand CancelCommand
        {
            get
            {
                if (Version.IsStateChanging && Version.StateChangeInfo != null)
                {
                    return Version.StateChangeInfo.CancelCommand;
                }
                return null;
            }
        }

        public ICommand PauseResumeCommand => _commands.PauseResumeCommand;

        public Visibility IsNewVisibility => Version.IsNew ? Visibility.Visible : Visibility.Collapsed;
        public Visibility IsRecommendedVisibility => Version.VersionType == VersionType.Release && !Version.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DownloadButtonVisibility => !Version.IsInstalled && !Version.IsStateChanging ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ProgressVisibility => Version.IsStateChanging ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CancelButtonVisibility => Version.IsStateChanging && Version.StateChangeInfo?.VersionState == VersionState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PauseResumeButtonVisibility => Version.IsStateChanging && Version.StateChangeInfo?.VersionState == VersionState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InstalledVisibility => Version.IsInstalled && !Version.IsStateChanging ? Visibility.Visible : Visibility.Collapsed;

        public ICommand LaunchCommand => _commands.LaunchCommand;
        public ICommand RemoveCommand => _commands.RemoveCommand;
        public ICommand DownloadCommand => _commands.DownloadCommand;

        // Localized button text
        public string PlayButtonText => Localization.Get("Play");
        public string DownloadButtonText => Localization.Get("Download");
        public string RemoveTooltipText => Localization.Get("RemoveTooltip");
        public string PauseResumeButtonText => Version.StateChangeInfo?.IsPaused == true ? Localization.Get("Resume") : Localization.Get("Pause");
        public string CancelButtonText => Localization.Get("Cancel");

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
