using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Skein;

/// <summary>A picture or video chosen for a new post.</summary>
public sealed class SubmitMedia(string path, string caption = "") : ObservableObject
{
    public string Path => path;
    public string Name => System.IO.Path.GetFileName(path);
    public bool IsVideo => RedditApi.IsVideoFile(path);

    string _caption = caption;
    public string Caption { get => _caption; set => Set(ref _caption, value); }

    /// <summary>A small thumbnail (pictures only), loaded without locking the file.</summary>
    public BitmapImage? Preview
    {
        get
        {
            if (IsVideo) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 144;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }
}

public partial class SubmitWindow : Window
{
    const int MaxGallery = 20;

    readonly RedditApi _api;
    readonly PostDraftStore _drafts = new();
    readonly ObservableCollection<SubmitMedia> _media = [];
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool _posting;
    bool _restoring;

    public static readonly DependencyProperty ShowCaptionsProperty =
        DependencyProperty.Register(nameof(ShowCaptions), typeof(bool), typeof(SubmitWindow));

    /// <summary>Captions only mean something in galleries (two or more pictures).</summary>
    public bool ShowCaptions
    {
        get => (bool)GetValue(ShowCaptionsProperty);
        set => SetValue(ShowCaptionsProperty, value);
    }

    readonly Services? _services;

    /// <param name="services">For the rich editor (it shares the browser profile and theme); without it, a plain text box.</param>
    public SubmitWindow(RedditApi api, string subreddit, Services? services = null)
    {
        InitializeComponent();
        DataContext = this;
        _api = api;
        _services = services;
        _communityTimer.Tick += async (_, _) =>
        {
            _communityTimer.Stop();
            await LoadPostingInfoAsync();
        };
        MediaList.ItemsSource = _media;
        _media.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (SubmitMedia m in e.NewItems) m.PropertyChanged += (_, _) => OnChanged(this, new RoutedEventArgs());
            ShowCaptions = _media.Count > 1 && !_media.Any(m => m.IsVideo);
            OnChanged(this, new RoutedEventArgs());
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveDraft();
        };

        Subreddit.Text = subreddit;
        if (_drafts.Load() is { } draft) RestoreDraft(draft);

        Loaded += async (_, _) =>
        {
            (Subreddit.Text.Length > 0 ? PostTitle : Subreddit).Focus();
            if (Subreddit.Text.Trim().Length > 0) _ = LoadPostingInfoAsync();
            await StartRichEditorAsync();
        };
        // Closing mid-submit would lose the result (and setting DialogResult afterwards throws).
        Closing += (_, e) =>
        {
            e.Cancel = _posting;
            if (!_posting && DialogResult != true) SaveDraft();
        };
    }

    /// <summary>The new post's address (null if Reddit is still processing uploaded media).</summary>
    public string? PostUrl { get; private set; }

    string Kind => MediaKind.IsChecked == true ? "media" : LinkKind.IsChecked == true ? "link" : "text";

    // ---- Drafts -------------------------------------------------------------------------------

    PostDraft CurrentDraft() => new(Subreddit.Text, PostTitle.Text, Kind, Body.Text, Nsfw.IsChecked == true,
        Spoiler.IsChecked == true, _media.Select(m => new PostDraft.MediaFile(m.Path, m.Caption)).ToList());

    void SaveDraft()
    {
        if (!_restoring && !_posting) _drafts.Save(CurrentDraft());
    }

    void RestoreDraft(PostDraft draft)
    {
        _restoring = true;
        if (draft.Subreddit.Length > 0) Subreddit.Text = draft.Subreddit;
        PostTitle.Text = draft.Title;
        Body.Text = draft.Body;
        Nsfw.IsChecked = draft.Nsfw;
        Spoiler.IsChecked = draft.Spoiler;
        (draft.Kind switch { "link" => LinkKind, "media" => MediaKind, _ => TextKind }).IsChecked = true;
        foreach (var file in draft.Media) _media.Add(new SubmitMedia(file.Path, file.Caption));
        _restoring = false;
        DraftBar.Visibility = Visibility.Visible;
    }

    void OnDiscardDraft(object sender, RoutedEventArgs e)
    {
        _restoring = true;
        PostTitle.Text = Body.Text = "";
        SendToEditor(new { type = "editorSet", text = "" });
        Nsfw.IsChecked = Spoiler.IsChecked = false;
        TextKind.IsChecked = true;
        _media.Clear();
        _restoring = false;
        _drafts.Clear();
        DraftBar.Visibility = Visibility.Collapsed;
        PostTitle.Focus();
    }

