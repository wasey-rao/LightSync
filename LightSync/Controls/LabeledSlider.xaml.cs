using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LightSync.Controls;

/// <summary>A slider with a caption on the left and its formatted value on the right.</summary>
public partial class LabeledSlider : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(LabeledSlider), new PropertyMetadata(""));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(LabeledSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    // Range properties are pushed straight into the inner slider (not bound) so they are in place
    // before the value binding activates; otherwise the slider would clamp and write back a wrong value.
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(LabeledSlider),
            new PropertyMetadata(0.0, (d, e) => ((LabeledSlider)d).PART_Slider.Minimum = (double)e.NewValue));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LabeledSlider),
            new PropertyMetadata(100.0, (d, e) => ((LabeledSlider)d).PART_Slider.Maximum = (double)e.NewValue));

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(double), typeof(LabeledSlider),
            new PropertyMetadata(1.0, (d, e) => ((LabeledSlider)d).ApplyStep()));

    public static readonly DependencyProperty FormatProperty =
        DependencyProperty.Register(nameof(Format), typeof(string), typeof(LabeledSlider),
            new PropertyMetadata("{0:0}", (d, _) => ((LabeledSlider)d).UpdateDisplay()));

    public static readonly DependencyProperty DisplayValueProperty =
        DependencyProperty.Register(nameof(DisplayValue), typeof(string), typeof(LabeledSlider), new PropertyMetadata(""));

    public LabeledSlider()
    {
        InitializeComponent();
        PART_Slider.Minimum = Minimum;
        PART_Slider.Maximum = Maximum;
        ApplyStep();
        UpdateDisplay();
    }

    public event EventHandler? ValueChanged;

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public string DisplayValue { get => (string)GetValue(DisplayValueProperty); private set => SetValue(DisplayValueProperty, value); }

    static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (LabeledSlider)d;
        slider.UpdateDisplay();
        slider.ValueChanged?.Invoke(slider, EventArgs.Empty);
    }

    void ApplyStep()
    {
        PART_Slider.TickFrequency = Step;
        PART_Slider.SmallChange = Step;
        PART_Slider.LargeChange = Step * 5;
    }

    void UpdateDisplay() => DisplayValue = string.Format(CultureInfo.CurrentCulture, Format, Value);
}
