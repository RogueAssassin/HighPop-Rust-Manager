using System.Windows;
using System.Windows.Controls;

namespace HighPop.Views.Sections;

/// <summary>
/// Placeholder for one server-workspace section.
/// <list type="bullet">
/// <item>The section is built from <see cref="Template"/> the first time its tab is shown and then
/// kept, so later visits only re-attach it (no XAML parsing or style application).</item>
/// <item>A hidden section keeps the server it last showed. Switching servers therefore only
/// rebinds the section on screen; the others catch up when they are next shown.</item>
/// </list>
/// </summary>
public sealed class DeferredSection : Decorator
{
    public static readonly DependencyProperty TemplateProperty = DependencyProperty.Register(
        nameof(Template), typeof(DataTemplate), typeof(DeferredSection));

    public DataTemplate? Template
    {
        get => (DataTemplate?)GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    public DeferredSection()
    {
        Loaded += (_, _) => Show();
        DataContextChanged += (_, _) => { if (IsLoaded) Show(); };
    }

    private void Show()
    {
        if (Child == null)
        {
            if (Template?.LoadContent() is not FrameworkElement section) return;
            section.DataContext = DataContext;
            Child = section;
        }
        else if (Child is FrameworkElement section && !ReferenceEquals(section.DataContext, DataContext))
        {
            section.DataContext = DataContext;
        }
    }
}