    void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_restoring || _saveTimer == null) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ---- Kind and media -----------------------------------------------------------------------

    void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (ContentLabel == null) return; // fires during InitializeComponent
        var kind = Kind;
        ContentLabel.Text = kind switch
        {
            "link" => "URL",
            "media" => "Pictures or video",
            _ => "Text",
        };
        Body.AcceptsReturn = kind == "text";
        UpdateEditors();
        MediaPanel.Visibility = kind == "media" ? Visibility.Visible : Visibility.Collapsed;
        OnChanged(sender, e);
    }

    /// <summary>Text posts use the rich editor when it's running; otherwise the plain box (with its toolbar).</summary>
    void UpdateEditors()
    {
        var kind = Kind;
        var rich = kind == "text" && _richReady;
        RichBody.Visibility = rich ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = kind == "media" || rich ? Visibility.Collapsed : Visibility.Visible;
        Formatting.Visibility = kind == "text" && !rich ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- The rich editor (the post view's, in a web view) ---------------------------------------

    bool _richReady;

    async Task StartRichEditorAsync()
    {
        if (_services == null) return;
        try
        {
            await RichBody.EnsureCoreWebView2Async(_services.Session.Environment, _services.Session.CreateControllerOptions());
            var core = RichBody.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("skein.app", System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            Appearance.ApplyToWebView(core, _services.Settings.Theme);
            // Only the editor page; links in its preview (and dropped files) go nowhere.
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("https://skein.app/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnEditorMessage;
            core.Navigate("https://skein.app/editor.html");
        }
        catch
        {
            // No web view: the plain text box and toolbar stay.
        }
    }

    void OnEditorMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
        switch (doc.RootElement.Str("type"))
        {
            case "editorReady":
                SendToEditor(new
                {
                    type = "editorInit",
                    text = Body.Text.Replace("\r\n", "\n"),
                    placeholder = "Write your post. Markdown or rich text, your choice.",
                    theme = _services!.Settings.Theme.ToLowerInvariant(),
                    accent = _services.Settings.Accent,
                }, always: true);
                _richReady = true;
                UpdateEditors();
                break;
            case "editorText":
                // The editor's Markdown is the post's text (drafts and posting read it from here).
                Body.Text = doc.RootElement.Str("text");
                break;
        }
    }

    /// <summary>Sends to the editor page (once it's running, unless <paramref name="always"/>).</summary>
    void SendToEditor(object message, bool always = false)
    {
        if (_richReady || always) RichBody.CoreWebView2?.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(message));
    }

    // ---- The community: flairs and rules ----------------------------------------------------------

    readonly DispatcherTimer _communityTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    PostingInfo? _postingInfo;
    string _infoFor = "";

    string CommunityName
    {
        get
        {
            var sub = Subreddit.Text.Trim().TrimStart('/');
            return sub.StartsWith("r/", StringComparison.OrdinalIgnoreCase) ? sub[2..] : sub;
        }
    }

    void OnSubredditChanged(object sender, RoutedEventArgs e)
    {
        OnChanged(sender, e);
        if (_communityTimer == null) return; // during InitializeComponent
        _communityTimer.Stop();
        _communityTimer.Start();
    }

    /// <summary>Loads the community's flairs (and whether one is required) and rules.</summary>
    async Task LoadPostingInfoAsync()
    {
        var name = CommunityName;
        if (name.Equals(_infoFor, StringComparison.OrdinalIgnoreCase)) return;
        _infoFor = name;
        _postingInfo = null;
        FlairRow.Visibility = RulesPanel.Visibility = CommunityNote.Visibility = Visibility.Collapsed;
        if (name.Length < 2) return;
        PostingInfo info;
        try
        {
            info = await _api.GetPostingInfoAsync(name, default);
        }
        catch
        {
            return; // not a community (yet): nothing to show
        }
        if (!name.Equals(CommunityName, StringComparison.OrdinalIgnoreCase)) return; // typed on meanwhile
        _postingInfo = info;

        Flair.Items.Clear();
        Flair.Items.Add(new ComboBoxItem { Content = info.FlairRequired ? "Choose a flair…" : "No flair", Tag = null });
        foreach (var flair in info.Flairs) Flair.Items.Add(new ComboBoxItem { Content = flair.ToString(), Tag = flair });
        Flair.SelectedIndex = 0;
        FlairRow.Visibility = info.Flairs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CommunityNote.Text = info.FlairRequired ? $"r/{name} requires a post flair." : "";
        CommunityNote.Visibility = info.FlairRequired ? Visibility.Visible : Visibility.Collapsed;

        RulesList.ItemsSource = info.Rules;
        RulesHeading.Text = $"r/{name} rules ({info.Rules.Count})";
        RulesPanel.Visibility = info.Rules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnFlairChanged(object sender, SelectionChangedEventArgs e)
    {
        var flair = (Flair.SelectedItem as ComboBoxItem)?.Tag as PostFlair;
        FlairText.Visibility = flair is { Editable: true } ? Visibility.Visible : Visibility.Collapsed;
        FlairText.Text = flair?.Text ?? "";
    }

    /// <summary>The flair to post with (none if there isn't one picked).</summary>
    FlairChoice ChosenFlair =>
        (Flair.SelectedItem as ComboBoxItem)?.Tag is PostFlair flair
            ? new FlairChoice(flair.Id, flair.Editable && FlairText.Text.Trim() != flair.Text ? FlairText.Text.Trim() : "")
            : FlairChoice.None;

    void OnAddMedia(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add pictures or a video",
            Multiselect = true,
            Filter = "Pictures and videos|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.mp4;*.mov|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        MediaKind.IsChecked = true;
        AddFiles(files);
    }

    void AddFiles(IEnumerable<string> files)
    {
        var problems = new List<string>();
        foreach (var file in files)
        {
            if (RedditApi.MediaTypeFor(file) == null)
            {
                problems.Add($"{Path.GetFileName(file)} isn't a picture or video Reddit takes.");
                continue;
            }
            if (_media.Any(m => m.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            var video = RedditApi.IsVideoFile(file);
            if (video && _media.Count > 0 || !video && _media.Any(m => m.IsVideo))
            {
                problems.Add("A post can have pictures or one video, not both.");
                continue;
            }
            if (_media.Count >= MaxGallery)
            {
                problems.Add($"Galleries hold up to {MaxGallery} pictures.");
                break;
            }
            _media.Add(new SubmitMedia(file));
        }
        ErrorText.Text = string.Join(" ", problems.Distinct());
    }

    void OnRemoveMedia(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SubmitMedia media }) _media.Remove(media);
    }

    // ---- Posting ------------------------------------------------------------------------------

    async void OnPost(object sender, RoutedEventArgs e)
    {
        var sub = Subreddit.Text.Trim().TrimStart('/');
        if (sub.StartsWith("r/", StringComparison.OrdinalIgnoreCase)) sub = sub[2..];
        var title = PostTitle.Text.Trim();
        var kind = Kind;
        var content = Body.Text.Trim();

        var problem = sub.Length == 0 || title.Length == 0 ? "Community and title are required."
            : kind == "link" && content.Length == 0 ? "Add the URL to post."
            : kind == "media" && _media.Count == 0 ? "Add a picture or a video."
            : _postingInfo is { FlairRequired: true } && ChosenFlair.Id.Length == 0 ? $"r/{sub} needs a post flair. Choose one above."
            : null;
        if (problem != null)
        {
            ErrorText.Text = problem;
            return;
        }

        _saveTimer.Stop();
        SaveDraft(); // if posting fails, it's still there next time
        PostButton.IsEnabled = false;
        _posting = true;
        try
        {
            ErrorText.Text = "Posting…";
            PostUrl = kind == "media"
                ? await PostMediaAsync(sub, title)
                : await _api.SubmitAsync(sub, title, kind == "link", content, Nsfw.IsChecked == true, Spoiler.IsChecked == true, ChosenFlair);
            _drafts.Clear();
            _posting = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _posting = false;
            ErrorText.Text = ex.Message;
            PostButton.IsEnabled = true;
        }
    }

    async Task<string?> PostMediaAsync(string sub, string title)
    {
        var nsfw = Nsfw.IsChecked == true;
        var spoiler = Spoiler.IsChecked == true;
        var files = _media.ToList();
        var uploaded = new List<RedditApi.UploadedMedia>();
        for (var i = 0; i < files.Count; i++)
        {
            ErrorText.Text = files.Count == 1 ? $"Uploading {files[i].Name}…" : $"Uploading {i + 1} of {files.Count}: {files[i].Name}…";
            uploaded.Add(await _api.UploadMediaAsync(files[i].Path, default));
        }

        if (files.Count > 1)
        {
            ErrorText.Text = "Creating the gallery…";
            return await _api.SubmitGalleryAsync(sub, title,
                files.Select((f, i) => new RedditApi.GalleryItem(uploaded[i].AssetId, f.Caption.Trim(), "")).ToList(), nsfw, spoiler, default, ChosenFlair);
        }

        RedditApi.UploadedMedia? poster = null;
        if (files[0].IsVideo)
        {
            ErrorText.Text = "Making the video's preview picture…";
            var posterFile = await VideoPoster.CreateAsync(files[0].Path);
            try
            {
                poster = await _api.UploadMediaAsync(posterFile, default);
            }
            finally
            {
                try { File.Delete(posterFile); } catch { /* temp file */ }
            }
        }
        ErrorText.Text = files[0].IsVideo ? "Reddit is processing the video (this can take a minute)…" : "Posting…";
        return await _api.SubmitMediaAsync(sub, title, uploaded[0], files[0].IsVideo, poster, nsfw, spoiler, default, ChosenFlair);
    }
}
