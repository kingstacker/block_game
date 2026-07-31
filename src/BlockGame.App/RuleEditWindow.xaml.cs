using System.Windows;
using System.Windows.Controls;
using BlockGame.Core.Models;
using BlockGame.Core.Services;
using Microsoft.Win32;

namespace BlockGame.App;

public partial class RuleEditWindow : Window
{
    public RuleEditWindow(BlockRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        InitializeComponent();

        RuleNameTextBox.Text = rule.Name;
        RulePatternTextBox.Text = rule.Target switch
        {
            RuleTarget.FileName => string.Join(
                Environment.NewLine,
                SafetyPolicy.SplitFileNamePatterns(rule.Pattern)),
            RuleTarget.Domain => string.Join(
                Environment.NewLine,
                WebsiteDomainRules.SplitAndNormalize(rule.Pattern)),
            _ => rule.Pattern
        };
        RuleTargetComboBox.SelectedIndex = rule.Target switch
        {
            RuleTarget.FullPath => 1,
            RuleTarget.Domain => 2,
            _ => 0
        };
        Loaded += (_, _) =>
        {
            RuleNameTextBox.Focus();
            RuleNameTextBox.SelectAll();
        };
    }

    public string RuleName { get; private set; } = string.Empty;

    public RuleTarget Target { get; private set; }

    public string Pattern { get; private set; } = string.Empty;

    private void RuleTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PatternHelpText is null || BrowseExeButton is null)
        {
            return;
        }

        RuleTarget target = GetSelectedTarget();
        PatternHelpText.Text = target switch
        {
            RuleTarget.FullPath => "匹配内容（完整 EXE 路径，支持 * 和 ?）",
            RuleTarget.Domain => "网站域名（可一行一个或用 ; 分隔；域名规则同时覆盖其子域名）",
            _ => "匹配文件名、内部产品名或文件描述（可一行一个或用 ; 分隔，支持 * 和 ?，自动补 .exe）"
        };
        BrowseExeButton.IsEnabled = target != RuleTarget.Domain;
    }

    private void BrowseExeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要禁止的程序",
            Filter = "可执行程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            RuleTargetComboBox.SelectedIndex = 1;
            RulePatternTextBox.Text = dialog.FileName;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        RuleTarget target = GetSelectedTarget();
        var candidate = new BlockRule
        {
            Name = RuleNameTextBox.Text.Trim(),
            Target = target,
            Pattern = RulePatternTextBox.Text
        };

        string? validationError = SafetyPolicy.ValidateRule(candidate);
        if (validationError is not null)
        {
            MessageBox.Show(
                validationError,
                "规则不安全",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        candidate.Pattern = SafetyPolicy.NormalizeRulePattern(target, candidate.Pattern);
        RuleName = candidate.Name;
        Target = candidate.Target;
        Pattern = candidate.Pattern;
        DialogResult = true;
    }

    private RuleTarget GetSelectedTarget()
    {
        var selected = RuleTargetComboBox.SelectedItem as ComboBoxItem;
        return Enum.TryParse(selected?.Tag?.ToString(), out RuleTarget target)
            ? target
            : RuleTarget.FileName;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
