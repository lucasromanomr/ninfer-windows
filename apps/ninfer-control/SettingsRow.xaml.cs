using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace NInferControl;

/// <summary>
/// One row of a settings list: optional glyph, header, optional description, and a control on the
/// right. Written against plain WinUI primitives on purpose — a control library would ship its
/// templates in a separate .pri, which the unpackaged portable build does not merge, and the app
/// would fail to load its XAML at startup.
/// </summary>
[ContentProperty(Name = nameof(RowContent))]
public sealed partial class SettingsRow : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingsRow),
        new PropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsRow),
        new PropertyMetadata(string.Empty, OnDescriptionChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SettingsRow),
        new PropertyMetadata(string.Empty, OnGlyphChanged));

    public static readonly DependencyProperty RowContentProperty = DependencyProperty.Register(
        nameof(RowContent), typeof(object), typeof(SettingsRow),
        new PropertyMetadata(null, OnRowContentChanged));

    public SettingsRow()
    {
        InitializeComponent();
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Segoe Fluent Icons code point, e.g. "". Empty hides the icon column.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public object? RowContent
    {
        get => GetValue(RowContentProperty);
        set => SetValue(RowContentProperty, value);
    }

    /// <summary>Replaces the header text with arbitrary content, for a live-updating hint.</summary>
    public void SetDescriptionText(string text)
    {
        DescriptionPresenter.Text = text;
        DescriptionPresenter.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        UpdateAutomationName();
    }

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (SettingsRow)d;
        row.HeaderPresenter.Text = (string)e.NewValue;
        row.UpdateAutomationName();
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SettingsRow)d).SetDescriptionText((string)e.NewValue);

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (SettingsRow)d;
        var glyph = (string)e.NewValue;
        row.IconPresenter.Glyph = glyph;
        row.IconPresenter.Visibility = string.IsNullOrEmpty(glyph) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnRowContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SettingsRow)d).RowContentPresenter.Content = e.NewValue;

    // Screen readers announce the row as one unit; without this the control on the right is read
    // with no idea which setting it belongs to.
    private void UpdateAutomationName()
    {
        var description = DescriptionPresenter.Text;
        AutomationProperties.SetName(RowBorder,
            string.IsNullOrEmpty(description) ? HeaderPresenter.Text : $"{HeaderPresenter.Text}. {description}");
    }
}
