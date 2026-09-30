using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace SwiftBatchApp.Controls;

public partial class BarChart : UserControl
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable), typeof(BarChart), new PropertyMetadata(null, (d, _) => ((BarChart)d).UpdateEmpty()));

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth), typeof(double), typeof(BarChart), new PropertyMetadata(140.0));

    private static readonly DependencyPropertyKey EmptyVisibilityKey = DependencyProperty.RegisterReadOnly(
        nameof(EmptyVisibility), typeof(Visibility), typeof(BarChart), new PropertyMetadata(Visibility.Visible));

    public BarChart() => InitializeComponent();

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double LabelWidth
    {
        get => (double)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public Visibility EmptyVisibility => (Visibility)GetValue(EmptyVisibilityKey.DependencyProperty);

    private void UpdateEmpty() =>
        SetValue(EmptyVisibilityKey, Items?.GetEnumerator().MoveNext() == true ? Visibility.Collapsed : Visibility.Visible);
}
