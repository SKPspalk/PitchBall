using System.Windows;
using System.Windows.Input;
using PitchBall.Models;

namespace PitchBall.Views;

/// <summary>拖入音频文件时的分析选项对话框:检测算法 + 人声场景。</summary>
public partial class AnalyzeOptionsDialog : Window
{
    public string Algorithm { get; private set; } = "Pyin";
    public string Profile { get; private set; } = "Balanced";
    public bool Remember { get; private set; }
    public bool Confirmed { get; private set; }

    public AnalyzeOptionsDialog(string algorithm, string profile)
    {
        InitializeComponent();
        Algorithm = algorithm;
        Profile = profile;
        AlgoPyinRadio.IsChecked = algorithm != "Yin";
        AlgoYinRadio.IsChecked = algorithm == "Yin";
        ProfileBalancedRadio.IsChecked = profile is not ("Clean" or "Live");
        ProfileCleanRadio.IsChecked = profile == "Clean";
        ProfileLiveRadio.IsChecked = profile == "Live";
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Algorithm = AlgoRmvpeRadio.IsChecked == true ? "Rmvpe"
            : AlgoYinRadio.IsChecked == true ? "Yin" : "Pyin";
        Profile = ProfileCleanRadio.IsChecked == true ? "Clean"
            : ProfileLiveRadio.IsChecked == true ? "Live"
            : "Balanced";
        Remember = RememberCheck.IsChecked == true;
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
