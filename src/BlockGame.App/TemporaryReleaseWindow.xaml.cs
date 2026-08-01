using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BlockGame.Core.Models;
using BlockGame.Core.Services;

namespace BlockGame.App;

public partial class TemporaryReleaseWindow : Window
{
    private readonly int _initialDurationMinutes;
    private TemporaryReleaseUnit _displayedUnit;

    public TemporaryReleaseWindow(string ruleName, int initialDurationMinutes)
    {
        InitializeComponent();
        _initialDurationMinutes = TemporaryReleasePolicy.NormalizeDurationMinutes(
            initialDurationMinutes);
        RuleDescriptionText.Text = $"为“{ruleName}”设置本次允许运行的时间。";
        _displayedUnit = _initialDurationMinutes >= 60
            ? TemporaryReleaseUnit.Hours
            : TemporaryReleaseUnit.Minutes;
        DurationUnitComboBox.SelectedIndex = _displayedUnit == TemporaryReleaseUnit.Hours
            ? 1
            : 0;
        RefreshDurationValue(_initialDurationMinutes, _displayedUnit);
        Loaded += (_, _) =>
        {
            DurationValueTextBox.Focus();
            DurationValueTextBox.SelectAll();
        };
    }

    public int DurationMinutes { get; private set; }

    private void DurationUnitComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (DurationValueTextBox is null)
        {
            return;
        }

        TemporaryReleaseUnit newUnit = GetSelectedUnit();
        int currentMinutes = TryReadDuration(_displayedUnit, out int parsedMinutes)
            ? parsedMinutes
            : _initialDurationMinutes;
        _displayedUnit = newUnit;
        RefreshDurationValue(currentMinutes, newUnit);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadDuration(GetSelectedUnit(), out int durationMinutes))
        {
            MessageBox.Show(
                "临时放行时长必须在 1 分钟到 24 小时之间。",
                "临时放行软件",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DurationMinutes = durationMinutes;
        DialogResult = true;
    }

    private bool TryReadDuration(TemporaryReleaseUnit unit, out int durationMinutes)
    {
        durationMinutes = 0;
        return double.TryParse(
                DurationValueTextBox.Text,
                NumberStyles.Number,
                CultureInfo.CurrentCulture,
                out double value)
            && TemporaryReleasePolicy.TryConvertToMinutes(
                value,
                unit,
                out durationMinutes);
    }

    private TemporaryReleaseUnit GetSelectedUnit()
    {
        var selected = DurationUnitComboBox.SelectedItem as ComboBoxItem;
        return Enum.TryParse(selected?.Tag?.ToString(), out TemporaryReleaseUnit unit)
            ? unit
            : TemporaryReleaseUnit.Minutes;
    }

    private void RefreshDurationValue(int durationMinutes, TemporaryReleaseUnit unit)
    {
        DurationValueTextBox.Text = TemporaryReleasePolicy.ConvertFromMinutes(
                durationMinutes,
                unit)
            .ToString("0.######", CultureInfo.CurrentCulture);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
