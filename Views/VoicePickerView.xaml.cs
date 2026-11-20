using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ALambdaTTS.Models;
using ALambdaTTS.Services;

namespace ALambdaTTS.Views;

/// <summary>音色面板：搜索 + 性别 + 语言筛选，列表虚拟化。</summary>
public partial class VoicePickerView : UserControl
{
    private VoiceCatalog _catalog = VoiceCatalog.Fallback();
    private string _gender = "all";
    private string _language = string.Empty;
    private bool _suppressEvents;

    public VoicePickerView()
    {
        InitializeComponent();
    }

    /// <summary>用户选中了一个音色。</summary>
    public event EventHandler<VoiceEntry>? VoiceChosen;

    /// <summary>面板里按了 Esc（键盘事件不会冒泡到主窗口）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>筛选条件变了，主窗口把它写回设置。</summary>
    public event EventHandler? FilterChanged;

    /// <summary>当前性别筛选（all / female / male）。</summary>
    public string SelectedGender => _gender;

    /// <summary>当前语言大类代码，空表示全部语言。</summary>
    public string SelectedLanguage => _language;

    /// <summary>面板打开时把光标放到搜索框。</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Key == Key.Enter && VoiceList.Items.Count > 0)
        {
            e.Handled = true;
            var entry = (VoiceList.SelectedItem as VoiceEntry) ?? (VoiceEntry)VoiceList.Items[0];
            VoiceChosen?.Invoke(this, entry);
        }
    }

    /// <summary>打开面板。性别 / 语言筛选沿用上次的选择，找不到对应语言就退回全部。</summary>
    public void Initialize(
        VoiceCatalog catalog,
        VoiceEntry? selected,
        string? gender = null,
        string? language = null)
    {
        _suppressEvents = true;
        _catalog = catalog;

        _gender = (gender ?? string.Empty).Trim().ToLowerInvariant();
        if (_gender is not ("female" or "male"))
        {
            _gender = "all";
        }

        BuildLanguageOptions();

        var wanted = (language ?? string.Empty).Trim();
        var match = (LanguageCombo.ItemsSource as List<LocaleOption>)?
            .FirstOrDefault(o => o.Code.Length > 0 && o.Code.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        _language = match?.Code ?? string.Empty;
        LanguageCombo.SelectedIndex = match is null
            ? 0
            : (LanguageCombo.ItemsSource as List<LocaleOption>)!.IndexOf(match);

        GenderAll.IsChecked = _gender == "all";
        GenderFemale.IsChecked = _gender == "female";
        GenderMale.IsChecked = _gender == "male";
        SearchBox.Text = string.Empty;
        _suppressEvents = false;

        ApplyFilter(selected);
    }

    private void BuildLanguageOptions()
    {
        var options = new List<LocaleOption> { new(string.Empty, "全部语言") };
        options.AddRange(_catalog.Languages.Select(language => new LocaleOption(
            language.Code,
            $"{language.Label}（{language.Count}）")));

        LanguageCombo.ItemsSource = options;
        LanguageCombo.SelectedIndex = 0;
    }

    private void ApplyFilter(VoiceEntry? select = null)
    {
        var keyword = SearchBox.Text.Trim();
        IEnumerable<VoiceEntry> query = _catalog.Voices;

        if (_gender == "female")
        {
            query = query.Where(v => v.IsFemale);
        }
        else if (_gender == "male")
        {
            query = query.Where(v => !v.IsFemale);
        }

        if (!string.IsNullOrEmpty(_language))
        {
            // 按语言大类筛选：中国大陆 / 香港 / 辽宁话 都在「中文」里
            query = query.Where(v => v.LanguageCode.Equals(_language, StringComparison.OrdinalIgnoreCase));
        }

        if (keyword.Length > 0)
        {
            query = query.Where(v => v.SearchText.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var result = query
            .OrderBy(v => LocaleRank(v.Locale))
            .ThenBy(v => LocaleRank(v.Locale) == 1 ? v.Locale : v.LocaleDisplay, StringComparer.CurrentCulture)
            .ThenBy(v => v.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        VoiceList.ItemsSource = result;
        EmptyHint.Visibility = result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = $"共 {_catalog.Voices.Count} 个音色，当前筛选 {result.Count} 个";

        var target = select ?? VoiceList.SelectedItem as VoiceEntry;
        if (target is not null)
        {
            var match = result.FirstOrDefault(v => v.ShortName == target.ShortName);
            if (match is not null)
            {
                _suppressEvents = true;
                VoiceList.SelectedItem = match;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => VoiceList.ScrollIntoView(match)));
                _suppressEvents = false;
            }
            else
            {
                _suppressEvents = true;
                VoiceList.SelectedItem = null;
                _suppressEvents = false;
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_suppressEvents)
        {
            ApplyFilter();
        }
    }

    private void GenderChip_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var tag = (sender as FrameworkElement)?.Tag as string ?? "all";
        _gender = tag;

        _suppressEvents = true;
        GenderAll.IsChecked = tag == "all";
        GenderFemale.IsChecked = tag == "female";
        GenderMale.IsChecked = tag == "male";
        _suppressEvents = false;

        ApplyFilter();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (LanguageCombo.SelectedItem is LocaleOption option)
        {
            _language = option.Code;
            ApplyFilter();
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 鼠标点击音色：自己命中容器再选中，不依赖 ListBoxItem 的默认选中逻辑。
    /// 用 PreviewMouseUp（冒泡前的隧道事件），左键才算数。
    /// </summary>
    private void VoiceList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(VoiceList, e.OriginalSource as DependencyObject) is ListBoxItem container
            && container.DataContext is VoiceEntry entry)
        {
            _suppressEvents = true;
            VoiceList.SelectedItem = entry;
            _suppressEvents = false;

            e.Handled = true;
            VoiceChosen?.Invoke(this, entry);
        }
    }

    private void VoiceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (VoiceList.SelectedItem is VoiceEntry entry)
        {
            VoiceChosen?.Invoke(this, entry);
        }
    }

    /// <summary>排序优先级：简体中文最前，其次其他中文，最后按语言名排。</summary>
    private static int LocaleRank(string locale)
    {
        if (locale.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    public sealed record LocaleOption(string Code, string Label);
}
